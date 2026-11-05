using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using MedicalDiscImporter.Core;

namespace MedicalDiscImporter.App;

internal static class ScanWindowsService
{
    internal const string Name = "MedicalDiscImporter.Defender";
    private static readonly CancellationTokenSource stopping = new();
    private static readonly NativeService.ServiceMain main = ServiceEntry;
    private static readonly NativeService.Handler handler = Control;
    private static IntPtr statusHandle;
    private static readonly object statusLock = new();
    private static int currentState = 2;
    private static bool diagnosticsEnabled;

    internal static void Run()
    {
        var table = new[] { new NativeService.Table { Name = Name, Main = main }, new NativeService.Table() };
        if (!NativeService.StartServiceCtrlDispatcher(table)) throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(main);
        GC.KeepAlive(handler);
    }

    private static void Status(int state, int error = 0)
    {
        lock (statusLock)
        {
            currentState = state;
            var status = new NativeService.Status { Type = 0x10, State = state,
                Accepted = state == 4 ? 5 : 0, ExitCode = error, WaitHint = state is 2 or 3 ? 60000 : 0 };
            NativeService.SetServiceStatus(statusHandle, ref status);
        }
    }

    private static void Control(int control)
    {
        if (control is 1 or 5)
        {
            Status(3);
            // SCM's dispatcher must return promptly; cancellation callbacks may perform I/O cleanup.
            _ = stopping.CancelAsync();
        }
        else if (control == 4) { lock (statusLock) Status(currentState); }
    }

    private static void ServiceEntry(int argc, IntPtr argv)
    {
        statusHandle = NativeService.RegisterServiceCtrlHandler(Name, handler);
        if (statusHandle == IntPtr.Zero) return;
        Status(2);
        try
        {
            if (!WindowsIdentity.GetCurrent().IsSystem) throw new IOException("SYSTEMサービスが必要です。");
            ServiceRegistration.ProtectDirectory(ServiceRegistration.InstallDirectory);
            diagnosticsEnabled = true;
            var work = Path.Combine(ServiceRegistration.InstallDirectory, "work");
            ServiceRegistration.ProtectDirectory(work);
            // Recover only this service's private GUID-named staging directories.
            foreach (var dir in Directory.EnumerateDirectories(work))
            {
                if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out _)) throw new IOException("不明な検査領域です。");
                ScanServiceWorker.DeleteStaging(dir);
            }
            using var ready = CreatePipe();
            Status(4);
            new ScanServiceListener(new ScanServiceWorker(new DefenderScanner()), work)
                .RunAsync(ready, stopping.Token, CreatePipe, ex => Report("Request", ex)).GetAwaiter().GetResult();
            Status(1);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { Status(1); }
        catch (Exception ex) { Report("Fatal", ex); Status(1, 1064); }
    }

    private static void Report(string phase, Exception error)
    {
        if (!diagnosticsEnabled) return;
        try
        {
            var path = Path.Combine(ServiceRegistration.InstallDirectory, "service.log");
            if (File.Exists(path) && new FileInfo(path).Length > 65536) File.WriteAllText(path, "");
            // No exception messages, file names, or caller input: they could contain patient data.
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {phase} {error.GetType().Name} 0x{error.HResult:X8}\r\n");
        }
        catch { /* Diagnostics must not affect service availability. */ }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(system);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Do not grant CreateNewInstance to clients. Remote/network logons are explicitly denied.
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(ScanServiceProtocol.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 65536, 65536, acl);
    }

}

internal static class NativeService
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ServiceMain(int argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Handler(int control);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Table { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public ServiceMain? Main; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status { public int Type, State, Accepted, ExitCode, SpecificExitCode, Checkpoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessStatus { public Status Status; public int ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool StartServiceCtrlDispatcher([In] Table[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr RegisterServiceCtrlHandler(string name, Handler handler);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetServiceStatus(IntPtr handle, ref Status status);
}
