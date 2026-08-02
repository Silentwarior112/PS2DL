namespace PS2Iso.Core;

/// <summary>Sequential 2048-byte sector writer with a running sector counter, used by the
/// disc builder. Enforces sector alignment and supports zero padding to a target sector.</summary>
public sealed class VolumeSectorWriter
{
    private readonly Stream _out;
    private static readonly byte[] ZeroSector = new byte[Sectors.Size];

    public VolumeSectorWriter(Stream output) => _out = output;

    /// <summary>Absolute number of sectors written so far (== current sector position).</summary>
    public long SectorsWritten { get; private set; }

    /// <summary>Fired periodically with the running sector count (for progress bars).</summary>
    public Action<long>? Progress { get; set; }

    /// <summary>Write one sector-sized buffer.</summary>
    public void WriteSector(byte[] sector)
    {
        if (sector.Length != Sectors.Size)
            throw new ArgumentException($"Sector must be {Sectors.Size} bytes, got {sector.Length}.");
        _out.Write(sector);
        SectorsWritten++;
    }

    /// <summary>Write a buffer that is a whole number of sectors.</summary>
    public void WriteSectors(byte[] data)
    {
        if (data.Length % Sectors.Size != 0)
            throw new ArgumentException($"Data must be a multiple of {Sectors.Size} bytes.");
        _out.Write(data);
        SectorsWritten += data.Length / Sectors.Size;
    }

    /// <summary>Write raw bytes (must be sector-aligned length); used for the system area blob.</summary>
    public void WriteRaw(byte[] data) => WriteSectors(data);

    public void WriteZeroSectors(long count)
    {
        for (long i = 0; i < count; i++)
        {
            _out.Write(ZeroSector);
            SectorsWritten++;
        }
    }

    /// <summary>Zero-pad until the given absolute sector.</summary>
    public void PadTo(long targetSector)
    {
        if (targetSector < SectorsWritten)
            throw new InvalidOperationException(
                $"Cannot pad backwards: at {SectorsWritten}, target {targetSector}.");
        WriteZeroSectors(targetSector - SectorsWritten);
    }

    /// <summary>Copy <paramref name="byteLength"/> bytes from a stream, then zero-fill the last
    /// sector's tail so the write ends on a sector boundary.</summary>
    public void WriteStreamPadded(Stream source, long byteLength)
    {
        var buf = new byte[1 << 20];
        long remaining = byteLength;
        long baseSectors = SectorsWritten;
        long written = 0;
        while (remaining > 0)
        {
            int n = (int)Math.Min(buf.Length, remaining);
            source.ReadExactly(buf, 0, n);
            _out.Write(buf, 0, n);
            remaining -= n;
            written += n;
            Progress?.Invoke(baseSectors + written / Sectors.Size);
        }
        int tail = (int)(byteLength % Sectors.Size);
        if (tail != 0)
            _out.Write(ZeroSector, 0, Sectors.Size - tail);
        SectorsWritten += (byteLength + Sectors.Size - 1) / Sectors.Size;
    }
}
