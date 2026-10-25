using System.Globalization;
using Apfs.Localization;
using Apfs.Windows;

namespace Apfs.Service;

/// <summary>
/// Hizmetin dili. Hizmet LocalSystem olarak çalıştığı için kullanıcının dilini bilmez: tepsi her istekte kendi dilini
/// gönderir (<see cref="Apfs.Windows.Ipc.Request.Lang"/>), geliştirme modunda `--lang` verilebilir. Bildirimler ve nedenler o
/// andan sonra bu dilde üretilir (önceden üretilmiş metinler değişmez). Yalnız desteklenen diller kabul edilir.
/// </summary>
internal static class ServiceLanguage
{
    private static string? _current;

    public static void Apply(string? name)
    {
        var culture = Languages.Resolve(name);
        if (culture is null || culture.Name == Volatile.Read(ref _current)) return;
        Volatile.Write(ref _current, culture.Name);
        // İş parçacıkları kendi dilini hiç ayarlamadığı için bu, hizmetin bütün iş parçacıklarına (WinFsp dahil) geçer.
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        ServiceLog.Write(WinText.T("Service.LanguageSet", culture.Name));
    }
}
