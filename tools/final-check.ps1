<#
.SYNOPSIS
  (YÖNETİCİ) Kurulu APFS for Windows'un uçtan uca doğrulaması. Önce install.ps1 çalıştırılmış olmalı.
.DESCRIPTION
  1. Test VHD'leri (normal, şifreli, bozuk) SALT OKUNUR takılır; hizmetin bunları kendiliğinden bağladığı, şifreli birim için
     parola beklediği ve bozuk birim için anlaşılır bildirim ürettiği doğrulanır. Şifreli test biriminin parolası tepside sorulur.
  2. Disk 2 (gerçek Mac diski) varsa: önce yalnızca okuyarak öz denetim (apfs check; parola konsolda sorulur), sonra hizmetin
     tepside parola sorması beklenir; bağlandıktan sonra hız ölçümü ve parmak izi (diskin değişmediği) alınır.
  3. VHD'ler çıkarılır; hizmetin sürücüleri kendiliğinden kaldırdığı doğrulanır.
  Rapor: %TEMP%\claude\apfs-test\final-report.txt
#>
param([int]$Disk = 2)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = New-Object Text.UTF8Encoding $false
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$work = Join-Path $env:TEMP 'claude\apfs-test'
$report = Join-Path $work 'final-report.txt'
$installDir = Join-Path $env:ProgramFiles 'APFS for Windows'
$apfs = Join-Path $installDir 'apfs.exe'
New-Item -ItemType Directory -Force $work | Out-Null
$lines = New-Object System.Collections.Generic.List[string]
function Log([string]$s) { Write-Host $s; $lines.Add($s) }
function Status {
    try {
        $c = New-Object IO.Pipes.NamedPipeClientStream('.', 'APFSforWindows', [IO.Pipes.PipeDirection]::InOut)
        $c.Connect(5000)
        $b = [Text.Encoding]::UTF8.GetBytes('{"op":"status","since":0}' + "`n")
        $c.Write($b, 0, $b.Length); $c.Flush()
        $r = (New-Object IO.StreamReader($c, [Text.Encoding]::UTF8)).ReadLine(); $c.Dispose()
        return $r | ConvertFrom-Json
    } catch { return $null }
}
function ShowStatus($s) {
    foreach ($d in $s.disks) { Log "  $($d.description) [$($d.id)]"; foreach ($v in $d.volumes) { Log "     $($v.name): $($v.state) $($v.mountPoint) $($v.message)" } }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host 'Yönetici olarak çalıştırın.' -ForegroundColor Red; exit 1 }
$svc = Get-Service APFSforWindows -ErrorAction SilentlyContinue
if (-not $svc -or $svc.Status -ne 'Running') { Write-Host 'Hizmet çalışmıyor; önce install.ps1 çalıştırın.' -ForegroundColor Red; exit 1 }
Log "=== $(Get-Date -Format s) APFS for Windows uçtan uca doğrulama ==="

# --- 1. Test VHD'leri ---
# Önceki denemelerden takılı kalmış test VHD'lerini çıkar (ör. attach-test-vhd.ps1'in basic.vhd'si)
Get-ChildItem $work -Filter *.vhd -ErrorAction SilentlyContinue | ForEach-Object { Dismount-DiskImage -ImagePath $_.FullName -ErrorAction SilentlyContinue | Out-Null }
Start-Sleep -Seconds 3
$mk = Join-Path $root 'tests\Apfs.ImageBuilder\bin\Debug\net10.0\apfs-mkimage.exe'
$vhds = @{}
foreach ($pair in @(@('basic', 'normal'), @('encrypted', 'sifreli'), @('basic', 'bozuk'))) {
    $path = Join-Path $work "test-$($pair[1]).vhd"
    if (Test-Path $path) { Dismount-DiskImage -ImagePath $path -ErrorAction SilentlyContinue | Out-Null; Remove-Item $path -Force }
    & $mk $pair[0] $path --vhd --seed $pair[1] | Out-Null
    $vhds[$pair[1]] = $path
}
# Bozuk VHD: birim süper bloğunu (blok 18) boz; önce gerçekten APSB olduğunu doğrula
$fs = [IO.File]::Open($vhds['bozuk'], 'Open', 'ReadWrite')
$off = 40 * 512 + 18 * 4096; $hdr = New-Object byte[] 36; $fs.Position = $off; [void]$fs.Read($hdr, 0, 36)
if ([Text.Encoding]::ASCII.GetString($hdr, 32, 4) -eq 'APSB') { $fs.Position = $off + 1000; $fs.WriteByte(0xEE) } else { Log '  UYARI: bozuk VHD hazırlanamadı' }
$fs.Dispose()

$attached = @{}
foreach ($k in 'normal', 'sifreli', 'bozuk') {
    try { Mount-DiskImage -ImagePath $vhds[$k] -Access ReadOnly -ErrorAction Stop | Out-Null; $attached[$k] = $true }
    catch { Log "  HATA: $k VHD takılamadı: $($_.Exception.Message)"; $attached[$k] = $false }
}
Log '--- VHD''ler salt okunur takıldı; hizmetin fark etmesi bekleniyor ---'
Start-Sleep -Seconds 12
$s = Status; ShowStatus $s
$normal = [IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady -and $_.VolumeLabel -eq 'Test Diski' }
if ($normal) { Log "  Normal VHD: $($normal.Name) içerik: $([IO.File]::ReadAllText("$($normal.Name)Merhaba.txt").Trim())" } else { Log '  HATA: normal VHD bağlanmadı' }
Log '  Bildirimler:'; $s.notices | ForEach-Object { Log "    ($($_.level)) $($_.text)" }

$bad = ($s.disks | ForEach-Object { $_.volumes } | Where-Object { $_.state -eq 'hata' } | Select-Object -First 1)
if ($attached['bozuk']) { Log "  Bozuk VHD: $(if ($bad) { 'beklendiği gibi açılmadı: ' + $bad.message } else { 'HATA: bozukluk bildirilmedi' })" }

if ($attached['sifreli']) {
    Write-Host ''
    Write-Host 'Tepsideki pencere "Kasa" birimi için parola soruyor. Test parolası: Gizli Şifre ğüİı 2024' -ForegroundColor Yellow
    $deadline = (Get-Date).AddMinutes(3)
    do { Start-Sleep 3; $kasa = [IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady -and $_.VolumeLabel -eq 'Kasa' } } while (-not $kasa -and (Get-Date) -lt $deadline)
    if ($kasa) { Log "  Şifreli VHD tepsiden açıldı: $($kasa.Name) içerik: $([IO.File]::ReadAllText("$($kasa.Name)gizli.txt").Trim())" } else { Log '  Şifreli VHD açılmadı (parola girilmedi?)' }
}

# --- 2. Disk 2 ---
$diskPath = "\\.\PhysicalDrive$Disk"
Log "--- Disk $Disk ---"
& $apfs info $diskPath --fingerprint 2>&1 | ForEach-Object { Log "  $_" }
Write-Host ''
Write-Host "Disk $Disk için öz denetim: parolanız şimdi bu pencerede sorulacak (yalnızca okuma)." -ForegroundColor Yellow
& $apfs check $diskPath --password-prompt 2>&1 | ForEach-Object { Log "  $_" }
Write-Host ''
Write-Host "Şimdi tepsideki pencere Disk $Disk birimi için parolanızı soracak; girdiğinizde disk sürücü harfi olarak bağlanacak." -ForegroundColor Yellow
$deadline = (Get-Date).AddMinutes(5)
do {
    Start-Sleep 3; $s = Status
    $real = $s.disks | Where-Object { $_.id -eq "PhysicalDrive$Disk" } | ForEach-Object { $_.volumes } | Where-Object { $_.state -eq 'bağlı' } | Select-Object -First 1
} while (-not $real -and (Get-Date) -lt $deadline)
if ($real) {
    Log "  Disk $Disk bağlandı: $($real.mountPoint) `"$($real.name)`""
    $mp = $real.mountPoint
    $di = New-Object IO.DriveInfo $mp
    Log ("  Gezgin'in gördüğü alan: toplam {0:N1} GB, boş {1:N1} GB" -f ($di.TotalSize / 1e9), ($di.TotalFreeSpace / 1e9))
    $files = @(Get-ChildItem "$mp\" -Recurse -File -Force -ErrorAction SilentlyContinue)
    Log "  Gezgin'den görünen dosya sayısı: $($files.Count)"
    $big = $files | Sort-Object Length -Descending | Select-Object -First 1
    if ($big) { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\benchmark.ps1') -File $big.FullName 2>&1 | ForEach-Object { Log "  $_" } }
    $dir = $files | Group-Object DirectoryName | Sort-Object Count -Descending | Select-Object -First 1
    if ($dir) { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\benchmark.ps1') -Dir $dir.Name 2>&1 | ForEach-Object { Log "  $_" } }
} else { Log "  Disk $Disk bağlanmadı (takılı değil veya parola girilmedi)." }
Log '  Parmak izi (önce ve sonra aynı olmalı):'
& $apfs info $diskPath --fingerprint 2>&1 | Select-String 'Parmak' | ForEach-Object { Log "  $_" }

# --- 3. Çıkarma ---
foreach ($k in 'normal', 'sifreli', 'bozuk') { if ($attached[$k]) { Dismount-DiskImage -ImagePath $vhds[$k] | Out-Null } }
Start-Sleep -Seconds 8
$left = [IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady -and $_.VolumeLabel -in 'Test Diski', 'Kasa', 'Açık' }
Log "--- VHD'ler çıkarıldı; kalan test sürücüsü: $(@($left).Count) ---"
$s = Status; ShowStatus $s
Log '  Son bildirimler:'; $s.notices | Select-Object -Last 6 | ForEach-Object { Log "    ($($_.level)) $($_.text)" }

$lines | Out-File $report -Encoding utf8
Write-Host "`nRapor: $report" -ForegroundColor Green
Write-Host 'Son adım (isteğe bağlı): görev çubuğundaki "Donanımı güvenli kaldır" simgesinden Disk 2''yi çıkarmayı deneyin.'
