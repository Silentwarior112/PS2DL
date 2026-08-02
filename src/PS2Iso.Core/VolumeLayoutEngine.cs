namespace PS2Iso.Core;

/// <summary>Assigns every sector position in a volume following the exact DVD-ROM GENERATOR
/// template, then computes volume_space_size. Deterministic from the tree + file sizes + the
/// physical placement order.</summary>
public static class VolumeLayoutEngine
{
    public const long PathTableL = 257, PathTableLCopy = 258, PathTableM = 259, PathTableMCopy = 260;
    public const long FirstDirectoryLba = 261;

    /// <summary>Compute sector placement. When <paramref name="ignorePins"/> is true, all files
    /// are packed contiguously in layout order regardless of any recorded LBA (used when a disc
    /// has been edited — e.g. a payload file resized for a spanning build).</summary>
    public static VolumeLayout Compute(VolumeSpec vol, bool ignorePins = false)
    {
        if (ignorePins)
            foreach (var file in vol.Files())
                file.Lba = -1;
        // Root timestamp anchors the PVD creation date.
        if (vol.Root.Timestamp.IsZero)
            vol.Root.Timestamp = vol.CreationDate;
        else if (vol.CreationDate.IsZero)
            vol.CreationDate = vol.Root.Timestamp;

        var directories = vol.DirectoriesInPathTableOrder().ToList();

        // 1. ISO directory extents: one sector each, path-table order from 261.
        long lba = FirstDirectoryLba;
        foreach (var dir in directories)
        {
            dir.Lba = lba;
            dir.Size = IsoVolumeWriter.DirectoryExtentLength(dir, vol.LargeFiles);
            long sectors = Sectors.AlignUp(dir.Size, Sectors.Size) / Sectors.Size;
            lba += sectors;
        }

        long partitionStart = lba;

        // 2. UDF partition metadata: FSD (part-rel 0), TD (1), FID extents (one per dir), FEs.
        long partBlock = 2; // after FSD + TD
        var fidBlocks = new Dictionary<DiscNode, long>();
        var fidLengths = new Dictionary<DiscNode, long>();
        foreach (var dir in directories)
        {
            long fidLen = UdfPartitionWriter.DirectoryFidLength(dir);
            fidLengths[dir] = fidLen;
            fidBlocks[dir] = partBlock;
            partBlock += Sectors.AlignUp(fidLen, Sectors.Size) / Sectors.Size;
        }

        // FE sectors: all directories first (path-table order, root first), then all files
        // (in physical placement order). Verified against GT3/GT4 masters.
        var files = vol.EffectiveLayout().ToList();
        var feBlocks = new Dictionary<DiscNode, long>();
        foreach (var dir in directories)
            feBlocks[dir] = partBlock++;
        foreach (var file in files)
            feBlocks[file] = partBlock++;

        long dataStart = partitionStart + partBlock;

        // 3. Decide whether the source LBA pins can be honored (byte-exact) or must be repacked.
        // Pins are valid only if every file is pinned, in ascending non-overlapping order, and the
        // first one starts at or after where the metadata ends. Otherwise (e.g. a foreign mastering
        // tool whose metadata is laid out differently) fall back to a clean contiguous repack.
        bool repacked = false;
        if (!ignorePins && files.Count > 0)
        {
            bool valid = files.All(f => f.Lba >= 0);
            long check = dataStart;
            if (valid)
                foreach (var f in files)
                {
                    if (f.Lba < check) { valid = false; break; }
                    check = f.Lba + f.SectorCount;
                }
            if (!valid)
            {
                repacked = true;
                foreach (var f in files) f.Lba = -1;
            }
        }

        // File data, packed in placement order (honoring pins when they survived the check above).
        long cursor = dataStart;
        foreach (var file in files)
        {
            if (file.Lba < 0)
                file.Lba = cursor;
            else
                cursor = file.Lba; // honor pinned LBA
            cursor += file.SectorCount;
        }
        long dataEnd = cursor;

        // 4. Tail padding + closing AVDP.
        long total = vol.TailPadding switch
        {
            TailPaddingStyle.EccAlign => Sectors.AlignUp(dataEnd, Sectors.EccBlock),
            TailPaddingStyle.EccAlignPlusRunout => Sectors.AlignUp(dataEnd, Sectors.EccBlock) + 10_240,
            TailPaddingStyle.FixedTotal => vol.FixedTotalSectors,
            _ => Sectors.AlignUp(dataEnd, Sectors.EccBlock),
        };
        if (total < dataEnd + 1)
            total = Sectors.AlignUp(dataEnd + 1, Sectors.EccBlock);

        return new VolumeLayout
        {
            Volume = vol,
            Directories = directories,
            Files = files,
            PartitionStart = partitionStart,
            FidBlocks = fidBlocks,
            FidLengths = fidLengths,
            FeBlocks = feBlocks,
            DataStart = dataStart,
            DataEnd = dataEnd,
            TotalSectors = total,
            Repacked = repacked,
        };
    }

    /// <summary>All objects (dirs + files) in ISO directory-record order, depth-first:
    /// each directory's children in list order, recursing into subdirectories after emitting
    /// the child record. This is the FE allocation order (root handled separately).</summary>
    public static IEnumerable<DiscNode> NodesInRecordOrder(DiscNode root)
    {
        foreach (var child in root.Children)
        {
            yield return child;
            if (child.IsDirectory)
                foreach (var d in NodesInRecordOrder(child))
                    yield return d;
        }
    }
}
