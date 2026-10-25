namespace Apfs.Crypto;

/// <summary>APFS anahtar kayıtlarındaki (KEK/VEK) DER yapıları için en küçük gerekli, sınır kontrollü ayrıştırıcı.</summary>
internal ref struct Der
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public Der(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public bool AtEnd => _pos >= _data.Length;

    /// <summary>Sıradaki öğeyi okur; etiketi <paramref name="tag"/> değilse bozuk veri hatası.</summary>
    public ReadOnlySpan<byte> Read(byte tag)
    {
        if (_pos + 2 > _data.Length) throw Corrupt("eksik DER öğesi");
        byte t = _data[_pos++];
        if (t != tag) throw Corrupt($"beklenen DER etiketi 0x{tag:x2}, bulunan 0x{t:x2}");
        int len = _data[_pos++];
        if (len >= 0x80)
        {
            int bytes = len & 0x7F;
            if (bytes is 0 or > 3 || _pos + bytes > _data.Length) throw Corrupt("geçersiz DER uzunluğu");
            len = 0;
            for (int i = 0; i < bytes; i++) len = (len << 8) | _data[_pos++];
        }
        if (len < 0 || _pos + len > _data.Length) throw Corrupt("DER öğesi sınır dışında");
        var value = _data.Slice(_pos, len);
        _pos += len;
        return value;
    }

    /// <summary>Etiket eşleşirse okur, eşleşmezse boş döner (isteğe bağlı alanlar için).</summary>
    public bool TryRead(byte tag, out ReadOnlySpan<byte> value)
    {
        if (_pos < _data.Length && _data[_pos] == tag)
        {
            value = Read(tag);
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Big-endian işaretsiz tamsayı.</summary>
    public static ulong ToUInt(ReadOnlySpan<byte> v)
    {
        if (v.Length > 9 || (v.Length == 9 && v[0] != 0)) throw Corrupt("DER tamsayısı çok büyük");
        ulong x = 0;
        foreach (byte b in v) x = (x << 8) | b;
        return x;
    }

    private static ApfsException Corrupt(string detail) => new(ApfsErrorCode.Corrupt, $"şifreleme anahtarı kaydı: {detail}");
}
