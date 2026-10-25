#!/bin/bash
# Linux'un bağımsız APFS yazıcısıyla (linux-apfs-rw) dolu test görüntüleri üretir ve apfsck ile denetler.
# WSL içinde, apfs.ko yüklüyken çalışır (bkz. tools/wsl/load-apfs.sh). Çıktı: fixtures/linux/pop-*.img
#   MSYS_NO_PATHCONV=1 wsl.exe -d Ubuntu -u root --exec bash /mnt/c/.../APFS-for-Windows/tools/make-linux-populated.sh
set -euo pipefail

HERE="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$HERE/fixtures/linux"
MKAPFS=/opt/apfsprogs/mkapfs/mkapfs
APFSCK=/opt/apfsprogs/apfsck/apfsck
MNT=/mnt/apfs-pop
mkdir -p "$OUT" "$MNT"
bash "$HERE/tools/wsl/load-apfs.sh"

pattern() { yes 'APFS-for-Windows 0123456789' | head -c "$1" || true; } # yes SIGPIPE ile biter
cleanup() { umount "$MNT" 2>/dev/null || true; losetup -D 2>/dev/null || true; }
trap cleanup EXIT

populate() {
    local m="$1" big="$2"
    mkdir -p "$m/Belgeler/Alt Klasör" "$m/Boş Klasör" "$m/derin/a/b/c/d/e/f"
    printf 'merhaba dünya\n' > "$m/Belgeler/merhaba.txt"
    for n in "çalışma.txt" "ĞÜŞİÖÇ ğüşıöç.txt" "İstanbul ı i I İ.txt"; do printf '%s\n' "$n" > "$m/Belgeler/$n"; done
    printf 'alt\n' > "$m/Belgeler/Alt Klasör/alt.txt"
    : > "$m/bos.txt"
    head -c "$big" /dev/urandom > "$m/buyuk.bin"
    pattern 3145728 > "$m/orta.bin"
    truncate -s 50M "$m/seyrek.bin"; printf 'ORTA' | dd of="$m/seyrek.bin" bs=1 seek=26214400 conv=notrunc 2>/dev/null
    ln "$m/Belgeler/merhaba.txt" "$m/sert-baglanti.txt"
    ln -s "Belgeler/merhaba.txt" "$m/sembolik"
    # öznitelikler (sürücü hangi ad alanını destekliyorsa); desteklenmezse atlanır
    python3 - "$m" <<'PY' || echo "  öznitelik yazılamadı (atlandı)"
import os, sys
m = sys.argv[1]
for prefix in ("osx.", "user.", "trusted."):
    try:
        os.setxattr(f"{m}/Belgeler/merhaba.txt", prefix + "com.example.not", b"deneme")
        os.setxattr(f"{m}/orta.bin", prefix + "com.example.buyuk", os.urandom(8000))
        print("  öznitelik ad alanı:", prefix)
        break
    except OSError as e:
        last = e
else:
    raise last
PY
    printf 'dip\n' > "$m/derin/a/b/c/d/e/f/dip.txt"
    mkdir -p "$m/cok"; for i in $(seq 1 2000); do printf '%d\n' "$i" > "$m/cok/dosya-$i.txt"; done
    # değişiklikler: silme, ad değiştirme, üzerine yazma, kısaltma
    for i in $(seq 1 2000 | awk 'NR%3==0'); do rm "$m/cok/dosya-$i.txt"; done
    mv "$m/Belgeler/çalışma.txt" "$m/Belgeler/çalışma-yeni.txt"
    mv "$m/derin/a/b" "$m/taşınan"
    pattern 5000 > "$m/orta.bin.kopya"; truncate -s 1000 "$m/orta.bin.kopya"
    printf 'XXXX' | dd of="$m/orta.bin" bs=1 seek=4096 conv=notrunc 2>/dev/null
}

make_one() { # make_one <ad> <boyut> <büyük dosya bayt> [mkapfs seçenekleri]
    local name="$1" size="$2" big="$3"; shift 3
    local img="$OUT/$name.img"
    rm -f "$img"; truncate -s "$size" "$img"
    "$MKAPFS" -U 11111111-2222-3333-4444-555555555555 -u 66666666-7777-8888-9999-aaaaaaaaaaaa "$@" "$img" >/dev/null
    local loop; loop=$(losetup -f --show "$img")
    mount -t apfs -o readwrite "$loop" "$MNT"
    populate "$MNT" "$big"
    ( cd "$MNT" && find . -type f -print0 | sort -z | xargs -0 sha256sum ) > "$img.manifest"
    umount "$MNT"; losetup -d "$loop"
    if "$APFSCK" -cuw "$img"; then echo "$name: apfsck TEMİZ"; else echo "$name: apfsck HATALI"; fi
}

make_one pop-ci-256m 256M 20971520 -L "Linux Dolu"
make_one pop-cs-256m 256M 20971520 -s -L "Linux Dolu CS"
# Testlerin kullandığı küçük kopya (depoda sıkıştırılmış durur)
make_one pop-ci-64m 64M 1048576 -L "Linux Dolu 64"
mkdir -p "$HERE/tests/Apfs.Tests/data/linux"
gzip -9 -n -c "$OUT/pop-ci-64m.img" > "$HERE/tests/Apfs.Tests/data/linux/pop-ci-64m.img.gz"
cp "$OUT/pop-ci-64m.img.manifest" "$HERE/tests/Apfs.Tests/data/linux/pop-ci-64m.manifest"
ls -la "$HERE/tests/Apfs.Tests/data/linux/pop-ci-64m.img.gz"
