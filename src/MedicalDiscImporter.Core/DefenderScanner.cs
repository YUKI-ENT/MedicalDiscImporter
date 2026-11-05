using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MedicalDiscImporter.Core;

public sealed record ScanResult(string State, string Reason, string? SignatureVersion = null, string? ScanId = null,
    long? StartEventId = null, long? EndEventId = null, int? ExitCode = null)
{
    public bool Passed => State == "Passed";
}

public interface IMalwareScanner
{
    Task<ScanResult> ScanAsync(string directory, int signatureMaxAgeDays, CancellationToken token);
}

public sealed class DefenderScanner : IMalwareScanner
{
    public async Task<ScanResult> ScanAsync(string directory, int signatureMaxAgeDays, CancellationToken token)
    {
        using var resource = typeof(DefenderScanner).Assembly.GetManifestResourceStream("MedicalDiscImporter.Core.DefenderScan.ps1")!;
        using var reader = new StreamReader(resource);
        var script = await reader.ReadToEndAsync(token);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        start.Environment["MDI_SCAN_PATH"] = Path.GetFullPath(directory);
        start.Environment["MDI_SIGNATURE_DAYS"] = signatureMaxAgeDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var process = Process.Start(start) ?? throw new IOException("Defender検査を起動できません。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            // This invocation's process tree only. A still-running OS scan cannot authorize output.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            return new("Unknown", "Defender検査が30分以内に完了しませんでした。出力は禁止しました。");
        }
        await stderr; // Do not retain command output containing patient paths.
        var output = await stdout;
        if (process.ExitCode != 0) return new("Unknown", "Defenderの状態・履歴・イベントを取得できません。権限とサービスを確認してください。");
        try
        {
            var result = JsonSerializer.Deserialize<ScanResult>(output);
            if (result == null) return new("Unknown", "Defender応答がありません。");
            if (result.Passed && (string.IsNullOrWhiteSpace(result.ScanId) || string.IsNullOrWhiteSpace(result.SignatureVersion) ||
                result.StartEventId is not > 0 || result.EndEventId <= result.StartEventId || result.EndEventId == null || result.ExitCode != 0))
                return new("Unknown", "Defender検査の完了証跡が不十分です。");
            return result;
        }
        catch (JsonException) { return new("Unknown", "Defender応答を確認できません。"); }
    }
}
