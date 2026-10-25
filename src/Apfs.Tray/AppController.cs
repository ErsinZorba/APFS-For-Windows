using System.Diagnostics;
using System.Text.Json;
using Apfs.Windows;
using Apfs.Windows.Ipc;

namespace Apfs.Tray;

/// <summary>
/// Tepsi, panel ve ana pencerenin ortak beyni: hizmetin durumunu 2 sn'de bir sorar, şifreli birimler için parola ister,
/// çıkarma/tarama isteklerini gönderir. Tüm iş hizmette yapılır; burası yalnızca arayüz mantığıdır.
/// </summary>
internal sealed class AppController : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly HashSet<string> _asked = [];     // parolası bir kez sorulmuş birimler (iptal edilirse tekrar sorulmaz)
    private readonly HashSet<string> _ejecting = [];  // çıkarılmakta olan diskler (aynı disk için ikinci istek gönderilmez)
    private readonly HashSet<string> _unlocking = []; // parola penceresi açık olan birimler (aynı birim için ikinci pencere açılmaz)
    private readonly HashSet<string> _switching = []; // yazma durumu değiştirilen bölümler ("disk#bölüm")
    private long _lastNotice = -1;
    private bool _busy;
    private string _signature = "";

    public AppController()
    {
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public StatusResponse? Status { get; private set; }
    public bool ServiceDown { get; private set; }
    public bool Rescanning { get; private set; }

    /// <summary>Görünen durum değişti (yalnızca gerçekten değişince; her sorguda değil).</summary>
    public event Action? Changed;

    /// <summary>Kullanıcıya balon olarak gösterilecek bildirim: başlık, metin, düzey.</summary>
    public event Action<string, ToolTipIcon>? Notify;

    public bool IsEjecting(string diskId) => _ejecting.Contains(diskId);
    /// <summary>Diskin herhangi bir bölümünün yazma durumu değişiyor mu.</summary>
    public bool IsSwitching(string diskId) => _switching.Any(k => k.StartsWith(diskId + "#", StringComparison.Ordinal));
    public bool IsSwitching(string diskId, int container) => _switching.Contains($"{diskId}#{container}");
    /// <summary>Diskin yazma durumu değişen bölümleri.</summary>
    public IEnumerable<int> SwitchingContainers(string diskId) =>
        _switching.Where(k => k.StartsWith(diskId + "#", StringComparison.Ordinal)).Select(k => int.Parse(k.AsSpan(diskId.Length + 1)));

    public IEnumerable<VolumeStatus> MountedVolumes =>
        Status?.Disks.SelectMany(d => d.Volumes).Where(v => v.State == VolumeStatus.Mounted) ?? [];

    public void Start()
    {
        _timer.Start();
        _ = PollAsync();
    }

    public async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var s = await PipeProtocol.SendAsync<StatusResponse>(new Request("status", Since: Math.Max(0, _lastNotice)), TimeSpan.FromSeconds(3));
            if (s is null) return;
            ServiceDown = false;
            Status = s;
            if (_lastNotice >= 0) ShowNotices(s.Notices); // ilk açılışta eski bildirimleri gösterme
            _lastNotice = s.LastNoticeId;
            RaiseIfChanged();
            await AskPasswordsAsync(s);
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            ServiceDown = true;
            RaiseIfChanged();
        }
        finally
        {
            _busy = false;
        }
    }

    private void RaiseIfChanged()
    {
        string sig = JsonSerializer.Serialize(new { ServiceDown, Rescanning, Disks = ServiceDown ? null : Status?.Disks, Ejecting = _ejecting.Order(), Switching = _switching.Order() });
        if (sig == _signature) return;
        _signature = sig;
        Changed?.Invoke();
    }

    private void ShowNotices(List<Notice> notices)
    {
        if (notices.Count == 0) return;
        var level = notices.Any(n => n.Level == "hata") ? ToolTipIcon.Error : notices.Any(n => n.Level == "uyarı") ? ToolTipIcon.Warning : ToolTipIcon.Info;
        string text = string.Join("\n", notices.TakeLast(3).Select(n => n.Text));
        Notify?.Invoke(text.Length > 250 ? text[..250] + "…" : text, level);
    }

    // ---------------- Parola ----------------

    private async Task AskPasswordsAsync(StatusResponse s)
    {
        // Artık parola beklemeyen birimleri unut: disk çıkarılıp yeniden takılınca parola yine kendiliğinden sorulsun.
        // Vazgeçilen pencere, birim parola beklemeye devam ettikçe yeniden açılmaz (anahtar biçimi tek yerden: AskKey).
        var waiting = s.Disks.SelectMany(d => d.Volumes.Where(v => v.State == VolumeStatus.NeedsPassword).Select(v => AskKey(d, v))).ToHashSet();
        _asked.RemoveWhere(k => !waiting.Contains(k));
        foreach (var d in s.Disks)
            foreach (var v in d.Volumes.Where(v => v.State == VolumeStatus.NeedsPassword))
            {
                if (!_asked.Add(AskKey(d, v))) continue;
                await UnlockAsync(d, v);
            }
    }

    /// <summary>Birimin parola penceresi anahtarı: disk + APFS bölüm sırası + birim sırası.</summary>
    internal static string AskKey(DiskStatus d, VolumeStatus v) => $"{d.Id}#{v.Container}#{v.Index}";

    public async Task UnlockAsync(DiskStatus d, VolumeStatus v)
    {
        string key = AskKey(d, v);
        _asked.Add(key);
        if (!_unlocking.Add(key)) return;
        try { await UnlockLoopAsync(d, v); }
        finally { _unlocking.Remove(key); }
    }

    private async Task UnlockLoopAsync(DiskStatus d, VolumeStatus v)
    {
        string? error = null;
        while (true)
        {
            using var form = new PasswordForm(v.Name, v.Hint, error);
            if (form.ShowDialog() != DialogResult.OK) return;
            UnlockResponse? r;
            try
            {
                r = await PipeProtocol.SendAsync<UnlockResponse>(new Request("unlock", Disk: d.Id, Volume: v.Index, Password: form.Password, Container: v.Container), TimeSpan.FromSeconds(60));
            }
            catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException)
            {
                error = "Hizmete ulaşılamadı.";
                continue;
            }
            if (r is { Ok: true })
            {
                Notify?.Invoke(r.Message, ToolTipIcon.Info);
                await PollAsync();
                return;
            }
            error = r?.Message ?? "Kilit açılamadı.";
        }
    }

    // ---------------- Çıkarma ----------------

    /// <summary>
    /// "Güvenle çıkar". Diskte açık dosya veya süren kopyalama varsa hizmet hiçbir şey yapmadan "meşgul" der; kullanıcıya sorulur,
    /// onaylarsa zorla çıkarılır. Başarıda hizmetin kendi bildirimi balon olarak gelir; burada yalnızca hata gösterilir.
    /// </summary>
    public async Task EjectAsync(DiskStatus d, IWin32Window? owner)
    {
        if (!_ejecting.Add(d.Id)) return;
        RaiseIfChanged();
        try
        {
            var r = await SendEjectAsync(d.Id, force: false);
            if (r is { Busy: true })
            {
                if (!ConfirmBusyEject(owner, r.Message)) return;
                r = await SendEjectAsync(d.Id, force: true);
            }
            if (r is not { Ok: true })
                MessageBox.Show(owner, r?.Message ?? "Disk çıkarılamadı.", "APFS for Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _ejecting.Remove(d.Id);
            await PollAsync();
            RaiseIfChanged();
        }
    }

    private static async Task<UnlockResponse?> SendEjectAsync(string disk, bool force)
    {
        try
        {
            return await PipeProtocol.SendAsync<UnlockResponse>(new Request("eject", Disk: disk, Force: force), TimeSpan.FromSeconds(60));
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException)
        {
            return new UnlockResponse(false, "Hizmete ulaşılamadı.");
        }
    }

    private static bool ConfirmBusyEject(IWin32Window? owner, string message)
    {
        var eject = new TaskDialogButton("Yine de çıkar");
        var cancel = new TaskDialogButton("Vazgeç");
        var page = new TaskDialogPage
        {
            Caption = "APFS for Windows",
            Heading = "Kopyalama sürüyor gibi görünüyor",
            Text = message,
            Icon = TaskDialogIcon.Warning,
            Buttons = { eject, cancel },
            DefaultButton = cancel,
            AllowCancel = true,
        };
        var result = owner is null ? TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen) : TaskDialog.ShowDialog(owner, page);
        return result == eject;
    }

    // ---------------- Yazma (deneysel) ----------------

    /// <summary>
    /// "Yazmaya izin ver" / "Salt okunura dön". İzin vermeden önce kullanıcıya deneysel olduğu ve yedek alması söylenir.
    /// Diskte açık dosya varsa hizmet "meşgul" der; kullanıcı onaylarsa zorla yapılır. Ret nedenini hizmet karta da yazar.
    /// </summary>
    /// <param name="container">APFS bölüm sırası (yazma bölüm başınadır).</param>
    /// <param name="label">Onay penceresinde gösterilecek ad (tek bölümlü diskte disk, çok bölümlüde bölümün birimi).</param>
    public async Task SetWritableAsync(DiskStatus d, int container, string label, bool enable, IWin32Window? owner)
    {
        if (enable && !ConfirmEnableWrite(owner, label)) return;
        string key = $"{d.Id}#{container}";
        if (!_switching.Add(key)) return;
        RaiseIfChanged();
        try
        {
            var r = await SendSetWritableAsync(d.Id, container, enable, force: false);
            if (r is { Busy: true })
            {
                if (!ConfirmBusy(owner, "Disk kullanımda", r.Message, "Yine de devam et")) return;
                r = await SendSetWritableAsync(d.Id, container, enable, force: true);
            }
            if (r is not { Ok: true })
                MessageBox.Show(owner, r?.Message ?? "İşlem yapılamadı.", enable ? "Yazmaya açılamadı" : "APFS for Windows",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _switching.Remove(key);
            await PollAsync();
            RaiseIfChanged();
        }
    }

    private static async Task<UnlockResponse?> SendSetWritableAsync(string disk, int container, bool enable, bool force)
    {
        try
        {
            // Kapı tam denetim yapar; büyük diskte birkaç saniye sürebilir
            return await PipeProtocol.SendAsync<UnlockResponse>(new Request("set-writable", Disk: disk, Enable: enable, Force: force, Container: container), TimeSpan.FromMinutes(5));
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException)
        {
            return new UnlockResponse(false, "Hizmete ulaşılamadı.");
        }
    }

    private static bool ConfirmEnableWrite(IWin32Window? owner, string disk)
    {
        var allow = new TaskDialogButton("Yazmaya izin ver");
        var cancel = new TaskDialogButton("Vazgeç");
        var page = new TaskDialogPage
        {
            Caption = "APFS for Windows",
            Heading = "Bu diske yazmaya izin verilsin mi?",
            Text = $"{disk}\n\n" +
                   "Yazma özelliği DENEYSELDİR. Önemli dosyalarınızın bir yedeği olsun.\n\n" +
                   "• Önce disk denetlenir; bir sorun görülürse yazmaya açılmaz.\n" +
                   "• Diski çıkarmadan ya da Mac'e takmadan önce mutlaka \"Güvenle çıkar\"ı kullanın.\n" +
                   "• Bu diskte silinen dosyalar Geri Dönüşüm Kutusu'na gitmez, kalıcı olarak silinir.\n" +
                   "• İstediğiniz an \"Salt okunura dön\" diyebilirsiniz; yaptığınız değişiklikler kaydedilir.",
            Icon = TaskDialogIcon.ShieldWarningYellowBar,
            Buttons = { allow, cancel },
            DefaultButton = cancel,
            AllowCancel = true,
        };
        var result = owner is null ? TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen) : TaskDialog.ShowDialog(owner, page);
        return result == allow;
    }

    private static bool ConfirmBusy(IWin32Window? owner, string heading, string message, string proceed)
    {
        var go = new TaskDialogButton(proceed);
        var cancel = new TaskDialogButton("Vazgeç");
        var page = new TaskDialogPage
        {
            Caption = "APFS for Windows",
            Heading = heading,
            Text = message,
            Icon = TaskDialogIcon.Warning,
            Buttons = { go, cancel },
            DefaultButton = cancel,
            AllowCancel = true,
        };
        var result = owner is null ? TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen) : TaskDialog.ShowDialog(owner, page);
        return result == go;
    }

    // ---------------- Diğer ----------------

    public async Task RescanAsync()
    {
        if (Rescanning) return;
        Rescanning = true;
        RaiseIfChanged();
        try { await PipeProtocol.SendAsync<UnlockResponse>(new Request("rescan"), TimeSpan.FromSeconds(30)); }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException) { }
        finally
        {
            Rescanning = false;
            await PollAsync();
            RaiseIfChanged();
        }
    }

    public static void OpenDrive(string mountPoint) =>
        Process.Start(new ProcessStartInfo("explorer.exe", mountPoint.EndsWith('\\') ? mountPoint : mountPoint + "\\") { UseShellExecute = true });

    public static void OpenLog()
    {
        string path = File.Exists(ServiceLog.PathName) ? ServiceLog.PathName : ServiceLog.FallbackPathName;
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        else MessageBox.Show("Henüz günlük yok.", "APFS for Windows");
    }

    public static void ShowDetails(IWin32Window? owner, VolumeStatus v) =>
        MessageBox.Show(owner, v.Message ?? "Bilinmeyen hata.", $"APFS for Windows: {v.Name}", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
