[CmdletBinding()]
param([ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$JobName = 'DrawLiar-Nightly')
$ErrorActionPreference = 'Stop'
foreach ($name in @('JENKINS_URL', 'JENKINS_USER_ID', 'JENKINS_API_TOKEN')) {
    if (![Environment]::GetEnvironmentVariable($name)) { throw "$name 환경 변수가 필요합니다." }
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$base = $env:JENKINS_URL.TrimEnd('/')
$headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($env:JENKINS_USER_ID + ':' + $env:JENKINS_API_TOKEN)) }
$pipeline = [IO.File]::ReadAllText((Join-Path $repository 'Jenkinsfile.Nightly'))
$validation = Invoke-RestMethod "$base/pipeline-model-converter/validate" -Headers $headers -Method Post -Body @{ jenkinsfile = $pipeline }
if ($validation -notmatch 'Jenkinsfile successfully validated') { throw "정오 파이프라인 검증 실패: $validation" }
$cron = [regex]::Match($pipeline, "triggers\s*\{\s*cron\('([^']+)'\)\s*\}")
if (!$cron.Success) { throw '예약 실행 설정이 없습니다.' }
$schedule = $cron.Groups[1].Value.Replace('\n', "`n")
if ($schedule -cne "TZ=Asia/Seoul`n0 12 * * *") { throw '정오 자동 빌드는 한국 시간 매일 12시여야 합니다.' }
$jobs = (Invoke-RestMethod "$base/api/json?tree=jobs[name]" -Headers $headers).jobs.name
foreach ($required in @('DrawLiar-Android', 'DrawLiar-Windows', 'DrawLiar-Server')) {
    if ($jobs -notcontains $required) { throw "하위 빌드 작업이 없습니다: $required" }
}
$credentialIds = (Invoke-RestMethod "$base/credentials/store/system/domain/_/api/json?tree=credentials[id]" -Headers $headers).credentials.id
if ($credentialIds -notcontains 'drawliar-discord-build-webhook') { throw 'DrawLiar Discord 자격 증명이 없습니다.' }
$document = [xml]'<flow-definition plugin="workflow-job"><description/><keepDependencies>false</keepDependencies><properties/><definition class="org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition" plugin="workflow-cps"><script/><sandbox>true</sandbox></definition><triggers/><disabled>false</disabled></flow-definition>'
if ($jobs -contains $JobName) {
    $response = Invoke-WebRequest "$base/job/$JobName/config.xml" -Headers $headers -UseBasicParsing
    $previous = [Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray())
    $document = [xml]($previous -replace 'version="1\.1"', 'version="1.0"')
    if ($document.'flow-definition'.definition.GetAttribute('class') -ne 'org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition') {
        throw '기존 정오 작업은 인라인 파이프라인이어야 합니다.'
    }
    $backup = Join-Path $repository 'Private/Jenkins'
    [void][IO.Directory]::CreateDirectory($backup)
    [IO.File]::WriteAllText((Join-Path $backup "$JobName-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$([Guid]::NewGuid().ToString('N')).xml"), $previous)
    $url = "$base/job/$JobName/config.xml"
}
else { $url = "$base/createItem?name=$JobName" }
$document.'flow-definition'.definition.script = $pipeline
$document.'flow-definition'.description = '한국 시간 매일 정오 12시. DrawLiar AAB → APK → Windows → Linux 서버 순차 빌드, 서버 빌드 성공 시 자동 배포. Discord 개별·전체 결과 알림.'
$properties = $document.SelectSingleNode('/flow-definition/properties')
foreach ($path in @('hudson.model.ParametersDefinitionProperty', 'org.jenkinsci.plugins.workflow.job.properties.PipelineTriggersJobProperty')) {
    $previousProperty = $properties.SelectSingleNode($path)
    if ($previousProperty) { [void]$properties.RemoveChild($previousProperty) }
}
$parameterProperty = $document.CreateElement('hudson.model.ParametersDefinitionProperty')
$definitions = $document.CreateElement('parameterDefinitions')
[void]$parameterProperty.AppendChild($definitions)
[void]$properties.AppendChild($parameterProperty)
$parameters = [regex]::Matches($pipeline, "(?m)^\s*booleanParam\(name: '([A-Za-z0-9_]+)', defaultValue: (true|false), description: '([^']*)'\)\s*$")
if ($parameters.Count -ne 2) { throw '정오 파이프라인 매개변수 2개를 확인하세요.' }
foreach ($parameter in $parameters) {
    $definition = $document.CreateElement('hudson.model.BooleanParameterDefinition')
    foreach ($field in @(@{ Name = 'name'; Value = $parameter.Groups[1].Value },
            @{ Name = 'defaultValue'; Value = $parameter.Groups[2].Value },
            @{ Name = 'description'; Value = $parameter.Groups[3].Value })) {
        $element = $document.CreateElement($field.Name)
        $element.InnerText = $field.Value
        [void]$definition.AppendChild($element)
    }
    [void]$definitions.AppendChild($definition)
}
$triggerProperty = $document.CreateElement('org.jenkinsci.plugins.workflow.job.properties.PipelineTriggersJobProperty')
$triggers = $document.CreateElement('triggers')
$timer = $document.CreateElement('hudson.triggers.TimerTrigger')
$spec = $document.CreateElement('spec')
$spec.InnerText = $schedule
[void]$timer.AppendChild($spec)
[void]$triggers.AppendChild($timer)
[void]$triggerProperty.AppendChild($triggers)
[void]$properties.AppendChild($triggerProperty)
Invoke-WebRequest $url -Headers $headers -Method Post -ContentType 'application/xml; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($document.OuterXml)) -UseBasicParsing | Out-Null
$response = Invoke-WebRequest "$base/job/$JobName/config.xml" -Headers $headers -UseBasicParsing
$installed = [xml]([Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray()) -replace 'version="1\.1"', 'version="1.0"')
if ($installed.SelectSingleNode('/flow-definition/definition/script').InnerText -cne $pipeline -or
    $installed.SelectSingleNode('//hudson.triggers.TimerTrigger/spec').InnerText -cne $schedule -or
    $installed.'flow-definition'.disabled -ne 'false') { throw '정오 파이프라인 저장 확인 실패' }
foreach ($definition in $definitions.ChildNodes) {
    $name = $definition.SelectSingleNode('name').InnerText
    $saved = $installed.SelectSingleNode("//parameterDefinitions/*[name='$name']/defaultValue")
    if (!$saved -or $saved.InnerText -cne $definition.SelectSingleNode('defaultValue').InnerText) { throw "매개변수 저장 확인 실패: $name" }
}
Write-Host "Jenkins 정오 작업 등록: $base/job/$JobName/"
