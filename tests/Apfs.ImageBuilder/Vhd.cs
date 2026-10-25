using System.Buffers.Binary;

namespace Apfs.ImageBuilder;

/// <summary>
/// Ham disk görüntüsünün sonuna sabit boyutlu VHD altbilgisi ekler (Microsoft VHD Image Format Specification).
/// Böylece görüntü Windows'a sanal disk olarak takılabilir ve gerçek bir \\.\PhysicalDriveN gibi görünür.
/// </summary>
internal static class Vhd
{
    public static void AppendFooter(FileStream fs, long diskBytes)
    {
        if (diskBytes % 512 != 0) throw new ArgumentException("VHD boyutu 512'nin katı olmalı");
        var f = new byte[512];
        "conectix"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(8), 2);              // features
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(12), 0x00010000);    // sürüm
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(16), ulong.MaxValue); // sabit disk: data offset yok
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(24), (uint)(new DateTime(2024, 5, 17, 0, 0, 0, DateTimeKind.Utc) - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
        "apfs"u8.CopyTo(f.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(32), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(36), 0x5769326B);    // "Wi2k"
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(40), (ulong)diskBytes);
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(48), (ulong)diskBytes);
        var (c, h, s) = Geometry(diskBytes / 512);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(56), c);
        f[58] = h;
        f[59] = s;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(60), 2);             // sabit disk
        Guid.Parse("6A2F9C1E-58B0-4D0A-9E71-3C4B5D6E7F80").TryWriteBytes(f.AsSpan(68, 16), bigEndian: true, out _);
        uint sum = 0;
        foreach (var b in f) sum += b;
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(64), ~sum);
        fs.Position = diskBytes;
        fs.Write(f);
    }

    /// <summary>VHD spesifikasyonundaki CHS hesaplama algoritması.</summary>
    private static (ushort C, byte H, byte S) Geometry(long totalSectors)
    {
        totalSectors = Math.Min(totalSectors, 65535L * 16 * 255);
        long spt, heads, cth;
        if (totalSectors >= 65535L * 16 * 63)
        {
            spt = 255; heads = 16; cth = totalSectors / spt;
        }
        else
        {
            spt = 17;
            cth = totalSectors / spt;
            heads = Math.Max(4, (cth + 1023) / 1024);
            if (cth >= heads * 1024 || heads > 16) { spt = 31; heads = 16; cth = totalSectors / spt; }
            if (cth >= heads * 1024) { spt = 63; heads = 16; cth = totalSectors / spt; }
        }
        return ((ushort)(cth / heads), (byte)heads, (byte)spt);
    }
}
