# Publishes the agent-identity hook helper (native AOT, win-x64) to .claude/hooks/bin.
# Run from any directory; the repo root is resolved from this script's location.
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'RoslynSentinel.Utilities.AgentIdentityHook\RoslynSentinel.Utilities.AgentIdentityHook.csproj'
$outDir = Join-Path $repoRoot '.claude\hooks\bin'

# vcvarsall calls vswhere through PATH. When it is missing, its error text ends up in the linker
# path the ILCompiler targets capture, and the link step fails with MSB3073 exit code 123.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path (Join-Path $vsInstaller 'vswhere.exe')) -and -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $env:PATH = "$vsInstaller;$env:PATH"
}

dotnet publish $project -c Release -r win-x64 -o $outDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host (Join-Path $outDir 'agent-identity-hook.exe')
