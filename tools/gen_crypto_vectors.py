"""
APFS şifrelemesinde kullanılan temel yapı taşları için BAĞIMSIZ test vektörleri üretir (OpenSSL tabanlı 'cryptography' paketi
ve Python'un hashlib'i). Okuyucunun ve görüntü üreticisinin kendi uygulamaları bu vektörlerle doğrulanır.

  xts     AES-XTS-128, 512 baytlık birim, tweak = 16 bayt little-endian sektör numarası (APFS'teki gibi)
  wrap    RFC 3394 AES anahtar sarma (128 ve 256 bit KEK)
  pbkdf2  PBKDF2-HMAC-SHA256 (Türkçe karakterli parola dahil, UTF-8)

Çıktı: tests/Apfs.Tests/data/crypto/vectors.txt   (her satır: tür ve onaltılık alanlar, boşlukla ayrılmış)
Kullanım: pip install cryptography && python tools/gen_crypto_vectors.py
"""
import hashlib
import os
import random

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives.keywrap import aes_key_wrap

OUT = "tests/Apfs.Tests/data/crypto/vectors.txt"


def xts_encrypt(key1, key2, sector, data):
    tweak = sector.to_bytes(16, "little")
    enc = Cipher(algorithms.AES(key1 + key2), modes.XTS(tweak)).encryptor()
    return enc.update(data) + enc.finalize()


def main():
    rnd = random.Random(1619)
    lines = ["# tools/gen_crypto_vectors.py (cryptography/OpenSSL ve hashlib ile)"]
    for i in range(12):
        k1, k2 = rnd.randbytes(16), rnd.randbytes(16)
        if k1 == k2:
            k2 = bytes(b ^ 1 for b in k2)
        sector = [0, 1, 7, 8, 0xFFFFFFFF, 1 << 40, rnd.getrandbits(48)][i % 7]
        pt = rnd.randbytes(512)
        lines.append(f"xts {k1.hex()} {k2.hex()} {sector} {pt.hex()} {xts_encrypt(k1, k2, sector, pt).hex()}")
    for kek_len, key_len in [(16, 16), (32, 32), (16, 32), (32, 16)]:
        for _ in range(3):
            kek, key = rnd.randbytes(kek_len), rnd.randbytes(key_len)
            lines.append(f"wrap {kek.hex()} {key.hex()} {aes_key_wrap(kek, key).hex()}")
    for pw, iters in [("parola", 1000), ("Şifre ğüİıöç 123", 4096), ("x", 1), ("çok uzun bir parola " * 4, 2000)]:
        salt = rnd.randbytes(16)
        dk = hashlib.pbkdf2_hmac("sha256", pw.encode("utf-8"), salt, iters, 32)
        lines.append(f"pbkdf2 {pw.encode('utf-8').hex()} {salt.hex()} {iters} {dk.hex()}")
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print(f"{OUT}: {len(lines) - 1} vektör")


if __name__ == "__main__":
    main()
