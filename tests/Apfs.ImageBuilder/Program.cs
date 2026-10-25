using System.Security.Cryptography;
using System.Text;
using Apfs.ImageBuilder;

// Kullanım: apfs-mkimage <senaryo> <çıktı> [--gpt] [--vhd] [--seed metin]
// --seed: kapsayıcı (ve GPT disk) kimliğini değiştirir; aynı anda takılan VHD'lerin Windows'ta imza çakışması yaşamaması için.
// apfs-mkimage wrap <kapsayıcı.img>... <çıktı.vhd|.img>: var olan APFS kapsayıcı görüntülerini (birden çoksa art arda bölümlere)
// GPT bölümlü sabit VHD'ye (ya da .img ise düz GPT disk görüntüsüne; Mac'in hdiutil'i bunu takabilir) sarar
if (args.Length >= 3 && args[0] == "wrap")
{
    try { GptDisk.Wrap(args[1..^1], args[^1]); }
    catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
    Console.WriteLine($"Yazıldı: {args[^1]} ({new FileInfo(args[^1]).Length:N0} bayt)");
    return 0;
}
// apfs-mkimage encrypt <şifresiz.img> <çıktı.img>: mkapfs/linux-apfs-rw görüntüsünün ilk birimini şifreli birime dönüştürür
// (parola ImageEncryptor.Password)
if (args.Length == 3 && args[0] == "encrypt")
{
    var r = ImageEncryptor.Encrypt(args[1], args[2]);
    Console.WriteLine($"Yazıldı: {args[2]} (birim {r.VolumeUuid}, {r.FsNodes} fs düğümü, {r.Extents} extent, {r.DataBlocks} veri bloğu)");
    return 0;
}
if (args.Length < 2)
{
    Console.Error.WriteLine($"Kullanım: apfs-mkimage <senaryo> <çıktı.img|.vhd> [--gpt] [--vhd] [--seed metin]\nSenaryolar: {string.Join(", ", Scenarios.Names)}");
    return 2;
}
var builder = Scenarios.Create(args[0]);
builder.AsVhd = args.Contains("--vhd");
builder.WithGpt = builder.AsVhd || args.Contains("--gpt");
int seedIndex = Array.IndexOf(args, "--seed");
if (seedIndex >= 0 && seedIndex + 1 < args.Length)
{
    var h = SHA256.HashData(Encoding.UTF8.GetBytes(args[seedIndex + 1]));
    h[6] = (byte)((h[6] & 0x0F) | 0x40);
    h[8] = (byte)((h[8] & 0x3F) | 0x80);
    builder.ContainerUuid = new Guid(h.AsSpan(0, 16), bigEndian: true);
}
if (builder.AsVhd) builder.MinBlockCount = 16 * 1024 * 1024 / ApfsImageBuilder.BlockSize; // Windows küçük VHD'leri takmıyor
builder.Write(args[1]);
Console.WriteLine($"Yazıldı: {args[1]} ({new FileInfo(args[1]).Length:N0} bayt)");
return 0;
