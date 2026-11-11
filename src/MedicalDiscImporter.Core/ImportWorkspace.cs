using System.Text.Json;

namespace MedicalDiscImporter.Core;

// Own one GUID-named job, including a lease that recovery cannot acquire while it is active.
public sealed class ImportWorkspace : IDisposable
{
    private const string OutputsFile = "cleanup-outputs.json";
    private readonly FileStream lease;
    public string DirectoryPath { get; }
    public string JobId => Path.GetFileName(DirectoryPath);

    private ImportWorkspace(string directory, FileStream lease)
    {
        DirectoryPath = directory;
        this.lease = lease;
    }

    public static ImportWorkspace Create(string? root = null)
    {
        root = Path.GetFullPath(root ?? PrototypeImporter.WorkRoot);
        SafeFiles.CreatePrivateDirectory(root);
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        SafeFiles.CreatePrivateDirectory(directory);
        var workspace = new ImportWorkspace(directory, OpenLease(directory));
        try
        {
            File.WriteAllText(Path.Combine(directory, ".cleanup-v1"), "");
            return workspace;
        }
        catch { workspace.Dispose(); throw; }
    }

    private static FileStream OpenLease(string directory)
    {
        var path = Path.Combine(directory, ".active");
        if (File.Exists(path)) SafeFiles.RejectLink(new FileInfo(path));
        // Allow deletion of our lease during cleanup, but no other reader/writer.
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete);
    }

    public static void RegisterOutput(string workDirectory, string destination, string jobId)
    {
        var path = Path.Combine(workDirectory, OutputsFile);
        if (File.Exists(path)) SafeFiles.RejectLink(new FileInfo(path));
        var outputs = File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path))
            ?? throw new IOException("作業ファイル削除記録が不正です。") : [];
        var staging = Path.Combine(Path.GetFullPath(destination), ".MedicalDiscImporter", "staging", jobId);
        if (!outputs.Contains(staging, StringComparer.OrdinalIgnoreCase)) outputs.Add(staging);
        var temporary = path + ".tmp";
        if (File.Exists(temporary)) SafeFiles.RejectLink(new FileInfo(temporary));
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, outputs);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    // No cancellation: removing patient data must finish even after the user presses Stop.
    public async Task<bool> CleanupAsync(IImageMounter mounter)
    {
        var complete = true;
        var outputsPath = Path.Combine(DirectoryPath, OutputsFile);
        if (File.Exists(outputsPath))
        {
            try
            {
                SafeFiles.RejectLink(new FileInfo(outputsPath));
                var outputs = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(outputsPath))
                    ?? throw new IOException("削除記録が不正です。");
                foreach (var staging in outputs)
                {
                    try
                    {
                        var full = Path.GetFullPath(staging);
                        var parent = Path.GetDirectoryName(full)!;
                        if (Path.GetFileName(full) != JobId || Path.GetFileName(parent) != "staging" ||
                            Path.GetFileName(Path.GetDirectoryName(parent)) != ".MedicalDiscImporter")
                            throw new IOException("準備領域の削除対象が不正です。");
                        // Directory.Exists also returns false for disconnected shares. Require the
                        // destination to be reachable before accepting an absent staging directory.
                        var destination = Path.GetDirectoryName(Path.GetDirectoryName(parent)!)!;
                        _ = File.GetAttributes(destination);
                        SafeFiles.DeleteOwnedDirectory(full, parent);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { complete = false; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { complete = false; }
        }
        var iso = Path.Combine(DirectoryPath, "disc.iso");
        var mountPending = Path.Combine(DirectoryPath, ".mount-pending");
        var keepIso = false;
        if (File.Exists(iso) && (File.Exists(mountPending) || !File.Exists(Path.Combine(DirectoryPath, ".cleanup-v1"))))
        {
            try
            {
                SafeFiles.RejectLink(new FileInfo(iso));
                await mounter.DismountAsync(iso);
            }
            catch (Exception) { keepIso = true; complete = false; }
        }
        // Preserve only the retry instructions and an ISO whose dismount could not be confirmed.
        foreach (var entry in new DirectoryInfo(DirectoryPath).EnumerateFileSystemInfos())
        {
            if (entry.Name == ".active" || entry.Name == OutputsFile && !complete ||
                keepIso && (entry.Name is "disc.iso" or ".mount-pending")) continue;
            try { SafeFiles.DeleteOwnedEntry(entry.FullName, DirectoryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
        }
        if (complete)
        {
            try { SafeFiles.DeleteOwnedDirectory(DirectoryPath, Path.GetDirectoryName(DirectoryPath)!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
        }
        return complete;
    }

    public static async Task<int> RecoverAsync(IImageMounter mounter, string? root = null)
    {
        root = Path.GetFullPath(root ?? PrototypeImporter.WorkRoot);
        if (!Directory.Exists(root)) return 0;
        SafeFiles.ValidateDirectoryPath(root);
        var failures = 0;
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (!Guid.TryParseExact(directory.Name, "N", out _)) { failures++; continue; }
            ImportWorkspace workspace;
            try
            {
                SafeFiles.ValidateDirectoryPath(directory.FullName);
                workspace = new(directory.FullName, OpenLease(directory.FullName));
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { continue; } // An active import owns it.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures++; continue; }
            using (workspace)
            {
                try { if (!await workspace.CleanupAsync(mounter)) failures++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures++; }
            }
        }
        return failures;
    }

    public void Dispose() => lease.Dispose();
}
