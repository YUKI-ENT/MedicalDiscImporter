using System.ComponentModel;
using MedicalDiscImporter.Core;

namespace MedicalDiscImporter.App;

internal sealed class ScanServicePanel : UserControl
{
    private readonly Label registration = StatusLabel("登録状況：確認中…");
    private readonly Label running = StatusLabel("稼働状況：確認中…");
    private readonly Label hint = BodyLabel("サービスの状態を確認しています。");
    private readonly Label connection = BodyLabel("通信：未確認");
    private readonly Label versions = BodyLabel($"アプリ：{ApplicationRelease.DisplayVersion}　稼働サービス：未確認");
    private readonly Label notice = BodyLabel("");
    private readonly Button register = ActionButton("サービスを登録");
    private readonly Button check = ActionButton("接続を確認");
    private readonly Button serviceStart = ActionButton("サービス開始");
    private readonly Button serviceStop = ActionButton("サービス停止");
    private readonly Button remove = ActionButton("登録を解除");
    private readonly Button refresh = ActionButton("状態を更新");
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 3000 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<ServiceRegistrationSnapshot> query;
    private readonly Func<ServiceMaintenanceAction, Task> change;
    private readonly Func<CancellationToken, Task<ServiceConnectionInfo>> ping;
    private readonly TableLayoutPanel? layout;
    private ServiceRegistrationSnapshot? snapshot;
    private bool querying;
    private bool busy;
    private ServiceConnectionInfo? serviceInfo;
    private DateTimeOffset lastVersionProbe = DateTimeOffset.MinValue;
    public bool MaintenanceInProgress { get; private set; }

    public ScanServicePanel() : this(ServiceRegistration.ReadState, ServiceRegistration.ChangeAsync, ServiceMalwareScanner.ProbeAsync) { }

    internal ScanServicePanel(Func<ServiceRegistrationSnapshot> query, Func<ServiceMaintenanceAction, Task> change,
        Func<CancellationToken, Task<ServiceConnectionInfo>> ping)
    {
        this.query = query;
        this.change = change;
        this.ping = ping;
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Inherit;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        BackColor = Color.White;
        Padding = new Padding(12);
        layout = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 1, RowCount = 7 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var row = 0; row < 7; row++) layout.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(layout);
        var status = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, WrapContents = true };
        registration.MinimumSize = new Size(300, 44);
        running.MinimumSize = new Size(260, 44);
        status.Controls.AddRange([registration, running, refresh]);
        layout.Controls.Add(status, 0, 0);
        layout.Controls.Add(versions, 0, 1);
        layout.Controls.Add(hint, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 8, 0, 4) };
        buttons.Controls.AddRange([register, check, serviceStart, serviceStop, remove]);
        layout.Controls.Add(buttons, 0, 3);
        layout.Controls.Add(connection, 0, 4);
        layout.Controls.Add(notice, 0, 5);
        notice.Visible = false;
        notice.TextChanged += (_, _) => notice.Visible = notice.Text.Length > 0;
        layout.Controls.Add(BodyLabel("登録・更新・開始・停止・解除には管理者承認が必要です。パスワードは保存しません。"), 0, 6);
        ApplyButtons();
        refresh.Click += async (_, _) => { lastVersionProbe = DateTimeOffset.MinValue; await RefreshStateAsync(); };
        register.Click += async (_, _) => await ServiceActionAsync(ServiceMaintenanceAction.Install);
        check.Click += async (_, _) => await ServiceActionAsync(null);
        serviceStart.Click += async (_, _) => await ServiceActionAsync(ServiceMaintenanceAction.Start);
        serviceStop.Click += async (_, _) =>
        {
            if (MessageBox.Show(FindForm(), "検査中に停止すると、その取り込みは出力されません。サービスを停止しますか？",
                "サービス停止", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                await ServiceActionAsync(ServiceMaintenanceAction.Stop);
        };
        remove.Click += async (_, _) =>
        {
            if (MessageBox.Show(FindForm(), "登録を解除すると、このPCで本アプリのウイルス検査ができなくなります。解除しますか？",
                "サービス登録の解除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                await ServiceActionAsync(ServiceMaintenanceAction.Remove);
        };
        poll.Tick += async (_, _) => await RefreshStateAsync();
        ResumeLayout(performLayout: true);
    }

    private static Label StatusLabel(string text) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Yu Gothic UI", 12, FontStyle.Bold), AccessibleName = text.Split('：')[0], Margin = new Padding(3, 8, 8, 8)
    };
    private static Label BodyLabel(string text) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(800, 0), Margin = new Padding(3, 4, 3, 4),
        ForeColor = Color.FromArgb(93, 113, 130)
    };
    private static Button ActionButton(string text)
    {
        var button = new FlatTileButton
        {
            Text = text, AutoSize = true, MinimumSize = new Size(116, 44), Padding = new Padding(8, 4, 8, 4),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(237, 243, 247), ForeColor = Color.FromArgb(29, 53, 71),
            Margin = new Padding(3), UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (layout == null) return base.GetPreferredSize(proposedSize);
        // Measure wrapping at the available width; an unconstrained percent column reports
        // an unnecessarily tall preferred size and pushes the settings Save button off screen.
        var width = proposedSize.Width > 0 ? proposedSize.Width : Width;
        var content = layout.GetPreferredSize(new Size(Math.Max(1, width - Padding.Horizontal), 0));
        return new Size(width, content.Height + Padding.Vertical);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (DesignMode) return;
        poll.Start();
        await RefreshStateAsync();
    }

    private void ApplyButtons()
    {
        var available = !busy && !querying;
        register.Text = snapshot?.Registration == ServiceRegistrationKind.Installed ? "サービスを更新" : "サービスを登録";
        register.Enabled = available && snapshot?.CanRegister == true;
        check.Enabled = available && snapshot?.CanCheck == true;
        serviceStart.Enabled = available && snapshot?.CanStart == true;
        serviceStop.Enabled = available && snapshot?.CanStop == true;
        remove.Enabled = available && snapshot?.CanRemove == true;
        refresh.Enabled = available;
    }

    private async Task RefreshStateAsync(bool afterAction = false)
    {
        if (IsDisposed || querying || busy && !afterAction) return;
        querying = true;
        ApplyButtons();
        try
        {
            var current = await Task.Run(query, lifetime.Token);
            if (IsDisposed) return;
            if (snapshot?.Registration != current.Registration || snapshot?.Status?.State != current.Status?.State ||
                snapshot?.Status?.ProcessId != current.Status?.ProcessId)
            {
                connection.Text = current.CanCheck ? "通信：未確認（「接続を確認」で確認できます）" : "通信：稼働後に確認できます";
                serviceInfo = null;
                lastVersionProbe = DateTimeOffset.MinValue;
            }
            snapshot = current;
            DisplaySnapshot();
            DisplayVersions();
            if (current.CanCheck && DateTimeOffset.UtcNow - lastVersionProbe >= TimeSpan.FromSeconds(30))
            {
                var response = await ping(lifetime.Token);
                if (IsDisposed) return;
                serviceInfo = response;
                lastVersionProbe = DateTimeOffset.UtcNow;
                connection.Text = "通信：" + response.Message;
                DisplayVersions();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            snapshot = new(ServiceRegistrationKind.Unknown, ErrorCode: ex is Win32Exception win32 ? win32.NativeErrorCode : null);
            serviceInfo = null;
            connection.Text = "通信：未確認";
            DisplaySnapshot();
            DisplayVersions();
        }
        finally
        {
            querying = false;
            if (!IsDisposed) ApplyButtons();
        }
    }

    private void DisplaySnapshot()
    {
        registration.Text = "登録状況：" + snapshot!.RegistrationText;
        running.Text = "稼働状況：" + snapshot.RunningText;
        hint.Text = snapshot.Hint;
        registration.ForeColor = snapshot.Registration == ServiceRegistrationKind.Installed ? Color.FromArgb(8, 125, 137) : Color.FromArgb(111, 131, 148);
        running.ForeColor = snapshot.CanCheck ? Color.FromArgb(8, 125, 137) : Color.FromArgb(111, 131, 148);
    }

    private void DisplayVersions()
    {
        var prefix = $"アプリ：{ApplicationRelease.DisplayVersion}　稼働サービス：";
        var updateRecommended = false;
        if (snapshot?.CanCheck != true)
            versions.Text = prefix + (snapshot?.Registration == ServiceRegistrationKind.Missing ? "未登録"
                : snapshot?.Registration == ServiceRegistrationKind.Installed ? "停止・移行中のため未取得" : "確認できません");
        else if (serviceInfo == null) versions.Text = prefix + "確認中…";
        else if (!serviceInfo.Connected) versions.Text = prefix + "取得できません（検査中・接続状態を確認してください）";
        else if (!ServiceRelease.TryGetCompatibleVersion(serviceInfo.ServiceVersion, out var release))
        {
            versions.Text = prefix + "未対応のバージョン情報\r\nサービスのバージョンを確認できません。「サービスを更新」を実行してください。";
            updateRecommended = true;
        }
        else
        {
            var match = ServiceRelease.Compare(serviceInfo.ServiceVersion);
            var display = ServiceRelease.TryParse(serviceInfo.ServiceVersion, out _) ? release.ToString()
                : $"{serviceInfo.ServiceVersion}（旧表記・サービス版{release}相当）";
            versions.Text = prefix + display + (match switch
            {
                ServiceVersionMatch.Older => $"\r\nサービス基準版{ServiceRelease.DisplayVersion}より古いバージョンです。「サービスを更新」を実行してください。",
                ServiceVersionMatch.Newer => "（同梱サービスより新しいバージョンです）",
                _ => "（更新不要）"
            });
            updateRecommended = match == ServiceVersionMatch.Older;
        }
        versions.ForeColor = updateRecommended ? Color.FromArgb(167, 101, 26) : Color.FromArgb(93, 113, 130);
        register.BackColor = updateRecommended ? Color.FromArgb(255, 239, 211) : Color.FromArgb(237, 243, 247);
    }

    private bool CanPerform(ServiceMaintenanceAction? action) => action switch
    {
        ServiceMaintenanceAction.Install => snapshot?.CanRegister == true,
        ServiceMaintenanceAction.Start => snapshot?.CanStart == true,
        ServiceMaintenanceAction.Stop => snapshot?.CanStop == true,
        ServiceMaintenanceAction.Remove => snapshot?.CanRemove == true,
        null => snapshot?.CanCheck == true,
        _ => false
    };

    private async Task ServiceActionAsync(ServiceMaintenanceAction? action)
    {
        if (IsDisposed || busy || querying || !CanPerform(action)) return;
        busy = true;
        MaintenanceInProgress = action.HasValue;
        poll.Stop();
        ApplyButtons();
        notice.Text = action.HasValue ? "管理者操作の完了を待っています…" : "アプリとの接続を確認しています…";
        try
        {
            if (action.HasValue)
            {
                await change(action.Value);
                if (IsDisposed) return;
                serviceInfo = null;
                lastVersionProbe = DateTimeOffset.MinValue;
                connection.Text = "通信：未確認";
                notice.Text = action == ServiceMaintenanceAction.Remove ? "登録解除を実行しました。削除待ちの場合は完了をお待ちください。" : "サービス操作が完了しました。";
            }
            else
            {
                var response = await ping(lifetime.Token);
                if (IsDisposed) return;
                serviceInfo = response;
                lastVersionProbe = DateTimeOffset.UtcNow;
                connection.Text = "通信：" + response.Message;
                DisplayVersions();
                notice.Text = "";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            if (!IsDisposed) notice.Text = "管理者操作がキャンセルされました。";
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            notice.Text = "サービスの操作・接続確認に失敗しました。";
            MessageBox.Show(FindForm(), ex.Message, "ウイルスチェックサービス", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed) await RefreshStateAsync(afterAction: true);
            busy = false;
            MaintenanceInProgress = false;
            if (!IsDisposed) { ApplyButtons(); poll.Start(); }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            poll.Dispose();
            lifetime.Cancel();
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
