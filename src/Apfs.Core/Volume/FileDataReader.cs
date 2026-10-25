namespace Apfs.Volume;

/// <summary>
/// Bir veri akışını extent listesi üzerinden okur. İş parçacığı güvenlidir (durum tutmaz).
/// Extent'ler arasındaki boşluklar ve PhysBlock = 0 olan extent'ler seyrek alandır ve sıfır döner.
/// </summary>
public sealed class FileDataReader : IDataReader
{
    private readonly ApfsVolume _volume;
    private readonly FileExtent[] _extents;

    /// <param name="extents">Mantıksal ofsete göre sıralı extent'ler. Dizi paylaşılabilir (önbellek); değiştirilmez.</param>
    public FileDataReader(ApfsVolume volume, FileExtent[] extents, ulong size)
    {
        _volume = volume;
        _extents = extents;
        Length = size;
        ulong prevEnd = 0;
        foreach (var e in _extents)
        {
            if (e.LogicalOffset < prevEnd)
                throw ApfsException.Corrupt("dosya parçaları (extent) üst üste biniyor");
            prevEnd = e.LogicalOffset + e.Length;
            if (prevEnd < e.LogicalOffset)
                throw ApfsException.Corrupt("dosya parçası boyutu taşıyor");
        }
    }

    public ulong Length { get; }

    /// <summary><paramref name="offset"/>'ten başlayarak okur; dosya sonunda kısalır. Okunan bayt sayısını döndürür.</summary>
    public int Read(ulong offset, Span<byte> buffer)
    {
        if (offset >= Length) return 0;
        int toRead = (int)Math.Min((ulong)buffer.Length, Length - offset);
        buffer = buffer[..toRead];
        uint bs = _volume.Reader.BlockSize;

        int done = 0;
        int idx = FindExtent(offset);
        while (done < toRead)
        {
            ulong pos = offset + (ulong)done;
            // idx: pos'u içeren veya pos'tan sonraki ilk extent
            while (idx < _extents.Length && _extents[idx].LogicalOffset + _extents[idx].Length <= pos) idx++;

            if (idx >= _extents.Length || _extents[idx].LogicalOffset > pos)
            {
                // Seyrek boşluk: bir sonraki extent'e kadar sıfır
                ulong holeEnd = idx < _extents.Length ? _extents[idx].LogicalOffset : ulong.MaxValue;
                int n = (int)Math.Min((ulong)(toRead - done), holeEnd - pos);
                buffer.Slice(done, n).Clear();
                done += n;
                continue;
            }

            var ext = _extents[idx];
            ulong within = pos - ext.LogicalOffset;
            int count = (int)Math.Min((ulong)(toRead - done), ext.Length - within);
            if (ext.PhysBlock == 0)
            {
                buffer.Slice(done, count).Clear();
            }
            else
            {
                ulong lastBlock = ext.PhysBlock + (ext.Length - 1) / bs;
                if (lastBlock >= _volume.Reader.BlockCount || lastBlock < ext.PhysBlock)
                    throw ApfsException.Corrupt("dosya parçası diskin dışını gösteriyor");
                if (_volume.Crypto is { } crypto && ext.CryptoId != 0)
                    ReadEncrypted(crypto, ext, within, buffer.Slice(done, count));
                else
                    _volume.Reader.ReadBlocks(ext.PhysBlock + within / bs, (long)(within % bs), buffer.Slice(done, count));
            }
            done += count;
        }
        return toRead;
    }

    /// <summary>
    /// Şifreli extent: okuma 512 baytlık XTS birimlerine genişletilir, her birim tweak = crypto_id × (blok/512) + birim sırası ile
    /// çözülür ve istenen bölüm kopyalanır.
    /// </summary>
    private void ReadEncrypted(Crypto.VolumeCrypto crypto, FileExtent ext, ulong within, Span<byte> dest)
    {
        const int unit = Crypto.AesXts.UnitSize;
        ulong firstUnit = within / unit;
        ulong end = within + (ulong)dest.Length;
        ulong lastUnitEnd = (end + unit - 1) / unit * unit;
        const int chunkUnits = 2048; // 1 MiB'lık parçalarla
        var buf = new byte[(int)Math.Min(lastUnitEnd - firstUnit * unit, (ulong)chunkUnits * unit)];
        uint bs = _volume.Reader.BlockSize;
        for (ulong u = firstUnit; u * unit < end; u += chunkUnits)
        {
            ulong start = u * unit;
            int len = (int)Math.Min((ulong)buf.Length, lastUnitEnd - start);
            var span = buf.AsSpan(0, len);
            _volume.Reader.ReadBlocks(ext.PhysBlock + start / bs, (long)(start % bs), span);
            crypto.DecryptData(span, ext.CryptoId, u);
            ulong copyFrom = Math.Max(start, within);
            ulong copyTo = Math.Min(start + (ulong)len, end);
            span.Slice((int)(copyFrom - start), (int)(copyTo - copyFrom)).CopyTo(dest[(int)(copyFrom - within)..]);
        }
    }

    /// <summary>İkili arama: offset'i içeren veya ondan sonraki ilk extent'in sırası.</summary>
    private int FindExtent(ulong offset)
    {
        int lo = 0, hi = _extents.Length - 1, ans = _extents.Length;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_extents[mid].LogicalOffset + _extents[mid].Length > offset) { ans = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return ans;
    }
}
