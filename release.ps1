#requires -Version 5.1
<#
.SYNOPSIS
    Builds the Soffritto Setup.exe with Velopack and, on request, uploads it to GitHub Releases.

.DESCRIPTION
    The package contains:
      - Soffritto.exe, self-contained: it runs on any Windows x64 without a .NET runtime;
      - Soffritto.Mcp.exe, the MCP server, next to the exe and on the same runtime;
      - vsix\Soffritto.Vsix.vsix, the Visual Studio extension.
    MCP and VSIX are installed from the Components tab of the app's setup.

    Nothing in this script touches the development builds: publish output goes to artifacts\pack.
    No configuration file goes into the package:
    on first launch the setup starts empty.

    The packaged exe updates itself from the releases of -Repository, which is baked into the
    assembly (AssemblyMetadata UpdateRepository): there is no address in the code.

.PARAMETER Repository
    The GitHub repository of the releases, e.g. https://github.com/name/repo. Empty = the origin remote.

.PARAMETER Upload
    Uploads the release to GitHub. Without -Publish it stays a draft to publish from the website.
    The token comes from GITHUB_TOKEN or from "gh auth token".

.PARAMETER Publish
    With -Upload, publishes the release right away instead of leaving it as a draft.

.PARAMETER SkipVsix
    Package without the extension, for a machine without the Visual Studio workload.
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

# Different from the data folder %LOCALAPPDATA%\Soffritto: Velopack installs into %LOCALAPPDATA%\<packId>,
# and with the same name the program and its configuration would end up in the same folder.
$packId    = 'Soffritto.App'
$title     = 'Soffritto'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$pack      = Join-Path $artifacts 'pack'
$releases  = Join-Path $artifacts 'releases'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    Write-Host "== $what" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

if (-not $Repository) {
    $Repository = (git remote get-url origin).Trim() -replace '\.git$', ''
}
if ($Repository -notmatch '^https://github\.com/[^/]+/[^/]+$') {
    throw "Invalid repository: '$Repository'. Expected https://github.com/<owner>/<repo>."
}

$version = ([xml](Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version not found in Directory.Build.props.' }

Write-Host "Version    : $version"
Write-Host "Repository : $Repository"

if (Test-Path $pack) { Remove-Item $pack -Recurse -Force }
New-Item -ItemType Directory -Force $pack, $releases | Out-Null

Invoke-Checked 'Restore vpk' { dotnet tool restore }

Invoke-Checked 'Publish the exe' {
    # A folder, not a single file: self-contained, WPF cannot load DirectWriteForwarder.dll (C++/CLI)
    # from the bundle and the exe exits with BadImageFormatException. The Velopack package is already an archive.
    dotnet publish src\Soffritto.Desktop -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:UpdateRepository=$Repository -o $pack --nologo -v minimal
}

$mcpPublish = Join-Path $artifacts 'mcp-publish'
if (Test-Path $mcpPublish) { Remove-Item $mcpPublish -Recurse -Force }

Invoke-Checked 'Publish the MCP server' {
    # A folder, not a single file: a 90 MB bundle can take more than a minute to start (the
    # antivirus scans it on every launch) and the MCP client drops the connection first (Claude
    # Code waits 30 seconds). As a folder it starts in a few seconds.
    dotnet publish src\Soffritto.Mcp -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -o $mcpPublish --nologo -v minimal
}

# The MCP server sits next to Soffritto.exe and uses its runtime: in a folder of its own the .NET
# runtime would end up in the package twice (about 90 MB). Of the shared files the exe's copy is
# kept, and it must have an equal or higher assembly version: otherwise the MCP server would not start.
Write-Host '== MCP server next to the exe' -ForegroundColor Cyan
foreach ($file in Get-ChildItem $mcpPublish -File) {
    $target = Join-Path $pack $file.Name
    if (-not (Test-Path $target)) {
        Copy-Item $file.FullName $target
        continue
    }
    if ($file.Extension -ne '.dll') { continue }
    try {
        $mine = [Reflection.AssemblyName]::GetAssemblyName($file.FullName).Version
        $kept = [Reflection.AssemblyName]::GetAssemblyName($target).Version
    } catch [BadImageFormatException] {
        continue
    }
    if ($kept -lt $mine) {
        throw "$($file.Name): the exe ships $kept, the MCP server needs $mine."
    }
}
Remove-Item $mcpPublish -Recurse -Force

if (-not $SkipVsix) {
    Invoke-Checked 'Build the VSIX' {
        dotnet build src\Soffritto.Vsix -c Release --nologo -v minimal
    }
    $vsix = Get-ChildItem (Join-Path $PSScriptRoot 'src\Soffritto.Vsix\bin\Release') -Recurse -Filter 'Soffritto.Vsix.vsix' -File |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $vsix) { throw 'Soffritto.Vsix.vsix not found after the build.' }
    New-Item -ItemType Directory -Force (Join-Path $pack 'vsix') | Out-Null
    Copy-Item $vsix.FullName (Join-Path $pack 'vsix\Soffritto.Vsix.vsix')
}

# The libraries' XML documentation files are of no use to whoever installs.
Get-ChildItem $pack -Recurse -Include '*.pdb', '*.xml' -File | Remove-Item -Force

$token = $null
if ($Upload) {
    $token = $env:GITHUB_TOKEN
    if (-not $token) { $token = (gh auth token 2>$null) }
    if (-not $token) { throw '-Upload needs GITHUB_TOKEN or a gh login (gh auth login).' }

    # vpk needs the latest published release to generate the delta package.
    Invoke-Checked 'Download the latest release' {
        dotnet vpk download github --repoUrl $Repository --token $token -o $releases
    }
}

Invoke-Checked 'Velopack package' {
    dotnet vpk pack -u $packId -v $version -p $pack -e Soffritto.exe -r win-x64 `
        --packTitle $title -i src\Soffritto.Desktop\app.ico -o $releases
}

if ($Upload) {
    $uploadArgs = @('vpk', 'upload', 'github', '--repoUrl', $Repository, '--token', $token, '-o', $releases,
        '--releaseName', "$title $version", '--tag', "v$version")
    if ($Publish) { $uploadArgs += '--publish' }
    Invoke-Checked 'Upload to GitHub' { dotnet @uploadArgs }
}

Write-Host ''
Write-Host "Setup      : $(Join-Path $releases "$packId-win-Setup.exe")" -ForegroundColor Green
