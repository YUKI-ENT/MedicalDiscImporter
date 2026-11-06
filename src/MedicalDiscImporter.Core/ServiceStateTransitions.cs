using System.ComponentModel;
using System.Diagnostics;

namespace MedicalDiscImporter.Core;

public sealed record ServiceState(int State, int ExitCode = 0, int ProcessId = 0);

public interface IServiceControlOperations
{
    ServiceState Query();
    int RequestStart();
    int RequestStop();
}

// SCM commands race with automatic startup and other administrators. Re-query pending states;
// never assume that a successful command means the requested transition has completed.
public sealed class ServiceStateTransitions(IServiceControlOperations operations, TimeSpan? pollInterval = null,
    TimeSpan? timeout = null)
{
    public Task StartAsync(CancellationToken token = default) => TransitionAsync(true, token);
    public Task StopAsync(CancellationToken token = default) => TransitionAsync(false, token);

    private async Task TransitionAsync(bool start, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var startObserved = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed > (timeout ?? TimeSpan.FromSeconds(60)))
                throw new IOException("サービスの開始・停止が時間内に完了しませんでした。");
            var status = operations.Query();
            if (start && status.State == 4 || !start && status.State == 1) return;
            if (start)
            {
                if (status.State == 2) startObserved = true;
                else if (status.State == 1)
                {
                    if (startObserved)
                        throw new Win32Exception(status.ExitCode == 0 ? 1067 : status.ExitCode,
                            "検査サービスが起動中に停止しました。サービスの診断ログを確認してください。");
                    var error = operations.RequestStart();
                    if (error is not (0 or 1056 or 1061)) throw new Win32Exception(error);
                    startObserved = error == 0;
                }
                else if (status.State is not (3 or 4))
                    throw new IOException("開始できるサービス状態ではありません。");
            }
            else if (status.State is 4 or 7)
            {
                var error = operations.RequestStop();
                if (error is not (0 or 1061 or 1062)) throw new Win32Exception(error);
            }
            else if (status.State is not (2 or 3 or 5 or 6))
                throw new IOException("停止できるサービス状態ではありません。");
            await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(200), token);
        }
    }
}
