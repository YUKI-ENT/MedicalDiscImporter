using MedicalDiscImporter.Core;

namespace MedicalDiscImporter.App;

internal enum ServiceRegistrationKind { Unknown, Missing, Installed, DeletePending }

internal sealed record ServiceRegistrationSnapshot(ServiceRegistrationKind Registration, ServiceState? Status = null, int? ErrorCode = null)
{
    private bool Stable => Registration == ServiceRegistrationKind.Installed && Status?.State is 1 or 4 or 7;
    public bool CanRegister => Registration == ServiceRegistrationKind.Missing || Stable;
    public bool CanStart => Registration == ServiceRegistrationKind.Installed && Status?.State == 1;
    public bool CanStop => Registration == ServiceRegistrationKind.Installed && Status?.State is 4 or 7;
    public bool CanRemove => Stable;
    public bool CanCheck => Registration == ServiceRegistrationKind.Installed && Status?.State == 4;
    public string RegistrationText => Registration switch
    {
        ServiceRegistrationKind.Missing => "未登録",
        ServiceRegistrationKind.Installed => "登録済み",
        ServiceRegistrationKind.DeletePending => "登録解除の完了待ち",
        _ => "確認できません"
    };
    public string RunningText => Registration switch
    {
        ServiceRegistrationKind.Missing => "サービスなし",
        ServiceRegistrationKind.DeletePending => "確認できません",
        ServiceRegistrationKind.Installed => Status?.State switch
        {
            1 => "停止中", 2 => "開始中", 3 => "停止処理中", 4 => "稼働中",
            5 => "再開中", 6 => "一時停止へ移行中", 7 => "一時停止中", _ => "不明"
        },
        _ => "確認できません"
    };
    public string Hint => Registration switch
    {
        ServiceRegistrationKind.Missing => "最初にサービスを登録してください。以後は標準ユーザーで利用できます。",
        ServiceRegistrationKind.DeletePending => "Windowsの「サービス」管理画面を閉じて、登録解除の完了をお待ちください。",
        ServiceRegistrationKind.Installed => Status?.State switch
        {
            1 => "サービスを開始してください。" + (Status.ExitCode == 0 ? "" : $" 前回停止時のWindowsエラー：{Status.ExitCode}"),
            4 => "サービスは稼働中です。必要に応じてアプリとの接続を確認できます。",
            7 => "サービスは一時停止中です。必要に応じて停止または更新してください。",
            2 or 3 or 5 or 6 => "状態の切り替えを待っています。自動で再確認します。",
            _ => "現在の状態では操作できません。状態を更新して確認してください。"
        },
        _ => "状態を取得できないためサービス操作は無効です。「状態を更新」で再確認してください。" +
            (ErrorCode.HasValue ? $" Windowsエラー：{ErrorCode}" : "")
    };
    public string Describe() => $"登録状況：{RegistrationText}　稼働状況：{RunningText}\r\n{Hint}";
}
