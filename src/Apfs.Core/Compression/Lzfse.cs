using System.Buffers.Binary;
using System.Numerics;

namespace Apfs.Compression;

/// <summary>
/// LZFSE çözücüsü (bvx-, bvxn, bvx1, bvx2 blokları ve bvx$ akış sonu). Biçim ve zorunlu sabit tablolar Apple'ın lzfse projesindeki
/// lzfse_internal.h, lzfse_fse.h/.c ve lzfse_decode_base.c (BSD-3) referans alınarak yazıldı (bkz. THIRD_PARTY_NOTICES.md).
/// Tüm okumalar sınır kontrollüdür; bozuk veri ApfsException verir.
/// </summary>
public static class Lzfse
{
    private const uint MagicEnd = 0x24787662;          // bvx$
    private const uint MagicRaw = 0x2d787662;          // bvx-
    private const uint MagicV1 = 0x31787662;           // bvx1
    private const uint MagicV2 = 0x32787662;           // bvx2
    private const uint MagicLzvn = 0x6e787662;         // bvxn

    private const int LSymbols = 20, MSymbols = 20, DSymbols = 64, LiteralSymbols = 256;
    private const int LStates = 64, MStates = 64, DStates = 256, LiteralStates = 1024;
    private const int MatchesPerBlock = 10000, LiteralsPerBlock = 4 * MatchesPerBlock;

    private static readonly byte[] LExtraBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 3, 5, 8];
    private static readonly int[] LBaseValue = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 20, 28, 60];
    private static readonly byte[] MExtraBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 5, 8, 11];
    private static readonly int[] MBaseValue = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 24, 56, 312];
    private static readonly byte[] DExtraBits =
    [
        0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7,
        8, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11, 12, 12, 12, 12, 13, 13, 13, 13, 14, 14, 14, 14, 15, 15, 15, 15,
    ];
    private static readonly int[] DBaseValue =
    [
        0, 1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 28, 36, 44, 52, 60, 76, 92, 108, 124, 156, 188, 220, 252, 316, 380, 444,
        508, 636, 764, 892, 1020, 1276, 1532, 1788, 2044, 2556, 3068, 3580, 4092, 5116, 6140, 7164, 8188, 10236, 12284,
        14332, 16380, 20476, 24572, 28668, 32764, 40956, 49148, 57340, 65532, 81916, 98300, 114684, 131068, 163836,
        196604, 229372,
    ];

    // Frekans tablolarının sabit (Huffman benzeri) kodlaması: alt 5 bit → bit sayısı / değer
    private static readonly sbyte[] FreqNBits = [2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14, 2, 3, 2, 5, 2, 3, 2, 8, 2, 3, 2, 5, 2, 3, 2, 14];
    private static readonly sbyte[] FreqValue = [0, 2, 1, 4, 0, 3, 1, -1, 0, 2, 1, 5, 0, 3, 1, -1, 0, 2, 1, 6, 0, 3, 1, -1, 0, 2, 1, 7, 0, 3, 1, -1];

    /// <summary>Tüm LZFSE akışını <paramref name="dst"/>'ye çözer; yazılan bayt sayısını döndürür.</summary>
    public static int Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int s = 0, d = 0;
        while (true)
        {
            uint magic = U32(src, s);
            switch (magic)
            {
                case MagicEnd:
                    return d;
                case MagicRaw:
                {
                    int n = (int)U32(src, s + 4);
                    s += 8;
                    Check(n >= 0 && s + n <= src.Length, "LZFSE ham blok akışın dışında");
                    Check(d + n <= dst.Length, "LZFSE çıktısı beklenen boyutu aşıyor");
                    src.Slice(s, n).CopyTo(dst[d..]);
                    s += n; d += n;
                    break;
                }
                case MagicLzvn:
                {
                    int raw = (int)U32(src, s + 4);
                    int payload = (int)U32(src, s + 8);
                    s += 12;
                    Check(raw >= 0 && payload >= 0 && s + payload <= src.Length, "LZFSE/LZVN bloğu akışın dışında");
                    Check(d + raw <= dst.Length, "LZFSE çıktısı beklenen boyutu aşıyor");
                    int n = Lzvn.Decode(src.Slice(s, payload), dst.Slice(d, raw));
                    Check(n == raw, "LZVN bloğu beklenenden kısa çıktı");
                    s += payload; d += raw;
                    break;
                }
                case MagicV1:
                case MagicV2:
                    d += DecodeCompressedBlock(src, ref s, dst, d, magic);
                    break;
                default:
                    throw Corrupt($"LZFSE blok imzası tanınmıyor (0x{magic:x8})");
            }
        }
    }

    private sealed class Header
    {
        public int RawBytes, Literals, Matches, LiteralPayload, LmdPayload, LiteralBits, LmdBits;
        public readonly int[] LiteralState = new int[4];
        public int LState, MState, DState;
        public readonly ushort[] LFreq = new ushort[LSymbols], MFreq = new ushort[MSymbols], DFreq = new ushort[DSymbols];
        public readonly ushort[] LiteralFreq = new ushort[LiteralSymbols];
    }

    private static int DecodeCompressedBlock(ReadOnlySpan<byte> src, ref int s, Span<byte> dst, int dstStart, uint magic)
    {
        var h = new Header();
        int headerSize;
        if (magic == MagicV2)
        {
            Check(s + 32 <= src.Length, "LZFSE v2 başlığı akışın dışında");
            h.RawBytes = (int)U32(src, s + 4);
            ulong v0 = U64(src, s + 8), v1 = U64(src, s + 16), v2 = U64(src, s + 24);
            h.Literals = (int)Field(v0, 0, 20);
            h.LiteralPayload = (int)Field(v0, 20, 20);
            h.Matches = (int)Field(v0, 40, 20);
            h.LiteralBits = (int)Field(v0, 60, 3) - 7;
            for (int i = 0; i < 4; i++) h.LiteralState[i] = (int)Field(v1, i * 10, 10);
            h.LmdPayload = (int)Field(v1, 40, 20);
            h.LmdBits = (int)Field(v1, 60, 3) - 7;
            headerSize = (int)Field(v2, 0, 32);
            h.LState = (int)Field(v2, 32, 10);
            h.MState = (int)Field(v2, 42, 10);
            h.DState = (int)Field(v2, 52, 10);
            Check(headerSize >= 32 && s + headerSize <= src.Length, "LZFSE v2 başlık boyutu geçersiz");
            DecodeFreqTables(src.Slice(s + 32, headerSize - 32), h);
        }
        else
        {
            // v1: sıkıştırılmamış başlık (sabit düzen; C yapısının sizeof'u, 4 bayt hizalamayla 770 → 772)
            headerSize = 772;
            Check(s + headerSize <= src.Length, "LZFSE v1 başlığı akışın dışında");
            var p = src.Slice(s, headerSize);
            h.RawBytes = (int)U32(p, 4);
            h.Literals = (int)U32(p, 12);
            h.Matches = (int)U32(p, 16);
            h.LiteralPayload = (int)U32(p, 20);
            h.LmdPayload = (int)U32(p, 24);
            h.LiteralBits = BinaryPrimitives.ReadInt32LittleEndian(p.Slice(28, 4));
            for (int i = 0; i < 4; i++) h.LiteralState[i] = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(32 + 2 * i, 2));
            h.LmdBits = BinaryPrimitives.ReadInt32LittleEndian(p.Slice(40, 4));
            h.LState = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(44, 2));
            h.MState = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(46, 2));
            h.DState = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(48, 2));
            int o = 50;
            foreach (var arr in new[] { h.LFreq, h.MFreq, h.DFreq, h.LiteralFreq })
                for (int i = 0; i < arr.Length; i++, o += 2) arr[i] = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(o, 2));
        }

        // Başlık doğrulaması (referanstaki lzfse_check_block_header_v1 ile aynı sınırlar)
        Check(h.Literals <= LiteralsPerBlock && h.Matches <= MatchesPerBlock && h.Literals % 4 == 0, "LZFSE blok sayaçları geçersiz");
        Check(h.LiteralState.All(x => x < LiteralStates) && h.LState < LStates && h.MState < MStates && h.DState < DStates,
            "LZFSE başlangıç durumları geçersiz");
        Check(h.LiteralBits is >= -7 and <= 0 && h.LmdBits is >= -7 and <= 0, "LZFSE bit sayaçları geçersiz");
        Check(h.LiteralPayload >= 0 && h.LmdPayload >= 0 && h.RawBytes >= 0, "LZFSE boyutları geçersiz");
        Check(s + (long)headerSize + h.LiteralPayload + h.LmdPayload <= src.Length, "LZFSE bloğu akışın dışında");
        Check(dstStart + (long)h.RawBytes <= dst.Length, "LZFSE çıktısı beklenen boyutu aşıyor");

        var literalTable = BuildDecoderTable(LiteralStates, h.LiteralFreq);
        var lTable = BuildValueTable(LStates, h.LFreq, LExtraBits, LBaseValue);
        var mTable = BuildValueTable(MStates, h.MFreq, MExtraBits, MBaseValue);
        var dTable = BuildValueTable(DStates, h.DFreq, DExtraBits, DBaseValue);

        s += headerSize;
        int streamStart = 0; // literal okuması, referanstaki gibi akışın başına kadar geri gidebilir

        // Değişmez baytlar: 4 iç içe FSE akışı, payload'ın SONUNDAN geriye okunur
        var literals = new byte[h.Literals + 64];
        {
            var bits = new BitReader(src, streamStart, s + h.LiteralPayload, h.LiteralBits);
            int st0 = h.LiteralState[0], st1 = h.LiteralState[1], st2 = h.LiteralState[2], st3 = h.LiteralState[3];
            for (int i = 0; i < h.Literals; i += 4)
            {
                bits.Flush();
                literals[i + 0] = DecodeSymbol(ref st0, literalTable, ref bits);
                literals[i + 1] = DecodeSymbol(ref st1, literalTable, ref bits);
                literals[i + 2] = DecodeSymbol(ref st2, literalTable, ref bits);
                literals[i + 3] = DecodeSymbol(ref st3, literalTable, ref bits);
            }
        }
        s += h.LiteralPayload;

        // L/M/D dizisi
        int lmdStart = s;
        var lmd = new BitReader(src, lmdStart, s + h.LmdPayload, h.LmdBits);
        int lState = h.LState, mState = h.MState, dState = h.DState;
        int litPos = 0, d = dstStart, D = -1;
        int end = dstStart + h.RawBytes;
        for (int sym = 0; sym < h.Matches; sym++)
        {
            lmd.Flush();
            int L = DecodeValue(ref lState, lTable, ref lmd);
            int M = DecodeValue(ref mState, mTable, ref lmd);
            int newD = DecodeValue(ref dState, dTable, ref lmd);
            D = newD != 0 ? newD : D;

            Check(litPos + L <= h.Literals, "LZFSE değişmez bayt sayısı aşıldı");
            Check(d + L + M <= end, "LZFSE çıktısı blok boyutunu aşıyor");
            // Mesafe, şimdiye kadarki toplam çıktıyı (değişmezler dahil) aşamaz
            Check(M == 0 || (D > 0 && D <= d + L), $"LZFSE eşleşme mesafesi geçersiz ({D})");
            literals.AsSpan(litPos, L).CopyTo(dst[d..]);
            d += L; litPos += L;
            for (int i = 0; i < M; i++) dst[d + i] = dst[d + i - D];
            d += M;
        }
        Check(d == end, "LZFSE bloğu beklenen boyutta değil");
        s += h.LmdPayload;
        return h.RawBytes;
    }

    private static void DecodeFreqTables(ReadOnlySpan<byte> p, Header h)
    {
        if (p.IsEmpty) return; // tablolar atlanmış (hepsi sıfır)
        uint accum = 0;
        int accumBits = 0, pos = 0;
        foreach (var arr in new[] { h.LFreq, h.MFreq, h.DFreq, h.LiteralFreq })
        {
            for (int i = 0; i < arr.Length; i++)
            {
                while (pos < p.Length && accumBits + 8 <= 32)
                {
                    accum |= (uint)p[pos++] << accumBits;
                    accumBits += 8;
                }
                uint b = accum & 31;
                int n = FreqNBits[b];
                int value = n switch
                {
                    8 => 8 + (int)((accum >> 4) & 0xF),
                    14 => 24 + (int)((accum >> 4) & 0x3FF),
                    _ => FreqValue[b],
                };
                Check(n <= accumBits, "LZFSE frekans tablosu kesik");
                arr[i] = (ushort)value;
                accum >>= n;
                accumBits -= n;
            }
        }
        Check(accumBits < 8 && pos == p.Length, "LZFSE frekans tablosu boyutu tutarsız");
    }

    // ---- FSE tabloları ----

    private readonly record struct Entry(int K, byte Symbol, int Delta);
    private readonly record struct ValueEntry(int TotalBits, int ValueBits, int Delta, int VBase);

    private static Entry[] BuildDecoderTable(int nStates, ushort[] freq)
    {
        var t = new Entry[nStates];
        int nClz = BitOperations.LeadingZeroCount((uint)nStates);
        int sum = 0, idx = 0;
        for (int i = 0; i < freq.Length; i++)
        {
            int f = freq[i];
            if (f == 0) continue;
            sum += f;
            Check(sum <= nStates, "LZFSE frekansları durum sayısını aşıyor");
            int k = BitOperations.LeadingZeroCount((uint)f) - nClz;
            int j0 = ((2 * nStates) >> k) - f;
            for (int j = 0; j < f; j++)
                t[idx++] = j < j0
                    ? new Entry(k, (byte)i, ((f + j) << k) - nStates)
                    : new Entry(k - 1, (byte)i, (j - j0) << (k - 1));
        }
        return t;
    }

    private static ValueEntry[] BuildValueTable(int nStates, ushort[] freq, byte[] vbits, int[] vbase)
    {
        var t = new ValueEntry[nStates];
        int nClz = BitOperations.LeadingZeroCount((uint)nStates);
        int sum = 0, idx = 0;
        for (int i = 0; i < freq.Length; i++)
        {
            int f = freq[i];
            if (f == 0) continue;
            sum += f;
            Check(sum <= nStates, "LZFSE frekansları durum sayısını aşıyor");
            int k = BitOperations.LeadingZeroCount((uint)f) - nClz;
            int j0 = ((2 * nStates) >> k) - f;
            for (int j = 0; j < f; j++)
                t[idx++] = j < j0
                    ? new ValueEntry(k + vbits[i], vbits[i], ((f + j) << k) - nStates, vbase[i])
                    : new ValueEntry(k - 1 + vbits[i], vbits[i], (j - j0) << (k - 1), vbase[i]);
        }
        return t;
    }

    private static byte DecodeSymbol(ref int state, Entry[] table, ref BitReader bits)
    {
        Check((uint)state < (uint)table.Length, "LZFSE durumu tablo dışında");
        var e = table[state];
        state = e.Delta + (int)bits.Pull(e.K);
        return e.Symbol;
    }

    private static int DecodeValue(ref int state, ValueEntry[] table, ref BitReader bits)
    {
        Check((uint)state < (uint)table.Length, "LZFSE durumu tablo dışında");
        var e = table[state];
        uint v = (uint)bits.Pull(e.TotalBits);
        state = e.Delta + (int)(v >> e.ValueBits);
        return e.VBase + (int)(v & ((1u << e.ValueBits) - 1));
    }

    /// <summary>Geriye doğru okuyan 64 bitlik bit akışı (referanstaki fse_in_stream64).</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _src;
        private readonly int _start;
        private int _pos;
        private ulong _accum;
        private int _nbits;

        public BitReader(ReadOnlySpan<byte> src, int start, int end, int n)
        {
            _src = src;
            _start = start;
            _pos = end;
            if (n != 0)
            {
                Check(_pos - 8 >= start, "LZFSE bit akışı başlatılamadı");
                _pos -= 8;
                _accum = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(_pos, 8));
                _nbits = n + 64;
            }
            else
            {
                Check(_pos - 7 >= start, "LZFSE bit akışı başlatılamadı");
                _pos -= 7;
                Span<byte> tmp = stackalloc byte[8];
                src.Slice(_pos, 7).CopyTo(tmp);
                tmp[7] = 0;
                _accum = BinaryPrimitives.ReadUInt64LittleEndian(tmp);
                _nbits = 56;
            }
            Check(_nbits is >= 56 and < 64 && (_accum >> _nbits) == 0, "LZFSE bit akışı başlangıcı geçersiz");
        }

        public void Flush()
        {
            int nbits = (63 - _nbits) & ~7;
            if (nbits == 0) return;
            int newPos = _pos - (nbits >> 3);
            Check(newPos >= _start, "LZFSE bit akışı başına taştı");
            _pos = newPos;
            ulong incoming = BinaryPrimitives.ReadUInt64LittleEndian(_src.Slice(_pos, 8));
            _accum = (_accum << nbits) | (incoming & ((1UL << nbits) - 1));
            _nbits += nbits;
        }

        public ulong Pull(int n)
        {
            Check(n >= 0 && n <= _nbits, "LZFSE bit akışı tükendi");
            _nbits -= n;
            ulong result = _accum >> _nbits;
            _accum = _nbits == 0 ? 0 : _accum & ((1UL << _nbits) - 1);
            return result;
        }
    }

    private static ulong Field(ulong v, int offset, int bits) =>
        bits == 32 ? (uint)(v >> offset) : (v >> offset) & ((1UL << bits) - 1);

    private static uint U32(ReadOnlySpan<byte> s, int off)
    {
        Check(off >= 0 && off + 4 <= s.Length, "LZFSE akışı beklenmeden bitti");
        return BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(off, 4));
    }

    private static ulong U64(ReadOnlySpan<byte> s, int off)
    {
        Check(off >= 0 && off + 8 <= s.Length, "LZFSE akışı beklenmeden bitti");
        return BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(off, 8));
    }

    private static void Check(bool ok, string detail)
    {
        if (!ok) throw Corrupt(detail);
    }

    private static ApfsException Corrupt(string detail) => new(ApfsErrorCode.Corrupt, detail);
}
