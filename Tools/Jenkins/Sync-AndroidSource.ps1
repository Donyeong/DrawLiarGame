[CmdletBinding()]
param(
    [string]$SourceRepository = 'C:\DrawLiar',
    [string]$Workspace = 'C:\DrawLiarJenkins'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = [IO.Path]::GetFullPath($SourceRepository).TrimEnd('\')
$destination = [IO.Path]::GetFullPath($Workspace).TrimEnd('\')
if ($destination -ine 'C:\DrawLiarJenkins' -or $source -ieq $destination -or
    $source.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Android snapshot destination must be the isolated C:\DrawLiarJenkins workspace.'
}
if (!(Test-Path -LiteralPath (Join-Path $source 'DrawLiar/ProjectSettings/ProjectVersion.txt'))) {
    throw 'Source Unity project not found.'
}
[void][IO.Directory]::CreateDirectory($destination)
foreach ($root in @($source, $destination)) {
    if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Snapshot roots must not be junctions or symbolic links.'
    }
}
$privateDirectory = Join-Path $destination '.jenkins-private'
if (Test-Path -LiteralPath $privateDirectory) {
    if ((Get-Item -LiteralPath $privateDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Private download directory must not be a junction or symbolic link.'
    }
    $previousDownload = Join-Path $privateDirectory 'FirebaseDownload.json'
    if (Test-Path -LiteralPath $previousDownload -PathType Leaf) {
        Remove-Item -LiteralPath $previousDownload -Force
    }
}
$unityLock = Join-Path $destination 'DrawLiar/Temp/UnityLockfile'
if (Test-Path -LiteralPath $unityLock) {
    try {
        $lockProbe = [IO.File]::Open($unityLock, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $lockProbe.Dispose()
    }
    catch { throw 'The Android CI Unity project is in use or its lock cannot be inspected.' }
}
# Mirror only these source directories; preserve the CI Library and Gradle caches.
$directories = @('DrawLiar/Assets', 'DrawLiar/Packages', 'DrawLiar/ProjectSettings', 'Tools/Jenkins', 'Tools/Firebase')
foreach ($relative in $directories) {
    $from = [IO.Path]::GetFullPath((Join-Path $source $relative))
    $to = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (!$to.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase) -or
        !$from.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Snapshot path is outside its allowed root.'
    }
    if (!(Test-Path -LiteralPath $from -PathType Container)) { throw "Source directory not found: $relative" }
    [void][IO.Directory]::CreateDirectory($to)
    $reparsePoints = @(Get-ChildItem -LiteralPath $from -Recurse -Force -Attributes ReparsePoint)
    $targetReparsePoints = @(Get-ChildItem -LiteralPath $to -Recurse -Force -Attributes ReparsePoint)
    if ($reparsePoints.Count -gt 0 -or $targetReparsePoints.Count -gt 0 -or
        ((Get-Item -LiteralPath $from).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ((Get-Item -LiteralPath $to).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Snapshot paths must not contain junctions or symbolic links.'
    }
    & robocopy.exe $from $to /MIR /R:2 /W:1 /XJ /XD node_modules /NJH /NJS /NFL /NDL /NP
    if ($LASTEXITCODE -ge 8) { throw "Source synchronization failed: $relative" }
}
$buildDirectory = Join-Path $destination 'Build'
if (Test-Path -LiteralPath $buildDirectory) {
    $resolved = (Resolve-Path -LiteralPath $buildDirectory).Path
    if ($resolved -ine ($destination + '\Build') -or
        ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        @(Get-ChildItem -LiteralPath $resolved -Recurse -Force -Attributes ReparsePoint).Count -gt 0) {
        throw 'Build cleanup target is outside the isolated workspace.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
$logs = Join-Path $destination 'Build/Logs'
[void][IO.Directory]::CreateDirectory($logs)
$revisionLines = @(& git -C $source rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $revisionLines.Count -ne 1 -or $revisionLines[0] -notmatch '^[0-9a-f]{40}$') {
    throw 'Cannot identify source Git revision.'
}
$revision = $revisionLines[0].Trim()
$workingTreeStatus = @(& git -C $source status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source working tree state.' }
$dirty = $workingTreeStatus.Count -gt 0
$sha = [Security.Cryptography.SHA256]::Create()
$listing = [Text.StringBuilder]::new()
foreach ($relative in $directories) {
    $from = Join-Path $source $relative
    $to = Join-Path $destination $relative
    foreach ($item in (Get-ChildItem -LiteralPath $from -Recurse -File | Where-Object { $_.FullName -notmatch '\\node_modules\\' } | Sort-Object FullName)) {
        $suffix = $item.FullName.Substring($from.Length).TrimStart('\')
        $copied = Join-Path $to $suffix
        $sourceHash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        if (!(Test-Path -LiteralPath $copied -PathType Leaf) -or
            (Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash -ne $sourceHash) {
            throw 'Source changed during the snapshot. Retry after saving the project.'
        }
        [void]$listing.AppendLine($relative + '/' + $suffix.Replace('\', '/') + ' ' + $sourceHash)
    }
}
try {
    $fingerprint = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($listing.ToString()))).Replace('-', '').ToLowerInvariant()
}
finally { $sha.Dispose() }
$metadata = [ordered]@{
    SourceMode = 'LocalWorkingTree'; SourceRepository = $source; Revision = $revision
    UncommittedChanges = $dirty; SnapshotSha256 = $fingerprint; CapturedUtc = [DateTime]::UtcNow.ToString('O')
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logs 'AndroidSource.json') -Encoding UTF8
Write-Host "DRAWLIAR_SOURCE_READY revision=$revision dirty=$dirty sha256=$fingerprint"
