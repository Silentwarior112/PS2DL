namespace PS2Iso.Core;

/// <summary>Computed sector placement for one volume. Mirrors the fixed Sony DVDGEN template:
/// <code>
///   0-15    system area            16 PVD           17 terminator
///   18-20   UDF VRS (BEA01/NSR02/TEA01)
///   32-37   UDF main VDS           48-53 reserve VDS
///   64      LVID                   65 TD             256 AVDP
///   257-260 path tables (L, L copy, M, M copy)
///   261+    one extent per directory (path-table order)
///   part+0  FSD    part+1 TD
///   part+2+ FID data extents (one run per directory, in directory order)
///   then    one FE sector per object
///   then    file data packed in layout order
///   tail    zero padding, closing AVDP in the very last sector
/// </code></summary>
public sealed class VolumeLayout
{
    public required VolumeSpec Volume { get; init; }

    /// <summary>Directories in path-table order (root first). Each has Lba/Size assigned.</summary>
    public required List<DiscNode> Directories { get; init; }

    /// <summary>Files in physical placement order, LBAs assigned.</summary>
    public required List<DiscNode> Files { get; init; }

    /// <summary>UDF partition start sector (== first sector after the last directory extent).</summary>
    public long PartitionStart { get; init; }

    /// <summary>Partition-relative block of each directory's FID data extent.</summary>
    public required Dictionary<DiscNode, long> FidBlocks { get; init; }

    /// <summary>Byte length of each directory's FID data (the UDF directory "file" size).</summary>
    public required Dictionary<DiscNode, long> FidLengths { get; init; }

    /// <summary>Partition-relative block of each object's File Entry sector.
    /// Keyed by node; the root directory is included.</summary>
    public required Dictionary<DiscNode, long> FeBlocks { get; init; }

    /// <summary>First absolute sector of file data.</summary>
    public long DataStart { get; init; }

    /// <summary>First absolute sector after the last file's data.</summary>
    public long DataEnd { get; init; }

    /// <summary>Total sectors in this volume (== ISO volume_space_size, includes closing AVDP).</summary>
    public long TotalSectors { get; init; }

    /// <summary>True if the source LBA pins could not be honored (they conflicted with the computed
    /// metadata layout) and files were repacked contiguously — a valid disc, but not byte-exact.</summary>
    public bool Repacked { get; init; }

    /// <summary>Partition length in sectors. The partition runs to N-2 inclusive; sector N-1
    /// holds the closing AVDP and is not part of the partition.</summary>
    public long PartitionLength => TotalSectors - 1 - PartitionStart;
}
