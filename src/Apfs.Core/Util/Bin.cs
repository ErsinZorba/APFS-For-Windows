using System.Buffers.Binary;

namespace Apfs.Util;

/// <summary>Sınır kontrollü little-endian okuma yardımcıları. Sınır taşması bozuk veri demektir.</summary>
internal static class Bin
{
    public static ushort U16(ReadOnlySpan<byte> s, int off) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(s, off, 2));
    public static uint U32(ReadOnlySpan<byte> s, int off) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(s, off, 4));
    public static ulong U64(ReadOnlySpan<byte> s, int off) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(s, off, 8));
    public static int I32(ReadOnlySpan<byte> s, int off) => BinaryPrimitives.ReadInt32LittleEndian(Slice(s, off, 4));

    /// <summary>APFS uuid_t (RFC 4122 bayt sırası).</summary>
    public static Guid Uuid(ReadOnlySpan<byte> s, int off) => new(Slice(s, off, 16), bigEndian: true);

    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> s, int off, int len)
    {
        if (off < 0 || len < 0 || off > s.Length - len)
            throw ApfsException.Corrupt($"kayıt sınırı aşıldı ({off}+{len} > {s.Length})");
        return s.Slice(off, len);
    }

    /// <summary>NUL ile biten (veya bitmeyen) UTF-8 dizgesi.</summary>
    public static string Utf8Z(ReadOnlySpan<byte> s)
    {
        int nul = s.IndexOf((byte)0);
        if (nul >= 0) s = s[..nul];
        return System.Text.Encoding.UTF8.GetString(s);
    }
}
