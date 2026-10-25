<#
.SYNOPSIS
  Bağlı bir APFS sürücüsünde okuma hızını ve büyük klasör performansını ölçer.
.DESCRIPTION
  -File: bağlı sürücüdeki büyük bir dosya. Önce NTFS'e kopyalanır (karşılaştırma tabanı), sonra
  aynı yöntemle (1 MB tampon, sıralı) hem APFS sürücüsünden hem NTFS kopyasından okunur.
  -Dir: çok dosyalı bir klasör. Listeleme süresi ve her dosyayı açıp okuma süresi ölçülür.
  Not: test görüntüsü bu makinenin NVMe diskinde ve büyük olasılıkla işletim sistemi önbelleğinde olduğu için
  ölçülen fark esas olarak programın kendi ek yüküdür; gerçek USB disk hızı ayrıca ölçülmelidir.
#>
param(
    [string] $File,
    [string] $Dir
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Measure-Read([string] $path) {
    $buf = New-Object byte[] (1MB)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $fs = New-Object IO.FileStream($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 1MB, [IO.FileOptions]::SequentialScan)
    $total = 0L
    try { while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) { $total += $n } } finally { $fs.Dispose() }
    $sw.Stop()
    [pscustomobject]@{ Bytes = $total; Seconds = $sw.Elapsed.TotalSeconds; MBps = [math]::Round($total / 1MB / $sw.Elapsed.TotalSeconds, 0) }
}

if ($File) {
    $copy = Join-Path ([IO.Path]::GetTempPath()) ('apfs-bench-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Copy-Item -LiteralPath $File $copy
    $sw.Stop()
    $size = (Get-Item -LiteralPath $copy).Length
    "Dosya: $File ($([math]::Round($size / 1MB)) MB)"
    "  Kopyalama (Copy-Item, APFS → NTFS):  $([math]::Round($size / 1MB / $sw.Elapsed.TotalSeconds)) MB/sn"
    try {
        $apfs1 = Measure-Read $File
        $apfs2 = Measure-Read $File
        $ntfs1 = Measure-Read $copy
        $ntfs2 = Measure-Read $copy
        "  APFS sürücüsünden okuma:             $($apfs1.MBps) MB/sn (tekrar: $($apfs2.MBps) MB/sn)"
        "  NTFS kopyasından okuma (taban):      $($ntfs1.MBps) MB/sn (tekrar: $($ntfs2.MBps) MB/sn)"
        "  Oran (en iyi APFS / en iyi NTFS):     $([math]::Round(100 * [math]::Max($apfs1.MBps, $apfs2.MBps) / [math]::Max($ntfs1.MBps, $ntfs2.MBps)))%"
    }
    finally { Remove-Item -LiteralPath $copy -Force }
}

if ($Dir) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $items = @(Get-ChildItem -LiteralPath $Dir -Force)
    $list = $sw.Elapsed.TotalSeconds
    $sw.Restart()
    $bytes = 0L
    foreach ($i in $items) { if (-not $i.PSIsContainer) { $bytes += [IO.File]::ReadAllBytes($i.FullName).Length } }
    $open = $sw.Elapsed.TotalSeconds
    "Klasör: $Dir ($($items.Count) girdi)"
    "  Listeleme:                       $([math]::Round($list, 2)) sn"
    "  Her dosyayı açıp okuma:          $([math]::Round($open, 2)) sn ($([math]::Round(1000 * $open / [math]::Max(1, $items.Count), 2)) ms/dosya)"
}
