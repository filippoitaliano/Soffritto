#requires -Version 5.1
<#
.SYNOPSIS
    Compila e installa l'estensione "Soffritto" su Visual Studio 2026.

.DESCRIPTION
    Su VS 2026 le estensioni con ExtensionType="VisualStudio.Extensibility" (modello out-of-process)
    NON sono installabili invocando VSIXInstaller.exe da riga di comando: fallisce sempre con

        RequiresInstallerException: Requested operation must be completed using Visual Studio Installer.
        Multiple targets are currently not supported for this operation.

    Verificato: l'errore e' identico con /instanceIds, con /admin, e con un manifest ridotto a un
    solo InstallationTarget. Non e' un problema di argomenti ne' di elevazione.

    Il doppio click sul .vsix invece funziona perche' non passa da VSIXInstaller: passa da
    VSLauncher.exe, che instrada verso l'installer giusto. Questo script fa la stessa cosa.

    Per iterare durante lo sviluppo la via migliore resta F5 sul progetto .Vsix, che usa il
    deployment dell'SDK e avvia un'istanza sperimentale senza toccare l'installazione di lavoro.

.PARAMETER SkipBuild
    Installa il .vsix gia' presente in bin\Release senza ricompilare.

.PARAMETER UninstallOnly
    Rimuove l'estensione senza reinstallarla.
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

# Il RuntimeIdentifier aggiunge un livello alla cartella di output (net10.0-windows8.0\win-x64), quindi
# cerchiamo il pacchetto invece di ricostruirne il percorso a mano.
function Find-Vsix {
    return Get-ChildItem $releaseRoot -Recurse -Filter 'Soffritto.Vsix.vsix' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

function Get-VsixInstallerMessage {
    param([int]$Code)

    switch ($Code) {
        0     { "completato" }
        1001  { "estensione gia installata a questa versione" }
        1002  { "estensione non installata (niente da rimuovere)" }
        1003  { "architettura o target non compatibile" }
        1004  { "accesso negato: serve elevazione" }
        2003  { "file in uso: Visual Studio e ancora aperto?" }
        2004  { "operazione annullata" }
        default { "exit code $Code" }
    }
}

$running = Get-Process devenv -ErrorAction SilentlyContinue
if ($running) {
    throw "Visual Studio e' in esecuzione (PID $($running.Id -join ', ')). Chiudilo e rilancia lo script."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe non trovato: Visual Studio non risulta installato."
}

$instance = & $vswhere -latest -prerelease -format json | ConvertFrom-Json | Select-Object -First 1
if (-not $instance) {
    throw "Nessuna istanza di Visual Studio individuata."
}

Write-Host "Istanza target : $($instance.displayName) $($instance.installationVersion)" -ForegroundColor Cyan

# La rimozione, a differenza dell'installazione, passa dalla CLI senza problemi:
# l'eccezione RequiresInstallerException vive solo nel percorso GetInstallableData.
$vsixInstaller = Join-Path $instance.installationPath 'Common7\IDE\VSIXInstaller.exe'
if (Test-Path $vsixInstaller) {
    Write-Host "Rimozione versione precedente..." -ForegroundColor Cyan
    $uninstall = Start-Process -FilePath $vsixInstaller -Wait -PassThru -ArgumentList @(
        "/uninstall:$extensionId"
        "/instanceIds:$($instance.instanceId)"
        '/quiet'
    )
    Write-Host "  ${extensionId}: $(Get-VsixInstallerMessage -Code $uninstall.ExitCode)" -ForegroundColor DarkGray
}

if ($UninstallOnly) {
    Write-Host "Rimozione completata." -ForegroundColor Green
    return
}

if (-not $SkipBuild) {
    Write-Host "Build Release..." -ForegroundColor Cyan
    & dotnet build $projectPath -c Release -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build fallita." }
}

$vsixPath = Find-Vsix
if (-not $vsixPath) {
    throw "VSIX non trovato sotto $releaseRoot. Rilancia senza -SkipBuild."
}

Write-Host "Pacchetto      : $vsixPath" -ForegroundColor Cyan

if (-not (Test-Path $launcher)) {
    throw "VSLauncher.exe non trovato in $launcher. Installa manualmente con doppio click su:`n  $vsixPath"
}

Write-Host ""
Write-Host "Avvio dell'installer di Visual Studio..." -ForegroundColor Cyan
Write-Host "  Si apre una finestra: conferma l'UAC se richiesto e premi Installa." -ForegroundColor DarkGray

$install = Start-Process -FilePath $launcher -ArgumentList "`"$vsixPath`"" -Wait -PassThru

Write-Host ""
if ($install.ExitCode -eq 0) {
    Write-Host "Installer terminato senza errori." -ForegroundColor Green
    Write-Host "Riapri Visual Studio: Visualizza > Altre finestre > Soffritto" -ForegroundColor Green
}
else {
    Write-Warning "L'installer ha restituito exit code $($install.ExitCode)."
    Write-Host "Alternativa: doppio click su" -ForegroundColor Yellow
    Write-Host "  $vsixPath" -ForegroundColor Yellow
    Write-Host "Oppure, per iterare senza installare: apri la soluzione e premi F5 sul progetto .Vsix." -ForegroundColor Yellow
    exit $install.ExitCode
}
