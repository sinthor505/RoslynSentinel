cls

# $body = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0.1"}}}'
# No Content-Length, just raw JSON line

# Write input first
# [System.IO.File]::WriteAllText("C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Server\bin\Debug\net10.0\in.txt", $body + "`n")

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Server\bin\Debug\net10.0\RoslynSentinel.Server.exe"
$psi.WorkingDirectory = "C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Server\bin\Debug\net10.0\"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false

# Start the process
$proc = [System.Diagnostics.Process]::Start($psi)

# Give the server time to start its read loop
Start-Sleep -Milliseconds 500

# Send the input to the server
$body = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"0.1"}}}'
$proc.StandardInput.Write($body)
$proc.StandardInput.Flush()

# Read one response line
$response = $proc.StandardOutput.ReadLine()
Write-Host "RESPONSE: $response"

# Send initialized notification to complete handshake
$notify = '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
$proc.StandardInput.WriteLine($notify)
$proc.StandardInput.Flush()

$out = $proc.StandardOutput.ReadToEnd()
$err = $proc.StandardError.ReadToEnd()

# Now keep stdin open — server stays alive
# $proc.StandardInput.Close()
$proc.WaitForExit(5000)

Write-Host "=== STDOUT ==="
$out
Write-Host "=== STDERR ==="
$err
