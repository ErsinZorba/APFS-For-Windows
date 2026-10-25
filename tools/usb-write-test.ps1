<#
.SYNOPSIS
  Gerçek USB bellekte yazma denemesi (Faz 5.2 / 5.3). YÖNETİCİ olarak çalıştırılmalıdır.
.DESCRIPTION
  Yalnızca Mac'te APFS olarak silinip adı USBTEST yapılmış, USB bağlantılı, 512 GB'tan küçük, tek birimli belleğe yazar; bellek
  numarasıyla değil adıyla bulunur ve başlamadan önce onay istenir. Diğer bütün diskler test yazıcısında da reddedilir.
    -Adim yaz    (5.2) otomatik kopyala/karşılaştır + sizin Gezgin'de elle yapacağınız işlemler → düzgün kapatma → tam denetim
    -Adim kablo  (5.3) büyük bir kopyalama sürerken belleği çekersiniz → yeniden takınca tam denetim + yazma kapısı
    -Adim denetle      yalnızca salt okunur tam denetim + yazma kapısı (yarıda kalmış bir denemeden sonra)
    -Adim yuzey        donanım yüzey testi: belleğin TAMAMEN BOŞ alanına APFS yazıcısı kullanılmadan 2 GB yazar, geri okur;
                       bellek çıkarılıp takıldıktan sonra yeniden okur (dosyalara ve APFS yapısına dokunmaz)
    -Adim mac-hazirla  Mac ziyareti (5.4): bellekteki her dosyanın parmak izini Mac kitinin biçiminde çıkarır, kiti ve listeyi
                       yazma özelliğiyle APFS-Mac-Kiti klasörüne koyar, belleği DÜZGÜN kapatır
  -Parola  şifreli birim (2026-10-11): parola başta bir kez sorulur, yalnız bellekte tutulur ve araçlara standart girdiden verilir
           (komut satırına, rapora, dosyaya yazılmaz)
  -Kisa    'yaz' adımında Gezgin'deki elle kısım atlanır
  Çok bölümlü bellekte yazılmayan öbür APFS bölümlerinin değişmediği, başta ve sonda dolu bloklarının özetiyle gösterilir.
  Rapor: <depo>\fixtures\usb-write-report.txt
#>
param([ValidateSet('yaz', 'kablo', 'denetle', 'yuzey', 'mac-hazirla')][string]$Adim = 'yaz', [string]$Ad = 'USBTEST', [int]$Parts = 192,
    [switch]$Parola, [switch]$Kisa)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[Console]::InputEncoding = [Text.Encoding]::UTF8          # yazıcının standart girdisi (parola) UTF-8 gitsin
$OutputEncoding = New-Object Text.UTF8Encoding $false     # apfs.exe'ye boruyla verilen parola UTF-8 gitsin
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$fx = Join-Path $root 'fixtures'
New-Item -ItemType Directory -Force $fx | Out-Null
$report = Join-Path $fx 'usb-write-report.txt'
$dw = Join-Path $root 'tests\Apfs.DevWriter\bin\Debug\net10.0-windows\apfs-devwriter.exe'
$apfs = Join-Path $root 'src\Apfs.Cli\bin\Debug\net10.0-windows\apfs.exe'
$src = Join-Path $env:TEMP "apfs-usb-kaynak-$PID"
$back = Join-Path $env:TEMP "apfs-usb-geri-$PID"

# Windows PowerShell 5.1'de $ErrorActionPreference = 'Stop' iken yerel bir programın standart hata çıktısı (ör. test yazıcısının
# başka bir diski reddettiğini bildiren satırı) betiği durduran hataya dönüşür. Yerel programlar yalnızca bunun üzerinden
# çağrılır: her iki çıktı da düz metin satırı olarak döner, çıkış kodu $LASTEXITCODE'da kalır.
function Native([string]$exe, [string[]]$argv) {
    $ErrorActionPreference = 'Continue'
    & $exe @argv 2>&1 | ForEach-Object { "$_" }
}

$script:pw = $null
# apfs.exe çağrısı: şifreli birimde parola standart girdiden (--password-stdin)
function ApfsRun([string[]]$argv) {
    if (-not $script:pw) { return Native $apfs $argv }
    $ErrorActionPreference = 'Continue'
    $script:pw | & $apfs @($argv + '--password-stdin') 2>&1 | ForEach-Object { "$_" }
}

function Log([string]$m) { Write-Host $m; Add-Content -Path $report -Value $m -Encoding UTF8 }
function Say([string]$m) { Write-Host ''; Write-Host $m -ForegroundColor Yellow }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host 'Bu betik yönetici olarak çalıştırılmalı (PowerShell → Yönetici olarak çalıştır).' -ForegroundColor Red; exit 1 }
Add-Content -Path $report -Value "`n=== $(Get-Date -Format s)  USB yazma denemesi: $Adim ===" -Encoding UTF8

# Belleği adıyla bulur (salt okunur). Tam olarak bir eşleşme yoksa $null. Çok bölümlü bellekte (ör. şifresiz + şifreli) adı
# taşıyan APFS bölümünün sırası da gelir; bütün apfs.exe çağrıları yalnız o bölüme bakar (--partition).
$script:part = 0
function Find-Stick {
    $out = Native $dw @('--usb-find', $Ad)
    $hits = @($out | Where-Object { $_ -like 'BULUNDU *' })
    if ($hits.Count -ne 1) { return $null }
    $f = $hits[0].Substring(8) -split "`t"
    $script:part = if ($f.Count -gt 3) { [int]$f[3] } else { 0 }
    return [pscustomobject]@{ Number = [int]$f[0]; Desc = $f[1]; Size = $f[2]; Part = $script:part }
}

# Kurulu sürüm (1.0.0) belleği salt okunur bağlar; onda "yalnızca bırak" isteği yok ("eject" belleği Windows'tan tamamen
# çıkarır — 2026-10-05'te test yazıcısı bu yüzden belleği bulamadı). Deneme süresince hizmet durdurulur, sonunda yeniden başlatılır.
$svcName = 'APFSforWindows'
$script:svcStopped = $false
function Stop-InstalledService {
    $svc = Get-Service $svcName -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -ne 'Running') { Log 'Kurulu hizmet çalışmıyor.'; return }
    Stop-Service $svcName -Force
    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    $script:svcStopped = $true
    Log 'Kurulu hizmet deneme süresince durduruldu.'
    Start-Sleep -Seconds 2
}
function Start-InstalledService {
    $svc = Get-Service $svcName -ErrorAction SilentlyContinue
    if (-not $svc -or $svc.Status -eq 'Running') { return }
    try { Start-Service $svcName; Log 'Kurulu hizmet yeniden başlatıldı.'; $script:svcStopped = $false }
    catch { Log "Kurulu hizmet yeniden başlatılamadı: $($_.Exception.Message) — bilgisayarı yeniden başlatmak yeter." }
}

function Full-Check([int]$n) {
    Log '--- Tam denetim (salt okunur)'
    ApfsRun @('check', "\\.\PhysicalDrive$n", '--partition', "$script:part", '--full', '--gizle') | ForEach-Object { Log "  $_" }
    Log '--- Yazma kapısı'
    ApfsRun @('check', "\\.\PhysicalDrive$n", '--partition', "$script:part", '--gate') | ForEach-Object { Log "  $_" }
}

function Save-Manifest([int]$n, [string]$tag) {
    $file = Join-Path $fx "usb-manifest-$tag-$(Get-Date -Format yyyyMMdd-HHmmss).txt"
    $lines = ApfsRun @('ls', "\\.\PhysicalDrive$n", '--partition', "$script:part", '/', '-r', '--hash')
    [IO.File]::WriteAllLines($file, [string[]]$lines, (New-Object Text.UTF8Encoding $false))
    $files = @($lines | Where-Object { ($_ -split "`t")[1] -eq 'f' }).Count
    $errs = @($lines | Where-Object { $_ -match "`tHATA" }).Count
    Log "Bellekteki dosyalar: $files (okunamayan: $errs) — liste: $(Split-Path -Leaf $file)"
}

function Start-Writer([int]$n) {
    $psi = New-Object Diagnostics.ProcessStartInfo($dw, "--usb-disk $n --name $Ad$(if ($script:pw) { ' --password-stdin' })")
    $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    $p = [Diagnostics.Process]::Start($psi)
    $script:errTask = $p.StandardError.ReadToEndAsync()
    if ($script:pw) { $p.StandardInput.WriteLine($script:pw); $p.StandardInput.Flush() }
    $first = $p.StandardOutput.ReadLine()
    if ($first -notlike 'HAZIR*') {
        $p.WaitForExit(60000) | Out-Null
        Log "Yazıcı başlamadı: $first"
        Log $script:errTask.Result
        throw 'yazıcı başlamadı'
    }
    $script:drive = $first.Substring(6).Trim()
    Log "Bellek yazılabilir bağlandı: $script:drive"
    return $p
}

function Stop-Writer($p, [int]$waitSec) {
    if (-not $p) { return }
    if (-not $p.HasExited) {
        try { $p.StandardInput.WriteLine('BITIR') } catch { }
        if (-not $p.WaitForExit($waitSec * 1000)) { Log "Yazıcı $waitSec sn'de kapanmadı, sonlandırılıyor"; $p.Kill(); $p.WaitForExit(10000) | Out-Null }
    }
    try { $o = $p.StandardOutput.ReadToEnd().Trim(); if ($o) { Log "Yazıcı: $o" } } catch { }
    $e = $script:errTask.Result.Trim()
    if ($e) {
        $text = 'Yazıcı günlüğü:' + "`n" + (($e -split "`n" | ForEach-Object { "  $($_.TrimEnd())" }) -join "`n")
        Write-Host $text
        Add-Content -Path $report -Value $text -Encoding UTF8
    }
}

# Yazılmayan öbür APFS bölümlerinin kimliği: blok 0 + checkpoint alanı parmak izi ve dolu blokların özeti (tüm bölümün özeti
# ucuz bellekte her okumada değişiyor — hiç yazılmamış flash; Parça 1 ölçümü)
$script:prints = @{}
function Other-Prints([int]$n, [string]$tag) {
    for ($p = 0; $p -lt 8; $p++) {
        if ($p -eq $script:part) { continue }
        $fp = @(Native $apfs @('info', "\\.\PhysicalDrive$n", '--partition', "$p", '--fingerprint') | Where-Object { $_ -match 'Parmak izi' })
        if ($fp.Count -eq 0) { break }
        $used = @(Native $dw @('--used-hash', "$n", '--partition', "$p") | Where-Object { $_ -like 'DOLU*' })
        $now = "$($fp[0].Trim()) | $($used -join ' ')"
        Log "Öbür bölüm $p ($tag): $now"
        if ($tag -eq 'önce') { $script:prints[$p] = $now }
        else { Log ("Öbür bölüm $p önce = sonra: " + $(if ($script:prints[$p] -eq $now) { 'AYNI' } else { 'FARKLI' })) }
    }
}

function Hash-Tree([string]$dir) {
    Get-ChildItem $dir -Recurse -File | ForEach-Object { '{0} {1}' -f $_.FullName.Substring($dir.Length), (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object
}

$proc = $null
try {
    # 1) Belleği bul ve onay al
    $s = Find-Stick
    if (-not $s) {
        Log "Adı '$Ad' olan tek bir USB APFS bellek bulunamadı (takılı değilse çıkarıp yeniden takın). Ayrıntı:"
        Native $dw @('--usb-find', $Ad) | ForEach-Object { Log "  $_" }
        throw 'bellek bulunamadı'
    }
    Log "Bellek: Disk $($s.Number) — $($s.Desc), $($s.Size), birim adı $Ad (APFS bölümü $($s.Part))"
    if ($Adim -ne 'denetle') { Say ("Disk $($s.Number): $($s.Desc), $($s.Size), birim adı '$Ad'.`n" +
        "Deneme süresince kurulu APFS for Windows birkaç dakikalığına durdurulur (diğer Mac diskleri o sürede görünmez; " +
        "üzerlerinde açık dosya varsa önce kapatın) ve sonunda kendiliğinden yeniden başlatılır.`n" +
        "Bu bellekteki her şey deneme amaçlı mı ve kaybolması sorun değil mi? (E/H)") }
    if ($Adim -ne 'denetle') {
        if ((Read-Host) -notmatch '^[eE]') { Log 'Kullanıcı onay vermedi; hiçbir şey yazılmadı.'; exit 0 }
    }
    $n = $s.Number
    if ($Parola) {
        $sec = Read-Host "'$Ad' biriminin parolası (ekranda görünmez; hiçbir yere kaydedilmez)" -AsSecureString
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
        try { $script:pw = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }
    Other-Prints $n 'önce'
    if ($Adim -eq 'denetle') {
        Log '=== Denetim (salt okunur)'
        Full-Check $n
        Save-Manifest $n 'denetle'
        Say "Bitti. Rapor: $report"
        return
    }

    if ($Adim -eq 'yuzey') {
        Stop-InstalledService
        Log '=== Yüzey testi: yaz + hemen geri oku'
        Native $dw @('--usb-surface', "$n", '--name', $Ad, '--mb', '2048') | ForEach-Object { Log "  $_" }
        Say 'Şimdi belleği çıkarın (Güvenle çıkar gerekmez; şu an hiçbir program kullanmıyor), 5 saniye bekleyip yeniden takın.'
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ((Get-Disk -Number $n -ErrorAction SilentlyContinue) -and $sw.Elapsed.TotalMinutes -lt 5) { Start-Sleep -Milliseconds 500 }
        $sw.Restart()
        do { Start-Sleep -Seconds 2; $s2 = Find-Stick } while (-not $s2 -and $sw.Elapsed.TotalMinutes -lt 5)
        if (-not $s2) { throw 'bellek 5 dakika içinde yeniden görülmedi' }
        Log '=== Yüzey testi: çıkar-tak sonrası yeniden oku'
        Native $dw @('--usb-surface', "$($s2.Number)", '--name', $Ad, '--mb', '2048', '--verify-only') | ForEach-Object { Log "  $_" }
        Full-Check $s2.Number
        Say "Bitti. Rapor: $report"
        return
    }

    if ($Adim -eq 'mac-hazirla') {
        Log '=== Mac ziyareti hazırlığı'
        Full-Check $n
        # Mac kitinin manifest() biçimi: "<sha256>  ./yol", LC_ALL=C sıralı; aynı iç klasörler ve kit klasörü dışarıda
        $utf8 = New-Object Text.UTF8Encoding $false
        $rows = foreach ($l in (ApfsRun @('ls', "\\.\PhysicalDrive$n", '--partition', "$script:part", '/', '-r', '--hash'))) {
            $f = $l -split "`t"
            if ($f.Count -ne 4 -or $f[1] -ne 'f') { continue }
            if ($f[3] -notmatch '^[0-9A-F]{64}$') { throw "okunamayan dosya: $($f[0]) → $($f[3])" }
            $pth = './' + $f[0]
            if ($pth -match '^\./(\.fseventsd|\.Spotlight-V100|\.Trashes|\.TemporaryItems|APFS-Mac-Kiti)/' -or $pth -match '/(\.metadata_never_index|\.DS_Store)$') { continue }
            [pscustomobject]@{ Key = [BitConverter]::ToString($utf8.GetBytes($pth)).Replace('-', '').ToLowerInvariant(); Line = "$($f[3].ToLowerInvariant())  $pth" }
        }
        $sorted = @($rows | Sort-Object Key -CaseSensitive | ForEach-Object Line)
        New-Item -ItemType Directory -Force $src | Out-Null
        [IO.File]::WriteAllText((Join-Path $src "$Ad.windows-manifest"), (($sorted | ForEach-Object { "$_`n" }) -join ''), $utf8)
        Copy-Item (Join-Path $root 'tools\mac\apfs-mac-kit.sh'), (Join-Path $root 'tools\mac\MAC-ZIYARETI.md') $src
        Log "Parmak izi listesi: $($sorted.Count) dosya"
        Stop-InstalledService
        $proc = Start-Writer $n
        $drive = $script:drive
        robocopy $src "$drive\APFS-Mac-Kiti" /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
        $diff = Compare-Object @(Hash-Tree $src) @(Hash-Tree "$drive\APFS-Mac-Kiti")
        Log ("Kit belleğe yazıldı: robocopy çıkış {0}, fark {1}" -f $LASTEXITCODE, ($diff | Measure-Object).Count)
        Stop-Writer $proc 180; $proc = $null
        Log '=== Düzgün kapatma sonrası'
        Full-Check $n
        Other-Prints $n 'sonra'
        Say "Hazır. Belleği tepsideki 'Güvenle çıkar' ile çıkarıp Mac'e götürün. Rapor: $report"
        return
    }

    # 2) Mac'in yazdığı hâli (salt okunur) ve kurulu hizmetin bırakması
    Log '=== Başlangıç durumu'
    Full-Check $n
    Save-Manifest $n 'once'
    Stop-InstalledService

    # 3) Yazılabilir bağla
    $proc = Start-Writer $n
    $drive = $script:drive

    if ($Adim -eq 'yaz') {
        # 4a) Otomatik kısım: Türkçe adlı ağaç, büyük dosya, geri okuma, Office tarzı kaydetme, silme
        $rnd = New-Object Random 7
        $dirs = 'Belgeler', 'Fotoğraflar\2025 Yaz', 'ığüşöç İĞÜŞÖÇ'
        foreach ($d in $dirs) { New-Item -ItemType Directory -Force (Join-Path $src $d) | Out-Null }
        for ($i = 0; $i -lt 500; $i++) {
            $b = New-Object byte[] ($rnd.Next(0, 40000)); $rnd.NextBytes($b)
            [IO.File]::WriteAllBytes((Join-Path $src "$($dirs[$i % 3])\dosya-$i-ğ.dat"), $b)
        }
        $big = New-Object byte[] (100MB); $rnd.NextBytes($big)
        [IO.File]::WriteAllBytes((Join-Path $src 'büyük.bin'), $big)
        $mb = (Get-ChildItem $src -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
        $sw = [Diagnostics.Stopwatch]::StartNew()
        robocopy $src "$drive\Deneme" /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
        Log ('İçeri kopyalama: robocopy çıkış {0}, {1:N1} MB, {2:N1} sn ({3:N1} MB/sn)' -f $LASTEXITCODE, $mb, $sw.Elapsed.TotalSeconds, ($mb / $sw.Elapsed.TotalSeconds))
        $sw.Restart()
        robocopy "$drive\Deneme" $back /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
        Log ('Geri okuma: robocopy çıkış {0}, {1:N1} sn' -f $LASTEXITCODE, $sw.Elapsed.TotalSeconds)
        $diff = Compare-Object @(Hash-Tree $src) @(Hash-Tree $back)
        Log ('Karşılaştırma: fark {0}' -f ($diff | Measure-Object).Count)
        Set-Content -Path "$drive\Deneme\belge.docx" -Value 'eski sürüm' -Encoding UTF8
        Set-Content -Path "$drive\Deneme\~belge.tmp" -Value 'yeni sürüm' -Encoding UTF8
        Remove-Item "$drive\Deneme\belge.docx"; Rename-Item "$drive\Deneme\~belge.tmp" 'belge.docx'
        Remove-Item "$drive\Deneme\Belgeler" -Recurse -Force
        Log ('Office tarzı kaydetme sonrası içerik: ' + (Get-Content "$drive\Deneme\belge.docx"))

        # 4b) Elle kısım (-Kisa ile atlanır)
        if (-not $Kisa) {
        Say @"
Otomatik kısım bitti. Şimdi Gezgin'de $drive sürücüsünü açın ve şunları deneyin:
  1. Masaüstünden ya da Belgeler'den küçük bir klasörü sürükleyip bırakın.
  2. Mac'te koyduğunuz dosyalardan birinin adını değiştirin.
  3. Bir dosyayı silin (Delete tuşu).
  4. Not Defteri ya da Word ile yeni bir belge yazıp belleğe kaydedin; kapatıp yeniden açın, değiştirip yeniden kaydedin.
  5. Bir fotoğrafı açın.
Bir şey garip görünürse (hata mesajı, donma) not alın. Bitince bu pencereye dönüp Enter'a basın.
"@
        Read-Host | Out-Null
        }
        $rootItems = @(Get-ChildItem $drive -Force)
        $odd = @($rootItems | Where-Object { $_.Name -like '$*' -or $_.Name -eq 'desktop.ini' -or $_.Name -eq 'System Volume Information' -or $_.Name -eq 'Thumbs.db' })
        Log ("Kökte {0} giriş; Windows artığı: {1}" -f $rootItems.Count, $(if ($odd) { ($odd | ForEach-Object Name) -join ', ' } else { 'yok' }))

        # 4c) Düzgün kapat, denetle
        Stop-Writer $proc 180; $proc = $null
        Log '=== Kapatma sonrası'
        Full-Check $n
        Save-Manifest $n 'sonra'
        Other-Prints $n 'sonra'
        Say "Bitti. Bellek birazdan yine salt okunur görünecek; çıkarmak için tepsideki 'Güvenle çıkar'. Rapor: $report"
    }
    else {
        # 5) Kablo çekme: ~3 GB'lık kopyalama sürerken bellek çekilir (hızlı yuvada 1,5 GB 12 sn'de bitti, 2026-10-05)
        New-Item -ItemType Directory -Force $src | Out-Null
        $rnd = New-Object Random 11
        $chunk = New-Object byte[] (16MB)
        $sha = [Security.Cryptography.SHA256]::Create()
        $expect = @{}
        for ($i = 0; $i -lt $Parts; $i++) {
            $rnd.NextBytes($chunk); $nm = 'parca-{0:D3}.bin' -f $i
            [IO.File]::WriteAllBytes((Join-Path $src $nm), $chunk)
            $expect[$nm] = [BitConverter]::ToString($sha.ComputeHash($chunk)).Replace('-', '')
        }
        $kdir = "Kablo-$(Get-Date -Format HHmmss)"
        $copy = Start-Process robocopy -ArgumentList "`"$src`" `"$drive\$kdir`" /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP" -PassThru -WindowStyle Hidden
        Log "Kopyalama başladı ($($Parts * 16) MB) → $kdir"
        Start-Sleep -Seconds 6   # en az bir kayıt (5 sn'lik commit) olsun, kopyalama da sürsün
        if ($copy.HasExited) { Log 'UYARI: kopyalama bellek çekilmeden bitti; bu koşu "yazma sürerken çekme" sayılmaz.' }
        Say 'ŞİMDİ belleği USB yuvasından çekin (Güvenle çıkar KULLANMADAN).'
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ((Get-Disk -Number $n -ErrorAction SilentlyContinue) -and $sw.Elapsed.TotalMinutes -lt 5) { Start-Sleep -Milliseconds 500 }
        if (Get-Disk -Number $n -ErrorAction SilentlyContinue) { throw 'bellek 5 dakika içinde çekilmedi' }
        Log ("Bellek çekildi; kopyalama {0}." -f $(if ($copy.HasExited) { "bitmişti (çıkış $($copy.ExitCode))" } else { 'sürüyordu' }))
        Start-Sleep -Seconds 3
        if (-not $copy.HasExited) { $copy.Kill() }
        Stop-Writer $proc 30; $proc = $null

        Say 'Şimdi belleği yeniden takın.'
        $sw.Restart()
        do { Start-Sleep -Seconds 2; $s2 = Find-Stick } while (-not $s2 -and $sw.Elapsed.TotalMinutes -lt 5)
        if (-not $s2) { throw 'bellek 5 dakika içinde yeniden görülmedi' }
        Log "Bellek yeniden görüldü: Disk $($s2.Number)"
        Start-Sleep -Seconds 3
        Log '=== Kablo çekme sonrası'
        Full-Check $s2.Number
        $left = @(ApfsRun @('ls', "\\.\PhysicalDrive$($s2.Number)", '--partition', "$script:part", "/$kdir", '--hash') | Where-Object { ($_ -split "`t")[1] -eq 'f' })
        $good = 0; $wrong = @(); $lastNm = ($left | ForEach-Object { ($_ -split "`t")[0].Split('/')[-1] } | Sort-Object | Select-Object -Last 1)
        $partial = $null
        foreach ($l in $left) {
            $f = $l -split "`t"; $nm = $f[0].Split('/')[-1]
            if ($expect[$nm] -eq $f[3]) { $good++ }
            elseif ($nm -eq $lastNm) { $partial = "$nm ($($f[2]) bayt)" }   # çekildiği anda yazılıyordu: yarım kalması beklenen
            else { $wrong += "$nm ($($f[2]) bayt)" }
        }
        Log "$kdir klasöründe kalan dosya: $($left.Count) / $Parts (son birkaç saniyenin kopyası kaybolabilir; bu beklenen)"
        Log "İçerik: $good dosya birebir doğru$(if ($partial) { "; son dosya yarım (beklenen): $partial" })"
        Log $(if ($wrong) { "BOZUK (beklenmeyen): $($wrong -join ', ')" } else { 'Beklenmeyen bozulma: YOK' })
        Save-Manifest $s2.Number 'kablo'
        Say "Bitti. Rapor: $report"
    }
}
catch { Log "HATA: $($_.Exception.Message)" }
finally {
    if ($proc) { Stop-Writer $proc 60 }
    $script:pw = $null
    Start-InstalledService
    foreach ($d in $src, $back) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
    Log "Bitti. Rapor: $report"
}
