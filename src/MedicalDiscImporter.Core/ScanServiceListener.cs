using System.IO.Pipes;

namespace MedicalDiscImporter.Core;

public sealed class ScanServiceListener(ScanServiceWorker worker, string work)
{
    public async Task RunAsync(NamedPipeServerStream pipe, CancellationToken token,
        Func<NamedPipeServerStream>? recreate = null, Action<Exception>? reportFailure = null)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // A client can connect and close before this await starts (ERROR_NO_DATA).
                    // This belongs to the request failure boundary, not the service's fatal boundary.
                    await pipe.WaitForConnectionAsync(token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromMinutes(40));
                    await worker.HandleAsync(pipe, work, deadline.Token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or
                    System.Text.Json.JsonException or ArgumentException or OverflowException)
                { reportFailure?.Invoke(ex); }
                finally
                {
                    try { pipe.Disconnect(); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            reportFailure?.Invoke(ex);
                            if (recreate == null) throw;
                            pipe.Dispose();
                            pipe = recreate();
                        }
                    }
                }
            }
        }
        finally { pipe.Dispose(); }
    }
}
