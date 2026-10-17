[CmdletBinding()]
param(
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor\Unity.exe',
    [string]$ProjectPath = 'C:\DrawLiarWindowsJenkins\DrawLiar',
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[.-][A-Za-z0-9]+)*$')][string]$BundleVersion = '0.1.0',
    [ValidateRange(1, 2147483647)][int]$BuildNumber = 1,
    [bool]$Development = $false,
    [ValidateRange(1, 75)][int]$TimeoutMinutes = 75
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$project = [IO.Path]::GetFullPath($ProjectPath).TrimEnd('\')
if ($project -ine 'C:\DrawLiarWindowsJenkins\DrawLiar') {
    throw 'Windows 빌드는 전용 C:\DrawLiarWindowsJenkins\DrawLiar 프로젝트에서 실행해야 합니다.'
}
$workspace = Split-Path -Parent $project
$buildRoot = Join-Path $workspace 'Build'
$output = Join-Path $buildRoot 'Windows/Player'
$logs = Join-Path $buildRoot 'Logs'
$executable = Join-Path $output 'DrawLiar.exe'
$buildLog = Join-Path $logs 'WindowsBuild.log'
$resultLog = Join-Path $logs 'WindowsBuildResult.json'
if (!(Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw 'Unity 실행 파일이 없습니다.' }
$version = Get-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt')
if ($version -notcontains 'm_EditorVersion: 6000.3.20f1') { throw 'Unity 6000.3.20f1 프로젝트가 필요합니다.' }
if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($workspace)).AvailableFreeSpace -lt 10GB) {
    throw 'Windows 빌드에는 디스크 여유 공간이 10GB 이상 필요합니다.'
}
foreach ($path in @($workspace, $project, $buildRoot)) {
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw '빌드 작업 경로는 심볼릭 링크나 junction일 수 없습니다.'
    }
}
if (@(Get-ChildItem -LiteralPath $buildRoot -Recurse -Force -Attributes ReparsePoint).Count -gt 0) {
    throw '빌드 폴더에 심볼릭 링크나 junction이 있습니다.'
}
if (Test-Path -LiteralPath $output) { throw '이전 Player 폴더가 있습니다. 소스 스냅샷 단계부터 다시 실행하세요.' }
[void][IO.Directory]::CreateDirectory($output)
[void][IO.Directory]::CreateDirectory($logs)
$runId = [Guid]::NewGuid().ToString()
$startedUtc = [DateTime]::UtcNow
$arguments = @(
    '-batchmode', '-nographics', '-quit', '-buildTarget', 'Win64',
    '-projectPath', $project, '-executeMethod', 'DrawLiar.Editor.DrawWindowsBuild.Build',
    '-buildPath', $executable, '-resultLogPath', $resultLog, '-buildRunId', $runId,
    '-bundleVersion', $BundleVersion, '-buildNumber', $BuildNumber.ToString(),
    '-development', $Development.ToString().ToLowerInvariant(), '-logFile', $buildLog
)
# 인수는 고정 경로 또는 검증된 값이며 끝의 역슬래시와 따옴표를 허용하지 않습니다.
foreach ($argument in $arguments) {
    if ($argument.Contains('"') -or $argument.EndsWith('\')) { throw '잘못된 Unity 실행 인수입니다.' }
}
$argumentLine = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
$unityProcess = $null
try {
    $unityProcess = Start-Process -FilePath $UnityPath -ArgumentList $argumentLine -PassThru -WindowStyle Hidden
    [void]$unityProcess.Handle
    Write-Host "Windows Unity 빌드 시작: PID=$($unityProcess.Id), log=$buildLog"
    if (!$unityProcess.WaitForExit($TimeoutMinutes * 60 * 1000)) { throw 'Windows 빌드 제한 시간을 초과했습니다.' }
    $unityProcess.Refresh()
    if ($unityProcess.ExitCode -ne 0) { throw "Unity Windows 빌드 실패: exit=$($unityProcess.ExitCode), log=$buildLog" }
    $result = Get-Content -Raw -LiteralPath $resultLog | ConvertFrom-Json
    if ($result.RunId -ne $runId -or $result.Result -ne 'Succeeded' -or $result.Errors -ne 0 -or
        $result.OutputPath -ine $executable -or $result.BuildNumber -ne $BuildNumber -or
        $result.BundleVersion -ne $BundleVersion -or [bool]$result.Development -ne $Development) {
        throw 'Windows 빌드 결과가 이번 실행과 일치하지 않습니다.'
    }
    $requiredFiles = @('DrawLiar.exe', 'UnityPlayer.dll', 'DrawLiar_Data/globalgamemanagers',
        'DrawLiar_Data/Managed/Assembly-CSharp.dll', 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll')
    foreach ($relative in $requiredFiles) {
        $file = Get-Item -LiteralPath (Join-Path $output $relative)
        if ($file.Length -le 0) { throw "필수 Windows 파일이 비어 있습니다: $relative" }
    }
    if ((Get-Item -LiteralPath $executable).LastWriteTimeUtc -lt $startedUtc.AddSeconds(-2)) {
        throw '이번 빌드의 실행 파일이 아닙니다.'
    }
    # 실행 파일의 PE 헤더로 x64 빌드를 확인합니다.
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($executable))
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Windows 실행 파일이 아닙니다.' }
        $reader.BaseStream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $reader.BaseStream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw 'Windows x64 실행 파일이 아닙니다.'
        }
    }
    finally { $reader.Dispose() }
    Copy-Item -LiteralPath $resultLog -Destination (Join-Path $output 'build.json')
    Copy-Item -LiteralPath (Join-Path $logs 'WindowsSource.json') -Destination (Join-Path $output 'source.json')
    $flavor = if ($Development) { '-Development' } else { '' }
    $zip = Join-Path $buildRoot "Windows/DrawLiar-Windows-x64-$BundleVersion-$BuildNumber$flavor.zip"
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $files = @(Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object {
        $_.FullName.Substring($output.Length + 1) -notmatch '(^|[\\/])[^\\/]*(_DoNotShip|_BackUpThisFolder_ButDontShipIt)([\\/]|$)'
    })
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($output.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($relative in $requiredFiles) {
            $entry = $archive.GetEntry($relative)
            if (!$entry -or $entry.Length -ne (Get-Item -LiteralPath (Join-Path $output $relative)).Length) {
                throw "ZIP 필수 파일 검증 실패: $relative"
            }
        }
        if ($archive.Entries.Count -ne $files.Count) {
            throw 'ZIP 파일 수가 빌드 폴더와 일치하지 않습니다.'
        }
    }
    finally { $archive.Dispose() }
    $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($zip + '.sha256', $sha + '  ' + [IO.Path]::GetFileName($zip) + "`n", [Text.Encoding]::ASCII)
    Write-Host "DRAWLIAR_WINDOWS_BUILD_OK version=$BundleVersion build=$BuildNumber bytes=$((Get-Item -LiteralPath $zip).Length) sha256=$sha"
}
finally {
    if ($null -ne $unityProcess) {
        if (!$unityProcess.HasExited) {
            $unityProcess.Kill()
            [void]$unityProcess.WaitForExit(10000)
        }
        $unityProcess.Dispose()
    }
}
