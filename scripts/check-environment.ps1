$ErrorActionPreference = "Stop"

Write-Host "Checking .NET installation..."
dotnet --info

Write-Host ""
Write-Host "Checking whether .NET SDK is installed..."
$info = dotnet --info | Out-String
if ($info -match "No SDKs were found") {
    Write-Host "Missing: .NET SDK is not installed." -ForegroundColor Yellow
    Write-Host "Install .NET 8 SDK, then run this script again."
    exit 1
}

Write-Host "OK: .NET SDK is available." -ForegroundColor Green
