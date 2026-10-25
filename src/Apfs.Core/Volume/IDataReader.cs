namespace Apfs.Volume;

/// <summary>Bir dosyanın (sıkıştırılmış veya düz) mantıksal içeriğine rastgele erişim. Uygulamalar iş parçacığı güvenlidir.</summary>
public interface IDataReader
{
    /// <summary>Mantıksal (açılmış) boyut.</summary>
    ulong Length { get; }

    /// <summary><paramref name="offset"/>'ten okur; dosya sonunda kısalır. Okunan bayt sayısını döndürür.</summary>
    int Read(ulong offset, Span<byte> buffer);
}
