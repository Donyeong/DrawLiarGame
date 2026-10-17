[CmdletBinding()]
param([string]$ReceiptPath = 'Build/Logs/WindowsDiscordNotification.json')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

try {
    $receipt = [IO.Path]::GetFullPath($ReceiptPath)
    if (Test-Path -LiteralPath $receipt -PathType Leaf) { Remove-Item -LiteralPath $receipt -Force }
    $webhook = $null
    if (![Uri]::TryCreate($env:DRAWLIAR_DISCORD_WEBHOOK, [UriKind]::Absolute, [ref]$webhook) -or
        $webhook.Scheme -ne 'https' -or $webhook.Host -notin @('discord.com', 'discordapp.com') -or
        !$webhook.IsDefaultPort -or $webhook.UserInfo -or $webhook.Query -or $webhook.Fragment -or
        $webhook.AbsolutePath -cnotmatch '^/api/webhooks/[0-9]+/[A-Za-z0-9_-]+$') {
        throw 'Invalid notification credential'
    }
    $version = $env:BUNDLE_VERSION
    $number = $env:BUILD_NUMBER
    if ($version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[.-][A-Za-z0-9]+)*$' -or
        $number -cnotmatch '^[1-9][0-9]*$' -or $env:DEVELOPMENT_BUILD -notin @('true', 'false')) {
        throw 'Invalid Windows build identity'
    }
    $flavor = if ($env:DEVELOPMENT_BUILD -eq 'true') { '-Development' } else { '' }
    $expectedUrl = "https://storage.googleapis.com/kona-survival-tksqml97/drawliar/builds/windows/$version/$number/DrawLiar-Windows-x64-$version-$number$flavor.zip"
    $downloadUrl = $null
    if ($env:DRAWLIAR_GCS_DOWNLOAD_URL) {
        if ($env:DRAWLIAR_BUILD_ARTIFACT_READY -ne 'true' -or $env:DRAWLIAR_GCS_DOWNLOAD_URL -cne $expectedUrl) {
            throw 'Download link does not match this Windows build'
        }
        $downloadUrl = $expectedUrl
    }
    $result = $env:DRAWLIAR_NOTIFY_RESULT
    if ($result -eq 'SUCCESS' -and $env:UPLOAD_GCS -eq 'true' -and !$downloadUrl) {
        throw 'Successful upload has no verified download link'
    }
    $resultText = switch ($result) {
        'SUCCESS' { '성공' }
        'FAILURE' { '실패' }
        'ABORTED' { '중단' }
        'UNSTABLE' { '불안정' }
        default { '확인 불가' }
    }
    $embed = @{
        title = "DrawLiar-Windows #$number"
        description = "PC Windows x64 빌드 결과: **$resultText**"
        color = $(if ($result -eq 'SUCCESS') { 3066993 } else { 15158332 })
        fields = @(@{ name = '버전'; value = "$version ($number)$flavor"; inline = $true })
        timestamp = [DateTime]::UtcNow.ToString('o')
    }
    if ($downloadUrl) {
        $embed.url = $downloadUrl
        $embed.fields = @(@{
            name = 'PC 빌드 다운로드'
            value = "[Windows x64 ZIP 다운로드]($downloadUrl)`n압축을 모두 푼 뒤 DrawLiar.exe를 실행하세요."
            inline = $false
        }) + $embed.fields
    }
    $payload = @{ allowed_mentions = @{ parse = @() }; embeds = @($embed) } | ConvertTo-Json -Depth 6 -Compress
    $target = [UriBuilder]::new($webhook)
    $target.Host = 'discord.com'
    $target.Query = 'wait=true'
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    $response = Invoke-RestMethod -Uri $target.Uri -Method Post -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 15 -MaximumRedirection 0 -ErrorAction Stop
    if (!$response -or [string]$response.id -cnotmatch '^[0-9]+$' -or
        [string]$response.channel_id -cnotmatch '^[0-9]+$') { throw 'Invalid notification response' }
    $confirmed = $false
    if ($downloadUrl) {
        foreach ($deliveredEmbed in $response.embeds) {
            foreach ($field in $deliveredEmbed.fields) {
                if ([string]$field.value -and ([string]$field.value).Contains("]($downloadUrl)")) { $confirmed = $true }
            }
        }
    }
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $receipt))
    [ordered]@{
        MessageId = [string]$response.id; ChannelId = [string]$response.channel_id; Result = $result
        BuildNumber = [int]$number; HasCloudStorageDownload = $confirmed; SentAtUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding UTF8
    if ($downloadUrl -and !$confirmed) { throw 'Cloud Storage link missing from delivered message' }
    Write-Output 'Discord Windows 빌드 알림 전송 성공'
}
catch {
    Write-Output 'Discord Windows 빌드 알림 전송 또는 다운로드 링크 확인 실패'
    exit 1
}
