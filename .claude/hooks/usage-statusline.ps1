# statusLine command: prints a one-line status AND records the claude.ai usage-limit windows to
# .claude/usage/usage.json so hooks (which never receive rate_limits themselves) can read them.
# Consumer: usage-nudge.ps1. Setup: "statusLine" in .claude/settings.json.
#
# Input (stdin JSON, subset): model.display_name, context_window.used_percentage,
#   rate_limits.five_hour / seven_day { used_percentage (0-100), resets_at (epoch seconds) }.
# rate_limits only exists for Pro/Max subscribers and only after the first API response, and a
# window disappears once its resets_at has passed - every field here is optional.
#
# The limits are per account, not per session, so one shared usage.json (last writer wins) is
# correct. .claude/usage/ is local-only (excluded by the .claude/* rule in .gitignore).
# Tests point it elsewhere with ROSLYNSENTINEL_USAGE_DIR.
#
# FAIL-OPEN: any error still prints a line and exits 0; a status line must never break the UI.

$ErrorActionPreference = 'Stop'
$line = 'claude'

function Format-Window($w, [string]$label) {
    if ($null -eq $w -or $null -eq $w.used_percentage) { return $null }
    $s = '{0} {1:0}%' -f $label, [double]$w.used_percentage
    if ($null -ne $w.resets_at) {
        $t = [DateTimeOffset]::FromUnixTimeSeconds([long]$w.resets_at).ToLocalTime()
        $s += ' (resets {0:HH:mm})' -f $t
    }
    $s
}

try {
    $raw = [Console]::In.ReadToEnd()
    $p = $raw | ConvertFrom-Json

    $parts = @()
    if ($p.model.display_name) { $parts += "[$($p.model.display_name)]" }
    if ($null -ne $p.context_window.used_percentage) { $parts += ('ctx {0:0}%' -f [double]$p.context_window.used_percentage) }
    $five  = Format-Window $p.rate_limits.five_hour '5h'
    $seven = Format-Window $p.rate_limits.seven_day '7d'
    if ($five)  { $parts += $five }
    if ($seven) { $parts += $seven }
    if ($parts.Count -gt 0) { $line = $parts -join ' | ' }

    if ($five -or $seven) {
        $dir = if ($env:ROSLYNSENTINEL_USAGE_DIR) { $env:ROSLYNSENTINEL_USAGE_DIR } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'usage' }
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $snap = [ordered]@{
            updated    = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            session_id = [string]$p.session_id
        }
        if ($five)  { $snap['five_hour'] = [ordered]@{ used_percentage = [double]$p.rate_limits.five_hour.used_percentage; resets_at = [long]$p.rate_limits.five_hour.resets_at } }
        if ($seven) { $snap['seven_day'] = [ordered]@{ used_percentage = [double]$p.rate_limits.seven_day.used_percentage; resets_at = [long]$p.rate_limits.seven_day.resets_at } }
        $file = Join-Path $dir 'usage.json'
        $tmp  = "$file.$PID.tmp"
        [System.IO.File]::WriteAllText($tmp, ($snap | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $tmp -Destination $file -Force
    }
}
catch { }

[Console]::Out.WriteLine($line)
exit 0
