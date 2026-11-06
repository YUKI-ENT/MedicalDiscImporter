using System.Drawing.Drawing2D;
using MedicalDiscImporter.Core;

namespace MedicalDiscImporter.App;

// The highlighted station follows explicit engine phases, never percentages or translated messages.
internal sealed class ImportJourney : Control
{
    private static readonly string[] Stations = ["ディスク読込", "ISO作成・照合", "ウイルス検査", "ISO・DICOM出力"];
    private int current = -1;
    private bool complete;
    private string? stopped;
    private readonly bool[] skipped = new bool[4];
    private string outputTitle = Stations[3];
    private static readonly Color Accent = Color.FromArgb(8, 125, 137);

    public ImportJourney()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        BackColor = Color.White;
        AccessibleRole = AccessibleRole.Diagram;
        Reset();
    }

    public void Reset() { current = -1; complete = false; stopped = null; UpdateDescription(); }
    public override Size GetPreferredSize(Size proposedSize) => new(
        Math.Max(0, proposedSize.Width), (int)Math.Ceiling(110 * DeviceDpi / 96f));
    public void Configure(ImportOptions options)
    {
        skipped[1] = !options.CreateIso;
        skipped[2] = !options.ScanForMalware;
        skipped[3] = !options.CreateIso && !options.SendDicom;
        outputTitle = options.CreateIso && options.SendDicom ? "ISO・DICOM出力" : options.CreateIso ? "ISO出力" : "DICOM送信";
        UpdateDescription();
    }
    public void MoveTo(ImportPhase phase)
    {
        if ((int)phase <= current || skipped[(int)phase]) return;
        current = Math.Max(current, (int)phase);
        UpdateDescription();
    }
    public void Finish() { current = Stations.Length - 1; complete = true; stopped = null; UpdateDescription(); }
    public void Stop(bool cancelled) { stopped = cancelled ? "中止" : "停止"; UpdateDescription(); }

    private string NameAt(int index) => index == 3 ? outputTitle : Stations[index];
    private string Status(int index) => skipped[index] ? "OFF" : complete || index < current ? "完了" : index == current ? stopped ?? "処理中" : "未着手";
    private void UpdateDescription()
    {
        AccessibleName = string.Join("、", Stations.Select((_, i) => $"{NameAt(i)}：{Status(i)}"));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var cell = ClientSize.Width / 4f;
        var y = 25 * scale;
        var radius = 15 * scale;
        var muted = Color.FromArgb(208, 219, 228);
        for (var i = 0; i < Stations.Length - 1; i++)
        {
            using var pen = new Pen((complete || i < current) && !skipped[i] && !skipped[i + 1] ? Accent : muted, 3 * scale);
            if (skipped[i] || skipped[i + 1]) pen.DashStyle = DashStyle.Dot;
            g.DrawLine(pen, cell * (i + .5f) + radius + 6 * scale, y,
                cell * (i + 1.5f) - radius - 6 * scale, y);
        }
        using var titleFont = new Font(Font.FontFamily, 12, FontStyle.Bold);
        using var statusFont = new Font(Font.FontFamily, 10);
        using var numberFont = new Font(Font.FontFamily, 12, FontStyle.Bold);
        for (var i = 0; i < Stations.Length; i++)
        {
            var done = !skipped[i] && (complete || i < current);
            var active = !skipped[i] && i == current && !complete;
            var color = active && stopped != null ? Color.FromArgb(183, 68, 55) : Accent;
            var x = cell * (i + .5f);
            if (active)
            {
                using var halo = new SolidBrush(Color.FromArgb(235, 245, 246));
                g.FillEllipse(halo, x - radius - 6 * scale, y - radius - 6 * scale, 2 * radius + 12 * scale, 2 * radius + 12 * scale);
            }
            using var fill = new SolidBrush(done || active ? color : Color.FromArgb(237, 242, 246));
            g.FillEllipse(fill, x - radius, y - radius, radius * 2, radius * 2);
            var circle = Rectangle.Round(new RectangleF(x - radius, y - radius, radius * 2, radius * 2));
            TextRenderer.DrawText(g, skipped[i] ? "−" : done ? "✓" : (i + 1).ToString(), numberFont, circle,
                done || active ? Color.White : Color.FromArgb(117, 137, 152), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var titleRect = new Rectangle((int)(cell * i), (int)(52 * scale), (int)cell, (int)(25 * scale));
            TextRenderer.DrawText(g, NameAt(i), titleFont, titleRect,
                done || active ? Color.FromArgb(29, 53, 71) : Color.FromArgb(123, 140, 154), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            titleRect.Y = (int)(79 * scale);
            TextRenderer.DrawText(g, Status(i), statusFont, titleRect,
                done || active ? color : Color.FromArgb(133, 150, 163), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
