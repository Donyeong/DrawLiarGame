param([string]$ProjectRoot = 'C:\DrawLiar', [int]$Width = 1600, [int]$Height = 900, [ValidateRange(3,12)][int]$PlayerCount = 3)
$ErrorActionPreference = 'Stop'
$gamePath = Join-Path $ProjectRoot 'DrawLiar\Builds\Windows\DrawLiar.exe'
$runPath = Join-Path $ProjectRoot ('.codex-temp\ui-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runPath -Force | Out-Null
$stopSignal = Join-Path $runPath 'leave.signal'
$processes = @()
function Read-UiReport([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $reader = [IO.StreamReader]::new($stream)
        try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}
try {
    for ($i = 0; $i -lt $PlayerCount; $i++) {
        $arguments = @('-screen-width',"$Width",'-screen-height',"$Height",'-screen-fullscreen','0',
            '-drawName',"UiPeer$i",'-voiceProfile',"uismoke$i",'-drawUiPlayers',"$PlayerCount",'-drawUiReport',('"'+(Join-Path $runPath "player-$i.json")+'"'),
            '-logFile',('"'+(Join-Path $runPath "player-$i.log")+'"'))
        if ($i -eq 0) { $arguments += @('-drawUiSmoke','-drawUiStop',('"'+$stopSignal+'"')) }
        else { $arguments += @('-drawUiSmokePeer','-drawJoin','localhost','-batchmode','-nographics') }
        $processes += Start-Process -FilePath $gamePath -ArgumentList $arguments -WorkingDirectory (Split-Path $gamePath) -WindowStyle Hidden -PassThru
        if ($i -eq 0) {
            $deadline = (Get-Date).AddSeconds(90)
            do {
                $hostReport = $null
                try { $hostReport = Read-UiReport (Join-Path $runPath 'player-0.json') } catch { }
                if ($hostReport.Outcome -eq 'FAIL') { throw $hostReport.Error }
                if ($processes[0].HasExited) { throw 'UI host exited before opening a room.' }
                if ($hostReport.Stage -ne 'LobbyReady') { Start-Sleep -Milliseconds 250 }
            } while ($hostReport.Stage -ne 'LobbyReady' -and (Get-Date) -lt $deadline)
            if ($hostReport.Stage -ne 'LobbyReady') { throw 'UI host did not finish home navigation and open a room within 90 seconds.' }
            if (-not $hostReport.HomeFlowVerified -or $hostReport.BoundsChecks -lt 10) { throw 'Home navigation or screen bounds checks were not completed.' }
            foreach ($screen in @('main','join','code','browse','options','customize','create-mode','create-rules','create-time','create-topics','create-details')) {
                if (-not (Test-Path -LiteralPath (Join-Path $runPath "$screen.png"))) { throw "UI screen capture is missing: $screen" }
            }
        }
    }
    Write-Output "UI interaction check started: $runPath"
    $deadline = (Get-Date).AddSeconds(240)
    $previous = ''
    do {
        $reports = @()
        foreach ($i in 0..($PlayerCount - 1)) { try { $reports += Read-UiReport (Join-Path $runPath "player-$i.json") } catch { } }
        $status = ($reports | ForEach-Object { "$($_.Stage)/$($_.Outcome)" }) -join ' | '
        if ($status -ne $previous) { Write-Output $status; $previous = $status }
        if (@($reports | Where-Object Outcome -eq 'FAIL').Count -gt 0) { throw ($reports | ConvertTo-Json -Depth 4) }
        if ($reports.Count -eq $PlayerCount -and @($reports | Where-Object {
            -not $_.InputObserved -or -not $_.EquipmentVerified -or -not $_.SecretVerified -or -not $_.DiscussionVerified -or -not $_.RebuttalVerified -or -not $_.VotingVerified -or -not $_.TurnCompleted -or $_.PlayersInRow -ne $PlayerCount
        }).Count -eq 0 -and @($reports | Where-Object { -not $_.Peer -and $_.VoteSubmittedOnce }).Count -eq 1) {
            if (-not (Test-Path -LiteralPath $stopSignal)) { Set-Content -LiteralPath $stopSignal -Value 'All clients verified drawing, chat, role visibility, discussion, rebuttal, voting, and player layout.' }
        }
        if ($reports.Count -eq $PlayerCount -and @($reports | Where-Object Outcome -ne 'PASS').Count -eq 0) {
            foreach ($screen in @('room-lobby','ui-input','room-discussion','room-rebuttal','room-voting','room-voted')) {
                if (-not (Test-Path -LiteralPath (Join-Path $runPath "$screen.png"))) { throw "In-game screen capture is missing: $screen" }
            }
            $reports | ConvertTo-Json -Depth 4
            Write-Output "DRAWLIAR_UI_SMOKE_PASS ($PlayerCount players): home navigation, cancel preserves preferences, creation/time/topic inputs, player row bounds, start, brush/eraser pixels, chat drawer/history, secret toggle, discussion/rebuttal/voting with canvas retained and tools hidden, select then submit once, menu leave."
            return
        }
        if (@($processes | Where-Object HasExited).Count -gt 0) { throw 'A UI check process exited unexpectedly.' }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw 'UI interaction check timed out.'
} finally {
    foreach ($process in $processes) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue } }
}
