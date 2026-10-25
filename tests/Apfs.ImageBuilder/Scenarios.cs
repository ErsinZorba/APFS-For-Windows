namespace Apfs.ImageBuilder;

/// <summary>Testlerde ve 7-Zip karşılaştırmasında kullanılan hazır görüntü senaryoları.</summary>
public static class Scenarios
{
    public static IReadOnlyList<string> Names { get; } =
        ["basic", "manyfiles", "sparse", "large", "bigdir", "unicode", "unicode-cs", "compressed", "compressed-lzfse", "multivolume", "encrypted"];

    /// <summary>"encrypted" senaryosunun parolası (testler ve el ile deneme için).</summary>
    public const string EncryptedPassword = "Gizli Şifre ğüİı 2024";

    /// <summary>
    /// 7-Zip ile karşılaştırılabilen senaryolar. 7-Zip'in APFS okuyucusu seyrek dosyaları desteklemediği için
    /// (ApfsHandler.cpp, GetStream2: boşluk extent'ini blok 0 olarak okur, eksik extent'i reddeder) "sparse" hariçtir.
    /// </summary>
    public static IReadOnlyList<string> SevenZipComparable { get; } = ["basic", "manyfiles"];

    public static ApfsImageBuilder Create(string name) => name switch
    {
        "basic" => Basic(),
        "manyfiles" => ManyFiles(),
        "sparse" => Sparse(),
        "large" => Large(),
        "bigdir" => BigDir(),
        "unicode" => Unicode(),
        "unicode-cs" => UnicodeCaseSensitive(),
        "compressed" => Compressed(),
        "compressed-lzfse" => CompressedLzfse(),
        "multivolume" => MultiVolume(),
        "encrypted" => Encrypted(),
        _ when name.StartsWith("c-") && uint.TryParse(name[2..], out uint t) => SingleCompressed(t),
        _ => throw new ArgumentException($"bilinmeyen senaryo: {name}"),
    };

    /// <summary>M1: dizinler, küçük/orta dosyalar, parçalı dosya, sembolik ve sert bağ, gizli ve boş dosya.</summary>
    public static ApfsImageBuilder Basic()
    {
        var b = new ApfsImageBuilder();
        b.AddVolume("Test Diski")
            .AddFile("/Merhaba.txt", FileContent.FromText("Merhaba dünya!\n"))
            .AddFile("/Belgeler/rapor.txt", FileContent.FromText(string.Concat(Enumerable.Repeat("APFS for Windows test raporu. ", 300))))
            .AddFile("/Belgeler/Alt Klasör/derin.bin", FileContent.Pattern(100_000, 1), fragments: 3)
            .AddDirectory("/Fotoğraflar")
            .AddFile("/büyük.bin", FileContent.Pattern(1_048_576 + 123, 2))
            .AddFile("/.gizli", FileContent.FromText("gizli dosya"), hidden: true)
            .AddFile("/boş.txt", FileContent.FromBytes([]))
            .AddSymlink("/bağlantı", "Belgeler/rapor.txt")
            .AddHardLink("/Belgeler/rapor-kopya.txt", "/Belgeler/rapor.txt");
        return b;
    }

    /// <summary>Seyrek dosyalar: boşluklar hem adresi 0 olan extent'lerle hem de hiç extent yazılmadan.</summary>
    public static ApfsImageBuilder Sparse()
    {
        var b = new ApfsImageBuilder();
        b.AddVolume("Seyrek")
            .AddFile("/örtük-boşluk.bin", new SparseContent(1_048_576, (0, Fill(4096, 0xAA)), (524_288, Fill(4096, 0xBB))), explicitHoles: false)
            .AddFile("/açık-boşluk.bin", new SparseContent(300_000, (100_000, Fill(5000, 0xCC))), explicitHoles: true)
            .AddFile("/baştan-boş.bin", new SparseContent(65_536 + 10, (65_536, Fill(10, 0xDD))), explicitHoles: true);
        return b;
    }

    public const long LargeVideoSize = 6L * 1024 * 1024 * 1024 + 12_345;

    /// <summary>
    /// M2: 4 GB sınırını aşan, 7 parçaya bölünmüş bir "video" (MarkerContent, görüntü dosyasında az yer kaplar)
    /// ve hız ölçümü için 300 MB gerçek (sıfır olmayan) veri.
    /// </summary>
    public static ApfsImageBuilder Large()
    {
        var b = new ApfsImageBuilder();
        b.AddVolume("Büyük Dosyalar")
            .AddFile("/Videolar/tatil.mov", new MarkerContent(LargeVideoSize, 3), fragments: 7)
            .AddFile("/orta.bin", FileContent.Pattern(300L * 1024 * 1024, 4), fragments: 3)
            .AddFile("/küçük.txt", FileContent.FromText("küçük bir dosya\n"));
        return b;
    }

    /// <summary>M2: tek klasörde 10.000 dosya (büyük klasörde arama ve listeleme hızı).</summary>
    public static ApfsImageBuilder BigDir()
    {
        var b = new ApfsImageBuilder();
        var v = b.AddVolume("Büyük Klasör");
        for (int i = 0; i < 10_000; i++)
            v.AddFile($"/Fotoğraflar/IMG_{i:D5}.jpg", FileContent.FromText($"foto {i}\n"));
        return b;
    }

    /// <summary>Mac'teki gibi NFD (ayrışık) biçimde saklanan ad.</summary>
    public static string Nfd(string s) => s.Normalize(System.Text.NormalizationForm.FormD);

    /// <summary>
    /// M3: büyük/küçük harf duyarsız birim (macOS varsayılanı). Türkçe harfler (NFD ve NFC), ı/i ayrımı,
    /// Windows'ta yasak karakterler, sondaki nokta/boşluk, kontrol karakteri, emoji ve diğer alfabeler.
    /// </summary>
    public static ApfsImageBuilder Unicode()
    {
        var b = new ApfsImageBuilder();
        b.AddVolume("Türkçe Ünicode")
            .AddFile("/" + Nfd("Şeker ğüİıöç.txt"), FileContent.FromText("şeker\n"))
            .AddFile("/Kılıç.txt", FileContent.FromText("noktasız ı\n"))
            .AddFile("/Kiliç.txt", FileContent.FromText("noktalı i\n"))
            .AddFile("/İstanbul.txt", FileContent.FromText("büyük noktalı İ\n"))
            .AddFile("/" + Nfd("Müşteri Dosyaları") + "/" + Nfd("Ödeme Ğ.txt"), FileContent.FromText("ödeme\n"))
            .AddFile("/a:b.txt", FileContent.FromText("iki nokta\n"))
            .AddFile("/soru?.txt", FileContent.FromText("soru işareti\n"))
            .AddFile("/yıldız*.txt", FileContent.FromText("yıldız\n"))
            .AddFile("/tırnak\"lı.txt", FileContent.FromText("tırnak\n"))
            .AddFile("/boru|çizgi.txt", FileContent.FromText("boru\n"))
            .AddFile("/küçük<büyük>.txt", FileContent.FromText("açılı ayraç\n"))
            .AddFile("/ters\\bölü.txt", FileContent.FromText("ters bölü\n"))
            .AddFile("/sonu nokta.", FileContent.FromText("sonda nokta\n"))
            .AddFile("/sonu boşluk ", FileContent.FromText("sonda boşluk\n"))
            .AddFile("/kontrol\u0001karakter.txt", FileContent.FromText("kontrol\n"))
            .AddFile("/emoji 😀.txt", FileContent.FromText("emoji\n"))
            .AddFile("/日本語のファイル.txt", FileContent.FromText("japonca\n"))
            .AddFile("/Ελληνικά.txt", FileContent.FromText("yunanca\n"))
            .AddFile("/Straße.txt", FileContent.FromText("almanca\n"))
            .AddFile("/.DS_Store", FileContent.FromBytes([0, 0, 0, 1]))
            .AddFile("/._" + Nfd("Şeker ğüİıöç.txt"), FileContent.FromBytes([0, 5, 22, 7]));
        return b;
    }

    /// <summary>M3: büyük/küçük harf DUYARLI birim: yalnızca harf büyüklüğüyle ayrılan iki dosya.</summary>
    public static ApfsImageBuilder UnicodeCaseSensitive()
    {
        var b = new ApfsImageBuilder();
        b.AddVolume("Duyarlı", caseInsensitive: false)
            .AddFile("/Rapor.txt", FileContent.FromText("büyük R\n"))
            .AddFile("/rapor.txt", FileContent.FromText("küçük r\n"))
            .AddFile("/Belge.txt", FileContent.FromText("tek belge\n"))
            .AddFile("/" + Nfd("Çiçek.txt"), FileContent.FromText("çiçek\n"));
        return b;
    }

    /// <summary>
    /// M4: 7-Zip'in de açabildiği sıkıştırma türleri (zlib 3/4, LZVN 7/8, kopya 9), sıkıştırılamayan veri için ham işaretler,
    /// birden çok 64 KiB bloklu kaynak çatalları. Sıkıştırılmamış bir dosya da var (karışık klasör).
    /// </summary>
    public static ApfsImageBuilder Compressed()
    {
        var b = new ApfsImageBuilder();
        b.AddVolume("Sıkıştırılmış")
            .AddCompressedFile("/zlib-küçük.txt", FileContent.Text(2_000, 11), new CompressionSpec(3))
            .AddCompressedFile("/zlib-ham.bin", FileContent.Pattern(500, 12), new CompressionSpec(3))
            .AddCompressedFile("/zlib-büyük.txt", FileContent.Text(300_000, 13), new CompressionSpec(4))
            .AddCompressedFile("/zlib-karışık.bin", Mixed(200_000, 14), new CompressionSpec(4))
            .AddCompressedFile("/lzvn-küçük.txt", FileContent.Text(2_500, 15), new CompressionSpec(7))
            .AddCompressedFile("/lzvn-ham.bin", FileContent.Pattern(300, 16), new CompressionSpec(7))
            .AddCompressedFile("/lzvn-büyük.txt", FileContent.Text(250_000, 17), new CompressionSpec(8))
            .AddCompressedFile("/lzvn-karışık.bin", Mixed(200_000, 18), new CompressionSpec(8))
            .AddCompressedFile("/kopya.txt", FileContent.Text(1_000, 19), new CompressionSpec(9))
            .AddCompressedFile("/Uygulama.app/Contents/MacOS/ikili", FileContent.Text(70_000, 20), new CompressionSpec(8))
            .AddFile("/düz.txt", FileContent.FromText("sıkıştırılmamış dosya\n"));
        return b;
    }

    /// <summary>
    /// M4: 7-Zip'in açamadığı türler: LZFSE (11/12; veri Apple kodlayıcısıyla üretildi), sıkıştırılmamış kaynak çatalı (10)
    /// ve desteklenmeyen bir tür (13, LZBITMAP) — bu son dosya listelenmeli ama açılınca anlaşılır hata vermeli.
    /// </summary>
    public static ApfsImageBuilder CompressedLzfse()
    {
        var b = new ApfsImageBuilder();
        b.AddVolume("LZFSE")
            .AddCompressedFile("/lzfse-küçük.txt", FileContent.FromBytes(CompressionVectors.Read("small-text.bin")),
                new CompressionSpec(11, PrecompressedInline: CompressionVectors.Read("small-text.lzfse")))
            .AddCompressedFile("/lzfse-büyük.txt", FileContent.FromBytes(CompressionVectors.Read("rsrc-src.bin")),
                new CompressionSpec(12, PrecompressedBlocks: CompressionVectors.ReadBlocks("rsrc-lzfse.blocks")))
            .AddCompressedFile("/kopya-çatal.bin", FileContent.Pattern(100_000, 21), new CompressionSpec(10))
            .AddCompressedFile("/desteklenmeyen.bin", FileContent.Pattern(5_000, 22), new CompressionSpec(13));
        return b;
    }

    /// <summary>
    /// M5: dört birimli kapsayıcı. "Belgeler" (rolsüz) ve "Fotoğraflar" (Veri) kullanıcıya görünür; "Preboot" (Önyükleme) ve
    /// "Recovery" (Kurtarma) macOS'un iç birimleri olarak varsayılan olarak gizlenir. Her birimde aynı adlı ama farklı içerikli dosya var
    /// (birimlerin birbirine karışmadığını göstermek için).
    /// </summary>
    public static ApfsImageBuilder MultiVolume()
    {
        var b = new ApfsImageBuilder();
        b.AddVolume("Belgeler")
            .AddFile("/hangi-birim.txt", FileContent.FromText("Belgeler\n"))
            .AddFile("/rapor.txt", FileContent.Text(5_000, 31));
        b.AddVolume("Fotoğraflar", role: 0x0040)
            .AddFile("/hangi-birim.txt", FileContent.FromText("Fotoğraflar\n"))
            .AddFile("/2024/tatil.jpg", FileContent.Pattern(300_000, 32));
        b.AddVolume("Preboot", role: 0x0010)
            .AddFile("/hangi-birim.txt", FileContent.FromText("Preboot\n"));
        b.AddVolume("Recovery", role: 0x0004)
            .AddFile("/hangi-birim.txt", FileContent.FromText("Recovery\n"));
        return b;
    }

    /// <summary>
    /// M6: şifreli birim ("Kasa", Türkçe karakterli parola, ipucu) ve yanında şifresiz bir birim ("Açık").
    /// Şifreli birimde çok parçalı büyük dosya, sıkıştırılmış dosya (kaynak çatalı da şifreli), Türkçe adlar ve çok seviyeli ağaç var.
    /// </summary>
    public static ApfsImageBuilder Encrypted()
    {
        var b = new ApfsImageBuilder();
        var kasa = b.AddVolume("Kasa")
            .AddFile("/gizli.txt", FileContent.FromText("Bu metin şifreli bir APFS biriminden okundu.\n"))
            .AddFile("/Belgeler/büyük.bin", FileContent.Pattern(3_000_000 + 77, 41), fragments: 3)
            .AddCompressedFile("/Belgeler/sıkıştırılmış.txt", FileContent.Text(200_000, 42), new CompressionSpec(8))
            .AddFile("/Şeker ğüİıöç.txt", FileContent.FromText("türkçe\n"));
        for (int i = 0; i < 300; i++) kasa.AddFile($"/Çok/dosya{i:D3}.txt", FileContent.FromText($"{i}\n"));
        kasa.Password = EncryptedPassword;
        kasa.PasswordHint = "her zamanki parola";
        b.AddVolume("Açık").AddFile("/hangi-birim.txt", FileContent.FromText("Açık\n"));
        return b;
    }

    /// <summary>Teşhis için tek bir sıkıştırılmış dosya ("c-3", "c-8" …; LZFSE türleri hariç).</summary>
    public static ApfsImageBuilder SingleCompressed(uint type)
    {
        var b = new ApfsImageBuilder();
        b.AddVolume("Tek").AddCompressedFile("/dosya.txt", FileContent.Text(type is 4 or 8 or 10 ? 100_000 : 2_000, 1), new CompressionSpec(type));
        return b;
    }

    /// <summary>Sıkıştırılabilir ve sıkıştırılamaz bölümlerin karışımı (bazı bloklar ham işaretle saklanır).</summary>
    private static FileContent Mixed(int length, int seed)
    {
        var text = FileContent.Text(length, seed).ToArray();
        var noise = FileContent.Pattern(length, seed).ToArray();
        var result = new byte[length];
        for (int i = 0; i < length; i++) result[i] = (i / 65536) % 2 == 0 ? text[i] : noise[i];
        return FileContent.FromBytes(result);
    }

    /// <summary>Çok seviyeli B-ağacı üretmek için çok sayıda dosya.</summary>
    public static ApfsImageBuilder ManyFiles()
    {
        var b = new ApfsImageBuilder();
        var v = b.AddVolume("Kalabalık");
        for (int d = 0; d < 20; d++)
            for (int f = 0; f < 100; f++)
                v.AddFile($"/klasör{d:D2}/dosya{f:D3}.txt", FileContent.FromText($"klasör {d} dosya {f}\n"));
        return b;
    }

    private static byte[] Fill(int n, byte value) => Enumerable.Repeat(value, n).ToArray();
}
