using System.Buffers.Binary;

namespace Apfs.Util;

/// <summary>APFS nesne sağlama toplamı (Apple File System Reference, "Checksum").</summary>
public static class Fletcher64
{
    private const ulong Mod = 0xFFFFFFFF;

    /// <summary>İlk 8 bayt (sağlama alanının kendisi) hariç bloğun sağlama toplamını hesaplar.</summary>
    public static ulong Compute(ReadOnlySpan<byte> block)
    {
        ulong sum1 = 0, sum2 = 0;
        var data = block[8..];
        int words = data.Length / 4;
        for (int i = 0; i < words; i++)
        {
            sum1 += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4, 4));
            sum2 += sum1;
            // Mod'u her adımda almak yerine periyodik al; denklik korunur, 4096 kelimede taşma olmaz.
            if ((i & 0xFFF) == 0xFFF) { sum1 %= Mod; sum2 %= Mod; }
        }
        sum1 %= Mod;
        sum2 %= Mod;
        ulong c1 = Mod - ((sum1 + sum2) % Mod);
        ulong c2 = Mod - ((sum1 + c1) % Mod);
        return (c2 << 32) | c1;
    }

    public static bool Verify(ReadOnlySpan<byte> block) =>
        block.Length >= 32 && BinaryPrimitives.ReadUInt64LittleEndian(block) == Compute(block);
}
