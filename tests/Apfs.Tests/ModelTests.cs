using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Volume;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>
/// Faz 2.7: bütün işlemlerin rastgele karışımı (oluştur, yaz, kısalt, uzat, sil, ad değiştir, taşı, üzerine yaz) hem görüntüye hem
/// bellekteki modele uygulanır. Belli aralıklarla commit edilir; görüntü sıfırdan açılıp bütün ağaç ve içerikler modelle
/// karşılaştırılır, tam denetimden geçer. APFS_LONG=1 ile uzun koşu (20.000 işlem).
/// </summary>
public class ModelTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    private sealed class MNodeModel
    {
        public bool IsDir;
        public List<byte> Data = [];
        public Dictionary<string, MNodeModel> Children = new(StringComparer.Ordinal);
    }

    private static readonly string[] NameParts = ["a", "b", "çay", "ığdır", "ŞEHİR", "x y", "ödev", "z"];

    [Theory]
    [InlineData("mk-ci-1g", 11)]
    [InlineData("pop-ci-64m", 12)]
    [InlineData("enc-pop-ci-64m", 13)]
    public void RandomMixedOperationsMatchModel(string fixture, int seed)
    {
        int ops = Environment.GetEnvironmentVariable("APFS_LONG") == "1" ? 20_000 : 3_000;
        var (path, grant) = FsOpsTests.Prepare(fixtures, fixture);
        var rnd = new Random(seed);
        var root = new MNodeModel { IsDir = true };
        int nameCounter = 0;
        string NewName() => $"{NameParts[rnd.Next(NameParts.Length)]}-{nameCounter++}";

        using (var dev = new FileImageWriter(path, grant))
        using (var s = WriteSession.Open(dev, grant))
        {
            var v = s.Volume(0, TestKeys.First(s.Container));
            ulong top = v.CreateDirectory(Inode.RootDirId, "model");
            var ids = new Dictionary<MNodeModel, ulong> { [root] = top };

            // Modeldeki rastgele klasör/dosya (yoluyla birlikte)
            List<(MNodeModel Parent, string Name, MNodeModel Node)> All()
            {
                var list = new List<(MNodeModel, string, MNodeModel)>();
                void Walk(MNodeModel d) { foreach (var (n, c) in d.Children) { list.Add((d, n, c)); if (c.IsDir) Walk(c); } }
                Walk(root);
                return list;
            }
            List<MNodeModel> Dirs() => [root, .. All().Where(x => x.Node.IsDir).Select(x => x.Node)];

            for (int op = 1; op <= ops; op++)
            {
                var all = All();
                var files = all.Where(x => !x.Node.IsDir).ToList();
                int kind = rnd.Next(100);
                if (kind < 15 || files.Count == 0)
                {
                    var parent = Dirs()[rnd.Next(Dirs().Count)];
                    string name = NewName();
                    bool dir = rnd.Next(4) == 0;
                    var node = new MNodeModel { IsDir = dir };
                    ids[node] = dir ? v.CreateDirectory(ids[parent], name) : v.CreateFile(ids[parent], name);
                    parent.Children[name] = node;
                }
                else if (kind < 45)
                {
                    var f = files[rnd.Next(files.Count)].Node;
                    int off = rnd.Next(0, f.Data.Count + 2000);
                    var chunk = new byte[rnd.Next(1, rnd.Next(10) == 0 ? 100_000 : 6000)];
                    rnd.NextBytes(chunk);
                    v.Write(ids[f], (ulong)off, chunk);
                    while (f.Data.Count < off) f.Data.Add(0);
                    for (int i = 0; i < chunk.Length; i++) { if (off + i < f.Data.Count) f.Data[off + i] = chunk[i]; else f.Data.Add(chunk[i]); }
                }
                else if (kind < 55)
                {
                    var f = files[rnd.Next(files.Count)].Node;
                    int size = rnd.Next(2) == 0 ? rnd.Next(0, f.Data.Count + 1) : f.Data.Count + rnd.Next(0, 50_000);
                    v.SetSize(ids[f], (ulong)size);
                    if (size < f.Data.Count) f.Data.RemoveRange(size, f.Data.Count - size); else while (f.Data.Count < size) f.Data.Add(0);
                }
                else if (kind < 70 && all.Count > 0)
                {
                    var (p, n, node) = all[rnd.Next(all.Count)];
                    if (node.IsDir && node.Children.Count > 0)
                        Assert.Equal(ApfsErrorCode.DirectoryNotEmpty, Assert.Throws<ApfsException>(() => v.Delete(ids[p], n)).Code);
                    else { v.Delete(ids[p], n); p.Children.Remove(n); ids.Remove(node); }
                }
                else if (all.Count > 0)
                {
                    var (p, n, node) = all[rnd.Next(all.Count)];
                    var dirs = Dirs();
                    var dst = dirs[rnd.Next(dirs.Count)];
                    // kendi içine taşıma reddedilmeli
                    bool intoSelf = false;
                    if (node.IsDir) { void Walk(MNodeModel d) { if (d == dst) intoSelf = true; foreach (var c in d.Children.Values) if (c.IsDir) Walk(c); } Walk(node); }
                    bool replace = rnd.Next(3) == 0 && !intoSelf;
                    var existing = replace ? dst.Children.FirstOrDefault(kv => kv.Value.IsDir == node.IsDir && kv.Value != node && (!kv.Value.IsDir || kv.Value.Children.Count == 0)) : default;
                    string newName = existing.Key ?? NewName();
                    if (intoSelf)
                    {
                        Assert.Throws<ApfsException>(() => v.Rename(ids[p], n, ids[dst], newName, false));
                    }
                    else
                    {
                        v.Rename(ids[p], n, ids[dst], newName, replace: existing.Key is not null);
                        if (existing.Key is not null) ids.Remove(dst.Children[newName]);
                        p.Children.Remove(n);
                        dst.Children[newName] = node;
                    }
                }

                if (op % 150 == 0)
                {
                    s.Commit();
                    AssertMatches(dev, root);
                }
            }
            s.Close();
        }
        using (var c = TestImages.Open(path)) Assert.True(FullCheck.Run(c, keys: TestKeys.Of(c)).IsClean);
        DevOutput.Save(path, $"model-{fixture}-{seed}");
    }

    private static void AssertMatches(IBlockDevice dev, MNodeModel root)
    {
        using var c = Apfs.Container.ApfsContainer.Open(dev, ownsDevice: false);
        var r = FullCheck.Run(c, keys: TestKeys.Of(c));
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        var vol = c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!;
        void Compare(MNodeModel d, Inode dir, string path)
        {
            var entries = vol.ListDirectory(dir.Id);
            Assert.Equal(d.Children.Count, entries.Count);
            foreach (var e in entries)
            {
                Assert.True(d.Children.TryGetValue(e.Name, out var m), $"{path}/{e.Name} modelde yok");
                var ino = vol.GetInode(e.FileId);
                Assert.Equal(m!.IsDir, ino.IsDirectory);
                if (m.IsDir) Compare(m, ino, $"{path}/{e.Name}");
                else Assert.True(m.Data.ToArray().AsSpan().SequenceEqual(ReaderTests.ReadAll(vol, ino)), $"{path}/{e.Name} içeriği farklı");
            }
        }
        Compare(root, vol.Lookup("model"), "model");
    }
}
