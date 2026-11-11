using System.Drawing.Drawing2D;

namespace MedicalDiscImporter.App;

internal sealed class DiscIndicator : Control
{
    private bool ready;

    [System.ComponentModel.DefaultValue(false)]
    public bool Ready
    {
        get => ready;
        set
        {
            if (ready == value) return;
            ready = value;
            Invalidate();
        }
    }

    public DiscIndicator()
    {
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        BackColor = Color.White;
        AccessibleName = "ディスク未認識";
        AccessibleRole = AccessibleRole.Graphic;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var diameter = Math.Min(22 * scale, Math.Min(Width, Height) - 2 * scale);
        if (diameter <= 0) return;
        var disc = new RectangleF((Width - diameter) / 2, (Height - diameter) / 2, diameter, diameter);
        var face = Ready ? Color.FromArgb(73, 214, 124) : Color.FromArgb(185, 193, 201);
        var edge = Ready ? Color.FromArgb(28, 159, 84) : Color.FromArgb(134, 145, 157);
        using var surface = new SolidBrush(face);
        using var reflection = new SolidBrush(Color.FromArgb(195, Color.White));
        using var outline = new Pen(edge, scale);
        g.FillEllipse(surface, disc);
        g.FillPie(reflection, disc.X, disc.Y, disc.Width, disc.Height, -70, 35);
        g.FillPie(reflection, disc.X, disc.Y, disc.Width, disc.Height, 110, 35);
        g.DrawEllipse(outline, disc);
        var hubSize = diameter * 0.30f;
        var hub = new RectangleF((Width - hubSize) / 2, (Height - hubSize) / 2, hubSize, hubSize);
        using var hubBrush = new SolidBrush(edge);
        g.FillEllipse(hubBrush, hub);
        var holeSize = diameter * 0.13f;
        using var hole = new SolidBrush(BackColor);
        g.FillEllipse(hole, (Width - holeSize) / 2, (Height - holeSize) / 2, holeSize, holeSize);
    }
}
