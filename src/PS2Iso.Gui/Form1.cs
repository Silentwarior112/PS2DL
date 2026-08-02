using System.Diagnostics;
using PS2Iso.Core;

namespace PS2Iso.Gui;

public partial class Form1 : Form
{
    private readonly AppSettings _settings = AppSettings.Load();

    private readonly TabControl _tabs = new()
    {
        Dock = DockStyle.Fill, Padding = new Point(16, 6), Font = Ui.BodyFont,
    };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = Ui.Mono, BackColor = Ui.LogBg, ForeColor = Ui.LogFg, BorderStyle = BorderStyle.None,
        WordWrap = false,
    };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous };
    private readonly Label _status = new()
    {
        Text = "Ready", AutoSize = false, Dock = DockStyle.Fill, ForeColor = Ui.Text,
        TextAlign = ContentAlignment.MiddleLeft, Font = Ui.BodyFont, Padding = new Padding(4, 0, 0, 0),
    };
    private Stopwatch _sw = new();

    public Form1()
    {
        InitializeComponent();
        Text = "PS2DL";
        BackColor = Ui.Body;
        Font = Ui.BodyFont;
        ClientSize = new Size(920, 700);
        MinimumSize = new Size(780, 600);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* no icon */ }

        Controls.Add(BuildContent());
        Controls.Add(BuildHeader());  // added last => docks above content
    }

    // ---------------- chrome ----------------
    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Ui.HeaderBg };
        header.Controls.Add(new Label
        {
            Text = "PS2DL", ForeColor = Color.White, Font = Ui.H1, AutoSize = true,
            Location = new Point(20, 10),
        });
        header.Controls.Add(new Label
        {
            Text = "Modern PlayStation 2 DVD image builder by Silentwarior112",
            ForeColor = Ui.HeaderSub, Font = Ui.Small, AutoSize = true, Location = new Point(22, 39),
        });
        return header;
    }

    private Control BuildContent()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            SplitterWidth = 6, BackColor = Ui.Body, Panel2MinSize = 90,
        };
        split.Panel1.BackColor = Ui.Body;
        split.Panel2.BackColor = Ui.LogBg;

        _tabs.TabPages.Add(WrapTab("  Extract  ", BuildExtractTab()));
        _tabs.TabPages.Add(WrapTab("  Build  ", BuildBuildTab()));
        _tabs.TabPages.Add(WrapTab("  Verify  ", BuildVerifyTab()));
        split.Panel1.Controls.Add(_tabs);

        // log with a small caption bar
        var logCaption = new Label
        {
            Text = "  Log", Dock = DockStyle.Top, Height = 22, BackColor = Color.FromArgb(30, 33, 40),
            ForeColor = Ui.HeaderSub, Font = Ui.Small, TextAlign = ContentAlignment.MiddleLeft,
        };
        var logHost = new Panel { Dock = DockStyle.Fill, BackColor = Ui.LogBg, Padding = new Padding(8, 4, 4, 6) };
        logHost.Controls.Add(_log);
        split.Panel2.Controls.Add(logHost);
        split.Panel2.Controls.Add(logCaption);

        // status + progress strip lives at the very bottom of the whole content
        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(split);
        host.Controls.Add(BuildStatusStrip());

        // set a sensible split once shown
        host.HandleCreated += (_, _) => { try { split.SplitterDistance = split.Height - 150; } catch { } };
        return host;
    }

    private Control BuildStatusStrip()
    {
        var strip = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 34, ColumnCount = 2, BackColor = Ui.Card,
            Padding = new Padding(10, 5, 12, 5),
        };
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        var pbHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 0, 4) };
        pbHost.Controls.Add(_progress);
        strip.Controls.Add(_status, 0, 0);
        strip.Controls.Add(pbHost, 1, 0);
        var top = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Ui.Border };
        var wrap = new Panel { Dock = DockStyle.Bottom, Height = 35 };
        wrap.Controls.Add(strip);
        wrap.Controls.Add(top);
        return wrap;
    }

    private static TabPage WrapTab(string title, Control content)
    {
        var page = new TabPage(title) { BackColor = Ui.Body, Padding = new Padding(14) };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    // ---------------- tabs ----------------
    private Control BuildExtractTab()
    {
        var (card, rows) = Card("Extract an ISO with a disc.iml file order list.");
        var iso = PathRow(rows, "Source ISO", PickKind.OpenFile, "PS2 ISO (*.iso)|*.iso|All files|*.*");
        var outDir = PathRow(rows, "Output folder", PickKind.Folder);
        var info = InfoBox(rows);

        iso.TextChanged += (_, _) =>
        {
            if (File.Exists(iso.Text))
            {
                if (string.IsNullOrWhiteSpace(outDir.Text))
                    outDir.Text = Path.Combine(Path.GetDirectoryName(iso.Text)!,
                        Path.GetFileNameWithoutExtension(iso.Text) + "_extract");
                _ = ShowDiscInfoAsync(iso.Text, info);
            }
        };

        var run = Ui.PrimaryButton("Extract  →");
        run.Click += async (_, _) =>
        {
            if (!Valid(iso.Text, "source ISO")) return;
            if (Blank(outDir.Text, "output folder")) return;
            await RunAsync("Extract", () =>
            {
                using var reader = new IsoImageReader(iso.Text);
                var spec = reader.ReadDisc();
                Directory.CreateDirectory(outDir.Text);
                Log($"{spec.Media} · {spec.LayerMode} · volume '{spec.Volume0.Identity.VolumeIdentifier}'");
                string files0 = Path.Combine(outDir.Text, spec.Volume1 is null ? "files" : "files_l0");
                reader.ExtractFiles(spec.Volume0, 0, files0,
                    n => Log($"  {n.FullPath} ({Fmt(n.Size)})"), Report);
                if (spec.Volume1 is not null)
                    reader.ExtractFiles(spec.Volume1, spec.LayerBreak - 16,
                        Path.Combine(outDir.Text, "files_l1"),
                        n => Log($"  {n.FullPath} ({Fmt(n.Size)})"), Report);
                string imlPath = Path.Combine(outDir.Text, "disc.iml");
                ImlDocument.Save(spec, imlPath);
                Log($"Wrote {imlPath}");
            });
        };
        return Compose(card, run);
    }

    private Control BuildBuildTab()
    {
        var (card, rows) = Card("Build a PS2 DVD image from an .iml config and its files.");
        var iml = PathRow(rows, "Config (.iml)", PickKind.OpenFile, "IML config (*.iml)|*.iml|All files|*.*");
        var outIso = PathRow(rows, "Output ISO", PickKind.SaveFile, "PS2 ISO (*.iso)|*.iso");
        var dual = CheckWithHint(rows, "Spanning dual-layer mode",
            "8.5 GB support for DVD-5 games that hold their data in one large file.\n" +
            "Leave OFF for true DVD-9 games.\n ", false);
        var lb = SmallField(rows, "Layer break", "sectors; blank = auto (midpoint)");
        AddFillRow(rows, new Panel { BackColor = Color.Transparent });

        void SyncDual()
        {
            lb.Enabled = dual.Checked;
            if (File.Exists(iml.Text))
                outIso.Text = Path.Combine(Path.GetDirectoryName(iml.Text)!,
                    Path.GetFileNameWithoutExtension(iml.Text) + (dual.Checked ? "_dl.iso" : ".iso"));
        }
        dual.CheckedChanged += (_, _) => SyncDual();
        lb.Enabled = false;

        iml.TextChanged += (_, _) =>
        {
            if (File.Exists(iml.Text) && string.IsNullOrWhiteSpace(outIso.Text))
                outIso.Text = Path.Combine(Path.GetDirectoryName(iml.Text)!,
                    Path.GetFileNameWithoutExtension(iml.Text) + (dual.Checked ? "_dl.iso" : ".iso"));
        };

        var run = Ui.PrimaryButton("Build  →");
        run.Click += async (_, _) =>
        {
            if (!Valid(iml.Text, "config")) return;
            if (Blank(outIso.Text, "output ISO")) return;
            long layerBreak = 0;
            if (dual.Checked && !string.IsNullOrWhiteSpace(lb.Text)
                && !long.TryParse(lb.Text.Replace(",", ""), out layerBreak))
            {
                Warn("Layer break must be a whole number of sectors."); return;
            }
            await RunAsync(dual.Checked ? "Dual-layer build" : "Build", () =>
            {
                var spec = ImlDocument.Load(iml.Text);
                spec.Volume0.LargeFiles = LargeFileMode.SingleExtent;  // always: PS2 can't read multi-extent
                if (spec.Volume1 is not null) spec.Volume1.LargeFiles = LargeFileMode.SingleExtent;
                if (dual.Checked)
                {
                    spec.Media = MediaType.Dvd9;
                    spec.LayerMode = DualLayerMode.Spanning;
                    spec.Volume1 = null;
                    // Runout padding (Sony GT4 convention) absorbs the game's read-ahead at the disc edge.
                    spec.Volume0.TailPadding = TailPaddingStyle.EccAlignPlusRunout;
                    foreach (var f in spec.Volume0.Files()) f.Lba = -1;
                    if (layerBreak > 0) spec.LayerBreak = layerBreak;
                }
                var builder = new DiscBuilder(spec) { Progress = ReportPair };
                builder.Build(outIso.Text, Log);
                Log($"Built {outIso.Text}");
                if (dual.Checked)
                    Log($"★ Burn with the LAYER BREAK set to {spec.LayerBreak:N0} sectors.");
            });
        };
        return Compose(card, run);
    }

    private Control BuildVerifyTab()
    {
        var (card, rows) = Card("Byte-compare two images and list any differing sectors.");
        var a = PathRow(rows, "Image A", PickKind.OpenFile, "PS2 ISO (*.iso)|*.iso|All files|*.*");
        var b = PathRow(rows, "Image B", PickKind.OpenFile, "PS2 ISO (*.iso)|*.iso|All files|*.*");
        AddFillRow(rows, new Panel { BackColor = Color.Transparent });

        var run = Ui.PrimaryButton("Compare  →");
        run.Click += async (_, _) =>
        {
            if (!Valid(a.Text, "image A") || !Valid(b.Text, "image B")) return;
            await RunAsync("Verify", () =>
            {
                var (identical, sa, sb, diffs) = ImageComparer.Compare(a.Text, b.Text);
                Log($"A: {sa:N0} sectors    B: {sb:N0} sectors");
                if (identical) { Log("IDENTICAL — the images match byte-for-byte."); return; }
                if (sa != sb) Log($"Size differs by {sb - sa:+#;-#;0} sectors.");
                foreach (var d in diffs)
                    Log($"  diff @ LBA {d.FirstLba:N0} ×{d.Count:N0} (byte offset {d.FirstByteOffset})");
                Log($"DIFFERENT — {diffs.Count} run(s) shown.");
            });
        };
        return Compose(card, run);
    }

    // ---------------- card + row helpers ----------------
    // Each field is its own fixed-height 3-column table (caption | control | browse) stacked in a
    // single-column table — deterministic layout, no fragile shared-grid bookkeeping.
    private const int CaptionW = 128, BrowseW = 42;

    private (Panel card, TableLayoutPanel stack) Card(string description)
    {
        var card = Ui.Card2();
        card.Dock = DockStyle.Fill;
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = false, BackColor = Color.Transparent,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(stack, new Label
        {
            Text = description, AutoSize = false, Dock = DockStyle.Fill, Font = Ui.BodyFont,
            ForeColor = Ui.Muted,
        }, description.Contains('\n') ? 46 : 28);
        card.Controls.Add(stack);
        return (card, stack);
    }

    private static void AddRow(TableLayoutPanel stack, Control c, int height)
    {
        int r = stack.RowCount;
        stack.RowCount = r + 1;
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        c.Dock = DockStyle.Fill;
        stack.Controls.Add(c, 0, r);
    }

    /// <summary>Add a row that absorbs all leftover vertical space (Percent). Used for the one
    /// growable element per tab, or an empty spacer so fixed rows keep their heights.</summary>
    private static Control AddFillRow(TableLayoutPanel stack, Control c)
    {
        int r = stack.RowCount;
        stack.RowCount = r + 1;
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        c.Dock = DockStyle.Fill;
        stack.Controls.Add(c, 0, r);
        return c;
    }

    private static TableLayoutPanel FieldGrid(string caption, Control field, Control? trailing)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0),
            BackColor = Color.Transparent,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CaptionW));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, trailing is null ? 0 : BrowseW));
        var cap = new Label
        {
            Text = caption, Dock = DockStyle.Fill, Font = Ui.BodyFont, ForeColor = Ui.Text,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        field.Dock = DockStyle.Fill;
        t.Controls.Add(cap, 0, 0);
        t.Controls.Add(field, 1, 0);
        if (trailing is not null)
        {
            trailing.Dock = DockStyle.Fill;
            t.Controls.Add(trailing, 2, 0);
        }
        return t;
    }

    private enum PickKind { OpenFile, SaveFile, Folder }

    private TextBox PathRow(TableLayoutPanel stack, string caption, PickKind kind, string filter = "")
    {
        var box = Ui.PathBox();
        WireDragDrop(box, kind == PickKind.Folder);
        var browse = Ui.GhostButton("…");
        browse.Margin = new Padding(6, 6, 0, 6);
        browse.Click += (_, _) => Browse(box, kind, filter);
        AddRow(stack, FieldGrid(caption, box, browse), 40);
        return box;
    }

    /// <summary>A checkbox with a bold title and a muted multi-line description beneath it.</summary>
    private static CheckBox CheckWithHint(TableLayoutPanel stack, string title, string hint, bool chk = false)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var cb = new CheckBox
        {
            Text = title, Checked = chk, AutoSize = false, Dock = DockStyle.Top, Height = 22,
            Font = Ui.H2, ForeColor = Ui.Text, Padding = new Padding(2, 0, 0, 0),
        };
        var desc = new Label
        {
            Text = hint, AutoSize = false, Dock = DockStyle.Fill, Font = Ui.Small, ForeColor = Ui.Muted,
            Padding = new Padding(22, 1, 0, 0),
        };
        panel.Controls.Add(desc);
        panel.Controls.Add(cb);
        int lines = hint.Count(c => c == '\n') + 1;
        AddRow(stack, panel, 24 + lines * 16);
        return cb;
    }

    private TextBox SmallField(TableLayoutPanel stack, string caption, string hint)
    {
        var box = new TextBox
        {
            Font = Ui.BodyFont, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = hint,
            Margin = new Padding(0, 6, 8, 6),
        };
        AddRow(stack, FieldGrid(caption, box, null), 40);
        return box;
    }

    private TextBox InfoBox(TableLayoutPanel stack)
    {
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = Ui.Mono,
            BackColor = Ui.Body, ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle,
            WordWrap = false, Text = "Select an ISO to preview its structure.",
        };
        AddFillRow(stack, FieldGrid("Preview", box, null));
        return box;
    }

    private Control Compose(Panel card, Button primary)
    {
        var outer = new Panel { Dock = DockStyle.Fill };
        var buttonBar = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(0, 12, 2, 8) };
        primary.Dock = DockStyle.Right;
        primary.Width = Math.Max(180, primary.PreferredSize.Width + 20);
        buttonBar.Controls.Add(primary);
        outer.Controls.Add(card);
        outer.Controls.Add(buttonBar);
        return outer;
    }

    // ---------------- behaviour ----------------
    private void Browse(TextBox box, PickKind kind, string filter)
    {
        string? start = Directory.Exists(_settings.LastDirectory) ? _settings.LastDirectory : null;
        if (kind == PickKind.Folder)
        {
            using var d = new FolderBrowserDialog { SelectedPath = start ?? "" };
            if (d.ShowDialog(this) == DialogResult.OK) { box.Text = d.SelectedPath; _settings.Remember(d.SelectedPath); }
        }
        else if (kind == PickKind.SaveFile)
        {
            using var d = new SaveFileDialog { Filter = filter, InitialDirectory = start };
            if (d.ShowDialog(this) == DialogResult.OK) { box.Text = d.FileName; _settings.Remember(d.FileName); }
        }
        else
        {
            using var d = new OpenFileDialog { Filter = filter, InitialDirectory = start };
            if (d.ShowDialog(this) == DialogResult.OK) { box.Text = d.FileName; _settings.Remember(d.FileName); }
        }
    }

    private void WireDragDrop(TextBox box, bool wantFolder)
    {
        box.AllowDrop = true;
        box.DragEnter += (_, e) =>
            e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        box.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                string p = files[0];
                if (wantFolder && File.Exists(p)) p = Path.GetDirectoryName(p) ?? p;
                box.Text = p;
                _settings.Remember(p);
            }
        };
    }

    private async Task ShowDiscInfoAsync(string path, TextBox info)
    {
        info.Text = "Reading…";
        try
        {
            var text = await Task.Run(() =>
            {
                using var reader = new IsoImageReader(path);
                var spec = reader.ReadDisc();
                var lines = new List<string>
                {
                    $"Media:   {spec.Media}   ({reader.TotalSectors:N0} sectors, {Fmt(reader.TotalSectors * 2048)})",
                    $"Layout:  {spec.LayerMode}" + (spec.LayerBreak > 0 ? $"   layer break {spec.LayerBreak:N0}" : ""),
                };
                AppendVolumeInfo(lines, spec.Volume0, spec.Volume1 is null ? null : "Layer 0");
                if (spec.Volume1 is not null)
                    AppendVolumeInfo(lines, spec.Volume1, "Layer 1");
                return string.Join(Environment.NewLine, lines);
            });
            info.Text = text;
        }
        catch (Exception ex) { info.Text = "Could not read: " + ex.Message; }
    }

    private static void AppendVolumeInfo(List<string> lines, VolumeSpec v, string? layerLabel)
    {
        lines.Add("");
        if (layerLabel is not null) lines.Add($"── {layerLabel} ──");
        lines.Add($"Volume:  {v.Identity.VolumeIdentifier}   ·   {v.Identity.PublisherIdentifier}");
        lines.Add($"Created: {v.CreationDate}");
        lines.Add($"Files:   {v.Files().Count()} in {v.DirectoriesInPathTableOrder().Count()} dir(s)   (physical order)");
        lines.Add("");
        foreach (var f in v.EffectiveLayout())
            lines.Add($"   {f.Lba,9}  {Fmt(f.Size),12}  {f.FullPath}");
    }

    private async Task RunAsync(string name, Action work)
    {
        SetBusy(true);
        _log.Clear();
        Log($"── {name} ──");
        _sw = Stopwatch.StartNew();
        try
        {
            await Task.Run(work);
            _sw.Stop();
            SetStatus($"{name} complete  ·  {_sw.Elapsed:mm\\:ss}", Ui.Ok);
            Log($"── done in {_sw.Elapsed:mm\\:ss} ──");
            BeginInvoke(() => _progress.Value = _progress.Maximum);
        }
        catch (Exception ex)
        {
            SetStatus($"{name} failed: {ex.Message}", Ui.Err);
            Log($"ERROR: {ex.Message}");
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _tabs.Enabled = !busy;
        if (busy) BeginInvoke(() => { _progress.Style = ProgressBarStyle.Continuous; _progress.Value = 0; });
    }

    private void ReportPair(long done, long total) => Report(done * 2048, total * 2048);

    private void Report(long done, long total)
    {
        if (total <= 0) return;
        int pct = (int)Math.Clamp(done * 100 / total, 0, 100);
        double secs = _sw.Elapsed.TotalSeconds;
        string speed = secs > 0.5 ? $"  ·  {Fmt((long)(done / secs))}/s" : "";
        BeginInvoke(() =>
        {
            _progress.Value = pct;
            _status.Text = $"{pct}%   {Fmt(done)} / {Fmt(total)}{speed}";
            _status.ForeColor = Ui.Text;
        });
    }

    private void Log(string line) => BeginInvoke(() => _log.AppendText(line + Environment.NewLine));

    private void SetStatus(string s, Color c) => BeginInvoke(() => { _status.Text = s; _status.ForeColor = c; });

    private static string Fmt(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {u[i]}";
    }

    private bool Valid(string path, string what)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return true;
        Warn($"Choose a valid {what}.");
        return false;
    }

    private bool Blank(string path, string what)
    {
        if (!string.IsNullOrWhiteSpace(path)) return false;
        Warn($"Choose an {what}.");
        return true;
    }

    private void Warn(string msg) =>
        MessageBox.Show(this, msg, "PS2 ISO Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
