[CmdletBinding()]
param(
    [string]$UnityPath,
    [string]$ProjectPath = (Join-Path $PSScriptRoot '../../DrawLiar'),
    [string]$BuildPath,
    [string]$BuildLogPath,
    [string]$ResultLogPath,
    [string]$BundleVersion = '0.1.0',
    [ValidateRange(1, 2147483647)][int]$BuildNumber = 1,
    [ValidateSet('APK', 'AAB')][string]$BuildFormat = 'APK',
    [bool]$Development = $false,
    [ValidateRange(1, 75)][int]$TimeoutMinutes = 75
)

$ErrorActionPreference = 'Stop'

function Resolve-WorkspacePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath($Path)
}

function ConvertTo-NativeArgument {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $encoded = [Text.StringBuilder]::new()
    [void]$encoded.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$encoded.Append('\', 2 * $slashes + 1)
            [void]$encoded.Append('"')
        }
        else {
            [void]$encoded.Append('\', $slashes)
            [void]$encoded.Append($character)
        }
        $slashes = 0
    }
    [void]$encoded.Append('\', 2 * $slashes)
    [void]$encoded.Append('"')
    return $encoded.ToString()
}

function Protect-BuildLog {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    try {
        $contents = [IO.File]::ReadAllText($Path)
        foreach ($name in @('DRAWLIAR_KEYSTORE_PASS', 'DRAWLIAR_KEY_ALIAS_PASS', 'DRAWLIAR_DISCORD_WEBHOOK')) {
            $value = [Environment]::GetEnvironmentVariable($name)
            if (![string]::IsNullOrEmpty($value)) { $contents = $contents.Replace($value, '***') }
        }
        foreach ($name in @('DRAWLIAR_KEYSTORE_FILE', 'GOOGLE_APPLICATION_CREDENTIALS')) {
            $value = [Environment]::GetEnvironmentVariable($name)
            if ([string]::IsNullOrEmpty($value)) { continue }
            foreach ($variant in @($value, $value.Replace('\', '/'), $value.Replace('/', '\'), $value.Replace('\', '\\')) | Sort-Object -Unique) {
                $contents = [Text.RegularExpressions.Regex]::Replace($contents,
                    [Text.RegularExpressions.Regex]::Escape($variant), '***',
                    [Text.RegularExpressions.RegexOptions]::IgnoreCase)
            }
        }
        [IO.File]::WriteAllText($Path, $contents, [Text.UTF8Encoding]::new($false))
    }
    catch {
        # 자격 증명을 제거하지 못한 원본 로그는 Jenkins에 보관하지 않습니다.
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        Write-Warning '자격 증명 제거에 실패해 Unity 로그 파일을 보관하지 않습니다.'
    }
}

$resolvedProjectPath = Resolve-WorkspacePath $ProjectPath
$repositoryPath = Split-Path -Parent $resolvedProjectPath
$projectVersionPath = Join-Path $resolvedProjectPath 'ProjectSettings/ProjectVersion.txt'
if (!(Test-Path -LiteralPath $projectVersionPath -PathType Leaf)) {
    throw "Unity 프로젝트를 찾을 수 없습니다: $resolvedProjectPath"
}
$editorVersion = (Get-Content -LiteralPath $projectVersionPath | Select-String '^m_EditorVersion: (.+)$').Matches.Groups[1].Value
if ($editorVersion -ne '6000.3.20f1') { throw 'Unity 프로젝트 버전은 6000.3.20f1이어야 합니다.' }
if (!$UnityPath) { $UnityPath = "C:/Program Files/Unity/Hub/Editor/$editorVersion/Editor/Unity.exe" }
if (!(Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw "Unity 실행 파일을 찾을 수 없습니다: $UnityPath" }
if ([string]::IsNullOrWhiteSpace($BundleVersion)) { throw 'BundleVersion이 비어 있습니다.' }
if (!$BuildPath) { $BuildPath = Join-Path $repositoryPath "Build/Android/DrawLiar.$($BuildFormat.ToLowerInvariant())" }
if (!$BuildLogPath) { $BuildLogPath = Join-Path $repositoryPath 'Build/Logs/AndroidBuild.log' }
if (!$ResultLogPath) { $ResultLogPath = Join-Path $repositoryPath 'Build/Logs/AndroidBuildResult.json' }
$resolvedBuildPath = Resolve-WorkspacePath $BuildPath
$resolvedBuildLogPath = Resolve-WorkspacePath $BuildLogPath
$resolvedResultLogPath = Resolve-WorkspacePath $ResultLogPath
if ([IO.Path]::GetExtension($resolvedBuildPath) -ine ".$BuildFormat") {
    throw 'BuildPath 확장자가 BuildFormat과 일치하지 않습니다.'
}
if (@($resolvedBuildPath, $resolvedBuildLogPath, $resolvedResultLogPath | Sort-Object -Unique).Count -ne 3) {
    throw '빌드 파일, 로그 파일, 결과 파일의 경로는 각각 달라야 합니다.'
}
foreach ($path in @($resolvedBuildPath, $resolvedBuildLogPath, $resolvedResultLogPath)) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
}

$runId = [Guid]::NewGuid().ToString()
$startedUtc = [DateTime]::UtcNow
$unityArguments = @(
    '-batchmode', '-nographics', '-quit', '-buildTarget', 'Android',
    '-projectPath', $resolvedProjectPath,
    '-executeMethod', 'DrawLiar.Editor.DrawAndroidBuild.Build',
    '-buildPath', $resolvedBuildPath, '-resultLogPath', $resolvedResultLogPath,
    '-buildRunId', $runId, '-bundleVersion', $BundleVersion,
    '-buildNumber', $BuildNumber.ToString(), '-buildFormat', $BuildFormat,
    '-development', $Development.ToString().ToLowerInvariant(), '-logFile', $resolvedBuildLogPath
)
$argumentLine = ($unityArguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$unityProcess = $null
try {
    # JDK의 Windows 소켓 경로 길이 제한을 피하도록 프로젝트 안의 짧은 경로를 사용합니다.
    $javaTemp = Join-Path $resolvedProjectPath 'Temp/JenkinsJava'
    [void][IO.Directory]::CreateDirectory($javaTemp)
    $env:TEMP = $javaTemp
    $env:TMP = $javaTemp
    $unityProcess = Start-Process -FilePath $UnityPath -ArgumentList $argumentLine -PassThru -WindowStyle Hidden
    [void]$unityProcess.Handle
    if (!$unityProcess.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        throw "Unity Android 빌드 제한 시간을 초과했습니다: $TimeoutMinutes 분."
    }
    $unityProcess.Refresh()
    if ($unityProcess.ExitCode -ne 0) {
        throw "Unity Android 빌드에 실패했습니다. ExitCode: $($unityProcess.ExitCode), 로그: $resolvedBuildLogPath"
    }
    if (!(Test-Path -LiteralPath $resolvedResultLogPath -PathType Leaf)) { throw '이번 빌드의 결과 JSON이 없습니다.' }
    $result = Get-Content -Raw -LiteralPath $resolvedResultLogPath | ConvertFrom-Json
    if ($result.RunId -ne $runId -or $result.Result -ne 'Succeeded' -or $result.Errors -ne 0 -or
        $result.OutputPath -ine $resolvedBuildPath -or $result.Format -ine $BuildFormat -or
        $result.VersionCode -ne $BuildNumber -or $result.BundleVersion -ne $BundleVersion -or
        [bool]$result.Development -ne $Development) {
        throw "Android 빌드 결과가 이번 실행과 일치하지 않습니다: $resolvedResultLogPath"
    }
    $artifact = Get-Item -LiteralPath $resolvedBuildPath -ErrorAction Stop
    if ($artifact.Length -le 0 -or $artifact.Length -ne $result.TotalBytes -or
        $artifact.LastWriteTimeUtc -lt $startedUtc.AddSeconds(-2)) {
        throw '이번 빌드의 정상 산출물을 확인할 수 없습니다.'
    }
    $androidBuildTools = Join-Path (Split-Path -Parent $UnityPath) 'Data/PlaybackEngines/AndroidPlayer/SDK/build-tools'
    $dexDumpPath = Get-ChildItem -LiteralPath $androidBuildTools -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [Version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'dexdump.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if (!$dexDumpPath) { throw 'Android SDK의 dexdump 도구가 없습니다.' }
    & (Join-Path $PSScriptRoot 'Verify-AndroidNativePlugins.ps1') -ArtifactPath $resolvedBuildPath -DexDumpPath $dexDumpPath
    Write-Host "DRAWLIAR_ANDROID_BUILD_OK format=$BuildFormat version=$BundleVersion code=$BuildNumber bytes=$($artifact.Length)"
    Write-Output $result
}
finally {
    if ($null -ne $unityProcess -and !$unityProcess.HasExited) {
        try {
            # 이 스크립트가 시작한 Unity 프로세스만 종료합니다.
            $unityProcess.Kill()
            [void]$unityProcess.WaitForExit(10000)
        }
        catch {
            Write-Warning '빌드용 Unity 프로세스 종료에 실패했습니다.'
        }
    }
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    Protect-BuildLog -Path $resolvedBuildLogPath
    if ($null -ne $unityProcess) { $unityProcess.Dispose() }
}
