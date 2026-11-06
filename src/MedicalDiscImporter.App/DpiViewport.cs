namespace MedicalDiscImporter.App;

// Keep touch controls at their readable size. Smaller working areas scroll the content
// instead of squeezing percent rows until controls overlap or disappear.
internal sealed class DpiViewport : Panel
{
    private readonly Control content;
    private readonly Size minimumLogicalSize;
    private bool adjustmentPending;

    public DpiViewport(Control content, Size minimumLogicalSize)
    {
        this.content = content;
        this.minimumLogicalSize = minimumLogicalSize;
        AutoScroll = true;
        AutoScrollMinSize = minimumLogicalSize;
        Dock = DockStyle.Fill;
        content.Dock = DockStyle.Top;
        // These are still 96-DPI authored values; the containing form scales them on load.
        content.MinimumSize = minimumLogicalSize;
        content.Height = minimumLogicalSize.Height;
        Controls.Add(content);
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); QueueAdjustment(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); QueueAdjustment(); }

    private void QueueAdjustment()
    {
        if (!IsHandleCreated || IsDisposed || adjustmentPending) return;
        adjustmentPending = true;
        // Run after the framework finishes scaling all children, not during ScaleControl.
        BeginInvoke((Action)(() =>
        {
            adjustmentPending = false;
            if (IsDisposed) return;
            var factor = DeviceDpi / 96f;
            var minimum = new Size((int)Math.Ceiling(minimumLogicalSize.Width * factor),
                (int)Math.Ceiling(minimumLogicalSize.Height * factor));
            if (AutoScrollMinSize != minimum) AutoScrollMinSize = minimum;
            if (content.MinimumSize != minimum) content.MinimumSize = minimum;
            var height = Math.Max(minimum.Height, ClientSize.Height);
            if (content.Height != height) content.Height = height;
        }));
    }
}
