namespace MedicalDiscImporter.App;

internal sealed class FlatTileButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Enabled) { base.OnPaint(e); return; }
        e.Graphics.Clear(Color.FromArgb(239, 243, 246));
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(151, 167, 180),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
