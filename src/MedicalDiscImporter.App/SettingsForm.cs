using MedicalDiscImporter.Core;

namespace MedicalDiscImporter.App;

public sealed class SettingsForm : Form
{
    public SettingsForm(ImportOptions options)
    {
        Text = "出力先・ウイルスチェックの設定";
        Font = new Font("Yu Gothic UI", 12);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(920, 740);
        MinimumSize = new Size(850, 700);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18), ColumnCount = 3, RowCount = 8 };
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        Controls.Add(layout);
        TextBox AddFolder(string label, string path, int row)
        {
            var text = new TextBox { Text = path, Dock = DockStyle.Fill, AccessibleName = label };
            layout.Controls.Add(new Label { Text = label, AutoSize = true }, 0, row);
            layout.Controls.Add(text, 1, row);
            var browse = new Button { Text = "参照…", AutoSize = true, Height = 44 };
            browse.Click += (_, _) =>
            {
                using var dialog = new FolderBrowserDialog { Description = label, UseDescriptionForTitle = true };
                if (Directory.Exists(text.Text)) dialog.SelectedPath = text.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) text.Text = dialog.SelectedPath;
            };
            layout.Controls.Add(browse, 2, row);
            return text;
        }
        var iso = AddFolder("ISO保存先", options.IsoDirectory, 0);
        var dicom = AddFolder("RSBase自動取込先", options.DicomDirectory, 1);
        layout.Controls.Add(new Label { Text = "RSBase検査名", AutoSize = true }, 0, 2);
        var exam = new TextBox { Text = options.RsBaseExamName, Dock = DockStyle.Fill, MaxLength = 40, AccessibleName = "RSBase検査名" };
        layout.Controls.Add(exam, 1, 2);
        layout.Controls.Add(new Label { Text = "既定：CTMRI", AutoSize = true }, 2, 2);
        layout.Controls.Add(new Label { Text = "Defender定義の有効日数", AutoSize = true }, 0, 3);
        var days = new NumericUpDown { Minimum = 1, Maximum = 30, Value = Math.Clamp(options.SignatureMaxAgeDays, 1, 30), Width = 100 };
        layout.Controls.Add(days, 1, 3);
        var service = new GroupBox { Text = "ウイルスチェックサービス", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12) };
        var serviceLayout = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        service.Controls.Add(serviceLayout);
        var serviceState = new Label { Text = "状態：未確認", AutoSize = true, MaximumSize = new Size(820, 0), AccessibleName = "サービスの状態" };
        serviceLayout.Controls.Add(serviceState);
        serviceLayout.Controls.Add(new Label { Text = "最初に管理者が登録してください。以後は標準ユーザーで利用できます。\r\n登録・更新・開始・停止・解除には管理者承認が必要です。パスワードは保存しません。",
            AutoSize = true, MaximumSize = new Size(820, 0), Margin = new Padding(3, 8, 3, 8) });
        var serviceButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        serviceLayout.Controls.Add(serviceButtons);
        var register = new Button { Text = "サービスを登録・更新", AutoSize = true, MinimumSize = new Size(180, 44) };
        var check = new Button { Text = "接続を確認", AutoSize = true, MinimumSize = new Size(120, 44) };
        var remove = new Button { Text = "登録を解除", AutoSize = true, MinimumSize = new Size(120, 44) };
        var serviceStart = new Button { Text = "サービス開始", AutoSize = true, MinimumSize = new Size(130, 44) };
        var serviceStop = new Button { Text = "サービス停止", AutoSize = true, MinimumSize = new Size(130, 44) };
        serviceButtons.Controls.AddRange([register, check, serviceStart, serviceStop, remove]);
        async Task ServiceAction(ServiceMaintenanceAction? action)
        {
            register.Enabled = check.Enabled = remove.Enabled = serviceStart.Enabled = serviceStop.Enabled = false;
            serviceState.Text = action.HasValue ? "管理者操作の完了を待っています…" : "接続を確認中…";
            try
            {
                if (action.HasValue) await ServiceRegistration.ChangeAsync(action.Value);
                if (action == ServiceMaintenanceAction.Remove)
                    serviceState.Text = "登録の削除を要求しました。Windowsの「サービス」管理画面を開いている場合は閉じてください。";
                else if (action == ServiceMaintenanceAction.Stop) serviceState.Text = ServiceRegistration.DescribeState();
                else
                {
                    var connection = await ServiceMalwareScanner.CheckAsync();
                    serviceState.Text = connection.StartsWith("未接続") ? connection + "\r\n" + ServiceRegistration.DescribeState() : connection;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { serviceState.Text = "管理者操作がキャンセルされました。"; }
            catch (Exception ex)
            {
                serviceState.Text = "サービスの設定・確認に失敗しました。";
                MessageBox.Show(this, ex.Message, "ウイルスチェックサービス", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { register.Enabled = check.Enabled = remove.Enabled = serviceStart.Enabled = serviceStop.Enabled = true; }
        }
        register.Click += async (_, _) => await ServiceAction(ServiceMaintenanceAction.Install);
        check.Click += async (_, _) => await ServiceAction(null);
        serviceStart.Click += async (_, _) => await ServiceAction(ServiceMaintenanceAction.Start);
        serviceStop.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "検査中に停止すると、その取り込みは出力されません。サービスを停止しますか？",
                "サービス停止", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                await ServiceAction(ServiceMaintenanceAction.Stop);
        };
        remove.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "登録を解除すると、このPCで本アプリのウイルス検査ができなくなります。解除しますか？",
                "サービス登録の解除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) await ServiceAction(ServiceMaintenanceAction.Remove);
        };
        layout.Controls.Add(service, 0, 4);
        layout.SetColumnSpan(service, 3);
        var note = new Label
        {
            Text = "検査合格後、取込先の直下へ「ID~000001~yyyy_MM_dd~検査名~RSB.dcm」で出力します。\r\n連番は患者ID・撮影日ごとに続番を使用し、DICOMの内容は変更しません。\r\nRSBase側で取込先の監視を設定してください。画像は1ファイルずつ受け渡します。\r\n\r\nAutoPlay/AutoRunを無効にし、ディスク内Viewerは起動しないでください。\r\n作業データ・出力先のアクセス権と保管期限は施設で管理してください。\r\n作業領域：" + PrototypeImporter.WorkRoot,
            AutoSize = true, MaximumSize = new Size(760, 0), Padding = new Padding(0, 16, 0, 10)
        };
        layout.Controls.Add(note, 0, 5);
        layout.SetColumnSpan(note, 3);
        var save = new Button { Text = "保存", AutoSize = true, MinimumSize = new Size(120, 48) };
        save.Click += (_, _) =>
        {
            try
            {
                (options with { IsoDirectory = iso.Text.Trim(), DicomDirectory = dicom.Text.Trim(), RsBaseExamName = exam.Text.Trim(), SignatureMaxAgeDays = (int)days.Value }).Save();
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を保存できません", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        layout.Controls.Add(save, 2, 6);
    }
}
