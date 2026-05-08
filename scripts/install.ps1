# Roslyn Sentinel - Installation Script

$repoRoot = Get-Location
$publishDir = "$repoRoot\publish"

Write-Host "--- Roslyn Sentinel Installer ---" -ForegroundColor Cyan

# 1. Build and Publish
Write-Host "Building and publishing server to $publishDir..."
dotnet publish "$repoRoot\RoslynSentinel.Server\RoslynSentinel.Server.csproj" -c Release -o "$publishDir"

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed. Ensure .NET 10 SDK is installed."
    exit $LASTEXITCODE
}

$dllPath = "$publishDir\RoslynSentinel.Server.dll"
# Normalize path for JSON
$normalizedPath = $dllPath.Replace("\", "/")

Write-Host "Success! Server is published at: $dllPath" -ForegroundColor Green

# 2. Registration Instructions
Write-Host "`n--- Registration ---" -ForegroundColor Yellow
Write-Host "To use this with Claude Desktop, add this to your config file:"
Write-Host @"
{
  `"mcpServers`": {
    `"roslyn-sentinel`": {
      `"command`": `"dotnet`",
      `"args`": [
        `"$normalizedPath`",
        `"--mode=all`"
      ]
    }
  }
}
"@ -ForegroundColor Green

Write-Host "`nConfig location: %APPDATA%\Claude\claude_desktop_config.json"
