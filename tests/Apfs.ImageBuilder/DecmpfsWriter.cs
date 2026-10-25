using System.Buffers.Binary;
using System.IO.Compression;

namespace Apfs.ImageBuilder;

/// <summary>Test görüntüsündeki sıkıştırılmış bir dosyanın tanımı.</summary>
/// <param name="Type">decmpfs türü (3/4 zlib, 7/8 LZVN, 9/10 kopya, 11/12 LZFSE, diğerleri desteklenmeyen tür testi).</param>
/// <param name="PrecompressedInline">11 için önceden sıkıştırılmış öznitelik verisi.</param>
/// <param name="PrecompressedBlocks">12 için 64 KiB'lık bloklar halinde önceden sıkıştırılmış veri.</param>
public sealed record CompressionSpec(uint Type, byte[]? PrecompressedInline = null, IReadOnlyList<byte[]>? PrecompressedBlocks = null)
{
    public bool IsResourceFork => Type is 4 or 8 or 10 or 12;
}

/// <summary>
/// decmpfs özniteliği ve kaynak çatalı üretir (okuyucudan bağımsız). Kaynak çatalı düzeni, 7-Zip'in doğruladığı biçimdedir:
/// zlib için klasik kaynak çatalı (0x100 başlık + blok tablosu + 50 baytlık kaynak haritası), diğerleri için ofset tablosu.
/// </summary>
internal static class DecmpfsWriter
{
    public const int BlockSize = 65536;
    private const uint Magic = 0x636D7066;

    public static byte[] Header(uint type, long size, ReadOnlySpan<byte> inline)
    {
        var h = new byte[16 + inline.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(h, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), type);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(8), (ulong)size);
        inline.CopyTo(h.AsSpan(16));
        return h;
    }

    /// <summary>Öznitelik içi veri (tür 3, 7, 9, 11 ve desteklenmeyen türler).</summary>
    public static byte[] Inline(CompressionSpec spec, byte[] data) => spec.Type switch
    {
        3 => Smaller(Zlib(data), data, 0xFF),
        7 => Smaller(LzvnEncoder.Encode(data), data, 0x06),
        9 => [0xCC, .. data],
        11 => spec.PrecompressedInline ?? throw new InvalidOperationException("LZFSE için önceden sıkıştırılmış veri gerekli"),
        _ => [],
    };

    /// <summary>Kaynak çatalının tamamı (tür 4, 8, 10, 12).</summary>
    public static byte[] ResourceFork(CompressionSpec spec, byte[] data)
    {
        var blocks = new List<byte[]>();
        if (spec.Type == 12)
            blocks.AddRange(spec.PrecompressedBlocks ?? throw new InvalidOperationException("LZFSE için önceden sıkıştırılmış bloklar gerekli"));
        else
            for (int off = 0; off < data.Length; off += BlockSize)
            {
                var block = data.AsSpan(off, Math.Min(BlockSize, data.Length - off)).ToArray();
                blocks.Add(spec.Type switch
                {
                    4 => Smaller(Zlib(block), block, 0xFF),
                    8 => Smaller(LzvnEncoder.Encode(block), block, 0x06),
                    10 => block,
                    _ => throw new InvalidOperationException($"kaynak çatalı türü değil: {spec.Type}"),
                });
            }
        return spec.Type == 4 ? ZlibFork(blocks) : OffsetTableFork(blocks);
    }

    /// <summary>Sıkıştırma kazandırmıyorsa: işaret baytı + ham veri.</summary>
    private static byte[] Smaller(byte[] compressed, byte[] raw, byte marker) =>
        compressed.Length < raw.Length ? compressed : [marker, .. raw];

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static byte[] OffsetTableFork(List<byte[]> blocks)
    {
        int tableSize = (blocks.Count + 1) * 4;
        var fork = new byte[tableSize + blocks.Sum(b => b.Length)];
        int pos = tableSize;
        for (int i = 0; i < blocks.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fork.AsSpan(i * 4), (uint)pos);
            blocks[i].CopyTo(fork, pos);
            pos += blocks[i].Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(fork.AsSpan(blocks.Count * 4), (uint)pos);
        return fork;
    }

    private static byte[] ZlibFork(List<byte[]> blocks)
    {
        const int mapSize = 50;
        int n = blocks.Count;
        int dataSize = 4 + 4 + 8 * n + blocks.Sum(b => b.Length);
        int mapPos = 0x100 + dataSize;
        var fork = new byte[mapPos + mapSize];
        var s = fork.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 0x100);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)mapPos);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], (uint)dataSize);
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], mapSize);
        BinaryPrimitives.WriteUInt32BigEndian(s[0x100..], (uint)(dataSize - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x104..], (uint)n);
        int rel = 4 + 8 * n; // ofsetler 0x104'e göredir
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[(0x108 + i * 8)..], (uint)rel);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(0x108 + i * 8 + 4)..], (uint)blocks[i].Length);
            blocks[i].CopyTo(fork, 0x104 + rel);
            rel += blocks[i].Length;
        }
        // Kaynak haritası: tek tür ('cmpf'), tek kaynak (kimlik 1)
        var m = s[mapPos..];
        s[..16].CopyTo(m);
        BinaryPrimitives.WriteUInt16BigEndian(m[24..], 28);
        BinaryPrimitives.WriteUInt16BigEndian(m[26..], mapSize);
        BinaryPrimitives.WriteUInt16BigEndian(m[28..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(m[30..], Magic);
        BinaryPrimitives.WriteUInt16BigEndian(m[34..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(m[36..], 10);
        BinaryPrimitives.WriteUInt16BigEndian(m[38..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(m[40..], 0xFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(m[42..], 0);
        return fork;
    }
}

/// <summary>
/// Basit, açgözlü LZVN kodlayıcısı (yalnızca test görüntüleri için). Kullanılan işlem kodları: sml_l/lrg_l (değişmezler),
/// lrg_d (16 bit mesafeli eşleşme, 3-10 bayt) ve sml_m/lrg_m (aynı mesafeyle uzatma), sonunda eos.
/// Çıktısı bağımsız olarak 7-Zip ile doğrulanır.
/// </summary>
internal static class LzvnEncoder
{
    public static byte[] Encode(byte[] src)
    {
        var o = new List<byte>(src.Length / 2 + 16);
        var table = new int[1 << 14];
        Array.Fill(table, -1);
        int i = 0, lit = 0;
        while (i + 4 <= src.Length)
        {
            int h = (int)((BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(i)) * 2654435761u) >> 18);
            int cand = table[h];
            table[h] = i;
            int dist = i - cand;
            if (cand >= 0 && dist <= 0xFFFF && src.AsSpan(cand, 4).SequenceEqual(src.AsSpan(i, 4)))
            {
                int len = 4;
                while (i + len < src.Length && len < 3 + 7 + 271 * 4 && src[cand + len] == src[i + len]) len++;
                EmitLiterals(o, src, lit, i - lit);
                EmitMatch(o, dist, len);
                i += len;
                lit = i;
            }
            else i++;
        }
        EmitLiterals(o, src, lit, src.Length - lit);
        o.Add(0x06);                     // eos
        o.AddRange(new byte[7]);         // eos 8 bayt
        return [.. o];
    }

    private static void EmitLiterals(List<byte> o, byte[] src, int start, int n)
    {
        while (n > 0)
        {
            int k = Math.Min(n, 271);
            if (k >= 16) { o.Add(0xE0); o.Add((byte)(k - 16)); }
            else o.Add((byte)(0xE0 | k));
            o.AddRange(src.AsSpan(start, k).ToArray());
            start += k;
            n -= k;
        }
    }

    private static void EmitMatch(List<byte> o, int dist, int len)
    {
        int first = Math.Min(len, 10);
        o.Add((byte)(((first - 3) << 3) | 7)); // lrg_d, L = 0
        o.Add((byte)dist);
        o.Add((byte)(dist >> 8));
        int rest = len - first;
        while (rest > 0)
        {
            int k = Math.Min(rest, 271);
            if (k >= 16) { o.Add(0xF0); o.Add((byte)(k - 16)); }
            else o.Add((byte)(0xF0 | k));
            rest -= k;
        }
    }
}
