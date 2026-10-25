namespace Apfs.Volume;

/// <summary>Parola istenirken kullanıcıya gösterilecek bilgiler.</summary>
public sealed record VolumeLockInfo(ulong Oid, string Name, Guid Uuid, ushort Role, string? Hint);
