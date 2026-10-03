[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArtifactPath,
    [Parameter(Mandatory = $true)][string]$DexDumpPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$artifact = (Resolve-Path -LiteralPath $ArtifactPath).Path
$dexDump = (Resolve-Path -LiteralPath $DexDumpPath).Path
$required = @(
    'Lcom/rascallab/drawliar/accounts/DrawGuestCredentials;',
    'Lcom/drawliar/accounts/DrawGoogleAccount;'
)
$found = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('drawliar-dex-' + [Guid]::NewGuid().ToString('N'))
$extractedFiles = [Collections.Generic.List[string]]::new()
[void][IO.Directory]::CreateDirectory($temporary)
$archive = $null
try {
    $archive = [IO.Compression.ZipFile]::OpenRead($artifact)
    $dexEntries = @($archive.Entries | Where-Object { $_.FullName -match '^(?:base/dex/)?classes(?:[0-9]+)?\.dex$' })
    if ($dexEntries.Count -eq 0) { throw 'Android 산출물에 DEX 파일이 없습니다.' }
    foreach ($entry in $dexEntries) {
        if ($entry.Length -gt 128MB) { throw 'DEX 파일이 검사 용량을 초과합니다.' }
        $dexPath = Join-Path $temporary ([IO.Path]::GetFileName($entry.FullName))
        [void]$extractedFiles.Add($dexPath)
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dexPath)
        # 문자열 참조가 아닌 실제 클래스 정의를 검사합니다.
        & $dexDump $dexPath | ForEach-Object {
            if ($_ -match "Class descriptor\s*:\s*'([^']+)'") {
                if ($required -ccontains $Matches[1]) { [void]$found.Add($Matches[1]) }
            }
        }
        if ($LASTEXITCODE -ne 0) { throw 'Android DEX 검사 도구가 실패했습니다.' }
    }
    $missing = @($required | Where-Object { !$found.Contains($_) })
    if ($missing.Count -ne 0) { throw ('Android 네이티브 로그인 모듈이 빠졌습니다: ' + ($missing -join ', ')) }
    Write-Host 'DRAWLIAR_ANDROID_NATIVE_PLUGINS_OK guest=true google=true'
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    foreach ($path in $extractedFiles) { [IO.File]::Delete($path) }
    [IO.Directory]::Delete($temporary)
}
