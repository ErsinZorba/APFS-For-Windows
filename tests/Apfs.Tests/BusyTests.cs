using System.Runtime.InteropServices;
using Apfs.Mount;

namespace Apfs.Tests;

/// <summary>"Güvenle çıkar" uyarısının dayandığı açık dosya sayacı (ApfsFileSystem.IsBusy).</summary>
public class BusyTests(TestImages images) : IClassFixture<TestImages>
{
    private ApfsFileSystem NewFileSystem(out IDisposable owner)
    {
        var c = TestImages.Open(images.Get("basic").Path);
        owner = c;
        return new ApfsFileSystem(c.OpenVolumes()[0].Volume!, 0, 0);
    }

    private static object OpenPath(ApfsFileSystem fs, string path, out object node)
    {
        Assert.Equal(0, fs.Open(path, 0, 0, out node, out var desc, out _, out _));
        return desc;
    }

    [Fact]
    public void OpenFileMakesVolumeBusyUntilCleanup()
    {
        var fs = NewFileSystem(out var owner);
        using (owner)
        {
            Assert.False(fs.IsBusy);
            var desc = OpenPath(fs, "\\Merhaba.txt", out var node);
            Assert.Equal(1, fs.OpenFileCount);
            Assert.True(fs.IsBusy);
            fs.Cleanup(node, desc, null!, 0);
            Assert.Equal(0, fs.OpenFileCount);
            Assert.False(fs.IsBusy);
            fs.Close(node, desc); // Cleanup'tan sonra gelen Close ikinci kez azaltmaz
            Assert.Equal(0, fs.OpenFileCount);
        }
    }

    [Fact]
    public void DirectoriesAreNotCounted()
    {
        var fs = NewFileSystem(out var owner);
        using (owner)
        {
            var desc = OpenPath(fs, "\\Belgeler", out var node);
            Assert.Equal(0, fs.OpenFileCount);
            Assert.False(fs.IsBusy);
            fs.Cleanup(node, desc, null!, 0);
            fs.Close(node, desc);
            Assert.Equal(0, fs.OpenFileCount);
        }
    }

    [Fact]
    public void CloseWithoutCleanupStillReleases()
    {
        var fs = NewFileSystem(out var owner);
        using (owner)
        {
            var a = OpenPath(fs, "\\Merhaba.txt", out var na);
            var b = OpenPath(fs, "\\Merhaba.txt", out var nb);
            Assert.Equal(2, fs.OpenFileCount);
            fs.Close(na, a); // çekirdek açılışı reddetti: yalnızca Close
            Assert.Equal(1, fs.OpenFileCount);
            fs.Cleanup(nb, b, null!, 0);
            fs.Close(nb, b);
            Assert.Equal(0, fs.OpenFileCount);
        }
    }

    [Fact]
    public void RecentReadKeepsVolumeBusyBriefly()
    {
        var fs = NewFileSystem(out var owner);
        using (owner)
        {
            var desc = OpenPath(fs, "\\Merhaba.txt", out var node);
            IntPtr buf = Marshal.AllocHGlobal(4096);
            try { Assert.Equal(0, fs.Read(node, desc, buf, 0, 4096, out _)); }
            finally { Marshal.FreeHGlobal(buf); }
            fs.Cleanup(node, desc, null!, 0);
            Assert.Equal(0, fs.OpenFileCount);
            Assert.True(fs.IsBusy); // kopyalamada dosyalar arasındaki boşluk
        }
    }
}
