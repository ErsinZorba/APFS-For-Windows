<#
.SYNOPSIS
  Kurulabilir sürümü derler: publish\APFS for Windows\ (apfs.exe, apfs-service.exe, apfs-tray.exe ve kütüphaneler).
  .NET çalışma zamanı programla birlikte gelir (self-contained): kullanıcının ayrıca .NET kurması gerekmez. Üç program aynı
  klasöre derlenir, çalışma zamanı dosyaları tek kopya kalır.
  -Runtime win-arm64: ARM Windows için (ör. Apple işlemcili Mac'te VMware Fusion) → publish\APFS for Windows (arm64)\.
  Kurulum sihirbazı (build-installer.ps1) şimdilik yalnız varsayılan win-x64 klasörünü paketler.
#>
param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out = Join-Path $root $(if ($Runtime -eq 'win-x64') { 'publish\APFS for Windows' } else { 'publish\APFS for Windows (arm64)' })
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
foreach ($p in 'src\Apfs.Cli', 'src\Apfs.Service', 'src\Apfs.Tray') {
    & dotnet publish (Join-Path $root $p) -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "derleme başarısız: $p" }
}
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY_NOTICES.md') $out
if ($Runtime -eq 'win-x64') { Copy-Item (Join-Path $root 'install.ps1'), (Join-Path $root 'uninstall.ps1') (Split-Path -Parent $out) }
$size = (Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum
"Hazır: $out ($([math]::Round($size / 1MB, 1)) MB)"
