namespace MedicalDiscImporter.App;

// All code-created layout dimensions are authored at 96 DPI. Keep layout suspended until
// derived forms have added every control, so the initial high-DPI scale runs exactly once.
public abstract class DpiAwareForm : Form
{
    private Size minimumLogicalSize;

    protected DpiAwareForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    protected void CompleteLayout()
    {
        minimumLogicalSize = MinimumSize;
        ResumeLayout(performLayout: true);
    }

    protected override void OnShown(EventArgs e)
    {
        FitWorkingArea();
        base.OnShown(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!e.Cancel) FitWorkingArea();
    }

    private void FitWorkingArea()
    {
        if (WindowState != FormWindowState.Normal) return;
        var work = Screen.FromControl(this).WorkingArea;
        var factor = DeviceDpi / 96f;
        MinimumSize = new Size(Math.Min(work.Width, (int)Math.Ceiling(minimumLogicalSize.Width * factor)),
            Math.Min(work.Height, (int)Math.Ceiling(minimumLogicalSize.Height * factor)));
        var width = Math.Min(Width, work.Width);
        var height = Math.Min(Height, work.Height);
        Bounds = new Rectangle(Math.Clamp(Left, work.Left, work.Right - width),
            Math.Clamp(Top, work.Top, work.Bottom - height), width, height);
    }
}
