using Apfs.Util;
using Apfs.Volume;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>Faz 1.5: diskteki her kayıt çözülüp yeniden kodlanınca bayt bayt aynı çıkmalı (Linux ve mkapfs görüntüleri).</summary>
public class RecordCodecTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    [Theory]
    [MemberData(nameof(StructureTests.AllFixtures), MemberType = typeof(StructureTests))]
    public void EveryRecordRoundTrips(string name)
    {
        using var c = TestImages.Open(fixtures.Get(name));
        var v = c.OpenVolumes()[0].Volume!;
        int inodes = 0, drecs = 0, extents = 0, others = 0;
        foreach (var e in v.AllRecords())
        {
            ulong id = JKey.ObjId(e.Key);
            switch (JKey.Type(e.Key))
            {
                case JType.Inode:
                    Assert.Equal(e.Value, InodeRecord.Parse(e.Value).Encode());
                    Assert.Equal(e.Key, Records.Key(id, JType.Inode));
                    inodes++;
                    break;
                case JType.DirRec:
                    var de = ApfsVolume.ParseDirRec(e.Key, e.Value)!;
                    bool hashed = KeyOrder.IsHashedDrec(e.Key);
                    Assert.Equal(e.Key, Records.DrecKey(id, de.Name, hashed, v.IsCaseInsensitive));
                    Assert.Equal(e.Value, Records.DrecVal(de.FileId, de.DateAddedNs, (DirEntryType)Bin.U16(e.Value, 16),
                        e.Value.Length > 18 ? e.Value[18..] : null));
                    drecs++;
                    break;
                case JType.FileExtent:
                    Assert.Equal(e.Key, Records.FileExtentKey(id, Bin.U64(e.Key, 8)));
                    Assert.Equal(e.Value, Records.FileExtentVal(Bin.U64(e.Value, 0), Bin.U64(e.Value, 8), Bin.U64(e.Value, 16)));
                    extents++;
                    break;
                case JType.DstreamId:
                    Assert.Equal(e.Value, Records.DstreamIdVal(Bin.U32(e.Value, 0)));
                    others++;
                    break;
                case JType.Xattr:
                    string xn = Bin.Utf8Z(Bin.Slice(e.Key, 10, Bin.U16(e.Key, 8)));
                    Assert.Equal(e.Key, Records.XattrKey(id, xn));
                    others++;
                    break;
            }
        }
        // extentref ağacı kayıtları
        var tree = new Apfs.BTree.BTree(c.Reader, v.ExtentRefTreeOid, Apfs.Container.ApfsConstants.TypeBlockRefTree, null);
        foreach (var e in tree.Scan(_ => 0))
        {
            ulong paddr = Bin.U64(e.Key, 0) & JKey.ObjIdMask;
            Assert.Equal(e.Key, Records.PhysExtKey(paddr));
            ulong lk = Bin.U64(e.Value, 0);
            Assert.Equal(e.Value, Records.PhysExtVal(lk & 0x0FFFFFFFFFFFFFFF, Bin.U64(e.Value, 8), Bin.I32(e.Value, 16), (byte)(lk >> 60)));
        }
        Assert.True(inodes >= 2 && drecs >= 2, $"{inodes} inode, {drecs} girdi");
        if (name.StartsWith("pop", StringComparison.Ordinal)) Assert.True(extents > 100 && others > 100);
    }
}
