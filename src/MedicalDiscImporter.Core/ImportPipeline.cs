using System.Text.Json;

namespace MedicalDiscImporter.Core;

public sealed record ImportResult(JobRecord Record, string WorkDirectory, IReadOnlyList<DicomEntry> Dicoms, string? Error = null);

public sealed class ImportPipeline(IMalwareScanner scanner)
{
    public Task<ImportResult> CompleteAsync(PrototypeResult source, ImportOptions options,
        IProgress<ImportProgress> progress, CancellationToken token,
        IProgress<IReadOnlyList<DicomEntry>>? catalogProgress = null) => Task.Run(async () =>
    {
        var job = source.Record;
        IReadOnlyList<DicomEntry> dicoms = [];
        try
        {
            options.CheckWritable();
            if (!job.ContentsVerified || !job.Unmounted) throw new IOException("ISO照合・解除が完了していません。");
            job.Finished = null;
            job.State = "Scanning";
            job.ScanState = "Scanning";
            PrototypeImporter.Persist(source.WorkDirectory, job);
            var tree = Path.Combine(source.WorkDirectory, "source");
            var manifest = JsonSerializer.Deserialize<TreeSnapshot>(File.ReadAllText(Path.Combine(source.WorkDirectory, "manifest.json")))
                ?? throw new IOException("元ファイル一覧がありません。");
            progress.Report(new("Defenderで全ファイルを検査中（中止時は正式出力しません）"));
            var scan = await scanner.ScanAsync(tree, options.SignatureMaxAgeDays, token);
            scan = scan with { TargetFileCount = scan.TargetFileCount ?? manifest.Files.Count };
            job.ScanState = scan.State;
            job.Scan = scan;
            File.WriteAllText(Path.Combine(source.WorkDirectory, "scan.json"), JsonSerializer.Serialize(scan));
            if (!scan.Passed) throw new IOException(scan.Reason);
            token.ThrowIfCancellationRequested();
            // A successful remediation can return 0. Missing/changed files must still block publication.
            SafeFiles.Verify(manifest, SafeFiles.ReadTree(tree, token));
            if (SafeFiles.HashFile(source.IsoPath, token) != job.IsoSha256) throw new IOException("検査中にISOが変化しました。");
            progress.Report(new("DICOM情報を読み取り中"));
            dicoms = new DicomCatalog().Read(tree, manifest, DateOnly.FromDateTime(job.Started.LocalDateTime), progress, token);
            job.DicomCount = dicoms.Count;
            job.DateFallbackCount = dicoms.Count(d => d.DateSource == "ImportDate");
            job.StudyDates = dicoms.Select(d => d.StudyDate.ToString("yyyy-MM-dd")).Distinct().ToArray();
            job.RsBaseExamName = options.RsBaseExamName;
            catalogProgress?.Report(dicoms);
            token.ThrowIfCancellationRequested();
            job.State = "Publishing";
            PrototypeImporter.Persist(source.WorkDirectory, job);
            progress.Report(new("検査合格：ISOを保存中"));
            var date = DateOnly.FromDateTime(job.Started.LocalDateTime);
            // Same-directory temporary file, verified bytes, no overwrite; retry only name collisions.
            var isoStage = Path.Combine(options.IsoDirectory, $".{job.JobId}.iso.partial");
            CopyVerified(source.IsoPath, isoStage, new("", new FileInfo(source.IsoPath).Length, job.IsoSha256!), token);
            for (var ordinal = 1; ; ordinal++)
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(options.IsoDirectory, InputRules.IsoFileName(job.PatientId, date, ordinal));
                try { File.Move(isoStage, target, false); job.PublishedIsoPath = target; break; }
                catch (IOException) when (File.Exists(target)) { }
            }
            job.IsoPublished = true;
            PrototypeImporter.Persist(source.WorkDirectory, job);
            if (dicoms.Count > 0)
            {
                progress.Report(new("検査合格：RSBase自動取込用のDICOMを準備中"));
                RsBasePublisher.Publish(tree, options.DicomDirectory, source.WorkDirectory, job.JobId, job.PatientId,
                    options.RsBaseExamName, dicoms, progress, _ =>
                {
                    job.DicomFilesPublished++;
                    job.PublishedDicomDirectory = Path.GetFullPath(options.DicomDirectory);
                    PrototypeImporter.Persist(source.WorkDirectory, job);
                }, token);
                job.DicomPublished = true;
            }
            job.State = dicoms.Count > 0 ? "Completed" : "IsoOnly_NoDicom";
            return new ImportResult(job, source.WorkDirectory, dicoms);
        }
        catch (Exception ex)
        {
            job.State = job.IsoPublished || job.DicomFilesPublished > 0 ? "PartialOutput" : ex is OperationCanceledException ? "Cancelled" : "Failed";
            if (job.ScanState == "Scanning") job.ScanState = ex is OperationCanceledException ? "Cancelled" : "Unknown";
            job.ErrorCode = $"{ex.GetType().Name}/0x{ex.HResult:X8}";
            return new ImportResult(job, source.WorkDirectory, dicoms,
                ex is OperationCanceledException ? "中止しました。" : ex.Message);
        }
        finally
        {
            job.Finished = DateTimeOffset.Now;
            PrototypeImporter.Persist(source.WorkDirectory, job);
        }
    });

    internal static void CopyVerified(string source, string target, FileRecord expected, CancellationToken token)
    {
        SafeFiles.RejectLink(new FileInfo(source));
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[1024 * 1024];
            long length = 0;
            int n;
            while ((n = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                length = checked(length + n);
                if (length > expected.Length) throw new IOException("コピー元のサイズが変化しました。");
                output.Write(buffer, 0, n);
            }
            output.Flush(true);
        }
        if (new FileInfo(target).Length != expected.Length || SafeFiles.HashFile(target, token) != expected.Sha256)
            throw new IOException("出力のサイズ・SHA-256照合に失敗しました。準備ファイルを保持します。");
    }
}
