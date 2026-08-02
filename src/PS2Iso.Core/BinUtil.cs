using System.Buffers.Binary;
using System.Text;

namespace PS2Iso.Core;

/// <summary>Encoding helpers for ISO9660/ECMA-119 on-disc primitives.</summary>
public static class BinUtil
{
    /// <summary>Both-endian 32-bit (ECMA-119 7.3.3): little then big.</summary>
    public static void WriteBoth32(Span<byte> dst, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, value);
        BinaryPrimitives.WriteUInt32BigEndian(dst[4..], value);
    }

    /// <summary>Both-endian 16-bit (ECMA-119 7.2.3).</summary>
    public static void WriteBoth16(Span<byte> dst, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dst, value);
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..], value);
    }

    /// <summary>Space-padded ASCII field (a-characters / d-characters).</summary>
    public static void WritePaddedString(Span<byte> dst, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length > dst.Length)
            throw new ArgumentException($"'{value}' exceeds field width {dst.Length}.");
        bytes.CopyTo(dst);
        dst[bytes.Length..].Fill((byte)' ');
    }

    /// <summary>17-byte volume descriptor date ("YYYYMMDDHHMMSScc" + tz offset).
    /// Zero timestamps are ASCII '0' x16 with tz byte 0 (as on Sony masters).</summary>
    public static void WriteVolumeDate(Span<byte> dst, DiscTimestamp t)
    {
        string text = t.IsZero
            ? "0000000000000000"
            : $"{t.Year:D4}{t.Month:D2}{t.Day:D2}{t.Hour:D2}{t.Minute:D2}{t.Second:D2}{t.Centiseconds:D2}";
        Encoding.ASCII.GetBytes(text, dst[..16]);
        dst[16] = t.IsZero ? (byte)0 : (byte)t.GmtOffset;
    }

    /// <summary>7-byte directory record date (ECMA-119 9.1.5).</summary>
    public static void WriteRecordDate(Span<byte> dst, DiscTimestamp t)
    {
        if (t.IsZero)
        {
            dst[..7].Clear();
            return;
        }
        dst[0] = (byte)(t.Year - 1900);
        dst[1] = (byte)t.Month;
        dst[2] = (byte)t.Day;
        dst[3] = (byte)t.Hour;
        dst[4] = (byte)t.Minute;
        dst[5] = (byte)t.Second;
        dst[6] = (byte)t.GmtOffset;
    }
}
