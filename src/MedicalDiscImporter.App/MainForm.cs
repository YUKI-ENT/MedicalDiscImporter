using MedicalDiscImporter.Core;

namespace MedicalDiscImporter.App;

public sealed class MainForm : Form
{
    private readonly TextBox patient = new() { Dock = DockStyle.Fill, Font = new Font("Yu Gothic UI", 28), MaxLength = 32, AccessibleName = "患者ID" };
    private readonly ComboBox drives = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "光学ドライブ" };
    private readonly Label discStatus = new() { Dock = DockStyle.Fill, Text = "ディスクを挿入してください", AutoSize = true };
    private readonly Label stage = new() { Dock = DockStyle.Fill, Text = "待機中", AutoSize = true };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Height = 24 };
    private readonly TextBox result = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "処理結果" };
    private readonly Button start = ActionButton("取り込み開始");
    private readonly Button settings = ActionButton("出力先・設定");
    private readonly DataGridView dicomList = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoGenerateColumns = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White, AccessibleName = "DICOMシーケンス一覧", SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
    };
    private readonly Button cancel = ActionButton("中止");
    private readonly Button refresh = ActionButton("ディスクを再確認");
    private readonly TableLayoutPanel keypad = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4 };
    private CancellationTokenSource? cancellation;
    private bool busy;

    public MainForm()
    {
        Text = "MedicalDiscImporter ｜ ディスク取り込み";
        Font = new Font("Yu Gothic UI", 13);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1200, 940);
        MinimumSize = new Size(920, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(243, 246, 250);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "他院ディスクの取り込み", AutoSize = true, Font = new Font(Font.FontFamily, 24, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(new Label { Text = "再構築ISO ｜ Defender合格後にISO・DICOM出力 ｜ 実機受入試験中", ForeColor = Color.DarkRed, AutoSize = true, Padding = new Padding(0, 8, 0, 14) }, 0, 1);

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        layout.Controls.Add(main, 0, 2);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1, Padding = new Padding(0, 0, 24, 0) };
        fields.Controls.Add(new Label { Text = "患者ID（半角数字）", AutoSize = true });
        fields.Controls.Add(patient);
        fields.Controls.Add(new Label { Text = "光学ドライブ", AutoSize = true, Padding = new Padding(0, 10, 0, 0) });
        fields.Controls.Add(drives);
        fields.Controls.Add(discStatus);
        fields.Controls.Add(refresh);
        fields.Controls.Add(new Label { Text = "原本のトラック・セッション情報は保存されません。\r\nディスク内のViewer等は起動しないでください。", AutoSize = true, ForeColor = Color.DimGray });
        main.Controls.Add(fields, 0, 0);
        main.Controls.Add(keypad, 1, 0);
        for (var c = 0; c < 3; c++) keypad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        for (var r = 0; r < 4; r++) keypad.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        foreach (var key in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "消去", "0", "⌫" })
        {
            var button = ActionButton(key);
            button.Font = new Font(Font.FontFamily, 22);
            button.Click += (_, _) =>
            {
                if (key == "消去") patient.Clear();
                else if (key == "⌫") patient.Text = patient.Text.Length > 0 ? patient.Text[..^1] : "";
                else if (patient.Text.Length < patient.MaxLength) patient.AppendText(key);
            };
            keypad.Controls.Add(button);
        }

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 12, 0, 12) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        start.BackColor = Color.FromArgb(25, 85, 145);
        start.ForeColor = Color.White;
        actions.Controls.Add(start, 0, 0);
        actions.Controls.Add(cancel, 1, 0);
        settings.Click += (_, _) => ShowSettings();
        actions.Controls.Add(settings, 2, 0);
        layout.Controls.Add(actions, 0, 3);
        var output = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        output.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        output.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        output.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        output.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        output.Controls.Add(stage, 0, 0);
        output.Controls.Add(progress, 0, 1);
        output.Controls.Add(result, 0, 2);
        output.Controls.Add(dicomList, 0, 3);
        dicomList.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        foreach (var (title, property, weight) in new[] { ("患者名", "PatientName", 23f), ("撮影日", "Date", 14f),
                     ("日付の取得元", "DateSource", 14f), ("シリーズ", "Series", 10f), ("シーケンス", "Sequence", 31f), ("ファイル数", "Count", 8f) })
            dicomList.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, DataPropertyName = property, FillWeight = weight,
                MinimumWidth = property == "DateSource" ? 150 : property == "Count" ? 110 : 100 });
        layout.Controls.Add(output, 0, 4);

        cancel.Enabled = false;
        refresh.Click += (_, _) => RefreshDrives();
        drives.SelectedIndexChanged += (_, _) => UpdateDiscStatus();
        start.Click += async (_, _) => await StartAsync();
        cancel.Click += (_, _) => { cancellation?.Cancel(); cancel.Enabled = false; stage.Text = "中止を要求しました。後始末完了までお待ちください。"; };
        FormClosing += (_, e) =>
        {
            if (busy)
            {
                e.Cancel = true;
                cancellation?.Cancel();
                stage.Text = "中止・マウント解除の完了後に閉じてください。";
            }
        };
        Shown += (_, _) => RefreshDrives();
    }

    private static Button ActionButton(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, AutoSize = true, MinimumSize = new Size(72, 56),
        Margin = new Padding(4), FlatStyle = FlatStyle.Flat, BackColor = Color.White, Padding = new Padding(6)
    };

    private void RefreshDrives()
    {
        var selected = drives.SelectedItem as string;
        drives.Items.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom)) drives.Items.Add(drive.Name);
        if (selected != null && drives.Items.Contains(selected)) drives.SelectedItem = selected;
        else if (drives.Items.Count > 0) drives.SelectedIndex = 0;
        UpdateDiscStatus();
    }

    private void UpdateDiscStatus()
    {
        try
        {
            var drive = drives.SelectedItem is string root ? new DriveInfo(root) : null;
            discStatus.Text = drive?.IsReady == true ? $"認識済み：{drive.DriveFormat} / {drive.TotalSize / 1048576:N0} MiB" : "読み取り可能なディスクがありません";
        }
        catch (IOException) { discStatus.Text = "ディスクを確認できません。再確認してください。"; }
    }

    private async Task StartAsync()
    {
        if (busy) return;
        try
        {
            InputRules.ValidatePatientId(patient.Text);
            var options = ImportOptions.Load();
            options.CheckWritable();
            if (drives.SelectedItem is not string root || !new DriveInfo(root).IsReady) throw new IOException("ディスクを挿入してください。");
            if (MessageBox.Show(this, $"患者ID：{patient.Text}\r\nドライブ：{root}\r\nDefender合格後に次のフォルダへ出力します。\r\nISO：{options.IsoDirectory}\r\nDICOM：{options.DicomDirectory}", "開始確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            busy = true;
            patient.Enabled = drives.Enabled = keypad.Enabled = start.Enabled = refresh.Enabled = settings.Enabled = false;
            cancel.Enabled = true;
            result.Clear();
            dicomList.DataSource = null;
            cancellation = new CancellationTokenSource();
            var reporter = new Progress<ImportProgress>(p =>
            {
                if (!busy) return;
                stage.Text = cancellation?.IsCancellationRequested == true ? "中止要求済み：後始末までお待ちください。" : p.Stage;
                progress.Style = p.Percent.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                if (p.Percent.HasValue) progress.Value = Math.Clamp(p.Percent.Value, 0, 100);
            });
            var importer = new PrototypeImporter(new ImapiImageBuilder(), new WindowsImageMounter());
            var completed = await importer.RunAsync(root, patient.Text, reporter, cancellation.Token);
            var catalogProgress = new Progress<IReadOnlyList<DicomEntry>>(ShowDicoms);
            var imported = await new ImportPipeline(new DefenderScanner()).CompleteAsync(completed, options, reporter, cancellation.Token, catalogProgress);
            var job = imported.Record;
            ShowDicoms(imported.Dicoms);
            stage.Text = imported.Error != null ? (job.IsoPublished ? "部分成功：ISO保存済み・DICOM出力未完了" : "処理中止・出力なし") : job.DicomCount == 0 ? "ISO保存完了・DICOMなし" : "ISO保存・DICOM出力完了";
            result.Text = (imported.Error == null ? "" : imported.Error + "\r\n") +
                $"ファイル内容照合：合格（{job.FileCount:N0}件）　ISOマウント解除：完了　Defender：{ScanLabel(job.ScanState)}\r\n" +
                $"ISO保存：{job.PublishedIsoPath ?? "なし"}\r\nDICOM出力：{job.PublishedDicomDirectory ?? "なし"}\r\n" +
                $"DICOM：{job.DicomCount:N0}ファイル　撮影日：{string.Join(", ", job.StudyDates)}　取込日で代用：{job.DateFallbackCount:N0}件\r\n" +
                $"隔離作業領域：{imported.WorkDirectory}";
        }
        catch (OperationCanceledException) { stage.Text = "中止しました"; result.Text = "正式出力なし。作業データは隔離領域に保持しています。"; }
        catch (ContentVerificationException ex)
        {
            stage.Text = "ISOファイル作成済み・内容照合不合格";
            result.Text = $"{ex.Message}\r\nISOが存在していても、内容照合には合格していません。\r\n正式出力なし。管理者が最新の作業フォルダのverification.jsonを確認してください。\r\n作業領域：{PrototypeImporter.WorkRoot}";
        }
        catch (Exception ex)
        {
            stage.Text = "処理失敗・記録と出力先を確認してください";
            result.Text = $"{ex.Message}\r\n管理者が最新のjob.jsonと設定の出力先を確認してください。\r\n作業領域：{PrototypeImporter.WorkRoot}";
        }
        finally
        {
            busy = false;
            patient.Enabled = drives.Enabled = keypad.Enabled = start.Enabled = refresh.Enabled = settings.Enabled = true;
            cancel.Enabled = false;
            cancellation?.Dispose();
            cancellation = null;
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = 0;
        }
    }

    private void ShowSettings()
    {
        if (busy) return;
        try
        {
            using var dialog = new SettingsForm(ImportOptions.Load());
            dialog.ShowDialog(this);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定を読み込めません", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static string ScanLabel(string state) => state switch
    {
        "Passed" => "合格", "ThreatDetected" => "脅威検出", "Cancelled" => "中止",
        "NotScanned" => "未検査", _ => "検査不能・不明"
    };

    private void ShowDicoms(IReadOnlyList<DicomEntry> entries)
    {
        dicomList.DataSource = entries.GroupBy(d => new { d.PatientName, d.StudyDate, d.DateSource, d.SeriesNumber, d.SeriesUid, d.Sequence })
            .Select(g => new { g.Key.PatientName, Date = g.Key.StudyDate.ToString("yyyy/MM/dd"), DateSource = g.Key.DateSource == "ImportDate" ? "取込日で代用" : g.Key.DateSource,
                Series = g.Key.SeriesNumber == int.MaxValue ? "不明" : g.Key.SeriesNumber.ToString(), g.Key.Sequence, Count = g.Count() }).ToArray();
    }
}
