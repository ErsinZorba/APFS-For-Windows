<#
.SYNOPSIS
  (YÖNETİCİ) Test VHD'sini Windows'a SALT OKUNUR sanal disk olarak takar ve apfs.exe'nin güncel derlemesini
  WinFsp Launcher'a yeniden kaydeder. Böylece Launcher yolu, gerçek bir \\.\PhysicalDriveN ile uçtan uca test edilebilir.
.PARAMETER Detach
  Takılı test VHD'sini çıkarır.
#>
param(
    [string] $Vhd = (Join-Path $env:TEMP 'claude\apfs-test\basic.vhd'),
    [switch] $Detach
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host 'Bu betik yönetici olarak çalıştırılmalı.' -ForegroundColor Red; exit 1 }
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$report = Join-Path $env:TEMP 'claude\apfs-test\vhd-attach.txt'

if ($Detach) {
    Dismount-DiskImage -ImagePath $Vhd | Out-Null
    "Çıkarıldı: $Vhd" | Tee-Object -FilePath $report
    exit 0
}

& {
    "--- apfs register (güncel derleme) ---"
    & (Join-Path $root 'src\Apfs.Cli\bin\Debug\net10.0-windows\apfs.exe') register 2>&1
    "--- VHD salt okunur takılıyor ---"
    $img = Mount-DiskImage -ImagePath $Vhd -Access ReadOnly -PassThru
    Start-Sleep -Seconds 2
    $disk = $img | Get-DiskImage | Get-Disk
    "DiskNumber=$($disk.Number)"
    "IsReadOnly=$($disk.IsReadOnly)"
    Get-Partition -DiskNumber $disk.Number | Select-Object PartitionNumber, DriveLetter, Size, GptType | Format-Table | Out-String
} *>&1 | Tee-Object -Variable lines
$lines | Out-File -FilePath $report -Encoding utf8
Write-Host "`nRapor: $report" -ForegroundColor Green
