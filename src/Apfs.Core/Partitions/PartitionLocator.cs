using Apfs.Container;
using Apfs.IO;
using Apfs.Util;

namespace Apfs.Partitions;

/// <summary>Bir aygıtta bulunan APFS kapsayıcısının konumu.</summary>
public sealed record ApfsPartition(long Offset, long Length, int Index, string Description);

/// <summary>GPT bölüm tablosunu okuyup APFS bölümlerini bulur. Bölüm tablosu yoksa aygıtın tamamını kapsayıcı kabul eder.</summary>
public static class PartitionLocator
{
    public static readonly Guid ApfsPartitionType = Guid.Parse("7C3457EF-0000-11AA-AA11-00306543ECAC");

    private static readonly byte[] GptSignature = "EFI PART"u8.ToArray();
    private const int MaxPartitionEntries = 1024;

    public static IReadOnlyList<ApfsPartition> FindApfsPartitions(IBlockDevice device)
    {
        // Doğrudan bir APFS kapsayıcısı mı (bölüm tablosu olmayan görüntü)?
        if (LooksLikeContainer(device, 0))
            return [new ApfsPartition(0, device.Length, 0, "bölümsüz APFS kapsayıcısı")];

        // GPT başlığı 1. LBA'dadır; sektör boyutu 512 veya 4096 olabilir.
        foreach (int sector in new[] { device.SectorSize, 512, 4096 }.Distinct())
        {
            if (device.Length < sector * 2L) continue;
            var header = new byte[sector];
            device.Read(sector, header);
            if (!header.AsSpan(0, 8).SequenceEqual(GptSignature)) continue;
            return ParseGpt(device, header, sector);
        }
        return [];
    }

    /// <summary>
    /// Diskteki APFS kapsayıcılarının kimliği: bölüm konumu/boyu + bölüm başındaki ilk 4 KiB'ın (blok 0) SHA-256'sı.
    /// Takılıyken blok 0'ı yalnızca yazma oturumunun Close'u yazar (macOS bağlı değildir); hizmet onu diski bıraktıktan sonra
    /// çağırır ve yeniden bağlarken kimliği tazeler. Başka bir program diski biçimlendirir ya da üzerine bir görüntü yazarsa
    /// (aynı UUID'li bir yedek bile) değişir. APFS yoksa boş metin.
    /// </summary>
    public static string Fingerprint(IBlockDevice device)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in FindApfsPartitions(device))
        {
            var head = new byte[Math.Min(ApfsConstants.MinBlockSize, p.Length)];
            device.Read(p.Offset, head);
            sb.Append($"{p.Offset}+{p.Length}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(head))};");
        }
        return sb.ToString();
    }

    /// <summary>Verilen konumda NXSB süper bloğu var mı?</summary>
    public static bool LooksLikeContainer(IBlockDevice device, long offset)
    {
        if (device.Length < offset + ApfsConstants.MinBlockSize) return false;
        Span<byte> head = stackalloc byte[40];
        device.Read(offset, head);
        return Bin.U32(head, 32) == ApfsConstants.NxMagic;
    }

    private static List<ApfsPartition> ParseGpt(IBlockDevice device, byte[] header, int sector)
    {
        ulong entriesLba = Bin.U64(header, 72);
        uint count = Bin.U32(header, 80);
        uint entrySize = Bin.U32(header, 84);
        if (entrySize < 128 || entrySize > 4096 || count == 0 || count > MaxPartitionEntries)
            throw ApfsException.Corrupt("GPT bölüm tablosu başlığı geçersiz");

        long tableOffset = checked((long)entriesLba * sector);
        long tableLength = (long)count * entrySize;
        if (tableOffset < 0 || tableOffset > device.Length - tableLength)
            throw ApfsException.Corrupt("GPT bölüm tablosu diskin dışında");

        var table = new byte[tableLength];
        device.Read(tableOffset, table);

        var result = new List<ApfsPartition>();
        for (int i = 0; i < count; i++)
        {
            var entry = table.AsSpan((int)(i * entrySize), (int)entrySize);
            var type = new Guid(entry[..16]);
            if (type != ApfsPartitionType) continue;
            ulong first = Bin.U64(entry, 32);
            ulong last = Bin.U64(entry, 40);
            if (last < first) continue;
            long offset = (long)first * sector;
            long length = ((long)(last - first) + 1) * sector;
            if (offset < 0 || offset > device.Length - length) continue; // diskin dışına taşan bölüm: atla
            string name = System.Text.Encoding.Unicode.GetString(entry.Slice(56, 72)).TrimEnd('\0');
            result.Add(new ApfsPartition(offset, length, i + 1, string.IsNullOrEmpty(name) ? $"bölüm {i + 1}" : name));
        }
        return result;
    }
}
