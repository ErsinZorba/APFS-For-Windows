using System.Buffers.Binary;

namespace Apfs.ImageBuilder;

/// <summary>Bir blok içine little-endian alan yazma yardımcısı.</summary>
internal sealed class BlockWriter(byte[] buf)
{
    public byte[] Buffer { get; } = buf;

    public void U8(int off, byte v) => Buffer[off] = v;
    public void U16(int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Buffer.AsSpan(off), v);
    public void U32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Buffer.AsSpan(off), v);
    public void U64(int off, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Buffer.AsSpan(off), v);
    public void Bytes(int off, ReadOnlySpan<byte> v) => v.CopyTo(Buffer.AsSpan(off));
    public void Uuid(int off, Guid g) => g.TryWriteBytes(Buffer.AsSpan(off, 16), bigEndian: true, out _);

    /// <summary>obj_phys_t başlığı (sağlama toplamı sonra hesaplanır).</summary>
    public void ObjHeader(ulong oid, ulong xid, uint type, uint subtype)
    {
        U64(8, oid);
        U64(16, xid);
        U32(24, type);
        U32(28, subtype);
    }

    /// <summary>
    /// Fletcher-64. Okuyucudaki uygulamadan bağımsız olarak, spesifikasyondaki "her adımda mod al" biçimiyle yazıldı.
    /// </summary>
    public void SealChecksum()
    {
        const ulong mod = 0xFFFFFFFF;
        ulong s1 = 0, s2 = 0;
        for (int i = 8; i + 4 <= Buffer.Length; i += 4)
        {
            s1 = (s1 + BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(i))) % mod;
            s2 = (s2 + s1) % mod;
        }
        ulong c1 = mod - ((s1 + s2) % mod);
        ulong c2 = mod - ((s1 + c1) % mod);
        U64(0, (c2 << 32) | c1);
    }
}
