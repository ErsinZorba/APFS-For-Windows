<#
.SYNOPSIS
  APFS for Windows kurulumu (YÖNETİCİ olarak çalıştırın).
.DESCRIPTION
  - WinFsp'i denetler (.NET çalışma zamanı programla birlikte gelir).
  - Programı "C:\Program Files\APFS for Windows" altına kopyalar.
  - "APFS for Windows" hizmetini (LocalSystem, otomatik başlangıç) kurar ve başlatır: takılan APFS diskleri kendiliğinden,
    salt okunur bağlanır.
  - Tepsi uygulamasını her oturum açılışında başlayacak şekilde kaydeder ve şimdi başlatır.
  Kurulum diskinize hiçbir şey yazmaz. Kaldırmak için: uninstall.ps1
#>
param([string]$Source)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$installDir = Join-Path $env:ProgramFiles 'APFS for Windows'
$serviceName = 'APFSforWindows'

function Fail($msg) { Write-Host "Kurulum durdu: $msg" -ForegroundColor Red; exit 1 }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Fail 'PowerShell''i "Yönetici olarak çalıştır" ile açıp tekrar deneyin.' }

# --- Ön koşullar ---
$winfsp = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\WinFsp' -ErrorAction SilentlyContinue).InstallDir
if (-not $winfsp) { Fail 'WinFsp kurulu değil. Kurmak için: winget install --id WinFsp.WinFsp -e' }

# --- Kaynak dosyalar ---
if (-not $Source) {
    $Source = Join-Path $here 'publish\APFS for Windows'
    if (-not (Test-Path (Join-Path $Source 'apfs-service.exe'))) {
        $alt = Join-Path $here 'APFS for Windows'
        if (Test-Path (Join-Path $alt 'apfs-service.exe')) { $Source = $alt }
        elseif (Test-Path (Join-Path $here 'tools\build-release.ps1')) {
            Write-Host 'Derleniyor...'
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here 'tools\build-release.ps1')
            if ($LASTEXITCODE -ne 0) { Fail 'derleme başarısız' }
        }
    }
}
foreach ($f in 'apfs.exe', 'apfs-service.exe', 'apfs-tray.exe') {
    if (-not (Test-Path (Join-Path $Source $f))) { Fail "$f bulunamadı: $Source" }
}

# --- Eski sürümü durdur ---
Get-Process apfs-tray -ErrorAction SilentlyContinue | Stop-Process -Force
$svc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service $serviceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }

# --- Kopyala ---
New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item (Join-Path $Source '*') $installDir -Recurse -Force
Write-Host "Kopyalandı: $installDir"

# --- Hizmet ---
$bin = "`"$installDir\apfs-service.exe`""
if (-not $svc) {
    New-Service -Name $serviceName -BinaryPathName $bin -DisplayName 'APFS for Windows' -StartupType Automatic `
        -Description 'Takılan Mac (APFS) disklerini Windows''ta salt okunur sürücü olarak bağlar.' | Out-Null
} else {
    & sc.exe config $serviceName binPath= $bin start= auto | Out-Null
}
# Beklenmedik çökmede 5 sn sonra yeniden başlat
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
Start-Service $serviceName
Write-Host 'Hizmet kuruldu ve başlatıldı.'

# --- Fiziksel diskleri elle bağlamak için WinFsp Launcher kaydı (isteğe bağlı kullanım) ---
& (Join-Path $installDir 'apfs.exe') register | Out-Null

# --- Tepsi uygulaması: her oturumda ve şimdi (normal kullanıcı yetkisiyle) ---
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'APFS for Windows' -Value "`"$installDir\apfs-tray.exe`""
Start-Process explorer.exe "`"$installDir\apfs-tray.exe`""

Write-Host ''
Write-Host 'Kurulum tamamlandı. Mac diskinizi taktığınızda birkaç saniye içinde sürücü harfi olarak görünecek.' -ForegroundColor Green
Write-Host 'Şifreli bir birimde tepsideki APFS simgesi parolanızı soracak. Günlük: C:\ProgramData\APFS for Windows\log.txt'
