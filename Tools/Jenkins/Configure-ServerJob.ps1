[CmdletBinding()]
param([string]$JobName = 'DrawLiar-Server')
$ErrorActionPreference = 'Stop'
foreach ($name in @('JENKINS_URL','JENKINS_USER_ID','JENKINS_API_TOKEN')) {
    if (![Environment]::GetEnvironmentVariable($name)) { throw "$name 환경 변수가 필요합니다." }
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$base = $env:JENKINS_URL.TrimEnd('/')
$headers = @{Authorization='Basic '+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($env:JENKINS_USER_ID+':'+$env:JENKINS_API_TOKEN))}
$pipeline = [IO.File]::ReadAllText((Join-Path $repository 'Jenkinsfile.Server'))
$document = [xml]'<flow-definition plugin="workflow-job"><description>DrawLiar 서버 빌드 및 Ubuntu 배포. 로컬 개발 저장소 C:\DrawLiar 사용.</description><keepDependencies>false</keepDependencies><properties/><definition class="org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition" plugin="workflow-cps"><script/><sandbox>true</sandbox></definition><triggers/><disabled>false</disabled></flow-definition>'
$document.'flow-definition'.definition.script = $pipeline
$jobs = (Invoke-RestMethod "$base/api/json?tree=jobs[name]" -Headers $headers).jobs.name
if ($jobs -contains $JobName) {
    $backup = Join-Path $repository 'Private/Jenkins'
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    (Invoke-WebRequest "$base/job/$JobName/config.xml" -Headers $headers).Content | Set-Content (Join-Path $backup "$JobName-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).xml")
    $url = "$base/job/$JobName/config.xml"
} else { $url = "$base/createItem?name=$JobName" }
Invoke-WebRequest $url -Headers $headers -Method Post -ContentType 'application/xml; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($document.OuterXml)) | Out-Null
Write-Host "Jenkins 작업 등록: $base/job/$JobName/"
