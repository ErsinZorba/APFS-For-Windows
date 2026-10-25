namespace Apfs.ImageBuilder;

/// <summary>Görüntüyü APFS bölümlü bir GPT diskine sarar (512 baytlık sektör). Birden çok kapsayıcı art arda bölümlere konur.</summary>
internal static class Gpt
{
    private const int Sector = 512;
    private const int EntryCount = 128;
    private const int EntrySize = 128;
    private const int EntrySectors = EntryCount * EntrySize / Sector; // 32
    private const long FirstPartitionLba = 40;
    public const long PartitionStartBytes = FirstPartitionLba * Sector;

    private static readonly Guid ApfsType = Guid.Parse("7C3457EF-0000-11AA-AA11-00306543ECAC");

    public static long TotalSize(long containerBytes) => TotalSize([containerBytes]);

    public static long TotalSize(IReadOnlyList<long> containerBytes) =>
        PartitionStartBytes + containerBytes.Sum() + (EntrySectors + 1) * Sector;

    /// <summary><paramref name="index"/>. bölümün diskteki başlangıcı (bayt).</summary>
    public static long PartitionStart(IReadOnlyList<long> containerBytes, int index) =>
        PartitionStartBytes + containerBytes.Take(index).Sum();

    public static void Write(FileStream fs, long containerBytes, Guid diskSeed) => Write(fs, [containerBytes], diskSeed);

    public static void Write(FileStream fs, IReadOnlyList<long> containerBytes, Guid diskSeed)
    {
        long totalSectors = TotalSize(containerBytes) / Sector;
        long lastUsable = totalSectors - EntrySectors - 2;

        // Koruyucu MBR
        var mbr = new byte[Sector];
        var m = new BlockWriter(mbr);
        m.U8(446 + 4, 0xEE);
        m.U32(446 + 8, 1);
        m.U32(446 + 12, (uint)Math.Min(totalSectors - 1, uint.MaxValue));
        m.U8(510, 0x55);
        m.U8(511, 0xAA);
        fs.Position = 0;
        fs.Write(mbr);

        var entries = new byte[EntryCount * EntrySize];
        var e = new BlockWriter(entries);
        for (int i = 0; i < containerBytes.Count; i++)
        {
            int o = i * EntrySize;
            long first = PartitionStart(containerBytes, i) / Sector;
            ApfsType.TryWriteBytes(entries.AsSpan(o, 16));
            // Tek bölümde eski sabit kimlik korunur (var olan görüntülerle aynı bayt)
            Guid.Parse(i == 0 ? "11111111-2222-4333-8444-555555555555" : $"11111111-2222-4333-8444-{555555555555 + i:D12}")
                .TryWriteBytes(entries.AsSpan(o + 16, 16));
            e.U64(o + 32, (ulong)first);
            e.U64(o + 40, (ulong)(first + containerBytes[i] / Sector - 1));
            var name = System.Text.Encoding.Unicode.GetBytes(i == 0 ? "Test APFS" : $"Test APFS {i + 1}");
            e.Bytes(o + 56, name);
        }
        uint entriesCrc = Crc32.Compute(entries);

        byte[] Header(long myLba, long altLba, long entriesLba)
        {
            var h = new byte[Sector];
            var w = new BlockWriter(h);
            w.Bytes(0, "EFI PART"u8);
            w.U32(8, 0x00010000);
            w.U32(12, 92);
            w.U64(24, (ulong)myLba);
            w.U64(32, (ulong)altLba);
            w.U64(40, (ulong)(2 + EntrySectors));
            w.U64(48, (ulong)lastUsable);
            diskSeed.TryWriteBytes(h.AsSpan(56, 16));
            w.U64(72, (ulong)entriesLba);
            w.U32(80, EntryCount);
            w.U32(84, EntrySize);
            w.U32(88, entriesCrc);
            w.U32(16, Crc32.Compute(h.AsSpan(0, 92)));
            return h;
        }

        fs.Position = Sector;
        fs.Write(Header(1, totalSectors - 1, 2));
        fs.Position = 2 * Sector;
        fs.Write(entries);
        fs.Position = (totalSectors - 1 - EntrySectors) * Sector;
        fs.Write(entries);
        fs.Position = (totalSectors - 1) * Sector;
        fs.Write(Header(totalSectors - 1, 1, totalSectors - 1 - EntrySectors));
    }
}

/// <summary>Var olan APFS kapsayıcı görüntülerini GPT diskine sarar (apfs-mkimage wrap ve testler).</summary>
public static class GptDisk
{
    /// <summary>Kapsayıcıları sırayla bölümlere koyar; .vhd uzantısında sabit VHD alt bilgisi eklenir.</summary>
    public static void Wrap(IReadOnlyList<string> containers, string output)
    {
        var sizes = containers.Select(c => new FileInfo(c).Length).ToList();
        if (sizes.Any(s => s % 512 != 0)) throw new ArgumentException("kapsayıcı boyutu 512'nin katı olmalı");
        // Sonraki bölümler 4 KiB hizalı başlasın
        if (sizes.SkipLast(1).Any(s => s % 4096 != 0)) throw new ArgumentException("son kapsayıcı dışındakilerin boyutu 4096'nın katı olmalı");
        long diskBytes = Gpt.TotalSize(sizes);
        using var outFs = new FileStream(output, FileMode.Create, FileAccess.ReadWrite);
        outFs.SetLength(diskBytes);
        Gpt.Write(outFs, sizes, Guid.NewGuid());
        for (int i = 0; i < containers.Count; i++)
        {
            outFs.Position = Gpt.PartitionStart(sizes, i);
            using var inFs = File.OpenRead(containers[i]);
            inFs.CopyTo(outFs);
        }
        if (output.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase)) Vhd.AppendFooter(outFs, diskBytes);
    }
}

/// <summary>CRC-32 (IEEE 802.3), GPT başlığı ve bölüm tablosu için.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
