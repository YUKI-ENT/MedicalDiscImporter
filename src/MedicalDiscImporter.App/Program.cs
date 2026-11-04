namespace MedicalDiscImporter.App;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        if (args is ["--preview", var path])
        {
            using var form = new MainForm();
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.GetFullPath(path));
            return;
        }
        using var instance = new Mutex(true, @"Local\MedicalDiscImporter.Prototype", out var created);
        if (!created)
        {
            MessageBox.Show("既に起動しています。", "MedicalDiscImporter");
            return;
        }
        Application.Run(new MainForm());
    }
}
