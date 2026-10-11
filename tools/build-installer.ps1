# Construiește installer-ul: publică aplicația (self-contained, fără simboluri) și generează MSI-ul.
# Rezultat: publish\KodDesk-<versiune>-x64.msi
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$app = Join-Path $root 'publish\app'

if (-not (Test-Path (Join-Path $root 'src\KodDesk.Core\RelayConfig.Local.cs'))) {
    Write-Warning 'Relay-ul nu este încorporat (rulați tools\set-relay.ps1): adresa și cheia vor trebui introduse în Setări.'
}
if (-not (Test-Path (Join-Path $root 'third_party\ffmpeg\avcodec-*.dll'))) {
    throw 'Lipsesc DLL-urile FFmpeg: rulați tools\get-ffmpeg.ps1.'
}

Write-Host "== Publicare KodDesk $version"
if (Test-Path $app) { Remove-Item -Recurse -Force $app }
dotnet publish src\KodDesk.App -c Release -r win-x64 --self-contained `
    -p:DebugType=None -p:DebugSymbols=false -o $app -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publicarea a eșuat.' }

Write-Host "== Construire MSI"
dotnet build installer\KodDesk.Installer.wixproj -c Release -p:AppPublishDir=$app -o (Join-Path $root 'publish') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Construirea MSI a eșuat.' }

$msi = Join-Path $root "publish\KodDesk-$version-x64.msi"
Write-Host ("== Gata: {0} ({1:N0} MB)" -f $msi, ((Get-Item $msi).Length / 1MB))
