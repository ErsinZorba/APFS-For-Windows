using System.Buffers.Binary;

namespace Apfs.ImageBuilder;

/// <summary>
/// Apple'ın LZFSE kodlayıcısıyla önceden üretilmiş vektörler (tools/gen_lzfse_vectors.py → tests/Apfs.Tests/data/compression).
/// Üretici LZFSE kodlayamadığı için tür 11/12 dosyalar bunlardan kurulur.
/// </summary>
public static class CompressionVectors
{
    public static string Directory { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, "data", "compression"), Path.Combine(dir.FullName, "tests", "Apfs.Tests", "data", "compression") })
                if (File.Exists(Path.Combine(candidate, "manifest.txt"))) return candidate;
        }
        throw new DirectoryNotFoundException("Sıkıştırma test vektörleri bulunamadı (tools/gen_lzfse_vectors.py)");
    }

    public static byte[] Read(string file) => File.ReadAllBytes(Path.Combine(Directory, file));

    /// <summary>rsrc-lzfse.blocks: u32 sayı, u32 boyutlar[], bloklar.</summary>
    public static List<byte[]> ReadBlocks(string file)
    {
        var b = Read(file);
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(b);
        var result = new List<byte[]>(n);
        int pos = 4 + 4 * n;
        for (int i = 0; i < n; i++)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4 + 4 * i));
            result.Add(b.AsSpan(pos, size).ToArray());
            pos += size;
        }
        return result;
    }
}
