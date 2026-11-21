# SessionStart hook: on startup or clear, run scripts/Test-RepoHygiene.ps1 (read-only) and inject
# a short warning with its findings. Silent when the repo is clean. Resume and compact do not
# run it, since they fire repeatedly.
# Plan: docs/current/plans/plan_scratch_root_and_repo_hygiene.md (Step 15b)
#
# Contract: SessionStart stdout is added to the model's context. FAIL-OPEN: any error prints
# nothing and exits 0.
#
# Tests: manual check, e.g.
#   echo '{"source":"startup","session_id":"x"}' | pwsh -NoProfile -File .claude/hooks/repo-hygiene-session-start.ps1

$ErrorActionPreference = 'Stop'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    try { $payload = $raw | ConvertFrom-Json } catch { exit 0 }
    if ([string]$payload.source -notin @('startup', 'clear')) { exit 0 }

    $repo = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
    $script = Join-Path $repo 'scripts\Test-RepoHygiene.ps1'
    if (-not (Test-Path -LiteralPath $script)) { exit 0 }

    $shell = $null
    if (Get-Command pwsh -ErrorAction SilentlyContinue) { $shell = 'pwsh' }
    elseif (Get-Command powershell.exe -ErrorAction SilentlyContinue) { $shell = 'powershell.exe' }
    if (-not $shell) { exit 0 }

    # Native stderr must not turn into a terminating error under Stop; the hook only wants stdout.
    $ErrorActionPreference = 'Continue'
    $lines = @(& $shell -NoProfile -File $script -Quiet -RepoRoot $repo 2>$null |
        ForEach-Object { [string]$_ } | Where-Object { $_ -and $_.Trim() })
    if ($lines.Count -eq 0) { exit 0 }

    $count = @($lines | Where-Object { $_ -match '^(UNEXPECTED-ROOT|UNTRACKED|IGNORED-DATA)' }).Count
    $out = @("Repo hygiene (scripts/Test-RepoHygiene.ps1): $count finding(s); untracked files may belong to a peer session sharing this tree. Run the script for the full list.")
    $out += ($lines | Select-Object -First 13)
    if ($lines.Count -gt 13) { $out += "... $($lines.Count - 13) more" }
    [Console]::Out.WriteLine(($out -join "`n"))
    exit 0
}
catch { exit 0 }
