using System.Globalization;
using System.Xml.Linq;

namespace PS2Iso.Core;

/// <summary>
/// Reads/writes the disc description config ("IML" file) — an XML document that captures
/// everything needed to rebuild a PS2 DVD image: volume identity, timestamps, the directory
/// tree in record order, the physical data layout order with optional LBA pins, the system
/// area blob, and dual-layer structure.
/// </summary>
public static class ImlDocument
{
    public static void Save(DiscSpec spec, string imlPath, string? systemAreaFileName = "system_area.bin")
    {
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(imlPath))!;

        var root = new XElement("ps2disc",
            new XAttribute("media", spec.Media == MediaType.Dvd9 ? "dvd9" : "dvd5"));
        if (spec.LayerMode != DualLayerMode.None)
            root.Add(new XAttribute("layerMode",
                spec.LayerMode == DualLayerMode.TwoVolumes ? "twoVolumes" : "spanning"));
        if (spec.LayerBreak > 0)
            root.Add(new XAttribute("layerBreak", spec.LayerBreak));

        // Prefer the compact boot-logo key (1 byte) over an opaque 32 KiB blob.
        if (spec.SystemAreaKey is byte key)
        {
            root.Add(new XElement("systemArea", new XAttribute("bootLogoKey", $"0x{key:X2}")));
        }
        else if (spec.SystemArea is not null && systemAreaFileName is not null)
        {
            File.WriteAllBytes(Path.Combine(baseDir, systemAreaFileName), spec.SystemArea);
            root.Add(new XElement("systemArea", new XAttribute("source", systemAreaFileName)));
        }

        root.Add(VolumeElement(spec.Volume0, 0, baseDir));
        if (spec.Volume1 is not null)
            root.Add(VolumeElement(spec.Volume1, 1, baseDir));

        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(imlPath);
    }

    private static XElement VolumeElement(VolumeSpec vol, int layer, string baseDir)
    {
        var e = new XElement("volume",
            new XAttribute("layer", layer),
            new XAttribute("creationDate", vol.CreationDate.ToString()),
            new XAttribute("tailPadding", vol.TailPadding switch
            {
                TailPaddingStyle.EccAlign => "eccAlign",
                TailPaddingStyle.EccAlignPlusRunout => "eccAlignRunout",
                _ => "fixed",
            }),
            new XAttribute("largeFiles", vol.LargeFiles == LargeFileMode.MultiExtent
                ? "multiExtent" : "singleExtent"),
            new XAttribute("totalSectors", vol.FixedTotalSectors));

        var id = vol.Identity;
        e.Add(new XElement("identity",
            new XAttribute("system", id.SystemIdentifier),
            new XAttribute("volume", id.VolumeIdentifier),
            new XAttribute("udfVolume", id.UdfVolumeIdentifier),
            new XAttribute("udfUniquePrefix", id.UdfUniquePrefix),
            new XAttribute("udfVolumeSetBody", id.UdfVolumeSetBody),
            new XAttribute("volumeSet", id.VolumeSetIdentifier),
            new XAttribute("publisher", id.PublisherIdentifier),
            new XAttribute("dataPreparer", id.DataPreparerIdentifier),
            new XAttribute("application", id.ApplicationIdentifier),
            new XAttribute("copyrightFile", id.CopyrightFileIdentifier),
            new XAttribute("abstractFile", id.AbstractFileIdentifier),
            new XAttribute("bibliographicFile", id.BibliographicFileIdentifier)));

        var tree = new XElement("tree");
        foreach (var child in vol.Root.Children)
            tree.Add(NodeElement(child, baseDir));
        e.Add(tree);

        var layout = new XElement("layout");
        foreach (var file in vol.EffectiveLayout())
            layout.Add(new XElement("file",
                new XAttribute("path", file.FullPath),
                file.Lba >= 0 ? new XAttribute("lba", file.Lba) : null));
        e.Add(layout);
        return e;
    }

    private static XElement NodeElement(DiscNode node, string baseDir)
    {
        if (node.IsDirectory)
        {
            int derived = 1 + node.Children.Count(c => c.IsDirectory);
            var d = new XElement("dir",
                new XAttribute("name", node.Name),
                new XAttribute("timestamp", node.Timestamp.ToString()));
            if (node.IsoName.Length > 0 && node.IsoName != node.Name.ToUpperInvariant())
                d.Add(new XAttribute("iso", node.IsoName));
            if (node.UdfLinkCount >= 0 && node.UdfLinkCount != derived)
                d.Add(new XAttribute("udfLinkCount", node.UdfLinkCount));
            foreach (var child in node.Children)
                d.Add(NodeElement(child, baseDir));
            return d;
        }
        var f = new XElement("file",
            new XAttribute("name", node.Name),
            new XAttribute("timestamp", node.Timestamp.ToString()));
        if (node.IsoName.Length > 0 && node.IsoName != node.Name.ToUpperInvariant())
            f.Add(new XAttribute("iso", node.IsoName));
        if (node.SourcePath is not null)
            f.Add(new XAttribute("source", Path.GetRelativePath(baseDir, node.SourcePath)));
        if (node.Version != 1)
            f.Add(new XAttribute("version", node.Version));
        return f;
    }

    public static DiscSpec Load(string imlPath)
    {
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(imlPath))!;
        var doc = XDocument.Load(imlPath);
        var root = doc.Root ?? throw new InvalidDataException("Empty IML document.");
        if (root.Name != "ps2disc")
            throw new InvalidDataException($"Unexpected root element <{root.Name}>.");

        var spec = new DiscSpec
        {
            Media = (string?)root.Attribute("media") == "dvd9" ? MediaType.Dvd9 : MediaType.Dvd5,
            LayerMode = (string?)root.Attribute("layerMode") switch
            {
                "twoVolumes" => DualLayerMode.TwoVolumes,
                "spanning" => DualLayerMode.Spanning,
                _ => DualLayerMode.None,
            },
            LayerBreak = (long?)root.Attribute("layerBreak") ?? 0,
        };

        var systemAreaEl = root.Element("systemArea");
        if (systemAreaEl?.Attribute("bootLogoKey")?.Value is string keyText)
        {
            spec.SystemAreaKey = (byte)Convert.ToInt32(keyText, keyText.StartsWith("0x") ? 16 : 10);
        }
        else if (systemAreaEl?.Attribute("source")?.Value is string sa)
        {
            spec.SystemArea = File.ReadAllBytes(Path.Combine(baseDir, sa));
            if (spec.SystemArea.Length != 16 * Sectors.Size)
                throw new InvalidDataException(
                    $"System area blob must be {16 * Sectors.Size} bytes, got {spec.SystemArea.Length}.");
        }

        var volumes = root.Elements("volume").ToList();
        if (volumes.Count == 0)
            throw new InvalidDataException("IML contains no <volume>.");
        spec.Volume0 = ParseVolume(volumes[0], baseDir);
        if (volumes.Count > 1)
            spec.Volume1 = ParseVolume(volumes[1], baseDir);
        return spec;
    }

    private static VolumeSpec ParseVolume(XElement e, string baseDir)
    {
        var vol = new VolumeSpec
        {
            CreationDate = DiscTimestamp.Parse((string?)e.Attribute("creationDate") ?? ""),
            TailPadding = (string?)e.Attribute("tailPadding") switch
            {
                "eccAlign" => TailPaddingStyle.EccAlign,
                "fixed" => TailPaddingStyle.FixedTotal,
                _ => TailPaddingStyle.EccAlignPlusRunout,
            },
            FixedTotalSectors = (long?)e.Attribute("totalSectors") ?? 0,
            LargeFiles = (string?)e.Attribute("largeFiles") == "multiExtent"
                ? LargeFileMode.MultiExtent : LargeFileMode.SingleExtent,
        };

        if (e.Element("identity") is XElement id)
        {
            var i = vol.Identity;
            i.SystemIdentifier = (string?)id.Attribute("system") ?? i.SystemIdentifier;
            i.VolumeIdentifier = (string?)id.Attribute("volume") ?? "";
            i.UdfVolumeIdentifier = (string?)id.Attribute("udfVolume") ?? "";
            i.UdfUniquePrefix = (string?)id.Attribute("udfUniquePrefix") ?? "00000000";
            i.UdfVolumeSetBody = (string?)id.Attribute("udfVolumeSetBody") ?? "";
            i.VolumeSetIdentifier = (string?)id.Attribute("volumeSet") ?? "";
            i.PublisherIdentifier = (string?)id.Attribute("publisher") ?? "";
            i.DataPreparerIdentifier = (string?)id.Attribute("dataPreparer") ?? "";
            i.ApplicationIdentifier = (string?)id.Attribute("application") ?? i.ApplicationIdentifier;
            i.CopyrightFileIdentifier = (string?)id.Attribute("copyrightFile") ?? "";
            i.AbstractFileIdentifier = (string?)id.Attribute("abstractFile") ?? "";
            i.BibliographicFileIdentifier = (string?)id.Attribute("bibliographicFile") ?? "";
        }

        foreach (var child in e.Element("tree")?.Elements() ?? [])
            vol.Root.Children.Add(ParseNode(child, vol.Root, baseDir));

        var byPath = vol.Root.Descendants().Where(n => !n.IsDirectory)
            .ToDictionary(n => n.FullPath, StringComparer.OrdinalIgnoreCase);
        foreach (var f in e.Element("layout")?.Elements("file") ?? [])
        {
            string path = (string?)f.Attribute("path")
                ?? throw new InvalidDataException("<layout><file> missing path attribute.");
            if (!byPath.TryGetValue(path, out var node))
                throw new InvalidDataException($"<layout> references unknown file {path}.");
            node.Lba = (long?)f.Attribute("lba") ?? -1;
            vol.Layout.Add(node);
        }

        var missing = vol.Root.Descendants().Where(n => !n.IsDirectory).Except(vol.Layout).ToList();
        if (vol.Layout.Count > 0 && missing.Count > 0)
            throw new InvalidDataException(
                $"<layout> is missing {missing.Count} file(s), e.g. {missing[0].FullPath}. " +
                "List every file or remove the <layout> element entirely.");
        return vol;
    }

    private static DiscNode ParseNode(XElement e, DiscNode parent, string baseDir)
    {
        string name = (string?)e.Attribute("name")
            ?? throw new InvalidDataException($"<{e.Name}> missing name attribute.");
        var node = new DiscNode
        {
            Name = name,
            IsoName = (string?)e.Attribute("iso") ?? "",
            IsDirectory = e.Name == "dir",
            Timestamp = DiscTimestamp.Parse((string?)e.Attribute("timestamp") ?? ""),
            Version = (int?)e.Attribute("version") ?? 1,
            UdfLinkCount = (int?)e.Attribute("udfLinkCount") ?? -1,
            Parent = parent,
        };
        if (node.IsDirectory)
        {
            foreach (var child in e.Elements())
                node.Children.Add(ParseNode(child, node, baseDir));
        }
        else
        {
            string src = (string?)e.Attribute("source")
                ?? throw new InvalidDataException($"<file name=\"{name}\"> missing source attribute.");
            node.SourcePath = Path.GetFullPath(Path.Combine(baseDir, src));
            var fi = new FileInfo(node.SourcePath);
            if (!fi.Exists)
                throw new FileNotFoundException($"Source file for {node.FullPath} not found.", node.SourcePath);
            node.Size = fi.Length;
        }
        return node;
    }
}
