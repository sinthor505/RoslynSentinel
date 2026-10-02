# Tests for usage-statusline.ps1 and usage-nudge.ps1.
# Run: pwsh -NoProfile -File .claude/hooks/usage-hooks.Tests.ps1   (exit 1 on any failure)
# Uses a throwaway ROSLYNSENTINEL_USAGE_DIR; never touches the real .claude/usage.

$ErrorActionPreference = 'Stop'
$statusline = Join-Path $PSScriptRoot 'usage-statusline.ps1'
$nudge      = Join-Path $PSScriptRoot 'usage-nudge.ps1'
$failures = 0

function Assert([bool]$cond, [string]$name) {
    if ($cond) { Write-Host "PASS $name" } else { Write-Host "FAIL $name"; $script:failures++ }
}

function Invoke-Hook([string]$script, [string]$json) {
    ($json | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script) -join "`n"
}

$dir = Join-Path ([IO.Path]::GetTempPath()) ("usage-tests-" + [guid]::NewGuid().ToString('N'))
$env:ROSLYNSENTINEL_USAGE_DIR = $dir
try {
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $future = $now + 3600
    $past = $now - 60

    # --- statusline ---
    $out = Invoke-Hook $statusline '{}'
    Assert ($out -eq 'claude') 'statusline: empty payload prints fallback'
    Assert (-not (Test-Path $dir)) 'statusline: no rate_limits writes no file'

    $out = Invoke-Hook $statusline '{not json'
    Assert ($out -eq 'claude') 'statusline: bad json prints fallback'

    $in = '{"session_id":"abcdef123456","model":{"display_name":"Opus"},"context_window":{"used_percentage":12.4},"rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":' + $future + '},"seven_day":{"used_percentage":41.2,"resets_at":' + ($now + 86400) + '}}}'
    $out = Invoke-Hook $statusline $in
    Assert ($out -match '^\[Opus\] \| ctx 12% \| 5h 24% \(resets \d\d:\d\d\) \| 7d 41%') 'statusline: formats line'
    $snap = Get-Content (Join-Path $dir 'usage.json') -Raw | ConvertFrom-Json
    Assert ($snap.five_hour.used_percentage -eq 23.5 -and $snap.five_hour.resets_at -eq $future) 'statusline: writes five_hour snapshot'

    # --- nudge ---
    function Write-Snap([double]$pct, [long]$resets) {
        $s = @{ updated = $now; five_hour = @{ used_percentage = $pct; resets_at = $resets } }
        [IO.File]::WriteAllText((Join-Path $dir 'usage.json'), ($s | ConvertTo-Json -Depth 4))
    }
    Remove-Item (Join-Path $dir '*.nudged'), (Join-Path $dir '*.tiptoe') -ErrorAction SilentlyContinue
    $prompt = '{"session_id":"abcdef123456","hook_event_name":"UserPromptSubmit"}'
    $post   = '{"session_id":"abcdef123456","hook_event_name":"PostToolUse"}'

    Write-Snap 50 $future
    Assert ((Invoke-Hook $nudge $post) -eq '') 'nudge: below threshold is silent'

    Write-Snap 85 $future
    $out = Invoke-Hook $nudge $post
    $j = $out | ConvertFrom-Json
    Assert ($j.hookSpecificOutput.hookEventName -eq 'PostToolUse') 'nudge: 85% emits PostToolUse context'
    Assert ($j.hookSpecificOutput.additionalContext -match 'wrapup' -and $j.hookSpecificOutput.additionalContext -match '85%') 'nudge: 85% asks for wrapup'
    Assert ((Invoke-Hook $nudge $post) -eq '') 'nudge: fires once per tier'

    Write-Snap 96 $future
    $out = Invoke-Hook $nudge $prompt
    Assert (($out | ConvertFrom-Json).hookSpecificOutput.hookEventName -eq 'UserPromptSubmit') 'nudge: 95% tier fires separately, echoes UserPromptSubmit'

    Write-Snap 99 $past
    Assert ((Invoke-Hook $nudge $post) -eq '') 'nudge: reset window is ignored'

    Remove-Item (Join-Path $dir '*.nudged')
    Write-Snap 90 $future
    New-Item (Join-Path $dir 'abcdef12.tiptoe') -ItemType File | Out-Null
    $ctx = ((Invoke-Hook $nudge $post) | ConvertFrom-Json).hookSpecificOutput.additionalContext
    Assert ($ctx -match 'Tiptoe mode' -and $ctx -notmatch 'run the wrapup') 'nudge: tiptoe flag switches wording'

    Assert ((Invoke-Hook $nudge '') -eq '') 'nudge: empty stdin is silent'
    Assert ((Invoke-Hook $nudge '{garbage') -eq '') 'nudge: bad json is silent'
}
finally {
    Remove-Item Env:\ROSLYNSENTINEL_USAGE_DIR -ErrorAction SilentlyContinue
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

if ($failures -gt 0) { Write-Host "$failures failure(s)"; exit 1 }
Write-Host 'all passed'
