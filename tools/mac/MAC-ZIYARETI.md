# Mac ziyareti — ne yapılacak?

## 4. ziyaret: çok bölümlü harici HDD (Seagate ST320LT020, 320 GB)

Disk İzlencesi'nde hazırlandı: GUID; bölüntüler `A-Duz` (APFS), `B-Sifreli` (APFS Şifreli), `C-Kapsayici` (APFS; içinde
`C1-Duz`, `C2-Sifreli` şifreli, `C3-Duyarli` harf duyarlı birimler), `D-exFAT` (ExFAT).

1. Windows'ta: disk takılır, kit `D-exFAT\APFS-Mac-Kiti\` klasörüne kopyalanır (Windows APFS bölümlerine yazmaz, salt okunur).
2. Mac'te Terminal:
   ```
   bash /Volumes/D-exFAT/APFS-Mac-Kiti/apfs-mac-kit.sh hdd
   ```
   Şifreli birimlerin parolasını macOS sorar. Her birimin `APFS-Test/` klasörü doldurulur (sıkıştırılmış dosyalar, gerçek bir
   uygulama, klon, sert/sembolik bağlantı, kaynak çatalı, Windows'ta yasak adlar, A'da 5 GB, B'de 1 GB desen dosyası), parmak
   izi listeleri ve rapor `D-exFAT/APFS-Mac-Kiti/hdd-sonuc/` altına yazılır, kapsayıcılara İlk Yardım çalışır.
   Dolu ya da iç diskteki birime dokunmaz. Birim adları farklıysa sonuna yazılır: `… hdd A-Duz B-Sifreli …`.
3. Disk Finder'da **Çıkar** ile çıkarılır, Windows'a takılır: okuma karşılaştırması, sonra yazma testleri.

## 3. ziyaret: iki bölümlü bellek — şifreli USBTEST + şifresiz non-password (sürüm 1.2.0)

Hazırlık Windows'ta (yönetici PowerShell), her bölüm için bir kez:
```
tools\usb-write-test.ps1 -Adim mac-hazirla -Ad non-password
tools\usb-write-test.ps1 -Adim mac-hazirla -Ad USBTEST -Parola
```
Her biri o birimin dosyalarının parmak izini çıkarır, kiti ve listeyi o birimin `APFS-Mac-Kiti` klasörüne yazar ve belleği düzgün
kapatır (USBTEST'in parolası sorulur, hiçbir yere kaydedilmez). Sonra bellek tepsideki **Güvenle çıkar** ile çıkarılır.

Mac'te:
1. Belleği takın. macOS USBTEST'in parolasını sorarsa girin (sormazsa kit kendisi sorar). Finder'da her iki birimden birkaç
   dosyayı açın (fotoğraf, Windows'ta yazılmış belge).
2. **Terminal**'de:
   ```
   bash /Volumes/non-password/APFS-Mac-Kiti/apfs-mac-kit.sh usb USBTEST non-password
   ```
   Kit her birim için sırasıyla: kilitliyse `diskutil apfs unlockVolume` ile parolayı sorar (macOS'un kendi istemi), parmak
   izlerini karşılaştırır, İlk Yardım (`diskutil verifyVolume`) çalıştırır, `Mac-ten-gelen` klasörünü yazar. Raporlar masaüstüne
   (`usb-dogrulama-<birim>-…txt`) ve belleğe kaydedilir.
3. Belleği Finder'da **Çıkar** ile çıkarın ve Windows'a takın; raporları oradan alırız. Bütün parmak izleri AYNI ve İlk Yardım iki
   birimde de temizse sürüm 1.2.0 GitHub'da yayımlanabilir.

## 2. ziyaret: Windows'ta yazılmış test belleğini (USBTEST) Mac'te doğrulamak

Hazırlık Windows'ta yapılır (yönetici PowerShell): `tools\usb-write-test.ps1 -Adim mac-hazirla`. Bu adım bellekteki her
dosyanın parmak izini çıkarır, Mac kitini ve listeyi belleğin `APFS-Mac-Kiti` klasörüne yazar ve belleği düzgün kapatır.
Sonra bellek tepsideki **Güvenle çıkar** ile çıkarılır.

Mac'te:
1. **USBTEST** belleğini takın. Finder'da görünmesini bekleyin. Birkaç dosyayı (bir fotoğraf, Windows'ta yazılmış bir belge)
   çift tıklayıp açın; düzgün açılıyor mu, bakın.
2. **Terminal**'i açın (⌘ + boşluk → "Terminal") ve şunu yazıp Enter'a basın:
   ```
   bash /Volumes/USBTEST/APFS-Mac-Kiti/apfs-mac-kit.sh usb
   ```
   Birkaç dakika sürer (bellekteki bütün dosyalar okunur). Sırasıyla: parmak izi karşılaştırması, İlk Yardım (`fsck_apfs`;
   bellek birkaç saniye Finder'dan kaybolup geri gelir), sonra Mac'ten belleğe `Mac-ten-gelen` klasörü yazılır.
   Rapor masaüstüne ve belleğe kaydedilir.
3. Aynı Terminal'de Mac'in kendi test disklerini üretin (5–15 dakika; geçen sefer macOS 27'de başarısız olmuştu, düzeltildi):
   ```
   bash /Volumes/USBTEST/APFS-Mac-Kiti/apfs-mac-kit.sh fixtures
   ```
   Bitince masaüstündeki `apfs-kit-….zip` dosyasını Finder'dan **USBTEST** belleğine sürükleyin.
4. Belleği Finder'da **Çıkar** ile çıkarın ve Windows'a takın. Program onu salt okunur açar; raporları ve zip'i oradan alırız.

Mac'te başka hiçbir şey değişmez; işiniz bitince masaüstündeki `usb-dogrulama-…`, `USBTEST.*` ve `apfs-kit-…` dosyalarını
silebilirsiniz.

## 1. ziyaret (2026-10-05, yapıldı): disk görüntüleri

Windows'ta yazılmış test görüntüleri (`fixtures\mac-paket.zip`, üretici `tools\mac\mac-paketi-hazirla.ps1`) Mac'te
`bash apfs-mac-kit.sh verify <klasör>` ile denetlendi: 20/20 temiz. Test belleği Disk İzlencesi'nde **USBTEST**, **APFS**
(şifresiz, harf duyarsız), **GUID Bölüm Haritası** olarak silindi.
