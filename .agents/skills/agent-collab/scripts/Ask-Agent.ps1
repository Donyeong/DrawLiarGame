[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('claude', 'codex')][string]$To,
    [Parameter(Mandatory)][string]$Prompt
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$maxDepth = 2
$previousDepth = $env:DRAWLIAR_AGENT_DEPTH
$depth = [int]$previousDepth
if ($depth -ge $maxDepth) { throw "Agent call depth limit ($maxDepth) reached. Handle this part yourself." }
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $OutputEncoding
$env:DRAWLIAR_AGENT_DEPTH = $depth + 1
Push-Location -LiteralPath (Join-Path $PSScriptRoot '..\..\..\..')
try {
    # Windows PowerShell turns redirected native stderr (agent progress logs) into terminating errors under Stop.
    $ErrorActionPreference = 'Continue'
    if ($To -eq 'claude') {
        $Prompt | & (Join-Path $env:USERPROFILE '.local\bin\claude.exe') -p --permission-mode auto
    }
    else {
        # Codex cannot resolve the profile directory inside its own sandbox.
        if (!$env:CODEX_HOME) { $env:CODEX_HOME = Join-Path $env:USERPROFILE '.codex' }
        try { [IO.File]::Create((Join-Path $env:CODEX_HOME '.agent-collab-probe'), 1, [IO.FileOptions]::DeleteOnClose).Dispose() }
        catch { throw 'Codex cannot start inside the Codex sandbox (CODEX_HOME is read-only). Handle this part yourself.' }
        # The npm codex.cmd fails sandbox setup while the Codex desktop app holds its runtime files.
        $config = Get-Content -LiteralPath (Join-Path $env:CODEX_HOME 'config.toml') -Raw
        if ($config -notmatch "CODEX_CLI_PATH = '([^']+)'") { throw 'CODEX_CLI_PATH not found in Codex config.' }
        $Prompt | & $Matches[1] exec --approve-for-me -
    }
    $exitCode = $LASTEXITCODE
}
finally {
    $env:DRAWLIAR_AGENT_DEPTH = $previousDepth
    Pop-Location
}
exit $exitCode
