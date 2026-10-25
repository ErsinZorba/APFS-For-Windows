using System.Text.Json;
using Apfs.Windows;
using Apfs.Windows.Ipc;

namespace Apfs.Service;

/// <summary>Tepsi uygulamasının bağlandığı adlandırılmış kanal sunucusu. Her bağlantı: tek istek, tek yanıt.</summary>
internal sealed class PipeServer(MountManager manager) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var server = PipeProtocol.CreateServer();
                await server.WaitForConnectionAsync(ct);
                _ = Task.Run(() => HandleAsync(server, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                ServiceLog.Write(WinText.T("Svc.PipeError", e.Message));
                await Task.Delay(1000, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private async Task HandleAsync(System.IO.Pipes.NamedPipeServerStream server, CancellationToken ct)
    {
        using (server)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var line = await PipeProtocol.ReadLineAsync(server, timeout.Token);
                if (line is null) return;
                var req = JsonSerializer.Deserialize<Request>(line, PipeProtocol.Json);
                ServiceLanguage.Apply(req?.Lang);
                object response = req?.Op switch
                {
                    "status" => manager.Status(req.Since),
                    "unlock" when req.Disk is not null && req.Password is not null =>
                        await Task.Run(() => manager.Unlock(req.Disk, req.Container, req.Volume, req.Password), ct),
                    "rescan" => await Task.Run(() => { manager.ForgetReleased(); manager.Rescan(); return new UnlockResponse(true, WinText.T("Svc.Rescanned")); }, ct),
                    "eject" when req.Disk is not null => await Task.Run(() => manager.Eject(req.Disk, req.Force), ct),
                    "set-writable" when req.Disk is not null => await Task.Run(() => manager.SetWritable(req.Disk, req.Container, req.Enable, req.Force), ct),
                    _ => new UnlockResponse(false, WinText.T("Svc.UnknownRequest")),
                };
                await PipeProtocol.WriteLineAsync(server, response, timeout.Token);
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or OperationCanceledException)
            {
                // Bozuk veya yarıda kalan istek: bağlantıyı kapat, hizmet çalışmaya devam eder.
            }
            catch (Exception e)
            {
                ServiceLog.Write(WinText.T("Svc.RequestFailed", e.GetType().Name, e.Message));
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
