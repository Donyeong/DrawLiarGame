param([string]$ProjectRoot = 'C:\DrawLiar', [switch]$Online)

$ErrorActionPreference = 'Stop'
$gamePath = Join-Path $ProjectRoot 'DrawLiar\Builds\Windows\DrawLiar.exe'
if (-not (Test-Path -LiteralPath $gamePath)) { throw "Build the development Windows player first: $gamePath" }
$connectionLabel = if ($Online) { 'online-' } else { '' }
$runPath = Join-Path $ProjectRoot ('.codex-temp\voice-smoke-' + $connectionLabel + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $runPath -Force | Out-Null
$commandPath = Join-Path $runPath 'command.json'
$processes = @()
$results = [Collections.Generic.List[object]]::new()
$profile = 'voice-smoke-' + [Guid]::NewGuid().ToString('N')
$sequence = 0
$roomCode = ''
$ugsProfile = 'voice' + [Guid]::NewGuid().ToString('N').Substring(0,16)
$passed = $false
$cleanupFailed = $false

function Send-Command([string]$Stage, [int]$Sender, [bool]$Talk = $true, [bool]$Typing = $false,
    [bool]$Muted = $false, [bool]$PeerMuted = $false, [float]$PeerVolume = 1, [bool]$Finish = $false) {
    $script:sequence++
    $json = @{Sequence=$script:sequence; Stage=$Stage; Sender=$Sender; Talk=$Talk; Typing=$Typing;
        Muted=$Muted; PeerMuted=$PeerMuted; PeerVolume=$PeerVolume; Finish=$Finish} | ConvertTo-Json
    [IO.File]::WriteAllText($commandPath + '.tmp', $json)
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            if ([IO.File]::Exists($commandPath)) { [IO.File]::Replace($commandPath + '.tmp', $commandPath, [NullString]::Value) }
            else { [IO.File]::Move($commandPath + '.tmp', $commandPath) }
            return
        } catch [IO.IOException] {
            if ($attempt -eq 9) { throw }
            Start-Sleep -Milliseconds 50
        }
    }
}

function Read-Reports {
    for ($i = 0; $i -lt 2; $i++) {
        $path = Join-Path $runPath "player-$i.json"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        try {
            $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            try {
                $reader = [IO.StreamReader]::new($stream)
                try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            } finally { $stream.Dispose() }
        } catch [IO.IOException] { }
    }
}

function Wait-Reports([scriptblock]$Ready, [int]$Timeout = 25) {
    $deadline = (Get-Date).AddSeconds($Timeout)
    do {
        $reports = @(Read-Reports)
        foreach ($report in $reports) { if ($report.Error) { throw $report.Error } }
        if (& $Ready $reports) { return ,$reports }
        foreach ($process in $processes) { if ($process.HasExited) { throw "Player $($process.Id) exited. Logs: $runPath" } }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "Voice check timed out. Ensure a development build and an active Windows audio output device. Reports: $runPath"
}

function Measure-Stage([string]$Name, [int]$Sender, [bool]$Talk = $true, [bool]$Typing = $false,
    [bool]$Muted = $false, [bool]$PeerMuted = $false, [float]$PeerVolume = 1) {
    Send-Command $Name $Sender $Talk $Typing $Muted $PeerMuted $PeerVolume
    $reports = Wait-Reports {
        param($r)
        $r.Count -eq 2 -and @($r | Where-Object { $_.Sequence -ne $script:sequence -or $_.MeasurementSeconds -lt 2 -or $_.Measurements -lt 20 }).Count -eq 0
    }
    $results.Add([PSCustomObject]@{Stage=$Name; Reports=$reports})
    if ($Online -and @($reports | Where-Object { -not $_.RelayPathVerified }).Count -gt 0) { throw 'Voice measurement did not use an active authenticated Relay transport.' }
    $receiver = $reports | Where-Object Player -ne $Sender
    Write-Output "${Name}: decoded RMS=$($receiver.Rms), callbacks=$($receiver.DecodedCallbacks), drain=$($receiver.DrainSeconds)s, speaking frames=$($receiver.PeerSpeakingFrames)" | Write-Host
    return ,$reports
}

try {
    Send-Command 'Waiting' -1 $false
    for ($i = 0; $i -lt 2; $i++) {
        $arguments = @('-screen-width','800','-screen-height','600','-screen-fullscreen','0',
            '-drawVoiceSmoke','-drawVoicePlayer',"$i",'-voiceProfile',($profile + "-$i"),'-drawName',"VoiceTest$i",
            '-ugsProfile',($ugsProfile + "_$i"),
            '-drawVoiceCommand',('"' + $commandPath + '"'),
            '-drawVoiceReport',('"' + (Join-Path $runPath "player-$i.json") + '"'),
            '-logFile',('"' + (Join-Path $runPath "player-$i.log") + '"'))
        $arguments += $(if ($i -eq 0) { if ($Online) { @('-drawOnlineHost') } else { @('-drawHost') } }
            else { if ($Online) { @('-drawOnlineJoin',$roomCode) } else { @('-drawJoin','localhost') } })
        # Keep the audio engine running. The helper mutes the final listener, not the source being measured.
        $processes += Start-Process -FilePath $gamePath -ArgumentList $arguments -WorkingDirectory (Split-Path $gamePath) -WindowStyle Hidden -PassThru
        if ($i -eq 0) {
            $hostReport = Wait-Reports { param($r) $r.Count -eq 1 -and $r[0].HostReady -and (-not $Online -or $r[0].RoomCode) } 60
            $roomCode = $hostReport[0].RoomCode
        }
    }
    $ready = Wait-Reports { param($r) $r.Count -eq 2 -and @($r | Where-Object { -not $_.Connected -or -not $_.PeerConnected -or -not $_.ProfileMapped }).Count -eq 0 }
    if ($ready[0].VoiceId -eq $ready[1].VoiceId) { throw 'The two processes share a voice identity.' }
    if ($Online -and @($ready | Where-Object { -not $_.Online -or -not $_.RelayPathVerified -or $_.RoomCode -ne $roomCode }).Count -gt 0) { throw 'The two voice peers did not join the same UGS Relay lobby.' }
    if (@($ready | Where-Object { -not $_.ListenerMuted }).Count -gt 0) { throw 'Voice smoke must silence the final audio listener.' }
    Write-Host "Both voice peers connected; final audio listener is silent. Synthetic input amplitude <= 0.01. Reports: $runPath"

    $full = Measure-Stage 'HostToClient' 0
    $baseline = [double]($full | Where-Object Player -eq 1).Rms
    if ($baseline -lt 0.000005 -or ($full | Where-Object Player -eq 1).DecodedCallbacks -lt 5 -or ($full | Where-Object Player -eq 1).PeerSpeakingFrames -eq 0 -or ($full | Where-Object Player -eq 0).LocalSpeakingFrames -eq 0) {
        throw 'No nonzero decoded PCM callbacks or speaking indicators for host-to-client transmission.'
    }
    $quietLimit = [Math]::Max(0.000001, $baseline * 0.03)

    $muted = Measure-Stage 'PeerMuted' 0 $true $false $false $true
    $receiver = $muted | Where-Object Player -eq 1
    if (-not $receiver.PeerMuted -or $receiver.Rms -gt $quietLimit -or $receiver.DecodedCallbacks -lt 5 -or $receiver.PeerSpeakingFrames -eq 0) { throw 'Per-peer mute did not silence decoded audio while retaining the speech session.' }

    $quarter = Measure-Stage 'PeerVolumeQuarter' 0 $true $false $false $false 0.25
    $receiver = $quarter | Where-Object Player -eq 1
    $ratio = $receiver.Rms / $baseline
    if ([Math]::Abs($receiver.PeerVolume - 0.25) -gt 0.001 -or $ratio -lt 0.12 -or $ratio -gt 0.4) { throw "Per-peer volume RMS ratio should approximate 0.25; received $ratio." }

    foreach ($gate in @('PttReleased','TypingBlocked','MicrophoneMuted')) {
        $reports = Measure-Stage $gate 0 ($gate -ne 'PttReleased') ($gate -eq 'TypingBlocked') ($gate -eq 'MicrophoneMuted')
        $sender = $reports | Where-Object Player -eq 0
        $receiver = $reports | Where-Object Player -eq 1
        if (-not $sender.CommsMuted -or $sender.LocalSpeakingFrames -ne 0 -or $receiver.PeerSpeakingFrames -ne 0 -or $sender.DrainSeconds -gt 4 -or $receiver.DrainSeconds -gt 4 -or $receiver.Rms -gt $quietLimit) { throw "$gate did not block microphone transmission after the bounded receive drain." }
    }

    $reverse = Measure-Stage 'ClientToHost' 1
    if (($reverse | Where-Object Player -eq 0).Rms -lt 0.000005 -or ($reverse | Where-Object Player -eq 0).DecodedCallbacks -lt 5 -or ($reverse | Where-Object Player -eq 0).PeerSpeakingFrames -eq 0 -or ($reverse | Where-Object Player -eq 1).LocalSpeakingFrames -eq 0) {
        throw 'No nonzero decoded PCM callbacks or speaking indicators for client-to-host transmission.'
    }
    $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runPath 'results.json') -Encoding UTF8
    $passed = $true
} finally {
    try {
        try { Send-Command 'Finish' -1 $false $false $false $false 1 $true }
        catch { Write-Warning "Could not request graceful voice smoke shutdown: $($_.Exception.Message)" }
        $exitDeadline = (Get-Date).AddSeconds($(if ($Online) { 30 } else { 5 }))
        while (@($processes | Where-Object { -not $_.HasExited }).Count -gt 0 -and (Get-Date) -lt $exitDeadline) { Start-Sleep -Milliseconds 100 }
    } finally {
        foreach ($process in $processes) { if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue } }
    }
    $cleanup = @(Read-Reports)
    $results.Add([PSCustomObject]@{Stage='Cleanup'; Reports=$cleanup})
    $cleanupFailed = $cleanup.Count -ne 2 -or @($cleanup | Where-Object { -not $_.Finished -or -not $_.ServicesCleaned -or $_.Error }).Count -gt 0
    if ($Online -and -not ($cleanup | Where-Object Player -eq 0).LobbyDeletionVerified) { $cleanupFailed = $true }
    if ($cleanupFailed) { Write-Warning "Voice smoke cleanup was not confirmed. Inspect $runPath before retrying." }
    if ($results.Count -gt 0) { $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runPath 'results.json') -Encoding UTF8 }
}
if ($cleanupFailed) { throw 'Voice smoke cleanup or hosted lobby deletion was not confirmed.' }
if ($passed) { Write-Host "DRAWLIAR_VOICE_SMOKE_PASS $connectionLabel(synthetic input, two processes, both directions, decoded RMS, mute, volume, PTT, typing, cleanup)" }
