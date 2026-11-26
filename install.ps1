#requires -Version 5.1
<#
.SYNOPSIS
    Builds and installs the "Soffritto" extension on Visual Studio 2026.

.DESCRIPTION
    On VS 2026, extensions with ExtensionType="VisualStudio.Extensibility" (out-of-process model)
    CANNOT be installed by invoking VSIXInstaller.exe from the command line: it always fails with

        RequiresInstallerException: Requested operation must be completed using Visual Studio Installer.
        Multiple targets are currently not supported for this operation.

    Verified: the error is the same with /instanceIds, with /admin, and with a manifest reduced to a
    single InstallationTarget. It is neither an argument problem nor an elevation problem.

    Double-clicking the .vsix works instead, because it does not go through VSIXInstaller: it goes
    through VSLauncher.exe, which routes to the right installer. This script does the same.

    To iterate during development the best way is still F5 on the .Vsix project, which uses the
    SDK deployment and starts an experimental instance without touching the working installation.

.PARAMETER SkipBuild
    Installs the .vsix already in bin\Release without rebuilding.

.PARAMETER UninstallOnly
    Removes the extension without reinstalling it.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$UninstallOnly
)

$ErrorActionPreference = 'Stop'

$extensionId = 'Soffritto.5f1c9a2e'
$projectPath = Join-Path $PSScriptRoot 'src\Soffritto.Vsix\Soffritto.Vsix.csproj'
$releaseRoot = Join-Path $PSScriptRoot 'src\Soffritto.Vsix\bin\Release'
$launcher    = Join-Path ${env:ProgramFiles(x86)} 'Common Files\Microsoft Shared\MSEnv\VSLauncher.exe'

# The RuntimeIdentifier adds a level to the output folder (net10.0-windows8.0\win-x64), so we
# search for the package instead of rebuilding its path by hand.
function Find-Vsix {
    return Get-ChildItem $releaseRoot -Recurse -Filter 'Soffritto.Vsix.vsix' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Get-VsixInstallerMessage {
    param([int]$Code)

    switch ($Code) {
        0     { "completed" }
        1001  { "extension already installed at this version" }
        1002  { "extension not installed (nothing to remove)" }
        1003  { "incompatible architecture or target" }
        1004  { "access denied: elevation required" }
        2003  { "file in use: is Visual Studio still open?" }
        2004  { "operation cancelled" }
        default { "exit code $Code" }
    }
}

$running = Get-Process devenv -ErrorAction SilentlyContinue
if ($running) {
    throw "Visual Studio is running (PID $($running.Id -join ', ')). Close it and run the script again."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found: Visual Studio does not appear to be installed."
}

$instance = & $vswhere -latest -prerelease -format json | ConvertFrom-Json | Select-Object -First 1
if (-not $instance) {
    throw "No Visual Studio instance found."
}

Write-Host "Target instance: $($instance.displayName) $($instance.installationVersion)" -ForegroundColor Cyan

# Unlike installation, removal goes through the CLI without problems:
# the RequiresInstallerException exception lives only in the GetInstallableData path.
$vsixInstaller = Join-Path $instance.installationPath 'Common7\IDE\VSIXInstaller.exe'
if (Test-Path $vsixInstaller) {
    Write-Host "Removing the previous version..." -ForegroundColor Cyan
    $uninstall = Start-Process -FilePath $vsixInstaller -Wait -PassThru -ArgumentList @(
        "/uninstall:$extensionId"
        "/instanceIds:$($instance.instanceId)"
        '/quiet'
    )
    Write-Host "  ${extensionId}: $(Get-VsixInstallerMessage -Code $uninstall.ExitCode)" -ForegroundColor DarkGray
}

if ($UninstallOnly) {
    Write-Host "Removal completed." -ForegroundColor Green
    return
}

if (-not $SkipBuild) {
    Write-Host "Build Release..." -ForegroundColor Cyan
    & dotnet build $projectPath -c Release -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

$vsixPath = Find-Vsix
if (-not $vsixPath) {
    throw "VSIX not found under $releaseRoot. Run again without -SkipBuild."
}

Write-Host "Package        : $vsixPath" -ForegroundColor Cyan

if (-not (Test-Path $launcher)) {
    throw "VSLauncher.exe not found at $launcher. Install manually by double-clicking:`n  $vsixPath"
}

Write-Host ""
Write-Host "Starting the Visual Studio installer..." -ForegroundColor Cyan
Write-Host "  A window opens: confirm the UAC prompt if asked and press Install." -ForegroundColor DarkGray

$install = Start-Process -FilePath $launcher -ArgumentList "`"$vsixPath`"" -Wait -PassThru

Write-Host ""
if ($install.ExitCode -eq 0) {
    Write-Host "Installer finished without errors." -ForegroundColor Green
    Write-Host "Reopen Visual Studio: View > Other Windows > Soffritto" -ForegroundColor Green
}
else {
    Write-Warning "The installer returned exit code $($install.ExitCode)."
    Write-Host "Alternative: double-click" -ForegroundColor Yellow
    Write-Host "  $vsixPath" -ForegroundColor Yellow
    Write-Host "Or, to iterate without installing: open the solution and press F5 on the .Vsix project." -ForegroundColor Yellow
    exit $install.ExitCode
}
