# Kill any running RoslynSentinel.Server process so the publish step can overwrite the .exe/.dll.
# Safe to run even when no instance is running.

$procs = Get-Process -Name "RoslynSentinel.Server" -ErrorAction SilentlyContinue
if ($procs) {
    Write-Host "Stopping $($procs.Count) RoslynSentinel.Server process(es)..." -ForegroundColor Yellow
    $procs | Stop-Process -Force
    # Brief wait for the OS to release file handles before the caller proceeds.
    Start-Sleep -Milliseconds 600
    Write-Host "Done." -ForegroundColor Green
} else {
    Write-Host "No running RoslynSentinel.Server processes found." -ForegroundColor DarkGray
}
