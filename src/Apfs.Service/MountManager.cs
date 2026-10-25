using Apfs.Container;
using Apfs.Crypto;
using Apfs.IO;
using Apfs.Mount;
using Apfs.Partitions;
using Apfs.Volume;
using Apfs.Windows;
using Apfs.Windows.Ipc;
using Apfs.Write;

namespace Apfs.Service;

/// <summary>
/// Takılı APFS disklerini bulur ve birimlerini salt okunur bağlar. Şifreli birimler için tepsiden parola bekler.
/// "Donanımı güvenli kaldır" isteğinde birimleri ayırıp disk tanıtıcısını kapatır; disk çekilirse temizlik yapar.
/// Tüm durum değişiklikleri tek kilit altında yapılır.
/// </summary>
internal sealed class MountManager : IDisposable
{
    private const int MaxPhysicalDrives = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, TrackedDisk> _disks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Notice> _notices = [];
    private readonly Dictionary<string, DateTime> _recentNotices = [];
    private readonly IReadOnlyList<string>? _imageSources;
    private readonly HashSet<string> _released = new(StringComparer.OrdinalIgnoreCase); // güvenli kaldırma sonrası yeniden bağlanmasın
    private long _noticeId;

    /// <param name="imageSources">Yalnızca geliştirme (konsol) modunda: fiziksel diskler yerine bu görüntü dosyaları kullanılır.</param>
    public MountManager(IReadOnlyList<string>? imageSources = null) => _imageSources = imageSources;

    /// <summary>
    /// Takılı bir disk. <see cref="Device"/> diskin tamamını salt okunur açar (bütün salt okunur kapsayıcılar ve kimlik denetimi
    /// bunu kullanır). Yazmaya açılan her APFS bölümünün kendi oturumu ve yazma tanıtıcısı vardır (<see cref="Writes"/>).
    /// </summary>
    private sealed class TrackedDisk(string key, IBlockDevice device, string description)
    {
        public string Key { get; } = key;
        public IBlockDevice Device { get; } = device;
        /// <summary>Takıldığındaki APFS kimliği (<see cref="PartitionLocator.Fingerprint"/>); değişirse disk başka bir programca değiştirilmiştir.</summary>
        public string Fingerprint { get; set; } = "";
        public string Description { get; } = description;
        /// <summary>APFS bölüm sırası → salt okunur açılmış kapsayıcı (yazmaya açık bölümde yok; onu oturum tutar).</summary>
        public Dictionary<int, ApfsContainer> Containers { get; } = [];
        public List<TrackedVolume> Volumes { get; } = [];
        public DeviceNotifications.Registration? RemovalWatch { get; set; }
        /// <summary>APFS bölüm sırası → yazma oturumu (o bölüm yazmaya açıksa).</summary>
        public Dictionary<int, ContainerWrite> Writes { get; } = [];
        /// <summary>APFS bölüm sırası → son "yazmaya izin ver" denemesinin ret nedeni.</summary>
        public Dictionary<int, string> WriteBlockReasons { get; } = [];
        /// <summary>Şu anda yazma durumu değişen bölümler (aynı bölüme ikinci istek reddedilir).</summary>
        public HashSet<int> Switching { get; } = [];
        /// <summary>
        /// Disk kimliğini değiştiren tek iş (oturumun Close'u blok 0'ı yazar) ile kimlik denetimi bu kilitle sıralanır: bir bölüm
        /// kapanırken araya giren tarama kimliği "değişmiş" görüp ÖBÜR bölümün oturumunu terk etmesin. Kimlik kilit içinde tazelenir.
        /// </summary>
        public object IdentityLock { get; } = new();
        /// <summary>
        /// Kilidi açılmış şifreli birimlerin anahtarları (birim UUID'si → VEK kopyası; <see cref="_gate"/> altında). Bölüm yeniden
        /// bağlanırken (yazmaya açma/kapama, ret) parola yeniden sorulmaz; disk bırakılınca sıfırlanır. Günlüğe yazılmaz.
        /// </summary>
        public Dictionary<Guid, VolumeKey> Keys { get; } = [];
    }

    /// <summary>Bir APFS bölümünün yazma oturumu ve onu kapatınca bırakılacak yazılabilir aygıtlar.</summary>
    private sealed class ContainerWrite(WriteSession session, IReadOnlyList<IDisposable> resources)
    {
        public WriteSession Session { get; } = session;
        public IReadOnlyList<IDisposable> Resources { get; } = resources;
        /// <summary>Yazma tanıtıcısının güvenli kaldırma kaydı (her açık tanıtıcı izlenir; hepsi aynı ReleaseDisk'e gider).</summary>
        public DeviceNotifications.Registration? RemovalWatch { get; set; }
    }

    private sealed class TrackedVolume(ApfsContainer container, int containerIndex, int index, ulong oid, string name)
    {
        public ApfsContainer Container { get; } = container;
        public int ContainerIndex { get; } = containerIndex;
        public int Index { get; } = index;
        public ulong Oid { get; } = oid;
        public string Name { get; set; } = name;
        public string State { get; set; } = VolumeStatus.Failed;
        public string? Hint { get; set; }
        public string? Message { get; set; }
        public ApfsMount? Mount { get; set; }
        public ApfsWritableMount? WritableMount { get; set; }
    }

    // ---------------- Tarama ----------------

    /// <summary>Yeni diskleri bağlar, kaybolanları temizler.</summary>
    public void Rescan()
    {
        foreach (var (key, open) in EnumerateSources())
        {
            lock (_gate)
            {
                if (_disks.ContainsKey(key) || _released.Contains(key)) continue;
            }
            IBlockDevice? device = null;
            try
            {
                device = open();
                if (device is null) continue;
                if (PartitionLocator.FindApfsPartitions(device).Count == 0)
                {
                    device.Dispose();
                    continue;
                }
                AttachDisk(key, device);
                device = null; // sahipliği TrackedDisk'e geçti
            }
            catch (ApfsException e) when (e.Code is ApfsErrorCode.NotFound or ApfsErrorCode.AccessDenied)
            {
                // disk yok veya erişilemiyor: sessizce geç
            }
            catch (Exception e)
            {
                AddNotice("uyarı", WinText.U("Notice.DiskUnreadable", key, e.Message), throttleKey: key);
            }
            finally
            {
                device?.Dispose();
            }
        }
        CheckForRemovedDisks();
    }

    private IEnumerable<(string Key, Func<IBlockDevice?> Open)> EnumerateSources()
    {
        if (_imageSources is not null)
        {
            foreach (var path in _imageSources)
                yield return (path, () => File.Exists(path) ? new FileImageDevice(path) : null);
            yield break;
        }
        for (int n = 0; n < MaxPhysicalDrives; n++)
        {
            int disk = n;
            yield return ($"PhysicalDrive{disk}", () => new RawDiskDevice(disk));
        }
    }

    private void AttachDisk(string key, IBlockDevice device)
    {
        var disk = new TrackedDisk(key, device, DescribeDisk(key, device)) { Fingerprint = PartitionLocator.Fingerprint(device) };
        int count = PartitionLocator.FindApfsPartitions(device).Count;
        for (int p = 0; p < count; p++) MountContainer(disk, p);

        if (device is RawDiskDevice raw)
            disk.RemovalWatch = DeviceNotifications.WatchHandle(raw.Handle, a => OnHandleEvent(key, a));
        lock (_gate) _disks[key] = disk;
    }

    /// <summary>
    /// <paramref name="p"/>. APFS bölümünü diskin salt okunur aygıtından (yeniden) açar ve birimlerini salt okunur bağlar.
    /// Disk yayımlanmış olabileceği için listeler <see cref="_gate"/> altında değiştirilir.
    /// </summary>
    private void MountContainer(TrackedDisk disk, int p)
    {
        try
        {
            var c = SourceOpener.OpenContainer(disk.Device, p, ownsDevice: false);
            var volumes = new List<TrackedVolume>();
            foreach (var r in c.OpenVolumes(keys: KeysOf(disk)))
            {
                ulong oid = c.Superblock.FsOids[r.Index];
                var tv = new TrackedVolume(c, p, r.Index, oid, r.Volume?.Name ?? WinText.U("Volume.DefaultName", r.Index));
                volumes.Add(tv);
                if (r.Volume is { } v) TryMount(disk, tv, v);
                else if (r.Error!.Code == ApfsErrorCode.Encrypted) MarkNeedsPassword(disk, tv);
                else
                {
                    tv.State = VolumeStatus.Failed;
                    tv.Message = r.Error.Message;
                    AddNotice("hata", WinText.U("Notice.VolumeOpenFailed", disk.Description, r.Index, r.Error.Message));
                }
            }
            bool published;
            lock (_gate)
            {
                // Disk henüz yayımlanmadıysa (AttachDisk) ya da hâlâ bizdeyse eklenir; bu arada bırakıldıysa geri alınır
                published = _disks.TryGetValue(disk.Key, out var current);
                if (!published || ReferenceEquals(current, disk))
                {
                    disk.Containers[p] = c;
                    disk.Volumes.AddRange(volumes);
                    return;
                }
            }
            foreach (var tv in volumes) { try { tv.Mount?.Dispose(); } catch { } }
            c.Dispose();
        }
        catch (ApfsException e)
        {
            AddNotice("hata", WinText.U("Notice.ContainerOpenFailed", disk.Description, e.Message));
        }
        catch (ObjectDisposedException) { } // disk bu arada bırakıldı (çıkarıldı)
    }

    /// <summary><paramref name="p"/>. bölümün salt okunur birimlerini ayırır ve kapsayıcısını bırakır (yazma durumu değişirken).</summary>
    private void UnmountContainer(TrackedDisk disk, int p)
    {
        List<TrackedVolume> volumes;
        ApfsContainer? c;
        lock (_gate)
        {
            volumes = disk.Volumes.Where(v => v.ContainerIndex == p).ToList();
            disk.Volumes.RemoveAll(v => v.ContainerIndex == p);
            disk.Containers.Remove(p, out c);
        }
        foreach (var tv in volumes)
        {
            try { tv.Mount?.Dispose(); } catch (Exception e) { ServiceLog.Write(WinText.T("Svc.UnmountError", disk.Key, e.Message)); }
        }
        c?.Dispose();
    }

    private List<VolumeKey> KeysOf(TrackedDisk disk)
    {
        lock (_gate) return [.. disk.Keys.Values];
    }

    private void MarkNeedsPassword(TrackedDisk disk, TrackedVolume tv)
    {
        var info = ProbeLockInfo(tv);
        tv.Name = info?.Name ?? tv.Name;
        tv.State = VolumeStatus.NeedsPassword;
        tv.Hint = info?.Hint;
        tv.Message = null;
        AddNotice("bilgi", WinText.U("Notice.NeedsPassword", tv.Name));
        ServiceLog.Write(WinText.T("Svc.WaitingForPassword", disk.Key, tv.Name));
    }

    /// <summary>Şifreli birimin adını ve ipucunu parola olmadan öğrenir (sağlayıcı null döndürür, bilgiyi yakalarız).</summary>
    private static VolumeLockInfo? ProbeLockInfo(TrackedVolume tv)
    {
        VolumeLockInfo? seen = null;
        try { ApfsVolume.Open(tv.Container, tv.Oid, info => { seen = info; return null; }); }
        catch (ApfsException) { }
        return seen;
    }

    private void TryMount(TrackedDisk disk, TrackedVolume tv, ApfsVolume v)
    {
        tv.Name = v.Name;
        if (!v.IsUserVisible)
        {
            tv.State = VolumeStatus.Hidden;
            tv.Message = WinText.U("Volume.InternalRole", v.RoleName);
            return;
        }
        try
        {
            var m = ApfsMount.Mount(v, null, msg => AddNotice("uyarı", $"{disk.Description} \"{v.Name}\": {msg}", throttleKey: msg));
            tv.Mount = m;
            tv.State = VolumeStatus.Mounted;
            tv.Message = null;
            AddNotice("bilgi", WinText.U("Notice.Mounted", v.Name, m.MountPoint));
            ServiceLog.Write(WinText.T("Svc.Mounted", disk.Key, v.Name, m.MountPoint));
        }
        catch (ApfsException e)
        {
            tv.State = VolumeStatus.Failed;
            tv.Message = e.Message;
            AddNotice("hata", WinText.U("Notice.MountFailed", v.Name, e.Message));
        }
    }

    // ---------------- Parola ----------------

    /// <param name="container">APFS bölüm sırası (birim sırası bölüm içindedir; çok bölümlü diskte ikisi de gerekir).</param>
    public UnlockResponse Unlock(string diskKey, int container, int volumeIndex, string password)
    {
        TrackedDisk? disk;
        TrackedVolume? tv;
        lock (_gate)
        {
            disk = _disks.GetValueOrDefault(diskKey);
            tv = disk?.Volumes.FirstOrDefault(x => x.ContainerIndex == container && x.Index == volumeIndex && x.State == VolumeStatus.NeedsPassword);
        }
        if (disk is null || tv is null) return new UnlockResponse(false, WinText.U("Unlock.NotWaiting"));
        VolumeKey? key = null;
        try
        {
            var uuid = tv.Container.VolumeUuid(tv.Oid);
            key = VolumeKey.Unlock(tv.Container, uuid, password);
            var v = ApfsVolume.Open(tv.Container, tv.Oid, key);
            lock (_gate)
            {
                if (disk.Keys.Remove(uuid, out var old)) old.Dispose();
                disk.Keys[uuid] = key;
            }
            key = null; // sahipliği diske geçti
            TryMount(disk, tv, v);
            return tv.State == VolumeStatus.Mounted
                ? new UnlockResponse(true, WinText.U("Unlock.Mounted", v.Name, tv.Mount!.MountPoint))
                : new UnlockResponse(false, tv.Message ?? WinText.U("Unlock.MountFailed"));
        }
        catch (ApfsException e)
        {
            // Parola yanlışsa günlüğe yalnızca olay yazılır, parola asla.
            ServiceLog.Write(WinText.T("Svc.UnlockFailed", disk.Key, tv.Name, e.Code));
            return new UnlockResponse(false, e.Message);
        }
        finally
        {
            key?.Dispose();
        }
    }

    // ---------------- Çıkarma ----------------

    /// <summary>
    /// Tepsideki "Güvenle çıkar": birimleri ayırır, disk tanıtıcısını kapatır ve diskin çıkarılabilir üst aygıtını Windows'a
    /// çıkarttırır. Windows reddederse (başka bir program diski kullanıyorsa) disk yeniden bağlanır ve neden bildirilir.
    /// </summary>
    /// <param name="force">false ise diskte açık dosya veya süren kopyalama varken hiçbir şey yapılmaz, Busy yanıtı döner.</param>
    public UnlockResponse Eject(string diskKey, bool force = false)
    {
        TrackedDisk? disk;
        List<string> busy;
        lock (_gate)
        {
            disk = _disks.GetValueOrDefault(diskKey);
            busy = disk is null ? [] : BusyVolumes(disk);
        }
        if (disk is null) return new UnlockResponse(false, WinText.U("Disk.Gone"));
        if (!force && busy.Count > 0)
        {
            ServiceLog.Write(WinText.T("Svc.EjectDeferred", diskKey, string.Join(", ", busy)));
            return new UnlockResponse(false,
                WinText.U("Eject.Busy", disk.Description, string.Join(", ", busy)), Busy: true);
        }

        DiskEjector.Target? target = null;
        if (_imageSources is null && diskKey.StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(diskKey.AsSpan(13), out int number))
            target = DiskEjector.Find(number);

        ReleaseDisk(diskKey, "", rememberReleased: true, notify: false);
        if (target is not { Removable: true })
        {
            AddNotice("bilgi", WinText.U("Notice.Detached", disk.Description));
            return new UnlockResponse(true, WinText.U("Eject.Detached"));
        }
        var (ok, error) = DiskEjector.Eject(target);
        if (ok)
        {
            AddNotice("bilgi", WinText.U("Notice.Ejected", disk.Description));
            return new UnlockResponse(true, WinText.U("Eject.Ejected"));
        }
        ServiceLog.Write(WinText.T("Svc.EjectRefused", diskKey, error));
        lock (_gate) _released.Remove(diskKey);
        ThreadPool.QueueUserWorkItem(_ => Rescan());
        return new UnlockResponse(false, WinText.U("Eject.Remounting", error));
    }

    // ---------------- Yazma (deneysel) ----------------

    private static List<string> BusyVolumes(TrackedDisk disk, int? container = null) =>
        [.. disk.Volumes.Where(v => container is null || v.ContainerIndex == container)
            .Where(v => v.Mount?.FileSystem.IsBusy == true || v.WritableMount?.FileSystem.IsBusy == true)
            .Select(v => $"{v.Mount?.MountPoint ?? v.WritableMount?.MountPoint} \"{v.Name}\"")];

    /// <summary>
    /// Tepsideki "Yazmaya izin ver" / "Salt okunura dön", APFS bölümü başına (<paramref name="container"/> = bölüm sırası; diskteki
    /// öbür bölümlere dokunulmaz). Açarken: bölümün birimleri ayrılır, bölüm diskten yeniden açılıp yazma kapısından geçirilir;
    /// geçerse yazma izniyle açılıp yazılabilir bağlanır, geçmezse salt okunur yeniden bağlanır ve neden karta yazılır.
    /// Kapatırken: sürücü ayrılır, son değişiklikler kaydedilir (blok 0 dahil), bölüm salt okunur yeniden bağlanır.
    /// </summary>
    public UnlockResponse SetWritable(string diskKey, int container, bool enable, bool force)
    {
        TrackedDisk? disk;
        lock (_gate)
        {
            disk = _disks.GetValueOrDefault(diskKey);
            if (disk is null) return new UnlockResponse(false, WinText.U("Disk.Gone"));
            if (!disk.Containers.ContainsKey(container) && !disk.Writes.ContainsKey(container))
                return new UnlockResponse(false, WinText.U("Write.NoSuchPartition"));
            bool writable = disk.Writes.ContainsKey(container);
            if (enable == writable)
                return new UnlockResponse(true, enable ? WinText.U("Write.AlreadyWritable") : WinText.U("Write.AlreadyReadOnly"));
            var busy = BusyVolumes(disk, container);
            if (!force && busy.Count > 0)
                return new UnlockResponse(false,
                    WinText.U("Write.Busy", disk.Description, string.Join(", ", busy)), Busy: true);
            if (!disk.Switching.Add(container)) return new UnlockResponse(false, WinText.U("Write.PreviousRequestPending"));
        }
        try
        {
            return enable ? EnableWrite(disk, container) : DisableWrite(disk, container);
        }
        finally
        {
            lock (_gate) disk.Switching.Remove(container);
        }
    }

    private UnlockResponse EnableWrite(TrackedDisk disk, int container)
    {
        string key = disk.Key, description = disk.Description;
        UnmountContainer(disk, container); // bölümün salt okunur sürücüleri; disk ve öbür bölümler bağlı kalır

        // 1) Bölümü diskten yeniden aç ve yazma kapısından geçir (salt okunur)
        GateResult gate;
        string? refusal = null;
        try
        {
            using var c = SourceOpener.OpenContainer(disk.Device, container, ownsDevice: false);
            gate = WriteGate.Evaluate(c, KeysOf(disk));
            if (gate.Allowed && c.Superblock.FsOids.Count != 1)
                refusal = WinText.U("Write.MultipleVolumes");
            refusal ??= gate.Allowed ? null : string.Join(" ", gate.Reasons);
        }
        catch (Exception e)
        {
            gate = new GateResult(false, [], null, null);
            refusal = WinText.U("Write.CheckFailed", e.Message);
        }
        if (refusal is not null)
        {
            ServiceLog.Write(WinText.T("Svc.WriteNotEnabled", key, container, refusal));
            // Denetimin ayrıntısı yalnız günlüğe (teşhis için); dosya ve klasör adları gizlenir
            foreach (var e in gate.Report?.Errors.Take(10) ?? [])
                ServiceLog.Write(WinText.T("Svc.WriteCheck", key, container, Apfs.Diagnostics.CheckReport.HideNames(e)));
            RemountReadOnly(disk, container, refusal);
            AddNotice("uyarı", WinText.U("Notice.WriteRefused", description, refusal));
            return new UnlockResponse(false, refusal);
        }

        // 2) Yazma izniyle aç, oturumu başlat, yazılabilir bağla
        var grant = gate.Grant!;
        var resources = new List<IDisposable>();
        ContainerWrite? write = null;
        TrackedVolume? tv = null;
        try
        {
            IWritableBlockDevice raw = _imageSources is not null ? new FileImageWriter(key, grant) : new RawDiskWriter(int.Parse(key.AsSpan(13)), grant);
            resources.Add(raw);
            var p = PartitionLocator.FindApfsPartitions(raw)[container];
            IWritableBlockDevice window = p.Offset == 0 && p.Length == raw.Length ? raw : new WritableOffsetDevice(raw, p.Offset, p.Length, grant);
            if (!ReferenceEquals(window, raw)) resources.Insert(0, window);
            var session = WriteSession.Open(window, grant);
            write = new ContainerWrite(session, resources);
            var volUuid = session.Container.VolumeUuid(session.Container.Superblock.FsOids[0]);
            var vw = session.Volume(0, KeysOf(disk).FirstOrDefault(k => k.VolumeUuid == volUuid)); // şifreli birimde anahtar şart
            tv = new TrackedVolume(session.Container, container, 0, vw.Oid, vw.Name);
            tv.WritableMount = ApfsWritableMount.Mount(session, 0, null,
                msg => AddNotice("uyarı", $"{description} \"{vw.Name}\": {msg}", throttleKey: msg), closeSession: false);
            tv.State = VolumeStatus.Mounted;
            if (raw is RawDiskWriter rw) write.RemovalWatch = DeviceNotifications.WatchHandle(rw.Handle, a => OnHandleEvent(key, a));
            bool attached;
            lock (_gate)
            {
                attached = _disks.TryGetValue(key, out var current) && ReferenceEquals(current, disk);
                if (attached)
                {
                    disk.Writes[container] = write;
                    disk.WriteBlockReasons.Remove(container);
                    disk.Volumes.Add(tv);
                }
            }
            if (!attached) throw new ApfsException(ApfsErrorCode.NotFound, WinText.T("Svc.DiskRemovedMeanwhile"));
            AddNotice("bilgi", WinText.U("Notice.MountedWritable", vw.Name, tv.WritableMount.MountPoint));
            ServiceLog.Write(WinText.T("Svc.MountedWritable", key, vw.Name, tv.WritableMount.MountPoint));
            return new UnlockResponse(true, WinText.U("Write.Enabled", vw.Name, tv.WritableMount.MountPoint));
        }
        catch (Exception e)
        {
            // Oturum yayımlanmadan bırakılır: commit ve blok 0 yazılmaz, kimlik değişmez
            try { tv?.WritableMount?.FileSystem.Dispose(); tv?.WritableMount?.Dispose(); } catch { }
            write?.RemovalWatch?.Dispose();
            write?.Session.Dispose();
            foreach (var r in resources) { try { r.Dispose(); } catch { } }
            string msg = WinText.U("Write.EnableFailed", e.Message);
            ServiceLog.Write($"{key}: {msg}");
            bool stillAttached;
            lock (_gate) stillAttached = _disks.TryGetValue(key, out var current) && ReferenceEquals(current, disk);
            if (stillAttached) RemountReadOnly(disk, container, msg);
            AddNotice("hata", $"{description}: {msg}");
            return new UnlockResponse(false, msg);
        }
    }

    private UnlockResponse DisableWrite(TrackedDisk disk, int container)
    {
        CloseWrite(disk, container, abandon: false); // son commit + blok 0; kimlik kilit içinde tazelenir
        RemountReadOnly(disk, container, null);
        AddNotice("bilgi", WinText.U("Notice.RemountedReadOnly", disk.Description));
        return new UnlockResponse(true, WinText.U("Write.RemountedReadOnly"));
    }

    /// <summary>Bölümü diskten salt okunur yeniden açıp bağlar (yazma denemesinden ya da oturumdan sonra; bayat önbellek kalmasın).</summary>
    private void RemountReadOnly(TrackedDisk disk, int container, string? writeBlockReason)
    {
        lock (_gate) if (!_disks.TryGetValue(disk.Key, out var current) || !ReferenceEquals(current, disk)) return; // bu arada bırakıldı
        MountContainer(disk, container);
        lock (_gate)
        {
            if (writeBlockReason is null) disk.WriteBlockReasons.Remove(container);
            else disk.WriteBlockReasons[container] = writeBlockReason;
        }
    }

    /// <summary>
    /// Bir bölümün yazma oturumunu kapatır: sürücü ayrılır (bekleyen veri gelir), son commit ve blok 0, yazılabilir aygıtlar
    /// bırakılır. Kapatma diskin kimliğini değiştirdiği için kimlik denetimiyle aynı kilit altında yapılır ve kimlik tazelenir.
    /// <paramref name="abandon"/>: diskin içeriği başka bir programca değiştirilmiş (veya doğrulanamıyor) — diske HİÇBİR ŞEY
    /// yazılmaz (commit ve blok 0 yok); önce kayıtlar durdurulur ki ayırma sırasında gelen Flush da yazmasın.
    /// </summary>
    private void CloseWrite(TrackedDisk disk, int container, bool abandon)
    {
        lock (disk.IdentityLock)
        {
            ContainerWrite? write;
            List<TrackedVolume> volumes;
            lock (_gate)
            {
                if (!disk.Writes.Remove(container, out write)) return;
                volumes = disk.Volumes.Where(v => v.ContainerIndex == container).ToList();
                disk.Volumes.RemoveAll(v => v.ContainerIndex == container);
            }
            foreach (var tv in volumes)
            {
                try
                {
                    if (abandon) tv.WritableMount?.FileSystem.Dispose(); // commit zamanlayıcısı ve Flush artık yazmaz
                    tv.WritableMount?.Dispose();
                }
                catch (Exception e) { ServiceLog.Write(WinText.T("Svc.WritableUnmountError", disk.Key, e.Message)); }
                tv.WritableMount = null;
            }
            if (abandon)
            {
                write.Session.Dispose(); // açık işlem yazılmaz
                ServiceLog.Write(WinText.T("Svc.WriteSessionAbandoned", disk.Key, container, write.Session.CommittedXid));
            }
            else
            {
                try
                {
                    write.Session.Close();
                    ServiceLog.Write(WinText.T("Svc.WriteSessionClosed", disk.Key, container, write.Session.CommittedXid));
                }
                catch (Exception e)
                {
                    AddNotice("hata", WinText.U("Notice.CloseWriteFailed", disk.Description, e.Message));
                }
            }
            // Yalnız geliştirme modu (testler): Close ile kimlik tazeleme arasındaki pencereyi genişletir
            if (_imageSources is not null && int.TryParse(Environment.GetEnvironmentVariable("APFS_DEV_CLOSE_DELAY_MS"), out int delay))
                Thread.Sleep(delay);
            write.RemovalWatch?.Dispose(); // kayıt kaldırma geri çağrı dışında (Dispose iş parçacığı havuzunda yapar)
            foreach (var r in write.Resources) { try { r.Dispose(); } catch { } }
            if (!abandon)
            {
                // Close blok 0'ı yazdı: kimlik yeni hâline tazelenir (okunamazsa eski kalır; sonraki tarama WinText.U("Release.Removed") der)
                try { disk.Fingerprint = PartitionLocator.Fingerprint(disk.Device); }
                catch (Exception e) when (e is ApfsException or ObjectDisposedException) { }
            }
        }
    }

    /// <summary>"Yeniden tara": daha önce ayrılmış (ama çıkarılmamış) diskler de yeniden bağlansın.</summary>
    public void ForgetReleased()
    {
        lock (_gate) _released.Clear();
    }

    private void OnHandleEvent(string key, DeviceNotifications.NotifyAction action)
    {
        switch (action)
        {
            case DeviceNotifications.NotifyAction.QueryRemove:
                // Windows diski çıkarmak istiyor: tanıtıcıyı geri çağrının içinde, eşzamanlı olarak kapat.
                ReleaseDisk(key, WinText.U("Release.SafeToRemove"), rememberReleased: true);
                break;
            case DeviceNotifications.NotifyAction.QueryRemoveFailed:
                lock (_gate) _released.Remove(key);
                ThreadPool.QueueUserWorkItem(_ => Rescan());
                break;
            case DeviceNotifications.NotifyAction.RemovePending:
            case DeviceNotifications.NotifyAction.RemoveComplete:
                ReleaseDisk(key, WinText.U("Release.Removed"), rememberReleased: false);
                lock (_gate) _released.Remove(key);
                break;
            case DeviceNotifications.NotifyAction.CustomEvent:
                // ör. başka bir program bölüm tablosunu yazdı: geri çağrıyı bekletmeden tarama iste (kimlik denetlenir)
                ScanRequested?.Invoke();
                break;
        }
    }

    /// <summary>Hizmetten tarama isteği (diske özel aygıt olayı geldi); hizmet kısa bir gecikmeyle tek tarama yapar.</summary>
    public event Action? ScanRequested;

    private static string ChangedReason =>
        WinText.U("Release.Changed");

    /// <summary>
    /// Kaybolan (okuma hatası veren) ve içeriği değişen (APFS kimliği tutmayan: ör. Rufus ile biçimlendirilmiş) diskleri
    /// bırakır. Değişen diskte yazma oturumu kaydedilmeden terk edilir; disk "bırakıldı" sayılmaz, yeni içerik APFS ise
    /// sonraki tarama onu bağlar.
    /// </summary>
    private void CheckForRemovedDisks()
    {
        List<TrackedDisk> disks;
        lock (_gate) disks = [.. _disks.Values];
        foreach (var d in disks)
        {
            // Kimlik oku-karşılaştır-uygula, bir bölümün Close'uyla (kimliği değiştirir ve tazeler) aynı kilit altında
            lock (d.IdentityLock)
            {
                lock (_gate) if (!_disks.TryGetValue(d.Key, out var current) || !ReferenceEquals(current, d)) continue;
                string? now; // null = okunamadı (çıkarılmış)
                try { now = PartitionLocator.Fingerprint(d.Device); }
                catch (ApfsException e) when (e.Code is ApfsErrorCode.IoError or ApfsErrorCode.NotFound) { now = null; }
                catch (ObjectDisposedException) { now = null; } // bu arada başka bir istek diski bıraktı
                catch (ApfsException) { now = ""; }              // bölüm tablosu/kapsayıcı başı bozuk: içerik değişmiş
                catch (Exception e)
                {
                    ServiceLog.Write(WinText.T("Svc.DiskCheckFailed", d.Key, e.Message));
                    continue;
                }
                // Yazma oturumundaki disk doğrulanamıyorsa da kaydedilmeden bırakılır: biçimlendirme okuma hatası da
                // doğurabilir; gerçekten çekilmiş diske zaten yazılamaz.
                if (now is null)
                    ReleaseDisk(d.Key, WinText.U("Release.Removed"), rememberReleased: false, abandonWrites: true, expected: d);
                else if (now != d.Fingerprint)
                    ReleaseDisk(d.Key, ChangedReason, rememberReleased: false, abandonWrites: true, expected: d, contentChanged: true);
            }
        }
        // Güvenli kaldırılmış ama aslında hâlâ takılı olmayan diskler listeden düşsün (yeniden takılınca bağlansın)
        if (_imageSources is null)
        {
            List<string> released;
            lock (_gate) released = [.. _released];
            foreach (var key in released)
            {
                bool present;
                try { using var dev = new RawDiskDevice(key); present = true; }
                catch (ApfsException) { present = false; }
                if (!present) lock (_gate) _released.Remove(key);
            }
        }
    }

    /// <param name="abandonWrites">Yazma oturumları varsa kaydetmeden bırak (bkz. <see cref="CloseWrite"/>).</param>
    /// <param name="expected">Verilirse yalnız anahtardaki disk hâlâ BU nesneyse bırakılır: tarama eski bir anlık görüntüyle
    /// çalışırken aynı anahtara yeni bağlanmış diski düşürmesin.</param>
    /// <param name="contentChanged">Disk başka bir programca değiştirildi (bildirimde kaydedilmeyen değişikliklerin nedeni farklı).</param>
    /// <remarks>
    /// Tekrar çağrılabilir: diskin her açık tanıtıcısının (salt okunur + her yazma tanıtıcısı) güvenli kaldırma kaydı buraya gelir;
    /// ilk çağrı BÜTÜN tanıtıcıları eşzamanlı kapatır (QueryRemove geri çağrısının içinde), sonrakiler bir şey bulmaz.
    /// </remarks>
    private void ReleaseDisk(string key, string reason, bool rememberReleased, bool notify = true, bool abandonWrites = false,
        TrackedDisk? expected = null, bool contentChanged = false)
    {
        TrackedDisk? disk;
        lock (_gate)
        {
            if (!_disks.TryGetValue(key, out disk) || (expected is not null && !ReferenceEquals(disk, expected))) return;
            _disks.Remove(key);
            if (rememberReleased) _released.Add(key);
        }
        bool abandoned;
        List<TrackedVolume> mounted;
        lock (disk.IdentityLock) // süren bir bölüm kapatması bitsin; aygıtlar onun altından çekilmesin
        {
            List<int> writes;
            lock (_gate)
            {
                writes = [.. disk.Writes.Keys];
                mounted = [.. disk.Volumes];
            }
            abandoned = abandonWrites && writes.Count > 0;
            foreach (int p in writes) CloseWrite(disk, p, abandonWrites);
            foreach (var tv in mounted)
            {
                try { tv.Mount?.Dispose(); } catch (Exception e) { ServiceLog.Write(WinText.T("Svc.UnmountError", key, e.Message)); }
            }
            List<ApfsContainer> containers;
            lock (_gate) containers = [.. disk.Containers.Values];
            foreach (var c in containers) c.Dispose();
            disk.Device.Dispose();
            disk.RemovalWatch?.Dispose();
            List<VolumeKey> keys;
            lock (_gate) { keys = [.. disk.Keys.Values]; disk.Keys.Clear(); }
            foreach (var k in keys) k.Dispose(); // çıkarılan/değişen diskin anahtarı bellekte kalmasın
        }
        if (!notify) return;
        var names = mounted.Where(v => v.State == VolumeStatus.Mounted).Select(v => $"\"{v.Name}\"").ToList();
        string text = $"{disk.Description}{(names.Count > 0 ? " (" + string.Join(", ", names) + ")" : "")} {reason}.";
        if (abandoned)
            text += " " + (contentChanged
                ? WinText.U("Release.UnsavedNotWrittenChanged")
                : WinText.U("Release.UnsavedLost"));
        AddNotice(abandoned ? "uyarı" : "bilgi", text);
        ServiceLog.Write($"{key}: {reason}.");
    }

    // ---------------- Durum ve bildirimler ----------------

    public StatusResponse Status(long since)
    {
        lock (_gate)
        {
            var disks = _disks.Values.Select(d => new DiskStatus(d.Key, d.Key, d.Description,
                d.Volumes.OrderBy(v => v.ContainerIndex).ThenBy(v => v.Index)
                    .Select(v => new VolumeStatus(v.Index, v.Name, v.State, v.Mount?.MountPoint ?? v.WritableMount?.MountPoint, v.Hint, v.Message,
                        Writable: v.WritableMount is not null, Container: v.ContainerIndex,
                        WriteBlockReason: d.WriteBlockReasons.GetValueOrDefault(v.ContainerIndex))).ToList(),
                Writable: d.Writes.Count > 0,
                WriteBlockReason: d.WriteBlockReasons.OrderBy(r => r.Key).Select(r => r.Value).FirstOrDefault())).ToList();
            return new StatusResponse(disks, _notices.Where(n => n.Id > since).ToList(), _noticeId);
        }
    }

    private void AddNotice(string level, string text, string? throttleKey = null)
    {
        lock (_gate)
        {
            // Aynı uyarı (ör. bozuk bir dosyaya tekrar tekrar erişim) bir dakikada bir kez bildirilir
            if (throttleKey is not null)
            {
                if (_recentNotices.TryGetValue(throttleKey, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
                _recentNotices[throttleKey] = DateTime.UtcNow;
            }
            _notices.Add(new Notice(++_noticeId, level, text, DateTime.Now));
            if (_notices.Count > 200) _notices.RemoveAt(0);
        }
        ServiceLog.Write($"[{level}] {text}");
    }

    private static string DescribeDisk(string key, IBlockDevice device)
    {
        double gb = device.Length / 1e9;
        string size = gb >= 1 ? $"{gb:0.#} GB" : $"{device.Length / 1e6:0.#} MB";
        return key.StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase) ? WinText.U("Disk.PhysicalName", key[13..], size) : $"{Path.GetFileName(key)} ({size})";
    }

    public void Dispose()
    {
        List<string> keys;
        lock (_gate) keys = [.. _disks.Keys];
        foreach (var k in keys) ReleaseDisk(k, WinText.U("Release.ServiceStopped"), rememberReleased: false);
    }
}
