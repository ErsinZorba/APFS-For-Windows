<#
.SYNOPSIS
  Gerçek bir APFS diskinde (yalnızca okuyarak) doğrulama yapar. YÖNETİCİ olarak çalıştırılmalıdır.
.DESCRIPTION
  -Phase before : disk bilgisi + parmak izi, 7-Zip ile liste karşılaştırması ve örnek dosya hash'leri, WinFsp Launcher kaydı.
  -Phase after  : yalnızca parmak izi (bağlama sonrası diskin değişmediğini göstermek için).
  Rapor: %TEMP%\claude\apfs-test\disk<N>-<phase>.txt
  Diske hiçbir şey yazılmaz: apfs.exe ve 7-Zip diski yalnızca okuma izniyle açar.
#>
param(
    [int] $Disk = 2,
    [ValidateSet('before', 'after')] [string] $Phase = 'before',
    [int] $SampleHashes = 20
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$apfs = Join-Path $root 'src\Apfs.Cli\bin\Debug\net10.0-windows\apfs.exe'
$src = "\\.\PhysicalDrive$Disk"
$outDir = Join-Path $env:TEMP 'claude\apfs-test'
New-Item -ItemType Directory -Force $outDir | Out-Null
$report = Join-Path $outDir "disk$Disk-$Phase.txt"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host 'Bu betik yönetici olarak çalıştırılmalı (PowerShell → Yönetici olarak çalıştır).' -ForegroundColor Red; exit 1 }

& {
    "=== $(Get-Date -Format s)  disk $Disk  aşama $Phase ==="
    "--- apfs info ---"
    & $apfs info $src --fingerprint 2>&1
    if ($Phase -eq 'before') {
        "--- 7-Zip listesi (ilk 15 satır) ---"
        & 7z l $src 2>&1 | Select-Object -First 15
        "--- apfs ile 7-Zip karşılaştırması ---"
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\compare_with_7z.ps1') -Image $src -Apfs $apfs -ListOnly -SampleHashes $SampleHashes 2>&1
        "--- WinFsp Launcher kaydı ---"
        & $apfs register 2>&1
    }
} *>&1 | Tee-Object -Variable lines
$lines | Out-File -FilePath $report -Encoding utf8

Write-Host "`nRapor kaydedildi: $report" -ForegroundColor Green
