# Tests for usage-statusline.ps1, usage-nudge.ps1 and context-size.ps1.
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
# Never let the nudge's OAuth refresh reach the real credentials/endpoint: no credentials file = no refresh.
$env:ROSLYNSENTINEL_USAGE_CREDENTIALS = Join-Path $dir 'no-such-credentials.json'
$listener = $null; $psListener = $null
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

    # --- subagent wording ---
    Remove-Item (Join-Path $dir '*.nudged'), (Join-Path $dir '*.tiptoe') -ErrorAction SilentlyContinue
    Write-Snap 90 $future
    $sub1 = '{"session_id":"abcdef123456","hook_event_name":"PostToolUse","agent_id":"a1","agent_type":"implementer"}'
    $sub2 = '{"session_id":"abcdef123456","hook_event_name":"PostToolUse","agent_id":"a2","agent_type":"implementer"}'
    $ctx = ((Invoke-Hook $nudge $sub1) | ConvertFrom-Json).hookSpecificOutput.additionalContext
    Assert ($ctx -match 'subagent' -and $ctx -match 'do NOT commit' -and $ctx -notmatch 'Do not start any new multi-step change') 'nudge: subagent gets its own wording'
    Assert ((Invoke-Hook $nudge $sub1) -eq '') 'nudge: subagent notice fires once per agent'
    Assert ((Invoke-Hook $nudge $sub2) -ne '') 'nudge: a second subagent still gets its notice'
    $ctx = ((Invoke-Hook $nudge $post) | ConvertFrom-Json).hookSpecificOutput.additionalContext
    Assert ($ctx -match 'run the wrapup skill') 'nudge: subagent notices do not consume the main conversation notice'

    # --- stale snapshot notice ---
    Remove-Item (Join-Path $dir '*.nudged') -ErrorAction SilentlyContinue
    $oldSnap = @{ updated = ($now - 7200); five_hour = @{ used_percentage = 10; resets_at = $future } }
    [IO.File]::WriteAllText((Join-Path $dir 'usage.json'), ($oldSnap | ConvertTo-Json -Depth 4))
    $ctx = ((Invoke-Hook $nudge $post) | ConvertFrom-Json).hookSpecificOutput.additionalContext
    Assert ($ctx -match 'INACTIVE' -and $ctx -match 'stale') 'nudge: stale snapshot says warnings are inactive'
    Assert ((Invoke-Hook $nudge $post) -eq '') 'nudge: stale notice fires once per session'

    Remove-Item (Join-Path $dir '*.nudged') -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $dir 'usage.json')
    $ctx = ((Invoke-Hook $nudge $post) | ConvertFrom-Json).hookSpecificOutput.additionalContext
    Assert ($ctx -match 'INACTIVE' -and $ctx -match 'never refreshed') 'nudge: missing snapshot says warnings are inactive'

    Remove-Item (Join-Path $dir '*.nudged') -ErrorAction SilentlyContinue
    Write-Snap 50 $future
    Assert ((Invoke-Hook $nudge $post) -eq '') 'nudge: fresh snapshot below threshold stays silent'

    # --- context-size notice (context-size.ps1) ---
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $settingsFile = Join-Path $dir 'claude-settings.json'
    [IO.File]::WriteAllText($settingsFile, '{"autoCompactWindow":250000}')   # compaction point 217000: nudge 185000, urgent 205000
    $env:ROSLYNSENTINEL_CLAUDE_SETTINGS = $settingsFile
    Remove-Item Env:\ROSLYNSENTINEL_CONTEXT_WINDOW, Env:\ROSLYNSENTINEL_CONTEXT_BUFFER, Env:\ROSLYNSENTINEL_CONTEXT_NUDGE_TOKENS, Env:\ROSLYNSENTINEL_CONTEXT_URGENT_TOKENS -ErrorAction SilentlyContinue
    $tfile = Join-Path $dir 'transcript.jsonl'
    function Write-Transcript([long[]]$totals, [string]$extra = '') {
        $lines = @('{"type":"user","message":{"content":"hi"}}')
        foreach ($t in $totals) {
            $lines += '{"type":"assistant","isSidechain":false,"message":{"model":"claude-sonnet-5-5","usage":{"input_tokens":10,"cache_read_input_tokens":' + ($t - 15) + ',"cache_creation_input_tokens":5,"output_tokens":3}}}'
            $lines += '{"type":"user","message":{"content":"tool result"}}'
        }
        if ($extra) { $lines += $extra }
        [IO.File]::WriteAllText($tfile, (($lines -join "`n") + "`n"))
    }
    $ctxPost = (@{ session_id = 'abcdef123456'; hook_event_name = 'PostToolUse'; transcript_path = $tfile } | ConvertTo-Json -Compress)
    $ctxSub  = (@{ session_id = 'abcdef123456'; hook_event_name = 'PostToolUse'; transcript_path = $tfile; agent_id = 'a9'; agent_type = 'implementer' } | ConvertTo-Json -Compress)
    Remove-Item (Join-Path $dir '*.nudged') -ErrorAction SilentlyContinue

    Write-Transcript 100000
    Assert ((Invoke-Hook $nudge $ctxPost) -eq '') 'context: well below the nudge tier is silent'

    Write-Transcript 100000, 190000
    $ctx = ((Invoke-Hook $nudge $ctxPost) | ConvertFrom-Json).hookSpecificOutput.additionalContext
    Assert ($ctx -match 'Context: about 190k' -and $ctx -match 'near 217k' -and $ctx -match 'journal' -and $ctx -notmatch 'imminent') 'context: nudge tier names size, compaction point and the journal'
    Assert ((Invoke-Hook $nudge $ctxPost) -eq '') 'context: nudge fires once'

    Write-Transcript 207000
    $ctx = ((Invoke-Hook $nudge $ctxPost) | ConvertFrom-Json).hookSpecificOutput.additionalContext
    Assert ($ctx -match 'imminent' -and $ctx -match '207k') 'context: urgent tier fires separately'
    Assert ((Invoke-Hook $nudge $ctxPost) -eq '') 'context: urgent fires once'

    Write-Transcript 40000
    Assert ((Invoke-Hook $nudge $ctxPost) -eq '') 'context: after compaction (small again) is silent'
    Write-Transcript 190000
    Assert ((Invoke-Hook $nudge $ctxPost) -match 'Context: about 190k') 'context: next fill after compaction warns again'

    Remove-Item (Join-Path $dir '*.nudged') -ErrorAction SilentlyContinue
    Write-Transcript 190000
    Assert ((Invoke-Hook $nudge $ctxSub) -eq '') 'context: subagent never gets the main context notice'

    Write-Transcript 100000 '{"type":"assistant","isSidechain":true,"message":{"model":"x","usage":{"input_tokens":1,"cache_read_input_tokens":300000,"cache_creation_input_tokens":1}}}'
    Assert ((Invoke-Hook $nudge $ctxPost) -eq '') 'context: sidechain records are ignored'

    [IO.File]::WriteAllText($settingsFile, '{"autoCompactWindow":250000,"modelSettings":{"claude-sonnet-5-5":{"autoCompactWindow":600000}}}')
    Write-Transcript 190000
    Assert ((Invoke-Hook $nudge $ctxPost) -eq '') 'context: per-model autoCompactWindow moves the tiers'
    [IO.File]::WriteAllText($settingsFile, '{"autoCompactWindow":250000}')

    $env:ROSLYNSENTINEL_CONTEXT_NUDGE_TOKENS = '150000'
    Write-Transcript 160000
    Assert ((Invoke-Hook $nudge $ctxPost) -match 'Context: about 160k') 'context: ROSLYNSENTINEL_CONTEXT_NUDGE_TOKENS overrides the tier'
    Remove-Item Env:\ROSLYNSENTINEL_CONTEXT_NUDGE_TOKENS

    Remove-Item $tfile
    Assert ((Invoke-Hook $nudge $ctxPost) -eq '') 'context: missing transcript is silent'
    Remove-Item Env:\ROSLYNSENTINEL_CLAUDE_SETTINGS
    Remove-Item (Join-Path $dir '*.nudged') -ErrorAction SilentlyContinue

    # --- OAuth refresh (usage-refresh.ps1) against a local fake endpoint ---
    $tcp = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $tcp.Start()
    $port = $tcp.LocalEndpoint.Port; $tcp.Stop()
    $state = [hashtable]::Synchronized(@{ status = 200; body = ''; retryAfter = $null; count = 0; auth = @() })
    $listener = New-Object Net.HttpListener
    $listener.Prefixes.Add("http://127.0.0.1:$port/"); $listener.Start()
    $psListener = [powershell]::Create()
    [void]$psListener.AddScript({
        param($l, $s)
        while ($l.IsListening) {
            try { $c = $l.GetContext() } catch { break }
            $s.count++; $s.auth += $c.Request.Headers['Authorization']
            $c.Response.StatusCode = $s.status
            if ($s.retryAfter) { $c.Response.Headers['Retry-After'] = [string]$s.retryAfter }
            $b = [Text.Encoding]::UTF8.GetBytes([string]$s.body)
            $c.Response.OutputStream.Write($b, 0, $b.Length); $c.Response.Close()
        }
    }).AddArgument($listener).AddArgument($state)
    [void]$psListener.BeginInvoke()

    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $credFile = Join-Path $dir 'credentials.json'
    function Set-Token([string]$t) { [IO.File]::WriteAllText($credFile, ('{"claudeAiOauth":{"accessToken":"' + $t + '"}}')) }
    Set-Token 'tok-1'
    $env:ROSLYNSENTINEL_USAGE_CREDENTIALS = $credFile
    $env:ROSLYNSENTINEL_USAGE_ENDPOINT = "http://127.0.0.1:$port/api/oauth/usage"
    $env:ROSLYNSENTINEL_USAGE_TIMEOUT_SECONDS = '5'
    $snapFile = Join-Path $dir 'usage.json'; $blockFile = Join-Path $dir 'usage.blockedUntil'
    $okBody = '{"five_hour":{"utilization":87.5,"resets_at":"2030-01-01T10:00:00.5+00:00"},"seven_day":{"utilization":40,"resets_at":"2030-01-05T10:00:00+00:00"},"seven_day_opus":null}'
    $expectFive = [DateTimeOffset]::Parse('2030-01-01T10:00:00+00:00').ToUnixTimeSeconds()
    function Reset-Refresh { Remove-Item $snapFile, $blockFile, (Join-Path $dir '*.nudged'), (Join-Path $dir '*.tiptoe') -ErrorAction SilentlyContinue }

    # 200: stale snapshot is replaced from the endpoint, and the 85% tier fires from the fresh data
    Reset-Refresh
    $state.status = 200; $state.body = $okBody; $state.count = 0; $state.auth = @()
    [IO.File]::WriteAllText($snapFile, (@{ updated = ($now - 7200); five_hour = @{ used_percentage = 1; resets_at = $future } } | ConvertTo-Json -Depth 4))
    $ctx = ((Invoke-Hook $nudge $post) | ConvertFrom-Json).hookSpecificOutput.additionalContext
    $snap = Get-Content $snapFile -Raw | ConvertFrom-Json
    Assert ($snap.five_hour.used_percentage -eq 87.5 -and $snap.five_hour.resets_at -eq $expectFive -and $snap.seven_day.used_percentage -eq 40) 'refresh: 200 writes snapshot in statusline shape (epoch resets_at)'
    Assert ($snap.source -eq 'oauth-usage' -and $state.auth[0] -eq 'Bearer tok-1') 'refresh: bearer token sent'
    Assert ($ctx -match '87%|88%' -and $ctx -match 'wrapup' -and $ctx -notmatch 'INACTIVE') 'refresh: fresh data drives the nudge, no stale notice'

    # throttle: a fresh snapshot is not refetched
    $state.count = 0
    [void](Invoke-Hook $nudge $post)
    Assert ($state.count -eq 0) 'refresh: fresh snapshot is not refetched'

    # 429: Retry-After is honoured as a block (minimum 60 s) and stops further calls
    Reset-Refresh
    $state.status = 429; $state.body = '{}'; $state.retryAfter = 120; $state.count = 0
    [void](Invoke-Hook $nudge $post)
    $until = [long](Get-Content $blockFile -Raw)
    Assert ($state.count -eq 1 -and $until -ge ($now + 110) -and $until -le ($now + 140)) 'refresh: 429 writes blockedUntil from Retry-After'
    [void](Invoke-Hook $nudge $post)
    Assert ($state.count -eq 1) 'refresh: blocked window suppresses further calls'

    # 500: short block, no snapshot written
    Reset-Refresh
    $state.status = 500; $state.body = 'boom'; $state.retryAfter = $null; $state.count = 0
    [void](Invoke-Hook $nudge $post)
    Assert ((Test-Path $blockFile) -and -not (Test-Path $snapFile)) 'refresh: server error blocks briefly and writes no snapshot'

    # 401: retried once with the re-read token only if it changed
    Reset-Refresh
    $state.status = 401; $state.body = '{}'; $state.count = 0
    [void](Invoke-Hook $nudge $post)
    Assert ($state.count -eq 1) 'refresh: 401 with unchanged token is not retried'

    Reset-Refresh
    $state.count = 0; $state.auth = @()
    # emulate Claude Code renewing the token on disk mid-call: stub the request function in-process
    . (Join-Path $PSScriptRoot 'usage-refresh.ps1')
    $realInvoke = ${function:Invoke-UsageRequest}
    $script:calls = 0
    function Invoke-UsageRequest([string]$token, [string]$url, [int]$timeout) {
        $script:calls++
        if ($token -eq 'tok-1') { Set-Token 'tok-2'; return @{ Status = 401; Body = '{}'; RetryAfter = $null } }
        @{ Status = 200; Body = $okBody; RetryAfter = $null }
    }
    $ok = Update-UsageSnapshot $dir
    Assert ($ok -and $script:calls -eq 2 -and (Test-Path $snapFile)) 'refresh: 401 then renewed token retries once and succeeds'
    Set-Item function:Invoke-UsageRequest $realInvoke
    Set-Token 'tok-1'

    # no credentials file: silent, no call, no block file
    Reset-Refresh
    $state.count = 0
    $env:ROSLYNSENTINEL_USAGE_CREDENTIALS = Join-Path $dir 'missing.json'
    [void](Invoke-Hook $nudge $post)
    Assert ($state.count -eq 0 -and -not (Test-Path $blockFile)) 'refresh: missing credentials makes no call'
}
finally {
    if ($listener) { try { $listener.Stop(); $listener.Close() } catch { } }
    if ($psListener) { try { $psListener.Stop(); $psListener.Dispose() } catch { } }
    Remove-Item Env:\ROSLYNSENTINEL_USAGE_CREDENTIALS, Env:\ROSLYNSENTINEL_USAGE_ENDPOINT, Env:\ROSLYNSENTINEL_USAGE_TIMEOUT_SECONDS -ErrorAction SilentlyContinue
    Remove-Item Env:\ROSLYNSENTINEL_CLAUDE_SETTINGS, Env:\ROSLYNSENTINEL_CONTEXT_NUDGE_TOKENS -ErrorAction SilentlyContinue
    Remove-Item Env:\ROSLYNSENTINEL_USAGE_DIR -ErrorAction SilentlyContinue
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

if ($failures -gt 0) { Write-Host "$failures failure(s)"; exit 1 }
Write-Host 'all passed'
