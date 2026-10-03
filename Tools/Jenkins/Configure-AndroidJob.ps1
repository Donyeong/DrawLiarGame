[CmdletBinding()]
param([ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$JobName = 'DrawLiar-Android')
$ErrorActionPreference = 'Stop'
foreach ($name in @('JENKINS_URL', 'JENKINS_USER_ID', 'JENKINS_API_TOKEN')) {
    if (![Environment]::GetEnvironmentVariable($name)) { throw "$name environment variable is required." }
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$base = $env:JENKINS_URL.TrimEnd('/')
$headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($env:JENKINS_USER_ID + ':' + $env:JENKINS_API_TOKEN)) }
$pipeline = [IO.File]::ReadAllText((Join-Path $repository 'Jenkinsfile.Android'))
$validation = Invoke-RestMethod "$base/pipeline-model-converter/validate" -Headers $headers -Method Post -Body @{ jenkinsfile = $pipeline }
if ($validation -notmatch 'Jenkinsfile successfully validated') { throw 'Jenkins rejected the Android pipeline definition.' }
$document = [xml]'<flow-definition plugin="workflow-job"><description>DrawLiar Android APK/AAB. Local working-tree snapshot into C:\DrawLiarJenkins; daily 01:00 Asia/Seoul; Firebase and Discord.</description><keepDependencies>false</keepDependencies><properties/><definition class="org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition" plugin="workflow-cps"><script/><sandbox>true</sandbox></definition><triggers/><disabled>false</disabled></flow-definition>'
$document.'flow-definition'.definition.script = $pipeline
$jobs = (Invoke-RestMethod "$base/api/json?tree=jobs[name]" -Headers $headers).jobs.name
if ($jobs -contains $JobName) {
    $backup = Join-Path $repository 'Private/Jenkins'
    [void][IO.Directory]::CreateDirectory($backup)
    $previous = (Invoke-WebRequest "$base/job/$JobName/config.xml" -Headers $headers -UseBasicParsing).Content
    [IO.File]::WriteAllText((Join-Path $backup "$JobName-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).xml"), $previous)
    $document = [xml]($previous -replace 'version="1\.1"', 'version="1.0"')
    if ($document.'flow-definition'.definition.GetAttribute('class') -ne 'org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition') {
        throw 'The existing Android job is not the expected inline pipeline.'
    }
    $document.'flow-definition'.definition.script = $pipeline
    $url = "$base/job/$JobName/config.xml"
}
else { $url = "$base/createItem?name=$JobName" }
Invoke-WebRequest $url -Headers $headers -Method Post -ContentType 'application/xml; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($document.OuterXml)) -UseBasicParsing | Out-Null
Write-Host "Jenkins Android job configured: $base/job/$JobName/"
