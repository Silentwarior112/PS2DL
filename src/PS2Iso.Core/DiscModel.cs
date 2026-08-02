namespace PS2Iso.Core;

/// <summary>Disc media targets supported by the builder.</summary>
public enum MediaType
{
    /// <summary>Single layer DVD-5 (max 2,298,496 sectors pressed; 2,295,104 DVD+R).</summary>
    Dvd5,
    /// <summary>Dual layer DVD-9.</summary>
    Dvd9,
}

/// <summary>How the disc's volumes map onto dual layer media.</summary>
public enum DualLayerMode
{
    /// <summary>Single volume; not dual layer.</summary>
    None,
    /// <summary>Sony retail convention (GT4): layer 0 is a complete volume, layer 1 is a second
    /// complete volume whose LBA 0 sits 16 sectors before the layer break, so its PVD lands on
    /// the first sector of layer 1.</summary>
    TwoVolumes,
    /// <summary>One volume spanning both layers; file data may cross the layer break.
    /// Used to give GT3 a dual-layer disc without an executable patch.</summary>
    Spanning,
}

/// <summary>A timestamp as stored in ISO9660/UDF structures: local time plus a GMT offset in
/// 15-minute units (PS2 masters use +36 = UTC+9), with centisecond precision.</summary>
public readonly record struct DiscTimestamp(
    int Year, int Month, int Day, int Hour, int Minute, int Second, int Centiseconds, sbyte GmtOffset)
{
    public static readonly DiscTimestamp Zero = default;

    public bool IsZero => Year == 0;

    public override string ToString() =>
        IsZero ? "" : $"{Year:D4}-{Month:D2}-{Day:D2}T{Hour:D2}:{Minute:D2}:{Second:D2}" +
                      (Centiseconds != 0 ? $".{Centiseconds:D2}" : "") + FormatOffset();

    private string FormatOffset()
    {
        int minutes = GmtOffset * 15;
        char sign = minutes < 0 ? '-' : '+';
        minutes = Math.Abs(minutes);
        return $"{sign}{minutes / 60:D2}:{minutes % 60:D2}";
    }

    public static DiscTimestamp Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Zero;
        var dto = DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return new DiscTimestamp(dto.Year, dto.Month, dto.Day, dto.Hour, dto.Minute, dto.Second,
            (int)(dto.Millisecond / 10), (sbyte)(dto.Offset.TotalMinutes / 15));
    }
}

/// <summary>A file or directory in the disc tree. Children order == directory record order
/// (PS2 masters use mastering order, NOT the alphabetical order ISO9660 mandates).</summary>
public sealed class DiscNode
{
    /// <summary>The object's name. For a bridge disc this is the UDF (original-case, possibly long)
    /// name; the ISO9660 record uses <see cref="IsoName"/> which may be an 8.3 truncation.</summary>
    public required string Name { get; set; }

    /// <summary>The ISO9660 record name (without the ";version" suffix), as stored on disc — which
    /// for a long filename is an uppercase 8.3 truncation like "LONGNA~1.DAT". Empty means derive it
    /// from <see cref="Name"/> (uppercased).</summary>
    public string IsoName { get; set; } = "";

    public bool IsDirectory { get; set; }
    public DiscTimestamp Timestamp { get; set; }
    public List<DiscNode> Children { get; } = [];
    public DiscNode? Parent { get; set; }

    /// <summary>For files: content length in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Assigned/preserved start LBA (volume-relative). -1 = unassigned.</summary>
    public long Lba { get; set; } = -1;

    /// <summary>For files: path of the content on the local filesystem (extract target / build source).</summary>
    public string? SourcePath { get; set; }

    /// <summary>File version suffix (";1"). Directories carry none.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Transient reader state: the ISO record for this extent had the multi-extent flag
    /// set, so a following record continues the same file. Not persisted.</summary>
    public bool MoreExtentsFollow { get; set; }

    /// <summary>UDF File Entry link count captured from the source, or -1 to derive it (files = 1,
    /// directories = 1 + immediate subdirectory count). Some masters use atypical values.</summary>
    public int UdfLinkCount { get; set; } = -1;

    public string FullPath => Parent is null || Parent.Parent is null && Parent.Name.Length == 0
        ? "/" + Name
        : Parent.FullPath + "/" + Name;

    public IEnumerable<DiscNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            if (child.IsDirectory)
                foreach (var d in child.Descendants())
                    yield return d;
        }
    }

    public long SectorCount => (Size + Sectors.Size - 1) / Sectors.Size;
}

/// <summary>Identity strings recorded in the ISO9660 PVD / UDF descriptors.</summary>
public sealed class VolumeIdentity
{
    public string SystemIdentifier { get; set; } = "PLAYSTATION";
    public string VolumeIdentifier { get; set; } = "";

    /// <summary>UDF volume/logical-volume name. May differ in case from the ISO9660 volume id
    /// (GT4: "GranTurismo4" in UDF vs "GRANTURISMO4" in ISO). Defaults to the ISO id.</summary>
    public string UdfVolumeIdentifier { get; set; } = "";

    /// <summary>8-character prefix of the UDF volume-set identifier (chars 0x30-0x3F). Opaque
    /// per-master value; preserved verbatim for byte-exact rebuilds.</summary>
    public string UdfUniquePrefix { get; set; } = "00000000";

    /// <summary>Full UDF volume-set-identifier body text (after the compression byte): the unique
    /// prefix + "SCEI    " + a title-specific tail (which GT fills with the volume name but some
    /// games leave blank). Captured verbatim when present; empty means construct the GT-style
    /// default.</summary>
    public string UdfVolumeSetBody { get; set; } = "";

    public string VolumeSetIdentifier { get; set; } = "";
    public string PublisherIdentifier { get; set; } = "";
    public string DataPreparerIdentifier { get; set; } = "POLYPHONY DIGITAL INC.";
    public string ApplicationIdentifier { get; set; } = "PLAYSTATION";
    public string CopyrightFileIdentifier { get; set; } = "";
    public string AbstractFileIdentifier { get; set; } = "";
    public string BibliographicFileIdentifier { get; set; } = "";
}

/// <summary>How files too large for a single 32-bit ISO9660 size field (≥4 GiB) are described.</summary>
public enum LargeFileMode
{
    /// <summary>Standards-correct ISO9660 multi-extent records (0x80 flag) + multiple UDF
    /// allocation descriptors. Correct per spec, but the PS2's CDVD file driver does not parse
    /// multi-extent records, so games cannot open such a file.</summary>
    MultiExtent,

    /// <summary>A single ISO9660 record (no multi-extent flag) and a single UDF descriptor, with
    /// the 32-bit size field clamped to its maximum. The file's data is still one contiguous run,
    /// so a game that reads it via its own internal offsets + raw sector reads (as monolithic
    /// archives like GT3.VOL are read) sees the correct start LBA and full contiguous data.
    /// This is the mode to use for a >4 GiB PS2 payload file.</summary>
    SingleExtent,
}

/// <summary>End-of-volume padding convention.</summary>
public enum TailPaddingStyle
{
    /// <summary>Pad to the next 16-sector ECC boundary (GT3-era masters, 2001).</summary>
    EccAlign,
    /// <summary>Pad to the next 16-sector boundary plus 10,240 runout sectors (GT4-era masters).</summary>
    EccAlignPlusRunout,
    /// <summary>Pad to an explicit total volume size in sectors.</summary>
    FixedTotal,
}

/// <summary>One complete ISO9660+UDF bridge volume.</summary>
public sealed class VolumeSpec
{
    public VolumeIdentity Identity { get; } = new();
    public DiscTimestamp CreationDate { get; set; }
    public DiscNode Root { get; set; } = new() { Name = "", IsDirectory = true };

    /// <summary>Global data layout order. Entries reference files (and nothing else);
    /// order here is physical placement order. If empty, depth-first tree order is used.</summary>
    public List<DiscNode> Layout { get; } = [];

    public TailPaddingStyle TailPadding { get; set; } = TailPaddingStyle.EccAlignPlusRunout;

    /// <summary>How ≥4 GiB files are described. Defaults to SingleExtent, which is what PS2 games
    /// can actually read; original discs have no such files so this never affects byte-exactness.</summary>
    public LargeFileMode LargeFiles { get; set; } = LargeFileMode.SingleExtent;

    /// <summary>Only for TailPaddingStyle.FixedTotal.</summary>
    public long FixedTotalSectors { get; set; }

    /// <summary>All directories in path-table order (root first, then in the order their
    /// records appear walking the tree breadth-first). Computed by the layout engine.</summary>
    public IEnumerable<DiscNode> DirectoriesInPathTableOrder()
    {
        var queue = new Queue<DiscNode>();
        queue.Enqueue(Root);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            yield return dir;
            foreach (var child in dir.Children)
                if (child.IsDirectory)
                    queue.Enqueue(child);
        }
    }

    public IEnumerable<DiscNode> Files() => Root.Descendants().Where(n => !n.IsDirectory);

    /// <summary>Effective data placement order.</summary>
    public IReadOnlyList<DiscNode> EffectiveLayout() =>
        Layout.Count > 0 ? Layout : Root.Descendants().Where(n => !n.IsDirectory).ToList();
}

/// <summary>Complete description of a PS2 DVD image: everything needed to rebuild it byte-identically.</summary>
public sealed class DiscSpec
{
    public MediaType Media { get; set; } = MediaType.Dvd5;
    public DualLayerMode LayerMode { get; set; } = DualLayerMode.None;

    /// <summary>Raw content of sectors 0-15 (the PS2 boot-logo region). Null = zero fill. When
    /// <see cref="SystemAreaKey"/> is set this is regenerated from the shared logo plaintext.</summary>
    public byte[]? SystemArea { get; set; }

    /// <summary>Per-disc boot-logo key byte. When set, the 32 KiB system area is regenerated from
    /// the embedded plaintext (<see cref="PS2Iso.Core.SystemArea"/>) rather than stored verbatim —
    /// so no opaque blob is needed. Null for an atypical system area kept as a raw blob.</summary>
    public byte? SystemAreaKey { get; set; }

    /// <summary>The system-area bytes to write: regenerated from the key if available, else the
    /// stored blob, else null (zero fill).</summary>
    public byte[]? EffectiveSystemArea =>
        SystemAreaKey is byte k ? PS2Iso.Core.SystemArea.Generate(k) : SystemArea;

    /// <summary>Layer 0 (or only) volume.</summary>
    public VolumeSpec Volume0 { get; set; } = new();

    /// <summary>Layer 1 volume (TwoVolumes mode only).</summary>
    public VolumeSpec? Volume1 { get; set; }

    /// <summary>Absolute sector where layer 1 begins (== layer 0 volume total size in
    /// TwoVolumes mode). 0 = single layer or auto.</summary>
    public long LayerBreak { get; set; }
}

public static class Sectors
{
    public const int Size = 2048;
    public const int EccBlock = 16;

    /// <summary>DVD-R SL / pressed DVD-5 usable sectors (4.7 GB).</summary>
    public const long Dvd5Max = 2_298_496;
    /// <summary>DVD+R DL per-layer maximum.</summary>
    public const long DvdPlusRDlLayerMax = 2_086_912;
    /// <summary>DVD+R DL total (8.5 GB).</summary>
    public const long Dvd9Max = 4_173_824;

    public static long AlignUp(long sectors, long boundary) =>
        (sectors + boundary - 1) / boundary * boundary;
}
