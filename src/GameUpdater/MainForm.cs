using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Updater.Core;

namespace GameUpdater;

/// <summary>
/// The launcher window: a borderless, owner-drawn fantasy frame with the game name, a status line, a progress
/// bar, Play and Verify buttons and a fold-away log. The look comes from a <see cref="LauncherTheme"/>: the
/// built-in one, or the skin in LauncherSkin.pak when the installed game has one (see docs/launcher-skin.md).
/// </summary>
internal sealed class MainForm : Form, IUpdaterUi
{
    // Logical (96 DPI) size of the window.
    private const int LogicalWidth = 960, LogicalHeight = 580;

    private sealed class Widget(string name)
    {
        public string Name { get; } = name;
        public Rectangle Bounds;
        public bool Enabled = true;
        public bool Visible = true;
        public Action? Click;
        public bool HitTest(Point p) => Visible && Bounds.Contains(p);
    }

    private readonly Runner _runner;
    private readonly RunOptions _opts;
    private readonly bool _autoLaunch;
    private readonly UpdaterConfig _config;
    private readonly string _version = Signing.CurrentVersion();
    private readonly string? _skinOverride;

    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Visible = false, TabStop = false };
    private readonly Widget _play = new("Play") { Enabled = false };
    private readonly Widget _verify = new("Verify files") { Enabled = false };
    private readonly Widget _details = new("Show details");
    private readonly Widget _minimize = new("Minimize");
    private readonly Widget _close = new("Close");
    private readonly Widget[] _widgets;

    private LauncherTheme _theme = new();
    private DateTime _skinStamp;
    private Bitmap? _backdrop; // background, title and panel: redrawn only on resize, DPI or theme change
    private Widget? _hover, _pressed;
    private string _status = "";
    private double _fraction;
    private bool _showProgress;
    private CancellationTokenSource _cts = new();
    private bool _busy;

    private float S => DeviceDpi / 96f;
    private Rectangle _titleArea, _crestArea, _dividerArea, _panel, _statusArea, _bar, _logArea;
    private Font _titleFont = null!, _statusFont = null!, _buttonFont = null!, _smallButtonFont = null!, _smallFont = null!, _logFont = null!;

    public MainForm(UpdaterConfig config, string exePath, RunOptions opts, bool autoLaunch)
    {
        _config = config;
        _opts = opts;
        _autoLaunch = autoLaunch;
        _runner = new Runner(config, exePath, this);
        _skinOverride = ArgValue(opts.OriginalArgs, "--skin");
        _widgets = [_play, _verify, _details, _minimize, _close];

        Text = $"{config.GameName} Launcher {_version}";
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        KeyPreview = true;
        ClientSize = new Size((int)(LogicalWidth * S), (int)(LogicalHeight * S));
        Controls.Add(_log);

        _play.Click = Play;
        _verify.Click = async () => await RunUpdate(_opts with { SelfUpdate = false, Verify = true });
        _details.Click = () => ShowLog(!_log.Visible);
        _minimize.Click = () => WindowState = FormWindowState.Minimized;
        _close.Click = Close;

        LoadTheme(initial: true);
        Relayout();

        Shown += async (_, _) =>
        {
            Invalidate(); Update(); // repaint everything once the final size is settled, before the update starts
            await RunUpdate(_opts);
        };
        FormClosing += (_, e) =>
        {
            if (_busy && MessageBox.Show(this, "An update is running. Stop it and close?", Text, MessageBoxButtons.YesNo) == DialogResult.No)
                e.Cancel = true;
            else _cts.Cancel();
        };
    }

    // ---- update flow (unchanged behaviour) ----

    private async Task RunUpdate(RunOptions o)
    {
        _busy = true;
        SetEnabled(_play, false); SetEnabled(_verify, false);
        _cts = new CancellationTokenSource();
        RunOutcome outcome;
        try { outcome = await _runner.RunAsync(o, _cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log("ERROR: " + ex);
            Status("Update failed.");
            outcome = RunOutcome.Failed;
        }
        finally { _busy = false; }

        if (outcome == RunOutcome.Restarting) { Close(); return; }
        if (outcome == RunOutcome.Ready) LoadTheme(initial: false); // a patch may have brought a new skin
        if (outcome == RunOutcome.Failed) ShowLog(true);
        _showProgress = outcome == RunOutcome.Ready && _fraction > 0;
        SetEnabled(_verify, outcome != RunOutcome.NotConfigured);
        SetEnabled(_play, outcome == RunOutcome.Ready);
        Invalidate();
        if (outcome == RunOutcome.Ready && _autoLaunch) Play();
    }

    private void Play()
    {
        if (!_play.Enabled) return;
        if (_runner.Launch() && _config.CloseOnLaunch) Close();
    }

    public void Status(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => Status(text)); return; }
        _status = text;
        Invalidate(_panel);
    }

    public void Progress(long done, long total)
    {
        if (InvokeRequired) { BeginInvoke(() => Progress(done, total)); return; }
        _fraction = total <= 0 ? 0 : Math.Clamp((double)done / total, 0, 1);
        _showProgress = true;
        Invalidate(_panel);
    }

    public void Log(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(text)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }

    // ---- skin ----

    private string SkinPath => _skinOverride ?? Path.Combine(_runner.InstallDir, SkinPack.FileName);

    /// <summary>Uses LauncherSkin.pak when there is one (and it changed), else the built-in look. Never fails the launcher.</summary>
    private void LoadTheme(bool initial)
    {
        LauncherTheme? next = null;
        if (_skinOverride is "none" or "builtin")
        {
            if (!initial) return;
        }
        else if (File.Exists(SkinPath))
        {
            var stamp = File.GetLastWriteTimeUtc(SkinPath);
            if (!initial && stamp == _skinStamp && _theme is SkinTheme) return;
            try { next = SkinTheme.Load(SkinPath); _skinStamp = stamp; }
            catch (Exception ex) when (ex is UpdaterException or IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
            {
                Log($"Launcher skin not used: {ex.Message}");
            }
        }
        else if (!initial && _theme is not SkinTheme) return;

        var old = _theme;
        _theme = next ?? new LauncherTheme();
        if (!ReferenceEquals(old, _theme)) old.Dispose();
        if (next != null) Log($"Launcher skin: {_theme.Name}");
        _log.BackColor = _theme.LogBack;
        _log.ForeColor = _theme.LogText;
        if (_titleFont != null) Relayout(); // the skin may place the log text differently
    }

    // ---- layout ----

    private void Relayout()
    {
        var s = S;
        int W = ClientSize.Width, H = ClientSize.Height;
        int P(float v) => (int)Math.Round(v * s);

        MakeFonts();
        _close.Bounds = new Rectangle(W - P(46), P(10), P(34), P(30));
        _minimize.Bounds = new Rectangle(W - P(84), P(10), P(34), P(30));

        _crestArea = new Rectangle(W / 2 - P(170), P(10), P(340), P(96));
        _titleArea = new Rectangle(P(40), P(104), W - P(80), P(62));
        _dividerArea = new Rectangle(W / 2 - P(260), P(164), P(520), P(40));

        _panel = new Rectangle(P(24), H - P(178), W - P(48), P(156));
        var left = _panel.Left + P(44);
        _play.Bounds = new Rectangle(_panel.Right - P(40) - P(300), _panel.Top + P(43), P(300), P(70));
        _statusArea = new Rectangle(left, _panel.Top + P(28), _play.Bounds.Left - P(32) - left, P(30));
        _bar = new Rectangle(left, _panel.Top + P(64), _statusArea.Width, P(30));
        _verify.Bounds = new Rectangle(_bar.Right - P(170), _panel.Top + P(104), P(170), P(40));
        var detailsSize = TextRenderer.MeasureText(DetailsText, _smallFont);
        _details.Bounds = new Rectangle(left - P(4), _panel.Top + P(111), detailsSize.Width + P(8), P(26));

        _logArea = new Rectangle(P(70), P(200), W - P(140), _panel.Top - P(12) - P(200));
        var inset = _theme.LogInset(s);
        _log.Bounds = Rectangle.FromLTRB(_logArea.Left + inset.Left, _logArea.Top + inset.Top, _logArea.Right - inset.Right, _logArea.Bottom - inset.Bottom);
        _log.Font = _logFont;
        DropBackdrop();
        Invalidate();
    }

    private string DetailsText => _log.Visible ? "Hide details ▴" : "Show details ▾";

    private void MakeFonts()
    {
        var s = S;
        string serif = FontInstalled("Palatino Linotype") ? "Palatino Linotype" : FontInstalled("Georgia") ? "Georgia" : FontFamily.GenericSerif.Name;
        foreach (var f in new[] { _titleFont, _statusFont, _buttonFont, _smallButtonFont, _smallFont, _logFont }) f?.Dispose();
        _titleFont = new Font(serif, 46 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        _statusFont = new Font(serif, 19 * s, FontStyle.Regular, GraphicsUnit.Pixel);
        _buttonFont = new Font(serif, 26 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        _smallButtonFont = new Font(serif, 15 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        _smallFont = new Font("Segoe UI", 12.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        _logFont = new Font("Consolas", 12 * s, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static bool FontInstalled(string name)
    {
        using var probe = new Font(name, 10f);
        return string.Equals(probe.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowLog(bool show)
    {
        _log.Visible = show;
        if (show) { _log.SelectionStart = _log.TextLength; _log.ScrollToCaret(); }
        var size = TextRenderer.MeasureText(DetailsText, _smallFont);
        _details.Bounds.Width = size.Width + (int)(8 * S);
        Invalidate();
    }

    private void SetEnabled(Widget w, bool enabled)
    {
        w.Enabled = enabled;
        if (!enabled && _hover == w) _hover = null;
        Invalidate(w.Bounds);
    }

    // ---- painting ----

    private void DropBackdrop() { _backdrop?.Dispose(); _backdrop = null; }

    private static void Quality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    private Bitmap Backdrop()
    {
        if (_backdrop != null && _backdrop.Size == ClientSize) return _backdrop;
        DropBackdrop();
        var bmp = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        Quality(g);
        var s = S;
        _theme.DrawBackground(g, ClientRectangle, s);
        _theme.DrawCrest(g, _crestArea, s);
        DrawTitle(g, _config.GameName.ToUpperInvariant(), _titleArea, s);
        _theme.DrawDivider(g, _dividerArea, s);
        var sub = $"Launcher {_version}";
        var subRect = new Rectangle((int)(16 * s), (int)(14 * s), (int)(300 * s), (int)(22 * s));
        TextRenderer.DrawText(g, sub, _smallFont, Shift(subRect, 0, (int)Math.Max(1, s)), Color.FromArgb(180, 0, 0, 0), TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, sub, _smallFont, subRect, _theme.Muted, TextFormatFlags.NoPadding);
        _theme.DrawPanel(g, _panel, s);
        return _backdrop = bmp;
    }

    /// <summary>The game name: engraved gold capitals with wide letter spacing and a dark outline.</summary>
    private void DrawTitle(Graphics g, string text, Rectangle area, float s)
    {
        using var path = new GraphicsPath();
        var em = _titleFont.Size; var tracking = 6 * s;
        using var sf = StringFormat.GenericTypographic;
        float x = 0;
        foreach (var ch in text)
        {
            var str = ch.ToString();
            if (ch != ' ') path.AddString(str, _titleFont.FontFamily, (int)_titleFont.Style, em, new PointF(x, 0), sf);
            x += g.MeasureString(str == " " ? "n" : str, _titleFont, PointF.Empty, sf).Width + tracking;
        }
        var b = path.GetBounds();
        using (var m = new Matrix())
        {
            m.Translate(area.Left + (area.Width - b.Width) / 2 - b.Left, area.Top + (area.Height - b.Height) / 2 - b.Top);
            path.Transform(m);
        }
        b = path.GetBounds();
        // Soft shadow, outline, gradient fill, then a thin bright rim on top.
        using (var shadow = (GraphicsPath)path.Clone())
        {
            using var m = new Matrix(); m.Translate(0, 3 * s); shadow.Transform(m);
            using var sb = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            using var sp = new Pen(Color.FromArgb(90, 0, 0, 0), 6 * s) { LineJoin = LineJoin.Round };
            g.DrawPath(sp, shadow); g.FillPath(sb, shadow);
        }
        using (var outline = new Pen(Color.FromArgb(0x2A, 0x1A, 0x08), 3.5f * s) { LineJoin = LineJoin.Round }) g.DrawPath(outline, path);
        using (var fill = new LinearGradientBrush(new RectangleF(b.X, b.Y - 1, b.Width, b.Height + 2), _theme.Title, _theme.TitleShade, LinearGradientMode.Vertical))
        {
            fill.InterpolationColors = new ColorBlend { Colors = [Color.FromArgb(0xFF, 0xF6, 0xD8), _theme.Title, _theme.TitleShade, _theme.Title], Positions = [0f, 0.35f, 0.7f, 1f] };
            g.FillPath(fill, path);
        }
        using var rim = new Pen(Color.FromArgb(90, 0xFF, 0xF4, 0xD0), Math.Max(1, 0.8f * s));
        g.DrawPath(rim, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(Backdrop(), 0, 0);
        Quality(g);
        var s = S;

        if (_log.Visible) _theme.DrawLogFrame(g, _logArea, s);

        // Status line and percentage.
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var pct = _showProgress && _busy ? $"{_fraction * 100:0}%" : "";
        var pctWidth = pct.Length > 0 ? TextRenderer.MeasureText(pct, _statusFont).Width + (int)(10 * s) : 0;
        var statusRect = _statusArea with { Width = _statusArea.Width - pctWidth };
        TextRenderer.DrawText(g, _status, _statusFont, Shift(statusRect, 0, (int)Math.Max(1, s)), Color.FromArgb(200, 0, 0, 0), flags);
        TextRenderer.DrawText(g, _status, _statusFont, statusRect, _theme.Text, flags);
        if (pct.Length > 0)
            TextRenderer.DrawText(g, pct, _statusFont, _statusArea, _theme.Accent, flags | TextFormatFlags.Right);

        _theme.DrawProgress(g, _bar, _showProgress ? _fraction : 0, s);

        DrawButton(g, _play, ButtonKind.Primary, "PLAY", _buttonFont);
        DrawButton(g, _verify, ButtonKind.Secondary, "Verify files", _smallButtonFont);

        // "Show details" link.
        var linkColor = _hover == _details ? _theme.Title : _theme.Muted;
        TextRenderer.DrawText(g, DetailsText, _smallFont, _details.Bounds, linkColor, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

        DrawWindowButton(g, _minimize, close: false);
        DrawWindowButton(g, _close, close: true);
    }

    private ButtonState StateOf(Widget w) =>
        !w.Enabled ? ButtonState.Disabled : _pressed == w && _hover == w ? ButtonState.Pressed : _hover == w ? ButtonState.Hover : ButtonState.Normal;

    private void DrawButton(Graphics g, Widget w, ButtonKind kind, string label, Font font)
    {
        var state = StateOf(w);
        _theme.DrawButton(g, w.Bounds, kind, state, S);
        var r = w.Bounds;
        if (state == ButtonState.Pressed) r.Offset(0, (int)Math.Round(S));
        var color = state == ButtonState.Disabled ? Color.FromArgb(0x8A, 0x80, 0x70) : _theme.ButtonText;
        var f = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        if (state != ButtonState.Disabled)
            TextRenderer.DrawText(g, label, font, Shift(r, 0, (int)Math.Max(1, 1.5f * S)), Color.FromArgb(0x20, 0x0C, 0x04), f);
        TextRenderer.DrawText(g, label, font, r, color, f);
    }

    private void DrawWindowButton(Graphics g, Widget w, bool close)
    {
        var s = S;
        var hot = _hover == w;
        if (hot)
        {
            using var hb = new SolidBrush(close ? Color.FromArgb(170, 0x8E, 0x1C, 0x12) : Color.FromArgb(70, 0xE2, 0xB0, 0x52));
            g.FillRectangle(hb, w.Bounds);
        }
        var c = new PointF(w.Bounds.Left + w.Bounds.Width / 2f, w.Bounds.Top + w.Bounds.Height / 2f);
        var k = 6 * s;
        using var pen = new Pen(hot ? Color.White : _theme.Title, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var shadow = new Pen(Color.FromArgb(160, 0, 0, 0), 3.5f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (var p in new[] { shadow, pen })
        {
            if (close) { g.DrawLine(p, c.X - k, c.Y - k, c.X + k, c.Y + k); g.DrawLine(p, c.X - k, c.Y + k, c.X + k, c.Y - k); }
            else g.DrawLine(p, c.X - k, c.Y + k * 0.6f, c.X + k, c.Y + k * 0.6f);
        }
    }

    // ---- input ----

    private Widget? WidgetAt(Point p) => _widgets.FirstOrDefault(w => w.HitTest(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var w = WidgetAt(e.Location);
        if (w is { Enabled: false }) w = null;
        if (w != _hover)
        {
            if (_hover != null) Invalidate(Rectangle.Inflate(_hover.Bounds, 4, 4));
            _hover = w;
            if (_hover != null) Invalidate(Rectangle.Inflate(_hover.Bounds, 4, 4));
            Cursor = _hover != null ? Cursors.Hand : Cursors.Default;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null) { Invalidate(Rectangle.Inflate(_hover.Bounds, 4, 4)); _hover = null; Cursor = Cursors.Default; }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = WidgetAt(e.Location) is { Enabled: true } w ? w : null;
        if (_pressed != null) { Capture = true; Invalidate(Rectangle.Inflate(_pressed.Bounds, 4, 4)); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var w = _pressed;
        _pressed = null;
        Capture = false;
        if (w == null) return;
        Invalidate(Rectangle.Inflate(w.Bounds, 4, 4));
        if (e.Button == MouseButtons.Left && w.Enabled && w.Bounds.Contains(e.Location)) w.Click?.Invoke();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter && _play.Enabled) { Play(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // WinForms may rescale the form while creating it; pin the intended size before it is shown.
        ClientSize = new Size((int)(LogicalWidth * S), (int)(LogicalHeight * S));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ClientSize = new Size((int)(LogicalWidth * e.DeviceDpiNew / 96f), (int)(LogicalHeight * e.DeviceDpiNew / 96f));
        Relayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_titleFont != null) Relayout();
    }

    // ---- borderless window plumbing ----

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000, WS_MINIMIZEBOX = 0x00020000, WS_SYSMENU = 0x00080000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            cp.Style |= WS_MINIMIZEBOX | WS_SYSMENU; // taskbar click minimizes; Alt+Space menu works
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // DeviceDpi is only certain once the window exists (it may open on a monitor with a different scale).
        var want = new Size((int)(LogicalWidth * S), (int)(LogicalHeight * S));
        if (ClientSize != want) ClientSize = want; else Relayout();
        try
        {
            int round = 2; // DWMWCP_ROUND: Windows 11 rounded corners (ignored on Windows 10)
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            SetWindowTheme(_log.Handle, "DarkMode_Explorer", null); // dark scrollbar on the log
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2;
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT)
        {
            // Drag the window by anything that isn't a button, the log, or the bottom panel.
            var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            if (WidgetAt(p) == null && !_panel.Contains(p) && !(_log.Visible && _logArea.Contains(p)))
                m.Result = HTCAPTION;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DropBackdrop();
            _theme.Dispose();
            foreach (var f in new[] { _titleFont, _statusFont, _buttonFont, _smallButtonFont, _smallFont, _logFont }) f?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Rectangle Shift(Rectangle r, int dx, int dy) { r.Offset(dx, dy); return r; }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);
}
