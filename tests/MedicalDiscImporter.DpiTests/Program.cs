using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MedicalDiscImporter.App;
using MedicalDiscImporter.Core;

internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
    private static IEnumerable<Control> All(Control parent)
    {
        yield return parent;
        foreach (Control child in parent.Controls)
            foreach (var descendant in All(child)) yield return descendant;
    }
    private static Rectangle Within(Control child, Control ancestor)
    {
        var bounds = child.Bounds;
        for (var parent = child.Parent; parent != ancestor; parent = parent?.Parent)
        {
            if (parent == null) throw new Exception("Missing ancestor");
            bounds.Offset(parent.Location);
        }
        return bounds;
    }
    private static void Check(bool passed, string name)
    {
        if (!passed) throw new Exception(name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--dpi", var value]) return CheckInitialDpi(int.Parse(value));
            if (args is ["--viewport"]) return CheckViewport();
            // Isolate the WinForms startup-DPI cache for each case. Do not change OS display settings.
            foreach (var dpi in new[] { 96, 120, 144, 192 })
                if (Child("--dpi", dpi.ToString()) != 0) return 1;
            return Child("--viewport");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static int Child(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Console.Write(output.GetAwaiter().GetResult());
        Console.Error.Write(error.GetAwaiter().GetResult());
        return process.ExitCode;
    }
    private static int CheckInitialDpi(int dpi)
    {
        ApplicationConfiguration.Initialize();
        // Test-only use of the .NET 10 startup cache. Never create HWNDs in this test: a real
        // 96-DPI monitor would replace the injected DPI. This verifies initial layout geometry,
        // not real high-DPI text rasterization. The application contains no DPI injection switch.
        var helper = typeof(HighDpiMode).Assembly.GetType("System.Windows.Forms.ScaleHelper")!;
        var field = helper.GetField("<InitialSystemDpi>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Exception("Update the test for the current WinForms DPI cache implementation.");
        field.SetValue(null, dpi);
        var factor = dpi / 96f;
        using var main = new MainForm();
        typeof(MainForm).GetMethod("ConfigurePreview", Hidden)!.Invoke(main, ["ready"]);
        main.PerformAutoScale();
        var viewport = main.Controls[0];
        var root = (TableLayoutPanel)viewport.Controls[0];
        Check(!main.IsHandleCreated && main.DeviceDpi == dpi, $"{dpi}: isolated startup DPI");
        Check(Math.Abs(root.RowStyles[1].Height - 330 * factor) <= 1, $"{dpi}: authored input row scales from 96 DPI");
        Check(root.MinimumSize.Height >= 820 * factor - 1, $"{dpi}: content cannot be vertically squeezed");
        foreach (var name in new[] { "isoSwitch", "scanSwitch", "dicomSwitch" })
        {
            var toggle = Field<Control>(main, name);
            Check(toggle.Height >= 56 * factor - 1, $"{dpi}: {name} reserves its full paint height");
            Check(Within(toggle, root).Bottom <= Within(Field<Control>(main, "start"), root).Top,
                $"{dpi}: {name} never overlaps start/cancel row");
        }
        Check(Field<Control>(main, "start").Height >= 44 * factor - 1, $"{dpi}: start remains touch-sized");
        Check(Field<Control>(main, "cancel").Height >= 44 * factor - 1, $"{dpi}: cancel remains touch-sized");
        Check(Field<Control>(main, "journey").Height >= 104 * factor - 1, $"{dpi}: station captions and states fit");
        var title = All(main).OfType<Label>().Single(l => l.Text == "MedicalDiscImporter");
        var instruction = All(main).OfType<Label>().Single(l => l.Text.StartsWith("患者IDとディスク"));
        Check(title.AutoSize && instruction.AutoSize && Within(title, root).Bottom <= Within(instruction, root).Top,
            $"{dpi}: heading and instruction occupy separate measured rows");
        Check(title.Height >= title.GetPreferredSize(new Size(title.Width, 0)).Height,
            $"{dpi}: heading is not clipped by a fixed row");
        var before = All(main).Select(c => c.Bounds).ToArray();
        main.PerformAutoScale();
        Check(before.SequenceEqual(All(main).Select(c => c.Bounds)), $"{dpi}: repeated scaling does not compound");
        using var settings = new SettingsForm(new ImportOptions());
        settings.PerformAutoScale();
        Check(!settings.IsHandleCreated && settings.DeviceDpi == dpi, $"{dpi}: settings uses same DPI baseline");
        var save = All(settings).OfType<Button>().Single(b => b.Text == "保存");
        Check(save.MinimumSize.Height >= 48 * factor - 1, $"{dpi}: settings save scales once");
        var eject = All(settings).OfType<CheckBox>().Single(c => c.AccessibleName == "取込完了後のディスク自動排出");
        Check(!eject.Checked && eject.AutoSize, $"{dpi}: auto eject defaults off with measured layout");
        using var enabledSettings = new SettingsForm(new ImportOptions { EjectAfterImport = true });
        Check(All(enabledSettings).OfType<CheckBox>().Single(c => c.AccessibleName == eject.AccessibleName).Checked,
            $"{dpi}: settings restores auto eject selection");
        var service = All(settings).Single(c => c.GetType().Name == "ScanServicePanel");
        var register = Field<Button>(service, "register");
        Check(Math.Abs(register.MinimumSize.Height - 44 * factor) <= 1,
            $"{dpi}: inherited service panel does not double-scale");
        Console.WriteLine($"DPI {dpi}: {checks} checks passed");
        return 0;
    }
    private static int CheckViewport()
    {
        ApplicationConfiguration.Initialize();
        using var main = new MainForm();
        typeof(MainForm).GetMethod("ConfigurePreview", Hidden)!.Invoke(main, ["ready"]);
        main.Show();
        Application.DoEvents();
        Check(Screen.FromControl(main).WorkingArea.Contains(main.Bounds), "window fits current working area");
        var viewport = (ScrollableControl)main.Controls[0];
        var root = viewport.Controls[0];
        main.MinimumSize = Size.Empty;
        main.ClientSize = new Size(860, 620);
        Application.DoEvents();
        Check(viewport.VerticalScroll.Visible, "small working area offers vertical scrolling");
        Check(viewport.HorizontalScroll.Visible, "narrow working area offers horizontal scrolling");
        Check(root.Height >= 820 && root.Width >= 960, "small window keeps content geometry intact");
        Check(Field<Control>(main, "start").Height >= 44 && Field<Control>(main, "isoSwitch").Height >= 56,
            "small window does not squeeze actions");
        main.ClientSize = new Size(1200, 900);
        Application.DoEvents();
        Check(root.Height >= viewport.ClientSize.Height - 1, "large viewport expands result area");
        Console.WriteLine($"Viewport: {checks} checks passed");
        return 0;
    }
}
