#requires -Version 5.1
<#
.SYNOPSIS
    Crea il Setup.exe di Soffritto con Velopack e, a richiesta, lo carica sulle GitHub Releases.

.DESCRIPTION
    Il pacchetto contiene:
      - Soffritto.exe self-contained: parte su qualunque Windows x64 senza runtime .NET;
      - mcp\Soffritto.Mcp.exe, il server MCP, anche lui self-contained, nella sua cartella;
      - vsix\Soffritto.Vsix.vsix, l'estensione di Visual Studio.
    MCP e VSIX si installano dalla scheda Componenti del setup dell'exe.

    Niente di questo script tocca le build di sviluppo: i publish vanno in artifacts\pack. Nessun
    file di configurazione entra nel pacchetto:
    al primo avvio il setup parte vuoto.

    L'exe del pacchetto si aggiorna dalle release di -Repository, che finisce nell'assembly
    (AssemblyMetadata UpdateRepository): nel codice non c'e' nessun indirizzo.

.PARAMETER Repository
    Il repository GitHub delle release, es. https://github.com/nome/repo. Vuoto = il remote origin.

.PARAMETER Upload
    Carica la release su GitHub. Senza -Publish resta una bozza da pubblicare dal sito.
    Il token viene da GITHUB_TOKEN o da «gh auth token».

.PARAMETER Publish
    Con -Upload pubblica subito la release invece di lasciarla in bozza.

.PARAMETER SkipVsix
    Pacchetto senza l'estensione, per una macchina senza il workload di Visual Studio.
#>
[CmdletBinding()]
param(
    [string]$Repository,
    [switch]$Upload,
    [switch]$Publish,
    [switch]$SkipVsix
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Diverso dalla cartella dati %LOCALAPPDATA%\Soffritto: Velopack installa in %LOCALAPPDATA%\<packId>,
# e con lo stesso nome programma e configurazione finirebbero nella stessa cartella.
$packId    = 'Soffritto.App'
$title     = 'Soffritto'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$pack      = Join-Path $artifacts 'pack'
$releases  = Join-Path $artifacts 'releases'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    Write-Host "== $what" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what non riuscito (exit code $LASTEXITCODE)." }
}

if (-not $Repository) {
    $Repository = (git remote get-url origin).Trim() -replace '\.git$', ''
}
if ($Repository -notmatch '^https://github\.com/[^/]+/[^/]+$') {
    throw "Repository non valido: '$Repository'. Atteso https://github.com/<proprietario>/<repo>."
}

$version = ([xml](Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version non trovata in Directory.Build.props.' }

Write-Host "Versione   : $version"
Write-Host "Repository : $Repository"

if (Test-Path $pack) { Remove-Item $pack -Recurse -Force }
New-Item -ItemType Directory -Force $pack, $releases | Out-Null

Invoke-Checked 'Restore di vpk' { dotnet tool restore }

Invoke-Checked 'Publish dell''exe' {
    # Cartella e non file singolo: self-contained, WPF non carica dal bundle DirectWriteForwarder.dll
    # (C++/CLI) e l'exe esce con BadImageFormatException. Il pacchetto di Velopack e' gia' un archivio.
    dotnet publish src\Soffritto.Desktop -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:UpdateRepository=$Repository -o $pack --nologo -v minimal
}

Invoke-Checked 'Publish del server MCP' {
    # Cartella e non file singolo: un bundle da 90 MB puo' metterci piu' di un minuto a partire
    # (antivirus che lo analizza a ogni avvio) e il client MCP chiude la connessione prima
    # (Claude Code aspetta 30 secondi). A cartella parte in pochi secondi.
    dotnet publish src\Soffritto.Mcp -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -o (Join-Path $pack 'mcp') --nologo -v minimal
}

if (-not $SkipVsix) {
    Invoke-Checked 'Build della VSIX' {
        dotnet build src\Soffritto.Vsix -c Release --nologo -v minimal
    }
    $vsix = Get-ChildItem (Join-Path $PSScriptRoot 'src\Soffritto.Vsix\bin\Release') -Recurse -Filter 'Soffritto.Vsix.vsix' -File |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $vsix) { throw 'Soffritto.Vsix.vsix non trovato dopo la build.' }
    New-Item -ItemType Directory -Force (Join-Path $pack 'vsix') | Out-Null
    Copy-Item $vsix.FullName (Join-Path $pack 'vsix\Soffritto.Vsix.vsix')
}

# I file di documentazione XML delle librerie non servono a chi installa.
Get-ChildItem $pack -Recurse -Include '*.pdb', '*.xml' -File | Remove-Item -Force

$token = $null
if ($Upload) {
    $token = $env:GITHUB_TOKEN
    if (-not $token) { $token = (gh auth token 2>$null) }
    if (-not $token) { throw 'Per -Upload serve GITHUB_TOKEN o una login di gh (gh auth login).' }

    # L'ultima release pubblicata serve a vpk per generare il pacchetto delta.
    Invoke-Checked 'Download dell''ultima release' {
        dotnet vpk download github --repoUrl $Repository --token $token -o $releases
    }
}

Invoke-Checked 'Pacchetto Velopack' {
    dotnet vpk pack -u $packId -v $version -p $pack -e Soffritto.exe -r win-x64 `
        --packTitle $title -i src\Soffritto.Desktop\app.ico -o $releases
}

if ($Upload) {
    $uploadArgs = @('vpk', 'upload', 'github', '--repoUrl', $Repository, '--token', $token, '-o', $releases,
        '--releaseName', "$title $version", '--tag', "v$version")
    if ($Publish) { $uploadArgs += '--publish' }
    Invoke-Checked 'Upload su GitHub' { dotnet @uploadArgs }
}

Write-Host ''
Write-Host "Setup      : $(Join-Path $releases "$packId-win-Setup.exe")" -ForegroundColor Green
