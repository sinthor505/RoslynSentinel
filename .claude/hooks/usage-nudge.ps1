# UserPromptSubmit + PostToolUse hook: when a claude.ai usage-limit window crosses a threshold,
# inject a one-time notice into the model's context. Reads the snapshot usage-statusline.ps1
# wrote to .claude/usage/usage.json. PostToolUse is what makes this work in long autonomous
# runs where no user prompt ever arrives.
#
# Tiers (percent used, per window): 85 = "wrap up", 95 = "last chance". Each (session, window
# reset time, tier) fires once, tracked in .claude/usage/<sid8>.nudged. Tune with
# ROSLYNSENTINEL_USAGE_NUDGE_PCT (default 85) and ROSLYNSENTINEL_USAGE_URGENT_PCT (default 95).
#
# The snapshot comes from usage-statusline.ps1 (CLI) and/or usage-refresh.ps1 (OAuth usage endpoint,
# works in the VS Code extension too); the nudge refreshes it itself when older than ~90 s.
#
# Tiptoe mode: if .claude/usage/<sid8>.tiptoe exists (created by the tiptoe skill), the
# notices say "keep going with interruptible steps" instead of "run wrapup".
#
# A snapshot whose window has already reset (resets_at <= now) is ignored. Usage only grows
# inside a window, so a stale snapshot under-reports and can only delay a nudge, never fake one.
#
# FAIL-OPEN: any error exits 0 silently. Tests: pwsh -NoProfile -File .claude/hooks/usage-hooks.Tests.ps1

$ErrorActionPreference = 'Stop'

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    $payload = $raw | ConvertFrom-Json

    $dir = if ($env:ROSLYNSENTINEL_USAGE_DIR) { $env:ROSLYNSENTINEL_USAGE_DIR } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'usage' }
    $snapFile = Join-Path $dir 'usage.json'
    # Refresh usage.json from the OAuth usage endpoint when it is older than ~90 s (throttled and
    # backed off inside; see usage-refresh.ps1). Needed in the VS Code extension, which never runs
    # the statusLine command that otherwise writes the snapshot.
    . (Join-Path $PSScriptRoot 'usage-refresh.ps1')
    [void](Update-UsageSnapshot $dir)
    $snap = if (Test-Path -LiteralPath $snapFile) { Get-Content -LiteralPath $snapFile -Raw | ConvertFrom-Json } else { $null }

    $nudgePct = 85; $urgentPct = 95
    if ($env:ROSLYNSENTINEL_USAGE_NUDGE_PCT  -match '^\d+$') { $nudgePct  = [int]$env:ROSLYNSENTINEL_USAGE_NUDGE_PCT }
    if ($env:ROSLYNSENTINEL_USAGE_URGENT_PCT -match '^\d+$') { $urgentPct = [int]$env:ROSLYNSENTINEL_USAGE_URGENT_PCT }

    $sid8 = ([string]$payload.session_id) -replace '[^A-Za-z0-9]', ''
    if ($sid8.Length -gt 8) { $sid8 = $sid8.Substring(0, 8) }
    if (-not $sid8) { exit 0 }
    $nudged  = Join-Path $dir "$sid8.nudged"
    $tiptoeFlag = Join-Path $dir "$sid8.tiptoe"
    $tiptoeing = Test-Path -LiteralPath $tiptoeFlag
    $done = if (Test-Path -LiteralPath $nudged) { @(Get-Content -LiteralPath $nudged) } else { @() }

    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $messages = @()

    # Stale-data notice. usage.json is written by the statusLine command (CLI only) and by the OAuth refresh
    # above. If both fail (no credentials, offline, endpoint changed or 429-blocked) the snapshot goes stale
    # and every limit warning below silently never fires. Say so once per session instead of letting silence
    # read as "safe".
    $staleAfterSeconds = 1800
    if ($env:ROSLYNSENTINEL_USAGE_STALE_SECONDS -match '^\d+$') { $staleAfterSeconds = [int]$env:ROSLYNSENTINEL_USAGE_STALE_SECONDS }
    $updated = if ($null -ne $snap -and $null -ne $snap.updated) { [long]$snap.updated } else { 0 }
    if ($null -eq $snap -or ($now - $updated) -gt $staleAfterSeconds) {
        $staleKey = 'stale-usage-data'
        if ($done -notcontains $staleKey) {
            $done += $staleKey
            $age = if ($updated -gt 0) { 'last refreshed {0:0.#} h ago' -f (($now - $updated) / 3600.0) } else { 'never refreshed' }
            $messages += "Usage-limit warnings are INACTIVE in this session: the usage snapshot is stale ($age). Neither the CLI status line nor the live fetch from the OAuth usage endpoint (usage-refresh.ps1) has updated it, so 85%/95% notices cannot fire. Do not treat silence as headroom: commit small steps often, keep the progress log current, and ask the user for the current 5-hour percentage if a long autonomous run is planned."
        }
    }

    foreach ($pair in @(@('five_hour', '5-hour session'), @('seven_day', 'weekly'))) {
        if ($null -eq $snap) { break }
        $w = $snap.($pair[0])
        if ($null -eq $w -or $null -eq $w.used_percentage -or $null -eq $w.resets_at) { continue }
        $resets = [long]$w.resets_at
        if ($resets -le $now) { continue }
        $pct = [double]$w.used_percentage
        $tier = if ($pct -ge $urgentPct) { $urgentPct } elseif ($pct -ge $nudgePct) { $nudgePct } else { continue }
        $key = '{0}:{1}:{2}' -f $pair[0], $resets, $tier
        if ($done -contains $key) { continue }
        $done += $key

        $at = [DateTimeOffset]::FromUnixTimeSeconds($resets).ToLocalTime()
        $mins = [int][math]::Ceiling(($resets - $now) / 60.0)
        $eta = if ($mins -ge 60) { '{0}h{1:00}m' -f [math]::Floor($mins / 60), ($mins % 60) } else { "${mins}m" }
        $head = 'Usage limit: the {0} limit is {1:0}% used and resets at {2:HH:mm} local (in {3}).' -f $pair[1], $pct, $at, $eta

        if ($tiptoeing) {
            if ($tier -ge $urgentPct) { $tail = 'Tiptoe mode: from now on take only steps that are safe to interrupt mid-way, and update the progress log after each one.' }
            else { $tail = 'Tiptoe mode: continue, but every step must leave the tree compiling and the progress log current; the limit may cut the session off at any point.' }
        }
        elseif ($tier -ge $urgentPct) { $tail = 'Stop new work now. If you have not already, run the wrapup skill immediately (build, commit session files, write the handoff log) and keep it minimal.' }
        else { $tail = 'Do not start any new multi-step change. Finish what is in flight and run the wrapup skill (it commits this session''s files and writes a handoff log) before the limit hits. If the user has told you to keep going until the limit, use the tiptoe skill instead.' }
        $messages += "$head $tail"
    }

    if ($messages.Count -eq 0) { exit 0 }

    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllLines($nudged, [string[]]$done)

    $event = if ($payload.hook_event_name) { [string]$payload.hook_event_name } else { 'PostToolUse' }
    $out = @{ hookSpecificOutput = @{ hookEventName = $event; additionalContext = ($messages -join ' ') } }
    [Console]::Out.WriteLine(($out | ConvertTo-Json -Compress -Depth 4))
    exit 0
}
catch { exit 0 }
