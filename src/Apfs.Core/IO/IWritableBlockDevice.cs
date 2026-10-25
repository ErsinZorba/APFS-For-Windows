namespace Apfs.IO;

/// <summary>
/// Yazılabilir aygıt. <see cref="IBlockDevice"/> bilinçli olarak yazmasız kalır; yazma yalnızca bu ayrı arayüzden yapılır ve bu
/// arayüzü uygulayan her sınıf yalnızca bir <see cref="WriteGrant"/> ile kurulabilir (yazma kapısından geçmeden diske yazan aygıt
/// oluşturulamaz; testle denetlenir).
/// </summary>
public interface IWritableBlockDevice : IBlockDevice
{
    /// <summary><paramref name="offset"/> konumuna yazar. Eksik yazma hata sayılır.</summary>
    void Write(long offset, ReadOnlySpan<byte> data);

    /// <summary>
    /// Yazma bariyeri: o ana kadarki bütün yazmalar kalıcı ortama ulaşmadan dönmez (disk önbelleği dahil). Commit sırası buna dayanır:
    /// bariyerden önceki yazmalar, sonrakilerden önce kalıcı olur.
    /// </summary>
    void Flush();
}

/// <summary>
/// Yazma izni: yalnızca yazma kapısı (<see cref="Apfs.Write.WriteGate"/>) kapsayıcı denetimden geçtiğinde üretir. Hangi kapsayıcı
/// (UUID) ve hangi checkpoint (xid) için verildiğini taşır; yazma oturumu açılırken disk o andan beri değişmemiş olmalıdır.
/// </summary>
public sealed class WriteGrant
{
    internal WriteGrant(Guid containerUuid, ulong xid)
    {
        ContainerUuid = containerUuid;
        Xid = xid;
    }

    public Guid ContainerUuid { get; }
    public ulong Xid { get; }
}
