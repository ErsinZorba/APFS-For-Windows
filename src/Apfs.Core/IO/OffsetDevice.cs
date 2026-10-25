namespace Apfs.IO;

/// <summary>Bir aygıtın içindeki bölüm penceresi (örneğin GPT'deki APFS bölümü).</summary>
public sealed class OffsetDevice : IBlockDevice
{
    private readonly IBlockDevice _inner;
    private readonly long _start;
    private readonly bool _ownsInner;

    public OffsetDevice(IBlockDevice inner, long start, long length, bool ownsInner = false)
    {
        if (start < 0 || length < 0 || start > inner.Length - length)
            throw ApfsException.Corrupt("bölüm sınırları diskin dışına taşıyor");
        _inner = inner;
        _start = start;
        Length = length;
        _ownsInner = ownsInner;
    }

    public long Length { get; }
    public int SectorSize => _inner.SectorSize;

    public void Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > Length - buffer.Length)
            throw ApfsException.Corrupt($"bölüm sınırı dışında okuma: {offset}+{buffer.Length}");
        _inner.Read(_start + offset, buffer);
    }

    public void Dispose()
    {
        if (_ownsInner) _inner.Dispose();
    }
}
