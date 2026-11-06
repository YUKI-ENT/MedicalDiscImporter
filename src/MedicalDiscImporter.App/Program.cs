namespace MedicalDiscImporter.App;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // SCM entry point must run before WinForms and the interactive application's mutex.
        if (args is ["--scan-service"])
        {
            try { ScanWindowsService.Run(); }
            catch { Environment.ExitCode = 1; }
            return;
        }
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        if (args is ["--install-scan-service"] or ["--remove-scan-service"] or ["--start-scan-service"] or ["--stop-scan-service"])
        {
            try
            {
                ServiceRegistration.Execute(args[0] switch
                {
                    "--install-scan-service" => ServiceMaintenanceAction.Install,
                    "--remove-scan-service" => ServiceMaintenanceAction.Remove,
                    "--start-scan-service" => ServiceMaintenanceAction.Start,
                    _ => ServiceMaintenanceAction.Stop
                });
            }
            catch (Exception ex)
            {
                var message = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1072 }
                    ? "以前のサービス削除が保留されています。Windowsの「サービス」管理画面を閉じてから、もう一度登録・更新してください。"
                    : ex.Message;
                var details = ex is System.ComponentModel.Win32Exception win32 ? $"\r\nWindowsエラー：{win32.NativeErrorCode}" : "";
                MessageBox.Show(message + details, "検査サービスを設定できません", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
            return;
        }
        if (args is ["--preview-settings", var settingsPath])
        {
            using var form = new SettingsForm(new MedicalDiscImporter.Core.ImportOptions());
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.GetFullPath(settingsPath));
            return;
        }
        if (args is ["--preview-state", var previewState, var previewPath])
        {
            using var form = new MainForm();
            form.ConfigurePreview(previewState);
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.GetFullPath(previewPath));
            return;
        }
        if (args is ["--preview", var path])
        {
            using var form = new MainForm();
            form.ConfigurePreview("waiting");
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
