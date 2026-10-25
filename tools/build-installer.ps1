<#
.SYNOPSIS
  Kurulum sihirbazını derler: publish\APFS-for-Windows-Setup-<sürüm>.exe
.DESCRIPTION
  1. tools\build-release.ps1 ile programı derler (self-contained).
  2. Gömülecek WinFsp kurulum paketini (installer\redist\winfsp-2.1.25156.msi) denetler: dosya yoksa indirme adresini söyler;
     Authenticode imzası geçerli ve NAVIMATICS LLC (WinFsp yazarı) değilse durur.
  3. Sürümü Directory.Build.props'tan okuyup Inno Setup (ISCC.exe) ile installer\APFS-for-Windows.iss'i derler.
  Gereken: Inno Setup 6 (winget install --id JRSoftware.InnoSetup -e).
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 bulunamadı. Kurmak için: winget install --id JRSoftware.InnoSetup -e' }

$msi = Join-Path $root 'installer\redist\winfsp-2.1.25156.msi'
if (-not (Test-Path $msi)) {
    throw "WinFsp kurulum paketi yok: $msi`nİndirin: https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi"
}
$sig = Get-AuthenticodeSignature $msi
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'CN=NAVIMATICS LLC') {
    throw "WinFsp paketinin imzası geçersiz ($($sig.Status)): $msi"
}

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\build-release.ps1')
if ($LASTEXITCODE -ne 0) { throw 'derleme başarısız' }

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Directory.Build.props içinde <Version> yok' }

& $iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\APFS-for-Windows.iss')
if ($LASTEXITCODE -ne 0) { throw 'kurulum derlenemedi' }
$setup = Join-Path $root "publish\APFS-for-Windows-Setup-$version.exe"
"Hazır: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
