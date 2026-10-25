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
        ApfsErrorCode.NotApfs => CoreText.U("Error.NotApfs") + Suffix(detail),
        ApfsErrorCode.NoValidCheckpoint =>
            CoreText.U("Error.NoValidCheckpoint") + Suffix(detail),
        ApfsErrorCode.Corrupt =>
            CoreText.U("Error.Corrupt") + Suffix(detail),
        ApfsErrorCode.ChecksumMismatch =>
            CoreText.U("Error.ChecksumMismatch") + Suffix(detail),
        ApfsErrorCode.Unsupported => CoreText.U("Error.Unsupported") + Suffix(detail),
        ApfsErrorCode.Encrypted => CoreText.U("Error.Encrypted") + Suffix(detail),
        ApfsErrorCode.NotFound => CoreText.U("Error.NotFound") + Suffix(detail),
        ApfsErrorCode.IoError => CoreText.U("Error.IoError") + Suffix(detail),
        ApfsErrorCode.AccessDenied => CoreText.U("Error.AccessDenied") + Suffix(detail),
        ApfsErrorCode.InvalidArgument => CoreText.U("Error.InvalidArgument") + Suffix(detail),
        ApfsErrorCode.WrongPassword => CoreText.U("Error.WrongPassword") + Suffix(detail),
        ApfsErrorCode.DiskFull => CoreText.U("Error.DiskFull") + Suffix(detail),
        ApfsErrorCode.AlreadyExists => CoreText.U("Error.AlreadyExists") + Suffix(detail),
        ApfsErrorCode.DirectoryNotEmpty => CoreText.U("Error.DirectoryNotEmpty") + Suffix(detail),
        _ => CoreText.U("Error.Unknown") + Suffix(detail),
    };

    private static string Suffix(string? detail) => string.IsNullOrEmpty(detail) ? "" : $" ({detail})";

    internal static ApfsException Corrupt(string detail) => new(ApfsErrorCode.Corrupt, detail);
}
