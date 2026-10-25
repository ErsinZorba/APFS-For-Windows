using Apfs.Windows.Ipc;
using DevService = Apfs.Tests.ServiceWriteTests.DevService;

namespace Apfs.Tests;

/// <summary>
/// Hizmetin dili (ayrı süreç): `--lang` açılıştaki dili, isteğin `Lang` alanı sonraki metinlerin dilini belirler; geçersiz
/// değer yok sayılır, `Lang` gönderilmezse dil değişmez (eski tepsiyle uyumlu).
/// </summary>
[Collection("WinFsp")]
public class ServiceLanguageTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    [Fact]
    public async Task StartupLanguageComesFromLangArgument()
    {
        using var svc = new DevService(fixtures.Copy("mk-ci-64m"), lang: "en");
        await svc.WaitDisk(d => d.Volumes.Any(v => v.State == VolumeStatus.Mounted));
        var st = await svc.Send<StatusResponse>(new Request("status"));
        Assert.Contains(st.Notices, n => n.Text.Contains("was mounted as drive", StringComparison.Ordinal));
        Assert.Contains("Language: en", svc.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestLanguageSwitchesLaterTextsAndInvalidValuesAreIgnored()
    {
        using var svc = new DevService(fixtures.Copy("mk-ci-64m"), lang: "tr");
        var d = await svc.WaitDisk(x => x.Volumes.Any(v => v.State == VolumeStatus.Mounted));
        async Task<string> NoSuchPartition(string? lang) =>
            (await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: true, Container: 99, Lang: lang))).Message;

        Assert.Equal("Bu diskte böyle bir APFS bölümü yok.", await NoSuchPartition("xx;rm -rf"));   // geçersiz: yok sayılır
        Assert.Equal("Bu diskte böyle bir APFS bölümü yok.", await NoSuchPartition("sv-SE"));       // desteklenmiyor: değişmez
        Assert.Equal("Bu diskte böyle bir APFS bölümü yok.", await NoSuchPartition(null));          // dil yok: değişmez
        Assert.Equal("This disk has no such APFS partition.", await NoSuchPartition("en-US"));
        Assert.Equal("This disk has no such APFS partition.", await NoSuchPartition(null));         // kalıcı
        Assert.Equal("Bu diskte böyle bir APFS bölümü yok.", await NoSuchPartition("tr"));
    }
}
