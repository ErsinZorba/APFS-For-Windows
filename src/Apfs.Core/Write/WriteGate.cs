using Apfs.Container;
using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Util;

namespace Apfs.Write;

/// <summary>Yazma kapısının kararı. İzin verilmediyse <see cref="Reasons"/> kullanıcıya gösterilecek sade Türkçe nedenlerdir.</summary>
public sealed record GateResult(bool Allowed, IReadOnlyList<string> Reasons, WriteGrant? Grant, CheckReport? Report);

/// <summary>
/// Yazma kapısı: bir kapsayıcıya yazmaya başlamadan önce yapılan denetim. Tek bir kural bile tutmazsa disk salt okunur kalır.
/// Kurallar:
/// <list type="bullet">
/// <item>kapsayıcıdaki BÜTÜN birimler anlık görüntüsüz, mühürsüz ve rolsüz/Kullanıcı rollü olmalı (alan yöneticisi ortak
/// olduğu için kardeş birimler de önemlidir);</item>
/// <item>şifreli birim (1.2.0) yalnızca: yazılım şifrelemesi tek anahtarla (fs bayrakları tam ONEKEY), kapsayıcıda NX_CRYPTO_SW,
/// kilidi açılmış anahtar verilmiş, şifreleme işlemi yok/bitmiş (er_state 0, ENC_ROLLED yok, omap'te şifreleme/anahtar değiştirme
/// bayrağı yok), dosya başına anahtar (crypto_state) yok ve tam denetim anahtarla temiz;</item>
/// <item>Fusion yok, checkpoint halkaları bitişik, blok boyutu 4096, tanınmayan özellik biti yok;</item>
/// <item>en yeni checkpoint kullanılıyor (daha eskisine düşülmemiş), reaper boşta;</item>
/// <item>tam denetleyici (<see cref="FullCheck"/>) 0 hata veriyor.</item>
/// </list>
/// </summary>
public static class WriteGate
{
    // Bilinen ve yazıcının desteklediği özellik bitleri
    private const ulong NxFeatureDefrag = 0x1;
    private const ulong NxIncompatAllowed = ApfsConstants.NxIncompatVersion2;
    private const ulong ApfsFeaturesAllowed = 0x2 /* HARDLINK_MAP_RECORDS */ | 0x4 /* DEFRAG */ | 0x8 /* STRICTATIME */;
    private const ulong ApfsIncompatAllowed = ApfsConstants.ApfsIncompatCaseInsensitive | ApfsConstants.ApfsIncompatNormalizationInsensitive;
    private const ulong ApfsFsFlagsAllowed = ApfsConstants.ApfsFsUnencrypted;
    private const uint OmapCryptoBusy = ApfsConstants.OmapEncrypting | ApfsConstants.OmapDecrypting | ApfsConstants.OmapKeyrolling | ApfsConstants.OmapCryptoGeneration;

    /// <param name="keys">Kilidi açılmış birim anahtarları; şifreli birim ancak kendi anahtarıyla yazmaya açılır.</param>
    public static GateResult Evaluate(ApfsContainer c, IEnumerable<Crypto.VolumeKey>? keys = null)
    {
        var keyList = keys?.ToList() ?? [];
        var reasons = new List<string>();
        var sb = c.Superblock;

        if ((sb.IncompatibleFeatures & ApfsConstants.NxIncompatFusion) != 0) reasons.Add("Fusion Drive kapsayıcısına yazma desteklenmiyor.");
        if ((sb.IncompatibleFeatures & ~NxIncompatAllowed & ~ApfsConstants.NxIncompatFusion) != 0
            || (sb.Features & ~NxFeatureDefrag) != 0 || sb.ReadOnlyCompatibleFeatures != 0)
            reasons.Add("Disk, bu sürümün tanımadığı bir APFS özelliği kullanıyor.");
        if (sb.BlockSize != 4096) reasons.Add($"Blok boyutu {sb.BlockSize}; yalnızca 4096 baytlık bloklar destekleniyor.");
        if ((sb.XpDescBlocks & ApfsConstants.XpDescBlocksNonContiguous) != 0 || (sb.XpDataBlocks & ApfsConstants.XpDescBlocksNonContiguous) != 0)
            reasons.Add("Diskin kontrol noktası alanı parçalı; bu düzen desteklenmiyor.");
        if (c.Warnings.Count > 0) reasons.Add("Disk açılırken sorun görüldü (en yeni kontrol noktası kullanılamıyor olabilir).");

        foreach (ulong oid in sb.FsOids)
        {
            byte[] v;
            try { v = c.Reader.ReadObject(c.Omap.Resolve(oid, c.Xid).Paddr, ApfsConstants.TypeFs, oid); }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { reasons.Add($"Bir birim okunamadı ({e.Detail})."); continue; }
            string name = Bin.Utf8Z(Bin.Slice(v, 704, 256));
            ulong fsFlags = Bin.U64(v, 264);
            ushort role = Bin.U16(v, 964);
            if ((fsFlags & 3) != ApfsConstants.ApfsFsUnencrypted)
            {
                if (fsFlags != ApfsConstants.ApfsFsOneKey || (sb.Flags & ApfsConstants.NxCryptoSw) == 0)
                    reasons.Add($"\"{name}\" birimi bu sürümün yazamadığı bir türde şifreli (donanım şifrelemesi ya da dosya başına anahtar).");
                else if (!keyList.Any(k => k.VolumeUuid == Bin.Uuid(v, 240)))
                    reasons.Add($"\"{name}\" birimi şifreli; yazmaya açmak için önce kilidini açın.");
                if ((Bin.U64(v, 56) & ApfsConstants.ApfsIncompatEncRolled) != 0)
                    reasons.Add($"\"{name}\" biriminin şifrelemesi sonradan değiştirilmiş (ENC_ROLLED); böyle bir diske yazılmaz.");
                try
                {
                    uint omFlags = Bin.U32(c.Reader.ReadObject(Bin.U64(v, 128), ApfsConstants.TypeOmap), 32);
                    if ((omFlags & OmapCryptoBusy) != 0)
                        reasons.Add($"\"{name}\" biriminde macOS'un başlattığı bir şifreleme işlemi henüz bitmemiş; böyle bir diske yazılmaz.");
                }
                catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { reasons.Add($"\"{name}\" biriminin nesne haritası okunamadı."); }
            }
            else if ((fsFlags & ~ApfsFsFlagsAllowed) != 0)
                reasons.Add($"\"{name}\" biriminde bu sürümün desteklemediği bir ayar var.");
            if (Bin.U64(v, 216) != 0) reasons.Add($"\"{name}\" biriminde anlık görüntü (ör. Time Machine) var; böyle disklere yazma desteklenmiyor.");
            if (role is not (ApfsConstants.RoleNone or ApfsConstants.RoleUser))
                reasons.Add($"\"{name}\" bir macOS sistem, yedek veya iç birimi; bu diske yazılmaz.");
            if (Bin.U64(v, 976) != 0)
                reasons.Add($"\"{name}\" biriminde macOS'un başlattığı bir şifreleme işlemi henüz bitmemiş; böyle bir diske yazılmaz.");
            ulong incompat = Bin.U64(v, 56);
            if ((incompat & ApfsConstants.ApfsIncompatSealedVolume) != 0) reasons.Add($"\"{name}\" mühürlü bir sistem birimi.");
            else if ((incompat & ~ApfsIncompatAllowed) != 0 || (Bin.U64(v, 40) & ~ApfsFeaturesAllowed) != 0 || Bin.U64(v, 48) != 0)
                reasons.Add($"\"{name}\" birimi bu sürümün tanımadığı bir APFS özelliği kullanıyor.");
        }

        if (reasons.Count > 0) return new GateResult(false, Distinct(reasons), null, null);

        // Yapısal denetim en sonda: pahalıdır ve yukarıdaki kurallar geçmeden anlamsızdır
        var report = FullCheck.Run(c, keys: keyList);
        if (!report.IsClean)
            return new GateResult(false, ["Diskin iç yapısında tutarsızlık bulundu; güvenlik için yazmaya açılmadı. Diski Mac'te Disk İzlencesi → İlk Yardım ile denetleyin."], null, report);
        if (report.WriteBlockers.Count > 0)
            return new GateResult(false, [.. report.WriteBlockers.Select(b => $"Diskte bu sürümün güncel tutamadığı bir özellik var ({b}); yazmaya açılmadı.")], null, report);
        if (report.OwnershipIncomplete)
            return new GateResult(false, ["Diskte bu sürümün çözümleyemediği bir yapı var; her bloğun sahibi doğrulanamadığı için yazmaya açılmadı."], null, report);
        return new GateResult(true, [], new WriteGrant(sb.Uuid, sb.Xid), report);
    }

    private static List<string> Distinct(List<string> l) => l.Distinct(StringComparer.Ordinal).ToList();
}
