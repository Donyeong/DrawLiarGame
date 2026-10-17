[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BaseVersion,
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$BuildNumber,
    [string]$StatePath = 'C:\DrawLiarJenkinsCache\AndroidBundleVersion.json'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function ConvertTo-BundleVersion([string]$Value) {
    $parsed = $null
    if ($Value -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        ![Version]::TryParse($Value, [ref]$parsed)) {
        throw '표시 버전 자동 증가에는 0.1.0 형식의 세 자리 숫자 버전이 필요합니다.'
    }
    return $parsed
}

$base = ConvertTo-BundleVersion $BaseVersion
$stateFile = [IO.Path]::GetFullPath($StatePath)
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $stateFile))
$lockFile = $stateFile + '.lock'
$lock = $null
$temporary = $null
try {
    try { $lock = [IO.File]::Open($lockFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None) }
    catch { throw '다른 실행이 Android 표시 버전을 예약 중이거나 상태 경로에 접근할 수 없습니다.' }
    $previous = $null
    if (Test-Path -LiteralPath $stateFile -PathType Leaf) {
        $previous = [IO.File]::ReadAllText($stateFile) | ConvertFrom-Json
        if ($previous.PackageName -cne 'com.rascallab.liargame' -or
            [string]$previous.BuildNumber -cnotmatch '^[1-9][0-9]*$') {
            throw 'Android 표시 버전 예약 상태가 잘못되었습니다.'
        }
        $previousVersion = ConvertTo-BundleVersion $previous.BundleVersion
        [void](ConvertTo-BundleVersion $previous.BaseVersion)
        if ([string]$previous.BuildNumber -ceq [string]$BuildNumber) {
            if ($previous.BaseVersion -cne $BaseVersion) { throw '같은 빌드의 기준 표시 버전이 변경됐습니다.' }
            $previous | ConvertTo-Json
            return
        }
        if ($previousVersion -gt $base) { $base = $previousVersion }
    }
    if ($base.Build -eq [int]::MaxValue) { throw '표시 버전 마지막 숫자의 최대값을 초과했습니다.' }
    $reservation = [ordered]@{
        PackageName = 'com.rascallab.liargame'
        BaseVersion = $BaseVersion
        BundleVersion = '{0}.{1}.{2}' -f $base.Major, $base.Minor, ($base.Build + 1)
        BuildNumber = $BuildNumber
        ReservedUtc = [DateTime]::UtcNow.ToString('o')
    }
    $json = $reservation | ConvertTo-Json
    $temporary = $stateFile + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $stateFile -PathType Leaf) { [IO.File]::Replace($temporary, $stateFile, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary, $stateFile) }
    Write-Output $json
}
finally {
    if ($temporary -and (Test-Path -LiteralPath $temporary -PathType Leaf)) { Remove-Item -LiteralPath $temporary -Force }
    if ($null -ne $lock) {
        $lock.Dispose()
        Remove-Item -LiteralPath $lockFile -Force
    }
}
