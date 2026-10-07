[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UnityPath,
    [string]$ArtifactPath
)

$ErrorActionPreference = 'Stop'
foreach ($Name in @('DRAWLIAR_KEYSTORE_FILE', 'DRAWLIAR_KEYSTORE_PASS', 'DRAWLIAR_KEY_ALIAS', 'DRAWLIAR_KEY_ALIAS_PASS')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($Name))) {
        throw "Play 제출용 서명 자격 증명이 필요합니다: $Name"
    }
}
$Keytool = Join-Path (Split-Path -Parent $UnityPath) 'Data/PlaybackEngines/AndroidPlayer/OpenJDK/bin/keytool.exe'
if (!(Test-Path -LiteralPath $Keytool -PathType Leaf)) { throw 'Unity Android JDK의 keytool을 찾을 수 없습니다.' }
if (!(Test-Path -LiteralPath $env:DRAWLIAR_KEYSTORE_FILE -PathType Leaf)) { throw 'Play 제출용 키 저장소 파일을 찾을 수 없습니다.' }
$VerifyArtifact = $PSBoundParameters.ContainsKey('ArtifactPath')
if ($VerifyArtifact) {
    if ([string]::IsNullOrWhiteSpace($ArtifactPath) -or [IO.Path]::GetExtension($ArtifactPath) -ine '.aab') {
        throw 'Play 제출용 서명 검사는 AAB 파일만 허용합니다.'
    }
    if (!(Test-Path -LiteralPath $ArtifactPath -PathType Leaf)) { throw '서명을 검사할 AAB 파일을 찾을 수 없습니다.' }
    $ArtifactPath = (Resolve-Path -LiteralPath $ArtifactPath).Path
    $Jarsigner = Join-Path (Split-Path -Parent $Keytool) 'jarsigner.exe'
    if (!(Test-Path -LiteralPath $Jarsigner -PathType Leaf)) { throw 'Unity Android JDK의 jarsigner를 찾을 수 없습니다.' }
}

$Certificate = $null
try {
    $PreviousErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $Listing = @(& $Keytool '-J-Duser.language=en' '-J-Duser.country=US' -list -rfc `
            -keystore $env:DRAWLIAR_KEYSTORE_FILE '-storepass:env' DRAWLIAR_KEYSTORE_PASS -alias $env:DRAWLIAR_KEY_ALIAS 2>$null) -join "`n"
        $KeytoolExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $PreviousErrorPreference }
    if ($KeytoolExitCode -ne 0 -or $Listing -notmatch 'Entry type: PrivateKeyEntry') {
        throw 'Play 제출용 키의 비밀번호·별칭 또는 개인 키 항목을 확인하세요.'
    }
    $Match = [regex]::Match($Listing, '-----BEGIN CERTIFICATE-----\s*([A-Za-z0-9+/=\s]+?)\s*-----END CERTIFICATE-----')
    if (!$Match.Success) { throw 'Play 제출용 인증서를 확인할 수 없습니다.' }
    $Bytes = [Convert]::FromBase64String(($Match.Groups[1].Value -replace '\s', ''))
    $Certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($Bytes)
    $Name = $Certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if ($Name -ieq 'Android Debug') { throw 'Google Play 제출에는 Android Debug 인증서 대신 출시용 업로드 키가 필요합니다.' }
    $Now = [DateTime]::UtcNow
    if ($Certificate.NotBefore.ToUniversalTime() -gt $Now -or $Certificate.NotAfter.ToUniversalTime() -le $Now) {
        throw 'Play 제출용 서명 인증서의 유효기간을 확인하세요.'
    }
    Write-Output 'Play 제출용 업로드 키 확인 완료'
    if ($VerifyArtifact) {
        $PreviousErrorPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $Verification = @(& $Jarsigner '-J-Duser.language=en' '-J-Duser.country=US' -verify $ArtifactPath 2>&1) -join "`n"
            $VerificationExitCode = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $PreviousErrorPreference }
        if ($VerificationExitCode -ne 0 -or $Verification -notmatch '(?m)^\s*jar verified\.\s*$' -or
            $Verification -match '(?i)unsigned entr|jar is unsigned|treated as unsigned') {
            throw 'AAB 서명이 없거나 손상되었거나 서명되지 않은 항목이 포함되어 있습니다.'
        }
        $PreviousErrorPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $ArtifactCertificates = @(& $Keytool '-J-Duser.language=en' '-J-Duser.country=US' -printcert -rfc -jarfile $ArtifactPath 2>&1) -join "`n"
            $KeytoolExitCode = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $PreviousErrorPreference }
        $Signers = [regex]::Matches($ArtifactCertificates, '(?ms)^Signer #\d+:\s*(.*?)(?=^Signer #\d+:|\z)')
        if ($KeytoolExitCode -ne 0 -or $Signers.Count -eq 0) {
            throw 'AAB 서명 인증서를 확인할 수 없습니다.'
        }
        $ExpectedCertificate = [Convert]::ToBase64String($Bytes)
        foreach ($Signer in $Signers) {
            $SignerCertificate = [regex]::Match($Signer.Groups[1].Value,
                '-----BEGIN CERTIFICATE-----\s*([A-Za-z0-9+/=\s]+?)\s*-----END CERTIFICATE-----')
            if (!$SignerCertificate.Success) { throw 'AAB 서명 인증서를 확인할 수 없습니다.' }
            $SignedBytes = [Convert]::FromBase64String(($SignerCertificate.Groups[1].Value -replace '\s', ''))
            if ([Convert]::ToBase64String($SignedBytes) -cne $ExpectedCertificate) {
                throw 'AAB 서명 인증서가 설정된 Play 업로드 키와 일치하지 않습니다.'
            }
        }
        $Sha256 = [Security.Cryptography.SHA256]::Create()
        try { $Fingerprint = [BitConverter]::ToString($Sha256.ComputeHash($Bytes)).Replace('-', ':') }
        finally { $Sha256.Dispose() }
        Write-Output "Play 제출용 AAB 서명 확인 완료 SHA-256=$Fingerprint"
    }
}
finally {
    if ($null -ne $Certificate) { $Certificate.Dispose() }
    $Listing = $null
    $Bytes = $null
    $Verification = $null
    $ArtifactCertificates = $null
    $SignedBytes = $null
}
