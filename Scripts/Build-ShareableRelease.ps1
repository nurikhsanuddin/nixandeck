#Requires -Version 5.1
<#
.SYNOPSIS
    Phase 5 (Opsi 1): Builds a self-contained x64 Release of NixanDeck (WinMacShell) + NixanDeck.Watchdog,
    generates a self-signed MSIX Code Signing Certificate (NixanDeck.cer & NixanDeck.pfx),
    packages and signs publish/dist/NixanDeck.msix via MakeAppx.exe & SignTool.exe,
    creates auto-elevating publish/dist/Install.bat, and compresses publish/dist/ into publish/NixanDeck-Windows11-x64.zip.
#>

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnetExe = if (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" } else { "dotnet" }

$publishRoot = Join-Path $repoRoot "publish"
$appDir      = Join-Path $publishRoot "app"
$distDir     = Join-Path $publishRoot "dist"
$certsDir    = Join-Path $publishRoot "certs"
$toolsDir    = Join-Path $publishRoot "tools"
$zipOutput   = Join-Path $publishRoot "NixanDeck-Windows11-x64.zip"

# Support either src/NixanDeck/NixanDeck.csproj or src/WinMacShell/WinMacShell.csproj
$mainCsproj = Join-Path $repoRoot "src\NixanDeck\NixanDeck.csproj"
$mainExeName = "NixanDeck.exe"
if (-not (Test-Path $mainCsproj)) {
    $mainCsproj = Join-Path $repoRoot "src\WinMacShell\WinMacShell.csproj"
    $mainExeName = "WinMacShell.exe"
}
$watchdogCsproj = Join-Path $repoRoot "src\NixanDeck.Watchdog\NixanDeck.Watchdog.csproj"

Write-Host "==> [1/5] Preparing publish directories..." -ForegroundColor Cyan
Get-Process WinMacShell, NixanDeck, NixanDeck.Watchdog -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

if (Test-Path $appDir)  { Remove-Item $appDir -Recurse -Force }
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
New-Item -ItemType Directory -Path $certsDir -Force | Out-Null
New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null

Write-Host "==> [2/5] Publishing Main App & NixanDeck.Watchdog (Release, win-x64, self-contained)..." -ForegroundColor Cyan
& $dotnetExe publish $watchdogCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $appDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for NixanDeck.Watchdog" }

& $dotnetExe publish $mainCsproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $appDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $mainCsproj" }

# Remove any .pdb files from publish/app to keep the MSIX clean & compact
Get-ChildItem -Path $appDir -Filter "*.pdb" -File -ErrorAction SilentlyContinue | Remove-Item -Force

# Write AppxManifest.xml into publish/app matching Subject CN=NixanDeck
$appxManifestPath = Join-Path $appDir "AppxManifest.xml"
$appxManifestXml = @"
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
  xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap uap3 desktop rescap">

  <Identity
    Name="NixanDeck"
    Publisher="CN=NixanDeck"
    Version="1.0.0.0"
    ProcessorArchitecture="x64" />

  <Properties>
    <DisplayName>NixanDeck (nixandock)</DisplayName>
    <PublisherDisplayName>NixanDeck</PublisherDisplayName>
    <Description>Lightweight macOS-style Dynamic Island, Top Bar, and Liquid-Glass Dock for Windows 11</Description>
    <Logo>Assets\Square44x44Logo.png</Logo>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>

  <Resources>
    <Resource Language="en-us" />
  </Resources>

  <Applications>
    <Application Id="NixanDeckApp"
                 Executable="$mainExeName"
                 EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="NixanDeck (nixandock)"
        Description="Dynamic Island &amp; Liquid-Glass Dock Shell for Windows 11"
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png" />
      <Extensions>
        <desktop:Extension Category="windows.startupTask"
                           Executable="$mainExeName"
                           EntryPoint="Windows.FullTrustApplication">
          <desktop:StartupTask TaskId="NixanDeckStartupTask"
                               Enabled="true"
                               DisplayName="NixanDeck (nixandock)" />
        </desktop:Extension>
      </Extensions>
    </Application>
  </Applications>

  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
    <uap3:Capability Name="userNotificationListener" />
    <DeviceCapability Name="radios" />
  </Capabilities>
</Package>
"@
[System.IO.File]::WriteAllText($appxManifestPath, $appxManifestXml, [System.Text.UTF8Encoding]::new($false))

Write-Host "==> [3/5] Ensuring Self-Signed Certificate (NixanDeck.cer & NixanDeck.pfx)..." -ForegroundColor Cyan
$pfxPath = Join-Path $certsDir "NixanDeck.pfx"
$cerPath = Join-Path $certsDir "NixanDeck.cer"
$distCerPath = Join-Path $distDir "NixanDeck.cer"
$pfxPasswordPlain = "NixanDeckRelease"
$pfxPassword = ConvertTo-SecureString -String $pfxPasswordPlain -Force -AsPlainText

if (-not (Test-Path $pfxPath) -or -not (Test-Path $cerPath)) {
    Write-Host "    Generating new self-signed MSIX code signing certificate (CN=NixanDeck)..." -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject "CN=NixanDeck" `
        -KeyUsage DigitalSignature `
        -FriendlyName "NixanDeck MSIX Release Certificate" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
        -NotAfter (Get-Date).AddYears(5)

    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pfxPassword | Out-Null
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
}
Copy-Item -Path $cerPath -Destination $distCerPath -Force

Write-Host "==> [4/5] Packaging & Signing publish/dist/NixanDeck.msix..." -ForegroundColor Cyan
function Resolve-SdkTool {
    param([string]$ToolName)

    $searchRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools",
        (Join-Path $toolsDir "sdk-buildtools")
    )

    foreach ($root in $searchRoots) {
        if (Test-Path $root) {
            $found = Get-ChildItem -Path $root -Recurse -Filter $ToolName -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match "\\x64\\" } |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($found) { return $found.FullName }
        }
    }

    return $null
}

$makeAppx = Resolve-SdkTool -ToolName "makeappx.exe"
$signTool = Resolve-SdkTool -ToolName "signtool.exe"

if (-not $makeAppx -or -not $signTool) {
    Write-Host "    Windows SDK tools not found globally; downloading Microsoft.Windows.SDK.BuildTools..." -ForegroundColor Yellow
    $sdkExtractDir = Join-Path $toolsDir "sdk-buildtools"
    $nupkgZip = Join-Path $toolsDir "Microsoft.Windows.SDK.BuildTools.zip"
    Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools" -OutFile $nupkgZip -UseBasicParsing
    if (Test-Path $sdkExtractDir) { Remove-Item $sdkExtractDir -Recurse -Force }
    Expand-Archive -Path $nupkgZip -DestinationPath $sdkExtractDir -Force
    Remove-Item $nupkgZip -Force

    $makeAppx = Resolve-SdkTool -ToolName "makeappx.exe"
    $signTool = Resolve-SdkTool -ToolName "signtool.exe"
}

if (-not $makeAppx -or -not $signTool) {
    throw "Could not locate MakeAppx.exe or SignTool.exe."
}

$msixPath = Join-Path $distDir "NixanDeck.msix"
& $makeAppx pack /d $appDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx.exe failed with exit code $LASTEXITCODE" }

& $signTool sign /fd SHA256 /a /f $pfxPath /p $pfxPasswordPlain $msixPath
if ($LASTEXITCODE -ne 0) { throw "SignTool.exe failed with exit code $LASTEXITCODE" }

Write-Host "==> [5/5] Generating Install.bat & Compressing publish/NixanDeck-Windows11-x64.zip..." -ForegroundColor Cyan
$installBatPath = Join-Path $distDir "Install.bat"
$installBatContent = @"
@echo off
title NixanDeck (nixandock) Installer
:: Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator privileges to install the NixanDeck certificate...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ========================================================
echo   NixanDeck (nixandock) - Windows 11 Shell Installer
echo ========================================================
echo.
echo [1/2] Installing security certificate into TrustedPeople...
certutil -addstore -f "TrustedPeople" "%~dp0NixanDeck.cer"
if %errorLevel% neq 0 (
    echo Failed to install NixanDeck.cer certificate.
    pause
    exit /b 1
)

echo.
echo [2/2] Launching NixanDeck.msix package installer...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-AppxPackage NixanDeck* | Remove-AppxPackage -ErrorAction SilentlyContinue"
echo Please click the "Install" button in the installer window that appears.
start "" "%~dp0NixanDeck.msix"
timeout /t 3 >nul
exit /b 0
"@
[System.IO.File]::WriteAllText($installBatPath, $installBatContent, [System.Text.Encoding]::ASCII)

if (Test-Path $zipOutput) { Remove-Item $zipOutput -Force }
Compress-Archive -Path (Join-Path $distDir "*") -DestinationPath $zipOutput -Force

Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host "  SHAREABLE RELEASE BUILD COMPLETE!" -ForegroundColor Green
Write-Host "  - MSIX Package : $msixPath" -ForegroundColor Green
Write-Host "  - Certificate  : $distCerPath" -ForegroundColor Green
Write-Host "  - Installer    : $installBatPath" -ForegroundColor Green
Write-Host "  - Shareable ZIP: $zipOutput" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
