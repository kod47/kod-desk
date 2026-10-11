# Descarcă DLL-urile FFmpeg (build shared, versiunea 9.0) în third_party/ffmpeg.
# Versiunea majoră trebuie să corespundă pachetului NuGet FFmpeg.AutoGen (9.x).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'third_party\ffmpeg'
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip'

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'ffmpeg.zip'
    Write-Host "Descarc $url ..."
    Invoke-WebRequest -Uri $url -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $tmp
    $bin = Get-ChildItem -Path $tmp -Directory -Recurse -Filter bin | Select-Object -First 1
    New-Item -ItemType Directory -Force $dest | Out-Null
    # Doar bibliotecile folosite: avcodec (depinde de avutil și swresample) și swscale.
    foreach ($lib in 'avcodec-*.dll', 'avutil-*.dll', 'swresample-*.dll', 'swscale-*.dll') {
        Copy-Item -Path (Join-Path $bin.FullName $lib) -Destination $dest -Force
    }
    Write-Host "FFmpeg copiat în $dest"
}
finally {
    Remove-Item -Recurse -Force $tmp
}
