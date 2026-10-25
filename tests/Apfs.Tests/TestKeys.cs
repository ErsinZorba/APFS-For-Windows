using Apfs.Container;
using Apfs.Crypto;

namespace Apfs.Tests;

/// <summary>
/// Test görüntülerindeki şifreli birimlerin anahtarları: <see cref="ImageBuilder.ImageEncryptor"/>'ın parolasıyla açılır. Şifresiz
/// görüntüde boş liste döner; böylece aynı test şifreli ve şifresiz görüntüde değişmeden koşar.
/// </summary>
internal static class TestKeys
{
    public static List<VolumeKey> Of(ApfsContainer c)
    {
        var keys = new List<VolumeKey>();
        foreach (ulong oid in c.Superblock.FsOids)
        {
            var sb = c.Reader.ReadObject(c.Omap.Resolve(oid, c.Xid).Paddr, ApfsConstants.TypeFs, oid);
            if ((Util.Bin.U64(sb, 264) & 3) == ApfsConstants.ApfsFsUnencrypted) continue;
            keys.Add(VolumeKey.Unlock(c, c.VolumeUuid(oid), ImageBuilder.ImageEncryptor.Password));
        }
        return keys;
    }

    /// <summary>İlk birimin anahtarı (şifresizse null).</summary>
    public static VolumeKey? First(ApfsContainer c)
    {
        ulong oid = c.Superblock.FsOids[0];
        var sb = c.Reader.ReadObject(c.Omap.Resolve(oid, c.Xid).Paddr, ApfsConstants.TypeFs, oid);
        return (Util.Bin.U64(sb, 264) & 3) == ApfsConstants.ApfsFsUnencrypted ? null
            : VolumeKey.Unlock(c, c.VolumeUuid(oid), ImageBuilder.ImageEncryptor.Password);
    }
}
