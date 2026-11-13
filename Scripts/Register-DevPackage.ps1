# Registers WinMacShell in Developer Mode so UserNotificationListener can receive Windows toast notifications.
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceManifest = Join-Path $repoRoot "src\WinMacShell\Package.appxmanifest"
$outputDir = Join-Path $repoRoot "src\WinMacShell\bin\Debug\net10.0-windows10.0.19041.0"
$destManifest = Join-Path $outputDir "AppxManifest.xml"

if (-not (Test-Path $sourceManifest)) {
    throw "Package.appxmanifest not found at $sourceManifest"
}

if (-not (Test-Path (Join-Path $outputDir "WinMacShell.exe"))) {
    throw "WinMacShell.exe not found in $outputDir. Run 'dotnet build' first."
}

Get-Process WinMacShell -ErrorAction SilentlyContinue | Stop-Process -Force

Copy-Item -Path $sourceManifest -Destination $destManifest -Force

Write-Host "Registering WinMacShell Dev Package..." -ForegroundColor Cyan
Add-AppxPackage -Register $destManifest -ForceApplicationShutdown
Write-Host "Dev Package registered! Launch WinMacShell from Start Menu or $outputDir\WinMacShell.exe." -ForegroundColor Green
