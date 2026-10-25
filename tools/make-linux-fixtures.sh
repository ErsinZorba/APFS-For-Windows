#!/bin/bash
# Testlerin kullandığı Linux (apfsprogs/mkapfs) örnek görüntülerini üretir: tests/Apfs.Tests/data/linux/*.img.gz
# WSL içinde çalışır (apfsprogs /opt/apfsprogs altında derlenmiş olmalı):
#   MSYS_NO_PATHCONV=1 wsl.exe -d Ubuntu -u root --exec bash /mnt/c/.../APFS-for-Windows/tools/make-linux-fixtures.sh
# mkapfs biçimlendirme zamanını yazdığı için çıktı her çalıştırmada bayt bayt aynı değildir; UUID'ler sabittir.
set -euo pipefail

HERE="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$HERE/tests/Apfs.Tests/data/linux"
MKAPFS=/opt/apfsprogs/mkapfs/mkapfs
APFSCK=/opt/apfsprogs/apfsck/apfsck
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
mkdir -p "$OUT"

make() { # make <ad> <boyut> [mkapfs seçenekleri...]
    local name="$1" size="$2"; shift 2
    truncate -s "$size" "$TMP/$name.img"
    "$MKAPFS" -U 11111111-2222-3333-4444-555555555555 -u 66666666-7777-8888-9999-aaaaaaaaaaaa "$@" "$TMP/$name.img" >/dev/null
    "$APFSCK" -cuw "$TMP/$name.img"
    gzip -9 -n -c "$TMP/$name.img" > "$OUT/$name.img.gz"
    echo "$name: $(stat -c %s "$OUT/$name.img.gz") bayt (apfsck temiz)"
}

make mk-ci-64m 64M -L "Linux CI"
make mk-cs-64m 64M -s -L "Linux CS"
make mk-ns-64m 64M -z -L "Linux NS"
make mk-ci-1g 1G -L "Linux 1G"
"$MKAPFS" -v
