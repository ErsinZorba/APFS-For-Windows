<#
.SYNOPSIS
  APFS for Windows'u kaldırır (YÖNETİCİ olarak çalıştırın). Bağlı diskler ayrılır; disklerinize hiçbir şey yazılmaz.
  Günlük dosyası (C:\ProgramData\APFS for Windows) incelemeniz için bırakılır. WinFsp kaldırılmaz.
#>
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$installDir = Join-Path $env:ProgramFiles 'APFS for Windows'
$serviceName = 'APFSforWindows'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host 'PowerShell''i "Yönetici olarak çalıştır" ile açıp tekrar deneyin.' -ForegroundColor Red; exit 1 }

Get-Process apfs-tray -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'APFS for Windows' -ErrorAction SilentlyContinue

$svc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service $serviceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }
    & sc.exe delete $serviceName | Out-Null
    Write-Host 'Hizmet kaldırıldı.'
}

if (Test-Path (Join-Path $installDir 'apfs.exe')) { & (Join-Path $installDir 'apfs.exe') unregister | Out-Null }
& reg.exe delete 'HKLM\SOFTWARE\WOW6432Node\WinFsp\Services\apfs' /f 2>$null | Out-Null
Get-Process apfs -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
if (Test-Path $installDir) { Remove-Item -Recurse -Force $installDir; Write-Host "Silindi: $installDir" }

Write-Host 'APFS for Windows kaldırıldı.' -ForegroundColor Green
