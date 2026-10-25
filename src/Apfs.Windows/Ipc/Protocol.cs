using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Apfs.Windows.Ipc;

/// <summary>Bir birimin durumu (hizmetten tepsiye).</summary>
/// <param name="Index">Birimin kapsayıcı içindeki sırası.</param>
/// <param name="Writable">Birim yazılabilir bağlı (deneysel yazma açık).</param>
/// <param name="Container">Birimin APFS bölümünün disk içindeki sırası (yazma ve kilit açma bölüm başınadır).</param>
/// <param name="WriteBlockReason">Bu bölüm için son "yazmaya izin ver" denemesi reddedildiyse nedeni.</param>
public sealed record VolumeStatus(int Index, string Name, string State, string? MountPoint, string? Hint, string? Message, bool Writable = false,
    int Container = 0, string? WriteBlockReason = null)
{
    public const string Mounted = "bağlı";
    public const string NeedsPassword = "parola-gerekli";
    public const string Failed = "hata";
    public const string Hidden = "gizli";
}

/// <param name="Writable">Diskin en az bir bölümü yazmaya açık.</param>
/// <param name="WriteBlockReason">Son "yazmaya izin ver" denemesi reddedildiyse nedeni (sade Türkçe; çok bölümlüde ilk bölümünki,
/// bölüm başına neden <see cref="VolumeStatus.WriteBlockReason"/>'da).</param>
public sealed record DiskStatus(string Id, string Device, string Description, List<VolumeStatus> Volumes, bool Writable = false, string? WriteBlockReason = null);

/// <summary>Kullanıcıya gösterilecek bildirim. Parola veya dosya içeriği asla içermez.</summary>
public sealed record Notice(long Id, string Level, string Text, DateTime Time);

public sealed record StatusResponse(List<DiskStatus> Disks, List<Notice> Notices, long LastNoticeId);

/// <param name="Busy">Yalnızca "eject": diskte açık dosya/kopyalama var, hiçbir şey yapılmadı; kullanıcı onaylarsa Force ile tekrar.</param>
public sealed record UnlockResponse(bool Ok, string Message, bool Busy = false);

/// <summary>
/// Tepsiden hizmete istek. "status" (since: son görülen bildirim), "unlock" (disk, birim, parola), "rescan",
/// "eject" (disk; force: açık dosya olsa da çıkar), "set-writable" (disk, container; enable: yazmayı aç/kapat; force: açık dosya
/// olsa da). Container: APFS bölüm sırası ("unlock" ve "set-writable"; eski istemcide 0 = ilk bölüm).
/// Lang: tepsinin arayüz dili (ör. "de"); hizmet bildirimleri ve nedenleri bu dilde üretir. Yoksa hizmetin dili değişmez.
/// </summary>
public sealed record Request(string Op, long Since = 0, string? Disk = null, int Volume = 0, string? Password = null, bool Force = false, bool Enable = false,
    int Container = 0, string? Lang = null);

/// <summary>
/// Adlandırılmış kanal: her bağlantıda tek satır JSON istek, tek satır JSON yanıt. Erişim listesi yalnızca SYSTEM, Yöneticiler ve
/// etkileşimli (oturum açmış) kullanıcılara izin verir; ağdan bağlantı reddedilir. İstekler 64 KiB ile sınırlıdır.
/// </summary>
public static class PipeProtocol
{
    /// <summary>Kanal adı. Yalnızca geliştirme modunda (`--console --pipe ad`) değiştirilir; kurulu hizmet ile çakışmasın diye.</summary>
    public static string PipeName { get; set; } = "APFSforWindows";
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>İstemcinin arayüz dili; <see cref="SendAsync{T}"/> dili belirtilmemiş her isteğe ekler (tepsi açılışta ayarlar).</summary>
    public static string? ClientLanguage { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // Kanalı açan sürecin kendisi (hizmette SYSTEM): yeni kanal örnekleri açabilmesi için gerekli
        using (var self = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, MaxMessageBytes, MaxMessageBytes, security);
    }

    public static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var buf = new List<byte>(256);
        var one = new byte[1];
        while (buf.Count < MaxMessageBytes)
        {
            int n = await s.ReadAsync(one, ct);
            if (n == 0) return buf.Count == 0 ? null : Encoding.UTF8.GetString([.. buf]);
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString([.. buf]);
            buf.Add(one[0]);
        }
        throw new InvalidDataException(WinText.T("Ipc.MessageTooLong"));
    }

    public static async Task WriteLineAsync(Stream s, object value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json) + "\n");
        await s.WriteAsync(bytes, ct);
        await s.FlushAsync(ct);
    }

    /// <summary>İstemci tarafı: tek istek gönderir, yanıtı döndürür. Hizmet çalışmıyorsa TimeoutException/IOException.</summary>
    public static async Task<T?> SendAsync<T>(Request request, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await client.ConnectAsync(cts.Token);
        await WriteLineAsync(client, request.Lang is null && ClientLanguage is not null ? request with { Lang = ClientLanguage } : request, cts.Token);
        var line = await ReadLineAsync(client, cts.Token);
        return line is null ? default : JsonSerializer.Deserialize<T>(line, Json);
    }
}
