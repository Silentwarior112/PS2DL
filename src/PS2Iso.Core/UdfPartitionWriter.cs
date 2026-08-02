using System.Buffers.Binary;
using System.Text;

namespace PS2Iso.Core;

/// <summary>Emits the UDF partition metadata exactly as DVD-ROM GENERATOR does: File Set
/// Descriptor, its terminating descriptor, directory FID data, and one File Entry per object.
/// All constants verified byte-for-byte against GT3/GT4 masters (analysis/rules_udf_partition).</summary>
public sealed class UdfPartitionWriter
{
    private readonly VolumeLayout _layout;

    public UdfPartitionWriter(VolumeLayout layout)
    {
        _layout = layout;
        if (EaTemplate.Length != ExtendedAttrLength)
            throw new InvalidOperationException(
                $"EA template is {EaTemplate.Length} bytes, expected {ExtendedAttrLength}.");
    }

    // ---- constants observed on every FE of every disc ----
    public const int FeFixedLength = 176;      // FE header through L_AD
    public const int ExtendedAttrLength = 132;
    public const int ShortAdLength = 8;
    public const ushort IcbFlags = 0x0630;     // short_ad + archive/contiguous/system/non-reloc bits
    public const uint OwnerNone = 0xFFFFFFFF;
    public const uint Permissions = 0x14A5;

    private LargeFileMode LargeFiles => _layout.Volume.LargeFiles;

    /// <summary>Number of allocation descriptors (short_ads) an object's FE holds: 1 for
    /// directories and &lt;4 GiB files (and single-extent large files), one per ~1 GiB extent for
    /// multi-extent large files.</summary>
    public static int AdCount(DiscNode node, LargeFileMode mode) =>
        node.IsDirectory ? 1 : FileExtentPlanner.ExtentCount(node, mode);

    /// <summary>Total used bytes of an object's FE descriptor (fixed part + EA + its ADs).</summary>
    public static int FeUsedLength(DiscNode node, LargeFileMode mode) =>
        FeFixedLength + ExtendedAttrLength + ShortAdLength * AdCount(node, mode);

    /// <summary>Constant 132-byte Extended Attribute template (EAHD + "*UDF FreeEASpace" +
    /// "*UDF DVD CGMS Info"), copied verbatim from the GT masters; only the EAHD tag location
    /// (offset 12) and checksum (offset 4) are patched per FE.</summary>
    private static readonly byte[] EaTemplate = Convert.FromHexString(
        "0601020000000000a734080000000000180000008400000000080000010000003400000004000000002a5544462046726565454153706163650000000000000002010000000000006105000000080000010000003800000008000000002a554446204456442043474d5320496e666f000000000002010000000000004905000000000000");

    // ---------- sizing ----------

    public static int FidRecordLength(int nameChars)
    {
        int lfi = nameChars == 0 ? 0 : 1 + 2 * nameChars; // 16-bit unicode: comp byte + 2/char
        int total = 38 + lfi;
        return (total + 3) / 4 * 4;
    }

    /// <summary>Byte length of a directory's FID data (parent FID + one FID per child).</summary>
    public static long DirectoryFidLength(DiscNode dir)
    {
        long len = FidRecordLength(0); // parent entry
        foreach (var child in dir.Children)
            len += FidRecordLength(child.Name.Length);
        return len;
    }

    // ---------- FSD ----------

    public byte[] BuildFsd()
    {
        var s = new byte[Sectors.Size];
        Udf.WriteTimestamp(s.AsSpan(16, 12), _layout.Volume.CreationDate);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(28), 3); // interchange level
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(30), 3); // max interchange level
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(32), 1); // charset list
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(36), 1); // max charset list
        // fileset number (40) + FSD number (44) = 0
        Udf.WriteCharspec(s.AsSpan(48, 64));
        string udfName = string.IsNullOrEmpty(_layout.Volume.Identity.UdfVolumeIdentifier)
            ? _layout.Volume.Identity.VolumeIdentifier : _layout.Volume.Identity.UdfVolumeIdentifier;
        Udf.WriteDstring(s.AsSpan(112, 128), 128, udfName);
        Udf.WriteCharspec(s.AsSpan(240, 64));
        Udf.WriteDstring(s.AsSpan(304, 32), 32, "PLAYSTATION2 DVD-ROM FILE SET");
        // copyright (336) + abstract (368) file identifiers: empty
        var root = _layout.Volume.Root;
        long rootFeBlock = _layout.FeBlocks[root];
        Udf.WriteLongAd(s.AsSpan(400, 16), (uint)FeUsedLength(root, LargeFiles), (uint)rootFeBlock, 0);
        Udf.WriteDomainId(s.AsSpan(416, 32));
        Udf.FinalizeTag(s, Udf.TagFsd, 0);
        return s;
    }

    public byte[] BuildPartitionTerminator()
    {
        var s = new byte[Sectors.Size];
        // Quirk: this TD carries an ABSOLUTE tag location (partitionStart + 1), unlike the FSD.
        Udf.FinalizeTag(s, Udf.TagTd, (uint)(_layout.PartitionStart + 1));
        return s;
    }

    // ---------- directory FID data ----------

    public byte[] BuildDirectoryFids(DiscNode dir)
    {
        long fidLen = _layout.FidLengths[dir];
        var buf = new byte[Sectors.AlignUp(fidLen, Sectors.Size)];
        uint startBlock = (uint)_layout.FidBlocks[dir];
        int off = 0;

        // Each FID's tag location is the partition block of the SECTOR its tag starts in — so for a
        // directory whose FID data spans several sectors, later FIDs carry a higher location.
        // Parent entry: characteristics 0x0a (directory + parent), no name. DVDGEN quirk: the
        // parent FID's ICB points at the directory's OWN FE, not the actual parent's FE.
        off += WriteFid(buf.AsSpan(off), (uint)(startBlock + off / Sectors.Size), 0x0A, "",
            _layout.FeBlocks[dir], FeUsedLength(dir, LargeFiles));

        foreach (var child in dir.Children)
        {
            byte ch = child.IsDirectory ? (byte)0x02 : (byte)0x00;
            off += WriteFid(buf.AsSpan(off), (uint)(startBlock + off / Sectors.Size), ch,
                child.Name, _layout.FeBlocks[child], FeUsedLength(child, LargeFiles));
        }
        return buf;
    }

    private static int WriteFid(Span<byte> b, uint tagLoc, byte characteristics, string name,
        long feBlock, int feUsedLength)
    {
        int nameChars = name.Length;
        int lfi = nameChars == 0 ? 0 : 1 + 2 * nameChars;
        int total = 38 + lfi;
        int recLen = (total + 3) / 4 * 4;

        BinaryPrimitives.WriteUInt16LittleEndian(b[16..], 1);   // file version number
        b[18] = characteristics;
        b[19] = (byte)lfi;
        Udf.WriteLongAd(b.Slice(20, 16), (uint)feUsedLength, (uint)feBlock, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b[36..], 0);   // length of implementation use
        if (lfi > 0)
        {
            b[38] = 0x10; // OSTA CS0 compression id 16 (UTF-16BE)
            for (int i = 0; i < nameChars; i++)
                BinaryPrimitives.WriteUInt16BigEndian(b[(39 + 2 * i)..], name[i]);
        }
        // pad bytes (already zero)
        Udf.FinalizeTag(b, Udf.TagFid, tagLoc, recLen - 16);
        return recLen;
    }

    // ---------- File Entry ----------

    public byte[] BuildFileEntry(DiscNode node)
    {
        var s = new byte[Sectors.Size];
        bool isDir = node.IsDirectory;
        long feBlock = _layout.FeBlocks[node];

        // ICB tag (offset 16, 20 bytes)
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(20), 4); // strategy type 4
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(24), 1); // max entries
        s[27] = isDir ? (byte)4 : (byte)5;                         // file type
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(34), IcbFlags);

        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(36), OwnerNone); // uid
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(40), OwnerNone); // gid
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(44), Permissions);
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(48), (ushort)LinkCount(node));
        // record format(50)/display(51)/length(52) = 0

        long infoLen = isDir ? _layout.FidLengths[node] : node.Size;
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(56), (ulong)infoLen);
        long blocks = (infoLen + Sectors.Size - 1) / Sectors.Size;
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(64), (ulong)blocks);

        Udf.WriteTimestamp(s.AsSpan(72, 12), node.Timestamp);  // access
        Udf.WriteTimestamp(s.AsSpan(84, 12), node.Timestamp);  // modification
        Udf.WriteTimestamp(s.AsSpan(96, 12), node.Timestamp);  // attribute
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(108), 1); // checkpoint
        // ea_icb (112) zero
        Udf.WriteGeneratorId(s.AsSpan(128, 32));
        int adCount = AdCount(node, LargeFiles);
        BinaryPrimitives.WriteUInt64LittleEndian(s.AsSpan(160), (ulong)UniqueId(node));
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(168), ExtendedAttrLength);
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(172), (uint)(ShortAdLength * adCount));

        // Extended attributes (offset 176): constant template with EAHD tag patched.
        var ea = s.AsSpan(176, ExtendedAttrLength);
        EaTemplate.CopyTo(ea);
        BinaryPrimitives.WriteUInt32LittleEndian(ea[12..], (uint)feBlock); // EAHD tag location
        ea[4] = Udf.TagChecksum(ea[..16]);

        // Allocation descriptors (offset 176 + 132 = 308).
        int adOffset = 176 + ExtendedAttrLength;
        long dataBlock = isDir ? _layout.FidBlocks[node] : (node.Lba - _layout.PartitionStart);
        if (adCount == 1)
        {
            // DVD-ROM GENERATOR stores the RAW 32-bit byte length in the length+type word, so
            // <4 GiB files >1 GiB overflow into the 2-bit type field — reproduced exactly.
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(adOffset), (uint)infoLen);
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(adOffset + 4), (uint)dataBlock);
        }
        else
        {
            // ≥4 GiB file in multi-extent mode: one proper short_ad per contiguous ~1 GiB extent.
            var extents = FileExtentPlanner.Plan(node.Lba, node.Size, LargeFiles);
            for (int i = 0; i < extents.Count; i++)
            {
                long relBlock = extents[i].Lba - _layout.PartitionStart;
                Udf.WriteShortAd(s.AsSpan(adOffset + i * ShortAdLength, ShortAdLength),
                    (uint)extents[i].Length, (uint)relBlock);
            }
        }

        Udf.FinalizeTag(s, Udf.TagFe, (uint)feBlock, FeUsedLength(node, LargeFiles) - 16);
        return s;
    }

    /// <summary>UDF File Entry link count: the captured source value when present, else derived
    /// (directory = 1 + immediate subdirectories; file = 1).</summary>
    private static int LinkCount(DiscNode node) =>
        node.UdfLinkCount >= 0 ? node.UdfLinkCount
        : node.IsDirectory ? 1 + node.Children.Count(c => c.IsDirectory) : 1;

    /// <summary>UDF unique id: root = 0; the k-th object after root (FE order) = 15 + k.</summary>
    private long UniqueId(DiscNode node)
    {
        if (ReferenceEquals(node, _layout.Volume.Root))
            return 0;
        long index = 0;
        foreach (var dir in _layout.Directories)
        {
            if (ReferenceEquals(dir, _layout.Volume.Root)) continue;
            index++;
            if (ReferenceEquals(dir, node)) return 15 + index;
        }
        foreach (var file in _layout.Files)
        {
            index++;
            if (ReferenceEquals(file, node)) return 15 + index;
        }
        throw new InvalidOperationException($"Node {node.FullPath} not in layout.");
    }
}
