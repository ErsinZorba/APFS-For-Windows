<#
.SYNOPSIS
  Gerçek Windows disk yolunda yazma denemesi (Faz 0.4 + 5.1). YÖNETİCİ olarak çalıştırılmalıdır. Gerçek disklere DOKUNMAZ.
.DESCRIPTION
  1) Linux test görüntüsünden (mkapfs, 1 GB) GPT bölümlü bir VHD üretir ve takar (sürücü harfi vermeden).
  2) Windows'un APFS bölümü için birim oluşturup oluşturmadığını kaydeder (0.4 sorusu).
  3) Kurulu hizmetten bu sanal diski bırakmasını ister (salt okunur bağlamasın).
  4) Test yazıcısı (apfs-devwriter --vhd-disk N; gerçek diskleri reddeder) VHD'yi \\.\PhysicalDriveN üzerinden yazılabilir bağlar.
  5) robocopy ile Türkçe adlı bir ağacı içeri yazar, geri okur, karşılaştırır; Office tarzı kaydetme ve silme yapar.
  6) Düzgün kapatır, tam denetim yapar, VHD'yi ayırır. Rapor: <depo>\fixtures\vhd-write-report.txt
#>
param([int]$SmallFiles = 2000, [int]$BigMb = 50)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$fx = Join-Path $root 'fixtures'
New-Item -ItemType Directory -Force $fx | Out-Null
$report = Join-Path $fx 'vhd-write-report.txt'
$mk = Join-Path $root 'tests\Apfs.ImageBuilder\bin\Debug\net10.0\apfs-mkimage.exe'
$dw = Join-Path $root 'tests\Apfs.DevWriter\bin\Debug\net10.0-windows\apfs-devwriter.exe'
$apfs = Join-Path $root 'src\Apfs.Cli\bin\Debug\net10.0-windows\apfs.exe'
$img = Join-Path $fx 'vhd-kaynak.img'
$vhd = Join-Path $fx 'yazma-denemesi.vhd'
$src = Join-Path $env:TEMP "apfs-vhd-kaynak-$PID"
$back = Join-Path $env:TEMP "apfs-vhd-geri-$PID"

function Log([string]$m) { Write-Host $m; Add-Content -Path $report -Value $m -Encoding UTF8 }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host 'Bu betik yönetici olarak çalıştırılmalı (PowerShell → Yönetici olarak çalıştır).' -ForegroundColor Red; exit 1 }
Set-Content -Path $report -Value "=== $(Get-Date -Format s)  VHD yazma denemesi ===" -Encoding UTF8

$proc = $null
$mounted = $false
try {
    # 1) VHD hazırla
    $gz = Join-Path $root 'tests\Apfs.Tests\data\linux\mk-ci-1g.img.gz'
    $in = [IO.File]::OpenRead($gz)
    $z = New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress)
    $out = [IO.File]::Create($img)
    $z.CopyTo($out); $out.Dispose(); $z.Dispose(); $in.Dispose()
    if (Test-Path $vhd) { [IO.File]::Delete($vhd) }
    & $mk wrap $img $vhd | ForEach-Object { Log $_ }
    [IO.File]::Delete($img)

    # 2) Tak (harfsiz) ve Windows'un bölüme ne yaptığını kaydet
    $di = Mount-DiskImage -ImagePath $vhd -NoDriveLetter -PassThru
    $mounted = $true
    Start-Sleep -Seconds 2
    $n = ($di | Get-Disk).Number
    Log "VHD disk numarası: $n"
    Log ("Bölümler: " + ((Get-Partition -DiskNumber $n | ForEach-Object { "#$($_.PartitionNumber) tür $($_.GptType) $([math]::Round($_.Size/1MB)) MB yollar=[$($_.AccessPaths -join ', ')]" }) -join ' | '))
    Log ("Birim var mı: " + ((Get-Partition -DiskNumber $n | Get-Volume -ErrorAction SilentlyContinue | ForEach-Object { "$($_.FileSystemType) $($_.Path)" }) -join ' | '))

    # 3) Kurulu hizmet bu diski bıraksın (salt okunur bağlamasın)
    try {
        $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', 'APFSforWindows', [IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(3000)
        $w = New-Object IO.StreamWriter($pipe, (New-Object Text.UTF8Encoding $false)); $w.AutoFlush = $true
        $w.WriteLine("{`"op`":`"eject`",`"disk`":`"PhysicalDrive$n`",`"force`":true}")
        $r = (New-Object IO.StreamReader($pipe)).ReadLine()
        Log "Kurulu hizmet: $r"
        $pipe.Dispose()
    } catch { Log "Kurulu hizmete ulaşılamadı (sorun değil): $($_.Exception.Message)" }
    Start-Sleep -Seconds 2

    # 4) Test yazıcısı
    $psi = New-Object Diagnostics.ProcessStartInfo($dw, "--vhd-disk $n")
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    $proc = [Diagnostics.Process]::Start($psi)
    $errTask = $proc.StandardError.ReadToEndAsync()
    $first = $proc.StandardOutput.ReadLine()
    Log "Yazıcı: $first"
    if ($first -notlike 'HAZIR*') { $proc.WaitForExit(30000) | Out-Null; Log "Yazıcı hatası: $($errTask.Result)"; throw 'yazıcı başlamadı' }
    $drive = $first.Substring(6).Trim()

    # 5) İş yükü
    $rnd = New-Object Random 42
    $dirs = 'Belgeler', 'Fotoğraflar\2024 Yaz', 'ığüşöç İĞÜŞÖÇ'
    foreach ($d in $dirs) { New-Item -ItemType Directory -Force (Join-Path $src $d) | Out-Null }
    for ($i = 0; $i -lt $SmallFiles; $i++) {
        $b = New-Object byte[] ($rnd.Next(0, 20000)); $rnd.NextBytes($b)
        [IO.File]::WriteAllBytes((Join-Path $src "$($dirs[$i % 3])\dosya-$i-ğ.dat"), $b)
    }
    $big = New-Object byte[] ($BigMb * 1MB); $rnd.NextBytes($big)
    [IO.File]::WriteAllBytes((Join-Path $src 'büyük.bin'), $big)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    robocopy $src "$drive\Kopya" /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
    $inSec = $sw.Elapsed.TotalSeconds
    $mb = ((Get-ChildItem $src -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
    Log ("İçeri kopyalama: robocopy çıkış {0}, {1:N1} MB, {2:N1} sn ({3:N1} MB/sn)" -f $LASTEXITCODE, $mb, $inSec, ($mb / $inSec))
    $sw.Restart()
    robocopy "$drive\Kopya" $back /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
    Log ("Dışarı kopyalama: robocopy çıkış {0}, {1:N1} sn" -f $LASTEXITCODE, $sw.Elapsed.TotalSeconds)
    $a = Get-ChildItem $src -Recurse -File | ForEach-Object { "{0} {1}" -f $_.FullName.Substring($src.Length), (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object
    $b2 = Get-ChildItem $back -Recurse -File | ForEach-Object { "{0} {1}" -f $_.FullName.Substring($back.Length), (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object
    $diff = Compare-Object $a $b2
    Log ("Karşılaştırma: {0} dosya, fark {1}" -f $a.Count, ($diff | Measure-Object).Count)
    # Office tarzı kaydetme, silme
    Set-Content -Path "$drive\~belge.tmp" -Value 'yeni sürüm' -Encoding UTF8
    Set-Content -Path "$drive\belge.docx" -Value 'eski sürüm' -Encoding UTF8
    Remove-Item "$drive\belge.docx"; Rename-Item "$drive\~belge.tmp" 'belge.docx'
    Remove-Item "$drive\Kopya\Belgeler" -Recurse -Force
    Log ("Son durum kökte: " + ((Get-ChildItem $drive -Force | ForEach-Object Name) -join ', '))

    # 6) Düzgün kapat
    $proc.StandardInput.WriteLine('BITIR')
    if (-not $proc.WaitForExit(120000)) { Log 'Yazıcı 2 dakikada kapanmadı'; $proc.Kill() }
    Log ("Yazıcı: " + $proc.StandardOutput.ReadToEnd().Trim())
    $e = $errTask.Result.Trim(); if ($e) { Log "Yazıcı uyarıları: $e" }

    # 7) Tam denetim (salt okunur)
    & $apfs check "\\.\PhysicalDrive$n" --full 2>&1 | ForEach-Object { Log "$_" }
}
catch { Log "HATA: $($_.Exception.Message)" }
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill() }
    if ($mounted) { Dismount-DiskImage -ImagePath $vhd | Out-Null; Log 'VHD ayrıldı.' }
    foreach ($d in $src, $back) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
    Log "Bitti. Rapor: $report"
}
