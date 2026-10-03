[CmdletBinding()]
param(
    [string]$WebhookFile,
    [string]$KeystoreFile,
    [string]$KeystorePasswordFile,
    [string]$FirebaseServiceAccountFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
foreach ($name in @('JENKINS_URL', 'JENKINS_USER_ID', 'JENKINS_API_TOKEN')) {
    if (![Environment]::GetEnvironmentVariable($name)) { throw "$name environment variable is required." }
}
$base = $env:JENKINS_URL.TrimEnd('/')
$headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($env:JENKINS_USER_ID + ':' + $env:JENKINS_API_TOKEN)) }
$store = "$base/credentials/store/system/domain/_"
$existing = @((Invoke-RestMethod "$store/api/json?tree=credentials[id]" -Headers $headers).credentials.id)
function Save-AndroidCredential([string]$Id, [string]$Path, [bool]$IsFile) {
    if ($existing -contains $Id) { Write-Host "Credential retained: $Id"; return }
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Credential input not found for $Id" }
    $class = if ($IsFile) { 'org.jenkinsci.plugins.plaincredentials.impl.FileCredentialsImpl' } else { 'org.jenkinsci.plugins.plaincredentials.impl.StringCredentialsImpl' }
    $document = [xml]("<$class />")
    $fields = [ordered]@{ scope = 'GLOBAL'; id = $Id; description = 'DrawLiar Android CI' }
    if ($IsFile) {
        $fields.fileName = [IO.Path]::GetFileName($Path)
        $fields.secretBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path))
    }
    else {
        $fields.secret = [IO.File]::ReadAllText($Path).Trim()
        if (!$fields.secret) { throw "Credential input is empty for $Id" }
        if ($Id -eq 'drawliar-discord-build-webhook' -and
            $fields.secret -notmatch '^https://discord(app)?\.com/api/webhooks/[0-9]+/[A-Za-z0-9_-]+$') {
            throw 'Discord webhook input has an invalid format.'
        }
    }
    foreach ($field in $fields.GetEnumerator()) {
        $element = $document.CreateElement($field.Key)
        $element.InnerText = $field.Value
        [void]$document.DocumentElement.AppendChild($element)
    }
    try {
        Invoke-WebRequest "$store/createCredentials" -Headers $headers -Method Post -ContentType 'application/xml; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($document.OuterXml)) -UseBasicParsing | Out-Null
    }
    catch { throw "Credential registration failed for $Id. Check Jenkins access." }
    finally { $fields = $null; $document = $null }
    $verified = @((Invoke-RestMethod "$store/api/json?tree=credentials[id]" -Headers $headers).credentials.id)
    if ($verified -notcontains $Id) { throw "Credential verification failed for $Id" }
    Write-Host "Credential registered: $Id"
}
if ($WebhookFile) { Save-AndroidCredential 'drawliar-discord-build-webhook' $WebhookFile $false }
if ($KeystoreFile) { Save-AndroidCredential 'drawliar-android-keystore' $KeystoreFile $true }
if ($KeystorePasswordFile) { Save-AndroidCredential 'drawliar-android-keystore-password' $KeystorePasswordFile $false }
if ($FirebaseServiceAccountFile) { Save-AndroidCredential 'drawliar-firebase-app-distribution' $FirebaseServiceAccountFile $true }
