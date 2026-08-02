using System.IO.Compression;

namespace PS2Iso.Core;

/// <summary>The 32 KiB system area (sectors 0-15) of a retail PS2 disc holds the encrypted
/// "PlayStation 2" boot logo. The 24 KiB plaintext is IDENTICAL on every retail disc; each master
/// differs only by a single per-disc key byte (sector 0, byte 0), and sectors 12-15 are zero.
/// So instead of storing an opaque 32 KiB blob per image we embed the shared plaintext once and
/// regenerate the area from just that key byte.
///
/// cipher = ROL8(plain, key &amp; 7) ^ key   (per byte, sectors 0-11);  sectors 12-15 = 0.
/// Verified to reproduce GT3/GT4/DMC/Champions/ATV4 system areas byte-for-byte.</summary>
public static class SystemArea
{
    public const int Bytes = 16 * Sectors.Size;   // 32768
    private const int LogoBytes = 12 * Sectors.Size; // 24576 encrypted plaintext region

    // gzip+base64 of the 24 KiB boot-logo plaintext (md5 10fc4999dc20129bfa206bacbfbeb3d7).
    private const string PlaintextGzB64 =
        "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";

    private static readonly Lazy<byte[]> Plaintext = new(() =>
    {
        using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(PlaintextGzB64)),
            CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    });

    /// <summary>Regenerate the full 32 KiB system area for a given per-disc key byte.</summary>
    public static byte[] Generate(byte key)
    {
        var plain = Plaintext.Value;
        var area = new byte[Bytes];
        int r = key & 7;
        for (int i = 0; i < LogoBytes; i++)
        {
            int rol = ((plain[i] << r) | (plain[i] >> (8 - r))) & 0xFF;
            area[i] = (byte)(rol ^ key);
        }
        return area; // sectors 12-15 stay zero
    }

    /// <summary>If <paramref name="area"/> is a boot-logo system area that regenerates exactly from
    /// its key byte, return that key; otherwise null (an atypical area must be stored verbatim).</summary>
    public static byte? DetectKey(byte[]? area)
    {
        if (area is null || area.Length != Bytes)
            return null;
        byte key = area[0];
        return Generate(key).AsSpan().SequenceEqual(area) ? key : null;
    }
}
