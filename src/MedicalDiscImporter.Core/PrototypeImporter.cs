using System.Runtime.InteropServices;
using System.Text.Json;

namespace MedicalDiscImporter.Core;

public sealed class PrototypeImporter(IImageBuilder builder, IImageMounter mounter)
{
    public static string WorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MedicalDiscImporter", "Quarantine");

    public Task<PrototypeResult> RunAsync(string driveRoot, string patientId, IProgress<ImportProgress> progress,
        CancellationToken token)
    {
        InputRules.ValidatePatientId(patientId);
        var drive = new DriveInfo(driveRoot);
        if (drive.DriveType != DriveType.CDRom || !drive.IsReady)
            throw new IOException("読み取り可能な光学ディスクを挿入してください。");
        return Task.Run(() => Run(drive, patientId, progress, token), token);
    }

    private PrototypeResult Run(DriveInfo source, string patientId, IProgress<ImportProgress> progress, CancellationToken token)
    {
        var job = new JobRecord { PatientId = patientId, DiscType = $"Optical/{source.DriveFormat}; tracks/sessions unknown" };
        var local = new DriveInfo(Path.GetPathRoot(WorkRoot)!);
        if (local.DriveType != DriveType.Fixed || local.DriveFormat != "NTFS")
            throw new IOException("作業領域はローカルNTFSに配置してください。");
        var appRoot = Path.GetDirectoryName(WorkRoot)!;
        SafeFiles.CreatePrivateDirectory(appRoot);
        SafeFiles.CreatePrivateDirectory(WorkRoot);
        var work = Path.Combine(WorkRoot, job.JobId);
        SafeFiles.CreatePrivateDirectory(work);
        var tree = Path.Combine(work, "source");
        Directory.CreateDirectory(tree);
        var iso = Path.Combine(work, InputRules.IsoFileName(patientId, DateOnly.FromDateTime(DateTime.Today)));
        var partial = iso + ".partial";
        var mountAttempted = false;
        var sleepSet = false;
        try
        {
            Persist(work, job);
            if (SetThreadExecutionState(0x80000001) == 0) throw new IOException("スリープ抑止に失敗しました。");
            sleepSet = true;
            token.ThrowIfCancellationRequested();
            var required = checked(Math.Min(source.TotalSize, SafeFiles.MaxBytes) * 3 + 2L * 1024 * 1024 * 1024);
            if (local.AvailableFreeSpace < required) throw new IOException("作業領域の空き容量が不足しています。");
            progress.Report(new ImportProgress("ディスクからローカル隔離領域へ読取中"));
            var manifest = SafeFiles.ReadTree(source.RootDirectory.FullName, token, tree, progress);
            if (manifest.Files.Count == 0) throw new IOException("データファイルがありません。音楽CD等は未対応です。");
            job.FileCount = manifest.Files.Count;
            job.SourceBytes = manifest.Bytes;
            File.WriteAllText(Path.Combine(work, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
            builder.Build(tree, partial, progress, token);
            token.ThrowIfCancellationRequested();
            job.IsoSha256 = SafeFiles.HashFile(partial, token);
            File.Move(partial, iso, false);
            progress.Report(new ImportProgress("ISOを読み取り専用マウントして全ファイルを照合中"));
            mountAttempted = true;
            var mountedRoot = mounter.MountAsync(iso, token).GetAwaiter().GetResult();
            var actual = SafeFiles.ReadTree(mountedRoot, token, progress: progress);
            var verification = SafeFiles.Compare(manifest, actual);
            File.WriteAllText(Path.Combine(work, "verification.json"), JsonSerializer.Serialize(verification, JsonOptions));
            if (!verification.Matches) throw new ContentVerificationException(verification);
            job.ContentsVerified = true;
            job.State = "PrototypeVerified_NotScanned_NotPublished";
        }
        catch (OperationCanceledException)
        {
            job.State = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            job.State = "Failed";
            job.ErrorCode = $"{ex.GetType().Name}/0x{ex.HResult:X8}";
            throw;
        }
        finally
        {
            try
            {
                if (mountAttempted)
                {
                    // Cleanup ignores user cancellation. Failure is visible and never counted as success.
                    mounter.DismountAsync(iso).GetAwaiter().GetResult();
                    job.Unmounted = true;
                }
            }
            catch (Exception ex)
            {
                job.State = "CleanupFailed_AdministratorRequired";
                job.ErrorCode = $"{ex.GetType().Name}/0x{ex.HResult:X8}";
                throw new IOException("ISOの解除を確認できません。管理者がマウント状態を確認してください。作業領域：" + work, ex);
            }
            finally
            {
                if (sleepSet) SetThreadExecutionState(0x80000000);
                job.Finished = DateTimeOffset.Now;
                Persist(work, job);
            }
        }
        return new PrototypeResult(job, work, iso);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static void Persist(string directory, JobRecord job)
    {
        var path = Path.Combine(directory, "job.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(job, JsonOptions));
        File.Move(path + ".tmp", path, true);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
