# Publishes a new GameDeck version on GitHub, which installed copies then offer in Settings → About.
#   ./tools/release.ps1 -Version 0.2.0 -Notes release-notes.md
# Steps: sets the version, builds and tests everything, builds the installer, writes its SHA-256 file,
# tags the commit and creates the GitHub release with both files (needs the GitHub CLI, `gh auth login`).
# Commit your changes first: the release is made from the current commit.
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$Notes
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path $Notes)) { throw "Notes file not found: $Notes" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI not found: winget install GitHub.cli, then gh auth login' }
if (git -C $root status --porcelain) { throw 'Commit or stash your changes first.' }

$project = "$root/src/GameDeck.App/GameDeck.App.csproj"
$text = Get-Content $project -Raw
$current = [regex]::Match($text, '<Version>([^<]+)</Version>').Groups[1].Value
if ([version]$Version -le [version]$current) { throw "Version $Version must be newer than $current." }
Set-Content $project ($text -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>") -NoNewline -Encoding utf8

& "$root/build.ps1" -Installer
$installer = "$root/dist/GameDeck-Setup-$Version.exe"
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content "$installer.sha256" "$hash  GameDeck-Setup-$Version.exe" -Encoding ascii

git -C $root commit -am "GameDeck $Version"
git -C $root tag "v$Version"
git -C $root push origin HEAD "v$Version"
gh release create "v$Version" $installer "$installer.sha256" --repo kalinvisuals/GameDeck --title "GameDeck $Version" --notes-file $Notes
Write-Host "Released GameDeck $Version. Installed copies will offer it within a few hours, or at once with Settings → About → Check now."
