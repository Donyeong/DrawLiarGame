param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '../DrawLiar'),
    [string]$UnityDataPath,
    [switch]$IncludeEditor
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $ProjectPath).Path
$projectFile = Join-Path $projectRoot 'Assembly-CSharp.csproj'
if (!(Test-Path -LiteralPath $projectFile)) {
    throw 'Generate the Unity C# project files before running this compiler check.'
}
$projectXml = [xml](Get-Content -Raw -LiteralPath $projectFile)
$references = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$sources = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($hint in $projectXml.Project.ItemGroup.Reference.HintPath) {
    if (!$hint) { continue }
    $referencePath = if ([IO.Path]::IsPathRooted($hint)) { $hint } else { Join-Path $projectRoot $hint }
    if (Test-Path -LiteralPath $referencePath) { [void]$references.Add([IO.Path]::GetFullPath($referencePath)) }
    if (!$UnityDataPath -and $hint -match '^(.*[\\/]Data)[\\/]Managed[\\/]UnityEngine[\\/]UnityEngine.CoreModule.dll$') {
        $UnityDataPath = $Matches[1]
    }
}
foreach ($assembly in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Library/ScriptAssemblies') -Filter '*.dll') {
    if ($assembly.Name -in @('Assembly-CSharp.dll', 'Assembly-CSharp-Editor.dll', 'GameCompileCheck.dll')) { continue }
    [void]$references.Add($assembly.FullName)
}
foreach ($compile in $projectXml.Project.ItemGroup.Compile.Include) {
    if (!$compile) { continue }
    $sourcePath = Join-Path $projectRoot $compile
    if (Test-Path -LiteralPath $sourcePath) { [void]$sources.Add([IO.Path]::GetFullPath($sourcePath)) }
}
foreach ($source in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Assets/DrawLiar') -Recurse -Filter '*.cs') {
    if (!$IncludeEditor -and $source.FullName -match '[\\/]Editor[\\/]') { continue }
    [void]$sources.Add($source.FullName)
}
if (!$UnityDataPath) { throw 'Pass -UnityDataPath pointing to the Unity Editor/Data folder.' }
$dotnetPath = Join-Path $UnityDataPath 'NetCoreRuntime/dotnet.exe'
$compilerPath = Join-Path $UnityDataPath 'DotNetSdkRoslyn/csc.dll'
$outputPath = Join-Path $projectRoot 'Temp/GameCompileCheck'
[void][IO.Directory]::CreateDirectory($outputPath)
$responsePath = Join-Path $outputPath 'compile.rsp'
$defines = @($projectXml.Project.PropertyGroup.DefineConstants | Where-Object { $_ })[-1]
$arguments = @('/nologo', '/target:library', '/langversion:9', '/nostdlib', '/unsafe',
    ('/define:' + $defines), ('/out:"' + (Join-Path $outputPath 'GameCompileCheck.dll') + '"'))
$arguments += @($references | Sort-Object | ForEach-Object { '/reference:"' + $_ + '"' })
$arguments += @($sources | Sort-Object | ForEach-Object { '"' + $_ + '"' })
[IO.File]::WriteAllLines($responsePath, $arguments)
Write-Host "Checking $($sources.Count) source files against $($references.Count) imported Unity assemblies."
& $dotnetPath $compilerPath "@$responsePath"
$compileExitCode = $LASTEXITCODE
if ($compileExitCode -eq 0) { Write-Host 'C# compilation passed. Unity import, Mirror weaving, and runtime validation are still required.' }
exit $compileExitCode
