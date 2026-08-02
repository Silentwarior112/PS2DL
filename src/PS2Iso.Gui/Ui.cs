using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace PS2Iso.Gui;

/// <summary>Shared theme colours and themed control factories for a clean, responsive UI.</summary>
public static class Ui
{
    public static readonly Color HeaderBg = Color.FromArgb(24, 33, 48);
    public static readonly Color HeaderSub = Color.FromArgb(150, 165, 185);
    public static readonly Color Accent = Color.FromArgb(59, 130, 246);
    public static readonly Color AccentHover = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentDown = Color.FromArgb(29, 78, 216);
    public static readonly Color Body = Color.FromArgb(244, 246, 249);
    public static readonly Color Card = Color.White;
    public static readonly Color Text = Color.FromArgb(31, 41, 55);
    public static readonly Color Muted = Color.FromArgb(107, 114, 128);
    public static readonly Color Border = Color.FromArgb(214, 219, 226);
    public static readonly Color LogBg = Color.FromArgb(22, 24, 29);
    public static readonly Color LogFg = Color.FromArgb(214, 217, 222);
    public static readonly Color Ok = Color.FromArgb(22, 163, 74);
    public static readonly Color Err = Color.FromArgb(220, 38, 38);

    public static readonly Font H1 = new("Segoe UI Semibold", 15f);
    public static readonly Font H2 = new("Segoe UI Semibold", 10.5f);
    public static readonly Font BodyFont = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Mono = new("Cascadia Mono", 9f);

    public static Button PrimaryButton(string text)
    {
        var b = new Button
        {
            Text = text,
            Height = 38,
            AutoSize = false,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 10.5f),
            Cursor = Cursors.Hand,
            Padding = new Padding(16, 0, 16, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.FlatAppearance.MouseDownBackColor = AccentDown;
        return b;
    }

    public static Button GhostButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Text,
            Font = BodyFont,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = Body;
        return b;
    }

    public static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, Font = BodyFont, ForeColor = Text,
        Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0),
    };

    public static Label Hint(string text) => new()
    {
        Text = text, AutoSize = false, Font = Small, ForeColor = Muted,
        Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 8),
    };

    /// <summary>A rounded card panel with a soft border for grouping content.</summary>
    public static Panel Card2(int padding = 18)
    {
        var p = new RoundedPanel
        {
            BackColor = Card, Padding = new Padding(padding), Radius = 10,
            BorderColor = Border,
        };
        return p;
    }

    public static TextBox PathBox()
    {
        return new TextBox
        {
            Font = BodyFont, Anchor = AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 6, 8, 6),
        };
    }
}

/// <summary>A panel with rounded corners and a 1px border.</summary>
public sealed class RoundedPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 8;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Ui.Border;

    public RoundedPanel() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                                      ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Round(r, Radius);
        using var bg = new SolidBrush(BackColor);
        using var pen = new Pen(BorderColor);
        // fill parent corners
        if (Parent is not null)
            e.Graphics.Clear(Parent.BackColor);
        e.Graphics.FillPath(bg, path);
        e.Graphics.DrawPath(pen, path);
        base.OnPaint(e);
    }

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
