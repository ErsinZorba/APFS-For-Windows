using Apfs.Tray;
using Apfs.Windows.Ipc;

// apfs-tray [--show] [--pipe ad]
//   --show: ana pencereyi aç (Başlat menüsü kısayolu). Program zaten çalışıyorsa açık örneğin penceresi öne gelir.
//   --pipe: yalnızca geliştirme; `apfs-service --console --pipe ad` ile çalışan hizmete bağlanır.
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--pipe") PipeProtocol.PipeName = args[i + 1];
bool show = args.Contains("--show");
string suffix = PipeProtocol.PipeName == "APFSforWindows" ? "" : "." + PipeProtocol.PipeName; // geliştirme örneği kurulu olanla çakışmasın

// Oturum başına tek örnek
using var mutex = new Mutex(initiallyOwned: true, @"Local\APFSforWindows.Tray" + suffix, out bool first);
using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\APFSforWindows.Tray.Show" + suffix);
if (!first)
{
    if (show) showEvent.Set();
    return;
}

ApplicationConfiguration.Initialize();
Application.Run(new TrayApp(showEvent, show));
