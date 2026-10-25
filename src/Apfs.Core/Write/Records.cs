using System.Buffers.Binary;
using System.Text;
using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Write;

/// <summary>Genişletilmiş alan (x_field_t + verisi). Tanınmayan alanlar olduğu gibi korunur.</summary>
internal sealed record XField(byte Type, byte Flags, byte[] Data);

/// <summary>
/// Dosya sistemi kayıtlarının kodlayıcıları (j_*_key_t / j_*_val_t). Çözülüp yeniden kodlanan her kayıt bayt bayt aynı olmalıdır
/// (testle doğrulanır); yazıcı yalnızca değiştirdiği alanlara dokunur.
/// </summary>
internal static class Records
{
    // Inode genişletilmiş alan türleri ve bayrakları
    public const byte XtName = 4, XtDstream = 8, XtSparseBytes = 13;
    public const byte XfDoNotCopy = 0x02, XfChildrenInherit = 0x08, XfSystemField = 0x20;
    // Inode iç bayrakları
    public const ulong InodeNoRsrcFork = 0x8000, InodeIsSparse = 0x200;
    // Öznitelik bayrakları
    public const ushort XattrDataStream = 0x1, XattrDataEmbedded = 0x2, XattrFileSystemOwned = 0x4;

    public static byte[] Key(ulong id, JType type, int extra = 0)
    {
        var k = new byte[8 + extra];
        BinaryPrimitives.WriteUInt64LittleEndian(k, (id & JKey.ObjIdMask) | ((ulong)type << JKey.TypeShift));
        return k;
    }

    // ---- xf_blob ----

    public static List<XField> ParseXFields(ReadOnlySpan<byte> blob)
    {
        var list = new List<XField>();
        if (blob.Length == 0) return list;
        int n = Bin.U16(blob, 0);
        int used = Bin.U16(blob, 2);
        int data = 4 + n * 4;
        if (data + used > blob.Length) throw ApfsException.Corrupt(CoreText.T("Records.XFieldsOverflow"));
        for (int i = 0; i < n; i++)
        {
            int h = 4 + i * 4;
            int size = Bin.U16(blob, h + 2);
            list.Add(new XField(blob[h], blob[h + 1], Bin.Slice(blob, data, size).ToArray()));
            data += (size + 7) & ~7;
        }
        return list;
    }

    public static byte[] EncodeXFields(IReadOnlyList<XField> fields)
    {
        if (fields.Count == 0) return [];
        int used = fields.Sum(f => (f.Data.Length + 7) & ~7);
        var b = new byte[4 + fields.Count * 4 + used];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)fields.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)used);
        int data = 4 + fields.Count * 4;
        for (int i = 0; i < fields.Count; i++)
        {
            int h = 4 + i * 4;
            b[h] = fields[i].Type;
            b[h + 1] = fields[i].Flags;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(h + 2), (ushort)fields[i].Data.Length);
            fields[i].Data.CopyTo(b, data);
            data += (fields[i].Data.Length + 7) & ~7;
        }
        return b;
    }

    public static byte[] NameXField(string name) => Encoding.UTF8.GetBytes(name + "\0");

    /// <summary>j_dstream_t (40 bayt): boyut, ayrılan, varsayılan crypto_id, toplam yazılan, toplam okunan.</summary>
    public static byte[] Dstream(ulong size, ulong alloced, ulong cryptoId = 0, ulong written = 0, ulong read = 0)
    {
        var d = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(d, size);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(8), alloced);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(16), cryptoId);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(24), written);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(32), read);
        return d;
    }

    // ---- dizin girdisi ----

    /// <summary>j_drec_hashed_key_t (karmalı birimde) veya j_drec_key_t.</summary>
    public static byte[] DrecKey(ulong parent, string name, bool hashed, bool caseInsensitive)
    {
        var nb = Encoding.UTF8.GetBytes(name + "\0");
        if (nb.Length > 255 + 1 && hashed) throw new ApfsException(ApfsErrorCode.InvalidArgument, CoreText.T("Records.NameTooLong"));
        if (hashed)
        {
            var k = Key(parent, JType.DirRec, 4 + nb.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(k.AsSpan(8), (ApfsNames.Hash(name, caseInsensitive) << 10) | (uint)nb.Length);
            nb.CopyTo(k, 12);
            return k;
        }
        var u = Key(parent, JType.DirRec, 2 + nb.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(u.AsSpan(8), (ushort)nb.Length);
        nb.CopyTo(u, 10);
        return u;
    }

    /// <summary>j_drec_val_t: dosya kimliği, eklenme zamanı, bayraklar (DT_* türü) + isteğe bağlı genişletilmiş alanlar.</summary>
    public static byte[] DrecVal(ulong fileId, long dateAddedNs, DirEntryType type, byte[]? xfields = null)
    {
        var v = new byte[18 + (xfields?.Length ?? 0)];
        BinaryPrimitives.WriteUInt64LittleEndian(v, fileId);
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), dateAddedNs);
        BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(16), (ushort)type);
        xfields?.CopyTo(v, 18);
        return v;
    }

    // ---- veri akışı ----

    public static byte[] DstreamIdVal(uint refcnt)
    {
        var v = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(v, refcnt);
        return v;
    }

    public static byte[] FileExtentKey(ulong privateId, ulong logical)
    {
        var k = Key(privateId, JType.FileExtent, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(k.AsSpan(8), logical);
        return k;
    }

    /// <summary>j_file_extent_val_t: uzunluk (+bayraklar üst 8 bit), fiziksel blok (0 = delik), crypto_id.</summary>
    public static byte[] FileExtentVal(ulong lengthBytes, ulong physBlock, ulong cryptoId = 0, byte flags = 0)
    {
        var v = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(v, (lengthBytes & 0x00FFFFFFFFFFFFFF) | (ulong)flags << 56);
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(8), physBlock);
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(16), cryptoId);
        return v;
    }

    /// <summary>j_phys_ext_key_t (extentref ağacı): fiziksel adres, tür EXTENT.</summary>
    public static byte[] PhysExtKey(ulong paddr) => Key(paddr, JType.Extent);

    public const byte PeKindNew = 1;

    /// <summary>j_phys_ext_val_t: uzunluk (blok) + tür (üst 4 bit), sahip nesne kimliği, başvuru sayısı.</summary>
    public static byte[] PhysExtVal(ulong blocks, ulong ownerId, int refcnt, byte kind = PeKindNew)
    {
        var v = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(v, (blocks & 0x0FFFFFFFFFFFFFFF) | ((ulong)kind << 60));
        BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(8), ownerId);
        BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(16), refcnt);
        return v;
    }

    // ---- öznitelik ----

    public static byte[] XattrKey(ulong id, string name)
    {
        var nb = Encoding.UTF8.GetBytes(name + "\0");
        var k = Key(id, JType.Xattr, 2 + nb.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(k.AsSpan(8), (ushort)nb.Length);
        nb.CopyTo(k, 10);
        return k;
    }
}

/// <summary>
/// j_inode_val_t'nin değiştirilebilir hâli. Tanınan sabit alanlar ve genişletilmiş alan listesi; <see cref="Encode"/> çözülen kaydı
/// bayt bayt aynı üretir.
/// </summary>
internal sealed class InodeRecord
{
    public const int BaseSize = 92;

    public ulong ParentId, PrivateId;
    public long CreateNs, ModNs, ChangeNs, AccessNs;
    public ulong InternalFlags;
    public int ChildrenOrLinks;
    public uint DefaultProtectionClass, WriteGenerationCounter, BsdFlags, Owner, Group;
    public ushort Mode, Pad1;
    public ulong UncompressedSize;
    public List<XField> XFields = [];

    public static InodeRecord Parse(ReadOnlySpan<byte> v)
    {
        if (v.Length < BaseSize) throw ApfsException.Corrupt(CoreText.T("Records.InodeTooShort"));
        return new InodeRecord
        {
            ParentId = Bin.U64(v, 0),
            PrivateId = Bin.U64(v, 8),
            CreateNs = (long)Bin.U64(v, 16),
            ModNs = (long)Bin.U64(v, 24),
            ChangeNs = (long)Bin.U64(v, 32),
            AccessNs = (long)Bin.U64(v, 40),
            InternalFlags = Bin.U64(v, 48),
            ChildrenOrLinks = Bin.I32(v, 56),
            DefaultProtectionClass = Bin.U32(v, 60),
            WriteGenerationCounter = Bin.U32(v, 64),
            BsdFlags = Bin.U32(v, 68),
            Owner = Bin.U32(v, 72),
            Group = Bin.U32(v, 76),
            Mode = Bin.U16(v, 80),
            Pad1 = Bin.U16(v, 82),
            UncompressedSize = Bin.U64(v, 84),
            XFields = Records.ParseXFields(v[BaseSize..]),
        };
    }

    public byte[] Encode()
    {
        var x = Records.EncodeXFields(XFields);
        var v = new byte[BaseSize + x.Length];
        var s = v.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(s, ParentId);
        BinaryPrimitives.WriteUInt64LittleEndian(s[8..], PrivateId);
        BinaryPrimitives.WriteInt64LittleEndian(s[16..], CreateNs);
        BinaryPrimitives.WriteInt64LittleEndian(s[24..], ModNs);
        BinaryPrimitives.WriteInt64LittleEndian(s[32..], ChangeNs);
        BinaryPrimitives.WriteInt64LittleEndian(s[40..], AccessNs);
        BinaryPrimitives.WriteUInt64LittleEndian(s[48..], InternalFlags);
        BinaryPrimitives.WriteInt32LittleEndian(s[56..], ChildrenOrLinks);
        BinaryPrimitives.WriteUInt32LittleEndian(s[60..], DefaultProtectionClass);
        BinaryPrimitives.WriteUInt32LittleEndian(s[64..], WriteGenerationCounter);
        BinaryPrimitives.WriteUInt32LittleEndian(s[68..], BsdFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(s[72..], Owner);
        BinaryPrimitives.WriteUInt32LittleEndian(s[76..], Group);
        BinaryPrimitives.WriteUInt16LittleEndian(s[80..], Mode);
        BinaryPrimitives.WriteUInt16LittleEndian(s[82..], Pad1);
        BinaryPrimitives.WriteUInt64LittleEndian(s[84..], UncompressedSize);
        x.CopyTo(v, BaseSize);
        return v;
    }

    public XField? Find(byte type) => XFields.FirstOrDefault(f => f.Type == type);

    public void Set(byte type, byte flags, byte[] data)
    {
        int i = XFields.FindIndex(f => f.Type == type);
        var f = new XField(type, flags, data);
        if (i >= 0) XFields[i] = f; else XFields.Add(f);
        XFields.Sort((a, b) => a.Type.CompareTo(b.Type));
    }

    public void Remove(byte type) => XFields.RemoveAll(f => f.Type == type);

    public bool IsDirectory => (Mode & Inode.ModeTypeMask) == Inode.ModeDir;

    /// <summary>Veri akışı alanı: (boyut, ayrılan); yoksa null.</summary>
    public (ulong Size, ulong Alloced)? Dstream =>
        Find(Records.XtDstream) is { } f && f.Data.Length >= 16 ? (Bin.U64(f.Data, 0), Bin.U64(f.Data, 8)) : null;
}
