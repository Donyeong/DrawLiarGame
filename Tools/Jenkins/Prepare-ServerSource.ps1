[CmdletBinding()]
param(
    [string]$RepositoryPath = (Join-Path $PSScriptRoot '../..'),
    [ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceCommit,
    [string]$ProtocolBaselineCommit,
    [switch]$DedicatedOnly
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath($RepositoryPath)
if (!$SourceCommit) { throw '서버 소스의 전체 커밋 SHA가 필요합니다.' }
function Resolve-Commit([string]$value) {
    if ($value -notmatch '^[a-fA-F0-9]{40}$') { throw '전체 커밋 SHA를 사용하세요.' }
    $resolved = & git -C $repository rev-parse --verify ($value + '^{commit}')
    if ($LASTEXITCODE -ne 0 -or $resolved -notmatch '^[a-f0-9]{40}$') { throw '서버 소스 커밋을 확인할 수 없습니다.' }
    return $resolved
}
$commit = Resolve-Commit $SourceCommit
$baseline = ''
if ($DedicatedOnly) {
    $baseline = Resolve-Commit $ProtocolBaselineCommit
    $protectedInputs = @('Server/DrawLiar.Shared','Server/DrawLiar.DedicatedServer/DrawLiar.DedicatedServer.csproj',
        'DrawLiar/Assets/DrawLiar/Scripts/Services/ServerContracts.cs','DrawLiar/Assets/DrawLiar/Scripts/Game/GameModels.cs',
        'DrawLiar/Assets/DrawLiar/Scripts/Game/GameRules.cs','DrawLiar/Assets/DrawLiar/Scripts/Game/GameplayEnvelope.cs',
        'DrawLiar/Assets/DrawLiar/Scripts/Game/AvatarParts.cs','DrawLiar/Assets/DrawLiar/Scripts/Game/AccountLevelRules.cs',
        'DrawLiar/Assets/DrawLiar/Scripts/Game/TopicWorkshopRules.cs','DrawLiar/Assets/DrawLiar/Scripts/Game/MatchRewardRules.cs',
        'DrawLiar/Assets/DrawLiar/Resources/DrawLiar/GameData.json','DrawLiar/Assets/DrawLiar/Resources/DrawLiar/TopicWorkshopPolicy.json',
        'DrawLiar/Assets/DrawLiar/Resources/DrawLiar/MatchRewardPolicy.json')
    $differences = @(& git -C $repository diff --name-only $baseline $commit -- @protectedInputs)
    if ($LASTEXITCODE -ne 0 -or $differences.Count) { throw 'DedicatedOnly는 기존 core의 Shared·DB·계약·정책과 동일한 소스만 사용할 수 있습니다.' }
}
$inputs = @('Server','DrawLiar/Assets/DrawLiar/Scripts/Game','DrawLiar/Assets/DrawLiar/Scripts/Services',
    'DrawLiar/Assets/DrawLiar/Resources/DrawLiar','Tools/Docker')
$entries = @(& git -C $repository ls-tree -r $commit -- @inputs)
if ($LASTEXITCODE -ne 0 -or @($entries | Where-Object { $_ -match '^120000 ' }).Count) { throw '서버 소스 archive에 링크는 허용하지 않습니다.' }
$allowed = [IO.Path]::GetFullPath((Join-Path $repository 'Build/ServerSource')).TrimEnd('\') + '\'
[void][IO.Directory]::CreateDirectory($allowed)
if ((Get-Item -LiteralPath $allowed).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '서버 소스 출력 링크는 허용하지 않습니다.' }
$source = [IO.Path]::GetFullPath((Join-Path $allowed ('source-' + $commit.Substring(0,8) + '-' + [Guid]::NewGuid().ToString('N'))))
if (!$source.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw '서버 소스 출력 경로 검증 실패' }
[void][IO.Directory]::CreateDirectory($source)
$archive = $source + '.tar'
& git -C $repository archive --format=tar --output=$archive $commit -- @inputs
if ($LASTEXITCODE -ne 0) { throw '서버 소스 archive 생성 실패' }
& tar -xf $archive -C $source
if ($LASTEXITCODE -ne 0) { throw '서버 소스 archive 추출 실패' }
$sourcePrefix = $source.TrimEnd('\') + '\'
$files = @(Get-ChildItem -LiteralPath $source -File -Recurse | ForEach-Object {
    [pscustomobject]@{Path=$_.FullName.Substring($sourcePrefix.Length);Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$manifest = [pscustomobject]@{SourceKind='git-archive';Commit=$commit;ProtocolBaselineCommit=$baseline;ProtocolBaselineValidated=[bool]$DedicatedOnly;Files=$files}
[IO.File]::WriteAllText((Join-Path $source 'source-manifest.json'),($manifest | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{SourceRoot=$source;Commit=$commit;ProtocolBaselineCommit=$baseline;ProtocolBaselineValidated=[bool]$DedicatedOnly;Files=$files.Count} | ConvertTo-Json
