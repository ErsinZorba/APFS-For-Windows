namespace Apfs.IO;

/// <summary>Yazılabilir bir aygıtın içindeki bölüm penceresi (GPT'deki APFS bölümü). Bölüm dışına yazma imkânsızdır.</summary>
public sealed class WritableOffsetDevice : IWritableBlockDevice
{
    private readonly IWritableBlockDevice _inner;
    private readonly long _start;
    private readonly bool _ownsInner;

    public WritableOffsetDevice(IWritableBlockDevice inner, long start, long length, WriteGrant grant, bool ownsInner = false)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (start < 0 || length < 0 || start > inner.Length - length)
            throw ApfsException.Corrupt(CoreText.T("Io.PartitionBeyondDisk"));
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
            throw ApfsException.Corrupt(CoreText.T("Io.ReadOutsidePartition", offset, buffer.Length));
        _inner.Read(_start + offset, buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset > Length - data.Length)
            throw new ApfsException(ApfsErrorCode.InvalidArgument, CoreText.T("Io.WriteOutsidePartition", offset, data.Length));
        _inner.Write(_start + offset, data);
    }

    public void Flush() => _inner.Flush();

    public void Dispose()
    {
        if (_ownsInner) _inner.Dispose();
    }
}
