[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ResultsPath,
    [string]$ReceiptPath = 'Build/Metadata/DiscordNotification.json',
    [switch]$ValidateOnly
)
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
        throw '알림 자격 증명 형식 오류'
    }
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    if ($ValidateOnly) {
        $channel = Invoke-RestMethod -Uri $webhook -Method Get -TimeoutSec 15 -MaximumRedirection 0 -ErrorAction Stop
        if ([string]$channel.id -cnotmatch '^[0-9]+$' -or [string]$channel.channel_id -cnotmatch '^[0-9]+$') {
            throw '알림 채널 형식 오류'
        }
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $receipt))
        [ordered]@{ Valid = $true; ChannelId = [string]$channel.channel_id; CheckedAtUtc = [DateTime]::UtcNow.ToString('o') } |
            ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding UTF8
        Write-Output 'Discord 자동 빌드 알림 채널 확인 성공'
        return
    }
    if ($env:BUILD_NUMBER -cnotmatch '^[1-9][0-9]*$') { throw '빌드 번호 형식 오류' }
    $resultNames = @{ SUCCESS = '성공'; FAILURE = '실패'; ABORTED = '중단'; UNSTABLE = '불안정'; NOT_BUILT = '실행 안 됨' }
    $result = $env:DRAWLIAR_NOTIFY_RESULT
    if (!$resultNames.ContainsKey($result)) { throw '빌드 결과 형식 오류' }
    $expected = [ordered]@{ AAB = 'DrawLiar-Android'; APK = 'DrawLiar-Android'; Windows = 'DrawLiar-Windows'; '서버' = 'DrawLiar-Server' }
    $rows = @(Import-Csv -LiteralPath $ResultsPath -Delimiter '|' -Encoding UTF8)
    $fields = @()
    foreach ($label in $expected.Keys) {
        $matching = @($rows | Where-Object { $_.Label -ceq $label })
        if ($matching.Count -gt 1) { throw '중복 빌드 결과' }
        $row = $matching | Select-Object -First 1
        $value = '실행 안 됨'
        if ($row) {
            if ($row.Job -cne $expected[$label] -or !$resultNames.ContainsKey($row.Result) -or
                ($row.Number -and $row.Number -cnotmatch '^[1-9][0-9]*$')) { throw '개별 빌드 결과 형식 오류' }
            $value = $resultNames[$row.Result]
            if ($row.Number) { $value += " · #$($row.Number)" }
        }
        $fields += @{ name = $label; value = $value; inline = $true }
    }
    $embed = @{
        title = "DrawLiar 정오 자동 빌드 #$env:BUILD_NUMBER"
        description = "전체 결과: **$($resultNames[$result])**`nAAB → APK → Windows → 서버 빌드·배포`n서버 빌드 성공 시 자동 배포"
        color = $(if ($result -eq 'SUCCESS') { 3066993 } else { 15158332 })
        fields = $fields
        timestamp = [DateTime]::UtcNow.ToString('o')
    }
    $payload = @{ allowed_mentions = @{ parse = @() }; embeds = @($embed) } | ConvertTo-Json -Depth 6 -Compress
    $target = [UriBuilder]::new($webhook)
    $target.Host = 'discord.com'
    $target.Query = 'wait=true'
    $response = Invoke-RestMethod -Uri $target.Uri -Method Post -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 15 -MaximumRedirection 0 -ErrorAction Stop
    if (!$response -or [string]$response.id -cnotmatch '^[0-9]+$' -or
        [string]$response.channel_id -cnotmatch '^[0-9]+$') { throw '알림 응답 형식 오류' }
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $receipt))
    [ordered]@{
        MessageId = [string]$response.id; ChannelId = [string]$response.channel_id
        Result = $result; BuildNumber = [int]$env:BUILD_NUMBER; SentAtUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding UTF8
    Write-Output 'Discord 자동 빌드 결과 전송 성공'
}
catch {
    Write-Output 'Discord 자동 빌드 결과 전송 실패'
    exit 1
}
