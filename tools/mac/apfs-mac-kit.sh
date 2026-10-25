#!/bin/bash
# APFS for Windows — Mac kiti.
#
#   bash apfs-mac-kit.sh fixtures [çıktı klasörü]   Test disk görüntülerini macOS'un kendisiyle üretir (Faz 0.2).
#   bash apfs-mac-kit.sh verify   <klasör>          Windows'ta yazılmış görüntüleri fsck_apfs ile denetler ve dosya
#                                                   parmak izlerini Windows'un bıraktığı listeyle karşılaştırır.
#   bash apfs-mac-kit.sh usb      [birim adı...]    Windows'ta yazılmış GERÇEK test belleğini (varsayılan USBTEST) denetler:
#                                                   parmak izleri, fsck_apfs (birim ayrılarak, salt okunur), sonra Mac'ten
#                                                   belleğe deneme dosyaları yazar (Windows'ta geri okunacak). Birden çok
#                                                   ad verilebilir (çok bölümlü bellek: ör. "USBTEST non-password"); şifreli
#                                                   birim kilitliyse önce `diskutil apfs unlockVolume` ile parola sorulur.
#   bash apfs-mac-kit.sh hdd      [birim adı...]    Kullanıcının test için sildiği çok bölümlü HARİCİ diskin adı verilen birimlerini
#                                                   (varsayılan: A-Duz B-Sifreli C1-Duz C2-Sifreli C3-Duyarli) Mac'e özgü içerikle
#                                                   doldurur (sıkıştırılmış, klon, sert bağlantı, kaynak çatalı, Windows'ta yasak
#                                                   adlar…), parmak izi listelerini ve raporu kitin yanına yazar, İlk Yardım çalıştırır.
#                                                   İç diske ve dolu birime dokunmaz.
#
# fixtures/verify hiçbir gerçek diske dokunmaz: yalnızca kendi oluşturduğu ya da verilen .img dosyalarıyla çalışır.
# usb yalnızca adı verilen birime (kullanıcının test için sildiği bellek) "Mac-ten-gelen" klasörünü yazar.
# macOS'un yerleşik bash 3.2'siyle uyumludur (ilişkisel dizi, mapfile vb. yok).

set -u
set -o pipefail

MODE="${1:-}"
STAMP="$(date +%Y%m%d-%H%M%S)"
REPORT=""

say()  { printf '%s\n' "$*"; [ -n "$REPORT" ] && printf '%s\n' "$*" >> "$REPORT"; return 0; }
fail() { say "HATA: $*"; }
die()  { say "DURDU: $*"; exit 1; }

[ "$(uname)" = "Darwin" ] || die "Bu betik yalnızca macOS'ta çalışır."

trim() { sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//'; }

# ---------------------------------------------------------------------------------------------------------------------
# Görüntü yardımcıları

# make_image <ad> <boyut MB> <dosya sistemi> <birim adı>  →  $OUT/<ad>.img (düz, GPT bölümlü disk görüntüsü)
# macOS 27'de `hdiutil create -format UDRW` "-srcfolder gerekir" diyerek reddediyor (2026-10-05). Bu yüzden: boş ham dosya →
# hdiutil attach -nomount (ham görüntü) → diskutil eraseDisk (GPT + APFS) → detach. Eski macOS'ta önce hdiutil create denenir.
make_image() {
    local name="$1" size="$2" fs="$3" vol="$4" out whole
    rm -f "$OUT/$name.dmg" "$OUT/$name.img"
    if out="$(hdiutil create -size "${size}m" -fs "$fs" -volname "$vol" -layout GPTSPUD -format UDRW -o "$OUT/$name" 2>&1)" \
        && [ -f "$OUT/$name.dmg" ]; then
        mv "$OUT/$name.dmg" "$OUT/$name.img" || return 1
        return 0
    fi
    rm -f "$OUT/$name.dmg"
    if ! dd if=/dev/zero of="$OUT/$name.img" bs=1m count=0 seek="$size" 2>/dev/null; then fail "$name: boş dosya oluşturulamadı"; return 1; fi
    out="$(hdiutil attach -nomount -imagekey diskimage-class=CRawDiskImage "$OUT/$name.img" 2>&1)" || { fail "$name bağlanamadı: $out"; return 1; }
    whole="$(printf '%s\n' "$out" | grep '^/dev/' | head -n 1 | awk '{print $1}')"
    [ -n "$whole" ] || { fail "$name: aygıt adı anlaşılamadı: $out"; return 1; }
    if ! out="$(diskutil eraseDisk "$fs" "$vol" GPT "$whole" 2>&1)"; then
        fail "$name: diskutil eraseDisk başarısız: $out"; detach "$whole"; return 1
    fi
    detach "$whole"
    return 0
}

# attach_rw <img>  →  ATT_WHOLE (ör. /dev/disk4), ATT_MNT (/Volumes/...)
attach_rw() {
    local out
    ATT_WHOLE=""; ATT_MNT=""
    out="$(hdiutil attach -nobrowse -owners on -imagekey diskimage-class=CRawDiskImage "$1" 2>&1)" || { fail "bağlanamadı: $1: $out"; return 1; }
    ATT_WHOLE="$(printf '%s\n' "$out" | head -n 1 | awk -F'\t' '{print $1}' | trim)"
    ATT_MNT="$(printf '%s\n' "$out" | awk -F'\t' '/\/Volumes\//{print $NF}' | head -n 1 | trim)"
    [ -n "$ATT_WHOLE" ] && [ -n "$ATT_MNT" ] || { fail "bağlama çıktısı anlaşılamadı: $out"; return 1; }
    return 0
}

# attach_nomount <img>  →  ATT_WHOLE, ATT_CONT (kapsayıcı aygıtı), ATT_VOLS (birim aygıtları, boşlukla ayrılmış)
attach_nomount() {
    local out
    ATT_WHOLE=""; ATT_CONT=""; ATT_VOLS=""
    out="$(hdiutil attach -nomount -readonly -imagekey diskimage-class=CRawDiskImage "$1" 2>&1)" || { fail "bağlanamadı: $1: $out"; return 1; }
    ATT_WHOLE="$(printf '%s\n' "$out" | head -n 1 | awk -F'\t' '{print $1}' | trim)"
    ATT_CONT="$(printf '%s\n' "$out" | awk -F'\t' 'toupper($2) ~ /EF57347C/ {print $1}' | head -n 1 | trim)"
    ATT_VOLS="$(printf '%s\n' "$out" | awk -F'\t' 'toupper($2) ~ /41504653/ {print $1}' | trim | tr '\n' ' ')"
    [ -n "$ATT_WHOLE" ] || { fail "bağlama çıktısı anlaşılamadı: $out"; return 1; }
    return 0
}

detach() { [ -n "${1:-}" ] && hdiutil detach -quiet "$1" >/dev/null 2>&1 || hdiutil detach -force -quiet "$1" >/dev/null 2>&1; return 0; }

# Spotlight ve fseventsd günlüğü test görüntüsünü kirletmesin
quiet_volume() {
    local m="$1"
    touch "$m/.metadata_never_index" 2>/dev/null
    mkdir -p "$m/.fseventsd" 2>/dev/null && touch "$m/.fseventsd/no_log" 2>/dev/null
    mdutil -i off "$m" >/dev/null 2>&1
    return 0
}

# manifest <bağlama noktası> <çıktı>: "sha256  ./yol" satırları (macOS'un iç klasörleri hariç), sıralı
manifest() {
    ( cd "$1" && find . -type f \
        ! -path './.fseventsd/*' ! -path './.Spotlight-V100/*' ! -path './.Trashes/*' ! -path './.TemporaryItems/*' \
        ! -name '.metadata_never_index' ! -name '.DS_Store' -print0 \
      | xargs -0 shasum -a 256 ) | LC_ALL=C sort -k 2 > "$2"
}

# fsck_img <img>: görüntüyü bağlamadan fsck_apfs -n çalıştırır; sonucu rapora yazar. 0 = temiz
fsck_img() {
    local img="$1" rc log
    attach_nomount "$img" || return 1
    if [ -z "$ATT_CONT" ]; then fail "$(basename "$img"): APFS kapsayıcısı bulunamadı"; detach "$ATT_WHOLE"; return 1; fi
    log="$(fsck_apfs -n "$ATT_CONT" 2>&1)"; rc=$?
    printf '%s\n' "$log" > "$img.fsck.txt"
    if [ $rc -eq 0 ] && ! printf '%s\n' "$log" | grep -qi 'warning\|uyarı'; then
        say "  fsck_apfs: TEMİZ  ($(basename "$img"))"
    elif [ $rc -eq 0 ]; then
        say "  fsck_apfs: UYARILI ($(basename "$img")) — ayrıntı: $(basename "$img").fsck.txt"
        printf '%s\n' "$log" | grep -i 'warning\|uyarı' | sed 's/^/    /' | while IFS= read -r l; do say "$l"; done
    else
        say "  fsck_apfs: HATALI (çıkış kodu $rc) ($(basename "$img")) — ayrıntı: $(basename "$img").fsck.txt"
        printf '%s\n' "$log" | tail -n 15 | sed 's/^/    /' | while IFS= read -r l; do say "$l"; done
    fi
    detach "$ATT_WHOLE"
    return $rc
}

# Sıkıştırılabilir, kendini tekrarlayan içerik (Windows tarafı aynısını üretebilsin diye belirlenimci)
pattern() { yes 'APFS-for-Windows 0123456789' | head -c "$1"; }

# ---------------------------------------------------------------------------------------------------------------------
# Görüntüleri dolduran adımlar

fill_full() {
    local m="$1" i d src
    quiet_volume "$m"
    mkdir -p "$m/Belgeler/Alt Klasör" "$m/Boş Klasör"
    printf 'merhaba dünya\n' > "$m/Belgeler/merhaba.txt"
    for n in "çalışma.txt" "ĞÜŞİÖÇ ğüşıöç.txt" "İstanbul ı i I İ.txt" "a b  c.txt"; do printf '%s\n' "$n" > "$m/Belgeler/$n"; done
    printf 'alt\n' > "$m/Belgeler/Alt Klasör/alt.txt"
    : > "$m/bos.txt"
    dd if=/dev/urandom of="$m/buyuk.bin" bs=1m count=200 2>/dev/null
    pattern 3145728 > "$m/orta.bin"
    # seyrek dosya: 100 MB, ortasında 4 bayt
    dd if=/dev/null of="$m/seyrek.bin" bs=1 seek=104857600 2>/dev/null
    printf 'ORTA' | dd of="$m/seyrek.bin" bs=1 seek=52428800 conv=notrunc 2>/dev/null
    # sıkıştırılmış dosyalar (ditto --hfsCompression): biri öznitelik içi (küçük), biri kaynak çatalında (büyük)
    src="$(mktemp -d)"
    pattern 2000 > "$src/kucuk.txt"; pattern 2097152 > "$src/buyuk.txt"
    ditto --hfsCompression "$src/kucuk.txt" "$m/sikistirilmis-kucuk.txt"
    ditto --hfsCompression "$src/buyuk.txt" "$m/sikistirilmis-buyuk.txt"
    rm -rf "$src"
    cp -c "$m/buyuk.bin" "$m/buyuk-klon.bin"                 # klon (bloklar paylaşılır)
    ln "$m/Belgeler/merhaba.txt" "$m/sert-baglanti.txt"     # sert bağlantı
    ln -s "Belgeler/merhaba.txt" "$m/sembolik"              # sembolik bağ
    xattr -w com.example.not "deneme" "$m/Belgeler/merhaba.txt"
    printf 'gizli\n' > "$m/gizli.txt"; chflags hidden "$m/gizli.txt"
    d="$m/derin"; for i in $(seq 1 30); do d="$d/k$i"; done; mkdir -p "$d"; printf 'dip\n' > "$d/dip.txt"
    mkdir -p "$m/cok"; for i in $(seq 1 10000); do printf '%d\n' "$i" > "$m/cok/dosya-$i.txt"; done
    ls -lO "$m" > "$OUT/tam-ci.ls-lO.txt" 2>&1
    return 0
}

fill_base() {
    local m="$1"
    quiet_volume "$m"
    mkdir -p "$m/Belgeler" "$m/Eski"
    printf 'merhaba dünya\n' > "$m/Belgeler/merhaba.txt"
    pattern 3145728 > "$m/orta.bin"
    return 0
}

# Altın işlemler: Windows tarafı aynılarını birebir uygulayıp yapıyı karşılaştıracak (bkz. ops.txt)
golden_op() {
    local op="$1" m="$2"
    case "$op" in
        olustur)
            mkdir "$m/Yeni Klasör"
            printf 'APFS for Windows\n' > "$m/Yeni Klasör/a.txt"
            pattern 5242880 > "$m/b.bin"
            : > "$m/c-bos.txt" ;;
        adlandir)
            mv "$m/Belgeler/merhaba.txt" "$m/Belgeler/selam.txt"
            mv "$m/orta.bin" "$m/Eski/orta.bin"
            mv "$m/Eski" "$m/Eski Yeni" ;;
        sil)
            rm "$m/Belgeler/merhaba.txt"
            rm "$m/orta.bin"
            rmdir "$m/Eski" ;;
        degistir)
            dd if=/dev/null of="$m/orta.bin" bs=1 seek=1048576 2>/dev/null               # 1 MB'a kısalt
            printf 'ek satır\n' >> "$m/Belgeler/merhaba.txt"                               # sona ekle
            printf 'XXXX' | dd of="$m/orta.bin" bs=1 seek=4096 conv=notrunc 2>/dev/null   # ortasına yaz
            dd if=/dev/null of="$m/Belgeler/merhaba.txt" bs=1 seek=8192 2>/dev/null ;;    # seyrek uzat
    esac
}

write_ops_txt() {
    cat > "$OUT/ops.txt" <<'EOF'
# Altın işlemler (her biri altin-taban.img'nin ayrı bir kopyasına uygulanır). İçerik: pattern(n) =
# "APFS-for-Windows 0123456789\n" satırının tekrarı, ilk n bayt.
taban:    mkdir Belgeler, Eski; Belgeler/merhaba.txt = "merhaba dünya\n"; orta.bin = pattern(3145728)
olustur:  mkdir "Yeni Klasör"; "Yeni Klasör/a.txt" = "APFS for Windows\n"; b.bin = pattern(5242880); c-bos.txt = boş
adlandir: Belgeler/merhaba.txt -> Belgeler/selam.txt; orta.bin -> Eski/orta.bin; Eski -> "Eski Yeni"
sil:      rm Belgeler/merhaba.txt; rm orta.bin; rmdir Eski
degistir: orta.bin 1048576'ya kısalt; merhaba.txt sonuna "ek satır\n"; orta.bin 4096'ya "XXXX"; merhaba.txt 8192'ye seyrek uzat
EOF
}

# ---------------------------------------------------------------------------------------------------------------------

do_fixtures() {
    OUT="${1:-$HOME/Desktop/apfs-kit-$STAMP}"
    mkdir -p "$OUT" || die "çıktı klasörü oluşturulamadı: $OUT"
    REPORT="$OUT/rapor.txt"; : > "$REPORT"
    say "APFS for Windows — Mac kiti (fixtures) $STAMP"
    say "macOS: $(sw_vers -productVersion) ($(sw_vers -buildVersion)), $(uname -m)"
    say "Çıktı: $OUT"
    say ""

    say "1/6 Boş görüntüler"
    make_image bos-ci 512 "APFS" "BOS" && fsck_img "$OUT/bos-ci.img"
    make_image bos-cs 512 "Case-sensitive APFS" "BOSCS" && fsck_img "$OUT/bos-cs.img"

    say "2/6 Dolu görüntü (birkaç dakika sürebilir)"
    if make_image tam-ci 1536 "APFS" "TAM" && attach_rw "$OUT/tam-ci.img"; then
        fill_full "$ATT_MNT"
        manifest "$ATT_MNT" "$OUT/tam-ci.img.manifest"
        detach "$ATT_WHOLE"
        fsck_img "$OUT/tam-ci.img"
    fi

    say "3/6 Harf duyarlı dolu görüntü"
    if make_image tam-cs 512 "Case-sensitive APFS" "TAMCS" && attach_rw "$OUT/tam-cs.img"; then
        quiet_volume "$ATT_MNT"
        printf 'kucuk\n' > "$ATT_MNT/dosya.txt"; printf 'BUYUK\n' > "$ATT_MNT/DOSYA.txt"
        printf 'nfc\n' > "$ATT_MNT/$(printf 'caf\xc3\xa9')"            # é tek karakter (NFC)
        printf 'nfd\n' > "$ATT_MNT/$(printf 'cafe\xcc\x81')"           # e + birleşen vurgu (NFD)
        manifest "$ATT_MNT" "$OUT/tam-cs.img.manifest"
        detach "$ATT_WHOLE"
        fsck_img "$OUT/tam-cs.img"
    fi

    say "4/6 Altın işlem görüntüleri"
    write_ops_txt
    if make_image altin-taban 512 "APFS" "ALTIN" && attach_rw "$OUT/altin-taban.img"; then
        fill_base "$ATT_MNT"
        detach "$ATT_WHOLE"
        for op in olustur adlandir sil degistir; do
            cp "$OUT/altin-taban.img" "$OUT/altin-$op.img"
            if attach_rw "$OUT/altin-$op.img"; then
                golden_op "$op" "$ATT_MNT"
                manifest "$ATT_MNT" "$OUT/altin-$op.img.manifest"
                detach "$ATT_WHOLE"
                fsck_img "$OUT/altin-$op.img"
            fi
        done
        fsck_img "$OUT/altin-taban.img"
    fi

    say "5/6 Şifreli kardeş birimli kapsayıcı (yazma kapısı bunu reddetmeli)"
    if make_image sifreli-kardes 512 "APFS" "ACIK" && attach_rw "$OUT/sifreli-kardes.img"; then
        quiet_volume "$ATT_MNT"
        printf 'acik\n' > "$ATT_MNT/acik.txt"
        cont="$(diskutil info "$ATT_MNT" | awk -F': *' '/APFS Container:/{print $2}' | trim)"
        if [ -n "$cont" ] && diskutil apfs addVolume "$cont" APFS SIFRELI -passphrase "Deneme123" >/dev/null 2>&1; then
            say "  şifreli birim eklendi (parola: Deneme123)"
        else
            fail "şifreli birim eklenemedi (kapsayıcı: ${cont:-?})"
        fi
        detach "$ATT_WHOLE"
        fsck_img "$OUT/sifreli-kardes.img"
    fi

    say "6/6 Paketleme"
    ( cd "$(dirname "$OUT")" && ditto -c -k --keepParent "$(basename "$OUT")" "$(basename "$OUT").zip" ) \
        && say "  Hazır: $OUT.zip  ← bu dosyayı Windows'a götürün." || fail "zip oluşturulamadı; klasörü olduğu gibi götürün."
    say ""
    say "Bitti. Sorun çıktıysa rapor.txt dosyasını da götürün."
}

do_verify() {
    local dir="${1:-}" img base vol mnt rc ok=0 bad=0
    [ -n "$dir" ] && [ -d "$dir" ] || die "kullanım: bash apfs-mac-kit.sh verify <görüntülerin olduğu klasör>"
    REPORT="$dir/mac-dogrulama-$STAMP.txt"; : > "$REPORT"
    say "APFS for Windows — Mac doğrulaması $STAMP"
    say "macOS: $(sw_vers -productVersion) ($(sw_vers -buildVersion))"
    say ""
    for img in "$dir"/*.img; do
        [ -f "$img" ] || continue
        base="$(basename "$img")"
        say "== $base"
        if fsck_img "$img"; then ok=$((ok+1)); else bad=$((bad+1)); fi
        # Windows'un bıraktığı parmak izi listesi varsa: salt okunur bağla, karşılaştır
        if [ -f "$img.windows-manifest" ]; then
            attach_nomount "$img" || continue
            for vol in $ATT_VOLS; do
                diskutil mount readOnly "$vol" >/dev/null 2>&1 || { fail "$vol bağlanamadı"; continue; }
                mnt="$(diskutil info "$vol" | awk -F': *' '/Mount Point/{print $2}' | trim)"
                manifest "$mnt" "$img.mac-manifest"
                if diff -u "$img.windows-manifest" "$img.mac-manifest" > "$img.manifest-fark.txt"; then
                    say "  parmak izleri: AYNI ($(wc -l < "$img.mac-manifest" | trim) dosya)"
                    rm -f "$img.manifest-fark.txt"
                else
                    say "  parmak izleri: FARKLI — ayrıntı: $base.manifest-fark.txt"; bad=$((bad+1))
                fi
                diskutil unmount "$vol" >/dev/null 2>&1
                break
            done
            detach "$ATT_WHOLE"
        fi
    done
    say ""
    say "Sonuç: $ok temiz, $bad sorunlu. Bu rapor dosyasını Windows'a götürün: $REPORT"
}

# find_volume_dev <ad>  →  APFS birim aygıtı (ör. disk5s1); `diskutil apfs list` çıktısında "Name: <ad> (...)" satırının birimi
find_volume_dev() {
    diskutil apfs list 2>/dev/null | awk -v want="$1" '
        /Volume disk[0-9]+s[0-9]+/ { for (i = 1; i <= NF; i++) if ($i ~ /^disk[0-9]+s[0-9]+$/) dev = $i }
        /Name:/ { line = $0; sub(/^.*Name:[[:space:]]*/, "", line); sub(/[[:space:]]+\(.*\)[[:space:]]*$/, "", line); if (line == want) { print dev; exit } }'
}

# ensure_mounted <ad>: /Volumes/<ad> yoksa birimi bağlar; şifreli ve kilitliyse parola macOS'un kendi istemiyle sorulur
# (bu betik parolayı görmez). 0 = birim hazır
ensure_mounted() {
    local vol="$1" mnt="/Volumes/$1" dev i=0
    [ -d "$mnt" ] && return 0
    dev="$(find_volume_dev "$vol")"
    [ -n "$dev" ] || { fail "'$vol' birimi bulunamadı. Diski takın ve Finder'da göründüğünden emin olun."; return 1; }
    diskutil mount "$dev" >/dev/null 2>&1
    if [ ! -d "$mnt" ]; then
        say "'$vol' kilitli görünüyor ($dev); kilidi açmak için parolası sorulacak."
        diskutil apfs unlockVolume "$dev" || { fail "'$vol' kilidi açılamadı."; return 1; }
    fi
    while [ ! -d "$mnt" ] && [ $i -lt 30 ]; do sleep 1; i=$((i+1)); done
    [ -d "$mnt" ] || { fail "'$vol' bağlanmadı."; return 1; }
    return 0
}

# verify_cont <kapsayıcı aygıtı> <günlük dosyası> <rapordaki ayrıntı yeri>: İlk Yardım (salt okunur denetim). 0 = çıkış kodu 0
# Gerçek aygıtı fsck_apfs ile açmak yönetici ister ("Permission denied", 2026-10-05); diskutil yetkiyi kendisi alır.
verify_cont() {
    local log rc
    log="$(diskutil verifyVolume "$1" 2>&1)"; rc=$?
    printf '%s\n' "$log" > "$2"
    if [ $rc -eq 0 ] && ! printf '%s\n' "$log" | grep -qi 'warning\|uyarı'; then
        say "  fsck_apfs: TEMİZ"
    elif [ $rc -eq 0 ]; then
        say "  fsck_apfs: UYARILI — ayrıntı: $3"
        printf '%s\n' "$log" | grep -i 'warning\|uyarı' | sed 's/^/    /' | while IFS= read -r l; do say "$l"; done
    else
        say "  fsck_apfs: HATALI (çıkış kodu $rc) — ayrıntı: $3"
        printf '%s\n' "$log" | tail -n 15 | sed 's/^/    /' | while IFS= read -r l; do say "$l"; done
    fi
    return $rc
}

do_usb() {
    local vol="${1:-USBTEST}" mnt cont kit
    mnt="/Volumes/$vol"
    ensure_mounted "$vol" || die "'$vol' kullanılamıyor."
    REPORT="$HOME/Desktop/usb-dogrulama-$vol-$STAMP.txt"; : > "$REPORT"
    kit="$mnt/APFS-Mac-Kiti"
    say "APFS for Windows — gerçek bellek doğrulaması $STAMP"
    say "macOS: $(sw_vers -productVersion) ($(sw_vers -buildVersion)), $(uname -m)"
    cont="$(diskutil info "$mnt" | awk -F': *' '/APFS Container:/{print $2}' | trim)"
    say "Birim: $mnt, kapsayıcı: ${cont:-?}"
    say ""

    say "1/3 Parmak izleri (Windows'ta yazılan her dosya Mac'te aynı mı?)"
    if [ -f "$kit/$vol.windows-manifest" ]; then
        manifest "$mnt" "$HOME/Desktop/$vol.mac-manifest.tmp"
        grep -v '  \./APFS-Mac-Kiti/' "$HOME/Desktop/$vol.mac-manifest.tmp" > "$HOME/Desktop/$vol.mac-manifest"
        rm -f "$HOME/Desktop/$vol.mac-manifest.tmp"
        if diff -u "$kit/$vol.windows-manifest" "$HOME/Desktop/$vol.mac-manifest" > "$HOME/Desktop/$vol.manifest-fark.txt"; then
            say "  parmak izleri: AYNI ($(wc -l < "$HOME/Desktop/$vol.mac-manifest" | trim) dosya)"
            rm -f "$HOME/Desktop/$vol.manifest-fark.txt"
        else
            say "  parmak izleri: FARKLI — ayrıntı: masaüstünde $vol.manifest-fark.txt"
            head -n 20 "$HOME/Desktop/$vol.manifest-fark.txt" | sed 's/^/    /' | while IFS= read -r l; do say "$l"; done
        fi
    else
        fail "Windows'un parmak izi listesi yok: $kit/$vol.windows-manifest"
    fi

    say "2/3 İlk Yardım (diskutil verifyVolume; birim kısa süre ayrılıp yeniden bağlanabilir)"
    if [ -n "$cont" ]; then
        verify_cont "$cont" "$HOME/Desktop/$vol.fsck.txt" "masaüstünde $vol.fsck.txt"
    else
        fail "kapsayıcı aygıtı bulunamadı; fsck atlandı"
    fi

    say "3/3 Ters yön: Mac'ten belleğe deneme dosyaları"
    local i=0
    while [ ! -d "$mnt" ] && [ $i -lt 30 ]; do sleep 1; i=$((i+1)); done
    if [ -d "$mnt" ]; then
        mkdir -p "$mnt/Mac-ten-gelen"
        printf "Mac'ten merhaba: ğüşıöç ĞÜŞİÖÇ\n" > "$mnt/Mac-ten-gelen/merhaba-mac.txt"
        pattern 5242880 > "$mnt/Mac-ten-gelen/desen-5mb.bin"
        ( cd "$mnt/Mac-ten-gelen" && shasum -a 256 merhaba-mac.txt desen-5mb.bin ) > "$mnt/Mac-ten-gelen/parmak-izi.txt"
        sync
        say "  yazıldı: Mac-ten-gelen/ (merhaba-mac.txt, desen-5mb.bin, parmak-izi.txt)"
    else
        fail "bellek yeniden bağlanmadı; ters yön atlandı"
    fi
    cp "$REPORT" "$mnt/usb-dogrulama-$STAMP.txt" 2>/dev/null && say "  rapor belleğe de kopyalandı"
    say ""
    say "Bitti. Rapor: $REPORT"
}

# ---------------------------------------------------------------------------------------------------------------------
# Çok bölümlü harici test diski (4. ziyaret)

# is_internal <aygıt>: birimin durduğu fiziksel disk iç disk mi? (APFS birimi → kapsayıcının fiziksel deposu)
is_internal() {
    local store
    store="$(diskutil info "$1" 2>/dev/null | awk -F': *' '/APFS Physical Store:/{print $2}' | trim)"
    diskutil info "${store:-$1}" 2>/dev/null | grep -Eq 'Device Location:[[:space:]]+Internal|^[[:space:]]*Internal:[[:space:]]+Yes'
}

# fill_hdd <bağlama noktası> <desen dosyası baytı, 0 = yok>: her şey APFS-Test/ altına
fill_hdd() {
    local m="$1" big="$2" t="$1/APFS-Test" n out
    quiet_volume "$m"
    mkdir -p "$t"
    fill_full "$t"          # ls -lO çıktısı $OUT'a (birim başına klasör)
    # Gerçek bir macOS uygulaması: ditto içindeki dosyaları macOS'un kendi sıkıştırmasıyla yazar
    # (2026-10-25 macOS 27'de beş birimde de başarısız oldu; hata metni o zaman yutuluyordu)
    mkdir -p "$t/Uygulama"
    out="$(ditto --hfsCompression /System/Applications/Calculator.app "$t/Uygulama/Calculator.app" 2>&1)" \
        || say "  uyarı: Calculator.app kopyalanamadı: $(printf '%s' "$out" | head -n 3)"
    # Mac'te geçerli ama Windows'ta yasak ya da sorunlu adlar (SFM eşlemesi, ayrılmış adlar)
    mkdir -p "$t/Windows-sorunlu"
    for n in 'iki:nokta.txt' 'soru?.txt' 'yildiz*.txt' 'kucuk<buyuk>.txt' 'boru|.txt' 'tirnak".txt' 'ters\bolu.txt' \
             'sonda nokta.' 'sonda bosluk ' 'CON.txt' 'emoji 😀.txt'; do
        printf '%s\n' "$n" > "$t/Windows-sorunlu/$n"
    done
    printf 'kaynak catali\n' > "$t/kaynak-catalli.txt"
    printf 'RSRC' > "$t/kaynak-catalli.txt/..namedfork/rsrc"
    if diskutil info "$m" | grep -q 'Case-sensitive'; then
        printf 'kucuk\n' > "$t/dosya.txt"; printf 'BUYUK\n' > "$t/DOSYA.txt"
        printf 'nfc\n' > "$t/$(printf 'caf\xc3\xa9')"            # é tek karakter (NFC)
        printf 'nfd\n' > "$t/$(printf 'cafe\xcc\x81')"           # e + birleşen vurgu (NFD)
    fi
    [ "$big" -gt 0 ] && pattern "$big" > "$t/buyuk-desen.bin"
    return 0
}

do_hdd() {
    local kitdir res vol mnt dev cont conts="" big n store whole
    kitdir="$(cd "$(dirname "$0")" && pwd)"
    [ $# -gt 0 ] || set -- A-Duz B-Sifreli C1-Duz C2-Sifreli C3-Duyarli
    res="$kitdir/hdd-sonuc"
    mkdir -p "$res" || die "sonuç klasörü oluşturulamadı: $res"
    REPORT="$res/rapor-$STAMP.txt"; : > "$REPORT"
    say "APFS for Windows — çok bölümlü test diski $STAMP"
    say "macOS: $(sw_vers -productVersion) ($(sw_vers -buildVersion)), $(uname -m)"
    say "Birimler: $*"
    say ""
    for vol in "$@"; do
        say "== $vol"
        ensure_mounted "$vol" || continue
        mnt="/Volumes/$vol"
        dev="$(diskutil info "$mnt" | awk -F': *' '/Device Identifier:/{print $2}' | trim)"
        if is_internal "$dev"; then fail "'$vol' iç diskte; dokunulmadı."; continue; fi
        if [ -d "$mnt/APFS-Test" ]; then fail "'$vol' zaten dolu (APFS-Test var); atlandı."; continue; fi
        n="$(find "$mnt" -type f ! -path "$mnt/.fseventsd/*" ! -path "$mnt/.Spotlight-V100/*" \
            ! -name '.DS_Store' ! -name '.metadata_never_index' 2>/dev/null | head -n 21 | wc -l | trim)"
        if [ "$n" -gt 20 ]; then fail "'$vol' boş değil (20'den çok dosya); yanlış birim olabilir, dokunulmadı."; continue; fi
        case "$vol" in A-*) big=5368709120 ;; B-*) big=1073741824 ;; *) big=0 ;; esac
        OUT="$res/$vol"; mkdir -p "$OUT"
        say "  dolduruluyor (birkaç dakika)…"
        fill_hdd "$mnt" "$big"
        sync
        manifest "$mnt" "$res/$vol.mac-manifest"
        say "  yazıldı: $(wc -l < "$res/$vol.mac-manifest" | trim) dosya; liste: hdd-sonuc/$vol.mac-manifest"
        cont="$(diskutil info "$mnt" | awk -F': *' '/APFS Container:/{print $2}' | trim)"
        case " $conts " in *" $cont "*) ;; *) [ -n "$cont" ] && conts="$conts $cont" ;; esac
    done
    say ""
    say "İlk Yardım (birimler kısa süre ayrılıp yeniden bağlanabilir)"
    for cont in $conts; do
        say "== kapsayıcı $cont"
        verify_cont "$cont" "$res/$cont.fsck.txt" "hdd-sonuc/$cont.fsck.txt"
        diskutil apfs list "$cont" > "$res/$cont.apfs-list.txt" 2>&1
        store="$(diskutil info "$cont" | awk -F': *' '/APFS Physical Store:/{print $2}' | trim)"
        [ -n "$store" ] && whole="${store%s*}"
    done
    if [ -n "${whole:-}" ]; then
        diskutil list "$whole" > "$res/disk-duzeni.txt" 2>&1
        diskutil info "$whole" >> "$res/disk-duzeni.txt" 2>&1
        say ""
        say "Disk: $whole — $(diskutil info "$whole" | grep -E 'Device Block Size|Media Name' | trim | tr '\n' ' ')"
    fi
    say ""
    say "Bitti. Diski Finder'da Çıkar ile çıkarın ve Windows'a takın. Sonuçlar: $res"
}

case "$MODE" in
    fixtures) do_fixtures "${2:-}" ;;
    verify)   do_verify "${2:-}" ;;
    usb)      shift; if [ $# -eq 0 ]; then do_usb USBTEST; else for v in "$@"; do do_usb "$v"; done; fi ;;
    hdd)      shift; do_hdd "$@" ;;
    *) printf 'kullanım:\n  bash %s fixtures [çıktı klasörü]\n  bash %s verify <klasör>\n  bash %s usb [birim adı...]\n  bash %s hdd [birim adı...]\n' "$0" "$0" "$0" "$0"; exit 2 ;;
esac
