namespace PS2Iso.Core;

public sealed record SectorDiff(long FirstLba, long Count, int FirstByteOffset);

/// <summary>Byte-compares two disc images and reports differing sector runs.
/// The workhorse behind 'verify' — proving rebuilds are byte-identical.</summary>
public static class ImageComparer
{
    public static (bool identical, long sectorsA, long sectorsB, List<SectorDiff> diffs)
        Compare(string pathA, string pathB, int maxDiffs = 64)
    {
        using var a = File.OpenRead(pathA);
        using var b = File.OpenRead(pathB);
        long sectorsA = a.Length / Sectors.Size;
        long sectorsB = b.Length / Sectors.Size;

        var diffs = new List<SectorDiff>();
        const int Chunk = 4 << 20;
        var bufA = new byte[Chunk];
        var bufB = new byte[Chunk];
        long common = Math.Min(a.Length, b.Length);
        long pos = 0;
        SectorDiff? open = null;

        while (pos < common && diffs.Count < maxDiffs)
        {
            int n = (int)Math.Min(Chunk, common - pos);
            a.ReadExactly(bufA, 0, n);
            b.ReadExactly(bufB, 0, n);
            if (bufA.AsSpan(0, n).SequenceEqual(bufB.AsSpan(0, n)))
            {
                if (open is not null) { diffs.Add(open); open = null; }
                pos += n;
                continue;
            }
            for (int off = 0; off < n; off += Sectors.Size)
            {
                int len = Math.Min(Sectors.Size, n - off);
                var sa = bufA.AsSpan(off, len);
                var sb = bufB.AsSpan(off, len);
                if (sa.SequenceEqual(sb))
                {
                    if (open is not null) { diffs.Add(open); open = null; }
                }
                else
                {
                    long lba = (pos + off) / Sectors.Size;
                    if (open is not null && open.FirstLba + open.Count == lba)
                        open = open with { Count = open.Count + 1 };
                    else
                    {
                        if (open is not null) diffs.Add(open);
                        int firstByte = sa.CommonPrefixLength(sb);
                        open = new SectorDiff(lba, 1, firstByte);
                    }
                    if (diffs.Count >= maxDiffs) break;
                }
            }
            pos += n;
        }
        if (open is not null) diffs.Add(open);

        bool identical = diffs.Count == 0 && sectorsA == sectorsB;
        return (identical, sectorsA, sectorsB, diffs);
    }
}
