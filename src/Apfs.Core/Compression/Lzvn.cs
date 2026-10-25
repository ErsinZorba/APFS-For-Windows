using System.Buffers.Binary;

namespace Apfs.Compression;

/// <summary>
/// LZVN çözücüsü. Biçim, Apple'ın lzfse projesindeki lzvn_decode_base.c (BSD-3) referans alınarak yazıldı
/// (bkz. THIRD_PARTY_NOTICES.md). Her işlem kodunda kaynak ve hedef sınırları kontrol edilir; bozuk veri
/// taşmaya değil ApfsException'a yol açar.
/// </summary>
public static class Lzvn
{
    private enum Op : byte { SmlD, MedD, LrgD, PreD, SmlL, LrgL, SmlM, LrgM, Nop, Eos, Udef }

    private static readonly Op[] Table = BuildTable();

    private static Op[] BuildTable()
    {
        var t = new Op[256];
        for (int op = 0; op < 256; op++)
        {
            int low3 = op & 7, l = op >> 6, mmm = (op >> 3) & 7;
            t[op] = op switch
            {
                >= 0xF1 => Op.SmlM,
                0xF0 => Op.LrgM,
                >= 0xE1 => Op.SmlL,
                0xE0 => Op.LrgL,
                >= 0xD0 => Op.Udef,               // 0xD0-0xDF
                >= 0xA0 and <= 0xBF => Op.MedD,
                >= 0x70 and <= 0x7F => Op.Udef,
                _ when low3 == 7 => Op.LrgD,
                _ when low3 == 6 && l == 0 => mmm switch { 0 => Op.Eos, 1 or 2 => Op.Nop, _ => Op.Udef },
                _ when low3 == 6 => Op.PreD,
                _ => Op.SmlD,
            };
        }
        return t;
    }

    /// <summary>
    /// <paramref name="src"/>'yi <paramref name="dst"/>'ye çözer. Akış sonu (eos) görülene veya hedef dolana kadar sürer.
    /// Yazılan bayt sayısını döndürür.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int s = 0, d = 0;
        int dPrev = 0;
        while (true)
        {
            if (d == dst.Length) return d; // hedef doldu (decmpfs blokları tam boyutla çözülür)
            if (s >= src.Length) throw Corrupt(CoreText.T("Lzvn.UnexpectedEnd"));
            byte op = src[s];
            int L = 0, M = 0, D = dPrev, len;
            switch (Table[op])
            {
                case Op.SmlD:
                    Need(src, s, 2);
                    L = op >> 6; M = ((op >> 3) & 7) + 3;
                    D = ((op & 7) << 8) | src[s + 1];
                    len = 2;
                    break;
                case Op.MedD:
                {
                    Need(src, s, 3);
                    ushort w = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(s + 1, 2));
                    L = (op >> 3) & 3;
                    M = (((op & 7) << 2) | (w & 3)) + 3;
                    D = w >> 2;
                    len = 3;
                    break;
                }
                case Op.LrgD:
                    Need(src, s, 3);
                    L = op >> 6; M = ((op >> 3) & 7) + 3;
                    D = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(s + 1, 2));
                    len = 3;
                    break;
                case Op.PreD:
                    L = op >> 6; M = ((op >> 3) & 7) + 3;
                    len = 1;
                    break;
                case Op.SmlL:
                    L = op & 0xF; len = 1;
                    break;
                case Op.LrgL:
                    Need(src, s, 2);
                    L = src[s + 1] + 16; len = 2;
                    break;
                case Op.SmlM:
                    M = op & 0xF; len = 1;
                    break;
                case Op.LrgM:
                    Need(src, s, 2);
                    M = src[s + 1] + 16; len = 2;
                    break;
                case Op.Nop:
                    s += 1;
                    continue;
                case Op.Eos:
                    return d;
                default:
                    throw Corrupt(CoreText.T("Lzvn.UndefinedOpcode", op));
            }
            s += len;

            // Değişmez (literal) baytlar
            if (L > 0)
            {
                if (s + L > src.Length) throw Corrupt(CoreText.T("Lzvn.LiteralsOutOfStream"));
                int n = Math.Min(L, dst.Length - d);
                src.Slice(s, n).CopyTo(dst[d..]);
                d += n; s += L;
                if (n < L) return d;
            }

            // Eşleşme (match): daha önce yazılan çıktının kopyası; kaynak ve hedef üst üste binebilir
            if (M > 0)
            {
                if (D <= 0 || D > d) throw Corrupt(CoreText.T("Lzvn.InvalidMatchDistance", D));
                int n = Math.Min(M, dst.Length - d);
                for (int i = 0; i < n; i++) dst[d + i] = dst[d + i - D];
                d += n;
                dPrev = D;
            }
        }
    }

    private static void Need(ReadOnlySpan<byte> src, int s, int n)
    {
        if (s + n > src.Length) throw Corrupt(CoreText.T("Lzvn.OpcodeOutOfStream"));
    }

    private static ApfsException Corrupt(string detail) => new(ApfsErrorCode.Corrupt, detail);
}
