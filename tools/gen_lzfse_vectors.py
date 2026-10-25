"""
Apple'ın referans LZFSE kodlayıcısıyla (PyPI 'lzfse' paketi; Apple lzfse kütüphanesinin sarmalayıcısı) test vektörleri üretir.
Okuyucunun LZFSE ve LZVN çözücüleri bu vektörlerle doğrulanır: çözülen veri orijinaliyle birebir aynı olmalıdır.

Çıktı (tests/Apfs.Tests/data/compression/):
  manifest.txt     her vektörün adı, orijinal boyutu ve SHA-256'sı (büyük orijinaller repoya konmaz)
  <ad>.lzfse       LZFSE akışı
  <ad>.lzvn        yalnızca küçük girdiler: Apple kodlayıcısı 4096 baytın altını 'bvxn' (LZVN) bloğu yapar; bloğun yükü ham LZVN akışıdır
  <ad>.bin         yalnızca 4096 bayta kadar olan orijinaller (decmpfs xattr senaryosu için)
  rsrc-src.bin     decmpfs kaynak çatalı (tür 12) senaryosunun orijinali
  rsrc-lzfse.blocks  aynı içeriğin 64 KiB'lık bloklar halinde ayrı ayrı LZFSE ile sıkıştırılmışı:
                   u32 blok sayısı, u32 boyutlar[], ardından bloklar

Kullanım: pip install lzfse && python tools/gen_lzfse_vectors.py
"""
import hashlib
import os
import random
import struct

import lzfse

OUT = "tests/Apfs.Tests/data/compression"


def text(n, seed):
    rnd = random.Random(seed)
    words = ["APFS", "Windows", "şeker", "ğüİıöç", "dosya", "klasör", "disk", "veri", "güvenli", "okuma", "Mac", "bağlantı"]
    out = []
    size = 0
    while size < n:
        w = rnd.choice(words) + (" " if rnd.random() < 0.85 else "\n")
        out.append(w)
        size += len(w.encode("utf-8"))
    return "".join(out).encode("utf-8")[:n]


def rnd_bytes(n, seed):
    return random.Random(seed).randbytes(n)


def mixed(n, seed):
    rnd = random.Random(seed)
    out = bytearray()
    while len(out) < n:
        if rnd.random() < 0.5:
            out += text(rnd.randint(100, 5000), rnd.randint(0, 10**6))
        elif rnd.random() < 0.5:
            out += bytes([rnd.randint(0, 255)]) * rnd.randint(1, 3000)
        else:
            out += rnd_bytes(rnd.randint(10, 2000), rnd.randint(0, 10**6))
    return bytes(out[:n])


CASES = {
    "empty": b"",
    "one": b"a",
    "small-text": text(200, 1),
    "text-3k": text(3000, 2),
    "repeat-4000": b"ab" * 2000,
    "text-64k": text(65536, 3),
    "text-300k": text(300_000, 4),
    "random-50k": rnd_bytes(50_000, 5),
    "mixed-1m": mixed(1_000_000, 6),
    "zeros-200k": bytes(200_000),
}


def lzvn_payload(stream):
    magic, n_raw, n_payload = struct.unpack_from("<4sII", stream, 0)
    if magic != b"bvxn":
        return None
    return stream[12:12 + n_payload]


def main():
    os.makedirs(OUT, exist_ok=True)
    manifest = []
    for name, data in CASES.items():
        comp = lzfse.compress(data)
        if data:  # Python sarmalayıcısı boş akışı geri çözerken hata veriyor; boş çıktıyı bizim çözücü doğrular
            assert lzfse.decompress(comp) == data
        # Orijinal veri repoya konmaz: boyut ve SHA-256 yeterli
        manifest.append(f"{name} {len(data)} {hashlib.sha256(data).hexdigest().upper()}")
        with open(f"{OUT}/{name}.lzfse", "wb") as f:
            f.write(comp)
        if len(data) <= 4096:
            with open(f"{OUT}/{name}.bin", "wb") as f:
                f.write(data)
        payload = lzvn_payload(comp)
        if payload is not None:
            with open(f"{OUT}/{name}.lzvn", "wb") as f:
                f.write(payload)
        print(f"{name:12} {len(data):>9} -> {len(comp):>9}  ilk blok {comp[:4].decode()}  {'(+lzvn)' if payload else ''}")
    # decmpfs tür 12 (LZFSE, kaynak çatalı): içerik 64 KiB'lık bloklara bölünür, her blok bağımsız bir LZFSE akışıdır
    src = text(150_000, 7)
    blocks = [lzfse.compress(src[i:i + 65536]) for i in range(0, len(src), 65536)]
    with open(f"{OUT}/rsrc-src.bin", "wb") as f:
        f.write(src)
    with open(f"{OUT}/rsrc-lzfse.blocks", "wb") as f:
        f.write(struct.pack("<I", len(blocks)))
        f.write(b"".join(struct.pack("<I", len(b)) for b in blocks))
        f.write(b"".join(blocks))
    print(f"rsrc-lzfse   {len(src):>9} -> {sum(map(len, blocks)):>9}  {len(blocks)} blok")
    with open(f"{OUT}/manifest.txt", "w", encoding="utf-8", newline="\n") as f:
        f.write("# ad boyut sha256 (tools/gen_lzfse_vectors.py, Apple lzfse kodlayıcısı)\n" + "\n".join(manifest) + "\n")


if __name__ == "__main__":
    main()
