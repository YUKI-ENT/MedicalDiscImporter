using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MedicalDiscImporter.Core;

public sealed record RsBaseOutput(string SourceRelativePath, string FileName, string Sha256, string State);

public static class RsBasePublisher
{
    public static void ValidateExamName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40 || name != name.Trim() || name.EndsWith('.') ||
            name.Contains('~') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("RSBase検査名は1～40文字で、~・改行・ファイル名に使えない文字を含めず指定してください。");
    }

    public static string FileName(string id, long ordinal, DateOnly date, string exam) => FormatName(id, ordinal, date, exam, "dcm");
    public static string IsoFileName(string id, long ordinal, DateOnly date, string exam) => FormatName(id, ordinal, date, exam, "iso");

    private static string FormatName(string id, long ordinal, DateOnly date, string exam, string extension)
    {
        InputRules.ValidatePatientId(id);
        ValidateExamName(exam);
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        return string.Create(CultureInfo.InvariantCulture, $"{id}~{ordinal:000000}~{date:yyyy_MM_dd}~{exam}~RSB.{extension}");
    }

    private sealed record PublicationEntry(string RelativePath, DateOnly StudyDate, FileRecord File);

    public static void PublishIso(string isoPath, string destination, string workDirectory, string jobId, string patientId,
        DateOnly importDate, string examName, string sha256, IProgress<ImportProgress> progress,
        Action<string> filePublished, CancellationToken token) =>
        PublishFiles(Path.GetDirectoryName(isoPath)!, destination, workDirectory, jobId, patientId, examName,
            [new(Path.GetFileName(isoPath), importDate, new("", new FileInfo(isoPath).Length, sha256))],
            "iso", "rsbase-iso-output.json", progress, filePublished, token);

    // Final files are flat. All pending bytes and sequence metadata stay in a non-RSB-named
    // subdirectory, on the same volume/share as the watch folder. Publish each verified file by rename.
    public static void Publish(string source, string destination, string workDirectory, string jobId, string patientId,
        string examName, IReadOnlyList<DicomEntry> dicoms, IProgress<ImportProgress> progress,
        Action<string> filePublished, CancellationToken token)
    {
        var ordered = dicoms.OrderBy(d => d.StudyDate).ThenBy(d => d.SeriesNumber).ThenBy(d => d.InstanceNumber)
            .ThenBy(d => d.SopInstanceUid, StringComparer.Ordinal).ThenBy(d => d.RelativePath, StringComparer.Ordinal)
            .Select(d => new PublicationEntry(d.RelativePath, d.StudyDate, d.File)).ToArray();
        PublishFiles(source, destination, workDirectory, jobId, patientId, examName, ordered, "dcm", "rsbase-output.json",
            progress, filePublished, token);
    }

    private static void PublishFiles(string source, string destination, string workDirectory, string jobId, string patientId,
        string examName, PublicationEntry[] ordered, string extension, string ledgerName, IProgress<ImportProgress> progress,
        Action<string> filePublished, CancellationToken token)
    {
        ValidateExamName(examName);
        InputRules.ValidatePatientId(patientId);
        if (!Guid.TryParseExact(jobId, "N", out _)) throw new IOException("処理IDが不正です。");
        var root = Path.GetFullPath(destination);
        var metadata = Path.Combine(root, ".MedicalDiscImporter");
        CreateDirectory(metadata);
        var stagingRoot = Path.Combine(metadata, "staging");
        CreateDirectory(stagingRoot);
        var staging = Path.Combine(stagingRoot, jobId);
        if (Directory.Exists(staging)) throw new IOException("RSBase出力の準備領域が既に存在します。");
        ImportWorkspace.RegisterOutput(workDirectory, root, jobId);
        CreateDirectory(staging);
        try
        {
            for (var i = 0; i < ordered.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = ordered[i];
                ImportPipeline.CopyVerified(Path.Combine(source, entry.RelativePath), PendingPath(staging, i), entry.File, token);
                if ((i + 1) % 50 == 0) progress.Report(new($"RSBase出力準備中：{i + 1:N0}/{ordered.Length:N0}", (i + 1) * 100 / ordered.Length));
            }
            token.ThrowIfCancellationRequested();
            using var lease = AcquireLock(Path.Combine(metadata, "sequence.lock"), token);
            var statePath = Path.Combine(metadata, "sequence.json");
            var counters = ReadCounters(statePath);
            // Include existing files from other producers and legacy imports, across exam names.
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                token.ThrowIfCancellationRequested();
                var parts = Path.GetFileName(path).Split('~');
                if (parts.Length != 5 || parts[0] != patientId ||
                    !(parts[4].Equals("RSB.dcm", StringComparison.OrdinalIgnoreCase) || parts[4].Equals("RSB.iso", StringComparison.OrdinalIgnoreCase)) ||
                    parts[1].Length < 6 || !parts[1].All(char.IsAsciiDigit) ||
                    !DateOnly.TryParseExact(parts[2], "yyyy_MM_dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
                    throw new IOException("既存のRSBase連番が扱える範囲を超えています。");
                var key = Key(patientId, date);
                counters[key] = Math.Max(counters.GetValueOrDefault(key), ordinal);
            }
            var ledger = new List<RsBaseOutput>();
            foreach (var entry in ordered)
                ledger.Add(new(entry.RelativePath, FormatName(patientId, Next(counters, Key(patientId, entry.StudyDate)),
                    entry.StudyDate, examName, extension), entry.File.Sha256, "Reserved"));
            // Reserve the entire range durably BEFORE any watched filename becomes visible. Even if
            // the watcher immediately consumes files, later jobs/machines will not reuse these numbers.
            WriteJson(statePath, counters);
            var ledgerPath = Path.Combine(workDirectory, ledgerName);
            WriteJson(ledgerPath, ledger);
            for (var i = 0; i < ordered.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                while (true)
                {
                    ledger[i] = ledger[i] with { State = "Publishing" };
                    WriteJson(ledgerPath, ledger);
                    var target = Path.Combine(root, ledger[i].FileName);
                    try { File.Move(PendingPath(staging, i), target, false); }
                    catch (IOException) when (File.Exists(target) || Directory.Exists(target))
                    {
                        // An external producer may not use our lock. Reserve another number, never overwrite.
                        token.ThrowIfCancellationRequested();
                        // Move the remaining range for this day forward as a unit, preserving stable image order.
                        for (var j = i; j < ordered.Length; j++)
                            if (ordered[j].StudyDate == ordered[i].StudyDate)
                                ledger[j] = ledger[j] with { FileName = FormatName(patientId,
                                    Next(counters, Key(patientId, ordered[j].StudyDate)), ordered[j].StudyDate, examName, extension), State = "Reserved" };
                        WriteJson(statePath, counters);
                        continue;
                    }
                    ledger[i] = ledger[i] with { State = "Published" };
                    // Callback updates the job immediately; the watcher may already have moved the file.
                    filePublished(target);
                    WriteJson(ledgerPath, ledger);
                    break;
                }
                if ((i + 1) % 50 == 0) progress.Report(new($"RSBaseへ受け渡し中：{i + 1:N0}/{ordered.Length:N0}", (i + 1) * 100 / ordered.Length));
            }
        }
        finally
        {
            SafeFiles.DeleteOwnedDirectory(staging, stagingRoot);
        }
    }

    private static string Key(string id, DateOnly date) => string.Create(CultureInfo.InvariantCulture, $"{id}~{date:yyyy_MM_dd}");
    private static string PendingPath(string root, int index) => Path.Combine(root, $"{index:00000000}.pending");
    private static long Next(Dictionary<string, long> counters, string key)
    {
        var previous = counters.GetValueOrDefault(key);
        if (previous == long.MaxValue) throw new IOException("RSBaseの連番が上限に達しました。");
        return counters[key] = previous + 1;
    }

    private static Dictionary<string, long> ReadCounters(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        SafeFiles.RejectLink(new FileInfo(path));
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("RSBase採番記録の容量上限を超えています。");
        try
        {
            var counters = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path))
                ?? throw new IOException("RSBase採番記録が空です。");
            if (counters.Values.Any(n => n < 0)) throw new IOException("RSBase採番記録に不正な値があります。");
            return counters;
        }
        catch (JsonException ex) { throw new IOException("RSBase採番記録が壊れています。管理者に確認してください。", ex); }
    }

    private static FileStream AcquireLock(string path, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path)) SafeFiles.RejectLink(new FileInfo(path));
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(60)) throw new IOException("別のRSBase出力処理が実行中です。完了後に再実行してください。", ex);
                token.WaitHandle.WaitOne(100);
            }
        }
    }

    private static void CreateDirectory(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
            if (dir.Exists) SafeFiles.RejectLink(dir);
        Directory.CreateDirectory(path);
    }

    private static void WriteJson<T>(string path, T value)
    {
        if (File.Exists(path)) SafeFiles.RejectLink(new FileInfo(path));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
