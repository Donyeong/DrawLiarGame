[CmdletBinding()]
param(
    [switch]$Disabled,
    [string]$ReceiptPath = 'Build/Logs/DiscordNotification.json'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-NotificationText([string]$Value, [int]$Limit, [string]$Fallback = '확인 불가') {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $Fallback }
    $Value = $Value.Trim()
    if ($Value.Length -gt $Limit) { return $Value.Substring(0, $Limit) }
    return $Value
}

function Get-PublicLink([string]$Value) {
    $Parsed = $null
    if (![Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$Parsed)) { return $null }
    if ($Parsed.Scheme -notin @('http', 'https') -or $Parsed.UserInfo -or
        $Parsed.AbsolutePath -match '/api/webhooks/' -or $Parsed.Fragment) { return $null }
    return $Parsed.AbsoluteUri.Replace('(', '%28').Replace(')', '%29')
}

function Get-FirebaseLink([string]$Value, [string[]]$Hosts) {
    $Link = Get-PublicLink $Value
    if (!$Link) { return $null }
    $Parsed = [Uri]$Link
    if ($Parsed.Scheme -ne 'https' -or $Parsed.Host -notin $Hosts -or !$Parsed.IsDefaultPort) { return $null }
    return $Link
}

function Get-PrivateDownload {
    if (!$env:DRAWLIAR_DOWNLOAD_RESULT) { return $null }
    try {
        $Path = [IO.Path]::GetFullPath($env:DRAWLIAR_DOWNLOAD_RESULT)
        if (![IO.File]::Exists($Path) -or [IO.FileInfo]::new($Path).Length -gt 32768) { return $null }
        $Data = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
        $Link = Get-FirebaseLink ([string]$Data.BinaryDownloadUri) @('firebaseappdistribution.googleapis.com')
        $Expires = [DateTimeOffset]::MinValue
        if (!$Link) { return $null }
        if (!([Uri]$Link).AbsolutePath.StartsWith('/app-binary-downloads/', [StringComparison]::Ordinal)) { return $null }
        if ($Data.ExpiresUtc -is [DateTimeOffset]) {
            $Expires = $Data.ExpiresUtc
        } elseif ($Data.ExpiresUtc -is [DateTime]) {
            $Expires = [DateTimeOffset]::new($Data.ExpiresUtc)
        } elseif (![DateTimeOffset]::TryParse([string]$Data.ExpiresUtc, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$Expires)) { return $null }
        $Now = [DateTimeOffset]::UtcNow
        if ($Expires -le $Now -or $Expires -gt $Now.AddMinutes(65)) { return $null }
        return @{ Uri = $Link; ExpiresUtc = $Expires }
    } catch { return $null }
}

function Get-RateLimitDelay($ErrorRecord) {
    try {
        $Response = $ErrorRecord.Exception.Response
        if (!$Response -or [int]$Response.StatusCode -ne 429) { return $null }
        $Body = $null
        if ($Response.Content -and $Response.Content.PSObject.Methods['ReadAsStringAsync']) {
            $Body = $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        } elseif ($Response.PSObject.Methods['GetResponseStream']) {
            $Reader = [IO.StreamReader]::new($Response.GetResponseStream())
            try { $Body = $Reader.ReadToEnd() } finally { $Reader.Dispose() }
        }
        if (!$Body -and $ErrorRecord.ErrorDetails) { $Body = $ErrorRecord.ErrorDetails.Message }
        $Delay = [double](($Body | ConvertFrom-Json).retry_after)
        if ([double]::IsNaN($Delay) -or [double]::IsInfinity($Delay) -or $Delay -le 0 -or $Delay -gt 10) {
            return $null
        }
        return [int][Math]::Ceiling($Delay * 1000)
    } catch { return $null }
}

if ($Disabled) {
    Write-Output 'Discord 알림 비활성화'
    exit 0
}
if ([string]::IsNullOrWhiteSpace($env:DRAWLIAR_DISCORD_WEBHOOK)) {
    Write-Output 'Discord 알림 생략 · 웹후크 자격 증명 없음'
    exit 0
}

try {
    $Webhook = $null
    if (![Uri]::TryCreate($env:DRAWLIAR_DISCORD_WEBHOOK.Trim(), [UriKind]::Absolute, [ref]$Webhook) -or
        $Webhook.Scheme -ne 'https' -or $Webhook.Host -notin @('discord.com', 'discordapp.com') -or
        !$Webhook.IsDefaultPort -or $Webhook.UserInfo -or $Webhook.Query -or $Webhook.Fragment -or
        $Webhook.AbsolutePath -cnotmatch '^/api/webhooks/[0-9]+/[A-Za-z0-9_-]+$') {
        throw 'Invalid notification credential'
    }

    $Result = Get-NotificationText $env:DRAWLIAR_NOTIFY_RESULT 20
    $ResultText = switch ($Result) {
        'SUCCESS' { '성공' }
        'FAILURE' { '실패' }
        'ABORTED' { '중단' }
        'UNSTABLE' { '불안정' }
        default { '확인 불가' }
    }
    $Color = switch ($Result) {
        'SUCCESS' { 3066993 }
        'FAILURE' { 15158332 }
        default { 15844367 }
    }
    $Revision = Get-NotificationText $env:DRAWLIAR_SOURCE_REVISION 200 ''
    if (!$Revision -and $env:GIT_COMMIT -match '^[0-9a-fA-F]{40,64}$') {
        $Revision = $env:GIT_COMMIT.Substring(0, 12)
    }
    $Revision = Get-NotificationText $Revision 200
    $Format = if ($env:BUILD_FORMAT -in @('APK', 'AAB')) { $env:BUILD_FORMAT } else { '확인 불가' }
    $Version = Get-NotificationText $env:BUNDLE_VERSION 100
    $VersionCode = Get-NotificationText $env:DRAWLIAR_VERSION_CODE 20
    $Job = Get-NotificationText $env:JOB_NAME 210 'DrawLiar-Android'
    $Number = Get-NotificationText $env:BUILD_NUMBER 20
    $ArtifactUrl = Get-PublicLink $env:DRAWLIAR_ARTIFACT_URL
    $BuildUrl = Get-PublicLink $env:BUILD_URL
    $TesterDownloadUrl = $null
    if ($env:DRAWLIAR_DISTRIBUTION_RESULT -in @('DISTRIBUTED', 'EMPTY_GROUP')) {
        $TesterDownloadUrl = Get-FirebaseLink $ArtifactUrl @('appdistribution.firebase.google.com')
        if ($TesterDownloadUrl -and ([Uri]$TesterDownloadUrl).AbsolutePath -cnotmatch '^/testerapps/[A-Za-z0-9:_%.-]+/releases/[A-Za-z0-9_-]+/?$') {
            $TesterDownloadUrl = $null
        }
    }
    $Embed = @{
        title = "$Job #$Number"
        description = "Android 빌드 결과: **$ResultText**"
        color = $Color
        fields = @(
            @{ name = '소요 시간'; value = (Get-NotificationText $env:DRAWLIAR_NOTIFY_DURATION 100); inline = $true },
            @{ name = '소스'; value = $Revision; inline = $false },
            @{ name = '형식 / 버전'; value = "$Format / $Version ($VersionCode)"; inline = $true }
        )
        timestamp = [DateTime]::UtcNow.ToString('o')
    }
    if ($BuildUrl) { $Embed.url = $BuildUrl }
    if ($TesterDownloadUrl) {
        $Embed.url = $TesterDownloadUrl
        $Embed.fields = @(@{
            name = '앱 설치'
            value = "[테스터 앱에서 다운로드]($TesterDownloadUrl)`n테스터 그룹에 등록된 Google 계정으로 로그인해 설치하세요."
            inline = $false
        }) + $Embed.fields
    }
    $Download = Get-PrivateDownload
    $SentDirectDownload = $false
    if ($Download) {
        $Expiry = $Download.ExpiresUtc.ToOffset([TimeSpan]::FromHours(9)).ToString('MM/dd HH:mm')
        $DownloadText = "[다운로드]($($Download.Uri))`n**1시간 제한** · 한국 시간 $Expiry 까지"
        if ($TesterDownloadUrl) {
            if ($DownloadText.Length -le 1024) {
                $Embed.fields += @{ name = "$Format 직접 다운로드 (임시)"; value = $DownloadText; inline = $false }
                $SentDirectDownload = $true
            }
        } else {
            if ($DownloadText.Length -gt 1024) {
                $DownloadText = "위 카드 제목을 누르면 다운로드됩니다.`n**1시간 제한** · 한국 시간 $Expiry 까지"
            }
            $Embed.url = $Download.Uri
            $Embed.fields = @(@{ name = "$Format 바로 다운로드"; value = $DownloadText; inline = $false }) + $Embed.fields
            $SentDirectDownload = $true
        }
    } elseif ($env:DRAWLIAR_DOWNLOAD_RESULT) {
        Write-Output 'APK 직접 다운로드 링크를 확인하지 못했습니다.'
    }
    if ($env:DRAWLIAR_UPLOAD_RESULT) {
        $UploadText = switch ($env:DRAWLIAR_UPLOAD_RESULT) {
            'SUCCESS' { '업로드 완료' }
            'FAILURE' { '업로드 실패' }
            'ABORTED' { '업로드 중단' }
            'SKIPPED' { '업로드 생략' }
            'NOT_STARTED' { '업로드 시작 전' }
            'RUNNING' { '업로드 완료 확인 필요' }
            default { '확인 불가' }
        }
        $Embed.fields += @{ name = 'Firebase 업로드'; value = $UploadText; inline = $true }
    }
    $TesterInviteUrl = Get-FirebaseLink $env:DRAWLIAR_TESTER_INVITE_URL @('appdistribution.firebase.dev', 'appdistribution.firebase.google.com')
    if ($TesterInviteUrl) {
        $Embed.fields += @{ name = '테스터 등록'; value = "[등록 링크]($TesterInviteUrl)"; inline = $false }
    }
    $FirebaseConsoleUrl = Get-FirebaseLink $env:DRAWLIAR_FIREBASE_CONSOLE_URL @('console.firebase.google.com')
    if ($FirebaseConsoleUrl) {
        $Embed.fields += @{ name = 'Firebase 관리'; value = "[앱 배포 콘솔]($FirebaseConsoleUrl)"; inline = $false }
    }
    $Payload = @{ allowed_mentions = @{ parse = @() }; embeds = @($Embed) } | ConvertTo-Json -Depth 6 -Compress
    $Target = [UriBuilder]::new($Webhook)
    $Target.Host = 'discord.com'
    $Target.Query = 'wait=true'
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    $Clock = [Diagnostics.Stopwatch]::StartNew()
    $Response = $null
    for ($Attempt = 0; $Attempt -lt 2; $Attempt++) {
        $Timeout = [Math]::Min(15, [Math]::Floor(29 - $Clock.Elapsed.TotalSeconds))
        if ($Timeout -lt 1) { throw 'Notification timed out' }
        try {
            $Response = Invoke-RestMethod -Uri $Target.Uri -Method Post -ContentType 'application/json; charset=utf-8' `
                -Body ([Text.Encoding]::UTF8.GetBytes($Payload)) -TimeoutSec $Timeout -MaximumRedirection 0 -ErrorAction Stop
            break
        } catch {
            $Delay = Get-RateLimitDelay $_
            if ($Attempt -ne 0 -or $null -eq $Delay -or $Clock.Elapsed.TotalMilliseconds + $Delay -gt 28000) { throw }
            Start-Sleep -Milliseconds $Delay
        }
    }

    if (!$Response -or [string]$Response.id -cnotmatch '^[0-9]+$' -or
        [string]$Response.channel_id -cnotmatch '^[0-9]+$') { throw 'Invalid notification response' }
    $DirectDownloadConfirmed = $false
    $TesterDownloadConfirmed = $false
    if ($Download -and $Response.embeds) {
        foreach ($DeliveredEmbed in $Response.embeds) {
            if ([string]$DeliveredEmbed.url -ceq $Download.Uri) { $DirectDownloadConfirmed = $true }
            foreach ($DeliveredField in $DeliveredEmbed.fields) {
                if ([string]$DeliveredField.value -and ([string]$DeliveredField.value).Contains($Download.Uri)) {
                    $DirectDownloadConfirmed = $true
                }
            }
        }
    }
    if ($TesterDownloadUrl -and $Response.embeds) {
        foreach ($DeliveredEmbed in $Response.embeds) {
            if ([string]$DeliveredEmbed.url -ceq $TesterDownloadUrl) { $TesterDownloadConfirmed = $true }
            foreach ($DeliveredField in $DeliveredEmbed.fields) {
                if ([string]$DeliveredField.value -and ([string]$DeliveredField.value).Contains($TesterDownloadUrl)) {
                    $TesterDownloadConfirmed = $true
                }
            }
        }
    }
    if ($ReceiptPath) {
        try {
            $Receipt = [IO.Path]::GetFullPath($ReceiptPath)
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Receipt)) | Out-Null
            @{
                MessageId = [string]$Response.id
                ChannelId = [string]$Response.channel_id
                Result = $Result
                SentAtUtc = [DateTime]::UtcNow.ToString('o')
                SentDirectDownload = $SentDirectDownload
                HasDirectDownload = $DirectDownloadConfirmed
                HasTesterDownload = $TesterDownloadConfirmed
                DownloadExpiresUtc = $(if ($Download) { $Download.ExpiresUtc.UtcDateTime.ToString('o') } else { $null })
            } |
                ConvertTo-Json | Set-Content -LiteralPath $Receipt -Encoding UTF8
        } catch { Write-Output 'Discord 전송 완료 · 알림 영수증 저장 실패' }
    }
    Write-Output 'Discord 완료 알림 전송 성공'
} catch {
    Write-Output 'Discord 알림 전송 실패 · 빌드 결과 유지'
}
exit 0
