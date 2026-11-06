using MedicalDiscImporter.Core;

namespace MedicalDiscImporter.App;

public sealed class SettingsForm : DpiAwareForm
{
    public SettingsForm(ImportOptions options)
    {
        Text = "出力先・ウイルスチェックの設定";
        Icon = AppBrand.Icon;
        Font = new Font("Yu Gothic UI", 12);
        ClientSize = new Size(960, 820);
        MinimumSize = new Size(850, 700);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18), ColumnCount = 3, RowCount = 9 };
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        for (var row = 0; row < 8; row++) layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
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
        layout.Controls.Add(new Label { Text = "DICOM検査名", AutoSize = true }, 0, 2);
        var exam = new TextBox { Text = options.RsBaseExamName, Dock = DockStyle.Fill, MaxLength = 40, AccessibleName = "DICOM検査名" };
        layout.Controls.Add(exam, 1, 2);
        layout.Controls.Add(new Label { Text = "既定：CTMRI", AutoSize = true }, 2, 2);
        layout.Controls.Add(new Label { Text = "ISO検査名", AutoSize = true }, 0, 3);
        var isoExam = new TextBox { Text = options.IsoExamName, Dock = DockStyle.Fill, MaxLength = 40, AccessibleName = "ISO検査名" };
        layout.Controls.Add(isoExam, 1, 3);
        layout.Controls.Add(new Label { Text = "既定：CDROM", AutoSize = true }, 2, 3);
        layout.Controls.Add(new Label { Text = "Defender定義の有効日数", AutoSize = true }, 0, 4);
        var days = new NumericUpDown { Minimum = 1, Maximum = 30, Value = Math.Clamp(options.SignatureMaxAgeDays, 1, 30), Width = 100 };
        layout.Controls.Add(days, 1, 4);
        var service = new GroupBox { Text = "ウイルスチェックサービス", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12) };
        var servicePanel = new ScanServicePanel();
        service.Controls.Add(servicePanel);
        FormClosing += (_, e) =>
        {
            // The elevated helper must finish before the status view is discarded.
            if (servicePanel.MaintenanceInProgress) e.Cancel = true;
        };
        layout.Controls.Add(service, 0, 5);
        layout.SetColumnSpan(service, 3);
        var note = new Label
        {
            Text = "ISO：ID~000001~取込日~ISO検査名~RSB.iso\r\nDICOM：ID~000001~撮影日~DICOM検査名~RSB.dcm（日付はyyyy_MM_dd）\r\n各出力先で連番を記録し、RSBaseへ受け渡します。DICOMの内容は変更しません。\r\nRSBase側で出力先の監視を設定してください。\r\nAutoPlay/AutoRunを無効にし、ディスク内Viewerは起動しないでください。\r\n作業データ・出力先のアクセス権と保管期限は施設で管理してください。\r\n作業領域：" + PrototypeImporter.WorkRoot,
            AutoSize = true, MaximumSize = new Size(760, 0), Padding = new Padding(0, 16, 0, 10)
        };
        layout.Controls.Add(note, 0, 6);
        layout.SetColumnSpan(note, 3);
        var save = new Button { Text = "保存", AutoSize = true, MinimumSize = new Size(120, 48) };
        save.Click += (_, _) =>
        {
            try
            {
                (options with { IsoDirectory = iso.Text.Trim(), DicomDirectory = dicom.Text.Trim(), RsBaseExamName = exam.Text.Trim(), IsoExamName = isoExam.Text.Trim(), SignatureMaxAgeDays = (int)days.Value }).Save();
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を保存できません", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        layout.Controls.Add(save, 2, 7);
        CompleteLayout();
    }
}
