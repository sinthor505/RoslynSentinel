<#
.SYNOPSIS
  Measures the per-tool schema cost of a server mode by starting the built exe over stdio and calling tools/list.

.DESCRIPTION
  Prints each emitted tool's serialized JSON size (chars) and an estimated token count, largest first,
  plus the total. Optionally lists the largest parameters of the biggest tools and the shared parameters
  repeated across tools. Uses the existing Debug build; run Build(fullBuild) first so the numbers reflect
  current source. Writes the raw tools/list response to -OutFile when given.

.PARAMETER Mode
  Server mode (default claude-lean).

.PARAMETER CharsPerToken
  Calibration. 2.57 reproduces the client-reported Git figure (7205 chars = ~2800 tokens).

.PARAMETER TopParams
  For the N largest tools, list their N largest parameters (0 disables).

.PARAMETER OutFile
  Save the raw tools/list JSON here.

.EXAMPLE
  .\scripts\Measure-ToolSchemaCost.ps1 -TopParams 5
#>
[CmdletBinding()]
param(
    [string]$Mode = 'claude-lean',
    [string]$Configuration = 'Debug',
    [double]$CharsPerToken = 2.57,
    [int]$TopParams = 0,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repoRoot "RoslynSentinel.Server.Advanced\bin\$Configuration\net10.0\RoslynSentinel.Server.Advanced.exe"
if (-not (Test-Path $exe)) { throw "Not built: $exe (run Build first)" }

$psi = New-Object Diagnostics.ProcessStartInfo $exe
$psi.Arguments = "--transport=stdio --mode=$Mode"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.WorkingDirectory = [IO.Path]::GetTempPath()

$p = [Diagnostics.Process]::Start($psi)
try {
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"measure","version":"1"}}}')
    $null = $p.StandardOutput.ReadLine()
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
    $line = $p.StandardOutput.ReadLine()
}
finally {
    if (-not $p.HasExited) { $p.Kill() }
}

if ($OutFile) { $line | Set-Content $OutFile -Encoding utf8 }
$tools = ($line | ConvertFrom-Json).result.tools

$rows = $tools | ForEach-Object {
    [pscustomobject]@{
        Name   = $_.name
        Chars  = ($_ | ConvertTo-Json -Depth 30 -Compress).Length
        Params = @($_.inputSchema.properties.PSObject.Properties).Count
        Tool   = $_
    }
} | Sort-Object Chars -Descending

$total = ($rows | Measure-Object Chars -Sum).Sum
$rows | ForEach-Object { '{0,-26} {1,6} ch  ~{2,5} tok  {3,3} params' -f $_.Name, $_.Chars, [int]($_.Chars / $CharsPerToken), $_.Params }
'{0,-26} {1,6} ch  ~{2,5} tok  ({3} tools, {4}/{5})' -f 'TOTAL', $total, [int]($total / $CharsPerToken), $rows.Count, 'Advanced', $Mode

if ($TopParams -gt 0) {
    foreach ($r in ($rows | Select-Object -First $TopParams)) {
        "--- $($r.Name): largest params"
        $r.Tool.inputSchema.properties.PSObject.Properties |
            ForEach-Object { [pscustomobject]@{ P = $_.Name; Chars = ($_.Value | ConvertTo-Json -Depth 20 -Compress).Length } } |
            Sort-Object Chars -Descending | Select-Object -First $TopParams |
            ForEach-Object { '  {0,-26} {1,5}' -f $_.P, $_.Chars }
    }
    '--- shared params (name: tools, total chars)'
    $rows | ForEach-Object { $t = $_.Tool; $t.inputSchema.properties.PSObject.Properties | ForEach-Object { [pscustomobject]@{ P = $_.Name; Chars = ($_.Value | ConvertTo-Json -Depth 20 -Compress).Length + $_.Name.Length + 4 } } } |
        Group-Object P | Where-Object Count -gt 1 |
        ForEach-Object { [pscustomobject]@{ P = $_.Name; N = $_.Count; Chars = ($_.Group | Measure-Object Chars -Sum).Sum } } |
        Sort-Object Chars -Descending | Select-Object -First 12 |
        ForEach-Object { '  {0,-22} in {1,2} tools, {2,5} ch' -f $_.P, $_.N, $_.Chars }
}
