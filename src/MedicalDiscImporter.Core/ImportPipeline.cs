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
            job.ScanState = scan.State;
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
            ValidateOutputNames(dicoms);
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
                progress.Report(new("検査合格：DICOMを出力中"));
                var staging = Path.Combine(options.DicomDirectory, $".{job.JobId}.partial");
                if (Directory.Exists(staging)) throw new IOException("DICOMの準備フォルダが既に存在します。");
                Directory.CreateDirectory(staging);
                var i = 0;
                foreach (var entry in dicoms)
                {
                    token.ThrowIfCancellationRequested();
                    var target = Path.Combine(staging, DicomCatalog.OutputRelativePath(entry.RelativePath));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    CopyVerified(Path.Combine(tree, entry.RelativePath), target, entry.File, token);
                    if (++i % 50 == 0) progress.Report(new($"DICOM出力準備中：{i:N0}/{dicoms.Count:N0}", i * 100 / dicoms.Count));
                }
                token.ThrowIfCancellationRequested();
                var final = Path.Combine(options.DicomDirectory, $"{job.PatientId}-{date:yyyyMMdd}-{job.JobId}");
                Directory.Move(staging, final);
                job.PublishedDicomDirectory = final;
                job.DicomPublished = true;
            }
            job.State = dicoms.Count > 0 ? "Completed" : "IsoOnly_NoDicom";
            return new ImportResult(job, source.WorkDirectory, dicoms);
        }
        catch (Exception ex)
        {
            job.State = ex is OperationCanceledException ? "Cancelled" : job.IsoPublished ? "PartialOutput" : "Failed";
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

    private static void ValidateOutputNames(IReadOnlyList<DicomEntry> dicoms)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in dicoms)
        {
            var name = DicomCatalog.OutputRelativePath(entry.RelativePath);
            SafeFiles.SafeRelative(Path.GetFullPath("."), Path.GetFullPath(name));
            if (!names.Add(name)) throw new IOException(".dcm付加後の名前が重複します。上書きを避けるため出力を中止します。");
        }
        foreach (var name in names)
            for (var parent = Path.GetDirectoryName(name); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (names.Contains(parent)) throw new IOException("DICOMの出力ファイル名とフォルダ名が競合します。");
    }

    private static void CopyVerified(string source, string target, FileRecord expected, CancellationToken token)
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
