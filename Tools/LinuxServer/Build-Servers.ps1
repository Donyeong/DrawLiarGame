[CmdletBinding()]
param(
    [string]$RepositoryPath = (Join-Path $PSScriptRoot '../..'),
    [string]$SourceRoot,
    [switch]$AdminOnly,
    [switch]$DedicatedOnly
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath($RepositoryPath)
$source = if ($SourceRoot) { [IO.Path]::GetFullPath($SourceRoot) } else { $repository }
if ($AdminOnly -and $DedicatedOnly) { throw 'AdminOnly와 DedicatedOnly는 함께 사용할 수 없습니다.' }
if ($DedicatedOnly) {
    $manifestPath = Join-Path $source 'source-manifest.json'
    if (!$SourceRoot -or !(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'DedicatedOnly는 검증된 Git archive SourceRoot가 필요합니다.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.SourceKind -ne 'git-archive' -or $manifest.Commit -notmatch '^[a-f0-9]{40}$' -or !$manifest.ProtocolBaselineValidated) { throw 'DedicatedOnly 소스 범위 검증에 실패했습니다.' }
    $sourcePrefix = $source.TrimEnd('\') + '\'
    foreach ($file in $manifest.Files) {
        $path = [IO.Path]::GetFullPath((Join-Path $source $file.Path))
        if (!$path.StartsWith($sourcePrefix,[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $file.Sha256) { throw 'DedicatedOnly archive 소스가 변경됐습니다.' }
    }
    $known = @($manifest.Files.Path)
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $relative = $file.FullName.Substring($sourcePrefix.Length)
        if ($relative -ne 'source-manifest.json' -and $relative -notmatch '(^|[\\/])(bin|obj)[\\/]' -and $relative -notin $known) { throw 'DedicatedOnly archive에 승인되지 않은 소스가 추가됐습니다.' }
    }
}
if (!$AdminOnly) {
    & dotnet run --project (Join-Path $source 'Server/DrawLiar.IntegrationTests/DrawLiar.IntegrationTests.csproj') -c Release -- --rules-only
    if ($LASTEXITCODE -ne 0) { throw '게임 규칙 검사 실패' }
}
$output = Join-Path $repository $(if ($AdminOnly) { 'Build/LinuxAdmin' } elseif ($DedicatedOnly) { 'Build/LinuxDedicated' } else { 'Build/LinuxServer' })
New-Item -ItemType Directory -Force -Path $output | Out-Null
$services = if ($AdminOnly) { @('AdminServer') } elseif ($DedicatedOnly) { @('DedicatedServer') } else { @('MainServer','GameServer','DedicatedServer','AdminServer') }
foreach ($name in $services) {
    $project = Join-Path $source "Server/DrawLiar.$name/DrawLiar.$name.csproj"
    $publishPath = [IO.Path]::GetFullPath((Join-Path $output "Apps/$name"))
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $output 'Apps')).TrimEnd('\') + '\'
    if (!$publishPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '빌드 출력 경로 검증 실패' }
    if (Test-Path -LiteralPath $publishPath) {
        if ((Get-Item -LiteralPath $publishPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '빌드 출력 링크는 허용하지 않습니다.' }
        Remove-Item -LiteralPath $publishPath -Recurse -Force
    }
    $revision = if (Test-Path -LiteralPath (Join-Path $source 'source-manifest.json')) { (Get-Content -LiteralPath (Join-Path $source 'source-manifest.json') -Raw | ConvertFrom-Json).Commit } else { '' }
    $properties = if ($revision) { @("-p:SourceRevisionId=$revision",'-p:EnableSourceControlManagerQueries=false') } else { @() }
    & dotnet publish $project -c Release -r linux-x64 --self-contained false -o $publishPath --disable-build-servers @properties
    if ($LASTEXITCODE -ne 0) { throw "$name 게시 실패" }
}
if (!$DedicatedOnly) {
    foreach ($asset in @('index.html','console.js','console.css')) {
        if (!(Test-Path -LiteralPath (Join-Path $output "Apps/AdminServer/wwwroot/$asset") -PathType Leaf)) { throw "관리자 UI 산출물 누락: $asset" }
    }
}
if ($AdminOnly) {
    Write-Host "관리자 서버 게시 완료: $output"
    return
}
if ($DedicatedOnly) {
    Write-Host "데디케이티드 서버 게시 완료: $output"
    return
}
Copy-Item -LiteralPath (Join-Path $repository 'Tools/Docker/Dockerfile') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $repository 'Tools/Docker/compose.yaml') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $repository 'Tools/Docker/Dockerfile.postgres') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $repository 'Tools/Docker/postgres-entrypoint.sh') -Destination $output -Force
Write-Host "Linux 서버 게시 완료: $output"
