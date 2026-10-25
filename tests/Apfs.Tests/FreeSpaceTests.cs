using Apfs.Container;

namespace Apfs.Tests;

/// <summary>
/// Boş alan (kullanıcı geri bildirimi: şifreli diskte Gezgin diski "tamamen boş" gösteriyordu, çünkü kullanılan alan yalnızca
/// açılabilen birimlerden toplanıyordu). Artık alan yöneticisinden, o okunamazsa şifresiz birim sayaçlarından hesaplanır.
/// </summary>
public class FreeSpaceTests(TestImages images) : IClassFixture<TestImages>
{
    [Theory]
    [InlineData("basic")]
    [InlineData("multivolume")]
    [InlineData("encrypted")]
    public void FreeSpaceComesFromSpaceManager(string scenario)
    {
        var (path, builder) = images.Get(scenario);
        using var c = TestImages.Open(path);
        Assert.Equal((long)builder.LastFreeBlocks * 4096, c.FreeBytes);
        Assert.Equal("alan yöneticisi", c.FreeSpaceSource);
    }

    [Fact]
    public void LockedEncryptedVolumeStillCountsAsUsed()
    {
        // Alan yöneticisi okunamasa bile (yedek yol) şifreli, kilidi açılmamış birimin kullandığı alan sayılmalı.
        var (src, _) = images.Get("encrypted");
        var bytes = File.ReadAllBytes(src);
        bytes[9 * 4096 + 100] ^= 0xFF; // alan yöneticisini boz → yedek yol
        using var c = ApfsContainer.Open(new MemoryDevice(bytes));
        Assert.Null(c.OpenVolumes()[0].Volume); // "Kasa" kilitli
        long total = (long)c.Superblock.BlockCount * c.BlockSize;
        Assert.Equal("birim sayaçları", c.FreeSpaceSource);
        Assert.True(c.FreeBytes < total, "kilitli şifreli birimin kullandığı alan sayılmalı");
        Assert.True(c.FreeBytes > 0);
    }
}
