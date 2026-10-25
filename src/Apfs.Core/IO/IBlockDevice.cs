namespace Apfs.IO;

/// <summary>
/// Diskteki tüm okumaların geçtiği tek kapı. Bilinçli olarak YAZMA METODU YOKTUR ve eklenmemelidir:
/// bu program hiçbir koşulda diske yazmaz.
/// </summary>
public interface IBlockDevice : IDisposable
{
    /// <summary>Aygıtın bayt cinsinden uzunluğu.</summary>
    long Length { get; }

    /// <summary>Fiziksel sektör boyutu (512 veya 4096).</summary>
    int SectorSize { get; }

    /// <summary><paramref name="offset"/> konumundan <paramref name="buffer"/> uzunluğu kadar okur. Eksik okuma hata sayılır.</summary>
    void Read(long offset, Span<byte> buffer);
}
