namespace PS2Iso.Core;

/// <summary>Assembles a complete PS2 DVD image from a <see cref="DiscSpec"/>, writing sectors in
/// order to an output stream. Single-layer produces one volume; dual-layer two-volume produces
/// two complete volumes with layer 1 starting 16 sectors before the layer break.</summary>
public sealed class DiscBuilder
{
    private readonly DiscSpec _spec;

    public DiscBuilder(DiscSpec spec) => _spec = spec;

    /// <summary>When true, pinned LBAs and fixed totals are honored strictly and any conflict
    /// throws rather than being silently relocated.</summary>
    public bool ExactMode { get; set; }

    /// <summary>Reports progress as (sectorsWritten, totalSectors).</summary>
    public Action<long, long>? Progress { get; set; }

    public void Build(string outputPath, Action<string>? log = null)
    {
        using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 1 << 20);
        Build(outStream, log);
    }

    public void Build(Stream output, Action<string>? log = null)
    {
        bool spanning = _spec.LayerMode == DualLayerMode.Spanning;
        var layout0 = VolumeLayoutEngine.Compute(_spec.Volume0, ignorePins: spanning);
        log?.Invoke($"Volume 0: {layout0.Files.Count} files, {layout0.Directories.Count} dirs, " +
                    $"{layout0.TotalSectors:N0} sectors");
        if (layout0.Repacked)
            log?.Invoke("warning: source LBA layout could not be reproduced exactly; files were " +
                        "repacked contiguously — the disc is valid and bootable but not byte-identical.");

        if (spanning)
            ValidateSpanning(layout0, log);

        long estimatedTotal = layout0.TotalSectors +
            (_spec.LayerMode == DualLayerMode.TwoVolumes && _spec.Volume1 is not null
                ? VolumeLayoutEngine.Compute(_spec.Volume1).TotalSectors - 16
                : 0);

        var writer = new VolumeSectorWriter(output);
        if (Progress is not null)
            writer.Progress = s => Progress(s, estimatedTotal);

        // System area (sectors 0-15): regenerated boot logo (from key), verbatim blob, or zeros.
        var systemArea = _spec.EffectiveSystemArea;
        if (systemArea is { Length: 16 * Sectors.Size })
            writer.WriteRaw(systemArea);
        else
            writer.WriteZeroSectors(16);

        WriteVolume(writer, _spec.Volume0, layout0, log);

        if (_spec.LayerMode == DualLayerMode.TwoVolumes && _spec.Volume1 is not null)
        {
            // Layer 1 begins 16 sectors before the layer break: L1's LBA 0 == abs (N_L0 - 16),
            // so L0's final 16 sectors ARE L1's system area (15 zero runout sectors + L0's closing
            // AVDP at L1 sector 15). Those 16 sectors were already written as L0's tail, and L1's
            // PVD lands on the very next sector — so we continue straight from L0's end.
            var layout1 = VolumeLayoutEngine.Compute(_spec.Volume1);
            long l1Base = layout0.TotalSectors - 16;
            log?.Invoke($"Volume 1 @ sector {l1Base:N0}: {layout1.Files.Count} files, " +
                        $"{layout1.TotalSectors:N0} sectors");
            WriteVolume(writer, _spec.Volume1, layout1, log, skipSystemArea: true);
        }

        output.Flush();
        log?.Invoke($"Total: {writer.SectorsWritten:N0} sectors ({writer.SectorsWritten * Sectors.Size:N0} bytes)");
    }

    /// <summary>Validate the spanning layout against DVD-9 limits, resolve the layer break, and
    /// report which file(s) cross it.</summary>
    private void ValidateSpanning(VolumeLayout layout, Action<string>? log)
    {
        long total = layout.TotalSectors;
        if (total > Sectors.Dvd9Max)
            throw new InvalidOperationException(
                $"Spanning volume is {total:N0} sectors, exceeding the DVD-9 maximum " +
                $"({Sectors.Dvd9Max:N0} ≈ 8.5 GB).");
        if (total <= Sectors.Dvd5Max)
            log?.Invoke($"note: volume fits a single layer ({total:N0} ≤ {Sectors.Dvd5Max:N0} sectors); " +
                        "a spanning DVD-9 is not strictly required.");

        long layerBreak = _spec.LayerBreak > 0 ? _spec.LayerBreak : LayerBreak.ChooseOtp(total);
        string? problem = LayerBreak.Validate(layerBreak, total);
        if (problem is not null)
        {
            if (ExactMode || _spec.LayerBreak > 0)
                throw new InvalidOperationException($"Invalid layer break: {problem}");
            long fallback = LayerBreak.ChooseOtp(total);
            log?.Invoke($"warning: {problem} Falling back to computed break {fallback:N0}.");
            layerBreak = fallback;
        }
        _spec.LayerBreak = layerBreak;

        var crossing = layout.Files
            .Where(f => f.Lba < layerBreak && f.Lba + f.SectorCount > layerBreak)
            .ToList();
        log?.Invoke($"Layer break at sector {layerBreak:N0} " +
                    $"(L0 {layerBreak:N0} + L1 {total - layerBreak:N0}). " +
                    $"Set this in your burner ('Layer Break' = {layerBreak}).");
        foreach (var f in crossing)
            log?.Invoke($"  {f.FullPath} spans the layer break " +
                        $"(sectors {f.Lba:N0}..{f.Lba + f.SectorCount - 1:N0}).");
        if (crossing.Count == 0)
            log?.Invoke("  note: no file crosses the layer break; the break falls in a gap.");
    }

    /// <summary>Write one complete volume (its structures + data + tail) at the current position.
    /// The writer must be positioned at the volume base. Directory/file LBAs in the spec are
    /// volume-relative.</summary>
    private void WriteVolume(VolumeSectorWriter writer, VolumeSpec vol, VolumeLayout layout,
        Action<string>? log, bool skipSystemArea = false)
    {
        // The volume's system area (its sectors 0-15) is always already written when we get here:
        // for volume 0 it is the real 16-sector system area, for layer 1 it is L0's overlapping
        // final 16 sectors. Either way the write head sits at the volume's sector 16 (its PVD),
        // so the volume base is 16 sectors back.
        _ = skipSystemArea;
        long baseSector = writer.SectorsWritten - 16;
        var udf = new UdfVolumeWriter(vol, layout);
        var part = new UdfPartitionWriter(layout);

        // 16 PVD, 17 terminator
        writer.WriteSector(IsoVolumeWriter.BuildPvd(layout));
        writer.WriteSector(IsoVolumeWriter.BuildTerminator());
        // 18-20 UDF VRS
        var vrs = new byte[3][];
        for (int i = 0; i < 3; i++) vrs[i] = new byte[Sectors.Size];
        udf.WriteVrs(vrs[0], vrs[1], vrs[2]);
        writer.WriteSector(vrs[0]);
        writer.WriteSector(vrs[1]);
        writer.WriteSector(vrs[2]);
        // 21-31 zero
        writer.PadTo(baseSector + 32);

        // 32-37 main VDS
        var main = BuildVds(udf, 32);
        foreach (var s in main) writer.WriteSector(s);
        // 38-47 zero
        writer.PadTo(baseSector + 48);
        // 48-53 reserve VDS (copies)
        for (int i = 0; i < 6; i++)
            writer.WriteSector(UdfVolumeWriter.MakeReserveCopy(main[i], (uint)(48 + i)));
        // 54-63 zero
        writer.PadTo(baseSector + 64);
        // 64 LVID, 65 TD
        var lvid = new byte[Sectors.Size];
        udf.WriteLvid(lvid);
        writer.WriteSector(lvid);
        var lvidTd = new byte[Sectors.Size];
        udf.WriteTd(lvidTd, 65);
        writer.WriteSector(lvidTd);
        // 66-255 zero
        writer.PadTo(baseSector + 256);
        // 256 AVDP
        var avdp = new byte[Sectors.Size];
        udf.WriteAvdp(avdp, 256);
        writer.WriteSector(avdp);
        // 257-260 path tables
        var ptL = IsoVolumeWriter.BuildPathTable(layout.Directories, bigEndian: false);
        var ptM = IsoVolumeWriter.BuildPathTable(layout.Directories, bigEndian: true);
        writer.WriteSector(ptL);
        writer.WriteSector(ptL); // optional copy (byte-identical)
        writer.WriteSector(ptM);
        writer.WriteSector(ptM); // optional copy
        // 261.. directory extents (path-table order)
        foreach (var dir in layout.Directories)
            writer.WriteSectors(IsoVolumeWriter.BuildDirectoryExtent(dir, vol.LargeFiles));

        // Partition: FSD, TD, FID sectors, FE sectors
        if (writer.SectorsWritten - baseSector != layout.PartitionStart)
            throw new InvalidOperationException(
                $"Partition start mismatch: at {writer.SectorsWritten - baseSector}, " +
                $"expected {layout.PartitionStart}.");
        writer.WriteSector(part.BuildFsd());
        writer.WriteSector(part.BuildPartitionTerminator());
        foreach (var dir in layout.Directories)
            writer.WriteSectors(part.BuildDirectoryFids(dir));
        foreach (var dir in layout.Directories)
            writer.WriteSector(part.BuildFileEntry(dir));
        foreach (var file in layout.Files)
            writer.WriteSector(part.BuildFileEntry(file));

        // File data, packed in placement order.
        if (writer.SectorsWritten - baseSector != layout.DataStart)
            throw new InvalidOperationException(
                $"Data start mismatch: at {writer.SectorsWritten - baseSector}, expected {layout.DataStart}.");
        foreach (var file in layout.Files)
        {
            writer.PadTo(baseSector + file.Lba);
            WriteFileData(writer, file, log);
        }

        // Tail padding then closing AVDP at N-1.
        long avdpSector = baseSector + layout.TotalSectors - 1;
        writer.PadTo(avdpSector);
        var closing = new byte[Sectors.Size];
        udf.WriteAvdp(closing, (uint)(layout.TotalSectors - 1));
        writer.WriteSector(closing);
    }

    private static byte[][] BuildVds(UdfVolumeWriter udf, uint baseLoc)
    {
        var vds = new byte[6][];
        for (int i = 0; i < 6; i++) vds[i] = new byte[Sectors.Size];
        udf.WriteMainVds(vds[0], vds[1], vds[2], vds[3], vds[4], vds[5], baseLoc);
        return vds;
    }

    private static void WriteFileData(VolumeSectorWriter writer, DiscNode file, Action<string>? log)
    {
        if (file.SourcePath is null)
            throw new InvalidOperationException($"File {file.FullPath} has no source path.");
        using var src = File.OpenRead(file.SourcePath);
        if (src.Length != file.Size)
            throw new InvalidDataException(
                $"{file.FullPath}: source is {src.Length} bytes but spec says {file.Size}.");
        writer.WriteStreamPadded(src, file.Size);
    }
}
