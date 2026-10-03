[CmdletBinding()]
param([string]$RepositoryPath = (Join-Path $PSScriptRoot '../..'))
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath($RepositoryPath)
& dotnet run --project (Join-Path $repository 'Server/DrawLiar.IntegrationTests/DrawLiar.IntegrationTests.csproj') -c Release -- --rules-only
if ($LASTEXITCODE -ne 0) { throw '게임 규칙 검사 실패' }
$output = Join-Path $repository 'Build/LinuxServer'
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($name in @('MainServer','GameServer','DedicatedServer','AdminServer')) {
    $project = Join-Path $repository "Server/DrawLiar.$name/DrawLiar.$name.csproj"
    $publishPath = [IO.Path]::GetFullPath((Join-Path $output "Apps/$name"))
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repository 'Build/LinuxServer/Apps')).TrimEnd('\') + '\'
    if (!$publishPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '빌드 출력 경로 검증 실패' }
    if (Test-Path -LiteralPath $publishPath) {
        if ((Get-Item -LiteralPath $publishPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '빌드 출력 링크는 허용하지 않습니다.' }
        Remove-Item -LiteralPath $publishPath -Recurse -Force
    }
    & dotnet publish $project -c Release -r linux-x64 --self-contained false -o $publishPath --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw "$name 게시 실패" }
}
Copy-Item -LiteralPath (Join-Path $repository 'Tools/Docker/Dockerfile') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $repository 'Tools/Docker/compose.yaml') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $repository 'Tools/Docker/Dockerfile.postgres') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $repository 'Tools/Docker/postgres-entrypoint.sh') -Destination $output -Force
Write-Host "Linux 서버 게시 완료: $output"
