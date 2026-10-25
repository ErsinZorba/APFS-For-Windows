namespace Apfs.ImageBuilder;

/// <summary>Test dosyasının içeriği. Büyük dosyalar bellekte tutulmadan üretilebilsin diye soyut.</summary>
public abstract class FileContent
{
    public abstract long Length { get; }

    /// <summary>Verisi olan aralıklar (seyrek dosyalarda boşluklar hariç). Sıralı ve çakışmasız olmalı.</summary>
    public virtual IEnumerable<(long Offset, long Length)> DataRanges => Length > 0 ? [(0, Length)] : [];

    public abstract void Read(long offset, Span<byte> buffer);

    public byte[] ToArray()
    {
        var b = new byte[Length];
        Read(0, b);
        return b;
    }

    public static FileContent FromBytes(byte[] data) => new BytesContent(data);
    public static FileContent FromText(string text) => new BytesContent(System.Text.Encoding.UTF8.GetBytes(text));
    public static FileContent Pattern(long length, int seed) => new PatternContent(length, seed);

    /// <summary>Sıkıştırılabilir, tekrarlanabilir Türkçe metin (UTF-8).</summary>
    public static FileContent Text(int length, int seed)
    {
        string[] words = ["APFS", "Windows", "şeker", "ğüİıöç", "dosya", "klasör", "disk", "veri", "güvenli", "okuma", "Mac", "bağlantı"];
        var rnd = new Random(seed);
        var bytes = new List<byte>(length + 32);
        while (bytes.Count < length)
        {
            bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(words[rnd.Next(words.Length)]));
            bytes.Add(rnd.Next(7) == 0 ? (byte)'\n' : (byte)' ');
        }
        return new BytesContent(bytes.GetRange(0, length).ToArray());
    }
}

public sealed class BytesContent(byte[] data) : FileContent
{
    public override long Length => data.Length;
    public override void Read(long offset, Span<byte> buffer) => data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
}

/// <summary>Konuma bağlı, tekrarlanabilir sözde rastgele içerik (sıkıştırılamaz, kaydırma hatalarını yakalar).</summary>
public sealed class PatternContent(long length, int seed) : FileContent
{
    public override long Length => length;

    public override void Read(long offset, Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = At(offset + i);
    }

    private byte At(long pos)
    {
        ulong x = (ulong)pos * 0x9E3779B97F4A7C15UL ^ (ulong)seed * 0xBF58476D1CE4E5B9UL;
        x ^= x >> 31;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 29;
        return (byte)x;
    }
}

/// <summary>
/// Büyük dosya içeriği: her MiB sınırında konumu kodlayan 64 baytlık bir işaret, gerisi sıfır.
/// APFS'te seyrek DEĞİLDİR (tüm bloklar gerçek extent'lerle yazılır); yalnızca görüntü dosyası NTFS'te az yer kaplar.
/// İşaretler konuma bağlı olduğu için yanlış ofsetten okuma hemen yakalanır.
/// </summary>
public sealed class MarkerContent(long length, int seed) : FileContent
{
    private const int Interval = 1 << 20;
    private const int MarkerLength = 64;

    public override long Length => length;

    public override void Read(long offset, Span<byte> buffer)
    {
        buffer.Clear();
        long firstMarker = offset / Interval * Interval;
        for (long m = firstMarker; m < offset + buffer.Length && m < length; m += Interval)
        {
            for (int i = 0; i < MarkerLength; i++)
            {
                long pos = m + i;
                if (pos < offset || pos >= offset + buffer.Length || pos >= length) continue;
                buffer[(int)(pos - offset)] = MarkerByte(m, i);
            }
        }
    }

    private byte MarkerByte(long markerPos, int i)
    {
        ulong x = (ulong)markerPos * 0x9E3779B97F4A7C15UL + (ulong)i * 0xD6E8FEB86659FD93UL + (ulong)seed;
        x ^= x >> 29;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 32;
        return (byte)(x | 1); // asla sıfır değil: işaret her zaman görünür
    }
}

/// <summary>Seyrek içerik: yalnızca verilen bölümlerde veri vardır, gerisi sıfırdır.</summary>
public sealed class SparseContent(long length, params (long Offset, byte[] Data)[] segments) : FileContent
{
    public override long Length => length;
    public override IEnumerable<(long Offset, long Length)> DataRanges =>
        segments.OrderBy(s => s.Offset).Select(s => (s.Offset, (long)s.Data.Length));

    public override void Read(long offset, Span<byte> buffer)
    {
        buffer.Clear();
        foreach (var (segOff, data) in segments)
        {
            long start = Math.Max(offset, segOff);
            long end = Math.Min(offset + buffer.Length, segOff + data.Length);
            if (start >= end) continue;
            data.AsSpan((int)(start - segOff), (int)(end - start)).CopyTo(buffer[(int)(start - offset)..]);
        }
    }
}
