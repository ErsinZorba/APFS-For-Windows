# APFS for Windows

Mac'te biçimlendirilmiş **APFS** diskleri Windows 11'de normal bir sürücü gibi açmanızı sağlayan açık kaynaklı, hafif bir araç.
Diski takarsınız, birkaç saniye içinde Dosya Gezgini'nde sürücü harfi olarak belirir; dosyaları açar, kopyalarsınız.

> **Önce salt okunur.** Diskler her zaman salt okunur bağlanır; program kendiliğinden diske hiçbir şey yazmaz. Bir diske yazmak
> isterseniz kartındaki **Yazmaya izin ver** düğmesiyle o disk için ayrıca izin verirsiniz (deneysel; bkz. "Yazma"). Verilerinizin
> güvenliği her şeyden önce gelir.

## Neler destekleniyor
| | |
|---|---|
| Takınca kendiliğinden bağlanma | Arka planda çalışan hizmet APFS disklerini bulur ve bağlar |
| Sade arayüz | Ana pencere ve tepside küçük bir panel: diskler, "Aç", "Kilidi aç", "Güvenle çıkar" |
| Birden çok birim | Her kullanıcı birimi ayrı sürücü harfi alır; macOS'un iç birimleri (Önyükleme, Kurtarma…) gizlenir |
| Şifreli birimler (deneysel) | Tepsi simgesi parolayı sorar (ipucuyla); parola hiçbir yere kaydedilmez |
| Sıkıştırılmış dosyalar | zlib, LZVN, LZFSE (macOS'un şeffaf sıkıştırması) |
| Türkçe ve Unicode adlar | ç, ğ, ı, İ, ö, ş, ü; Mac'in NFD biçimi; Windows'ta yasak karakterler (`: ? *` …) |
| Büyük dosyalar | 4 GB üstü, çok parçalı ve seyrek dosyalar |
| Bozuk diskler | Program çökmez; ne olduğunu Türkçe bildirir; sağlam birimler açılmaya devam eder |

## Kurulum
`APFS-for-Windows-Setup-1.2.1.exe` dosyasını çalıştırın ve sihirbazı izleyin (İleri › Kur › Son). Windows 10/11 (64 bit) yeter;
başka bir şey kurmanız gerekmez: .NET programla birlikte gelir, [WinFsp](https://winfsp.dev) sürücüsü yoksa sihirbaz onu da kurar.
Kurulumdan sonra Başlat menüsünde **APFS for Windows** bulunur ve tepsi simgesi her oturumda kendiliğinden açılır.

Kaldırmak için: **Ayarlar › Uygulamalar › Yüklü uygulamalar › APFS for Windows › Kaldır**. Kaldırma disklerinize hiçbir şey yazmaz;
günlük (`C:\ProgramData\APFS for Windows\log.txt`) incelemeniz için bırakılır, WinFsp kaldırılmaz.

## Kullanım
- **Ana pencere** (Başlat menüsü veya tepsi menüsünden "Ana pencereyi aç"): takılı Mac diskleri kart olarak görünür. Her birimin
  yanında **Aç** (Gezgin'de açar), şifreliyse **Kilidi aç**, açılamadıysa **Ayrıntı**; kartın altında **Güvenle çıkar**.
  Pencereyi kapatmak programı kapatmaz; diskler bağlı kalır ve program tepside çalışmaya devam eder.
- **Tepsi paneli**: saatin yanındaki APFS simgesine sol tıklayın; aynı işlemler küçük bir panelde. Sağ tık menüsünde
  "Ana pencereyi aç", disk başına "güvenle çıkar", "Yeniden tara", "Günlüğü aç" ve "Çıkış" bulunur.

## Yazma (deneysel)
Diskin kartındaki **Yazmaya izin ver**'e tıklayın ve uyarıyı onaylayın. Program diski önce baştan sona denetler; her şey yolundaysa
sürücü **yazılabilir** olarak yeniden bağlanır (kartta turuncu nokta, "yazılabilir (deneysel)"). Artık Gezgin'de dosya kopyalayabilir,
klasör açabilir, adlandırabilir, silebilir ve dosyaları düzenleyip kaydedebilirsiniz. **Salt okunura dön** değişiklikleri kaydedip
diski yeniden salt okunur bağlar.

- **Önce yedek alın.** Yazma özelliği yenidir; önemli dosyalarınızın bir kopyası olsun.
- **Diski çıkarmadan veya Mac'e takmadan önce mutlaka "Güvenle çıkar"ı kullanın.** Değişiklikler birkaç saniyede bir kaydedilir;
  Güvenle çıkar son değişiklikleri de kaydeder ve diski "düzgün kapatıldı" olarak işaretler.
- Yazma yarıda kesilirse (kablo çekilmesi, elektrik kesintisi) disk bir önceki kaydedilmiş hâliyle açılır; Mac diskiniz bozulmaz.
  Son birkaç saniyenin değişiklikleri kaybolabilir. (Bazı USB kutuları "diske boşalt" komutuna uymaz; o durumda kaybolan süre
  biraz uzayabilir, disk yine bir önceki sağlam hâliyle açılır.)
- Hangi diskler yazmaya açılır: tek birimli APFS bölümleri, **şifresiz** ya da Mac'te **parolayla şifrelenmiş** (Disk İzlencesi'nde
  "APFS (Şifreli)"; 1.2.0). Şifreli birimde önce **Kilidi aç**, sonra **Yazmaya izin ver**: dosyalar diske yine şifreli yazılır.
  Parola yazmaya açarken yeniden sorulmaz, "Salt okunura dön"de de sorulmaz (anahtar yalnızca bellekte tutulur; disk çıkarılınca
  ya da hizmet durunca silinir, hiçbir yere kaydedilmez).
- Birden çok APFS bölümü olan bir diskte (ör. Mac'te bölümlenmiş bir bellek) her bölümün kendi **«ad»: Yazmaya izin ver**
  düğmesi vardır; bir bölümü yazmaya açmak öbürlerine dokunmaz.
- Şunlar salt okunur kalır ve nedeni kartta yazar: macOS'un şifreleme işlemi bitmemiş ya da şifrelemesi sonradan değiştirilmiş
  birimler, donanımla (T2/Apple Silicon) veya dosya başına anahtarla şifrelenmiş birimler, anlık görüntüsü olan (ör. Time Machine)
  diskler, macOS sistem/yedek birimleri, Fusion Drive, aynı bölümde birden çok birim ve iç yapısında tutarsızlık görülen diskler.
- Bu sürümde içeriği değiştirilemeyenler: Mac'te "Çoğalt" ile oluşturulmuş (klon) dosyalar ve macOS'un sıkıştırarak sakladığı
  dosyalar (çoğunlukla uygulama ve sistem dosyaları; Finder ile kopyalanan belgeler ve fotoğraflar değil). Bunlar açılabilir,
  kopyalanabilir, silinip taşınabilir; üzerine kaydedilmek istendiğinde Windows "erişim engellendi" der (kopyasını düzenleyin).
- Gezgin'de silinen dosyalar Geri Dönüşüm Kutusu'na gitmez; Windows "kalıcı olarak silinsin mi?" diye sorar (USB bellekteki gibi).
- Windows'tan oluşturulan dosyalar Mac'te "bilinmeyen" sahipli (her kullanıcı açabilir), dosyalar 0644, klasörler 0755 izinlidir.
  Windows erişim listeleri (ACL) ve ek veri akışları saklanmaz.

## Diski çıkarma
Ana pencerede veya tepsi panelinde diskin kartındaki **Güvenle çıkar**'a tıklayın. Birimler ayrılır, Windows diski çıkarır ve
"Kabloyu şimdi çekebilirsiniz" bildirimi gelir. O anda diskten bir dosya açıksa veya kopyalanıyorsa program önce sorar:
"Kopyalama sürüyor gibi görünüyor" → **Vazgeç** (kopyalama sürer) veya **Yine de çıkar** (kopyalama yarıda kalır).
Gezgin'de sürücünün sağ tık menüsünde "Çıkar" görünmez (WinFsp sürücüleri Windows'a sanal sürücü olarak görünür). Salt okunur
bir diski doğrudan çekmek diske zarar vermez; yalnızca o anda süren bir kopyalama yarıda kalır. Yazılabilir bir diski de doğrudan
çekmek diski bozmaz (bir önceki kayıtlı hâliyle açılır), ama son değişiklikler kaybolabilir: önce Güvenle çıkar.

Disk takılıyken başka bir programla (Rufus, Disk Yönetimi…) biçimlendirilirse APFS sürücüsü en geç 15 saniye içinde kaldırılır.
Disk yazmaya açıksa kaydedilmemiş son değişiklikler, yeni içeriği bozmamak için diske yazılmaz.

## Nasıl çalışır
- **WinFsp** üzerinde çalışan kullanıcı modu bir dosya sistemi. Çekirdek sürücüsü yazılmadı; bir hata olsa bile sistem çökmez.
- APFS okuyucusu, Apple'ın *Apple File System Reference* belgesi temel alınarak C# ile sıfırdan yazıldı.
- Salt okunur bağlamada disk işletim sistemi seviyesinde **yalnızca okuma izniyle** açılır; ayrıca birim Windows'a salt okunur
  olarak bildirilir ve değiştiren her işlem reddedilir. Diske yazabilen kod yalnızca yazma kapısının (tam denetim) verdiği izinle
  çalışabilir.
- Yazma, APFS'in kendi güvenlik düzenine uyar: hiçbir kayıtlı yapı yerinde değiştirilmez; değişiklikler yeni bloklara yazılır ve en
  sonda tek bir "kontrol noktası" işareti ilerletilir (iki ayrı "diske boşalt" adımıyla). Yazma yarıda kesilirse disk bir önceki
  kontrol noktasıyla açılır.
- Diskteki her yapı sağlama toplamıyla doğrulanır. Bozuk bir yapı bulunduğunda program çökmez; ne olduğunu Türkçe söyler.
- Bileşenler: `apfs-service` (LocalSystem hizmeti: diskleri bulur ve bağlar), `apfs-tray` (ana pencere, tepsi simgesi ve paneli,
  parola penceresi, bildirimler), `apfs` (komut satırı aracı).
- WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — <https://github.com/winfsp/winfsp>. Kurulum sihirbazı
  WinFsp'in değiştirilmemiş resmi kurulum paketini içerir (GPLv3 + FLOSS istisnası; bkz. `THIRD_PARTY_NOTICES.md`).

## Komut satırı
```
apfs info <kaynak> [--fingerprint]     Disk ve birim bilgisi
apfs ls <kaynak> [yol] [-r] [--hash]   Klasör içeriği
apfs extract <kaynak> <yol> <hedef>    Bir dosyayı Windows'a kopyala
apfs check <kaynak>                    Salt okunur öz denetim: tüm ağacı gezer, her dosyanın başını ve sonunu okur, sayaçları karşılaştırır
apfs check <kaynak> --full [--gizle]   Yapısal tam denetim: her bloğun sahibi, boş alan kayıtları, başvuru sayıları (salt okunur)
apfs check <kaynak> --gate             Yazma kapısının bu disk için kararı ve nedenleri (salt okunur)
apfs dump <kaynak> [yol]               Geliştirme: iç yapı dökümü (yol verilirse o dosyanın ham kayıtları)
apfs mount <kaynak> [X:]               Elle bağlama (Ctrl+C ile ayır); iç birimler için --all
```
`<kaynak>`: disk görüntüsü dosyası veya `\\.\PhysicalDriveN` (yönetici gerekir). Şifreli birimler için `--password-prompt`.
Kurulumdan sonra araç PATH'e eklenmez; tam yoluyla çalıştırın: `& "C:\Program Files\APFS for Windows\apfs.exe" info \\.\PhysicalDrive2`

## Dosya adları
- Türkçe karakterler dahil tüm Unicode adlar desteklenir. Mac'in adları farklı Unicode biçiminde (NFD) saklaması sorun değildir.
- Mac'te büyük/küçük harf duyarsız birimlerde (varsayılan) Windows'taki gibi davranılır. "ı" ile "i" Mac'teki gibi farklı harflerdir.
- Mac'te geçerli olup Windows'ta yasak olan karakterler (`: * ? " < > | \`, sondaki nokta ve boşluk) Windows'un ve macOS'un
  dosya paylaşımında kullandığı standart eşlemeyle (SFM) özel karakterlere çevrilir. Gezgin'de görünür, açılır ve kopyalanabilir.
- Bilinen sınırlama: `CON`, `NUL`, `AUX`, `COM1` gibi Windows'a ayrılmış adlarla başlayan dosyalar bazı programlarda açılmayabilir.

## Sıkıştırılmış dosyalar
macOS birçok dosyayı şeffaf biçimde sıkıştırarak saklar. Bu dosyalar Windows'ta da normal görünür ve açılır; boyutları açılmış
halleriyle gösterilir. Desteklenmeyen bir yöntemle (ör. LZBITMAP) sıkıştırılmış bir dosya listede görünür, açılmak istendiğinde
"İstek desteklenmiyor" hatası verir ve nedeni günlüğe yazılır.

## Desteklenmeyenler
Aynı bölümde birden çok birim olan disklere yazma; şifreli birimin parolasını değiştirme, şifreleme/şifre çözme; onarım, biçimlendirme; anlık görüntüler (snapshot) ve Time Machine yapısı;
Fusion Drive; mühürlü macOS sistem birimi; DMG/sparsebundle dosyaları; HFS+; Apple Silicon/T2 dahili disk donanım şifrelemesi.

## Geliştirme
```
dotnet build
dotnet test
powershell -File tools\build-installer.ps1   # kurulum sihirbazı: publish\APFS-for-Windows-Setup-<sürüm>.exe
```
`build-installer.ps1` için Inno Setup 6 (`winget install --id JRSoftware.InnoSetup -e`) ve `installer\redist\` altında WinFsp'in
resmi `winfsp-2.1.25156.msi` paketi gerekir (git'e eklenmez; betik imzasını denetler). Yalnızca program klasörü için
`tools\build-release.ps1` (`publish\APFS for Windows\`); onu sihirbazsız kurmak için yönetici PowerShell'de `install.ps1`.
`install.ps1` ile kurulan sürüm "Yüklü uygulamalar"da görünmez (kaldırmak için `uninstall.ps1`); sihirbazla kurduysanız
oradan kaldırın, `uninstall.ps1` kullanılırsa listede sahipsiz bir kayıt kalır.
Test görüntüleri `tests/Apfs.ImageBuilder` ile üretilir ve bağımsız bir okuyucu olan 7-Zip ile karşılaştırılır
(`tools/compare_with_7z.ps1`). Yazma desteği bağımsız hakemlerle sınanır: Linux'un APFS araçları (apfsprogs: `mkapfs`, `apfsck`;
linux-apfs-rw sürücüsü) WSL içinde çalışır (`tools/make-linux-fixtures.sh`, `tools/make-linux-populated.sh`); macOS'un kendi
denetleyicisi için `tools/mac/` (Mac kiti). Gerçek disk yolunu sanal diskte sınamak için `tools/vhd-write-test.ps1` (yönetici).
Uzun testler: `APFS_LONG=1 dotnet test`.

## Lisans
Copyright (C) 2026 Ersin Zorba. GNU Genel Kamu Lisansı sürüm 3 (yalnız sürüm 3; `GPL-3.0-only`), metni [LICENSE](LICENSE).
Üçüncü taraf bileşenler için [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Proje tek geliştiricilidir; dış katkı (pull request) kabul edilmiyor. Hata bildirimleri için Issues kullanılabilir.
