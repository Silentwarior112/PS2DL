using System.Buffers.Binary;
using System.Text;

namespace PS2Iso.Core;

/// <summary>ECMA-167 / UDF 1.02 primitives as emitted by Sony's "DVD-ROM GENERATOR".
/// Constants and encodings verified byte-for-byte against GT3/GT4 masters.</summary>
public static class Udf
{
    public const ushort DescriptorVersion = 2; // NSR02
    public const int LogicalBlockSize = 2048;

    // Descriptor tag identifiers.
    public const ushort TagPvd = 1, TagAvdp = 2, TagVdp = 3, TagIuvd = 4, TagPd = 5,
        TagLvd = 6, TagUsd = 7, TagTd = 8, TagLvid = 9,
        TagFsd = 256, TagFid = 257, TagAed = 258, TagFe = 261;

    // --- Tag checksum (ECMA-167 7.2.3) ---
    public static byte TagChecksum(ReadOnlySpan<byte> tag16)
    {
        int sum = 0;
        for (int i = 0; i < 16; i++)
            if (i != 4)
                sum += tag16[i];
        return (byte)sum;
    }

    // --- CRC-16/CCITT (poly 0x1021, init 0) ---
    private static readonly ushort[] CrcTable = BuildCrcTable();

    private static ushort[] BuildCrcTable()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i << 8;
            for (int j = 0; j < 8; j++)
                c = (c & 0x8000) != 0 ? (c << 1) ^ 0x1021 : c << 1;
            t[i] = (ushort)c;
        }
        return t;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
            crc = (ushort)((crc << 8) ^ CrcTable[((crc >> 8) ^ b) & 0xFF]);
        return crc;
    }

    /// <summary>Finalize a descriptor tag: write CRC over bytes [16, crcLen+16), the checksum,
    /// and the location. The tag id/version/serial must already be set. DVD-ROM GENERATOR always
    /// uses crcLen = 2032 (whole sector tail) for full-sector descriptors.</summary>
    public static void FinalizeTag(Span<byte> sector, ushort tagId, uint location, int crcLen = 2032)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(sector, tagId);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[2..], DescriptorVersion);
        sector[5] = 0;                                   // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(sector[6..], 0); // serial number
        ushort crc = Crc16(sector.Slice(16, crcLen));
        BinaryPrimitives.WriteUInt16LittleEndian(sector[8..], crc);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[10..], (ushort)crcLen);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[12..], location);
        sector[4] = TagChecksum(sector[..16]);
    }

    // --- CS0 charspec (64 bytes): "OSTA Compressed Unicode" ---
    public static void WriteCharspec(Span<byte> dst)
    {
        dst[..64].Clear();
        dst[0] = 0;
        Encoding.ASCII.GetBytes("OSTA Compressed Unicode", dst[1..]);
    }

    /// <summary>OSTA CS0 dstring, 8-bit compression: 0x08 + ASCII chars, zero pad,
    /// last byte of field = used length (1 + char count).</summary>
    public static void WriteDstring(Span<byte> dst, int fieldLen, string value)
    {
        dst[..fieldLen].Clear();
        if (value.Length == 0)
        {
            dst[0] = 8;
            dst[fieldLen - 1] = 1; // compression byte alone (Sony writes len 1, not 0)
            return;
        }
        dst[0] = 8;
        int n = Encoding.ASCII.GetBytes(value, dst[1..fieldLen]);
        dst[fieldLen - 1] = (byte)(1 + n);
    }

    /// <summary>Raw dstring for the volume set identifier: caller supplies the exact body bytes,
    /// we write compression + body + length byte.</summary>
    public static void WriteDstringRaw(Span<byte> dst, int fieldLen, ReadOnlySpan<byte> body)
    {
        dst[..fieldLen].Clear();
        dst[0] = 8;
        body.CopyTo(dst[1..]);
        dst[fieldLen - 1] = (byte)(1 + body.Length);
    }

    // --- Entity identifier (regid, 32 bytes) ---
    public static void WriteRegid(Span<byte> dst, byte flags, string identifier, ReadOnlySpan<byte> suffix)
    {
        dst[..32].Clear();
        dst[0] = flags;
        Encoding.ASCII.GetBytes(identifier, dst[1..24]);
        suffix.CopyTo(dst[24..32]);
    }

    // Fixed regids as observed on the masters.
    public static readonly byte[] SuffixZero = new byte[8];
    public static readonly byte[] SuffixDomain = [0x02, 0x01, 0x03, 0, 0, 0, 0, 0]; // UDF 1.02, hard+soft WP
    public static readonly byte[] SuffixUdf102 = [0x02, 0x01, 0, 0, 0, 0, 0, 0];    // UDF 1.02

    public static void WritePlaystationAppId(Span<byte> dst)
    {
        // "PLAYSTATION" + 12 spaces (space-padded to 23), zero suffix.
        dst[..32].Clear();
        dst[0] = 0;
        var idb = dst.Slice(1, 23);
        idb.Fill((byte)' ');
        Encoding.ASCII.GetBytes("PLAYSTATION", idb);
    }

    public static void WriteGeneratorId(Span<byte> dst) =>
        WriteRegid(dst, 0, "DVD-ROM GENERATOR", SuffixZero);

    public static void WriteLvInfoId(Span<byte> dst) =>
        WriteRegid(dst, 0, "*UDF LV Info", SuffixUdf102);

    public static void WriteDomainId(Span<byte> dst) =>
        WriteRegid(dst, 0, "*OSTA UDF Compliant", SuffixDomain);

    public static void WriteNsr02Id(Span<byte> dst) =>
        WriteRegid(dst, 0x02, "+NSR02", SuffixZero);

    // --- Timestamp (12 bytes): type 1 (local), TZ +540 (JST) ---
    public static void WriteTimestamp(Span<byte> dst, DiscTimestamp t)
    {
        dst[..12].Clear();
        if (t.IsZero)
            return;
        ushort typeTz = (ushort)((1 << 12) | (t.GmtOffset * 15 & 0xFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(dst, typeTz);
        BinaryPrimitives.WriteInt16LittleEndian(dst[2..], (short)t.Year);
        dst[4] = (byte)t.Month;
        dst[5] = (byte)t.Day;
        dst[6] = (byte)t.Hour;
        dst[7] = (byte)t.Minute;
        dst[8] = (byte)t.Second;
        dst[9] = (byte)t.Centiseconds;
    }

    // --- Allocation descriptors ---
    public static void WriteExtentAd(Span<byte> dst, uint length, uint location)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, length);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], location);
    }

    /// <summary>short_ad: 30-bit length + 2-bit type, then logical block position.</summary>
    public static void WriteShortAd(Span<byte> dst, uint length, uint position, int type = 0)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, (length & 0x3FFFFFFF) | ((uint)type << 30));
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], position);
    }

    /// <summary>long_ad: length+type, then lb_num, partition ref, 6 impl-use bytes.</summary>
    public static void WriteLongAd(Span<byte> dst, uint length, uint block, ushort partition,
        int type = 0)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, (length & 0x3FFFFFFF) | ((uint)type << 30));
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], block);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], partition);
        dst.Slice(10, 6).Clear();
    }

    /// <summary>Split a byte length into UDF allocation extents. Each extent's length must be a
    /// multiple of the block size and &lt;= a block-aligned cap below 2^30; the final partial
    /// extent carries the remainder. Sony caps each short_ad at the largest 2048-multiple that
    /// fits in 30 bits and marks the file's whole span with a single "allocated, recorded" run
    /// per cap, using type 2 only where a run continues past the 30-bit boundary.</summary>
    public const uint MaxExtentBytes = 0x3FFFF800; // largest multiple of 2048 that fits in 30 bits
}
