using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using Updater.Core;

namespace GameUpdater;

internal enum ButtonKind { Primary, Secondary }
internal enum ButtonState { Normal, Hover, Pressed, Disabled }

/// <summary>
/// How the launcher window looks. The base class is the built-in look, drawn entirely in code so the public
/// exe carries no third-party art. <see cref="SkinTheme"/> overrides pieces with images from LauncherSkin.pak.
/// All rectangles are in device pixels; <c>s</c> is the DPI scale (1 at 96 DPI).
/// </summary>
internal class LauncherTheme : IDisposable
{
    public virtual string Name => "built-in";
    public Color Title { get; protected set; } = Color.FromArgb(0xF4, 0xD9, 0x8E);
    public Color TitleShade { get; protected set; } = Color.FromArgb(0xB0, 0x7A, 0x2C);
    public Color Text { get; protected set; } = Color.FromArgb(0xEF, 0xE6, 0xD2);
    public Color Muted { get; protected set; } = Color.FromArgb(0xA8, 0x98, 0x7C);
    public Color Accent { get; protected set; } = Color.FromArgb(0xE2, 0xB0, 0x52);
    public Color LogBack { get; protected set; } = Color.FromArgb(0x14, 0x11, 0x0D);
    public Color LogText { get; protected set; } = Color.FromArgb(0xC9, 0xBD, 0xA4);
    public Color ButtonText { get; protected set; } = Color.FromArgb(0xFF, 0xF3, 0xD6);

    protected static readonly Color Gold = Color.FromArgb(0xD4, 0xA8, 0x55);
    protected static readonly Color GoldDark = Color.FromArgb(0x6E, 0x52, 0x26);

    public virtual void DrawBackground(Graphics g, Rectangle r, float s)
    {
        using (var bg = new LinearGradientBrush(r, Color.FromArgb(0x24, 0x1D, 0x16), Color.FromArgb(0x0B, 0x09, 0x07), LinearGradientMode.Vertical))
            g.FillRectangle(bg, r);
        // Warm glow behind the title.
        using (var path = new GraphicsPath())
        {
            var glow = new RectangleF(r.Width * 0.15f, -r.Height * 0.35f, r.Width * 0.7f, r.Height * 0.9f);
            path.AddEllipse(glow);
            using var pg = new PathGradientBrush(path) { CenterColor = Color.FromArgb(70, 0xC8, 0x8A, 0x3A), SurroundColors = [Color.FromArgb(0, 0xC8, 0x8A, 0x3A)] };
            g.FillPath(pg, path);
        }
        // Faint diagonal lattice, like tooled leather.
        using (var pen = new Pen(Color.FromArgb(6, 0xE0, 0xC0, 0x80), 1))
        {
            var step = (int)(28 * s);
            for (int x = -r.Height; x < r.Width; x += step)
            {
                g.DrawLine(pen, x, r.Bottom, x + r.Height, r.Top);
                g.DrawLine(pen, x, r.Top, x + r.Height, r.Bottom);
            }
        }
        // Thin gold border round the whole (borderless) window.
        using var edge = new Pen(Color.FromArgb(160, GoldDark), Math.Max(1, s));
        g.DrawRectangle(edge, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    public virtual void DrawPanel(Graphics g, Rectangle r, float s)
    {
        var cut = 14 * s;
        using var path = Chamfer(r, cut);
        using (var fill = new LinearGradientBrush(r, Color.FromArgb(235, 0x22, 0x1B, 0x14), Color.FromArgb(240, 0x12, 0x0F, 0x0B), LinearGradientMode.Vertical))
            g.FillPath(fill, path);
        using (var outer = new Pen(Gold, 2 * s)) g.DrawPath(outer, path);
        var inner = Rectangle.Inflate(r, -(int)(6 * s), -(int)(6 * s));
        using var innerPath = Chamfer(inner, cut * 0.7f);
        using (var pen = new Pen(Color.FromArgb(150, GoldDark), Math.Max(1, s))) g.DrawPath(pen, innerPath);
        // Little diamonds at the top corners.
        Diamond(g, new PointF(r.Left + cut, r.Top), 5 * s);
        Diamond(g, new PointF(r.Right - cut, r.Top), 5 * s);
    }

    /// <summary>Ornament above the game name; <paramref name="area"/> is the box it may fill.</summary>
    public virtual void DrawCrest(Graphics g, Rectangle area, float s)
    {
        var c = new PointF(area.Left + area.Width / 2f, area.Top + area.Height * 0.55f);
        var big = Math.Min(area.Height * 0.42f, 26 * s);
        using (var pen = new Pen(Gold, 1.5f * s))
        {
            g.DrawLine(pen, c.X - 150 * s, c.Y, c.X - big * 1.6f, c.Y);
            g.DrawLine(pen, c.X + big * 1.6f, c.Y, c.X + 150 * s, c.Y);
        }
        Diamond(g, new PointF(c.X - 158 * s, c.Y), 4 * s);
        Diamond(g, new PointF(c.X + 158 * s, c.Y), 4 * s);
        Diamond(g, c, big);
        using var gem = new GraphicsPath();
        var h = big * 0.55f;
        gem.AddPolygon([new PointF(c.X, c.Y - h), new PointF(c.X + h, c.Y), new PointF(c.X, c.Y + h), new PointF(c.X - h, c.Y)]);
        using var gb = new LinearGradientBrush(new RectangleF(c.X - h, c.Y - h, 2 * h, 2 * h), Color.FromArgb(0x9A, 0x22, 0x18), Color.FromArgb(0x4A, 0x0C, 0x08), LinearGradientMode.ForwardDiagonal);
        g.FillPath(gb, gem);
        using var gp = new Pen(Color.FromArgb(0xF8, 0xE2, 0xA8), Math.Max(1, s));
        g.DrawPath(gp, gem);
    }

    /// <summary>Rule under the game name, centred in <paramref name="area"/>.</summary>
    public virtual void DrawDivider(Graphics g, Rectangle area, float s)
    {
        var y = area.Top + area.Height / 2f;
        using (var brush = new LinearGradientBrush(new RectangleF(area.Left, y - 1, area.Width, 2), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
        {
            brush.InterpolationColors = new ColorBlend { Colors = [Color.FromArgb(0, Gold), Gold, Color.FromArgb(0, Gold)], Positions = [0f, 0.5f, 1f] };
            g.FillRectangle(brush, area.Left, y - 0.75f * s, area.Width, 1.5f * s);
        }
        Diamond(g, new PointF(area.Left + area.Width / 2f, y), 5 * s);
    }

    public virtual void DrawButton(Graphics g, Rectangle r, ButtonKind kind, ButtonState state, float s)
    {
        if (state == ButtonState.Pressed) r.Offset(0, (int)Math.Round(s));
        var cut = (kind == ButtonKind.Primary ? 12 : 8) * s;
        using var path = Chamfer(r, cut);
        (Color top, Color bottom) = (kind, state) switch
        {
            (_, ButtonState.Disabled) => (Color.FromArgb(0x3A, 0x34, 0x2C), Color.FromArgb(0x26, 0x22, 0x1D)),
            (ButtonKind.Primary, ButtonState.Hover) => (Color.FromArgb(0xD8, 0x44, 0x2A), Color.FromArgb(0x86, 0x1C, 0x10)),
            (ButtonKind.Primary, ButtonState.Pressed) => (Color.FromArgb(0x7A, 0x1A, 0x10), Color.FromArgb(0x52, 0x10, 0x0A)),
            (ButtonKind.Primary, _) => (Color.FromArgb(0xA4, 0x2A, 0x1A), Color.FromArgb(0x5E, 0x12, 0x0B)),
            (ButtonKind.Secondary, ButtonState.Hover) => (Color.FromArgb(0x4A, 0x3C, 0x2A), Color.FromArgb(0x2A, 0x21, 0x17)),
            (ButtonKind.Secondary, ButtonState.Pressed) => (Color.FromArgb(0x24, 0x1C, 0x13), Color.FromArgb(0x1A, 0x14, 0x0E)),
            _ => (Color.FromArgb(0x36, 0x2C, 0x20), Color.FromArgb(0x1E, 0x18, 0x11)),
        };
        using (var fill = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical)) g.FillPath(fill, path);
        if (state != ButtonState.Disabled)
        {
            // Sheen on the upper half.
            var upper = new Rectangle(r.X, r.Y, r.Width, r.Height / 2);
            using var sheen = new LinearGradientBrush(upper, Color.FromArgb(state == ButtonState.Hover ? 60 : 36, Color.White), Color.FromArgb(0, Color.White), LinearGradientMode.Vertical);
            var clip = g.Clip; g.SetClip(path, CombineMode.Intersect);
            g.FillRectangle(sheen, upper);
            g.Clip = clip;
        }
        var border = state == ButtonState.Disabled ? Color.FromArgb(0x5C, 0x52, 0x44) : state == ButtonState.Hover ? Color.FromArgb(0xFF, 0xDE, 0x8C) : Gold;
        using (var pen = new Pen(border, (kind == ButtonKind.Primary ? 2f : 1.5f) * s)) g.DrawPath(pen, path);
        if (kind == ButtonKind.Primary)
        {
            var inner = Rectangle.Inflate(r, -(int)(4 * s), -(int)(4 * s));
            using var ip = Chamfer(inner, cut * 0.6f);
            using var pen = new Pen(Color.FromArgb(state == ButtonState.Disabled ? 40 : 110, border), Math.Max(1, s));
            g.DrawPath(pen, ip);
        }
    }

    public virtual void DrawProgress(Graphics g, Rectangle r, double fraction, float s)
    {
        var cut = 6 * s;
        using var path = Chamfer(r, cut);
        using (var track = new SolidBrush(Color.FromArgb(230, 0x0C, 0x0A, 0x08))) g.FillPath(track, path);
        var inner = Rectangle.Inflate(r, -(int)(3 * s), -(int)(3 * s));
        var w = (int)Math.Round(inner.Width * Math.Clamp(fraction, 0, 1));
        if (w > 0)
        {
            var fillRect = inner with { Width = w };
            using var fp = Chamfer(inner, cut * 0.6f);
            var clip = g.Clip;
            g.SetClip(fp, CombineMode.Intersect);
            g.SetClip(fillRect, CombineMode.Intersect);
            using (var fill = new LinearGradientBrush(inner, Color.FromArgb(0xF2, 0xC2, 0x5E), Color.FromArgb(0x9C, 0x5E, 0x16), LinearGradientMode.Vertical)) g.FillRectangle(fill, inner);
            using (var sheen = new SolidBrush(Color.FromArgb(50, Color.White))) g.FillRectangle(sheen, inner.X, inner.Y, inner.Width, inner.Height / 2f);
            g.Clip = clip;
        }
        using var pen = new Pen(Gold, 1.5f * s);
        g.DrawPath(pen, path);
    }

    /// <summary>Frame round the log; the log text box sits inside <see cref="LogInset"/>.</summary>
    public virtual void DrawLogFrame(Graphics g, Rectangle r, float s)
    {
        using var path = Chamfer(r, 8 * s);
        using (var fill = new SolidBrush(Color.FromArgb(245, LogBack))) g.FillPath(fill, path);
        using var pen = new Pen(Color.FromArgb(200, Gold), 1.5f * s);
        g.DrawPath(pen, path);
    }

    public virtual Padding LogInset(float s) => new((int)(14 * s), (int)(12 * s), (int)(8 * s), (int)(12 * s));

    public virtual void Dispose() { }

    protected static GraphicsPath Chamfer(RectangleF r, float c)
    {
        var p = new GraphicsPath();
        p.AddPolygon([
            new PointF(r.Left + c, r.Top), new PointF(r.Right - c, r.Top), new PointF(r.Right, r.Top + c), new PointF(r.Right, r.Bottom - c),
            new PointF(r.Right - c, r.Bottom), new PointF(r.Left + c, r.Bottom), new PointF(r.Left, r.Bottom - c), new PointF(r.Left, r.Top + c),
        ]);
        return p;
    }

    protected static void Diamond(Graphics g, PointF c, float h)
    {
        using var path = new GraphicsPath();
        path.AddPolygon([new PointF(c.X, c.Y - h), new PointF(c.X + h, c.Y), new PointF(c.X, c.Y + h), new PointF(c.X - h, c.Y)]);
        using var b = new LinearGradientBrush(new RectangleF(c.X - h, c.Y - h, 2 * h + 1, 2 * h + 1), Color.FromArgb(0xFF, 0xE8, 0xA8), Color.FromArgb(0x9A, 0x6A, 0x22), LinearGradientMode.ForwardDiagonal);
        g.FillPath(b, path);
    }
}

/// <summary>
/// A look loaded from LauncherSkin.pak (see <see cref="SkinPack"/>). Every piece is optional: anything the
/// skin leaves out is drawn by the built-in look.
/// </summary>
internal sealed class SkinTheme : LauncherTheme
{
    private sealed class Piece : IDisposable
    {
        public required Bitmap Image;
        public Padding Slice;   // nine-slice borders, image pixels
        public Padding Inset;   // content area inside the piece, image pixels
        public float Scale = 1; // image pixels -> logical pixels
        public void Dispose() => Image.Dispose();
    }

    private sealed record ImageSpec(string File, int[]? Slice, int[]? Inset, float? Scale);
    private sealed record Manifest(string? Name, Dictionary<string, string>? Colors, Dictionary<string, ImageSpec>? Images);

    private readonly Dictionary<string, Piece> _pieces = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _name;
    public override string Name => _name;

    private SkinTheme(string name) { _name = name; }

    public static SkinTheme Load(string packFile)
    {
        var files = SkinPack.Open(packFile);
        var json = files[SkinPack.ManifestName].AsSpan();
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) json = json[3..]; // tolerate a UTF-8 BOM (PowerShell writes one)
        var manifest = JsonSerializer.Deserialize<Manifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? throw new UpdaterException("skin.json is empty.");
        var t = new SkinTheme(manifest.Name ?? Path.GetFileName(packFile));
        try
        {
            if (manifest.Colors is { } c)
            {
                Color Col(string key, Color fallback) => c.TryGetValue(key, out var v) ? ColorTranslator.FromHtml(v) : fallback;
                t.Title = Col("title", t.Title); t.TitleShade = Col("titleShade", t.TitleShade); t.Text = Col("text", t.Text);
                t.Muted = Col("muted", t.Muted); t.Accent = Col("accent", t.Accent); t.LogBack = Col("logBack", t.LogBack);
                t.LogText = Col("logText", t.LogText); t.ButtonText = Col("buttonText", t.ButtonText);
            }
            foreach (var (role, spec) in manifest.Images ?? [])
            {
                if (!files.TryGetValue(spec.File, out var bytes)) throw new UpdaterException($"skin.json names {spec.File}, which is not in the pack.");
                using var ms = new MemoryStream(bytes);
                using var loaded = new Bitmap(ms);
                // Copy into a 32bpp premultiplied bitmap: independent of the stream, and the fastest format for GDI+ to draw.
                var bmp = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(bmp)) g.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);
                t._pieces[role] = new Piece { Image = bmp, Slice = Pad(spec.Slice), Inset = Pad(spec.Inset), Scale = spec.Scale ?? 1 };
            }
        }
        catch { t.Dispose(); throw; }
        return t;
    }

    private static Padding Pad(int[]? v) => v is { Length: 4 } ? new Padding(v[0], v[1], v[2], v[3]) : Padding.Empty;

    private Piece? Get(string role) => _pieces.GetValueOrDefault(role);

    public override void DrawBackground(Graphics g, Rectangle r, float s)
    {
        if (Get("background") is not { } p) { base.DrawBackground(g, r, s); return; }
        // Cover: fill the window, cropping the image's overflow evenly.
        var img = p.Image;
        var scale = Math.Max((float)r.Width / img.Width, (float)r.Height / img.Height);
        var sw = r.Width / scale; var sh = r.Height / scale;
        var src = new RectangleF((img.Width - sw) / 2, (img.Height - sh) / 2, sw, sh);
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(img, r, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        using var edge = new Pen(Color.FromArgb(200, 0x6E, 0x52, 0x26), Math.Max(1, s));
        g.DrawRectangle(edge, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    public override void DrawPanel(Graphics g, Rectangle r, float s)
    {
        if (Get("panel") is { } p) NineSlice(g, p, r, s); else base.DrawPanel(g, r, s);
    }

    public override void DrawCrest(Graphics g, Rectangle area, float s)
    {
        if (Get("crest") is { } p) Fit(g, p, area, s); else base.DrawCrest(g, area, s);
    }

    public override void DrawDivider(Graphics g, Rectangle area, float s)
    {
        if (Get("divider") is { } p) Fit(g, p, area, s); else base.DrawDivider(g, area, s);
    }

    public override void DrawButton(Graphics g, Rectangle r, ButtonKind kind, ButtonState state, float s)
    {
        var prefix = kind == ButtonKind.Primary ? "button.primary." : "button.secondary.";
        var p = Get(prefix + state.ToString().ToLowerInvariant()) ?? (state == ButtonState.Pressed ? Get(prefix + "hover") : null) ?? Get(prefix + "normal");
        if (p == null) { base.DrawButton(g, r, kind, state, s); return; }
        if (state == ButtonState.Pressed) r.Offset(0, (int)Math.Round(s));
        NineSlice(g, p, r, s);
    }

    public override void DrawProgress(Graphics g, Rectangle r, double fraction, float s)
    {
        var frame = Get("progress.frame");
        var track = Get("progress.track");
        var fill = Get("progress.fill");
        if (frame == null && track == null && fill == null) { base.DrawProgress(g, r, fraction, s); return; }
        // The bar's channel is the frame's inset; track and fill are stretched into it.
        var channel = frame == null ? r : Deflate(r, frame.Inset, frame.Scale * s);
        if (track != null) NineSlice(g, track, channel, s);
        var w = (int)Math.Round(channel.Width * Math.Clamp(fraction, 0, 1));
        if (fill != null && w > 0)
        {
            var state = g.Save();
            g.SetClip(channel with { Width = w });
            NineSlice(g, fill, channel, s);
            g.Restore(state);
        }
        if (frame != null) NineSlice(g, frame, r, s);
    }

    public override void DrawLogFrame(Graphics g, Rectangle r, float s)
    {
        if (Get("log") is { } p) NineSlice(g, p, r, s); else base.DrawLogFrame(g, r, s);
    }

    public override Padding LogInset(float s)
    {
        if (Get("log") is not { } p) return base.LogInset(s);
        var k = p.Scale * s;
        return new Padding((int)(p.Inset.Left * k), (int)(p.Inset.Top * k), (int)(p.Inset.Right * k), (int)(p.Inset.Bottom * k));
    }

    private static Rectangle Deflate(Rectangle r, Padding p, float k) =>
        Rectangle.FromLTRB(r.Left + (int)(p.Left * k), r.Top + (int)(p.Top * k), r.Right - (int)(p.Right * k), r.Bottom - (int)(p.Bottom * k));

    /// <summary>Draws the whole image as large as fits in <paramref name="area"/> (never above its own scale), centred.</summary>
    private static void Fit(Graphics g, Piece p, Rectangle area, float s)
    {
        var k = Math.Min(p.Scale * s, Math.Min((float)area.Width / p.Image.Width, (float)area.Height / p.Image.Height));
        var w = p.Image.Width * k; var h = p.Image.Height * k;
        g.DrawImage(p.Image, area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
    }

    /// <summary>Unity-style sliced sprite: corners keep their size, edges and centre stretch.</summary>
    private static void NineSlice(Graphics g, Piece p, Rectangle dst, float s)
    {
        var img = p.Image; var sl = p.Slice; var k = p.Scale * s;
        float l = sl.Left * k, t = sl.Top * k, r = sl.Right * k, b = sl.Bottom * k;
        // Too small for the borders: shrink them evenly, as Unity does.
        if (l + r > dst.Width) { var f = dst.Width / (l + r); l *= f; r *= f; }
        if (t + b > dst.Height) { var f = dst.Height / (t + b); t *= f; b *= f; }

        // Whole-pixel seams, so neighbouring slices meet without gaps or overlaps.
        int[] dx = [dst.Left, dst.Left + (int)Math.Round(l), dst.Right - (int)Math.Round(r), dst.Right];
        int[] dy = [dst.Top, dst.Top + (int)Math.Round(t), dst.Bottom - (int)Math.Round(b), dst.Bottom];
        float[] sx = [0, sl.Left, img.Width - sl.Right, img.Width];
        float[] sy = [0, sl.Top, img.Height - sl.Bottom, img.Height];

        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // stops edge pixels bleeding between slices
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 3; x++)
        {
            var sw = sx[x + 1] - sx[x]; var sh = sy[y + 1] - sy[y];
            var dw = dx[x + 1] - dx[x]; var dh = dy[y + 1] - dy[y];
            if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) continue;
            g.DrawImage(img, new Rectangle(dx[x], dy[y], dw, dh), sx[x], sy[y], sw, sh, GraphicsUnit.Pixel, attrs);
        }
    }

    public override void Dispose()
    {
        foreach (var p in _pieces.Values) p.Dispose();
        _pieces.Clear();
    }
}
