<#
.SYNOPSIS
    Moves one docs/current/TODO.md entry to the top of docs/current/CLOSED.md with an Evidence line.

.DESCRIPTION
    Finds the single TODO.md line that starts with -Prefix, removes it, and inserts it as a new
    "## <Title> - resolved (<Date>)" entry above the first heading-2 entry of CLOSED.md (newest first).
    The leading status tag ("- [OPEN] ") is dropped from the moved text. Each file keeps its own
    line endings and is written as UTF-8 without BOM. Fails without writing anything when the
    prefix matches zero or several lines.

.EXAMPLE
    .\scripts\Close-TodoItem.ps1 -Prefix '- [OPEN] MoveMember collateral' -Title 'MoveMember collateral tripwire' -Evidence 'f498d7f0; MoveMemberToolCollateralRefusalTests'
#>
param(
    [Parameter(Mandatory)][string]$Prefix,   # exact start of the TODO.md line
    [Parameter(Mandatory)][string]$Title,    # heading text for CLOSED.md, without the date
    [Parameter(Mandatory)][string]$Evidence, # commit hash(es), tests, or why it is stale
    [string]$Date = (Get-Date -Format 'yyyy-MM-dd')
)
$ErrorActionPreference = 'Stop'
$root = Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\current'
$enc = New-Object System.Text.UTF8Encoding($false)
$todoPath = Join-Path $root 'TODO.md'
$closedPath = Join-Path $root 'CLOSED.md'

$todo = [IO.File]::ReadAllText($todoPath)
$nl = if ($todo.Contains("`r`n")) { "`r`n" } else { "`n" }
$lines = $todo -split "`r?`n"
$hits = @($lines | Where-Object { $_.StartsWith($Prefix) })
if ($hits.Count -ne 1) { throw "Prefix matched $($hits.Count) lines in TODO.md (need exactly 1): $Prefix" }

$closed = [IO.File]::ReadAllText($closedPath)
$cnl = if ($closed.Contains("`r`n")) { "`r`n" } else { "`n" }
$idx = $closed.IndexOf("$cnl## ")
if ($idx -lt 0) { throw 'No "## " entry heading found in CLOSED.md' }
$idx += $cnl.Length

$body = $hits[0] -replace '^- \[[A-Z-]+\] ', ''
$entry = "## $Title - resolved ($Date)$cnl$cnl$body$cnl${cnl}Evidence: $Evidence$cnl$cnl"

$kept = $lines | Where-Object { -not $_.StartsWith($Prefix) }
[IO.File]::WriteAllText($todoPath, ($kept -join $nl), $enc)
[IO.File]::WriteAllText($closedPath, $closed.Insert($idx, $entry), $enc)
"Closed: $Title"
