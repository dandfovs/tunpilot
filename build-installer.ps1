# Сборка установщиков TunPilot / Build TunPilot installers:
#   dist\TunPilot-Setup-<версия>.exe        — обычные ПК (x64)
#   dist\TunPilot-Setup-<версия>-arm64.exe  — Windows на ARM (Snapdragon и т. п.)
# Запуск / run:  powershell -ExecutionPolicy Bypass -File build-installer.ps1 [-Arch x64|arm64|all]
param([ValidateSet('all', 'x64', 'arm64')][string]$Arch = 'all')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'TunPilot'
$setup = Join-Path $root 'Setup'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$version = ([xml](Get-Content (Join-Path $app 'TunPilot.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "TunPilot $version" -ForegroundColor Green
$archs = if ($Arch -eq 'all') { @('x64', 'arm64') } else { @($Arch) }

# версия установщика = версия программы (читаем/пишем явно в UTF-8, иначе PowerShell 5.1 портит кириллицу)
$proj = Join-Path $setup 'TunPilotSetup.csproj'
$text = [IO.File]::ReadAllText($proj, [Text.Encoding]::UTF8) -replace '<Version>[^<]*</Version>', "<Version>$version</Version>"
[IO.File]::WriteAllText($proj, $text, (New-Object Text.UTF8Encoding $false))

$payload = Join-Path $setup 'payload.zip'
$archFile = Join-Path $setup 'payload-arch.txt'
foreach ($f in $payload, $archFile) { if (Test-Path $f) { Remove-Item $f -Force } }

# Маленький Uninstall.exe — тот же проект без встроенного архива; один на обе архитектуры (.NET Framework 4.8)
dotnet build $setup -c Release -v q -nologo --no-incremental
if ($LASTEXITCODE -ne 0) { throw "Сборка программы удаления не удалась" }
$uninstaller = Join-Path $dist 'Uninstall.exe'
Copy-Item (Join-Path $setup 'bin\Release\net48\TunPilotSetup.exe') $uninstaller -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($a in $archs) {
    Write-Host "== $a ==" -ForegroundColor Cyan
    $cores = Join-Path $app ($(if ($a -eq 'arm64') { 'cores-arm64' } else { 'cores' }))
    if (-not (Test-Path (Join-Path $cores 'sing-box\sing-box.exe')) -or -not (Test-Path (Join-Path $cores 'xray\xray.exe'))) {
        & (Join-Path $root 'get-cores.ps1') -Arch $a
    }

    # 1. Программа: один exe со встроенной средой .NET под нужный процессор
    $stage = Join-Path $dist "app-$a"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    dotnet publish $app -c Release -r "win-$a" --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -o $stage -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "Сборка программы ($a) не удалась" }
    # ядра: проект кладёт x64-набор из TunPilot\cores — для arm64 заменяем своим
    $stageCores = Join-Path $stage 'cores'
    if (Test-Path $stageCores) { Remove-Item $stageCores -Recurse -Force }
    Copy-Item $cores $stageCores -Recurse
    Get-ChildItem $stage -Recurse -Include 'xray_no_window.*', 'README.md' | Remove-Item -Force
    Copy-Item $uninstaller (Join-Path $stage 'Uninstall.exe') -Force

    # 2. Архив + метка архитектуры — встраиваются в установщик
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)
    [IO.File]::WriteAllText($archFile, $a)

    # 3. Установщик (--no-incremental: архив поменялся, ресурс нужно встроить заново)
    dotnet build $setup -c Release -v q -nologo --no-incremental
    if ($LASTEXITCODE -ne 0) { throw "Сборка установщика ($a) не удалась" }
    $suffix = if ($a -eq 'arm64') { '-arm64' } else { '' }
    $out = Join-Path $dist "TunPilot-Setup-$version$suffix.exe"
    Copy-Item (Join-Path $setup 'bin\Release\net48\TunPilotSetup.exe') $out -Force
    foreach ($f in $payload, $archFile) { Remove-Item $f -Force }
    $mb = [math]::Round((Get-Item $out).Length / 1MB, 1)
    Write-Host "Готово: $out ($mb МБ)" -ForegroundColor Green
}
Remove-Item $uninstaller -Force
