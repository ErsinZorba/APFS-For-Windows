using System.Globalization;
using System.Text;
using Apfs.ImageBuilder;
using Apfs.Util;

namespace Apfs.Tests;

/// <summary>M3: Türkçe ve Unicode adlar, harf katlaması, normalizasyon ve Windows ad eşlemesi.</summary>
public class UnicodeNameTests(TestImages images) : IClassFixture<TestImages>
{
    [Fact]
    public void TestsRunUnderTurkishCulture()
    {
        Assert.Equal("tr-TR", CultureInfo.CurrentCulture.Name);
        Assert.Equal("İ", "i".ToUpper()); // kültüre duyarlı API'nin Türkçe davrandığının kanıtı
    }

    [Theory]
    [InlineData("I", "i", true)]          // I → i
    [InlineData("ı", "i", false)]         // noktasız ı, i değildir (Apple, Türkçe'ye özel katlamayı kullanmaz)
    [InlineData("İ", "I", false)]         // İ → i̇ (i + U+0307); I → i
    [InlineData("İ", "i̇", true)]
    [InlineData("Ş", "ş", true)]
    [InlineData("Ğ", "ğ", true)]
    [InlineData("Ç", "ç", true)]
    [InlineData("Ö", "ö", true)]
    [InlineData("Ü", "ü", true)]
    [InlineData("KILIÇ", "kılıç", false)] // I → i, ı değil
    [InlineData("KILIÇ", "kiliç", true)]
    [InlineData("STRASSE", "straße", true)] // tam katlama: ß → ss
    [InlineData("ΣΊΣΥΦΟΣ", "σίσυφος", true)]
    public void FoldKeyFollowsUnicodeFullCaseFolding(string a, string b, bool equal)
    {
        Assert.Equal(equal, ApfsNames.FoldKey(a) == ApfsNames.FoldKey(b));
    }

    [Fact]
    public void NfcAndNfdNamesShareKeys()
    {
        string nfc = "Şeker ğüİıöç".Normalize(NormalizationForm.FormC);
        string nfd = nfc.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(nfc, nfd);
        Assert.Equal(ApfsNames.NormalizationKey(nfc), ApfsNames.NormalizationKey(nfd));
        Assert.Equal(ApfsNames.FoldKey(nfc), ApfsNames.FoldKey(nfd));
    }

    [Fact]
    public void InvalidUnicodeDoesNotThrow()
    {
        string lone = "a\uD800b"; // eşleşmemiş vekil karakter
        Assert.Equal(lone, ApfsNames.NormalizationKey(lone));
        _ = ApfsNames.FoldKey(lone);
    }

    [Theory]
    [InlineData("a:b.txt", "ab.txt")]
    [InlineData("soru?.txt", "soru.txt")]
    [InlineData("yıldız*.txt", "yıldız.txt")]
    [InlineData("tırnak\"lı.txt", "tırnaklı.txt")]
    [InlineData("boru|çizgi.txt", "boruçizgi.txt")]
    [InlineData("küçük<büyük>.txt", "küçükbüyük.txt")]
    [InlineData("ters\\bölü.txt", "tersbölü.txt")]
    [InlineData("sonu nokta.", "sonu nokta")]
    [InlineData("sonu boşluk ", "sonu boşluk")]
    [InlineData("a. .", "a")]
    [InlineData("kontrol\u0001karakter.txt", "kontrolkarakter.txt")]
    [InlineData("Şeker ğüİıöç.txt", "Şeker ğüİıöç.txt")]
    [InlineData(".DS_Store", ".DS_Store")]
    public void WindowsNameMappingRoundTrips(string apfs, string windows)
    {
        Assert.Equal(windows, WindowsNames.ToWindows(apfs));
        Assert.Equal(apfs, WindowsNames.FromWindows(windows));
    }

    [Theory]
    [InlineData("\\Şeker.txt", "\\ŞEKER.TXT", true)]                    // yalnızca harf büyüklüğü: gerçek ad bildirilir
    [InlineData("\\S\u0327eker.txt", "\\Şeker.txt", false)]             // NFD ↔ NFC: uzunluk farklı, bildirilmez
    [InlineData("\\Straße.txt", "\\STRASSE.TXT", false)]                // ß ↔ SS
    public void NormalizedNameOnlyWhenLengthMatches(string actual, string requested, bool reported)
    {
        // WinFsp çekirdekteki adı yerinde değiştirir; farklı uzunluk Windows'ta "sözdizimi hatalı" hatasına yol açıyordu (M3'te bulundu).
        var result = Mount.ApfsFileSystem.SameLengthOrNull(actual, requested);
        Assert.Equal(reported ? actual : null, result);
    }

    [Fact]
    public void CaseInsensitiveVolumeLookups()
    {
        var (path, builder) = images.Get("unicode");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        Assert.True(v.IsCaseInsensitive);

        string Read(string p) => Encoding.UTF8.GetString(ReaderTests.ReadAll(v, v.Lookup(p)));

        // Diskte NFD saklanan ada, Windows'un gönderdiği NFC biçimiyle ve farklı harf büyüklüğüyle ulaşılır
        Assert.Equal("şeker\n", Read("/Şeker ğüİıöç.txt".Normalize(NormalizationForm.FormC)));
        Assert.Equal("şeker\n", Read("/ŞEKER ĞÜİıÖÇ.TXT"));
        Assert.Equal("ödeme\n", Read("/müşteri dosyaları/ÖDEME ğ.txt"));

        // ı ile i ayrı harflerdir: iki dosya da ayrı ayrı açılır
        Assert.Equal("noktasız ı\n", Read("/Kılıç.txt"));
        Assert.Equal("noktalı i\n", Read("/Kiliç.txt"));
        Assert.Equal("noktalı i\n", Read("/KILIÇ.TXT")); // I → i: Mac'te de "Kiliç.txt"yi bulur
        Assert.Equal("büyük noktalı İ\n", Read("/i̇stanbul.txt"));
        Assert.Throws<ApfsException>(() => v.Lookup("/ıstanbul.txt"));

        Assert.Equal("almanca\n", Read("/STRASSE.TXT"));
        Assert.Equal("emoji\n", Read("/EMOJI 😀.TXT"));
        Assert.Equal("yunanca\n", Read("/ΕΛΛΗΝΙΚΆ.txt"));
        Assert.Equal(builder.Volumes[0].Enumerate().Count(), CountAll(v));
    }

    [Fact]
    public void CaseSensitiveVolumeLookups()
    {
        var (path, _) = images.Get("unicode-cs");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        Assert.False(v.IsCaseInsensitive);
        string Read(string p) => Encoding.UTF8.GetString(ReaderTests.ReadAll(v, v.Lookup(p)));

        Assert.Equal("büyük R\n", Read("/Rapor.txt"));
        Assert.Equal("küçük r\n", Read("/rapor.txt"));
        // İki aday varsa tahmin yapılmaz
        var ex = Assert.Throws<ApfsException>(() => v.Lookup("/RAPOR.TXT"));
        Assert.Equal(ApfsErrorCode.NotFound, ex.Code);
        // Tek aday varsa Windows uygulamalarının farklı harf büyüklüğüyle istemesine izin verilir
        Assert.Equal("tek belge\n", Read("/belge.txt"));
        // Normalizasyon farkı her zaman tolere edilir
        Assert.Equal("çiçek\n", Read("/Çiçek.txt".Normalize(NormalizationForm.FormC)));
    }

    private static int CountAll(Volume.ApfsVolume v)
    {
        int n = 0;
        void Walk(ulong id)
        {
            foreach (var e in v.ListDirectory(id))
            {
                n++;
                if (e.Type == Volume.DirEntryType.Directory) Walk(e.FileId);
            }
        }
        Walk(Volume.Inode.RootDirId);
        return n;
    }
}
