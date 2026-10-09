# Dot-sourced by usage-nudge.ps1. Defines Update-UsageSnapshot: fetches the claude.ai usage-limit
# windows straight from Anthropic's OAuth usage endpoint and writes them to usage.json in the same
# shape usage-statusline.ps1 writes. This is what keeps the snapshot fresh in the VS Code extension,
# which never runs the statusLine command. Approach taken from CodeBurn (src/quota/claude.ts).
#
# - Endpoint is undocumented: GET /api/oauth/usage with the Claude Code OAuth access token from
#   ~/.claude/.credentials.json (claudeAiOauth.accessToken) and the oauth-2025-04-20 beta header.
#   Response: five_hour / seven_day { utilization (0-100), resets_at (ISO-8601) }.
# - The token is only ever sent to api.anthropic.com, never logged or written anywhere. This file
#   never writes the credentials file (Claude Code owns token renewal); on a 401 it re-reads it once.
# - Throttled: skips the call while usage.json is younger than ROSLYNSENTINEL_USAGE_REFRESH_SECONDS
#   (default 90). A 429 or any failure writes <dir>/usage.blockedUntil (epoch seconds) so the hook,
#   which runs on every tool call, does not hammer the endpoint: Retry-After (min 60, default 300)
#   for 429, 60 s otherwise.
# - Test/override env vars: ROSLYNSENTINEL_USAGE_CREDENTIALS (credentials file path),
#   ROSLYNSENTINEL_USAGE_ENDPOINT (URL), ROSLYNSENTINEL_USAGE_TIMEOUT_SECONDS (default 5; the hook's
#   own timeout is 10 s).
#
# Returns $true when a fresh snapshot was written. FAIL-OPEN: never throws.

function Get-UsageAccessToken([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $o = (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).claudeAiOauth
    if ($null -ne $o -and $o.accessToken -is [string] -and $o.accessToken.Length -gt 0) { return [string]$o.accessToken }
    $null
}

function Invoke-UsageRequest([string]$token, [string]$url, [int]$timeout) {
    # Returns @{ Status; Body; RetryAfter } and never throws for HTTP errors.
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/json'; 'anthropic-beta' = 'oauth-2025-04-20'; 'User-Agent' = 'claude-code/2.1.0' }
    try {
        $r = Invoke-WebRequest -Uri $url -Headers $headers -TimeoutSec $timeout -UseBasicParsing -ErrorAction Stop
        # Windows PowerShell 5.1 returns byte[] when the response has no text Content-Type.
        $text = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
        return @{ Status = [int]$r.StatusCode; Body = $text; RetryAfter = $null }
    }
    catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { return @{ Status = 0; Body = $null; RetryAfter = $null } }
        $body = $null; $retry = $null
        try {
            if ($resp -is [System.Net.HttpWebResponse]) {
                $retry = $resp.Headers['Retry-After']
                $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
                try { $body = $sr.ReadToEnd() } finally { $sr.Dispose() }
            }
            else {
                $ra = $resp.Headers.RetryAfter
                if ($null -ne $ra -and $null -ne $ra.Delta) { $retry = [string][int]$ra.Delta.TotalSeconds }
                $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
        } catch { }
        return @{ Status = [int]$resp.StatusCode; Body = $body; RetryAfter = $retry }
    }
}

function ConvertTo-UsageWindow($w) {
    # $w is a five_hour / seven_day object from the endpoint; $null when unusable.
    if ($null -eq $w -or $null -eq $w.utilization -or -not $w.resets_at) { return $null }
    $dto = [DateTimeOffset]::MinValue
    if ($w.resets_at -is [DateTimeOffset]) { $dto = $w.resets_at }
    elseif ($w.resets_at -is [DateTime]) { $dto = [DateTimeOffset]$w.resets_at }   # PowerShell 7 ConvertFrom-Json auto-converts ISO strings
    elseif (-not [DateTimeOffset]::TryParse([string]$w.resets_at, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$dto)) { return $null }
    [ordered]@{
        used_percentage = [math]::Min(100.0, [math]::Max(0.0, [double]$w.utilization))
        resets_at       = $dto.ToUnixTimeSeconds()
    }
}

function Update-UsageSnapshot([string]$dir) {
    try {
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $snapFile = Join-Path $dir 'usage.json'
        $blockFile = Join-Path $dir 'usage.blockedUntil'

        $refreshAfter = 90
        if ($env:ROSLYNSENTINEL_USAGE_REFRESH_SECONDS -match '^\d+$') { $refreshAfter = [int]$env:ROSLYNSENTINEL_USAGE_REFRESH_SECONDS }
        if (Test-Path -LiteralPath $snapFile) {
            $snap = Get-Content -LiteralPath $snapFile -Raw | ConvertFrom-Json
            if ($null -ne $snap.updated -and ($now - [long]$snap.updated) -lt $refreshAfter) { return $false }
        }
        if (Test-Path -LiteralPath $blockFile) {
            $until = 0L
            if ([long]::TryParse(((Get-Content -LiteralPath $blockFile -Raw).Trim()), [ref]$until) -and $until -gt $now) { return $false }
        }

        $credPath = if ($env:ROSLYNSENTINEL_USAGE_CREDENTIALS) { $env:ROSLYNSENTINEL_USAGE_CREDENTIALS } else { Join-Path $env:USERPROFILE '.claude\.credentials.json' }
        $url = if ($env:ROSLYNSENTINEL_USAGE_ENDPOINT) { $env:ROSLYNSENTINEL_USAGE_ENDPOINT } else { 'https://api.anthropic.com/api/oauth/usage' }
        $timeout = 5
        if ($env:ROSLYNSENTINEL_USAGE_TIMEOUT_SECONDS -match '^\d+$') { $timeout = [int]$env:ROSLYNSENTINEL_USAGE_TIMEOUT_SECONDS }

        $token = Get-UsageAccessToken $credPath
        if (-not $token) { return $false }   # not logged in with a subscription: nothing to back off from

        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        function Set-Block([long]$seconds) {
            [System.IO.File]::WriteAllText($blockFile, [string]($now + $seconds))
        }

        $res = Invoke-UsageRequest $token $url $timeout
        if ($res.Status -eq 401) {
            # Claude Code renews the token on disk; retry once only if it actually changed.
            $again = Get-UsageAccessToken $credPath
            if ($again -and $again -ne $token) { $res = Invoke-UsageRequest $again $url $timeout }
        }
        if ($res.Status -eq 429) {
            $secs = 0.0
            $hint = $res.RetryAfter
            if (-not $hint -and $res.Body) { try { $hint = (ConvertFrom-Json $res.Body).retry_after } catch { } }
            if (-not [double]::TryParse([string]$hint, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$secs)) { $secs = 300 }
            Set-Block ([long][math]::Max(60, $secs))
            return $false
        }
        if ($res.Status -ne 200 -or -not $res.Body) { Set-Block 60; return $false }

        $data = ConvertFrom-Json $res.Body
        $five = ConvertTo-UsageWindow $data.five_hour
        $seven = ConvertTo-UsageWindow $data.seven_day
        if ($null -eq $five -and $null -eq $seven) { Set-Block 60; return $false }

        $out = [ordered]@{ updated = $now; source = 'oauth-usage' }
        if ($five)  { $out['five_hour'] = $five }
        if ($seven) { $out['seven_day'] = $seven }
        $tmp = "$snapFile.$PID.tmp"
        [System.IO.File]::WriteAllText($tmp, ($out | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $tmp -Destination $snapFile -Force
        Remove-Item -LiteralPath $blockFile -ErrorAction SilentlyContinue
        return $true
    }
    catch { return $false }
}
