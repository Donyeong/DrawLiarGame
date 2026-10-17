[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArtifactPath,
    [Parameter(Mandatory = $true)][ValidateSet('APK', 'AAB')][string]$BuildFormat,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[.-][A-Za-z0-9]+)*$')][string]$BundleVersion,
    [Parameter(Mandatory = $true)][ValidateRange(1, 2100000000)][int]$VersionCode,
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$BuildNumber,
    [string]$BuildResultPath = 'Build/Logs/AndroidBuildResult.json',
    [string]$UploadResultPath = 'Build/Logs/CloudStorageUploadResult.json',
    [string]$GcloudPath = 'C:\Tools\GoogleCloudCLI\google-cloud-sdk\bin\gcloud.cmd'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$bucket = 'kona-survival-tksqml97'
$prefix = 'drawliar/builds/android'
$artifact = Get-Item -LiteralPath $ArtifactPath
$build = Get-Content -Raw -LiteralPath $BuildResultPath | ConvertFrom-Json
$runId = [Guid]::Empty
if ($artifact.PSIsContainer -or $artifact.Length -le 0 -or $artifact.Extension -ine ('.' + $BuildFormat) -or
    $build.Result -ne 'Succeeded' -or $build.Errors -ne 0 -or ![Guid]::TryParse($build.RunId, [ref]$runId) -or
    $build.Format -ne $BuildFormat -or $build.BundleVersion -ne $BundleVersion -or
    $build.VersionCode -ne $VersionCode -or $build.TotalBytes -ne $artifact.Length -or
    [IO.Path]::GetFullPath($build.OutputPath) -ine $artifact.FullName) {
    throw '검증된 이번 Android 빌드 파일만 Cloud Storage에 게시할 수 있습니다.'
}
if (!$env:GOOGLE_APPLICATION_CREDENTIALS -or !(Test-Path -LiteralPath $env:GOOGLE_APPLICATION_CREDENTIALS -PathType Leaf)) {
    throw 'Jenkins Cloud Storage Secret file이 필요합니다.'
}
if (!(Test-Path -LiteralPath $GcloudPath -PathType Leaf)) { throw 'Google Cloud CLI를 찾을 수 없습니다.' }
$resultPath = [IO.Path]::GetFullPath($UploadResultPath)
if ($resultPath -ieq $artifact.FullName -or $resultPath -ieq [IO.Path]::GetFullPath($BuildResultPath)) {
    throw '업로드 결과 경로는 빌드 파일 및 빌드 결과 경로와 달라야 합니다.'
}
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $resultPath))
$downloadFile = Join-Path (Split-Path -Parent $resultPath) 'AndroidDownloads.txt'
if (Test-Path -LiteralPath $downloadFile) { Remove-Item -LiteralPath $downloadFile -Force }
$receipt = [ordered]@{ Success = $false; Format = $BuildFormat; BundleVersion = $BundleVersion; VersionCode = $VersionCode; BuildNumber = $BuildNumber }
$receipt | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
$sha256 = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$stream = [IO.File]::OpenRead($artifact.FullName)
$md5 = [Security.Cryptography.MD5]::Create()
try { $md5Base64 = [Convert]::ToBase64String($md5.ComputeHash($stream)) }
finally { $stream.Dispose(); $md5.Dispose() }
$fileName = "DrawLiar-Android-$BundleVersion-$VersionCode.$($BuildFormat.ToLowerInvariant())"
$objectName = "$prefix/$BundleVersion/$VersionCode/$fileName"
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
    $contentType = if ($BuildFormat -eq 'APK') { 'application/vnd.android.package-archive' } else { 'application/octet-stream' }
    # 버전별 새 객체만 생성하고 공유 버킷의 기존 접근 정책을 유지합니다.
    Invoke-StorageCommand @('storage', 'cp', $artifact.FullName, $destination, '--if-generation-match=0',
        "--content-md5=$md5Base64", "--content-type=$contentType", "--content-disposition=attachment; filename=$fileName",
        '--cache-control=public,max-age=31536000,immutable', "--custom-metadata=sha256=$sha256,build-run-id=$($build.RunId)") | Out-Null
    $remote = Invoke-StorageCommand @('storage', 'objects', 'describe', $destination, '--raw', '--format=json') | ConvertFrom-Json
    if ($remote.bucket -ne $bucket -or $remote.name -ne $objectName -or [long]$remote.size -ne $artifact.Length -or
        $remote.md5Hash -cne $md5Base64 -or $remote.metadata.sha256 -cne $sha256 -or
        $remote.metadata.'build-run-id' -ne $build.RunId) {
        throw 'Cloud Storage 업로드 파일의 크기 또는 체크섬이 이번 빌드와 일치하지 않습니다.'
    }
    # 인증 없는 실제 다운로드 경로도 확인한 뒤에만 링크를 제공합니다.
    $response = Invoke-WebRequest -Uri $downloadUrl -Method Head -UseBasicParsing -TimeoutSec 60
    if ($response.StatusCode -ne 200 -or [long]$response.Headers['Content-Length'] -ne $artifact.Length -or
        ($response.Headers['x-goog-hash'] -join ',') -notmatch ('(^|[,\s])md5=' + [regex]::Escape($md5Base64) + '($|[,\s])')) {
        throw 'Cloud Storage 공개 다운로드 검증에 실패했습니다.'
    }
    $receipt = [ordered]@{
        Success = $true; Bucket = $bucket; ObjectName = $objectName; Generation = $remote.generation
        DownloadUrl = $downloadUrl; Format = $BuildFormat; BundleVersion = $BundleVersion
        VersionCode = $VersionCode; BuildNumber = $BuildNumber; RunId = $build.RunId
        TotalBytes = $artifact.Length; Sha256 = $sha256; CompletedUtc = [DateTime]::UtcNow.ToString('O')
    }
    $receipt | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    "$BuildFormat v$BundleVersion ($VersionCode)`r`n$downloadUrl`r`nSHA256: $sha256" | Set-Content -LiteralPath $downloadFile -Encoding UTF8
    Write-Host "DRAWLIAR_GCS_UPLOAD_OK format=$BuildFormat bytes=$($artifact.Length) sha256=$sha256"
    Write-Host "$BuildFormat 다운로드: $downloadUrl"
}
finally {
    $env:CLOUDSDK_AUTH_CREDENTIAL_FILE_OVERRIDE = $previousCredential
    $env:CLOUDSDK_STORAGE_PARALLEL_COMPOSITE_UPLOAD_ENABLED = $previousComposite
}
