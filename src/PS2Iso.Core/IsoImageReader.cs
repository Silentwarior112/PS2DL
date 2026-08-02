using System.Buffers.Binary;
using System.Text;

namespace PS2Iso.Core;

/// <summary>Parses a PS2 DVD image (ISO9660 side) into a <see cref="DiscSpec"/>,
/// including detection of Sony's dual-layer two-volume convention.</summary>
public sealed class IsoImageReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly HashSet<long> _visitedDirs = [];
    public long TotalSectors { get; }

    public IsoImageReader(string imagePath)
    {
        _stream = File.OpenRead(imagePath);
        // Floor to whole 2048-byte sectors so trimmed dumps (size not a multiple of 2048) still
        // read. Non-ISO files are rejected later by the PVD check at sector 16.
        TotalSectors = _stream.Length / Sectors.Size;
        if (TotalSectors < 18)
            throw new InvalidDataException("File is too small to be a PS2 DVD image.");
    }

    public byte[] ReadSectors(long lba, int count)
    {
        var buf = new byte[count * Sectors.Size];
        _stream.Position = lba * Sectors.Size;
        _stream.ReadExactly(buf);
        return buf;
    }

    public void CopyRange(long lba, long byteLength, Stream destination)
    {
        _stream.Position = lba * Sectors.Size;
        var buf = new byte[1 << 20];
        long remaining = byteLength;
        while (remaining > 0)
        {
            int n = (int)Math.Min(buf.Length, remaining);
            _stream.ReadExactly(buf, 0, n);
            destination.Write(buf, 0, n);
            remaining -= n;
        }
    }

    /// <summary>Parse the full disc. For DVD9 two-volume discs both volumes are returned.</summary>
    public DiscSpec ReadDisc()
    {
        var spec = new DiscSpec
        {
            SystemArea = ReadSectors(0, 16),
        };
        // Prefer the compact boot-logo key over storing the 32 KiB blob when it regenerates exactly.
        spec.SystemAreaKey = SystemArea.DetectKey(spec.SystemArea);

        long volume0Size = ReadVolume(0, spec.Volume0);

        // If the image is larger than volume 0, it is either a Sony two-volume DVD-9 (a second
        // complete volume whose LBA 0 sits 16 sectors before the layer break) or simply a single
        // volume with trailing padding/dummy sectors. Detect the former; otherwise treat as single.
        if (volume0Size < TotalSectors && TryReadSecondVolume(volume0Size, spec))
            return spec;

        spec.Media = TotalSectors > Sectors.Dvd5Max ? MediaType.Dvd9 : MediaType.Dvd5;
        spec.LayerMode = volume0Size > Sectors.Dvd5Max ? DualLayerMode.Spanning : DualLayerMode.None;
        return spec;
    }

    /// <summary>Try to read a Sony two-volume DVD-9 second volume beginning 16 sectors before the
    /// layer break. Returns false (and reads nothing) if there is no valid layer-1 PVD there —
    /// i.e. the trailing sectors are just padding.</summary>
    private bool TryReadSecondVolume(long volume0Size, DiscSpec spec)
    {
        long l1Base = volume0Size - 16;
        if (l1Base < 0 || l1Base + 17 > TotalSectors)
            return false;
        var pvd = ReadSectors(l1Base + 16, 1);
        if (pvd[0] != 1 || !pvd.AsSpan(1, 5).SequenceEqual("CD001"u8))
            return false;
        // Sanity: the layer-1 volume must fit within the image.
        long l1Vss = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(80));
        if (l1Vss == 0 || l1Base + l1Vss > TotalSectors)
            return false;

        spec.Media = MediaType.Dvd9;
        spec.LayerMode = DualLayerMode.TwoVolumes;
        spec.LayerBreak = volume0Size;
        spec.Volume1 = new VolumeSpec();
        ReadVolume(l1Base, spec.Volume1);
        return true;
    }

    /// <summary>Parse one volume rooted at absolute sector <paramref name="baseLba"/>.
    /// Returns the volume's size in sectors (from its PVD).</summary>
    private long ReadVolume(long baseLba, VolumeSpec volume)
    {
        var pvd = ReadSectors(baseLba + 16, 1);
        if (pvd[0] != 1 || !pvd.AsSpan(1, 5).SequenceEqual("CD001"u8))
            throw new InvalidDataException($"No ISO9660 PVD at sector {baseLba + 16}.");

        var id = volume.Identity;
        id.SystemIdentifier = AString(pvd, 8, 32);
        id.VolumeIdentifier = AString(pvd, 40, 32);
        id.VolumeSetIdentifier = AString(pvd, 190, 128);
        id.PublisherIdentifier = AString(pvd, 318, 128);
        id.DataPreparerIdentifier = AString(pvd, 446, 128);
        id.ApplicationIdentifier = AString(pvd, 574, 128);
        id.CopyrightFileIdentifier = AString(pvd, 702, 37);
        id.AbstractFileIdentifier = AString(pvd, 739, 37);
        id.BibliographicFileIdentifier = AString(pvd, 776, 37);
        volume.CreationDate = ParseVolumeDate(pvd.AsSpan(813, 17));

        long volumeSpaceSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(80));

        uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 2));
        uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 10));
        volume.Root = new DiscNode
        {
            Name = "",
            IsDirectory = true,
            Lba = rootLba,
            Size = rootSize,
            Timestamp = ParseRecordDate(pvd.AsSpan(156 + 18, 7)),
        };
        _visitedDirs.Clear();
        ReadDirectory(baseLba, volume.Root);

        // Recover original-case names + UDF volume identity from the UDF side.
        ReadUdfIdentity(baseLba, volume);
        ReadUdfNames(baseLba, volume);

        // Physical placement order for the layout list.
        foreach (var file in volume.Root.Descendants()
                     .Where(n => !n.IsDirectory)
                     .OrderBy(n => n.Lba))
            volume.Layout.Add(file);

        // Infer the tail padding convention.
        long dataEnd = volume.Layout.Count > 0
            ? volume.Layout.Max(f => f.Lba + f.SectorCount)
            : rootLba + 1;
        long eccEnd = Sectors.AlignUp(dataEnd, Sectors.EccBlock);
        volume.TailPadding =
            volumeSpaceSize == eccEnd ? TailPaddingStyle.EccAlign :
            volumeSpaceSize == eccEnd + 10_240 ? TailPaddingStyle.EccAlignPlusRunout :
            TailPaddingStyle.FixedTotal;
        volume.FixedTotalSectors = volumeSpaceSize;

        return volumeSpaceSize;
    }

    /// <summary>Largest plausible directory extent (16 MiB); guards against garbage size fields.</summary>
    private const int MaxDirectorySectors = 8192;

    private void ReadDirectory(long baseLba, DiscNode dir)
    {
        // Guard against garbage/cyclic directory records.
        if (!_visitedDirs.Add(baseLba + dir.Lba))
            return;
        int sectorCount = (int)dir.SectorCount;
        if (sectorCount <= 0 || sectorCount > MaxDirectorySectors ||
            baseLba + dir.Lba + sectorCount > TotalSectors)
            return;

        var data = ReadSectors(baseLba + dir.Lba, sectorCount);
        for (int s = 0; s < sectorCount; s++)
        {
            int off = s * Sectors.Size;
            int end = off + Sectors.Size;
            while (off + 33 <= end)
            {
                int recLen = data[off];
                if (recLen == 0)
                    break; // rest of sector is padding
                if (recLen < 34 || off + recLen > end)
                    break; // malformed record; skip rest of this sector

                int nameLen = data[off + 32];
                if (off + 33 + nameLen > end)
                    break;
                var nameBytes = data.AsSpan(off + 33, nameLen);
                byte flags = data[off + 25];
                bool isDir = (flags & 2) != 0;

                if (nameLen == 1 && (nameBytes[0] == 0 || nameBytes[0] == 1))
                {
                    off += recLen; // '.' / '..'
                    continue;
                }

                string name = Encoding.ASCII.GetString(nameBytes);
                int version = 1;
                int semi = name.IndexOf(';');
                if (semi >= 0)
                {
                    int.TryParse(name[(semi + 1)..], out version);
                    if (version <= 0) version = 1;
                    name = name[..semi];
                }

                long extentLba = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 2));
                long extentSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 10));

                // Multi-extent file (flag 0x80): coalesce consecutive records for the same file.
                var last = dir.Children.LastOrDefault();
                if (last is not null && !last.IsDirectory && last.Name == name &&
                    last.MoreExtentsFollow)
                {
                    last.Size += extentSize;
                    last.MoreExtentsFollow = (flags & 0x80) != 0;
                    off += recLen;
                    continue;
                }

                var node = new DiscNode
                {
                    Name = name,
                    IsoName = name, // exact ISO9660 record name (may be an 8.3 truncation); UDF name overrides Name later
                    IsDirectory = isDir,
                    Lba = extentLba,
                    Size = extentSize,
                    Timestamp = ParseRecordDate(data.AsSpan(off + 18, 7)),
                    Version = version,
                    Parent = dir,
                    MoreExtentsFollow = (flags & 0x80) != 0,
                };
                dir.Children.Add(node);
                if (isDir)
                    ReadDirectory(baseLba, node);
                off += recLen;
            }
        }
    }

    /// <summary>Extract all file contents of a volume to a directory tree on disk,
    /// setting each node's SourcePath. Volume-relative LBAs are offset by <paramref name="baseLba"/>.</summary>
    public void ExtractFiles(VolumeSpec volume, long baseLba, string outputDir,
        Action<DiscNode>? progress = null, Action<long, long>? byteProgress = null)
    {
        long totalBytes = volume.Files().Sum(f => f.Size);
        long doneBytes = 0;
        foreach (var node in volume.Root.Descendants())
        {
            string rel = node.FullPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string target = Path.Combine(outputDir, rel);
            if (node.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var f = File.Create(target))
                CopyRange(baseLba + node.Lba, node.Size, f);
            node.SourcePath = target;
            doneBytes += node.Size;
            progress?.Invoke(node);
            byteProgress?.Invoke(doneBytes, totalBytes);
        }
    }

    /// <summary>Read the UDF volume name and unique-prefix from the UDF PVD (sector 32) and its
    /// volume set identifier, so a rebuild reproduces them exactly.</summary>
    private void ReadUdfIdentity(long baseLba, VolumeSpec volume)
    {
        var pvd = ReadSectors(baseLba + 32, 1);
        if (BinaryPrimitives.ReadUInt16LittleEndian(pvd) != Udf.TagPvd)
        {
            volume.Identity.UdfVolumeIdentifier = volume.Identity.VolumeIdentifier;
            return;
        }
        volume.Identity.UdfVolumeIdentifier = ReadDstring(pvd.AsSpan(24, 32));
        // Volume set identifier body: 8-char unique prefix + "SCEI    " + a title-specific tail.
        string vsi = ReadDstring(pvd.AsSpan(72, 128));
        volume.Identity.UdfUniquePrefix = vsi.Length >= 8 ? vsi[..8] : vsi.PadRight(8, '0');
        volume.Identity.UdfVolumeSetBody = vsi;
    }

    private static string ReadDstring(ReadOnlySpan<byte> field)
    {
        int used = field[^1];
        if (used <= 1)
            return "";
        byte compression = field[0];
        if (compression == 16)
        {
            var sb = new StringBuilder();
            for (int i = 1; i + 1 < used; i += 2)
                sb.Append((char)BinaryPrimitives.ReadUInt16BigEndian(field[i..]));
            return sb.ToString();
        }
        return Encoding.Latin1.GetString(field.Slice(1, used - 1));
    }

    /// <summary>Overwrite ISO-derived (uppercased) node names with the original-case names from
    /// the UDF directory FIDs, matched positionally (FID order == ISO record order). Only applies
    /// to ISO9660+UDF bridge discs (e.g. Gran Turismo); ISO9660-only games keep their ISO names.</summary>
    private void ReadUdfNames(long baseLba, VolumeSpec volume)
    {
        try
        {
            var dirs = volume.DirectoriesInPathTableOrder().ToList();
            if (dirs.Count == 0)
                return;
            long partitionStart = dirs.Max(d => d.Lba + d.SectorCount);
            if (baseLba + partitionStart + 1 > TotalSectors)
                return;
            // Bridge-disc gate: a real UDF partition begins with a File Set Descriptor here.
            var fsd = ReadSectors(baseLba + partitionStart, 1);
            if (BinaryPrimitives.ReadUInt16LittleEndian(fsd) != Udf.TagFsd
                || Udf.TagChecksum(fsd.AsSpan(0, 16)) != fsd[4])
                return; // not a UDF bridge disc

            // Walk the FID region sequentially: FSD, TD, then each directory's FID data (parent +
            // one FID per child), sector-aligned. Directories may span multiple sectors, so advance
            // by each one's actual FID byte length rather than assuming one sector per directory.
            long fidBlock = partitionStart + 2;
            foreach (var dir in dirs)
            {
                int wanted = 1 + dir.Children.Count; // parent entry + one per child
                // Read enough sectors to hold `wanted` FIDs (a FID is at most 38 + 1 + 2*255 bytes).
                int estSectors = Math.Clamp((wanted * 552 + 2 * Sectors.Size) / Sectors.Size, 1, 512);
                if (baseLba + fidBlock >= TotalSectors)
                    break;
                estSectors = (int)Math.Min(estSectors, TotalSectors - (baseLba + fidBlock));
                var (names, consumed) = ParseFidNames(ReadSectors(baseLba + fidBlock, estSectors), wanted);
                // names[0] is the parent entry (empty); the rest align with dir.Children order.
                int childIndex = 0;
                for (int n = 1; n < names.Count && childIndex < dir.Children.Count; n++, childIndex++)
                    if (names[n].Length > 0)
                        dir.Children[childIndex].Name = names[n];
                long usedSectors = Math.Max(1, (consumed + Sectors.Size - 1) / Sectors.Size);
                fidBlock += usedSectors;
            }

            // The File Entries follow the FID region (directories first, in the same order).
            // Capture each directory's link count so atypical values reproduce exactly.
            long feBlock = fidBlock;
            for (int i = 0; i < dirs.Count; i++, feBlock++)
            {
                if (baseLba + feBlock + 1 > TotalSectors) break;
                var fe = ReadSectors(baseLba + feBlock, 1);
                if (BinaryPrimitives.ReadUInt16LittleEndian(fe) != Udf.TagFe) break;
                dirs[i].UdfLinkCount = BinaryPrimitives.ReadUInt16LittleEndian(fe.AsSpan(48));
            }
        }
        catch (IOException) { /* leave ISO names on any read trouble */ }
    }

    /// <summary>Parse up to <paramref name="wanted"/> UDF FIDs from a buffer (FIDs may span sector
    /// boundaries). Defensive: every field is bounds-checked and each record's tag id + checksum is
    /// validated, so non-UDF data never overruns. Returns the names and total bytes consumed.</summary>
    private static (List<string> names, int consumed) ParseFidNames(byte[] data, int wanted = int.MaxValue)
    {
        var names = new List<string>();
        int off = 0;
        while (names.Count < wanted && off + 38 <= data.Length)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off)) != Udf.TagFid)
                break;
            if (Udf.TagChecksum(data.AsSpan(off, 16)) != data[off + 4])
                break;
            int lfi = data[off + 19];
            int iuLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 36));
            int nameOff = off + 38 + iuLen;
            if (nameOff + lfi > data.Length)
                break;
            string name = "";
            if (lfi > 0)
            {
                byte compression = data[nameOff];
                if (compression == 16)
                {
                    var sb = new StringBuilder();
                    for (int i = 1; i + 1 < lfi; i += 2)
                        sb.Append((char)BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(nameOff + i)));
                    name = sb.ToString();
                }
                else if (compression == 8)
                {
                    name = Encoding.Latin1.GetString(data, nameOff + 1, lfi - 1);
                }
            }
            names.Add(name);
            int total = 38 + iuLen + lfi;
            off += (total + 3) / 4 * 4;
        }
        return (names, off);
    }

    private static string AString(byte[] buf, int offset, int length) =>
        Encoding.ASCII.GetString(buf, offset, length).TrimEnd(' ');

    private static DiscTimestamp ParseVolumeDate(ReadOnlySpan<byte> b)
    {
        string t = Encoding.ASCII.GetString(b[..16]);
        if (t == "0000000000000000" || t.All(c => c == '\0'))
            return DiscTimestamp.Zero;
        return new DiscTimestamp(
            int.Parse(t[..4]), int.Parse(t[4..6]), int.Parse(t[6..8]),
            int.Parse(t[8..10]), int.Parse(t[10..12]), int.Parse(t[12..14]),
            int.Parse(t[14..16]), (sbyte)b[16]);
    }

    private static DiscTimestamp ParseRecordDate(ReadOnlySpan<byte> b) =>
        b[0] == 0 && b[1] == 0 ? DiscTimestamp.Zero :
        new DiscTimestamp(1900 + b[0], b[1], b[2], b[3], b[4], b[5], 0, (sbyte)b[6]);

    public void Dispose() => _stream.Dispose();
}
