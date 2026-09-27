param(
    [string]$ProjectRoot = 'C:\DrawLiar',
    [ValidateRange(60, 300)][int]$TimeoutSeconds = 210
)
$ErrorActionPreference = 'Stop'

function Read-PublicRoomReport([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) { return $null }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $reader = [IO.StreamReader]::new($stream)
        try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

$gamePath = Join-Path $ProjectRoot 'DrawLiar\Builds\Windows\DrawLiar.exe'
if (!(Test-Path -LiteralPath $gamePath)) { throw "Build a Development Windows player first: $gamePath" }
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$runPath = Join-Path $ProjectRoot ('.codex-temp\public-room-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $runId)
[void][IO.Directory]::CreateDirectory($runPath)
$processes = @()
$roles = @('host', 'guest')
try {
    foreach ($role in $roles) {
        $arguments = @('-batchmode', '-nographics', '-drawPublicRoomSmoke', '-publicRoomRole', $role,
            '-publicRoomRun', $runId, '-publicRoomReports', ('"' + $runPath + '"'),
            '-ugsProfile', ('public' + $runId + '_' + $role), '-voiceProfile', ('public' + $runId + '_' + $role),
            '-drawName', ('PublicRoom_' + $role), '-logFile', ('"' + (Join-Path $runPath ($role + '.log')) + '"'))
        $processes += Start-Process -FilePath $gamePath -ArgumentList $arguments -WorkingDirectory (Split-Path $gamePath) -WindowStyle Hidden -PassThru
    }
    Write-Output "Public/private room checks started. Reports: $runPath"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $previous = ''
    while ((Get-Date) -lt $deadline) {
        $reports = @($roles | ForEach-Object { Read-PublicRoomReport (Join-Path $runPath ($_ + '.json')) } | Where-Object { $null -ne $_ })
        $summary = ($reports | ForEach-Object { "$($_.Role): $($_.Stage) / $($_.Outcome)" }) -join ' | '
        if ($summary -ne $previous) { Write-Output $summary; $previous = $summary }
        if (@($reports | Where-Object Outcome -eq 'FAIL').Count -gt 0) {
            $reports | ConvertTo-Json -Depth 4 | Write-Output
            throw 'A public-room check failed.'
        }
        if ($reports.Count -eq 2 -and @($reports | Where-Object Outcome -ne 'PASS').Count -eq 0) {
            $hostReport = $reports | Where-Object Role -eq 'host'
            $guestReport = $reports | Where-Object Role -eq 'guest'
            if (!$hostReport.PrivateVerified -or !$hostReport.PrivateDeleted -or !$hostReport.PublicVerified -or
                !$hostReport.HostObservedJoin -or !$hostReport.GuestRemoved -or !$hostReport.PublicDeleted -or
                !$guestReport.PrivateExcluded -or !$guestReport.PublicListed -or !$guestReport.JoinedById -or !$guestReport.GuestLeft -or
                !$hostReport.ServicesCleaned -or !$guestReport.ServicesCleaned -or
                $hostReport.ConnectedPlayers -ne 2 -or $guestReport.ConnectedPlayers -ne 2 -or
                [string]::IsNullOrEmpty($hostReport.PlayerId) -or $hostReport.PlayerId -eq $guestReport.PlayerId) {
                throw 'A report claimed PASS without all required API, connection, identity, and cleanup evidence.'
            }
            $reports | ConvertTo-Json -Depth 4 | Write-Output
            Write-Output 'DRAWLIAR_PUBLIC_ROOM_SMOKE_PASS (private excluded, public listed, JoinLobbyAsync connected, both lobbies deleted)'
            return
        }
        if (@($processes | Where-Object HasExited).Count -gt 0) { throw 'A test process exited before producing complete evidence.' }
        Start-Sleep -Milliseconds 500
    }
    throw 'Public-room check timed out; verify the player includes DrawPublicRoomSmoke and UGS is available.'
} finally {
    Set-Content -LiteralPath (Join-Path $runPath 'stop.signal') -Value 'Clean up only this run before stopping.'
    $cleanupDeadline = (Get-Date).AddSeconds(40)
    do {
        $cleaned = $true
        foreach ($role in $roles) {
            $report = Read-PublicRoomReport (Join-Path $runPath ($role + '.json'))
            if ($null -eq $report -or !$report.ServicesCleaned) { $cleaned = $false }
        }
        if (!$cleaned) { Start-Sleep -Milliseconds 250 }
    } while (!$cleaned -and (Get-Date) -lt $cleanupDeadline -and @($processes | Where-Object { !$_.HasExited }).Count -gt 0)
    if (!$cleaned) { Write-Warning "Cloud cleanup was not fully confirmed. Inspect reports in $runPath." }
    foreach ($process in $processes) {
        if (!$process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
    }
}
