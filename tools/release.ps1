# Publică o versiune nouă pe GitHub: construiește MSI-ul, creează tag-ul v<versiune> și release-ul cu installer-ul.
# Versiunea se ia din Directory.Build.props (măriți-o înainte). Necesită GitHub CLI autentificat (gh auth login).
#   .\tools\release.ps1 -Notes "Ce s-a schimbat"
param([string]$Notes = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = 'C:\Program Files\GitHub CLI\gh.exe' }
if (-not (Test-Path $gh)) { throw 'GitHub CLI nu este instalat (winget install --id GitHub.cli).' }

# Installer-ul public folosește relay-ul încorporat (fără el, utilizatorii ar trebui să configureze singuri un relay).
if (-not (Test-Path (Join-Path $root 'src\KodDesk.Core\RelayConfig.Local.cs'))) {
    throw 'Relay-ul nu este încorporat: rulați tools\set-relay.ps1 înainte de release.'
}

if (git status --porcelain) { throw 'Există modificări necomise. Faceți commit înainte de release.' }

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$tag = "v$version"
& (Join-Path $PSScriptRoot 'build-installer.ps1')
$msi = Join-Path $root "publish\KodDesk-$version-x64.msi"

git tag -a $tag -m "KodDesk $version"
git push origin HEAD $tag

$body = "## KodDesk $version`n`nDownload ``KodDesk-$version-x64.msi`` below and run it. It installs over a previous version; your ID and password are kept.`n`n$Notes"
& $gh release create $tag $msi --title "KodDesk $version" --notes $body
