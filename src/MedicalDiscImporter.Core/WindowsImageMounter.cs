using System.Diagnostics;
using System.Text;

namespace MedicalDiscImporter.Core;

public sealed class WindowsImageMounter : IImageMounter
{
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    public async Task<string> MountAsync(string imagePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Mount is intentionally not killed on cancellation: its outcome must be known before cleanup.
        var output = await RunAsync($"$i = Mount-DiskImage -ImagePath {Quote(imagePath)} -StorageType ISO -Access ReadOnly -PassThru -ErrorAction Stop; " +
            "$v = @($i | Get-Volume -ErrorAction Stop); if ($v.Count -ne 1 -or !$v[0].DriveLetter) { throw 'VolumeMappingFailed' }; " +
            "[Console]::Write([string]$v[0].DriveLetter)");
        var letter = output.Trim();
        if (letter.Length != 1 || !char.IsAsciiLetter(letter[0])) throw new IOException("仮想ドライブの確認に失敗しました。");
        token.ThrowIfCancellationRequested();
        return letter + @":\";
    }

    public async Task DismountAsync(string imagePath)
    {
        await RunAsync($"$i = Get-DiskImage -ImagePath {Quote(imagePath)} -ErrorAction Stop; " +
            $"if ($i.Attached) {{ Dismount-DiskImage -ImagePath {Quote(imagePath)} -ErrorAction Stop }}; " +
            $"if ((Get-DiskImage -ImagePath {Quote(imagePath)} -ErrorAction Stop).Attached) {{ throw 'StillMounted' }}");
    }

    private static async Task<string> RunAsync(string script)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ErrorActionPreference='Stop'; try { " + script + " } catch { exit 1 }")));
        using var process = Process.Start(start) ?? throw new IOException("PowerShell起動失敗");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stderr; // Drain, but avoid persisting sensitive paths in logs.
        var text = await stdout;
        if (process.ExitCode != 0) throw new IOException("Windows ISOマウント／解除コマンドに失敗しました。");
        return text;
    }
}
