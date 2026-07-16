$ErrorActionPreference = "Stop"

Write-Host "Installing .NET 8 SDK with winget..."
winget install --id Microsoft.DotNet.SDK.8 --source winget --accept-package-agreements --accept-source-agreements

Write-Host ""
Write-Host "After installation, open a new terminal and run:"
Write-Host "  dotnet --info"
