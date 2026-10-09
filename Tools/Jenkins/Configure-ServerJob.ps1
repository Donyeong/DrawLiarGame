[CmdletBinding()]
param([ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string]$JobName = 'DrawLiar-Server')
$ErrorActionPreference = 'Stop'
foreach ($name in @('JENKINS_URL','JENKINS_USER_ID','JENKINS_API_TOKEN')) {
    if (![Environment]::GetEnvironmentVariable($name)) { throw "$name 환경 변수가 필요합니다." }
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$base = $env:JENKINS_URL.TrimEnd('/')
$headers = @{Authorization='Basic '+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($env:JENKINS_USER_ID+':'+$env:JENKINS_API_TOKEN))}
$pipeline = [IO.File]::ReadAllText((Join-Path $repository 'Jenkinsfile.Server'))
$validation = Invoke-RestMethod "$base/pipeline-model-converter/validate" -Headers $headers -Method Post -Body @{jenkinsfile=$pipeline}
if ($validation -notmatch 'Jenkinsfile successfully validated') { throw 'Jenkins 서버 파이프라인 검증 실패' }
$document = [xml]'<flow-definition plugin="workflow-job"><description>DrawLiar 서버 빌드 및 Ubuntu 배포. 로컬 개발 저장소 C:\DrawLiar 사용.</description><keepDependencies>false</keepDependencies><properties/><definition class="org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition" plugin="workflow-cps"><script/><sandbox>true</sandbox></definition><triggers/><disabled>false</disabled></flow-definition>'
$jobs = (Invoke-RestMethod "$base/api/json?tree=jobs[name]" -Headers $headers).jobs.name
if ($jobs -contains $JobName) {
    $backup = Join-Path $repository 'Private/Jenkins'
    [void][IO.Directory]::CreateDirectory($backup)
    $previous = (Invoke-WebRequest "$base/job/$JobName/config.xml" -Headers $headers -UseBasicParsing).Content
    [IO.File]::WriteAllText((Join-Path $backup "$JobName-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$([Guid]::NewGuid().ToString('N')).xml"), $previous, [Text.UTF8Encoding]::new($false))
    $document = [xml]($previous -replace 'version="1\.1"', 'version="1.0"')
    $definition = $document.SelectSingleNode('/flow-definition/definition')
    if (!$definition -or $definition.GetAttribute('class') -ne 'org.jenkinsci.plugins.workflow.cps.CpsFlowDefinition') { throw '기존 서버 작업은 인라인 파이프라인이어야 합니다.' }
    $url = "$base/job/$JobName/config.xml"
} else { $url = "$base/createItem?name=$JobName" }
$definition = $document.SelectSingleNode('/flow-definition/definition')
$script = $definition.SelectSingleNode('script')
if (!$script) {
    $script = $document.CreateElement('script')
    [void]$definition.AppendChild($script)
}
$script.InnerText = $pipeline
$properties = $document.SelectSingleNode('/flow-definition/properties')
if (!$properties) {
    $properties = $document.CreateElement('properties')
    [void]$document.DocumentElement.InsertBefore($properties, $definition)
}
$parameterProperty = $properties.SelectSingleNode('hudson.model.ParametersDefinitionProperty')
if (!$parameterProperty) {
    $parameterProperty = $document.CreateElement('hudson.model.ParametersDefinitionProperty')
    [void]$properties.AppendChild($parameterProperty)
}
$parameterDefinitions = $parameterProperty.SelectSingleNode('parameterDefinitions')
if (!$parameterDefinitions) {
    $parameterDefinitions = $document.CreateElement('parameterDefinitions')
    [void]$parameterProperty.AppendChild($parameterDefinitions)
}
$requiredParameters = @{
    VM_HOST='string'; SSH_CREDENTIAL_ID='string'; KNOWN_HOSTS_CREDENTIAL_ID='string'
    DEPLOY_TO_VM='booleanParam'; ADMIN_ONLY='booleanParam'; DEDICATED_ONLY='booleanParam'
    SOURCE_COMMIT='string'; PROTOCOL_BASE_COMMIT='string'
}
$parameterLines = [regex]::Matches($pipeline, "(?m)^\s*(string|booleanParam)\(name: '([A-Za-z0-9_]+)',([^\r\n]+)\)\s*$")
if ($parameterLines.Count -ne $requiredParameters.Count) { throw '서버 파이프라인의 필수 매개변수 8개를 확인하세요.' }
$configuredNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in $parameterLines) {
    $kind, $name, $options = $line.Groups[1].Value, $line.Groups[2].Value, $line.Groups[3].Value
    if ($requiredParameters[$name] -ne $kind -or !$configuredNames.Add($name)) { throw "서버 매개변수 형식 오류: $name" }
    $description = [regex]::Match($options, "description: '([^']*)'")
    $default = [regex]::Match($options, $(if ($kind -eq 'string') { "defaultValue: '([^']*)'" } else { 'defaultValue: (true|false)' }))
    if (!$description.Success -or !$default.Success) { throw "서버 매개변수 설정 누락: $name" }
    if ($name -in @('ADMIN_ONLY','DEDICATED_ONLY') -and $default.Groups[1].Value -ne 'false') { throw '단독 배포 매개변수 기본값은 false이어야 합니다.' }
    foreach ($existing in @($parameterDefinitions.ChildNodes)) {
        $existingName = $existing.SelectSingleNode('name')
        if ($existingName -and $existingName.InnerText -eq $name) { [void]$parameterDefinitions.RemoveChild($existing) }
    }
    $class = if ($kind -eq 'string') { 'hudson.model.StringParameterDefinition' } else { 'hudson.model.BooleanParameterDefinition' }
    $parameter = $document.CreateElement($class)
    foreach ($field in @(@{Name='name';Value=$name}, @{Name='description';Value=$description.Groups[1].Value}, @{Name='defaultValue';Value=$default.Groups[1].Value})) {
        $element = $document.CreateElement($field.Name)
        $element.InnerText = $field.Value
        [void]$parameter.AppendChild($element)
    }
    [void]$parameterDefinitions.AppendChild($parameter)
}
Invoke-WebRequest $url -Headers $headers -Method Post -ContentType 'application/xml; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($document.OuterXml)) -UseBasicParsing | Out-Null
$installed = [xml]((Invoke-WebRequest "$base/job/$JobName/config.xml" -Headers $headers -UseBasicParsing).Content -replace 'version="1\.1"', 'version="1.0"')
if ($installed.SelectSingleNode('/flow-definition/definition/script').InnerText -ne $pipeline) { throw 'Jenkins 서버 파이프라인 저장 확인 실패' }
foreach ($name in $requiredParameters.Keys) {
    $parameter = $installed.SelectNodes('/flow-definition/properties/hudson.model.ParametersDefinitionProperty/parameterDefinitions/*') | Where-Object { $_.SelectSingleNode('name').InnerText -eq $name }
    $expected = $parameterDefinitions.ChildNodes | Where-Object { $_.SelectSingleNode('name').InnerText -eq $name }
    if (@($parameter).Count -ne 1 -or $parameter.LocalName -ne $expected.LocalName -or $parameter.SelectSingleNode('defaultValue').InnerText -ne $expected.SelectSingleNode('defaultValue').InnerText) { throw "Jenkins 서버 매개변수 저장 확인 실패: $name" }
}
Write-Host "Jenkins 작업 등록: $base/job/$JobName/"
