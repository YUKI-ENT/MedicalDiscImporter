using MedicalDiscImporter.Core;

namespace MedicalDiscImporter.App;

public sealed class SettingsForm : Form
{
    public SettingsForm(ImportOptions options)
    {
        Text = "出力先の設定";
        Font = new Font("Yu Gothic UI", 12);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 450);
        MinimumSize = new Size(680, 460);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 3, RowCount = 6 };
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
        var dicom = AddFolder("DICOM出力先", options.DicomDirectory, 1);
        layout.Controls.Add(new Label { Text = "Defender定義の有効日数", AutoSize = true }, 0, 2);
        var days = new NumericUpDown { Minimum = 1, Maximum = 30, Value = Math.Clamp(options.SignatureMaxAgeDays, 1, 30), Width = 100 };
        layout.Controls.Add(days, 1, 2);
        var note = new Label
        {
            Text = "DICOMは処理ごとのサブフォルダへ元の構成・名前で出力します。\r\n拡張子のないDICOMには .dcm を付加します。RSBase用の改名・直接転送は行いません。\r\n\r\nAutoPlay/AutoRunを無効にし、ディスク内Viewerは起動しないでください。\r\n作業データ・出力先のアクセス権と保管期限は施設で管理してください。\r\n作業領域：" + PrototypeImporter.WorkRoot,
            AutoSize = true, MaximumSize = new Size(760, 0), Padding = new Padding(0, 16, 0, 10)
        };
        layout.Controls.Add(note, 0, 3);
        layout.SetColumnSpan(note, 3);
        var save = new Button { Text = "保存", AutoSize = true, MinimumSize = new Size(120, 48) };
        save.Click += (_, _) =>
        {
            try
            {
                (options with { IsoDirectory = iso.Text.Trim(), DicomDirectory = dicom.Text.Trim(), SignatureMaxAgeDays = (int)days.Value }).Save();
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を保存できません", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        layout.Controls.Add(save, 2, 4);
    }
}
