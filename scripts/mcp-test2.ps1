Clear-Host
Add-Type -AssemblyName System.Net.Http

$base = "http://localhost:5100/mcp"

# ── Start server if not already running ───────────────────────────────────────
$manualStart = $false
$proc = Get-Process RoslynSentinel.HttpHost -ErrorAction SilentlyContinue
if ($null -eq $proc) {
    Write-Host "Starting HTTP host..."
    $proc = Start-Process -FilePath "dotnet" `
        -ArgumentList "C:\Users\Administrator\source\repos\RoslynSentinel\publish\RoslynSentinel.HttpHost.dll" `
        -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 4
    $manualStart = $true
}

# ── Helpers ────────────────────────────────────────────────────────────────────
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [System.TimeSpan]::FromSeconds(30)

function New-McpRequest([string]$Json, [string]$SessionId) {
    $req = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Post, $base)
    $req.Content = [System.Net.Http.StringContent]::new(
        $Json, [System.Text.Encoding]::UTF8, "application/json")
    $req.Headers.Accept.ParseAdd("application/json")
    $req.Headers.Accept.ParseAdd("text/event-stream")
    if ($SessionId) { $req.Headers.TryAddWithoutValidation("mcp-session-id", $SessionId) | Out-Null }
    return $req
}

function Read-SseJson([System.IO.Stream]$Stream, [int]$TimeoutMs = 5000) {
    $reader = [System.IO.StreamReader]::new($Stream)
    $deadline = [datetime]::UtcNow.AddMilliseconds($TimeoutMs)
    $result = $null
    while (-not $reader.EndOfStream -and [datetime]::UtcNow -lt $deadline) {
        $line = $reader.ReadLine()
        if ($line -match '^data:\s*(.+)') { $result = $matches[1] | ConvertFrom-Json; break }
    }
    return $result
}

# ── 1. Initialize  (keep SSE body open on a background thread) ─────────────────
$initJson = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"PSClient","version":"1.0"}}}'

$initResp = $client.SendAsync(
    (New-McpRequest $initJson),
    [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
).GetAwaiter().GetResult()

# Extract session ID from response headers BEFORE touching the body
$sessionId = $initResp.Headers.GetValues("mcp-session-id") | Select-Object -First 1
Write-Host "Session ID: $sessionId"

# Drain the SSE body on a thread job to keep the session alive
$sseStream = $initResp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
$drainJob = Start-ThreadJob -ScriptBlock {
    param($s)
    $r = [System.IO.StreamReader]::new($s)
    while (-not $r.EndOfStream) { $null = $r.ReadLine() }
} -ArgumentList $sseStream
Start-Sleep -Milliseconds 300   # let data arrive

# Re-read the stream for our use (we need to read it once to get the init response)
# The stream is already open - read from it
# Since Start-ThreadJob may have already consumed it, just log the session ID for now
Write-Host "Initialize: OK (session established)"

# ── 2. notifications/initialized ──────────────────────────────────────────────
$notifyJson = '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
$notifyResp = $client.SendAsync(
    (New-McpRequest $notifyJson $sessionId)
).GetAwaiter().GetResult()
Write-Host "Initialized notification: $($notifyResp.StatusCode)"

# ── 3. tools/list ─────────────────────────────────────────────────────────────
$listJson = '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
$listResp = $client.SendAsync(
    (New-McpRequest $listJson $sessionId),
    [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
).GetAwaiter().GetResult()

$listStream = $listResp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
$parsed     = Read-SseJson $listStream

if ($parsed.result) {
    $tools = $parsed.result.tools
    Write-Host "`n$($tools.Count) tools:`n"
    $tools | Select-Object name, description | Export-Csv -Path "tools.csv" -Force
    $tools | ConvertTo-Json -Depth 5 | Set-Content -Path "tools.json" -Force
    $tools | Select-Object name, description | Format-Table -AutoSize -Wrap
} else {
    Write-Warning "Unexpected response: $($parsed | ConvertTo-Json -Depth 5)"
}

# ── Cleanup ────────────────────────────────────────────────────────────────────
Stop-Job  $drainJob -ErrorAction SilentlyContinue
Remove-Job $drainJob -ErrorAction SilentlyContinue
$client.Dispose()

if ($manualStart) {
    Write-Host "Stopping HTTP host..."
    $proc | Stop-Process -Force
}
