namespace PS2Iso.Core;

/// <summary>One contiguous run of a file's data: a logical block address and a byte length.</summary>
public readonly record struct FileExtent(long Lba, long Length);

/// <summary>Splits a file into ISO9660/UDF allocation extents.
///
/// Files under 4 GiB use a single extent, reproducing Sony's exact single-descriptor convention
/// (one ISO record, one raw-uint32 UDF short_ad) so original discs rebuild byte-identically.
///
/// Files of 4 GiB or more — only possible on a purpose-built spanning disc — cannot be described
/// by a single 32-bit ISO size field or a single 30-bit UDF length, so they are split into
/// contiguous ~1 GiB extents (ISO multi-extent records + multiple UDF short_ads). The underlying
/// data stays one unbroken run, so it still crosses the layer break transparently.</summary>
public static class FileExtentPlanner
{
    /// <summary>Largest per-extent byte length: a multiple of 2048 that fits UDF's 30-bit
    /// short_ad length field (0x3FFFF800 = 1,073,739,776 bytes ≈ 1 GiB).</summary>
    public const long MaxExtentBytes = 0x3FFFF800;

    /// <summary>At or above this size a single extent can no longer hold the file.</summary>
    public const long SingleExtentLimit = 0x1_0000_0000; // 4 GiB

    /// <summary>Largest byte value expressible in an ISO9660 32-bit data-length field, rounded
    /// down to a whole sector (0xFFFFF800 = 4,294,965,248).</summary>
    public const long MaxIsoSizeField = 0xFFFFF800;

    public static bool NeedsSplit(long size, LargeFileMode mode) =>
        mode == LargeFileMode.MultiExtent && size >= SingleExtentLimit;

    public static List<FileExtent> Plan(long startLba, long size,
        LargeFileMode mode = LargeFileMode.MultiExtent)
    {
        var extents = new List<FileExtent>();
        if (!NeedsSplit(size, mode))
        {
            // One extent covering the whole (contiguous) file; the writers clamp the on-disc
            // 32-bit size field for the SingleExtent case where size exceeds it.
            extents.Add(new FileExtent(startLba, size));
            return extents;
        }
        long lba = startLba;
        long remaining = size;
        while (remaining > 0)
        {
            long len = Math.Min(MaxExtentBytes, remaining);
            extents.Add(new FileExtent(lba, len));
            lba += (len + Sectors.Size - 1) / Sectors.Size;
            remaining -= len;
        }
        return extents;
    }

    /// <summary>The value to store in a 32-bit ISO9660 / UDF size field for one extent, clamped to
    /// the field's maximum (only reached by a &gt;4 GiB single-extent file).</summary>
    public static uint SizeField(long extentLength) =>
        extentLength > MaxIsoSizeField ? (uint)MaxIsoSizeField : (uint)extentLength;

    public static int ExtentCount(DiscNode file, LargeFileMode mode) =>
        NeedsSplit(file.Size, mode)
            ? (int)((file.Size + MaxExtentBytes - 1) / MaxExtentBytes)
            : 1;
}
