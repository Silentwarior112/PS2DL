using System.Buffers.Binary;
using System.Text;

namespace PS2Iso.Core;

/// <summary>Generates the ISO9660 structures of a volume exactly as Sony's DVDGEN masters do:
/// PVD with PLAYSTATION conventions, terminator, path tables (with the optional copies most
/// tools omit), and directory extents with mastering-order records carrying a 14-byte zeroed
/// system-use area.</summary>
public static class IsoVolumeWriter
{
    /// <summary>Extra zeroed system-use bytes on every directory record (DVDGEN emits 14).</summary>
    public const int SystemUseLength = 14;

    public static int DirectoryRecordLength(int nameLength)
    {
        int len = 33 + nameLength;
        if ((nameLength & 1) == 0)
            len++; // pad byte so name field ends on an even offset
        return len + SystemUseLength;
    }

    /// <summary>ISO9660 directory-record name (without version): the captured ISO name (which may
    /// be an 8.3 truncation of a long filename), else the UDF name uppercased.</summary>
    public static string IsoBaseName(DiscNode node) =>
        node.IsoName.Length > 0 ? node.IsoName : node.Name.ToUpperInvariant();

    /// <summary>Full ISO9660 record name, with the version suffix appended for files.</summary>
    public static string RecordName(DiscNode node) =>
        node.IsDirectory ? IsoBaseName(node) : $"{IsoBaseName(node)};{node.Version}";

    /// <summary>Number of ISO directory records a child needs (>1 only for ≥4 GiB files in
    /// multi-extent mode, which are written as consecutive multi-extent records).</summary>
    public static int RecordCount(DiscNode child, LargeFileMode mode) =>
        child.IsDirectory ? 1 : FileExtentPlanner.ExtentCount(child, mode);

    /// <summary>Exact byte length of a directory's extent content (its ISO size field).</summary>
    public static long DirectoryExtentLength(DiscNode dir, LargeFileMode mode = LargeFileMode.MultiExtent)
    {
        long total = 2 * DirectoryRecordLength(1); // '.' and '..'
        long sectorUsed = total;
        foreach (var child in dir.Children)
        {
            int recLen = DirectoryRecordLength(RecordName(child).Length);
            for (int r = 0; r < RecordCount(child, mode); r++)
            {
                // A record never crosses — nor exactly ends on — a sector boundary; skip to the
                // next sector when it would fill or overflow the current one (DVD-ROM GENERATOR
                // keeps at least an implicit zero terminator at each sector's tail).
                long room = Sectors.Size - sectorUsed % Sectors.Size;
                if (recLen >= room && sectorUsed % Sectors.Size != 0)
                {
                    total += room;
                    sectorUsed += room;
                }
                total += recLen;
                sectorUsed += recLen;
            }
        }
        return total;
    }

    public static byte[] BuildPvd(VolumeLayout layout)
    {
        var vol = layout.Volume;
        var id = vol.Identity;
        var b = new byte[Sectors.Size];
        b[0] = 1;
        "CD001"u8.CopyTo(b.AsSpan(1));
        b[6] = 1;
        BinUtil.WritePaddedString(b.AsSpan(8, 32), id.SystemIdentifier);
        BinUtil.WritePaddedString(b.AsSpan(40, 32), id.VolumeIdentifier);
        BinUtil.WriteBoth32(b.AsSpan(80), (uint)layout.TotalSectors);
        BinUtil.WriteBoth16(b.AsSpan(120), 1); // volume set size
        BinUtil.WriteBoth16(b.AsSpan(124), 1); // volume sequence number
        BinUtil.WriteBoth16(b.AsSpan(128), Sectors.Size);
        uint pathTableSize = (uint)PathTableLength(layout.Directories);
        BinUtil.WriteBoth32(b.AsSpan(132), pathTableSize);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(140), 257); // L
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(144), 258); // L optional copy
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(148), 259);    // M
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(152), 260);    // M optional copy
        WriteDirectoryRecord(b.AsSpan(156, 34), layout.Volume.Root,
            overrideName: "\0", includeSystemUse: false);
        BinUtil.WritePaddedString(b.AsSpan(190, 128), id.VolumeSetIdentifier);
        BinUtil.WritePaddedString(b.AsSpan(318, 128), id.PublisherIdentifier);
        BinUtil.WritePaddedString(b.AsSpan(446, 128), id.DataPreparerIdentifier);
        BinUtil.WritePaddedString(b.AsSpan(574, 128), id.ApplicationIdentifier);
        BinUtil.WritePaddedString(b.AsSpan(702, 37), id.CopyrightFileIdentifier);
        BinUtil.WritePaddedString(b.AsSpan(739, 37), id.AbstractFileIdentifier);
        BinUtil.WritePaddedString(b.AsSpan(776, 37), id.BibliographicFileIdentifier);
        BinUtil.WriteVolumeDate(b.AsSpan(813, 17), vol.CreationDate);
        BinUtil.WriteVolumeDate(b.AsSpan(830, 17), DiscTimestamp.Zero); // modification
        BinUtil.WriteVolumeDate(b.AsSpan(847, 17), DiscTimestamp.Zero); // expiration
        BinUtil.WriteVolumeDate(b.AsSpan(864, 17), DiscTimestamp.Zero); // effective
        b[881] = 1; // file structure version
        return b;
    }

    public static byte[] BuildTerminator()
    {
        var b = new byte[Sectors.Size];
        b[0] = 255;
        "CD001"u8.CopyTo(b.AsSpan(1));
        b[6] = 1;
        return b;
    }

    public static int PathTableLength(List<DiscNode> directories)
    {
        int total = 0;
        foreach (var d in directories)
        {
            int nameLen = d.Parent is null ? 1 : IsoBaseName(d).Length;
            total += 8 + nameLen + (nameLen & 1);
        }
        return total;
    }

    public static byte[] BuildPathTable(List<DiscNode> directories, bool bigEndian)
    {
        var b = new byte[Sectors.AlignUp(PathTableLength(directories), Sectors.Size)];
        var index = new Dictionary<DiscNode, ushort>();
        for (int i = 0; i < directories.Count; i++)
            index[directories[i]] = (ushort)(i + 1);

        int off = 0;
        foreach (var d in directories)
        {
            bool isRoot = d.Parent is null;
            int nameLen = isRoot ? 1 : IsoBaseName(d).Length;
            b[off] = (byte)nameLen;
            b[off + 1] = 0; // extended attribute record length
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(off + 2), (uint)d.Lba);
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(off + 6), index[isRoot ? d : d.Parent!]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off + 2), (uint)d.Lba);
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off + 6), index[isRoot ? d : d.Parent!]);
            }
            if (isRoot)
                b[off + 8] = 0;
            else
                Encoding.ASCII.GetBytes(IsoBaseName(d), b.AsSpan(off + 8, nameLen));
            off += 8 + nameLen + (nameLen & 1);
        }
        return b;
    }

    public static byte[] BuildDirectoryExtent(DiscNode dir, LargeFileMode mode = LargeFileMode.MultiExtent)
    {
        var b = new byte[Sectors.AlignUp(dir.Size, Sectors.Size)];
        int off = 0;
        off += WriteDirectoryRecord(b.AsSpan(off), dir, overrideName: "\0");
        off += WriteDirectoryRecord(b.AsSpan(off), dir.Parent ?? dir, overrideName: "\x01");
        foreach (var child in dir.Children)
        {
            int recLen = DirectoryRecordLength(RecordName(child).Length);
            var extents = child.IsDirectory
                ? [new FileExtent(child.Lba, child.Size)]
                : FileExtentPlanner.Plan(child.Lba, child.Size, mode);
            for (int e = 0; e < extents.Count; e++)
            {
                int room = Sectors.Size - off % Sectors.Size;
                if (recLen >= room && off % Sectors.Size != 0)
                    off += room; // zero padding; a record never crosses nor exactly ends a sector
                bool notLast = e < extents.Count - 1;
                // Clamp the 32-bit size field for a single-extent file that exceeds it.
                long sizeField = child.IsDirectory
                    ? child.Size
                    : FileExtentPlanner.SizeField(extents[e].Length);
                off += WriteDirectoryRecord(b.AsSpan(off), child,
                    extentLba: extents[e].Lba, extentSize: sizeField, multiExtent: notLast);
            }
        }
        return b;
    }

    /// <summary>Writes one directory record; returns its length. For multi-extent files the caller
    /// supplies the per-extent LBA/size and sets <paramref name="multiExtent"/> on all but the last.</summary>
    private static int WriteDirectoryRecord(Span<byte> b, DiscNode node,
        string? overrideName = null, bool includeSystemUse = true,
        long? extentLba = null, long? extentSize = null, bool multiExtent = false)
    {
        string name = overrideName ?? RecordName(node);
        int recLen = 33 + name.Length + (1 - name.Length % 2);
        if (includeSystemUse)
            recLen += SystemUseLength;

        b[0] = (byte)recLen;
        b[1] = 0; // extended attribute record length
        BinUtil.WriteBoth32(b[2..], (uint)(extentLba ?? node.Lba));
        BinUtil.WriteBoth32(b[10..], (uint)(extentSize ?? node.Size));
        BinUtil.WriteRecordDate(b[18..], node.Timestamp);
        byte flags = node.IsDirectory ? (byte)2 : (byte)0;
        if (multiExtent) flags |= 0x80; // more extents follow (ISO 9660 multi-extent file)
        b[25] = flags;
        b[26] = 0; // file unit size
        b[27] = 0; // interleave gap
        BinUtil.WriteBoth16(b[28..], 1); // volume sequence number
        b[32] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name, b.Slice(33, name.Length));
        // remaining bytes (pad + system use) are already zero
        return recLen;
    }
}
