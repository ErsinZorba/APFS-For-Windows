# Üçüncü taraf bileşenler

| Bileşen | Kullanım | Lisans |
|---|---|---|
| [WinFsp](https://github.com/winfsp/winfsp) ve `winfsp.net` NuGet paketi | Kullanıcı modu dosya sistemi sürücüsü ve .NET bağlaması (çalışma zamanında bağlanır, kodu kopyalanmaz). Kurulum sihirbazı WinFsp'in değiştirilmemiş resmi kurulum paketini (`winfsp-2.1.25156.msi`) içerir | GPLv3 + FLOSS istisnası |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | Kurulum sihirbazı (derleyici yalnızca geliştirmede; sihirbazın çalışma kodu Setup.exe içinde) | Inno Setup lisansı |
| [xUnit](https://xunit.net) | Yalnızca testler | Apache-2.0 |
| Unicode Character Database, `CaseFolding.txt` (Unicode 18.0) | `src/Apfs.Core/Util/CaseFoldingTable.g.cs` bu veriden `tools/gen_casefold.py` ile üretildi | [Unicode License v3](https://www.unicode.org/license.txt) |

## WinFsp
WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — <https://github.com/winfsp/winfsp>.
WinFsp, GPLv3 ve FLOSS istisnasıyla lisanslıdır. İstisna, açık kaynak yazılımların (bu proje GPLv3) WinFsp DLL'leriyle
bağlanmasına ve WinFsp'in değiştirilmemiş resmi kurulum paketini dağıtmasına izin verir; şartı, bu telif notunun ve depo
bağlantısının arayüzde (Hakkında penceresi: ana penceredeki sürüm numarasına tıklayınca) ve kullanıcı belgelerinde (README) bulunmasıdır.

Unicode verisi için bildirim: Copyright © 1991-2026 Unicode, Inc. Unicode License v3 koşullarına göre dağıtılır.

## Apple lzfse (BSD-3-Clause)
`src/Apfs.Core/Compression/Lzfse.cs` ve `Lzvn.cs`, Apple'ın [lzfse](https://github.com/lzfse/lzfse) projesindeki biçim tanımı
ve çözücü (lzfse_internal.h, lzfse_fse.h/.c, lzfse_decode_base.c, lzvn_decode_base.c) referans alınarak C# ile yeniden yazıldı.
Biçimin zorunlu kıldığı sabit tablolar (L/M/D taban değerleri ve ek bit sayıları, frekans kodlama tabloları, LZVN işlem kodu tablosu)
oradan alınmıştır.

```
Copyright (c) 2015-2016, Apple Inc. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following
conditions are met:

1.  Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

2.  Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer
    in the documentation and/or other materials provided with the distribution.

3.  Neither the name of the copyright holder(s) nor the names of any contributors may be used to endorse or promote products derived
    from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Test araçları
- `tools/gen_lzfse_vectors.py`, test vektörlerini üretmek için PyPI [`lzfse`](https://pypi.org/project/lzfse/) paketini (MIT;
  Apple lzfse sarmalayıcısı) kullanır. Paket programın bir parçası değildir, yalnızca geliştirme sırasında gerekir.
- `tools/gen_crypto_vectors.py`, şifreleme test vektörleri için PyPI [`cryptography`](https://pypi.org/project/cryptography/)
  paketini (Apache-2.0/BSD; OpenSSL) kullanır. Yalnızca geliştirme sırasında gerekir.
- 7-Zip ve apfs-fuse kaynakları yalnızca davranış referansı olarak okundu (biçim ayrıntılarını doğrulamak için; ör. şifreli dosya
  verisinin tweak'i ve anahtar kayıtlarının DER yapısı); kod kopyalanmadı.

## Referans alınan belgeler (kod kopyalanmadı)
- Apple Inc., *Apple File System Reference* (2020-06-22). Biçimin temel kaynağı.
- Joachim Metz, libfsapfs biçim belgesi. Yalnızca davranış referansı olarak okundu.
- apfs-fuse (GPL-2.0). Yalnızca davranış referansı olarak okundu, kodu kopyalanmadı.
