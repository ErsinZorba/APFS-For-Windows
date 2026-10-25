using System.Buffers.Binary;
using System.Globalization;
using Apfs.Diagnostics;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>
/// Kullanıcı metinleri arayüz diline göre: İngilizce, Türkçe ve çevirisi olmayan dil (İngilizceye düşer). Kültür yalnız bu
/// testin iş parçacığında değiştirilir ve geri alınır (öbür testler Türkçe kültürde paralel koşar).
/// </summary>
public class EnglishTextTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    private static T In<T>(string culture, Func<T> f)
    {
        var old = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try { return f(); }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    [Fact]
    public void ErrorMessagesFollowTheUiLanguage()
    {
        Assert.Equal("Wrong password.", In("en-US", () => new ApfsException(ApfsErrorCode.WrongPassword).Message));
        Assert.Equal("Parola yanlış.", In("tr-TR", () => new ApfsException(ApfsErrorCode.WrongPassword).Message));
        Assert.Equal("The disk is full. (x)", In("en", () => new ApfsException(ApfsErrorCode.DiskFull, "x").Message));
        // Çevirisi olmayan dil (İsveççe) İngilizce görür
        Assert.Equal("Wrong password.", In("sv-SE", () => new ApfsException(ApfsErrorCode.WrongPassword).Message));
    }

    [Fact]
    public void GateReasonsFollowTheUiLanguage()
    {
        GateResult Locked()
        {
            using var c = TestImages.Open(fixtures.Copy("enc-pop-ci-64m"));
            return WriteGate.Evaluate(c, null);
        }
        var en = In("en-GB", Locked);
        Assert.False(en.Allowed);
        Assert.Contains(en.Reasons, r => r.Contains("is encrypted; unlock it before allowing writing", StringComparison.Ordinal));
        var tr = In("tr-TR", Locked);
        Assert.Contains(tr.Reasons, r => r.Contains("önce kilidini açın", StringComparison.Ordinal));
    }

    /// <summary>
    /// `apfs check --full --gizle` İngilizcede de adları gizler: teknik metinlerde adlar ASCII çift tırnakta kalmalı
    /// (CheckReport.HideNames yalnız tırnak arasını gizler). Bozuk birim sayacı her hatayı birim adıyla etiketletir.
    /// </summary>
    [Fact]
    public void FullCheckReportIsEnglishAndHidesNamesInEnglish()
    {
        string path = fixtures.Copy("pop-ci-64m");
        string name;
        ulong apsb;
        using (var c = TestImages.Open(path))
        {
            apsb = c.Omap.Resolve(c.Superblock.FsOids[0], c.Xid).Paddr;
            name = Apfs.Volume.ApfsVolume.Open(c, c.Superblock.FsOids[0]).Name;
        }
        FullCheckTests.PatchObject(path, apsb, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(184), 3));

        var r = In("en", () =>
        {
            using var c = TestImages.Open(path);
            return FullCheck.Run(c);
        });
        Assert.Contains(r.Errors, e => e.Contains($"volume \"{name}\"", StringComparison.Ordinal));
        var lines = r.Info.Concat(r.Warnings).Concat(r.Errors).ToList();
        Assert.Contains(lines, l => l.Contains(name, StringComparison.Ordinal));
        Assert.DoesNotContain(lines.Select(CheckReport.HideNames), l => l.Contains(name, StringComparison.Ordinal));
        // İngilizce çıktıda Türkçe harf kalmamalı (adlar gizlendikten sonra)
        Assert.DoesNotContain(lines.Select(CheckReport.HideNames), l => l.IndexOfAny("çğıöşüÇĞİÖŞÜ".ToCharArray()) >= 0);
    }
}
