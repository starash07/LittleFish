param(
    [string]$Version = "1.6.0",
    [switch]$SkipObfuscation,
    [switch]$KeepBuildArtifacts
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$appProject = Join-Path $root "src\LittleFish.App\LittleFish.App.csproj"
$installerProject = Join-Path $root "src\LittleFish.Installer\LittleFish.Installer.csproj"
$uninstallerProject = Join-Path $root "src\LittleFish.Uninstaller\LittleFish.Uninstaller.csproj"
$dist = Join-Path $root "dist\installer"
$publishDir = Join-Path $dist "publish"
$protectedDir = Join-Path $dist "protected"
$obfuscatedDir = Join-Path $dist "obfuscated"
$setupDir = Join-Path $root "dist\setup"
$payloadPath = Join-Path $root "src\LittleFish.Installer\Assets\payload.zip"
$payloadHashPath = Join-Path $root "src\LittleFish.Installer\Assets\payload.sha256"
$uninstallerPublishDir = Join-Path $dist "uninstall-publish"
$installerPublishDir = Join-Path $dist "setup-publish"
$obfuscarConfig = Join-Path $dist "obfuscar.xml"

function Find-Obfuscar {
    $cmd = Get-Command "obfuscar.console" -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $candidate = Join-Path $env:USERPROFILE ".dotnet\tools\obfuscar.console.exe"
    if (Test-Path $candidate) {
        return $candidate
    }

    throw "obfuscar.console was not found. Install it with: dotnet tool install -g Obfuscar.GlobalTool"
}

function Remove-DirectoryIfExists([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        return
    }

    $resolvedRoot = (Resolve-Path -LiteralPath $root).Path
    $resolvedPath = (Resolve-Path -LiteralPath $path).Path
    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove outside workspace: $resolvedPath"
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Remove-FileIfExists([string]$path) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

function Clear-DirectoryIfExists([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        return
    }

    $resolvedRoot = (Resolve-Path -LiteralPath $root).Path
    $resolvedPath = (Resolve-Path -LiteralPath $path).Path
    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear outside workspace: $resolvedPath"
    }

    Get-ChildItem -LiteralPath $resolvedPath -Force | Remove-Item -Recurse -Force
}

function Invoke-SmokeTest([string]$exePath, [string]$workingDirectory, [string]$name) {
    $process = Start-Process -FilePath $exePath -WorkingDirectory $workingDirectory -PassThru
    Start-Sleep -Seconds 3
    if ($process.HasExited) {
        throw "$name exited during smoke test with code $($process.ExitCode)."
    }
    $process.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 700
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}

function Invoke-ExitSmokeTest([string]$exePath, [string]$workingDirectory, [string]$name, [string]$argumentList) {
    $process = Start-Process -FilePath $exePath -ArgumentList $argumentList -WorkingDirectory $workingDirectory -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(15000)) {
        Stop-Process -Id $process.Id -Force
        throw "$name smoke test timed out."
    }
    if ($process.ExitCode -ne 0) {
        throw "$name smoke test failed with code $($process.ExitCode)."
    }
}

function Assert-NativeCommandSucceeded([string]$name) {
    if ($LASTEXITCODE -ne 0) {
        throw "$name failed with exit code $LASTEXITCODE."
    }
}

Remove-DirectoryIfExists $dist
Clear-DirectoryIfExists $setupDir
Remove-FileIfExists $payloadPath
Remove-FileIfExists $payloadHashPath
New-Item -ItemType Directory -Force -Path $publishDir, $protectedDir, $setupDir, $uninstallerPublishDir, $installerPublishDir | Out-Null

dotnet publish $appProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publishDir `
    /p:PublishSingleFile=false `
    /p:PublishReadyToRun=false `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:Version=$Version
Assert-NativeCommandSucceeded "LittleFish app publish"

Copy-Item -Path (Join-Path $publishDir "*") -Destination $protectedDir -Recurse -Force

if (-not $SkipObfuscation) {
    New-Item -ItemType Directory -Force -Path $obfuscatedDir | Out-Null
    $config = @"
<?xml version="1.0" encoding="utf-8" ?>
<Obfuscator>
  <Var name="InPath" value="$protectedDir" />
  <Var name="OutPath" value="$obfuscatedDir" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="HideStrings" value="true" />
  <Var name="ReuseNames" value="true" />
  <Var name="SuppressIldasm" value="true" />
  <Module file="`$(InPath)\LittleFish.dll">
    <SkipType name="LittleFish.App.App" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="LittleFish.App.MainWindow" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="LittleFish.App.SettingsWindow" skipMethods="true" skipFields="true" skipProperties="true" />
  </Module>
</Obfuscator>
"@
    Set-Content -LiteralPath $obfuscarConfig -Value $config -Encoding UTF8

    $obfuscar = Find-Obfuscar
    & $obfuscar $obfuscarConfig
    Assert-NativeCommandSucceeded "Obfuscar"

    $obfuscatedDll = Join-Path $obfuscatedDir "LittleFish.dll"
    if (-not (Test-Path $obfuscatedDll)) {
        throw "Obfuscated DLL was not generated: $obfuscatedDll"
    }
    Copy-Item -LiteralPath $obfuscatedDll -Destination (Join-Path $protectedDir "LittleFish.dll") -Force
}

dotnet publish $uninstallerProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $uninstallerPublishDir `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=true `
    /p:PublishReadyToRun=false `
    /p:EnableCompressionInSingleFile=true `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:Version=$Version
Assert-NativeCommandSucceeded "LittleFish uninstaller publish"

$builtUninstaller = Join-Path $uninstallerPublishDir "LittleFish_Uninstall.exe"
if (-not (Test-Path $builtUninstaller)) {
    throw "Lightweight uninstaller was not generated: $builtUninstaller"
}

$protectedUninstaller = Join-Path $protectedDir "LittleFish_Uninstall.exe"
Copy-Item -LiteralPath $builtUninstaller -Destination $protectedUninstaller -Force

$smokeExe = Join-Path $protectedDir "LittleFish.exe"
Invoke-SmokeTest $smokeExe $protectedDir "Protected app"
Invoke-ExitSmokeTest $protectedUninstaller $protectedDir "Lightweight uninstaller" "--package-smoke-test"
$settings = Join-Path $protectedDir "settings.json"
Remove-FileIfExists $settings

Compress-Archive -Path (Join-Path $protectedDir "*") -DestinationPath $payloadPath -CompressionLevel Optimal -Force
$payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
Set-Content -LiteralPath $payloadHashPath -Value $payloadHash -Encoding Ascii -NoNewline

dotnet publish $installerProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $installerPublishDir `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:PublishReadyToRun=false `
    /p:EnableCompressionInSingleFile=true `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:Version=$Version
Assert-NativeCommandSucceeded "LittleFish installer publish"

$builtInstaller = Join-Path $installerPublishDir "LittleFish_Setup.exe"
if (-not (Test-Path $builtInstaller)) {
    throw "Custom installer was not generated: $builtInstaller"
}

Invoke-ExitSmokeTest $builtInstaller $installerPublishDir "Single-file installer" "--package-smoke-test"

$finalInstaller = Join-Path $setupDir "LittleFish_Setup_v$Version.exe"
Copy-Item -LiteralPath $builtInstaller -Destination $finalInstaller -Force
$finalInstallerHash = (Get-FileHash -LiteralPath $finalInstaller -Algorithm SHA256).Hash
Set-Content -LiteralPath "$finalInstaller.sha256" -Value $finalInstallerHash -Encoding Ascii -NoNewline

$uninstallerMiB = (Get-Item -LiteralPath $builtUninstaller).Length / 1MB
$installedMiB = (Get-ChildItem -LiteralPath $protectedDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
$installerMiB = (Get-Item -LiteralPath $finalInstaller).Length / 1MB
if ($uninstallerMiB -gt 20) {
    throw "Uninstaller size regression: $([math]::Round($uninstallerMiB, 2)) MiB exceeds 20 MiB."
}
if ($installedMiB -gt 175) {
    throw "Installed payload size regression: $([math]::Round($installedMiB, 2)) MiB exceeds 175 MiB."
}
if ($installerMiB -gt 182.5) {
    throw "Installer size target missed: $([math]::Round($installerMiB, 2)) MiB exceeds the 20% reduction threshold."
}

Remove-FileIfExists $payloadPath
Remove-FileIfExists $payloadHashPath

if (-not $KeepBuildArtifacts) {
    Remove-DirectoryIfExists $dist
}

Write-Host "Installer created: $finalInstaller"
Write-Host "Installer SHA-256: $finalInstallerHash"
Write-Host "Installer size: $([math]::Round($installerMiB, 2)) MiB"
Write-Host "Installed payload size: $([math]::Round($installedMiB, 2)) MiB"
Write-Host "Lightweight uninstaller size: $([math]::Round($uninstallerMiB, 2)) MiB"
