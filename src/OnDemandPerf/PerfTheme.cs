using System.ComponentModel;

namespace OnDemandPerf;

internal static class PerfTheme
{
    internal static readonly Color Background = Color.FromArgb(23, 25, 34);
    internal static readonly Color Panel = Color.FromArgb(16, 21, 26);
    internal static readonly Color Border = Color.FromArgb(48, 60, 69);
    internal static readonly Color Text = Color.FromArgb(225, 234, 237);
    internal static readonly Color Muted = Color.FromArgb(153, 172, 180);
    internal static readonly Color Cyan = Color.FromArgb(69, 228, 237);
    internal static readonly Color Green = Color.FromArgb(104, 234, 113);
    internal static readonly Color Purple = Color.FromArgb(209, 137, 246);
    internal static readonly Color Amber = Color.FromArgb(247, 192, 92);
    internal static readonly Color Red = Color.FromArgb(255, 107, 107);

    internal static Icon LoadIcon()
    {
        using var stream = typeof(PerfTheme).Assembly.GetManifestResourceStream("OnDemandPerf.Assets.OnDemandPerf.ico")
            ?? throw new InvalidOperationException("Application icon is missing.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}

internal sealed class PerfButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Primary { get; init; }
    public PerfButton()
    {
        AutoSize = false;
        Size = new Size(140, 36);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var fill = Enabled && Primary ? PerfTheme.Cyan : PerfTheme.Panel;
        if (Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position)))
            fill = Primary ? Color.FromArgb(120, 244, 248) : Color.FromArgb(39, 51, 60);
        using var brush = new SolidBrush(fill);
        e.Graphics.FillRectangle(brush, ClientRectangle);
        using var pen = new Pen(Enabled ? (Primary ? PerfTheme.Cyan : PerfTheme.Border) : PerfTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        var foreground = !Enabled ? PerfTheme.Muted : Primary ? PerfTheme.Background : PerfTheme.Text;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, foreground,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), foreground, fill);
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
}

internal sealed class StageCard : Control
{
    private readonly Font headingFont = new("Consolas", 9, FontStyle.Bold);
    private readonly Font valueFont = new("Segoe UI", 16, FontStyle.Bold);
    private readonly Font captionFont = new("Segoe UI", 8);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Heading { get; init; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color Accent { get; init; }
    private string value = "—", caption = "Waiting", state = "waiting";
    private int phase;
    internal void UpdateState(string newState, string newValue, string newCaption)
    {
        state = newState; value = newValue; caption = newCaption;
        AccessibleName = $"{Heading}: {caption}, {value}";
        Invalidate();
    }
    internal void Advance() { if (state == "running") { phase = (phase + 1) % 12; Invalidate(); } }
    public StageCard() { DoubleBuffered = true; BackColor = PerfTheme.Panel; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = DeviceDpi / 96f;
        var g = e.Graphics;
        using var border = new Pen(state == "running" ? Accent : PerfTheme.Border);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        int pad = (int)(12 * scale);
        TextRenderer.DrawText(g, Heading, headingFont, new Point(pad, (int)(10 * scale)), Accent);
        TextRenderer.DrawText(g, value, valueFont, new Rectangle(pad, (int)(29 * scale), Width - pad * 2, (int)(33 * scale)), PerfTheme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, caption, captionFont, new Rectangle(pad, (int)(64 * scale), Width - pad * 2, (int)(22 * scale)), PerfTheme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        float segment = (Width - pad * 2f) / 12;
        for (int i = 0; i < 12; i++)
        {
            bool lit = state == "success" || (state == "running" && (i + 12 - phase) % 12 < 4);
            var color = state == "partial" ? PerfTheme.Amber : lit ? Accent : Color.FromArgb(31, 48, 57);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, pad + i * segment, Height - 13 * scale, Math.Max(1, segment - 2 * scale), 5 * scale);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { headingFont.Dispose(); valueFont.Dispose(); captionFont.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class SegmentedActivity : Control
{
    private int phase;
    private ProgressBarStyle style;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal ProgressBarStyle Style { get => style; set { style = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Value { set { phase = value; Invalidate(); } }
    internal void Advance() { phase = (phase + 1) % 40; if (style == ProgressBarStyle.Marquee) Invalidate(); }
    public SegmentedActivity() { DoubleBuffered = true; AccessibleName = "Test activity indicator"; }
    protected override void OnPaint(PaintEventArgs e)
    {
        float width = Width / 40f;
        for (int i = 0; i < 40; i++)
        {
            bool lit = style == ProgressBarStyle.Marquee && (i + 40 - phase) % 40 < 7;
            using var brush = new SolidBrush(lit ? PerfTheme.Cyan : Color.FromArgb(27, 51, 65));
            e.Graphics.FillRectangle(brush, i * width, 0, Math.Max(1, width - 2), Height);
        }
    }
}

internal sealed class PerfMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => PerfTheme.Panel;
    public override Color ImageMarginGradientBegin => PerfTheme.Panel;
    public override Color ImageMarginGradientMiddle => PerfTheme.Panel;
    public override Color ImageMarginGradientEnd => PerfTheme.Panel;
    public override Color MenuItemSelected => Color.FromArgb(36, 65, 75);
    public override Color MenuItemBorder => PerfTheme.Cyan;
    public override Color MenuBorder => PerfTheme.Border;
    public override Color SeparatorDark => PerfTheme.Border;
    public override Color SeparatorLight => PerfTheme.Border;
}

