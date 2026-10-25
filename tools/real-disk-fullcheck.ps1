<#
.SYNOPSIS
  Gerçek bir APFS diskinde YALNIZCA OKUYARAK yapısal tam denetim (Faz 0.5 gerçek veri doğrulaması). YÖNETİCİ olarak çalıştırılmalıdır.
.DESCRIPTION
  Sırasıyla: parmak izi (önce) → iç yapı dökümü → yazma kapısı kararı → tam denetim (kilitli) → tam denetim (parolayla; parola
  yalnızca ekrandan sorulur, kaydedilmez) → parmak izi (sonra). Dosya adları raporda gizlenir (--gizle).
  Rapor: <depo>\fixtures\disk<N>-fullcheck.txt
  Diske hiçbir şey yazılmaz: apfs.exe diski yalnızca okuma izniyle açar (RawDiskDevice, GENERIC_READ).
#>
param([int] $Disk = 2)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$apfs = Join-Path $root 'src\Apfs.Cli\bin\Debug\net10.0-windows\apfs.exe'
$src = "\\.\PhysicalDrive$Disk"
$outDir = Join-Path $root 'fixtures'
New-Item -ItemType Directory -Force $outDir | Out-Null
$report = Join-Path $outDir "disk$Disk-fullcheck.txt"
$tmp = Join-Path $outDir "disk$Disk-parola-denetimi.tmp"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host 'Bu betik yönetici olarak çalıştırılmalı (PowerShell → Yönetici olarak çalıştır).' -ForegroundColor Red; exit 1 }

function Section($title, [scriptblock] $body) {
    Write-Host "--- $title ---" -ForegroundColor Cyan
    "--- $title ---" | Out-File -FilePath $report -Append -Encoding utf8
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & $body 2>&1 | ForEach-Object { "$_" }
    $out | Out-File -FilePath $report -Append -Encoding utf8
    "(süre: $([int]$sw.Elapsed.TotalSeconds) sn)" | Out-File -FilePath $report -Append -Encoding utf8
    $out | Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }
}

"=== $(Get-Date -Format s)  disk $Disk  tam denetim (salt okunur) ===" | Out-File -FilePath $report -Encoding utf8
Section 'parmak izi (önce)'        { & $apfs info $src --fingerprint }
Section 'iç yapı dökümü'           { & $apfs dump $src }
Section 'yazma kapısı'             { & $apfs check $src --gate }
Section 'tam denetim (kilitli)'    { & $apfs check $src --full --gizle }

Write-Host '--- tam denetim (parolayla) ---' -ForegroundColor Cyan
Write-Host 'Birazdan diskin parolası sorulacak (yazarken ekranda görünmez). Parola hiçbir yere kaydedilmez.' -ForegroundColor Yellow
$sw = [Diagnostics.Stopwatch]::StartNew()
& $apfs check $src --full --gizle --password-prompt > $tmp
'--- tam denetim (parolayla) ---' | Out-File -FilePath $report -Append -Encoding utf8
Get-Content $tmp -Encoding UTF8 | Out-File -FilePath $report -Append -Encoding utf8
"(süre: $([int]$sw.Elapsed.TotalSeconds) sn)" | Out-File -FilePath $report -Append -Encoding utf8
Get-Content $tmp -Encoding UTF8 | Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }
[IO.File]::Delete($tmp)

Section 'parmak izi (sonra)'       { & $apfs info $src --fingerprint }
Write-Host "`nBitti. Rapor: $report" -ForegroundColor Green
Write-Host 'Bu pencereyi kapatabilirsiniz; rapor dosyaya yazıldı.' -ForegroundColor Green
