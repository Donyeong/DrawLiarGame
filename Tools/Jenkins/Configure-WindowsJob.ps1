[CmdletBinding()]
param([ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$JobName = 'DrawLiar-Windows')
$ErrorActionPreference = 'Stop'
foreach ($name in @('JENKINS_URL', 'JENKINS_USER_ID', 'JENKINS_API_TOKEN')) {
    if (![Environment]::GetEnvironmentVariable($name)) { throw "$name 환경 변수가 필요합니다." }
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$base = $env:JENKINS_URL.TrimEnd('/')
$headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($env:JENKINS_USER_ID + ':' + $env:JENKINS_API_TOKEN)) }
$pipeline = [IO.File]::ReadAllText((Join-Path $repository 'Jenkinsfile.Windows'))
$validation = Invoke-RestMethod "$base/pipeline-model-converter/validate" -Headers $headers -Method Post -Body @{ jenkinsfile = $pipeline }
if ($validation -notmatch 'Jenkinsfile successfully validated') { throw "Windows 파이프라인 검증 실패: $validation" }
$document = [xml]'<flow-definition plugin="workflow-job"><description/><keepDependencies>false</keepDependencies><properties/><definition class="org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition" plugin="workflow-cps"><script/><sandbox>true</sandbox></definition><triggers/><disabled>false</disabled></flow-definition>'
$jobs = (Invoke-RestMethod "$base/api/json?tree=jobs[name]" -Headers $headers).jobs.name
if ($jobs -contains $JobName) {
    $previousResponse = Invoke-WebRequest "$base/job/$JobName/config.xml" -Headers $headers -UseBasicParsing
    $previous = [Text.Encoding]::UTF8.GetString($previousResponse.RawContentStream.ToArray())
    $document = [xml]($previous -replace 'version="1\.1"', 'version="1.0"')
    if ($document.'flow-definition'.definition.GetAttribute('class') -ne 'org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition') {
        throw '기존 Windows 작업은 인라인 파이프라인이어야 합니다.'
    }
    $backup = Join-Path $repository 'Private/Jenkins'
    [void][IO.Directory]::CreateDirectory($backup)
    [IO.File]::WriteAllText((Join-Path $backup "$JobName-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).xml"), $previous)
    $url = "$base/job/$JobName/config.xml"
}
else { $url = "$base/createItem?name=$JobName" }
$document.'flow-definition'.definition.script = $pipeline
$document.'flow-definition'.description = 'DrawLiar Windows x64 ZIP. Local working-tree snapshot, Cloud Storage upload, optional Discord download link.'
$parameterProperty = $document.SelectSingleNode('/flow-definition/properties/hudson.model.ParametersDefinitionProperty')
if (!$parameterProperty) {
    $parameterProperty = $document.CreateElement('hudson.model.ParametersDefinitionProperty')
    [void]$document.SelectSingleNode('/flow-definition/properties').AppendChild($parameterProperty)
}
$parameterProperty.RemoveAll()
$definitions = $document.CreateElement('parameterDefinitions')
[void]$parameterProperty.AppendChild($definitions)
$parameters = [regex]::Matches($pipeline, "(?m)^\s*(string|booleanParam)\(name: '([A-Za-z0-9_]+)',([^\r\n]+)\)\s*$")
if ($parameters.Count -ne 4) { throw 'Windows 파이프라인 매개변수를 확인할 수 없습니다.' }
foreach ($parameter in $parameters) {
    $kind = $parameter.Groups[1].Value
    $name = $parameter.Groups[2].Value
    $options = $parameter.Groups[3].Value
    $class = if ($kind -eq 'string') { 'hudson.model.StringParameterDefinition' } else { 'hudson.model.BooleanParameterDefinition' }
    $defaultPattern = if ($kind -eq 'string') { "defaultValue: '([^']*)'" } else { 'defaultValue: (true|false)' }
    $default = [regex]::Match($options, $defaultPattern)
    $description = [regex]::Match($options, "description: '([^']*)'")
    if (!$default.Success -or !$description.Success) { throw "매개변수 파싱 실패: $name" }
    $definition = $document.CreateElement($class)
    foreach ($field in @{ name = $name; description = $description.Groups[1].Value; defaultValue = $default.Groups[1].Value }.GetEnumerator()) {
        $element = $document.CreateElement($field.Key)
        $element.InnerText = $field.Value
        [void]$definition.AppendChild($element)
    }
    [void]$definitions.AppendChild($definition)
}
Invoke-WebRequest $url -Headers $headers -Method Post -ContentType 'application/xml; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($document.OuterXml)) -UseBasicParsing | Out-Null
$installedResponse = Invoke-WebRequest "$base/job/$JobName/config.xml" -Headers $headers -UseBasicParsing
$installedText = [Text.Encoding]::UTF8.GetString($installedResponse.RawContentStream.ToArray())
$installed = [xml]($installedText -replace 'version="1\.1"', 'version="1.0"')
if ($installed.SelectSingleNode('/flow-definition/definition/script').InnerText -ne $pipeline) { throw 'Windows 파이프라인 저장 확인 실패' }
foreach ($definition in $definitions.ChildNodes) {
    $name = $definition.SelectSingleNode('name').InnerText
    $saved = $installed.SelectSingleNode("/flow-definition/properties/hudson.model.ParametersDefinitionProperty/parameterDefinitions/*[name='$name']")
    if (!$saved -or $saved.LocalName -ne $definition.LocalName -or $saved.SelectSingleNode('defaultValue').InnerText -ne $definition.SelectSingleNode('defaultValue').InnerText) {
        throw "Windows 매개변수 저장 확인 실패: $name"
    }
}
Write-Host "Jenkins Windows 작업 등록: $base/job/$JobName/"
