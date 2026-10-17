[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[.-][A-Za-z0-9]+)*$')][string]$BundleVersion,
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$BuildNumber,
    [bool]$Development = $false,
    [string]$BuildResultPath = 'Build/Logs/WindowsBuildResult.json',
    [string]$UploadResultPath = 'Build/Logs/WindowsCloudStorageUploadResult.json',
    [string]$GcloudPath = 'C:\Tools\GoogleCloudCLI\google-cloud-sdk\bin\gcloud.cmd'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$bucket = 'kona-survival-tksqml97'
$prefix = 'drawliar/builds/windows'
$buildResult = [IO.Path]::GetFullPath($BuildResultPath)
$build = Get-Content -Raw -LiteralPath $buildResult | ConvertFrom-Json
$flavor = if ($Development) { '-Development' } else { '' }
$fileName = "DrawLiar-Windows-x64-$BundleVersion-$BuildNumber$flavor.zip"
$windowsRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $buildResult) '../Windows'))
$artifact = Get-Item -LiteralPath (Join-Path $windowsRoot $fileName)
$runId = [Guid]::Empty
if ($artifact.PSIsContainer -or $artifact.Length -le 0 -or
    $build.Result -ne 'Succeeded' -or $build.Errors -ne 0 -or ![Guid]::TryParse($build.RunId, [ref]$runId) -or
    $build.BundleVersion -ne $BundleVersion -or $build.BuildNumber -ne $BuildNumber -or
    [bool]$build.Development -ne $Development -or
    [IO.Path]::GetFullPath($build.OutputPath) -ine (Join-Path $windowsRoot 'Player/DrawLiar.exe')) {
    throw '검증된 이번 Windows 빌드만 Cloud Storage에 게시할 수 있습니다.'
}
$sha256 = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = [IO.File]::ReadAllText($artifact.FullName + '.sha256').Trim()
if ($checksum -cne ($sha256 + '  ' + $fileName)) { throw 'Windows ZIP 체크섬이 일치하지 않습니다.' }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($artifact.FullName)
try {
    foreach ($relative in @('DrawLiar.exe', 'UnityPlayer.dll', 'DrawLiar_Data/globalgamemanagers',
        'DrawLiar_Data/Managed/Assembly-CSharp.dll', 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll', 'build.json', 'source.json')) {
        $entry = $archive.GetEntry($relative)
        if (!$entry -or $entry.Length -le 0) { throw "Windows ZIP 필수 파일 검증 실패: $relative" }
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('build.json').Open())
    try { $packagedBuild = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($packagedBuild -cne [IO.File]::ReadAllText($buildResult)) {
        throw 'Windows ZIP이 이번 빌드 결과와 일치하지 않습니다.'
    }
}
finally { $archive.Dispose() }
if (!$env:GOOGLE_APPLICATION_CREDENTIALS -or !(Test-Path -LiteralPath $env:GOOGLE_APPLICATION_CREDENTIALS -PathType Leaf)) {
    throw 'Jenkins Cloud Storage Secret file이 필요합니다.'
}
if (!(Test-Path -LiteralPath $GcloudPath -PathType Leaf)) { throw 'Google Cloud CLI를 찾을 수 없습니다.' }
$resultPath = [IO.Path]::GetFullPath($UploadResultPath)
if ($resultPath -ieq $artifact.FullName -or $resultPath -ieq $buildResult -or $resultPath -ieq ($artifact.FullName + '.sha256')) {
    throw '업로드 결과 경로는 빌드 파일과 달라야 합니다.'
}
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $resultPath))
$downloadFile = Join-Path (Split-Path -Parent $resultPath) 'WindowsDownloads.txt'
if (Test-Path -LiteralPath $downloadFile) { Remove-Item -LiteralPath $downloadFile -Force }
@{ Success = $false; BundleVersion = $BundleVersion; BuildNumber = $BuildNumber } |
    ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
$stream = [IO.File]::OpenRead($artifact.FullName)
$md5 = [Security.Cryptography.MD5]::Create()
try { $md5Base64 = [Convert]::ToBase64String($md5.ComputeHash($stream)) }
finally { $stream.Dispose(); $md5.Dispose() }
$objectName = "$prefix/$BundleVersion/$BuildNumber/$fileName"
$destination = "gs://$bucket/$objectName"
$downloadUrl = "https://storage.googleapis.com/$bucket/$objectName"

function Invoke-StorageCommand([string[]]$Arguments) {
    $previousErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $GcloudPath @Arguments --quiet --verbosity=error 2>&1)
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorPreference }
    if ($code -ne 0) { throw "Cloud Storage 명령 실패(exit=$code). 업로드 권한·파일 중복·연결 상태를 확인하세요." }
    return ($output | ForEach-Object { [string]$_ }) -join "`n"
}

$previousCredential = $env:CLOUDSDK_AUTH_CREDENTIAL_FILE_OVERRIDE
$previousComposite = $env:CLOUDSDK_STORAGE_PARALLEL_COMPOSITE_UPLOAD_ENABLED
try {
    $env:CLOUDSDK_AUTH_CREDENTIAL_FILE_OVERRIDE = $env:GOOGLE_APPLICATION_CREDENTIALS
    $env:CLOUDSDK_STORAGE_PARALLEL_COMPOSITE_UPLOAD_ENABLED = 'false'
    Invoke-StorageCommand @('storage', 'cp', $artifact.FullName, $destination, '--if-generation-match=0',
        "--content-md5=$md5Base64", '--content-type=application/zip', "--content-disposition=attachment; filename=$fileName",
        '--cache-control=public,max-age=31536000,immutable', "--custom-metadata=sha256=$sha256,build-run-id=$($build.RunId)") | Out-Null
    $remote = Invoke-StorageCommand @('storage', 'objects', 'describe', $destination, '--raw', '--format=json') | ConvertFrom-Json
    if ($remote.bucket -ne $bucket -or $remote.name -ne $objectName -or [long]$remote.size -ne $artifact.Length -or
        $remote.md5Hash -cne $md5Base64 -or $remote.metadata.sha256 -cne $sha256 -or
        $remote.metadata.'build-run-id' -ne $build.RunId) {
        throw 'Cloud Storage 파일의 크기 또는 체크섬이 이번 Windows 빌드와 일치하지 않습니다.'
    }
    $response = Invoke-WebRequest -Uri $downloadUrl -Method Head -UseBasicParsing -TimeoutSec 60
    if ($response.StatusCode -ne 200 -or [long]$response.Headers['Content-Length'] -ne $artifact.Length -or
        ($response.Headers['x-goog-hash'] -join ',') -notmatch ('(^|[,\s])md5=' + [regex]::Escape($md5Base64) + '($|[,\s])')) {
        throw 'Cloud Storage 공개 다운로드 검증에 실패했습니다.'
    }
    [ordered]@{
        Success = $true; Bucket = $bucket; ObjectName = $objectName; Generation = $remote.generation
        DownloadUrl = $downloadUrl; Platform = 'Windows-x64'; BundleVersion = $BundleVersion
        BuildNumber = $BuildNumber; Development = $Development; RunId = $build.RunId
        TotalBytes = $artifact.Length; Sha256 = $sha256; CompletedUtc = [DateTime]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    "Windows x64 v$BundleVersion ($BuildNumber)`r`n$downloadUrl`r`nSHA256: $sha256" |
        Set-Content -LiteralPath $downloadFile -Encoding UTF8
    Write-Host "DRAWLIAR_WINDOWS_GCS_UPLOAD_OK bytes=$($artifact.Length) sha256=$sha256"
    Write-Host "Windows 다운로드: $downloadUrl"
}
finally {
    $env:CLOUDSDK_AUTH_CREDENTIAL_FILE_OVERRIDE = $previousCredential
    $env:CLOUDSDK_STORAGE_PARALLEL_COMPOSITE_UPLOAD_ENABLED = $previousComposite
}
