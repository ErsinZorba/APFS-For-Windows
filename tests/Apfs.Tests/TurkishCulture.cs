using System.Globalization;
using System.Runtime.CompilerServices;

namespace Apfs.Tests;

/// <summary>
/// Tüm testler bilinçli olarak Türkçe kültürde çalışır (Risk 3): kültüre duyarlı bir karşılaştırma ("i".ToUpper() == "İ")
/// koda sızarsa testler bunu yakalar. Makinenin kendi dilinden bağımsızdır (CI'da da aynı davranır).
/// </summary>
internal static class TurkishCulture
{
    [ModuleInitializer]
    internal static void Init()
    {
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.DefaultThreadCurrentCulture = tr;
        CultureInfo.DefaultThreadCurrentUICulture = tr;
        CultureInfo.CurrentCulture = tr;
        CultureInfo.CurrentUICulture = tr;
    }
}
