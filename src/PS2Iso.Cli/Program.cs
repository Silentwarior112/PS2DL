using PS2Iso.Core;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "info":
            return Info(Req(args, 1, "image path"));
        case "extract":
            return Extract(Req(args, 1, "image path"), Req(args, 2, "output directory"));
        case "build":
            return Build(Req(args, 1, "iml path"), Req(args, 2, "output image path"),
                args.Contains("--exact"));
        case "spanning":
            return Spanning(Req(args, 1, "input iml"), Req(args, 2, "output iml"), args);
        case "verify":
            return Verify(Req(args, 1, "image A"), Req(args, 2, "image B"));
        case "unlock":
            return Unlock(Req(args, 1, "file path"));
        default:
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    if (ex is FileInUseException locked)
        Console.Error.WriteLine($"hint: 'ps2iso unlock \"{locked.Path}\"' closes stale SMB handles on it (needs admin).");
    if (Environment.GetEnvironmentVariable("PS2ISO_DEBUG") == "1")
        Console.Error.WriteLine(ex);
    return 2;
}

static string Req(string[] args, int i, string what) =>
    args.Length > i && !args[i].StartsWith("--")
        ? args[i]
        : throw new ArgumentException($"Missing argument: {what}");

static void PrintUsage()
{
    Console.WriteLine("""
        PS2 ISO Tool - Sony-convention PS2 DVD image builder (GT3/GT4 grade)

        usage:
          ps2iso info <image.iso>                     Show disc structure summary
          ps2iso extract <image.iso> <outdir>         Extract files + disc.iml config
          ps2iso build <disc.iml> <out.iso> [--exact] Build image from config
                                                      --exact: honor all LBA pins/totals,
                                                      fail rather than relocate anything
          ps2iso spanning <in.iml> <out.iml>          Convert a config to a dual-layer spanning
                          [--layer-break N]           DVD-9 (one volume across both layers).
                                                      Replace payload file(s) first, then build.
          ps2iso verify <a.iso> <b.iso>               Byte-compare two images
          ps2iso unlock <file>                        Close stale SMB-server handles on a file
                                                      (a client dropped off with it open); admin
        """);
}

static int Info(string path)
{
    using var reader = new IsoImageReader(path);
    var spec = reader.ReadDisc();
    Console.WriteLine($"Image: {path}");
    Console.WriteLine($"  sectors: {reader.TotalSectors:N0} ({reader.TotalSectors * Sectors.Size:N0} bytes)");
    Console.WriteLine($"  media: {spec.Media}  layerMode: {spec.LayerMode}" +
                      (spec.LayerBreak > 0 ? $"  layerBreak: {spec.LayerBreak:N0}" : ""));
    PrintVolume(spec.Volume0, 0);
    if (spec.Volume1 is not null)
        PrintVolume(spec.Volume1, 1);
    return 0;

    static void PrintVolume(VolumeSpec v, int layer)
    {
        Console.WriteLine($"  volume {layer}: '{v.Identity.VolumeIdentifier}' " +
                          $"created {v.CreationDate}  total {v.FixedTotalSectors:N0} sectors, " +
                          $"tail={v.TailPadding}");
        Console.WriteLine($"    publisher: {v.Identity.PublisherIdentifier}");
        foreach (var f in v.EffectiveLayout())
            Console.WriteLine($"    {f.Lba,10}  {f.Size,13:N0}  {f.FullPath}");
    }
}

static int Extract(string path, string outDir)
{
    using var reader = new IsoImageReader(path);
    var spec = reader.ReadDisc();
    Directory.CreateDirectory(outDir);

    string filesDir0 = Path.Combine(outDir, spec.Volume1 is null ? "files" : "files_l0");
    Console.WriteLine($"Extracting volume 0 -> {filesDir0}");
    reader.ExtractFiles(spec.Volume0, 0, filesDir0,
        n => Console.WriteLine($"  {n.FullPath} ({n.Size:N0} bytes)"));

    if (spec.Volume1 is not null)
    {
        string filesDir1 = Path.Combine(outDir, "files_l1");
        Console.WriteLine($"Extracting volume 1 -> {filesDir1}");
        reader.ExtractFiles(spec.Volume1, spec.LayerBreak - 16, filesDir1,
            n => Console.WriteLine($"  {n.FullPath} ({n.Size:N0} bytes)"));
    }

    string iml = Path.Combine(outDir, "disc.iml");
    ImlDocument.Save(spec, iml);
    Console.WriteLine($"Wrote {iml}");
    return 0;
}

static int Build(string imlPath, string outPath, bool exact)
{
    var spec = ImlDocument.Load(imlPath);
    var builder = new DiscBuilder(spec) { ExactMode = exact };
    builder.Build(outPath, msg => Console.WriteLine(msg));
    Console.WriteLine($"Built {outPath}");
    return 0;
}

static int Spanning(string inIml, string outIml, string[] args)
{
    var spec = ImlDocument.Load(inIml);
    spec.Media = MediaType.Dvd9;
    spec.LayerMode = DualLayerMode.Spanning;
    spec.Volume1 = null;
    // Runout padding after the data (Sony's GT4-era convention): absorbs the game's read-ahead
    // when it streams the last file at the disc edge, so reads don't run off the end of the disc.
    spec.Volume0.TailPadding = TailPaddingStyle.EccAlignPlusRunout;
    // Default: single-extent large files (PS2-readable). --multi-extent for standards-correct.
    spec.Volume0.LargeFiles = args.Contains("--multi-extent")
        ? LargeFileMode.MultiExtent : LargeFileMode.SingleExtent;
    // Repack from scratch: clear pins so a resized payload lays out cleanly.
    foreach (var f in spec.Volume0.Files())
        f.Lba = -1;

    int lbIdx = Array.IndexOf(args, "--layer-break");
    if (lbIdx >= 0 && lbIdx + 1 < args.Length)
        spec.LayerBreak = long.Parse(args[lbIdx + 1]);

    ImlDocument.Save(spec, outIml);
    Console.WriteLine($"Wrote spanning config {outIml}");
    Console.WriteLine("Media set to DVD-9, one volume spanning both layers.");
    Console.WriteLine("Replace the payload file(s) with your larger version(s) before building.");
    Console.WriteLine($"Then: ps2iso build {Path.GetFileName(outIml)} <out.iso>");
    return 0;
}

static int Verify(string a, string b)
{
    var (identical, sa, sb, diffs) = ImageComparer.Compare(a, b);
    Console.WriteLine($"A: {sa:N0} sectors   B: {sb:N0} sectors");
    if (identical)
    {
        Console.WriteLine("IDENTICAL");
        return 0;
    }
    if (sa != sb)
        Console.WriteLine($"SIZE MISMATCH ({sb - sa:+#;-#;0} sectors)");
    foreach (var d in diffs)
        Console.WriteLine($"  diff @ LBA {d.FirstLba:N0} x{d.Count:N0} (first byte offset {d.FirstByteOffset})");
    Console.WriteLine($"DIFFERENT ({diffs.Count} run(s) shown)");
    return 3;
}

static int Unlock(string path)
{
    var (closed, message) = SmbOpenFiles.Close(path);
    Console.WriteLine(message);
    return closed ? 0 : 1;
}
