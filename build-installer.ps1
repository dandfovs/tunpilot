# Сборка установщика TunPilot: dist\TunPilot-Setup-<версия>.exe
# Запуск:  powershell -ExecutionPolicy Bypass -File C:\TunPilot\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'TunPilot'
$setup = Join-Path $root 'Setup'
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'app'

$version = ([xml](Get-Content (Join-Path $app 'TunPilot.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "TunPilot $version" -ForegroundColor Green

# 0. Ядер нет (свежий клон репозитория) — скачать
if (-not (Test-Path (Join-Path $app 'cores\sing-box\sing-box.exe')) -or -not (Test-Path (Join-Path $app 'cores\xray\xray.exe'))) {
    & (Join-Path $root 'get-cores.ps1')
}

# 1. Программа: один exe со встроенной средой .NET (на чужом ПК ничего доустанавливать не нужно) + ядра рядом
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish $app -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $stage -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "Сборка программы не удалась" }
# лишнее из комплекта Xray
Get-ChildItem $stage -Recurse -Include 'xray_no_window.*', 'README.md' | Remove-Item -Force

# 2. Маленький Uninstall.exe: тот же проект, но без встроенного архива (иначе он весил бы как установщик)
$payload = Join-Path $setup 'payload.zip'
if (Test-Path $payload) { Remove-Item $payload -Force }
$proj = Join-Path $setup 'TunPilotSetup.csproj'
$text = [IO.File]::ReadAllText($proj, [Text.Encoding]::UTF8) -replace '<Version>[^<]*</Version>', "<Version>$version</Version>"
[IO.File]::WriteAllText($proj, $text, (New-Object Text.UTF8Encoding $false))
dotnet build $setup -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "Сборка программы удаления не удалась" }
Copy-Item (Join-Path $setup 'bin\Release\net48\TunPilotSetup.exe') (Join-Path $stage 'Uninstall.exe') -Force

# 3. Архив с файлами программы — встраивается в установщик
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)

# 4. Установщик (.NET Framework 4.8 — есть в любой Windows 10/11). Версия = версия программы (выставлена выше).
# --no-incremental: архив поменялся, ресурс нужно встроить заново
dotnet build $setup -c Release -v q -nologo --no-incremental
if ($LASTEXITCODE -ne 0) { throw "Сборка установщика не удалась" }

$out = Join-Path $dist "TunPilot-Setup-$version.exe"
Copy-Item (Join-Path $setup 'bin\Release\net48\TunPilotSetup.exe') $out -Force
Remove-Item $payload -Force
$mb = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host "Готово: $out ($mb МБ)" -ForegroundColor Green
