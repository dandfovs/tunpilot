# Скачивает ядра sing-box и Xray (официальные выпуски на GitHub).
# Downloads the sing-box and Xray cores (official GitHub releases).
#   x64   -> TunPilot\cores        (по умолчанию / default)
#   arm64 -> TunPilot\cores-arm64  (для сборки под Windows на ARM / for the Windows on ARM build)
# Запуск / run:  powershell -ExecutionPolicy Bypass -File get-cores.ps1 [-Arch arm64]
param(
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$SingBox = '1.14.4',
    [string]$Xray = '26.11.9'
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$cores = Join-Path $PSScriptRoot ($(if ($Arch -eq 'arm64') { 'TunPilot\cores-arm64' } else { 'TunPilot\cores' }))
$sbAsset = if ($Arch -eq 'arm64') { "sing-box-$SingBox-windows-arm64.zip" } else { "sing-box-$SingBox-windows-amd64.zip" }
$xrAsset = if ($Arch -eq 'arm64') { 'Xray-windows-arm64-v8a.zip' } else { 'Xray-windows-64.zip' }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('tunpilot-cores-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Zip([string]$url, [string]$name) {
    $zip = Join-Path $tmp "$name.zip"
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    $dir = Join-Path $tmp $name
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dir)
    return $dir
}

try {
    # sing-box: архив содержит папку sing-box-<версия>-windows-<arch>
    $sb = Get-Zip "https://github.com/SagerNet/sing-box/releases/download/v$SingBox/$sbAsset" 'sing-box'
    $sbRoot = Split-Path (Get-ChildItem $sb -Recurse -Filter 'sing-box.exe' | Select-Object -First 1).FullName
    $sbDst = Join-Path $cores 'sing-box'
    New-Item -ItemType Directory -Force $sbDst | Out-Null
    Copy-Item (Join-Path $sbRoot '*') $sbDst -Recurse -Force

    # Xray: проверяем SHA2-256 из файла .dgst
    $xrUrl = "https://github.com/XTLS/Xray-core/releases/download/v$Xray/$xrAsset"
    $xr = Get-Zip $xrUrl 'xray'
    $dgst = (Invoke-WebRequest "$xrUrl.dgst" -UseBasicParsing).Content
    $expected = ($dgst -split "`n" | Where-Object { $_ -match '^SHA2-256' } | Select-Object -First 1) -replace '.*=\s*', ''
    $actual = (Get-FileHash (Join-Path $tmp 'xray.zip') -Algorithm SHA256).Hash
    if ($expected -and $actual -ne $expected.Trim().ToUpper()) { throw "Xray checksum mismatch" }
    $xrDst = Join-Path $cores 'xray'
    New-Item -ItemType Directory -Force $xrDst | Out-Null
    Copy-Item (Join-Path $xr '*') $xrDst -Recurse -Force

    Write-Host "OK ($Arch): sing-box $SingBox, Xray $Xray -> $cores" -ForegroundColor Green
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
