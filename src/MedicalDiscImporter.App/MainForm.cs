using MedicalDiscImporter.Core;

namespace MedicalDiscImporter.App;

public sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(29, 53, 71);
    private static readonly Color Muted = Color.FromArgb(111, 131, 148);
    private static readonly Color Accent = Color.FromArgb(8, 125, 137);
    private static readonly Color Tile = Color.FromArgb(237, 243, 247);
    private readonly TextBox patient = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Yu Gothic UI", 30),
        BackColor = Color.FromArgb(241, 246, 250), ForeColor = Ink, MaxLength = 32, AccessibleName = "患者ID", PlaceholderText = "患者ID"
    };
    private readonly ComboBox drives = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "光学ドライブ", FlatStyle = FlatStyle.Flat };
    private readonly Label discStatus = new() { Dock = DockStyle.Fill, ForeColor = Muted, Text = "ドライブを確認しています", Font = new Font("Yu Gothic UI", 11), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label stage = new() { Dock = DockStyle.Fill, Text = "開始を待っています", ForeColor = Muted, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Yu Gothic UI", 12) };
    private readonly Label overview = new() { Dock = DockStyle.Fill, Text = "取り込み後に結果を表示します", ForeColor = Muted, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, Font = new Font("Yu Gothic UI", 11) };
    private readonly TextBox result = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, ScrollBars = ScrollBars.Vertical, AccessibleName = "処理結果", Font = new Font("Yu Gothic UI", 12) };
    private readonly Button start = TileButton("取込開始");
    private readonly Button cancel = TileButton("中止");
    private readonly Button settings = TileButton("設定");
    private readonly Button refresh = TileButton("更新");
    private readonly ImportSwitch isoSwitch = new("ISOイメージ");
    private readonly ImportSwitch scanSwitch = new("ウイルススキャン");
    private readonly ImportSwitch dicomSwitch = new("DICOM送信");
    private readonly TableLayoutPanel operationSwitches = new() { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
    private readonly ImportJourney journey = new();
    private readonly TabControl outputTabs = new() { Dock = DockStyle.Fill, Padding = new Point(16, 6), Font = new Font("Yu Gothic UI", 12) };
    private readonly TabPage detailsTab = new("保存先・検査の詳細") { BackColor = Color.White, Padding = new Padding(12) };
    private readonly DataGridView dicomList = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AutoGenerateColumns = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White, AccessibleName = "DICOMシーケンス一覧", SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, BorderStyle = BorderStyle.None,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, EnableHeadersVisualStyles = false,
        GridColor = Color.FromArgb(230, 237, 243)
    };
    private readonly TableLayoutPanel keypad = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, Margin = new Padding(12, 0, 0, 0) };
    private readonly System.Windows.Forms.Timer driveTimer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer deviceTimer = new() { Interval = 700 };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? cancellation;
    private bool busy;
    private bool refreshingDrives;
    private bool preview;
    private bool restoringSelection;
    private ImportOptions savedSelection = new();

    public MainForm()
    {
        Text = $"MedicalDiscImporter {ApplicationRelease.DisplayVersion}";
        Icon = AppBrand.Icon;
        Font = new Font("Yu Gothic UI", 13);
        ForeColor = Ink;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1200, 900);
        MinimumSize = new Size(1000, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(237, 242, 247);
        DoubleBuffered = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 330));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 174));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Margin = Padding.Empty };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label { Text = "MedicalDiscImporter", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 20, FontStyle.Bold) }, 0, 0);
        heading.Controls.Add(new Label { Text = "患者IDとディスクを確認して、取り込みを開始してください", Dock = DockStyle.Fill, ForeColor = Muted, Font = new Font(Font.FontFamily, 11) }, 0, 1);
        header.Controls.Add(heading, 0, 0);
        settings.Dock = DockStyle.Top;
        settings.Height = 36;
        settings.Font = new Font(Font.FontFamily, 11);
        settings.BackColor = BackColor;
        settings.ForeColor = Muted;
        settings.Margin = new Padding(0, 4, 0, 0);
        settings.Click += (_, _) => ShowSettings();
        header.Controls.Add(settings, 1, 0);
        layout.Controls.Add(header, 0, 0);

        var input = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 12) };
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 59));
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 41));
        layout.Controls.Add(input, 0, 1);
        var idCard = Card();
        idCard.Margin = new Padding(0, 0, 12, 0);
        idCard.ColumnCount = 2;
        idCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        idCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        var idFields = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Margin = Padding.Empty };
        idFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        idFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        idFields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        idFields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        idFields.Controls.Add(SectionTitle("患者ID"), 0, 0);
        var inputBox = new Panel { Dock = DockStyle.Fill, BackColor = patient.BackColor, Padding = new Padding(12, 7, 12, 7), Margin = new Padding(0, 0, 0, 4) };
        inputBox.Controls.Add(patient);
        idFields.Controls.Add(inputBox, 0, 1);
        idFields.Controls.Add(new Label { Text = "半角数字で入力", Dock = DockStyle.Fill, ForeColor = Muted, Font = new Font(Font.FontFamily, 11), TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        idCard.Controls.Add(idFields, 0, 0);
        idCard.Controls.Add(keypad, 1, 0);
        input.Controls.Add(idCard, 0, 0);
        for (var c = 0; c < 3; c++) keypad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        for (var r = 0; r < 4; r++) keypad.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        foreach (var key in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "クリア", "0", "削除" })
        {
            var button = TileButton(key);
            button.Font = new Font(Font.FontFamily, key.Length == 1 ? 23 : 12, FontStyle.Bold);
            button.TabStop = false;
            button.Margin = new Padding(4);
            button.Click += (_, _) => EnterKey(key);
            keypad.Controls.Add(button);
        }

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        input.Controls.Add(right, 1, 0);
        var driveCard = Card();
        driveCard.Margin = new Padding(0, 0, 0, 6);
        driveCard.RowCount = 3;
        driveCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        driveCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        driveCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        driveCard.Controls.Add(SectionTitle("光学ドライブ"), 0, 0);
        var selector = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selector.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        drives.Margin = new Padding(0, 4, 8, 0);
        selector.Controls.Add(drives, 0, 0);
        refresh.Font = new Font(Font.FontFamily, 11);
        refresh.Margin = new Padding(0, 0, 0, 4);
        selector.Controls.Add(refresh, 1, 0);
        driveCard.Controls.Add(selector, 0, 1);
        driveCard.Controls.Add(discStatus, 0, 2);
        right.Controls.Add(driveCard, 0, 0);

        var actions = Card();
        actions.Margin = new Padding(0, 6, 0, 0);
        actions.RowCount = 3;
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.Controls.Add(SectionTitle("取込操作"), 0, 0);
        for (var column = 0; column < 3; column++) operationSwitches.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        operationSwitches.Controls.Add(isoSwitch, 0, 0);
        operationSwitches.Controls.Add(scanSwitch, 1, 0);
        operationSwitches.Controls.Add(dicomSwitch, 2, 0);
        actions.Controls.Add(operationSwitches, 0, 1);
        var actionButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        actionButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        actionButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        start.BackColor = Accent;
        start.ForeColor = Color.White;
        start.FlatAppearance.MouseOverBackColor = Color.FromArgb(6, 106, 118);
        start.FlatAppearance.MouseDownBackColor = Color.FromArgb(5, 88, 99);
        start.Font = new Font(Font.FontFamily, 17, FontStyle.Bold);
        start.Margin = new Padding(0, 0, 10, 0);
        cancel.BackColor = Color.FromArgb(247, 237, 235);
        cancel.ForeColor = Color.FromArgb(164, 65, 53);
        cancel.Margin = Padding.Empty;
        actionButtons.Controls.Add(start, 0, 0);
        actionButtons.Controls.Add(cancel, 1, 0);
        actions.Controls.Add(actionButtons, 0, 2);
        right.Controls.Add(actions, 0, 1);

        var flowCard = Card();
        flowCard.Padding = new Padding(12);
        journey.Margin = Padding.Empty;
        flowCard.Margin = new Padding(0, 0, 0, 12);
        flowCard.RowCount = 2;
        flowCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        flowCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var flowHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        flowHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        flowHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        flowHeading.Controls.Add(SectionTitle("取込の進み具合"), 0, 0);
        flowHeading.Controls.Add(stage, 1, 0);
        flowCard.Controls.Add(flowHeading, 0, 0);
        flowCard.Controls.Add(journey, 0, 1);
        layout.Controls.Add(flowCard, 0, 2);

        var output = Card();
        output.RowCount = 2;
        output.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        output.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var outputHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        outputHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        outputHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputHeading.Controls.Add(SectionTitle("取込結果"), 0, 0);
        outputHeading.Controls.Add(overview, 1, 0);
        output.Controls.Add(outputHeading, 0, 0);
        var listTab = new TabPage("DICOM一覧") { BackColor = Color.White, Padding = new Padding(4, 8, 4, 4) };
        listTab.Controls.Add(dicomList);
        detailsTab.Controls.Add(result);
        outputTabs.TabPages.AddRange([listTab, detailsTab]);
        output.Controls.Add(outputTabs, 0, 1);
        dicomList.DefaultCellStyle.ForeColor = Ink;
        dicomList.DefaultCellStyle.SelectionBackColor = Color.FromArgb(218, 239, 241);
        dicomList.DefaultCellStyle.SelectionForeColor = Ink;
        dicomList.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        dicomList.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 250, 252);
        dicomList.ColumnHeadersDefaultCellStyle.BackColor = Tile;
        dicomList.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        dicomList.ColumnHeadersDefaultCellStyle.SelectionBackColor = Tile;
        dicomList.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
        dicomList.ColumnHeadersDefaultCellStyle.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
        dicomList.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 6, 6, 6);
        dicomList.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        foreach (var (title, property, weight) in new[] { ("患者名", "PatientName", 23f), ("撮影日", "Date", 14f),
                     ("日付の取得元", "DateSource", 14f), ("シリーズ", "Series", 10f), ("シーケンス", "Sequence", 31f), ("ファイル数", "Count", 8f) })
            dicomList.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, DataPropertyName = property, FillWeight = weight,
                MinimumWidth = property == "DateSource" ? 140 : property == "Count" ? 105 : 95 });
        layout.Controls.Add(output, 0, 3);

        cancel.Enabled = false;
        start.Enabled = false;
        isoSwitch.CheckedChanged += (_, _) => SelectionChanged();
        scanSwitch.CheckedChanged += (_, _) => SelectionChanged();
        dicomSwitch.CheckedChanged += (_, _) => SelectionChanged();
        try { ApplySelection(ImportOptions.Load()); }
        catch (Exception) { stage.Text = "設定を読み込めません。設定ファイルを確認してください"; }
        patient.TextChanged += (_, _) => UpdateStartAvailability();
        patient.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true; };
        refresh.Click += async (_, _) => await RefreshDrivesAsync();
        drives.SelectedIndexChanged += (_, _) => UpdateDiscStatus();
        start.Click += async (_, _) => await StartAsync();
        cancel.Click += (_, _) => RequestCancellation();
        driveTimer.Tick += async (_, _) => await RefreshDrivesAsync();
        deviceTimer.Tick += async (_, _) => { deviceTimer.Stop(); await RefreshDrivesAsync(); };
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            RequestCancellation();
        };
        Shown += async (_, _) =>
        {
            if (preview) return;
            driveTimer.Start();
            await RefreshDrivesAsync();
        };
    }

    private static TableLayoutPanel Card() => new()
    {
        Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16), Margin = Padding.Empty,
        ColumnCount = 1, RowCount = 1
    };
    private static Label SectionTitle(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Ink, Font = new Font("Yu Gothic UI", 15, FontStyle.Bold)
    };
    private static Button TileButton(string text)
    {
        var button = new FlatTileButton
        {
            Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Tile,
            ForeColor = Ink, Cursor = Cursors.Hand, Margin = Padding.Empty,
            UseVisualStyleBackColor = false, AccessibleName = text
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(219, 233, 242);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(197, 220, 234);
        return button;
    }

    private void EnterKey(string key)
    {
        if (busy) return;
        if (key == "クリア") patient.Clear();
        else if (key == "削除")
        {
            if (patient.SelectionLength > 0) patient.SelectedText = "";
            else if (patient.SelectionStart > 0)
            {
                var caret = patient.SelectionStart;
                patient.Text = patient.Text.Remove(caret - 1, 1);
                patient.SelectionStart = caret - 1;
            }
        }
        else if (patient.TextLength - patient.SelectionLength < patient.MaxLength) patient.SelectedText = key;
        patient.Focus();
    }

    private void RequestCancellation()
    {
        cancellation?.Cancel();
        cancel.Enabled = false;
        stage.Text = "中止要求済み · 後始末が終わるまでお待ちください";
    }

    private void ApplySelection(ImportOptions options)
    {
        restoringSelection = true;
        try
        {
            savedSelection = options;
            isoSwitch.Checked = options.CreateIso;
            scanSwitch.Checked = options.ScanForMalware;
            dicomSwitch.Checked = options.SendDicom;
        }
        finally { restoringSelection = false; }
        ShowSelection();
    }

    private ImportOptions SelectedOptions(ImportOptions options) => options with
    {
        CreateIso = isoSwitch.Checked, ScanForMalware = scanSwitch.Checked, SendDicom = dicomSwitch.Checked
    };

    private void SelectionChanged()
    {
        if (restoringSelection || busy) return;
        try
        {
            if (!preview) ImportOptions.SaveSelection(isoSwitch.Checked, scanSwitch.Checked, dicomSwitch.Checked);
            savedSelection = SelectedOptions(savedSelection);
        }
        catch (Exception ex)
        {
            ApplySelection(savedSelection);
            MessageBox.Show(this, ex.Message, "選択状態を保存できません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        ShowSelection();
    }

    private void ShowSelection()
    {
        var selected = SelectedOptions(savedSelection);
        journey.Configure(selected);
        journey.Reset();
        stage.Text = !selected.HasAnyOperation ? "実行する処理をONにしてください" : selected.ScanForMalware
            ? "開始を待っています" : "ウイルススキャンOFF · 未検査で出力します";
        stage.ForeColor = selected.ScanForMalware ? Muted : Color.FromArgb(167, 101, 26);
        UpdateStartAvailability();
    }

    private void UpdateStartAvailability() => start.Enabled = !busy && !refreshingDrives &&
        (isoSwitch.Checked || scanSwitch.Checked || dicomSwitch.Checked) &&
        drives.SelectedItem is OpticalDriveSnapshot { Ready: true } && patient.TextLength > 0 && patient.Text.All(char.IsAsciiDigit);

    private async Task RefreshDrivesAsync()
    {
        if (busy || refreshingDrives || IsDisposed || preview) return;
        refreshingDrives = true;
        refresh.Enabled = false;
        UpdateStartAvailability();
        try
        {
            var available = await Task.Run(OpticalDrives.Read, lifetime.Token);
            if (IsDisposed || busy || preview) return;
            var selected = OpticalDrives.SelectRoot(available, (drives.SelectedItem as OpticalDriveSnapshot)?.Root);
            if (!drives.Items.Cast<OpticalDriveSnapshot>().SequenceEqual(available))
            {
                drives.BeginUpdate();
                try
                {
                    drives.Items.Clear();
                    drives.Items.AddRange(available.Cast<object>().ToArray());
                    drives.SelectedItem = available.FirstOrDefault(d => d.Root == selected);
                }
                finally { drives.EndUpdate(); }
            }
            UpdateDiscStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed) discStatus.Text = "ドライブを確認できません。「更新」で再確認してください";
        }
        finally
        {
            refreshingDrives = false;
            if (!IsDisposed)
            {
                refresh.Enabled = !busy;
                UpdateStartAvailability();
            }
        }
    }

    private void UpdateDiscStatus()
    {
        discStatus.Text = drives.SelectedItem is OpticalDriveSnapshot drive ? drive.Status : "USBドライブを接続してください";
        discStatus.ForeColor = drives.SelectedItem is OpticalDriveSnapshot { Ready: true } ? Accent : Muted;
        UpdateStartAvailability();
    }

    protected override void WndProc(ref Message m)
    {
        // WM_DEVICECHANGE: device-tree changes, arrival, removal. Debounce notification bursts.
        if (m.Msg == 0x0219 && m.WParam.ToInt64() is 0x0007 or 0x8000 or 0x8004 && !busy && !preview && !IsDisposed)
        {
            deviceTimer.Stop();
            deviceTimer.Start();
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        driveTimer.Stop();
        deviceTimer.Stop();
        lifetime.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            driveTimer.Dispose();
            deviceTimer.Dispose();
            lifetime.Cancel();
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task StartAsync()
    {
        if (busy || refreshingDrives) return;
        ImportOptions options;
        string root;
        try
        {
            InputRules.ValidatePatientId(patient.Text);
            options = SelectedOptions(ImportOptions.Load());
            if (!options.HasAnyOperation) throw new ArgumentException("実行する処理をONにしてください。");
            options.CheckWritable();
            if (drives.SelectedItem is not OpticalDriveSnapshot { Ready: true } selectedDrive) throw new IOException("ディスクを挿入してください。");
            root = selectedDrive.Root;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "取り込みを開始できません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var scanNotice = options.ScanForMalware ? "ウイルス検査合格後に、選択した出力を実行します。" : "ウイルススキャンOFF：未検査で選択した出力を実行します。";
        if (MessageBox.Show(this, $"患者ID：{patient.Text}\r\nドライブ：{root}\r\n{scanNotice}\r\nISOイメージ：{(options.CreateIso ? options.IsoDirectory : "OFF")}\r\nDICOM送信：{(options.SendDicom ? options.DicomDirectory : "OFF")}\r\n検査名：{options.RsBaseExamName}", "開始確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            busy = true;
            driveTimer.Stop();
            deviceTimer.Stop();
            patient.Enabled = drives.Enabled = keypad.Enabled = start.Enabled = refresh.Enabled = settings.Enabled = operationSwitches.Enabled = false;
            cancel.Enabled = true;
            result.Clear();
            dicomList.DataSource = null;
            overview.Text = "取り込み中";
            outputTabs.SelectedIndex = 0;
            journey.Configure(options);
            journey.Reset();
            stage.ForeColor = Accent;
            cancellation = new CancellationTokenSource();
            var operation = cancellation;
            var reporter = new Progress<ImportProgress>(p =>
            {
                if (!busy || !ReferenceEquals(cancellation, operation) || operation.IsCancellationRequested) return;
                if (p.Phase is { } phase) journey.MoveTo(phase);
                stage.Text = p.Stage + (p.Percent.HasValue ? $" · {Math.Clamp(p.Percent.Value, 0, 100)}%" : "");
            });
            if (options.ScanForMalware)
            {
                stage.Text = "ウイルスチェックサービスの接続を確認中";
                await ServiceMalwareScanner.EnsureAvailableAsync(cancellation.Token);
            }
            var importer = new PrototypeImporter(new ImapiImageBuilder(), new WindowsImageMounter());
            var completed = await importer.RunAsync(root, patient.Text, reporter, cancellation.Token, options);
            var catalogProgress = new Progress<IReadOnlyList<DicomEntry>>(entries =>
            {
                if (busy && ReferenceEquals(cancellation, operation)) ShowDicoms(entries);
            });
            var imported = await new ImportPipeline(new ServiceMalwareScanner()).CompleteAsync(completed, options, reporter, cancellation.Token, catalogProgress);
            var job = imported.Record;
            ShowDicoms(imported.Dicoms);
            if (imported.Error == null) journey.Finish();
            else
            {
                journey.Stop(job.State == "Cancelled" || operation.IsCancellationRequested);
                stage.ForeColor = Color.FromArgb(183, 68, 55);
            }
            overview.Text = imported.Error != null ? "取り込み未完了 · 出力状況は詳細を確認してください"
                : options.SendDicom ? $"DICOM {job.DicomCount:N0}ファイル · 撮影日 {job.StudyDates.Length:N0}日"
                : options.CreateIso ? "ISO保存完了 · DICOM送信OFF" : "ウイルス検査完了 · 出力なし";
            if (imported.Error != null || !options.SendDicom || job.DicomCount == 0) outputTabs.SelectedTab = detailsTab;
            stage.Text = imported.Error != null ? (job.State == "PartialOutput" ? "一部出力済み · 詳細を確認してください" : "処理中止・出力なし")
                : !options.CreateIso && !options.SendDicom ? "ウイルス検査完了 · 出力なし"
                : "選択した処理が完了しました" + (options.SendDicom && job.DicomCount == 0 ? " · DICOMなし" : "")
                    + (!options.ScanForMalware ? " · 未検査" : "");
            if (!options.ScanForMalware && imported.Error == null) stage.ForeColor = Color.FromArgb(167, 101, 26);
            result.Text = (imported.Error == null ? "" : imported.Error + "\r\n") +
                $"ファイル内容照合：合格（{job.FileCount:N0}件）　ISO照合・解除：{(options.CreateIso ? "完了" : "対象外（OFF）")}　ウイルス検査：{ScanLabel(job.ScanState)}\r\n" +
                (job.Scan == null ? "" : job.Scan.Describe() + "\r\n" + (job.Scan.SignatureVersion == null ? "" : $"Defender定義：{job.Scan.SignatureVersion}\r\n")) +
                $"ISO保存：{(options.CreateIso ? job.PublishedIsoPath ?? "なし" : "OFF")}\r\n" +
                (options.SendDicom ? $"RSBase取込先：{job.PublishedDicomDirectory ?? "未出力"}\r\n" +
                    $"RSBase受け渡し：{job.DicomFilesPublished:N0}/{job.DicomCount:N0}件　検査名：{job.RsBaseExamName ?? options.RsBaseExamName}\r\n" +
                    $"DICOM：{job.DicomCount:N0}ファイル　撮影日：{string.Join(", ", job.StudyDates)}　取込日で代用：{job.DateFallbackCount:N0}件\r\n"
                    : "DICOM解析・送信：OFF\r\n") +
                $"隔離作業領域：{imported.WorkDirectory}";
        }
        catch (OperationCanceledException)
        {
            journey.Stop(true);
            stage.ForeColor = Color.FromArgb(183, 68, 55);
            stage.Text = "中止しました";
            overview.Text = "中止 · 出力なし";
            result.Text = "正式出力なし。作業データは隔離領域に保持しています。";
            outputTabs.SelectedTab = detailsTab;
        }
        catch (ContentVerificationException ex)
        {
            stage.Text = options.CreateIso ? "ISOファイル作成済み・内容照合不合格" : "ファイル読込・コピー照合不合格";
            journey.Stop(false);
            stage.ForeColor = Color.FromArgb(183, 68, 55);
            overview.Text = "照合不合格 · 出力なし";
            outputTabs.SelectedTab = detailsTab;
            result.Text = $"{ex.Message}\r\nファイル内容の照合には合格していません。\r\n正式出力なし。管理者が最新の作業フォルダのverification.jsonを確認してください。\r\n作業領域：{PrototypeImporter.WorkRoot}";
        }
        catch (Exception ex)
        {
            stage.Text = "処理失敗・記録と出力先を確認してください";
            journey.Stop(false);
            stage.ForeColor = Color.FromArgb(183, 68, 55);
            overview.Text = "処理失敗 · 詳細を確認してください";
            outputTabs.SelectedTab = detailsTab;
            result.Text = $"{ex.Message}\r\n管理者が最新のjob.jsonと設定の出力先を確認してください。\r\n作業領域：{PrototypeImporter.WorkRoot}";
        }
        finally
        {
            busy = false;
            patient.Enabled = drives.Enabled = keypad.Enabled = refresh.Enabled = settings.Enabled = operationSwitches.Enabled = true;
            cancel.Enabled = false;
            cancellation?.Dispose();
            cancellation = null;
            UpdateStartAvailability();
            if (!preview && !IsDisposed)
            {
                driveTimer.Start();
                await RefreshDrivesAsync();
            }
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
        "NotScanned" => "未検査", "SkippedByUser" => "未検査（OFF）", _ => "検査不能・不明"
    };

    // Preview only: no drive queries, scans, imports, or output writes are started.
    internal void ConfigurePreview(string state)
    {
        preview = true;
        ApplySelection(new ImportOptions());
        driveTimer.Stop();
        deviceTimer.Stop();
        discStatus.Text = "USBドライブを接続してください";
        if (state == "compact") { ClientSize = new Size(1000, 780); return; }
        if (state == "waiting") return;
        if (state is not ("ready" or "running" or "completed" or "failed" or "cancelled"))
            throw new ArgumentException("Unknown preview state", nameof(state));
        patient.Text = "00123456";
        drives.Items.Add(new OpticalDriveSnapshot(@"E:\", true, "ディスク認識済み  ·  UDF / 684 MiB"));
        drives.SelectedIndex = 0;
        if (state == "ready") return;
        journey.MoveTo(state == "cancelled" ? ImportPhase.CreatingIso : ImportPhase.Scanning);
        if (state == "running")
        {
            busy = true;
            patient.Enabled = drives.Enabled = keypad.Enabled = start.Enabled = refresh.Enabled = settings.Enabled = operationSwitches.Enabled = false;
            cancel.Enabled = true;
            stage.ForeColor = Accent;
            stage.Text = "ウイルス検査を実行しています";
            overview.Text = "取り込み中";
            return;
        }
        if (state == "completed")
        {
            journey.Finish();
            stage.ForeColor = Accent;
            stage.Text = "取り込みが完了しました";
            overview.Text = "DICOM 360ファイル · 撮影日 1日";
            dicomList.DataSource = new[]
            {
                new { PatientName = "サンプル^患者", Date = "2026/11/06", DateSource = "StudyDate", Series = "1", Sequence = "T1 AXIAL", Count = 120 },
                new { PatientName = "サンプル^患者", Date = "2026/11/06", DateSource = "StudyDate", Series = "2", Sequence = "T2 AXIAL", Count = 120 },
                new { PatientName = "サンプル^患者", Date = "2026/11/06", DateSource = "StudyDate", Series = "3", Sequence = "FLAIR", Count = 120 }
            };
            return;
        }
        journey.Stop(state == "cancelled");
        stage.ForeColor = Color.FromArgb(183, 68, 55);
        stage.Text = state == "cancelled" ? "中止しました" : "ウイルス検査を完了できませんでした";
        overview.Text = "出力なし · 詳細を確認してください";
        result.Text = "これは画面確認用のサンプルです。\r\nISO・DICOMは出力していません。";
        outputTabs.SelectedTab = detailsTab;
    }

    private void ShowDicoms(IReadOnlyList<DicomEntry> entries)
    {
        dicomList.DataSource = entries.GroupBy(d => new { d.PatientName, d.StudyDate, d.DateSource, d.SeriesNumber, d.SeriesUid, d.Sequence })
            .Select(g => new { g.Key.PatientName, Date = g.Key.StudyDate.ToString("yyyy/MM/dd"), DateSource = g.Key.DateSource == "ImportDate" ? "取込日で代用" : g.Key.DateSource,
                Series = g.Key.SeriesNumber == int.MaxValue ? "不明" : g.Key.SeriesNumber.ToString(), g.Key.Sequence, Count = g.Count() }).ToArray();
    }
}
