"""
Bağımsız (OpenSSL tabanlı 'cryptography' paketi) AES-XTS-128 çözücü: bir APFS görüntüsünden ardışık blokları okur, verilen
VEK ve ilk sektör (tweak) ile çözer; düz metnin Fletcher-64 sağlamasının tutup tutmadığını ve SHA-256'sını yazar.
Okuyucunun/yazıcının kendi şifreleme kodundan bağımsız hakem olarak testlerde kullanılır (EncryptedWriteTests).

  tweak: fs ağacı düğümünde sektör = fiziksel blok × (blok/512); dosya verisinde sektör = crypto_id × (blok/512) + birim

Kullanım: python tools/openssl_check.py <görüntü> <blok boyutu> <ilk blok> <blok sayısı> <vek: 64 onaltılık> <ilk sektör>
Çıktı:    fletcher=ok|bad sha256=<onaltılık>
"""
import hashlib
import struct
import sys

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes


def xts_decrypt(key, first_sector, data):
    out = bytearray()
    for u in range(len(data) // 512):
        tweak = (first_sector + u).to_bytes(16, "little")
        dec = Cipher(algorithms.AES(key), modes.XTS(tweak)).decryptor()
        out += dec.update(data[u * 512:(u + 1) * 512]) + dec.finalize()
    return bytes(out)


def fletcher64_ok(block):
    """Apple File System Reference: ilk 8 bayt hariç 32 bitlik sözcükler; her adımda mod 2^32-1."""
    mod = 0xFFFFFFFF
    s1 = s2 = 0
    for (w,) in struct.iter_unpack("<I", block[8:]):
        s1 = (s1 + w) % mod
        s2 = (s2 + s1) % mod
    c1 = mod - ((s1 + s2) % mod)
    c2 = mod - ((s1 + c1) % mod)
    return struct.unpack("<Q", block[:8])[0] == (c2 << 32) | c1


def main():
    path, bs, first, count, vek_hex, sector = sys.argv[1:7]
    bs, first, count, sector = int(bs), int(first), int(count), int(sector)
    vek = bytes.fromhex(vek_hex)
    with open(path, "rb") as f:
        f.seek(first * bs)
        data = f.read(count * bs)
    plain = xts_decrypt(vek, sector, data)
    ok = all(fletcher64_ok(plain[i * bs:(i + 1) * bs]) for i in range(count))
    print(f"fletcher={'ok' if ok else 'bad'} sha256={hashlib.sha256(plain).hexdigest()}")


if __name__ == "__main__":
    main()
