using System.Globalization;
using Apfs.Localization;
using Apfs.Windows.Localization;
using Microsoft.Win32;

namespace Apfs.Tests;

/// <summary>Arayüz dili çözümleme ve kullanıcının elle seçtiği dilin kaydı.</summary>
public class LanguageTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("tr-TR", "tr")]
    [InlineData("de-AT", "de")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("ja-JP", "ja")]
    [InlineData("KO-kr", "ko")]
    [InlineData("pt-PT", null)]
    [InlineData("sv-SE", null)]
    [InlineData("xx", null)]
    [InlineData("xx;rm -rf", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ResolvesToSupportedLanguage(string? input, string? expected) =>
        Assert.Equal(expected, Languages.Resolve(input)?.Name);

    [Fact]
    public void EverySupportedLanguageIsAKnownCulture()
    {
        Assert.Equal(10, Languages.Supported.Count);
        foreach (var (name, native) in Languages.Supported)
        {
            Assert.Equal(name, Languages.Resolve(name)?.Name);
            Assert.False(string.IsNullOrWhiteSpace(native));
        }
    }

    [Fact]
    public void SettingRoundTripsAndIgnoresInvalidValues()
    {
        string path = @"Software\APFS for Windows Test " + Guid.NewGuid().ToString("N")[..8];
        string old = LanguageSetting.KeyPath;
        LanguageSetting.KeyPath = path;
        try
        {
            Assert.Null(LanguageSetting.Load());
            LanguageSetting.Save(CultureInfo.GetCultureInfo("de"));
            Assert.Equal("de", LanguageSetting.Load()?.Name);
            using (var key = Registry.CurrentUser.CreateSubKey(path)) key.SetValue("Language", "bozuk;değer");
            Assert.Null(LanguageSetting.Load());
            LanguageSetting.Save(null);
            Assert.Null(LanguageSetting.Load());
        }
        finally
        {
            LanguageSetting.KeyPath = old;
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void MissingKeyNeverThrows()
    {
        Assert.Equal("Yok.Boyle.Bir.Anahtar", CoreText.U("Yok.Boyle.Bir.Anahtar"));
        Assert.Equal("Yok.Boyle.Bir.Anahtar: 1, x", WinText.T("Yok.Boyle.Bir.Anahtar", 1, "x"));
    }
}
