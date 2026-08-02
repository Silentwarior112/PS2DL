using System.Buffers.Binary;
using System.Text;

namespace PS2Iso.Core;

/// <summary>Emits the UDF 1.02 structures OUTSIDE the partition exactly as Sony's DVD-ROM
/// GENERATOR does: VRS (18-20), main VDS (32-37), reserve VDS (48-53), LVID (64), TD (65),
/// AVDP (256) and the closing AVDP (N-1). Every constant here was verified byte-for-byte
/// against GT3/GT4 masters (see analysis/rules_udf_vds.md).</summary>
public sealed class UdfVolumeWriter
{
    private readonly VolumeSpec _vol;
    private readonly long _partitionStart;
    private readonly long _partitionLength;
    private readonly long _totalSectors;
    private readonly int _numDirs;
    private readonly int _numFiles;

    public UdfVolumeWriter(VolumeSpec vol, VolumeLayout layout)
    {
        _vol = vol;
        _partitionStart = layout.PartitionStart;
        _partitionLength = layout.PartitionLength;
        _totalSectors = layout.TotalSectors;
        _numDirs = layout.Directories.Count;
        _numFiles = layout.Files.Count;
    }

    /// <summary>8-char volume-set-identifier prefix (chars 0x30-0x3F), from the volume identity.</summary>
    public string UniquePrefix => string.IsNullOrEmpty(_vol.Identity.UdfUniquePrefix)
        ? "00000000" : _vol.Identity.UdfUniquePrefix;

    /// <summary>UDF logical-volume name (may differ in case from the ISO id).</summary>
    private string UdfName => string.IsNullOrEmpty(_vol.Identity.UdfVolumeIdentifier)
        ? _vol.Identity.VolumeIdentifier : _vol.Identity.UdfVolumeIdentifier;

    public void WriteVrs(Span<byte> s18, Span<byte> s19, Span<byte> s20)
    {
        WriteVrsSector(s18, "BEA01");
        WriteVrsSector(s19, "NSR02");
        WriteVrsSector(s20, "TEA01");
    }

    private static void WriteVrsSector(Span<byte> s, string id)
    {
        s.Clear();
        s[0] = 0;
        Encoding.ASCII.GetBytes(id, s[1..6]);
        s[6] = 1;
    }

    /// <summary>Write the six main-VDS sectors at their locations (32..37).</summary>
    public void WriteMainVds(Span<byte> pvd, Span<byte> iuvd, Span<byte> pd, Span<byte> lvd,
        Span<byte> usd, Span<byte> td, uint baseLoc = 32)
    {
        WritePvd(pvd, baseLoc + 0);
        WriteIuvd(iuvd, baseLoc + 1);
        WritePd(pd, baseLoc + 2);
        WriteLvd(lvd, baseLoc + 3);
        WriteUsd(usd, baseLoc + 4);
        WriteTd(td, baseLoc + 5);
    }

    public void WritePvd(Span<byte> s, uint loc)
    {
        s.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0); // VDS number
        BinaryPrimitives.WriteUInt32LittleEndian(s[20..], 0); // primary VD number
        Udf.WriteDstring(s.Slice(24, 32), 32, UdfName);
        BinaryPrimitives.WriteUInt16LittleEndian(s[56..], 1); // volume sequence number
        BinaryPrimitives.WriteUInt16LittleEndian(s[58..], 1); // max volume sequence number
        BinaryPrimitives.WriteUInt16LittleEndian(s[60..], 2); // interchange level
        BinaryPrimitives.WriteUInt16LittleEndian(s[62..], 2); // max interchange level
        BinaryPrimitives.WriteUInt32LittleEndian(s[64..], 1); // charset list
        BinaryPrimitives.WriteUInt32LittleEndian(s[68..], 1); // max charset list
        WriteVolumeSetId(s.Slice(72, 128));
        Udf.WriteCharspec(s.Slice(200, 64));
        Udf.WriteCharspec(s.Slice(264, 64));
        // volume abstract (328) + copyright (336) extents already zero
        Udf.WritePlaystationAppId(s.Slice(344, 32));
        Udf.WriteTimestamp(s.Slice(376, 12), _vol.CreationDate);
        Udf.WriteGeneratorId(s.Slice(388, 32));
        // implementation use (420) zero, predecessor (484) zero, flags (488) zero
        Udf.FinalizeTag(s, Udf.TagPvd, loc);
    }

    private void WriteVolumeSetId(Span<byte> field)
    {
        // Prefer the captured body verbatim (reproduces per-title tails exactly); otherwise build
        // the GT-style default: UNIQ8 (8) + "SCEI    " (8) + VOLID space-padded to 32 => 48 bytes.
        var captured = _vol.Identity.UdfVolumeSetBody;
        if (!string.IsNullOrEmpty(captured))
        {
            Udf.WriteDstringRaw(field, 128, Encoding.Latin1.GetBytes(captured));
            return;
        }
        Span<byte> body = stackalloc byte[48];
        body.Fill((byte)' ');
        Encoding.ASCII.GetBytes(UniquePrefix.PadRight(8).AsSpan(0, 8), body[..8]);
        Encoding.ASCII.GetBytes("SCEI    ", body.Slice(8, 8));
        var name = UdfName;
        Encoding.ASCII.GetBytes(name.Length > 32 ? name[..32] : name, body.Slice(16, 32));
        Udf.WriteDstringRaw(field, 128, body);
    }

    public void WriteIuvd(Span<byte> s, uint loc)
    {
        s.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 1); // VDS number
        Udf.WriteLvInfoId(s.Slice(20, 32));
        Udf.WriteCharspec(s.Slice(52, 64));
        Udf.WriteDstring(s.Slice(116, 128), 128, UdfName);
        Udf.WriteDstring(s.Slice(244, 36), 36, "");
        Udf.WriteDstring(s.Slice(280, 36), 36, "");
        Udf.WriteDstring(s.Slice(316, 36), 36, "");
        Udf.WriteGeneratorId(s.Slice(352, 32));
        Udf.FinalizeTag(s, Udf.TagIuvd, loc);
    }

    public void WritePd(Span<byte> s, uint loc)
    {
        s.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 2); // VDS number
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], 1); // partition flags: allocated
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], 0); // partition number
        Udf.WriteNsr02Id(s.Slice(24, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(s[184..], 1); // access type: read-only
        BinaryPrimitives.WriteUInt32LittleEndian(s[188..], (uint)_partitionStart);
        BinaryPrimitives.WriteUInt32LittleEndian(s[192..], (uint)_partitionLength);
        Udf.WriteGeneratorId(s.Slice(196, 32));
        Udf.FinalizeTag(s, Udf.TagPd, loc);
    }

    public void WriteLvd(Span<byte> s, uint loc)
    {
        s.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 3); // VDS number
        Udf.WriteCharspec(s.Slice(20, 64));
        Udf.WriteDstring(s.Slice(84, 128), 128, UdfName);
        BinaryPrimitives.WriteUInt32LittleEndian(s[212..], Udf.LogicalBlockSize);
        Udf.WriteDomainId(s.Slice(216, 32));
        // contents use = FSD long_ad: extent len 4096 (FSD+TD), block 0, partition 0
        Udf.WriteLongAd(s.Slice(248, 16), 4096, 0, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[264..], 6); // map table length
        BinaryPrimitives.WriteUInt32LittleEndian(s[268..], 1); // number of partition maps
        Udf.WriteGeneratorId(s.Slice(272, 32));
        Udf.WriteExtentAd(s.Slice(432, 8), 4096, 64); // integrity sequence extent: sectors 64-65
        // partition map 1 (type 1): 01 06 0001 0000
        s[440] = 1; s[441] = 6;
        BinaryPrimitives.WriteUInt16LittleEndian(s[442..], 1); // volume sequence number
        BinaryPrimitives.WriteUInt16LittleEndian(s[444..], 0); // partition number
        Udf.FinalizeTag(s, Udf.TagLvd, loc);
    }

    public void WriteUsd(Span<byte> s, uint loc)
    {
        s.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 4); // number of allocation descriptors
        // four zero extent_ads at 20..51 (already zero)
        Udf.FinalizeTag(s, Udf.TagUsd, loc);
    }

    public void WriteTd(Span<byte> s, uint loc)
    {
        s.Clear();
        Udf.FinalizeTag(s, Udf.TagTd, loc);
    }

    public void WriteLvid(Span<byte> s, uint loc = 64)
    {
        s.Clear();
        Udf.WriteTimestamp(s.Slice(16, 12), _vol.CreationDate);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], 1); // integrity type: close
        // next integrity extent (32) zero
        // logical volume contents use: uniqueID = 0x00000000FFFFFFFF
        BinaryPrimitives.WriteUInt64LittleEndian(s[40..], 0xFFFFFFFFUL);
        BinaryPrimitives.WriteUInt32LittleEndian(s[72..], 1);  // number of partitions
        BinaryPrimitives.WriteUInt32LittleEndian(s[76..], 48); // impl use length
        BinaryPrimitives.WriteUInt32LittleEndian(s[80..], 0);  // free space table[0]: full
        BinaryPrimitives.WriteUInt32LittleEndian(s[84..], (uint)_partitionLength); // size table[0]
        Udf.WriteGeneratorId(s.Slice(88, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(s[120..], (uint)_numFiles);
        BinaryPrimitives.WriteUInt32LittleEndian(s[124..], (uint)_numDirs);
        BinaryPrimitives.WriteUInt16LittleEndian(s[128..], 0x0102); // min read
        BinaryPrimitives.WriteUInt16LittleEndian(s[130..], 0x0102); // min write
        BinaryPrimitives.WriteUInt16LittleEndian(s[132..], 0x0102); // max write
        Udf.FinalizeTag(s, Udf.TagLvid, loc);
    }

    public void WriteAvdp(Span<byte> s, uint loc)
    {
        s.Clear();
        Udf.WriteExtentAd(s.Slice(16, 8), 32768, 32); // main VDS: 16 sectors at 32
        Udf.WriteExtentAd(s.Slice(24, 8), 32768, 48); // reserve VDS: 16 sectors at 48
        Udf.FinalizeTag(s, Udf.TagAvdp, loc);
    }

    /// <summary>Reserve VDS (48-53) is a byte copy of the main VDS with each tag's location and
    /// checksum adjusted. Given the already-built main sector, produce the reserve sector.</summary>
    public static byte[] MakeReserveCopy(ReadOnlySpan<byte> mainSector, uint newLocation)
    {
        var copy = mainSector.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(12), newLocation);
        copy[4] = Udf.TagChecksum(copy.AsSpan(0, 16));
        return copy;
    }
}
