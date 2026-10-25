<#
.SYNOPSIS
  Bir APFS kaynağını (görüntü dosyası veya \\.\PhysicalDriveN) hem apfs.exe hem de bağımsız bir okuyucu olan 7-Zip ile
  okuyup sonuçları karşılaştırır.
.DESCRIPTION
  Varsayılan: her iki taraf için "yol <TAB> tür <TAB> boyut <TAB> sha256" listesi üretilir (7-Zip her şeyi çıkarır).
  -ListOnly: yalnızca yol/tür/boyut karşılaştırılır (büyük gerçek diskler için; hiçbir şey çıkarılmaz).
  -SampleHashes N: ayrıca boyuta göre yayılmış N dosyanın SHA-256'sı tek tek karşılaştırılır.
  Sembolik bağlar için hash yerine "->hedef" karşılaştırılır.
  Not: 7-Zip seyrek (sparse) APFS dosyalarını desteklemez; bu dosyalar fark olarak görünebilir.
  Çıkış kodu: 0 = aynı, 1 = fark var.
.EXAMPLE
  powershell -File tools\compare_with_7z.ps1 -Image test.img
  powershell -File tools\compare_with_7z.ps1 -Image \\.\PhysicalDrive2 -ListOnly -SampleHashes 20
#>
param(
    [Parameter(Mandatory)] [string] $Image,
    [string] $Apfs,
    [int] $Volume = 0,
    [switch] $ListOnly,
    [int] $SampleHashes = 0,
    [long] $MaxSampleBytes = 4GB
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not $Apfs) { $Apfs = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\src\Apfs.Cli\bin\Debug\net10.0-windows\apfs.exe' }
$work = Join-Path ([IO.Path]::GetTempPath()) ("apfs-cmp-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null

function Get-7zEntries {
    $slt = & 7z l -slt -sccUTF-8 $Image
    if ($LASTEXITCODE -ne 0) { throw "7-Zip kaynağı okuyamadı (çıkış $LASTEXITCODE)" }
    $entries = @(); $cur = $null; $inBody = $false
    foreach ($line in $slt) {
        if ($line -eq '----------') { $inBody = $true; continue }
        if (-not $inBody) { continue }
        if ($line -match '^Path = (.*)$') { if ($cur) { $entries += $cur }; $cur = @{ Path = $Matches[1] }; continue }
        if ($cur -and $line -match '^(Size|Mode|Symbolic Link) = (.*)$') { $cur[$Matches[1]] = $Matches[2] }
    }
    if ($cur) { $entries += $cur }
    return $entries
}

function Get-7zHash([string] $path7z) {
    # 7-Zip çıktısını diske yazmadan akış olarak SHA-256'ya verir (çok büyük dosyalar için).
    $psi = New-Object Diagnostics.ProcessStartInfo '7z'
    # -spd: ad içindeki ? ve * joker sayılmasın; -ssc: büyük/küçük harf duyarlı eşleştir (Rapor.txt ≠ rapor.txt).
    # 7-Zip'in komut satırı ayrıştırıcısı \" kaçışını tanımaz: " içeren adlarda " yerine ? jokeri kullanılır (-spd olmadan).
    $wild = if ($path7z.Contains('"')) { '' } else { '-spd ' }
    $psi.Arguments = "e -so $wild-ssc -sccUTF-8 `"$Image`" `"$($path7z.Replace('"', '?'))`""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $sha = [Security.Cryptography.SHA256]::Create()
    $hash = [BitConverter]::ToString($sha.ComputeHash($p.StandardOutput.BaseStream)).Replace('-', '')
    $p.WaitForExit()
    if ($p.ExitCode -ne 0) { return "7z-HATA($($p.ExitCode))" }
    return $hash
}

try {
    # --- Bizim okuyucumuz ---
    $lsArgs = @('ls', $Image, '/', '-r', '--volume', $Volume)
    if (-not $ListOnly) { $lsArgs += '--hash' }
    $ours = & $Apfs @lsArgs
    if ($LASTEXITCODE -ne 0) { throw "apfs.exe başarısız oldu (çıkış $LASTEXITCODE)" }

    # --- 7-Zip ---
    $entries = Get-7zEntries
    # Birden çok birimli kapsayıcıda 7-Zip yolları "VolumeN\" ile başlatır (N = birim sırası + 1)
    if ($entries.Count -gt 0 -and @($entries | Where-Object { $_.Path -notmatch '^Volume\d+(\\|$)' }).Count -eq 0) {
        $prefix = "Volume$($Volume + 1)\"
        $entries = @($entries | Where-Object { $_.Path.StartsWith($prefix) } | ForEach-Object { $_.Orig = $_.Path; $_.Path = $_.Path.Substring($prefix.Length); $_ })
    }
    $x = Join-Path $work 'x'
    if (-not $ListOnly) {
        $excl = @(); foreach ($e in $entries | Where-Object { $_.Mode -like 'l*' }) { $excl += "-x!$($e.Path)" }
        & 7z x $Image "-o$x" -y @excl 2>&1 | Out-Null
    }
    # Diske çıkarılınca bozulan adlar: Windows'ta geçersiz karakter içerenler ve NTFS'te büyük/küçük harf çakışanlar.
    # Bunların hash'i diske çıkarmadan akış olarak alınır.
    $invalid = [IO.Path]::GetInvalidFileNameChars()
    $lowerCount = @{}
    foreach ($e in $entries) { $k = $e.Path.ToLowerInvariant(); $lowerCount[$k] = 1 + [int]$lowerCount[$k] }
    $theirs = foreach ($e in $entries) {
        # 7-Zip, ad içindeki "\" karakterini U+F05C olarak gösterir; karşılaştırma için geri çevir.
        $p = ($e.Path -replace '\\', '/').Replace([string][char]0xF05C, '\')
        if ($e.Mode -like 'd*') { "$p`td`t-`t-" }
        elseif ($e.Mode -like 'l*') { "$p`tl`t-`t->$($e['Symbolic Link'])" }
        elseif ($ListOnly) { "$p`tf`t$($e.Size)`t-" }
        else {
            $name = Split-Path -Leaf $e.Path
            $awkward = ($name.IndexOfAny($invalid) -ge 0) -or $name.EndsWith('.') -or $name.EndsWith(' ') -or ($lowerCount[$e.Path.ToLowerInvariant()] -gt 1) -or ($name.IndexOf([char]0xF05C) -ge 0)
            $orig = if ($e.Orig) { $e.Orig } else { $e.Path }
            if ($awkward) { $h = Get-7zHash $orig }
            else {
                $f = Join-Path $x $orig
                $h = if (Test-Path -LiteralPath $f) { (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash } else { 'EKSİK' }
            }
            "$p`tf`t$($e.Size)`t$h"
        }
    }

    $a = @($ours | Sort-Object { $_ } -Culture '')
    $b = @($theirs | Sort-Object { $_ } -Culture '')
    $failed = $false
    "apfs.exe: $($a.Count) girdi, 7-Zip: $($b.Count) girdi"
    $diff = Compare-Object $a $b
    if ($diff) {
        $failed = $true
        "FARK VAR ($(@($diff).Count) satır, ilk 50):"
        $diff | Select-Object -First 50 | ForEach-Object { "  {0} {1}" -f ($(if ($_.SideIndicator -eq '<=') { 'apfs ' } else { '7-Zip' })), $_.InputObject }
    }
    else {
        if ($ListOnly) { "AYNI: tüm yollar, türler ve boyutlar eşleşiyor." }
        else { "AYNI: tüm yollar, türler, boyutlar ve SHA-256 değerleri eşleşiyor." }
    }

    if ($SampleHashes -gt 0) {
        $files = @($a | ForEach-Object { $c = $_ -split "`t"; if ($c[1] -eq 'f' -and [long]$c[2] -gt 0 -and [long]$c[2] -le $MaxSampleBytes) { [pscustomobject]@{ Path = $c[0]; Size = [long]$c[2] } } } | Sort-Object Size)
        $picked = @()
        if ($files.Count -gt 0) {
            $n = [Math]::Min($SampleHashes, $files.Count)
            for ($i = 0; $i -lt $n; $i++) { $picked += $files[[int][Math]::Floor($i * ($files.Count - 1) / [Math]::Max(1, $n - 1))] }
            $picked = $picked | Sort-Object Path -Unique
        }
        "Örnek dosya hash karşılaştırması ($($picked.Count) dosya, en büyüğü $([Math]::Round(($picked | Measure-Object Size -Maximum).Maximum / 1MB, 1)) MB):"
        foreach ($f in $picked) {
            $line = & $Apfs ls $Image "/$($f.Path)" --hash --volume $Volume
            $ourHash = ($line -split "`t")[3]
            $theirHash = Get-7zHash ($f.Path -replace '/', '\')
            $ok = $ourHash -eq $theirHash
            if (-not $ok) { $failed = $true }
            "  {0} {1,12:N0}  {2}" -f $(if ($ok) { 'AYNI ' } else { 'FARK ' }), $f.Size, $f.Path
        }
    }
    if ($failed) { exit 1 }
    exit 0
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
