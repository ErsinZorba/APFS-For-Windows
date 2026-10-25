using Apfs.IO;
using Apfs.Util;

namespace Apfs.Container;

/// <summary>
/// Blok ve nesne okuyucu. Her metadata nesnesinde sağlama toplamı, tür ve sınır kontrolü yapar.
/// Doğrulanmış nesneler sınırlı bir önbellekte tutulur (nesneler değişmez: disk salt okunur açılır).
/// </summary>
public sealed class ObjectReader
{
    private readonly IBlockDevice _device;
    private readonly LruCache<ulong, byte[]> _cache;

    public ObjectReader(IBlockDevice device, uint blockSize, int cacheBlocks = 4096)
    {
        if (blockSize < ApfsConstants.MinBlockSize || blockSize > ApfsConstants.MaxBlockSize || !uint.IsPow2(blockSize))
            throw ApfsException.Corrupt(CoreText.T("Object.InvalidBlockSize", blockSize));
        _device = device;
        BlockSize = blockSize;
        BlockCount = (ulong)(device.Length / blockSize);
        _cache = new LruCache<ulong, byte[]>(cacheBlocks);
    }

    public IBlockDevice Device => _device;
    public uint BlockSize { get; }
    public ulong BlockCount { get; }

    /// <summary>Ham blok okur (sağlama kontrolü yok). Dosya verisi ve tarama için.</summary>
    public byte[] ReadBlock(ulong paddr)
    {
        if (paddr >= BlockCount)
            throw ApfsException.Corrupt(CoreText.T("Object.BlockOutsideDisk", paddr));
        var buf = new byte[BlockSize];
        _device.Read((long)(paddr * BlockSize), buf);
        return buf;
    }

    /// <summary>Ardışık blokları okur (dosya verisi için).</summary>
    public void ReadBlocks(ulong paddr, long byteOffsetInBlock, Span<byte> buffer)
    {
        if (paddr >= BlockCount)
            throw ApfsException.Corrupt(CoreText.T("Object.BlockOutsideDisk", paddr));
        long start = (long)(paddr * BlockSize) + byteOffsetInBlock;
        _device.Read(start, buffer);
    }

    /// <summary>
    /// Bir nesneyi okur ve doğrular. <paramref name="expectedType"/> 0 değilse nesne türü eşleşmelidir.
    /// </summary>
    public byte[] ReadObject(ulong paddr, uint expectedType, ulong expectedOid = 0, Crypto.VolumeCrypto? decrypt = null)
    {
        if (_cache.TryGet(paddr, out var cached))
        {
            Validate(cached, paddr, expectedType, expectedOid, checksum: false);
            return cached;
        }
        var block = ReadBlock(paddr);
        // Şifreli düğüm: sağlama toplamı düz metin üzerinden hesaplandığı için ÖNCE çözülür, SONRA doğrulanır.
        decrypt?.DecryptBlock(block, paddr);
        Validate(block, paddr, expectedType, expectedOid, checksum: true);
        _cache.Add(paddr, block);
        return block;
    }

    /// <summary>Önbelleğe alınmamış, doğrulanmış okuma (checkpoint taraması gibi tek seferlik işler için).</summary>
    public bool TryReadValidObject(ulong paddr, uint expectedType, out byte[] block)
    {
        block = ReadBlock(paddr);
        if (!Fletcher64.Verify(block)) return false;
        return expectedType == 0 || (Bin.U32(block, 24) & ApfsConstants.ObjectTypeMask) == expectedType;
    }

    private static void Validate(byte[] block, ulong paddr, uint expectedType, ulong expectedOid, bool checksum)
    {
        if (checksum && !Fletcher64.Verify(block))
            throw new ApfsException(ApfsErrorCode.ChecksumMismatch, CoreText.T("Object.Block", paddr));
        uint type = Bin.U32(block, 24) & ApfsConstants.ObjectTypeMask;
        if (expectedType != 0 && type != expectedType)
            throw ApfsException.Corrupt(CoreText.T("Object.TypeMismatch", paddr, expectedType, type));
        if (expectedOid != 0 && Bin.U64(block, 8) != expectedOid)
            throw ApfsException.Corrupt(CoreText.T("Object.OidMismatch", paddr));
    }
}
