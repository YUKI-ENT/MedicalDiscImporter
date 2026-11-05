using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using MedicalDiscImporter.Core;

namespace MedicalDiscImporter.App;

internal enum ServiceMaintenanceAction { Install, Remove, Start, Stop }

internal static class ServiceRegistration
{
    internal static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "MedicalDiscImporter.ScanService");
    private static string InstalledExe => Path.Combine(InstallDirectory, "MedicalDiscImporter.App.exe");

    internal static async Task ChangeAsync(ServiceMaintenanceAction action)
    {
        var exe = Environment.ProcessPath ?? throw new IOException("アプリの実行ファイルが不明です。");
#pragma warning disable IL3000 // Empty Location is deliberately used to require the published single-file distribution.
        if (action == ServiceMaintenanceAction.Install && !string.IsNullOrEmpty(typeof(Program).Assembly.Location))
            throw new IOException("サービス登録には発行済みの単一EXE版を使用してください。");
#pragma warning restore IL3000
        var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(action switch
        {
            ServiceMaintenanceAction.Install => "--install-scan-service", ServiceMaintenanceAction.Remove => "--remove-scan-service",
            ServiceMaintenanceAction.Start => "--start-scan-service", ServiceMaintenanceAction.Stop => "--stop-scan-service",
            _ => throw new ArgumentException("不明なサービス操作です。")
        });
        using var process = Process.Start(start) ?? throw new IOException("管理者操作を起動できません。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("サービス操作を完了できませんでした。管理者画面のWindowsエラー番号を確認してください。");
    }

    internal static void Execute(ServiceMaintenanceAction action)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new IOException("サービスの登録・解除には管理者権限が必要です。");
#pragma warning disable IL3000
        if (action == ServiceMaintenanceAction.Install && !string.IsNullOrEmpty(typeof(Program).Assembly.Location))
            throw new IOException("発行済み単一EXEが必要です。");
#pragma warning restore IL3000
        if (action == ServiceMaintenanceAction.Install && string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase))
            throw new IOException("配布用フォルダのEXEから登録・更新してください。");
        using var scm = ServiceHandle.OpenManager(action == ServiceMaintenanceAction.Install ? 3 : 1);
        using var existing = ServiceHandle.Open(scm, ScanWindowsService.Name);
        if (action is ServiceMaintenanceAction.Start or ServiceMaintenanceAction.Stop)
        {
            if (existing == null) throw new IOException("検査サービスが未登録です。先に登録してください。");
            if (action == ServiceMaintenanceAction.Start) existing.StartAndWait();
            else existing.StopAndWait();
            return;
        }
        if (existing != null) existing.StopAndWait();
        if (action == ServiceMaintenanceAction.Remove)
        {
            if (existing != null && !DeleteService(existing.DangerousGetHandle())) Fail();
            // Leave protected binaries/data in place. Removal must not recursively delete data or an executing EXE.
            return;
        }
        ProtectDirectory(InstallDirectory);
        var source = Environment.ProcessPath!;
        var staged = Path.Combine(InstallDirectory, "service.exe.new");
        if (File.Exists(staged)) SafeFiles.RejectLink(new FileInfo(staged));
        if (File.Exists(InstalledExe)) SafeFiles.RejectLink(new FileInfo(InstalledExe));
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        File.Move(staged, InstalledExe, true);
        foreach (var name in new[] { "THIRD-PARTY-NOTICES.md", "licenses" })
        {
            var from = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(from)) File.Copy(from, Path.Combine(InstallDirectory, name), true);
            else if (Directory.Exists(from))
            {
                var to = Path.Combine(InstallDirectory, name);
                ProtectDirectory(to);
                foreach (var file in Directory.EnumerateFiles(from))
                {
                    SafeFiles.RejectLink(new FileInfo(file));
                    File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
                }
            }
        }
        var command = $"\"{InstalledExe}\" --scan-service";
        if (existing != null)
        {
            if (!ChangeServiceConfig(existing.DangerousGetHandle(), 0x10, 2, 1, command,
                    null, IntPtr.Zero, null, "LocalSystem", null, "MedicalDiscImporter ウイルスチェック")) Fail();
            existing.StartAndWait();
        }
        else
        {
            using var created = new ServiceHandle(CreateService(scm.DangerousGetHandle(), ScanWindowsService.Name,
                "MedicalDiscImporter ウイルスチェック", 0xF01FF, 0x10, 2, 1, command, null, IntPtr.Zero, null, "LocalSystem", null));
            if (created.IsInvalid) Fail();
            created.StartAndWait();
        }
    }

    internal static string DescribeState()
    {
        try
        {
            using var scm = ServiceHandle.OpenManager(1);
            using var service = ServiceHandle.Open(scm, ScanWindowsService.Name, 4);
            if (service == null) return "サービス未登録：登録・更新を実行してください。";
            var status = service.Query();
            return status.State switch
            {
                1 => $"サービス停止中（Windowsエラー {status.ExitCode}）：開始または登録・更新を実行してください。",
                2 => "サービス開始中：しばらく待ってから接続を確認してください。",
                3 => "サービス停止中の処理を実行しています。",
                4 => "サービス実行中：検査中、またはアプリとサービスの更新状況を確認してください。",
                _ => $"サービスの状態：{status.State}（Windowsエラー {status.ExitCode}）"
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1072)
        { return "以前の登録の削除待ち：Windowsの「サービス」管理画面を閉じてから再登録してください。"; }
        catch (Win32Exception ex) { return $"サービス状態の取得に失敗（Windowsエラー {ex.NativeErrorCode}）。"; }
    }

    internal static void ProtectDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        for (var parent = info.Parent; parent != null; parent = parent.Parent)
            if (parent.Exists) SafeFiles.RejectLink(parent);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (info.Exists)
        {
            SafeFiles.RejectLink(info);
            var previous = info.GetAccessControl();
            var owner = previous.GetOwner(typeof(SecurityIdentifier));
            if (!system.Equals(owner) && !admins.Equals(owner)) throw new IOException("サービス配置先の所有者を確認できません。");
            var writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in previous.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                    !system.Equals(rule.IdentityReference) && !admins.Equals(rule.IdentityReference))
                    throw new IOException("サービス配置先が管理者以外から変更可能です。");
        }
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(admins);
        foreach (var sid in new[] { system, admins })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (info.Exists) info.SetAccessControl(acl);
        else FileSystemAclExtensions.Create(info, acl);
    }

    private static void Fail() => throw new Win32Exception(Marshal.GetLastWin32Error());

    private sealed class ServiceHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid, IServiceControlOperations
    {
        public ServiceHandle(IntPtr handle) : base(true) { SetHandle(handle); }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        public static ServiceHandle OpenManager(int access = 3)
        {
            var result = new ServiceHandle(OpenSCManager(null, null, access));
            if (result.IsInvalid) Fail();
            return result;
        }
        public static ServiceHandle? Open(ServiceHandle scm, string name, int access = 0xF01FF)
        {
            var result = new ServiceHandle(OpenService(scm.DangerousGetHandle(), name, access));
            if (!result.IsInvalid) return result;
            var error = Marshal.GetLastWin32Error();
            result.Dispose();
            if (error == 1060) return null;
            throw new Win32Exception(error);
        }
        public ServiceState Query()
        {
            if (!QueryServiceStatus(handle, out var status)) Fail();
            return new(status.State, status.ExitCode);
        }
        public int RequestStart() => StartService(handle, 0, IntPtr.Zero) ? 0 : Marshal.GetLastWin32Error();
        public int RequestStop() => ControlService(handle, 1, out _) ? 0 : Marshal.GetLastWin32Error();
        public void StopAndWait()
        {
            if (!QueryServiceStatusEx(handle, 0, out var status, Marshal.SizeOf<NativeService.ProcessStatus>(), out _)) Fail();
            Microsoft.Win32.SafeHandles.SafeProcessHandle? process = null;
            try
            {
                if (status.ProcessId > 0)
                {
                    process = OpenProcess(0x100000, false, status.ProcessId); // SYNCHRONIZE only.
                    if (process.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        process.Dispose(); process = null;
                        if (error != 87) throw new Win32Exception(error); // PID already exited is harmless.
                    }
                }
                new ServiceStateTransitions(this).StopAsync().GetAwaiter().GetResult();
                // SERVICE_STOPPED may precede process exit. Wait for the EXE to be released before update/delete.
                if (process != null)
                {
                    var wait = WaitForSingleObject(process, 60000);
                    if (wait == 0xFFFFFFFF) Fail();
                    if (wait != 0) throw new IOException("サービス停止後もプロセスが終了していません。更新・解除は中止しました。");
                }
            }
            finally { process?.Dispose(); }
        }
        public void StartAndWait() => new ServiceStateTransitions(this).StartAsync().GetAwaiter().GetResult();
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, int access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, int access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateService(IntPtr manager, string name, string displayName, int access, int type, int start, int error, string path, string? group, IntPtr tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(IntPtr service, int type, int start, int error, string path, string? group, IntPtr tag, string? dependencies, string? account, string? password, string displayName);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(IntPtr service);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(IntPtr service, int control, out NativeService.Status status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service, out NativeService.Status status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out NativeService.ProcessStatus status, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(IntPtr service, int count, IntPtr args);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint milliseconds);
}
