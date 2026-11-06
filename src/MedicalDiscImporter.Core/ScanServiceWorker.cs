namespace MedicalDiscImporter.Core;

// The host supplies an administrator-protected work root; scanner injection is internal to the process.
public sealed class ScanServiceWorker(IMalwareScanner scanner)
{
    public async Task HandleAsync(Stream pipe, string work, CancellationToken token)
    {
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        var request = await ScanServiceProtocol.ReadAsync<ScanServiceRequest>(pipe, headerTimeout.Token);
        var staging = Path.Combine(work, Guid.NewGuid().ToString("N"));
        ScanServiceProtocol.Validate(request, staging);
        if (request.Operation == "Ping")
        {
            await ScanServiceProtocol.WriteResponseAsync(pipe,
                new ScanResult("Ready", "検査サービスに接続しました。", ServiceVersion: ApplicationRelease.DisplayVersion), token);
            return;
        }
        // Limit slow uploads separately from the Defender scan itself.
        using var upload = CancellationTokenSource.CreateLinkedTokenSource(token);
        upload.CancelAfter(TimeSpan.FromMinutes(10));
        ScanResult result;
        SafeFiles.RejectLink(new DirectoryInfo(work));
        Directory.CreateDirectory(staging);
        try
        {
            var tree = request.Manifest!;
            foreach (var dir in tree.Directories.OrderBy(p => p.Length)) Directory.CreateDirectory(Path.Combine(staging, dir));
            foreach (var file in tree.Files)
            {
                using var output = new FileStream(Path.Combine(staging, file.RelativePath), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
                await ScanServiceProtocol.CopyExactlyAsync(pipe, output, file.Length, upload.Token);
            }
            SafeFiles.Verify(tree, SafeFiles.ReadTree(staging, token));
            // No more input is expected. Disconnect or extra input cancels this request's scan.
            using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var monitorCancellation = new CancellationTokenSource();
            var monitor = MonitorDisconnectAsync(pipe, scanCancellation, monitorCancellation.Token);
            try
            {
                result = await scanner.ScanAsync(staging, request.SignatureDays, scanCancellation.Token);
                if (result.Passed) SafeFiles.Verify(tree, SafeFiles.ReadTree(staging, scanCancellation.Token));
            }
            finally
            {
                monitorCancellation.Cancel();
                await monitor;
            }
        }
        finally { DeleteStaging(staging); }
        // Cleanup failure also blocks success. No uploaded files are retained after a completed request.
        await ScanServiceProtocol.WriteResponseAsync(pipe, result with { ServiceVersion = ApplicationRelease.DisplayVersion }, token);
    }

    private static async Task MonitorDisconnectAsync(Stream pipe, CancellationTokenSource scan, CancellationToken token)
    {
        try { var count = await pipe.ReadAsync(new byte[1], token); scan.Cancel(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { scan.Cancel(); }
    }

    public static void DeleteStaging(string directory)
    {
        SafeFiles.RejectLink(new DirectoryInfo(directory));
        // No content reads during cleanup: Defender may deny reads of a detected file.
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                SafeFiles.RejectLink(entry);
                if (entry is DirectoryInfo child) pending.Push(child.FullName);
            }
        Directory.Delete(directory, true);
    }
}
