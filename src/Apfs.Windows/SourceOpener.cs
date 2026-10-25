using System.Text.RegularExpressions;
using Apfs.Container;
using Apfs.IO;
using Apfs.Partitions;

namespace Apfs.Windows;

/// <summary>Kullanıcının verdiği kaynağı (görüntü dosyası veya fiziksel disk) açıp APFS kapsayıcısına ulaştırır.</summary>
public static partial class SourceOpener
{
    [GeneratedRegex(@"^(?:\\\\\.\\)?PhysicalDrive(\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex PhysicalDriveRegex();

    /// <summary>"\\.\PhysicalDrive2" veya "PhysicalDrive2" ise disk numarasını döndürür.</summary>
    public static int? ParsePhysicalDrive(string source)
    {
        var m = PhysicalDriveRegex().Match(source.Trim());
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    public static IBlockDevice OpenDevice(string source) =>
        ParsePhysicalDrive(source) is int n ? new RawDiskDevice(n) : new FileImageDevice(source);

    /// <summary>Kaynaktaki <paramref name="partitionIndex"/>. APFS kapsayıcısını açar (varsayılan: ilki).</summary>
    public static ApfsContainer OpenContainer(string source, int partitionIndex = 0)
    {
        var device = OpenDevice(source);
        try
        {
            return OpenContainer(device, partitionIndex, ownsDevice: true);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>Açık bir aygıttaki <paramref name="partitionIndex"/>. APFS kapsayıcısı. ownsDevice=false ise aygıtı kapsayıcı kapatmaz.</summary>
    public static ApfsContainer OpenContainer(IBlockDevice device, int partitionIndex, bool ownsDevice)
    {
        var parts = PartitionLocator.FindApfsPartitions(device);
        if (parts.Count == 0)
            throw new ApfsException(ApfsErrorCode.NotApfs, WinText.T("Source.NoApfsPartition"));
        if (partitionIndex < 0 || partitionIndex >= parts.Count)
            throw new ApfsException(ApfsErrorCode.InvalidArgument, WinText.T("Source.PartitionIndex", partitionIndex, parts.Count));
        var p = parts[partitionIndex];
        var window = p.Offset == 0 && p.Length == device.Length ? device : new OffsetDevice(device, p.Offset, p.Length, ownsInner: ownsDevice);
        return ApfsContainer.Open(window, ownsDevice: ownsDevice || !ReferenceEquals(window, device));
    }
}
