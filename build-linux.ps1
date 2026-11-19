# Сборка пакетов TunPilot для Linux (.deb, .rpm, .tar.gz) на Windows через WSL.
# Builds TunPilot Linux packages (.deb, .rpm, .tar.gz) on Windows via WSL.
#   .deb и .tar.gz собираются в Ubuntu (dpkg-deb), .rpm — в Fedora (rpmbuild).
# Запуск / run:  powershell -ExecutionPolicy Bypass -File build-linux.ps1 [-Arch x64,arm64] [-Deb Ubuntu-24.04] [-Rpm FedoraLinux-44]
param(
    [string[]]$Arch = @('x64', 'arm64'),
    [string]$Deb = 'Ubuntu-24.04',
    [string]$Rpm = 'FedoraLinux-44'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'TunPilot.Linux\TunPilot.Linux.csproj'
$version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $root 'dist'

function ConvertTo-WslPath([string]$p) { '/mnt/' + $p.Substring(0, 1).ToLower() + ($p.Substring(2) -replace '\\', '/') }

foreach ($a in $Arch) {
    $cores = Join-Path $root "TunPilot.Linux\cores-$a"
    if (-not (Test-Path (Join-Path $cores 'sing-box\sing-box'))) { & (Join-Path $root 'get-cores.ps1') -OS linux -Arch $a }

    $app = Join-Path $out "linux-$a\app"
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    Write-Host "== publish linux-$a" -ForegroundColor Cyan
    dotnet publish $proj -c Release -r "linux-$a" --self-contained -o $app -p:DebugType=none | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    Copy-Item $cores (Join-Path $app 'cores') -Recurse

    $script = ConvertTo-WslPath (Join-Path $root 'TunPilot.Linux\packaging\build-packages.sh')
    $appW = ConvertTo-WslPath $app
    $outW = ConvertTo-WslPath $out
    $pkgW = ConvertTo-WslPath (Join-Path $root 'TunPilot.Linux\packaging')
    Write-Host "== deb + tar.gz ($Deb)" -ForegroundColor Cyan
    wsl -d $Deb -- bash -c "tr -d '\r' < '$script' > /tmp/tp-build.sh && TP_PACKAGING='$pkgW' bash /tmp/tp-build.sh '$appW' $a $version '$outW' deb,tar"
    if ($LASTEXITCODE -ne 0) { throw "deb build failed" }
    Write-Host "== rpm ($Rpm)" -ForegroundColor Cyan
    wsl -d $Rpm -- bash -c "tr -d '\r' < '$script' > /tmp/tp-build.sh && TP_PACKAGING='$pkgW' bash /tmp/tp-build.sh '$appW' $a $version '$outW' rpm"
    if ($LASTEXITCODE -ne 0) { throw "rpm build failed (Fedora: sudo dnf install rpm-build)" }
}
Get-ChildItem $out -File | Where-Object { $_.Name -like 'tunpilot*' } | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
