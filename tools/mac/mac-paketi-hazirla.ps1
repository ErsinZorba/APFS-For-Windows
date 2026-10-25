<#
.SYNOPSIS
  Mac doğrulama paketi: verilen klasördeki bölümsüz .img'leri GPT disk görüntüsüne sarar; her .img için okuyucumuzun
  gördüğü dosya parmak izlerini Mac kitinin "manifest" biçiminde (<sha256>  ./yol, UTF-8 bayt sırasıyla) <img>.windows-manifest dosyasına yazar ve klasörü zip'ler.
  Mac'te: bash apfs-mac-kit.sh verify <klasör>  →  fsck_apfs + parmak izi karşılaştırması. Görüntülere yalnızca okur.
.EXAMPLE
  APFS_DEV_OUT=<klasör> ile yazma testlerini koşturun, sonra: .\tools\mac\mac-paketi-hazirla.ps1 <klasör>
#>
param([Parameter(Mandatory)][string]$Dir)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$apfs = Join-Path $root 'src\Apfs.Cli\bin\Debug\net10.0-windows\apfs.exe'
$mk = Join-Path $root 'tests\Apfs.ImageBuilder\bin\Debug\net10.0\apfs-mkimage.exe'
$utf8 = New-Object Text.UTF8Encoding $false
# Mac kitinin manifest() işlevinin dışarıda bıraktıkları
$skipDir = '^\./(\.fseventsd|\.Spotlight-V100|\.Trashes|\.TemporaryItems)/'
$skipName = '/(\.metadata_never_index|\.DS_Store)$'

$bad = 0
# Bölümsüz (çıplak kapsayıcı) görüntüleri GPT'ye sar: Mac kiti kapsayıcıyı GPT bölüm türünden bulur
foreach ($img in Get-ChildItem $Dir -Filter *.img) {
    $head = New-Object byte[] 36
    $fs = [IO.File]::OpenRead($img.FullName); [void]$fs.Read($head, 0, 36); $fs.Dispose()
    if ([Text.Encoding]::ASCII.GetString($head, 32, 4) -ne 'NXSB') { continue }
    $tmp = "$($img.FullName).gpt"
    & $mk wrap $img.FullName $tmp | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$($img.Name) sarılamadı" }
    Move-Item $tmp $img.FullName -Force
}
foreach ($img in Get-ChildItem $Dir -Filter *.img | Sort-Object Name) {
    $lines = & $apfs ls $img.FullName '/' -r --hash
    if ($LASTEXITCODE -ne 0) { Write-Host "$($img.Name): okunamadı" -ForegroundColor Red; $bad++; continue }
    $rows = foreach ($l in $lines) {
        $f = $l -split "`t"
        if ($f.Count -ne 4 -or $f[1] -ne 'f') { continue }
        if ($f[3] -notmatch '^[0-9A-F]{64}$') { Write-Host "$($img.Name): $($f[0]) → $($f[3])" -ForegroundColor Red; $bad++; continue }
        $p = './' + $f[0]
        if ($p -match $skipDir -or $p -match $skipName) { continue }
        # LC_ALL=C sort: UTF-8 bayt sırası (küçük harfli onaltılık gösterimin sözlük sırası aynıdır)
        [pscustomobject]@{ Key = [BitConverter]::ToString($utf8.GetBytes($p)).Replace('-', '').ToLowerInvariant(); Line = "$($f[3].ToLowerInvariant())  $p" }
    }
    $sorted = @($rows | Sort-Object Key -CaseSensitive | ForEach-Object Line)
    [IO.File]::WriteAllText("$($img.FullName).windows-manifest", (($sorted | ForEach-Object { "$_`n" }) -join ''), $utf8)
    Write-Host ("{0}: {1} dosya" -f $img.Name, $sorted.Count)
}
$kit = Join-Path $root 'tools\mac\apfs-mac-kit.sh'
Copy-Item $kit (Join-Path $Dir 'apfs-mac-kit.sh') -Force
$zip = "$((Resolve-Path $Dir).Path.TrimEnd('\')).zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path $Dir).Path, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Host ("Paket: {0} ({1:N0} MB){2}" -f $zip, ((Get-Item $zip).Length / 1MB), $(if ($bad) { "  — $bad sorun var!" } else { '' }))
