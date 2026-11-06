using System.Drawing.Drawing2D;

namespace MedicalDiscImporter.App;

internal sealed class ImportSwitch : CheckBox
{
    public ImportSwitch(string text)
    {
        Text = text;
        AccessibleName = text;
        Checked = true;
        AutoSize = false;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Cursor = Cursors.Hand;
        Font = new Font("Yu Gothic UI", 10.5f);
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var ink = Enabled ? Color.FromArgb(29, 53, 71) : Color.FromArgb(151, 167, 180);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, (int)(23 * scale)), ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var x = (Width - 72 * scale) / 2;
        var y = 27 * scale;
        var h = 20 * scale;
        var w = 38 * scale;
        using var track = new SolidBrush(Enabled && Checked ? Color.FromArgb(8, 125, 137) : Color.FromArgb(188, 201, 212));
        g.FillRectangle(track, x + h / 2, y, w - h, h);
        g.FillEllipse(track, x, y, h, h);
        g.FillEllipse(track, x + w - h, y, h, h);
        using var thumb = new SolidBrush(Color.White);
        g.FillEllipse(thumb, x + (Checked ? w - 17 * scale : 3 * scale), y + 3 * scale, 14 * scale, 14 * scale);
        TextRenderer.DrawText(g, Checked ? "ON" : "OFF", Font,
            Rectangle.Round(new RectangleF(x + w + 5 * scale, y - scale, 34 * scale, h + 2 * scale)), ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -2, -2));
    }

    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}
