namespace Apfs;

/// <summary>Kullanıcıya gösterilebilecek hata türleri. Mesajlar Türkçe ve teknik olmayan dildedir.</summary>
public enum ApfsErrorCode
{
    NotApfs,
    NoValidCheckpoint,
    Corrupt,
    ChecksumMismatch,
    Unsupported,
    Encrypted,
    NotFound,
    IoError,
    AccessDenied,
    InvalidArgument,
    WrongPassword,
    DiskFull,
    AlreadyExists,
    DirectoryNotEmpty,
}

public sealed class ApfsException : Exception
{
    public ApfsErrorCode Code { get; }
    public string? Detail { get; }

    public ApfsException(ApfsErrorCode code, string? detail = null, Exception? inner = null)
        : base(Describe(code, detail), inner)
    {
        Code = code;
        Detail = detail;
    }

    public static string Describe(ApfsErrorCode code, string? detail) => code switch
    {
        ApfsErrorCode.NotApfs => "Bu disk veya dosya APFS biçiminde değil." + Suffix(detail),
        ApfsErrorCode.NoValidCheckpoint =>
            "APFS kapsayıcısının hiçbir kontrol noktası sağlam değil. Disk ciddi şekilde hasarlı olabilir. Diskinize hiçbir şey yazılmadı." + Suffix(detail),
        ApfsErrorCode.Corrupt =>
            "Diskteki bir yapı bozuk görünüyor. Bu kısım okunamadı, diskinize hiçbir şey yazılmadı." + Suffix(detail),
        ApfsErrorCode.ChecksumMismatch =>
            "Diskteki bir bloğun sağlama toplamı tutmuyor; bu bölüm hasarlı olabilir. Diskinize hiçbir şey yazılmadı." + Suffix(detail),
        ApfsErrorCode.Unsupported => "Bu özellik henüz desteklenmiyor." + Suffix(detail),
        ApfsErrorCode.Encrypted => "Bu birim şifreli. Açmak için parola gerekiyor." + Suffix(detail),
        ApfsErrorCode.NotFound => "Dosya veya klasör bulunamadı." + Suffix(detail),
        ApfsErrorCode.IoError => "Diskten okunamadı. Disk çıkarılmış veya arızalı olabilir." + Suffix(detail),
        ApfsErrorCode.AccessDenied => "Erişim izni yok." + Suffix(detail),
        ApfsErrorCode.InvalidArgument => "Geçersiz değer." + Suffix(detail),
        ApfsErrorCode.WrongPassword => "Parola yanlış." + Suffix(detail),
        ApfsErrorCode.DiskFull => "Diskte yer kalmadı." + Suffix(detail),
        ApfsErrorCode.AlreadyExists => "Bu adda bir dosya veya klasör zaten var." + Suffix(detail),
        ApfsErrorCode.DirectoryNotEmpty => "Klasör boş değil." + Suffix(detail),
        _ => "Bilinmeyen hata." + Suffix(detail),
    };

    private static string Suffix(string? detail) => string.IsNullOrEmpty(detail) ? "" : $" ({detail})";

    internal static ApfsException Corrupt(string detail) => new(ApfsErrorCode.Corrupt, detail);
}
