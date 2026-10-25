using System.Buffers.Binary;
using System.IO.Compression;
using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Compression;

/// <summary>
/// macOS dosya sistemi sıkıştırması (decmpfs). Sıkıştırılmış bir dosyanın verisi "com.apple.decmpfs" özniteliğindedir:
/// 16 baytlık başlık ('cmpf', tür, açılmış boyut) ve küçük dosyalarda doğrudan sıkıştırılmış veri. Büyük dosyalarda veri
/// "com.apple.ResourceFork" özniteliğinde, her biri 64 KiB'a açılan bloklar halinde durur.
/// Türler: 1 ve 9 sıkıştırılmamış, 3/4 zlib, 7/8 LZVN, 10 sıkıştırılmamış (kaynak çatalı), 11/12 LZFSE.
/// Tek sayılı (ve 1) türler öznitelikte, çift sayılılar kaynak çatalında saklanır.
/// </summary>
public static class Decmpfs
{
    public const string AttrName = "com.apple.decmpfs";
    public const string ResourceForkName = "com.apple.ResourceFork";
    public const uint Magic = 0x636D7066; // 'cmpf' (little-endian saklanır)
    public const int HeaderSize = 16;
    public const int BlockSize = 65536;

    /// <summary>Bellekte tamamen açılan öznitelik içi verinin üst sınırı.</summary>
    private const long MaxInlineSize = 64L * 1024 * 1024;

    public readonly record struct Header(uint Type, ulong UncompressedSize);

    public static Header ParseHeader(ReadOnlySpan<byte> attr)
    {
        if (attr.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(attr) != Magic)
            throw new ApfsException(ApfsErrorCode.Corrupt, "sıkıştırma başlığı (decmpfs) geçersiz");
        return new Header(BinaryPrimitives.ReadUInt32LittleEndian(attr[4..]), BinaryPrimitives.ReadUInt64LittleEndian(attr[8..]));
    }

    public static string TypeName(uint type) => type switch
    {
        1 or 9 or 10 => "sıkıştırılmamış",
        3 or 4 => "zlib",
        7 or 8 => "LZVN",
        11 or 12 => "LZFSE",
        5 => "tekilleştirilmiş",
        13 or 14 => "LZBITMAP",
        _ => $"tür {type}",
    };

    internal static bool IsSupported(uint type) => type is 1 or 3 or 4 or 7 or 8 or 9 or 10 or 11 or 12;
    internal static bool IsInline(uint type) => type is 1 or 3 or 7 or 9 or 11;

    internal static ApfsException Unsupported(uint type) =>
        new(ApfsErrorCode.Unsupported, $"bu dosya desteklenmeyen bir sıkıştırma türü kullanıyor ({TypeName(type)})");

    /// <summary>Sıkıştırılmış dosyanın okuyucusunu açar.</summary>
    public static IDataReader Open(ApfsVolume volume, Inode inode)
    {
        var attr = volume.ReadXattr(inode.Id, AttrName)
            ?? throw new ApfsException(ApfsErrorCode.Corrupt, "sıkıştırılmış olarak işaretli dosyada sıkıştırma bilgisi yok");
        var h = ParseHeader(attr);
        if (!IsSupported(h.Type)) throw Unsupported(h.Type);
        if (IsInline(h.Type))
        {
            if (h.UncompressedSize > MaxInlineSize)
                throw new ApfsException(ApfsErrorCode.Unsupported, $"öznitelik içinde çok büyük sıkıştırılmış veri ({h.UncompressedSize} bayt)");
            var data = new byte[h.UncompressedSize];
            DecodeChunk(h.Type, attr.AsSpan(HeaderSize), data);
            return new MemoryDataReader(data);
        }
        var fork = volume.OpenXattrStream(inode.Id, ResourceForkName)
            ?? throw new ApfsException(ApfsErrorCode.Corrupt, "sıkıştırılmış dosyanın kaynak çatalı bulunamadı");
        return new ResourceForkReader(h, fork);
    }

    /// <summary>
    /// Bir parçayı (öznitelik içi veri veya kaynak çatalı bloğu) tam olarak <paramref name="output"/> uzunluğunda açar.
    /// Sıkıştırılmamış işaretleri: zlib için ilk baytın alt dört biti 0xF, LZVN için 0x06, kopyalama için 0xCC.
    /// </summary>
    internal static void DecodeChunk(uint type, ReadOnlySpan<byte> src, Span<byte> output)
    {
        if (output.IsEmpty) return;
        if (src.IsEmpty) throw new ApfsException(ApfsErrorCode.Corrupt, "sıkıştırılmış veri boş");
        switch (type)
        {
            case 1:
                CopyExact(src, output);
                return;
            case 9 or 10:
                CopyExact(src[0] == 0xCC && src.Length == output.Length + 1 ? src[1..] : src, output);
                return;
            case 3 or 4:
                if ((src[0] & 0x0F) == 0x0F) { CopyExact(src[1..], output); return; }
                Inflate(src, output);
                return;
            case 7 or 8:
                if (src[0] == 0x06) { CopyExact(src[1..], output); return; }
                if (Lzvn.Decode(src, output) != output.Length)
                    throw new ApfsException(ApfsErrorCode.Corrupt, "LZVN verisi beklenenden kısa");
                return;
            case 11 or 12:
                if (src.Length == output.Length + 1 && src[0] != (byte)'b') { CopyExact(src[1..], output); return; }
                if (Lzfse.Decode(src, output) != output.Length)
                    throw new ApfsException(ApfsErrorCode.Corrupt, "LZFSE verisi beklenenden kısa");
                return;
            default:
                throw Unsupported(type);
        }
    }

    private static void CopyExact(ReadOnlySpan<byte> src, Span<byte> output)
    {
        if (src.Length < output.Length) throw new ApfsException(ApfsErrorCode.Corrupt, "sıkıştırılmamış veri beklenenden kısa");
        src[..output.Length].CopyTo(output);
    }

    /// <summary>
    /// zlib akışını açar. Başlık doğrulanıp atlanır ve ham deflate çözülür; Adler-32 sağlaması zorunlu tutulmaz
    /// (bazı Apple dosyalarında bulunmadığı biliniyor).
    /// </summary>
    private static void Inflate(ReadOnlySpan<byte> src, Span<byte> output)
    {
        if (src.Length < 2 || (src[0] & 0x0F) != 8 || ((src[0] << 8) | src[1]) % 31 != 0)
            throw new ApfsException(ApfsErrorCode.Corrupt, "zlib başlığı geçersiz");
        using var ms = new MemoryStream(src[2..].ToArray(), writable: false);
        using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
        int done = 0;
        try
        {
            while (done < output.Length)
            {
                int n = deflate.Read(output[done..]);
                if (n == 0) break;
                done += n;
            }
        }
        catch (InvalidDataException e)
        {
            throw new ApfsException(ApfsErrorCode.Corrupt, $"zlib verisi bozuk: {e.Message}", e);
        }
        if (done != output.Length) throw new ApfsException(ApfsErrorCode.Corrupt, "zlib verisi beklenenden kısa");
    }

    private sealed class MemoryDataReader(byte[] data) : IDataReader
    {
        public ulong Length => (ulong)data.Length;

        public int Read(ulong offset, Span<byte> buffer)
        {
            if (offset >= (ulong)data.Length) return 0;
            int n = (int)Math.Min((ulong)buffer.Length, (ulong)data.Length - offset);
            data.AsSpan((int)offset, n).CopyTo(buffer);
            return n;
        }
    }

    /// <summary>
    /// Kaynak çatalındaki 64 KiB'lık blokları gerektikçe açar. Blok tablosu ilk açılışta okunur ve doğrulanır;
    /// açılan son birkaç blok önbellekte tutulur (sıralı okumada her blok bir kez açılır).
    /// </summary>
    private sealed class ResourceForkReader : IDataReader
    {
        private readonly uint _type;
        private readonly IDataReader _fork;
        private readonly (long Offset, int Size)[] _blocks;
        private readonly LruCache<int, byte[]> _cache = new(8);

        public ResourceForkReader(Header h, IDataReader fork)
        {
            _type = h.Type;
            _fork = fork;
            Length = h.UncompressedSize;
            long numBlocks = (long)((h.UncompressedSize + BlockSize - 1) / BlockSize);
            if (numBlocks > 1 << 26) throw new ApfsException(ApfsErrorCode.Corrupt, "sıkıştırılmış dosya boyutu geçersiz");
            _blocks = h.Type == 4 ? ReadZlibTable(fork, (int)numBlocks) : ReadOffsetTable(fork, (int)numBlocks);
        }

        public ulong Length { get; }

        /// <summary>zlib kaynak çatalı: klasik kaynak çatalı başlığı (big-endian), 0x100'de blok tablosu.</summary>
        private static (long, int)[] ReadZlibTable(IDataReader fork, int numBlocks)
        {
            var head = ReadFork(fork, 0, 0x108);
            uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(head);
            if (dataOffset != 0x100) throw new ApfsException(ApfsErrorCode.Corrupt, "zlib kaynak çatalı başlığı geçersiz");
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x104));
            if (count != numBlocks) throw new ApfsException(ApfsErrorCode.Corrupt, "zlib blok sayısı dosya boyutuyla uyuşmuyor");
            var table = ReadFork(fork, 0x108, checked(numBlocks * 8));
            var result = new (long, int)[numBlocks];
            for (int i = 0; i < numBlocks; i++)
            {
                uint off = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 8));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 8 + 4));
                result[i] = Validate(fork, 0x104L + off, size);
            }
            return result;
        }

        /// <summary>LZVN/LZFSE/kopya kaynak çatalı: (blok sayısı + 1) adet u32 ofset, son ofset çatalın sonu.</summary>
        private static (long, int)[] ReadOffsetTable(IDataReader fork, int numBlocks)
        {
            var table = ReadFork(fork, 0, checked((numBlocks + 1) * 4));
            var result = new (long, int)[numBlocks];
            uint prev = BinaryPrimitives.ReadUInt32LittleEndian(table);
            if (prev != (uint)(numBlocks + 1) * 4) throw new ApfsException(ApfsErrorCode.Corrupt, "sıkıştırılmış blok tablosu geçersiz");
            for (int i = 0; i < numBlocks; i++)
            {
                uint next = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan((i + 1) * 4));
                if (next < prev) throw new ApfsException(ApfsErrorCode.Corrupt, "sıkıştırılmış blok tablosu sırasız");
                result[i] = Validate(fork, prev, next - prev);
                prev = next;
            }
            return result;
        }

        private static (long, int) Validate(IDataReader fork, long offset, uint size)
        {
            if (size == 0 || size > BlockSize * 2 || offset < 0 || (ulong)offset + size > fork.Length)
                throw new ApfsException(ApfsErrorCode.Corrupt, "sıkıştırılmış blok kaynak çatalının dışında");
            return (offset, (int)size);
        }

        private static byte[] ReadFork(IDataReader fork, long offset, int length)
        {
            // Bozuk bir tablo boyutu yüzünden büyük bellek ayrılmasın (64 MiB ≈ 16 milyon blok ≈ 1 TB'lık dosya)
            if (length < 0 || length > 64 * 1024 * 1024 || (ulong)offset + (ulong)length > fork.Length)
                throw new ApfsException(ApfsErrorCode.Corrupt, "kaynak çatalı beklenenden kısa");
            var buf = new byte[length];
            if (fork.Read((ulong)offset, buf) != length) throw new ApfsException(ApfsErrorCode.Corrupt, "kaynak çatalı okunamadı");
            return buf;
        }

        private byte[] GetBlock(int index) => _cache.GetOrAdd(index, i =>
        {
            var (off, size) = _blocks[i];
            int outLen = (int)Math.Min(BlockSize, Length - (ulong)i * BlockSize);
            var output = new byte[outLen];
            DecodeChunk(_type, ReadFork(_fork, off, size), output);
            return output;
        });

        public int Read(ulong offset, Span<byte> buffer)
        {
            if (offset >= Length) return 0;
            int toRead = (int)Math.Min((ulong)buffer.Length, Length - offset);
            int done = 0;
            while (done < toRead)
            {
                ulong pos = offset + (ulong)done;
                int index = (int)(pos / BlockSize);
                int within = (int)(pos % BlockSize);
                var block = GetBlock(index);
                int n = Math.Min(block.Length - within, toRead - done);
                block.AsSpan(within, n).CopyTo(buffer[done..]);
                done += n;
            }
            return toRead;
        }
    }
}
