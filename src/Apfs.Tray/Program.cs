using System.Globalization;
using Apfs.Tray;
using Apfs.Windows.Ipc;
using Apfs.Windows.Localization;

// apfs-tray [--show] [--pipe ad] [--restart]
//   --show: ana pencereyi aç (Başlat menüsü kısayolu). Program zaten çalışıyorsa açık örneğin penceresi öne gelir.
//   --pipe: yalnızca geliştirme; `apfs-service --console --pipe ad` ile çalışan hizmete bağlanır.
//   --restart: dil değişince kendini yeniden başlatan örnek; eskisi kapanana kadar (en çok 10 sn) tek örnek kilidini bekler.
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--pipe") PipeProtocol.PipeName = args[i + 1];
bool show = args.Contains("--show");
string suffix = PipeProtocol.PipeName == "APFSforWindows" ? "" : "." + PipeProtocol.PipeName; // geliştirme örneği kurulu olanla çakışmasın

// Arayüz dili: kayıtlı seçim, yoksa Windows'un dili (süreç kendi dilini ayarlamadan önce okunmalı). Hizmete her istekte gider.
var language = LanguageSetting.Effective();
CultureInfo.DefaultThreadCurrentUICulture = language;
CultureInfo.CurrentUICulture = language;
PipeProtocol.ClientLanguage = language.Name;

// Oturum başına tek örnek
using var mutex = new Mutex(initiallyOwned: true, @"Local\APFSforWindows.Tray" + suffix, out bool first);
using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\APFSforWindows.Tray.Show" + suffix);
if (!first && args.Contains("--restart"))
{
    try { first = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
    catch (AbandonedMutexException) { first = true; } // eski örnek kilidi bırakmadan kapandı: kilit artık bizim
}
if (!first)
{
    if (show) showEvent.Set();
    return;
}

ApplicationConfiguration.Initialize();
Application.Run(new TrayApp(showEvent, show));
mutex.ReleaseMutex(); // yeniden başlatılan örnek beklemesin
