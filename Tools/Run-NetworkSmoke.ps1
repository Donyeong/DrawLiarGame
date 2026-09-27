param(
    [ValidateSet('Relay','Individual')][string]$Mode = 'Relay',
    [switch]$Online,
    [ValidateRange(3,12)][int]$PlayerCount = 4,
    [string]$ProjectRoot = 'C:\DrawLiar'
)
$ErrorActionPreference = 'Stop'
function Read-SmokeReport([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $reader = [IO.StreamReader]::new($stream)
        try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}
$gamePath = Join-Path $ProjectRoot 'DrawLiar\Builds\Windows\DrawLiar.exe'
if (-not (Test-Path -LiteralPath $gamePath)) { throw "Build the Windows player first: $gamePath" }
$connectionLabel = if ($Online) { 'online-' } else { '' }
$runPath = Join-Path $ProjectRoot ('.codex-temp\smoke-' + $connectionLabel + $Mode.ToLowerInvariant() + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runPath -Force | Out-Null
$processes = @()
$stopSignal = Join-Path $runPath 'stop-host.signal'
$expectSpectator = $PlayerCount -lt 12
$totalPlayers = $PlayerCount + [int]$expectSpectator
$spectatorStarted = $false
$shutdownRequested = $false
$roomCode = ''
$ugsProfilePrefix = 'smoke' + [Guid]::NewGuid().ToString('N').Substring(0,12)
try {
    for ($i = 0; $i -lt $PlayerCount; $i++) {
        $reportPath = Join-Path $runPath "player-$i.json"
        $logPath = Join-Path $runPath "player-$i.log"
        $arguments = @('-screen-width','1600','-screen-height','1000','-screen-fullscreen','0',
            '-drawSmoke','-drawSmokePlayers',([string]$PlayerCount),'-drawLifecycleSmoke','-drawSmokeMode',$Mode,'-drawName',"Tester$i",'-voiceProfile',"smoke$i",
            '-ugsProfile',($ugsProfilePrefix + '_' + $i),
            '-drawReport',('"'+$reportPath+'"'),'-logFile',('"'+$logPath+'"'))
        if ($i -eq 0) {
            $arguments += @('-drawStopSignal',('"'+$stopSignal+'"'),'-drawCapture',('"'+(Join-Path $runPath 'host.png')+'"'))
            $arguments += $(if ($Online) { '-drawOnlineHost' } else { '-drawHost' })
        } else {
            $arguments += @('-batchmode','-nographics')
            $arguments += $(if ($Online) { @('-drawOnlineJoin',$roomCode) } else { @('-drawJoin','localhost') })
        }
        $processes += Start-Process -FilePath $gamePath -ArgumentList $arguments -WorkingDirectory (Split-Path $gamePath) -WindowStyle Hidden -PassThru
        if ($i -eq 0) {
            $readyDeadline = (Get-Date).AddSeconds(60)
            $hostReady = $false
            while ((Get-Date) -lt $readyDeadline -and -not $hostReady) {
                if ($processes[0].HasExited) { throw 'Host exited during startup.' }
                if (Test-Path -LiteralPath $reportPath) {
                    try {
                        $readyReport = Read-SmokeReport $reportPath
                        $roomCode = $readyReport.RoomCode
                        $hostReady = $readyReport.LastPhase -eq 'Lobby' -and (-not $Online -or -not [string]::IsNullOrWhiteSpace($roomCode))
                    } catch { }
                }
                if (-not $hostReady) { Start-Sleep -Milliseconds 500 }
            }
            if (-not $hostReady) { throw 'Host did not become ready within 60 seconds.' }
            Write-Output 'Host is listening; starting clients.'
        }
    }
    Write-Output "Smoke processes started. Reports: $runPath"
    $deadline = (Get-Date).AddSeconds(270)
    $previous = ''
    while ((Get-Date) -lt $deadline) {
        $reports = @()
        for ($i = 0; $i -lt $processes.Count; $i++) {
            $path = Join-Path $runPath "player-$i.json"
            if (Test-Path -LiteralPath $path) {
                try { $reports += Read-SmokeReport $path } catch { }
            }
        }
        $summary = ($reports | ForEach-Object { "$($_.Player):$($_.Round)/$($_.LastPhase)/$($_.Outcome)" }) -join ' | '
        if ($summary -ne $previous) { Write-Output $summary; $previous = $summary }
        if (@($reports | Where-Object Outcome -eq 'FAIL').Count -gt 0) {
            $reports | ConvertTo-Json -Depth 5 | Write-Output
            throw 'A multiplayer smoke client failed.'
        }
        $hostReport = $reports | Where-Object Player -eq 'Tester0' | Select-Object -First 1
        if ($expectSpectator -and -not $spectatorStarted -and $null -ne $hostReport -and $hostReport.ReplayReady) {
            $arguments = @('-drawSmoke','-drawSmokePlayers',([string]$PlayerCount),'-drawLifecycleSmoke','-drawSpectator','-drawSmokeMode',$Mode,
                '-drawName',('Spectator'+$PlayerCount),'-voiceProfile',('smoke'+$PlayerCount),'-ugsProfile',($ugsProfilePrefix + '_' + $PlayerCount),'-batchmode','-nographics',
                '-drawReplayVersion',([string]$hostReport.ReplayVersion),'-drawReplayHash',$hostReport.ReplayHash,
                '-drawReport',('"'+(Join-Path $runPath "player-$PlayerCount.json")+'"'),
                '-logFile',('"'+(Join-Path $runPath "player-$PlayerCount.log")+'"'))
            $arguments += $(if ($Online) { @('-drawOnlineJoin',$roomCode) } else { @('-drawJoin','localhost') })
            $processes += Start-Process -FilePath $gamePath -ArgumentList $arguments -WorkingDirectory (Split-Path $gamePath) -WindowStyle Hidden -PassThru
            $spectatorStarted = $true
            Write-Output "Joining spectator after three drawn segments: canvas $($hostReport.ReplayVersion), fingerprint $($hostReport.ReplayHash)."
        }
        if (-not $shutdownRequested -and $reports.Count -eq $totalPlayers -and @($reports | Where-Object { -not $_.MatchPassed }).Count -eq 0) {
            Set-Content -LiteralPath $stopSignal -Value 'Leave after all clients verified three rounds.'
            $shutdownRequested = $true
            Write-Output 'All clients verified the match; requesting host Leave and waiting for every connection to close.'
        }
        if ($reports.Count -eq $totalPlayers -and @($reports | Where-Object Outcome -ne 'PASS').Count -eq 0) {
            if ($Online -and (@($reports | Where-Object { -not $_.ServicesCleaned }).Count -gt 0 -or -not $hostReport.LobbyDeletionVerified)) {
                throw 'Online clients did not finish cleanup or the hosted lobby still exists.'
            }
            if (@($reports | Where-Object { -not $_.HostDisconnectObserved }).Count -gt 0) { throw 'A client passed without observing host disconnection.' }
            $spectatorReport = $reports | Where-Object IsSpectator | Select-Object -First 1
            if ($expectSpectator -and ($null -eq $spectatorReport -or -not $spectatorReport.ReplayVerified)) { throw 'The late spectator did not verify canvas replay.' }
            $reports | ConvertTo-Json -Depth 5 | Write-Output
            if (@($reports | Where-Object { $_.PeakPlayers -ne $totalPlayers -or $_.RoomCapacity -ne 12 }).Count -gt 0) { throw "Room capacity or connected participant count did not match the requested test." }
            Write-Output "DRAWLIAR_NETWORK_SMOKE_PASS $connectionLabel$Mode ($PlayerCount players, late spectator=$expectSpectator, host exit)"
            return
        }
        if (@($processes | Where-Object HasExited).Count -gt 0) { throw 'A smoke process exited before completion.' }
        Start-Sleep -Milliseconds 500
    }
    throw 'Multiplayer smoke test timed out.'
} finally {
    if ($Online -and $processes.Count -gt 0 -and -not $processes[0].HasExited) {
        Set-Content -LiteralPath $stopSignal -Value 'Close the online test lobby before stopping processes.'
        $cleanupDeadline = (Get-Date).AddSeconds(25)
        do {
            $hostCleaned = $false
            try { $hostCleaned = (Read-SmokeReport (Join-Path $runPath 'player-0.json')).ServicesCleaned } catch { }
            if (-not $hostCleaned -and -not $processes[0].HasExited) { Start-Sleep -Milliseconds 250 }
        } while (-not $hostCleaned -and -not $processes[0].HasExited -and (Get-Date) -lt $cleanupDeadline)
        if (-not $hostCleaned) { Write-Warning "Host cleanup was not confirmed; inspect reports at $runPath." }
    }
    foreach ($process in $processes) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
    }
}
