using MedicalDiscImporter.Core;
using FellowOakDicom;
using System.Text.Json;

var failures = 0;
void Check(string name, Action test)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex}"); }
}
void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
void Reject<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var source = Path.Combine(root, "source");
var copied = Path.Combine(root, "copy");
Directory.CreateDirectory(source);
Directory.CreateDirectory(copied);
Directory.CreateDirectory(Path.Combine(source, "日本語 folder"));
Directory.CreateDirectory(Path.Combine(source, "empty"));
File.WriteAllBytes(Path.Combine(source, "日本語 folder", "no-extension"), Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
File.WriteAllText(Path.Combine(source, "viewer.exe"), "dummy non-executable content");
File.WriteAllBytes(Path.Combine(source, "zero.dat"), []);
var quiet = new QuietProgress();

Check("patient ID rejects unsafe and full-width input", () =>
{
    foreach (var id in new[] { "", "../1", "1~2", "CON", "１２３", "1\n", new string('1', 33) }) Reject<ArgumentException>(() => InputRules.ValidatePatientId(id));
    InputRules.ValidatePatientId("000123");
});
Check("ISO naming preserves ID and collision suffix", () =>
{
    Assert(InputRules.IsoFileName("0001", new DateOnly(2026, 11, 4)) == "0001-20261104.iso");
    Assert(InputRules.IsoFileName("0001", new DateOnly(2026, 11, 4), 2) == "0001-20261104-02.iso");
});
Check("tree traversal blocked", () => Reject<IOException>(() => SafeFiles.SafeRelative(source, Path.Combine(root, "escape"))));
Check("abnormal names blocked", () => Reject<IOException>(() => SafeFiles.SafeRelative(source, Path.Combine(source, "trailing."))));
TreeSnapshot? original = null;
Check("copy all files including extensionless and inert executable bytes", () =>
{
    original = SafeFiles.ReadTree(source, default, copied);
    Assert(original.Files.Count == 3 && original.Directories.Count == 2);
    SafeFiles.Verify(original, SafeFiles.ReadTree(copied, default));
});
Check("existing files never overwritten", () => Reject<IOException>(() => SafeFiles.ReadTree(source, default, copied)));
Check("content changes detected", () =>
{
    File.WriteAllText(Path.Combine(copied, "viewer.exe"), "changed");
    Reject<IOException>(() => SafeFiles.Verify(original!, SafeFiles.ReadTree(copied, default)));
});
Check("missing file detected", () =>
{
    var missing = original! with { Files = original!.Files.Skip(1).ToArray() };
    Reject<IOException>(() => SafeFiles.Verify(original, missing));
});
Check("renamed file detected", () =>
{
    var renamed = original! with { Files = original!.Files.Select(x => x with { RelativePath = "renamed-" + x.RelativePath }).ToArray() };
    Reject<IOException>(() => SafeFiles.Verify(original, renamed));
});
Check("empty directory loss detected", () => Reject<IOException>(() => SafeFiles.Verify(original!, original! with { Directories = [] })));
Check("cancel before read", () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    Reject<OperationCanceledException>(() => SafeFiles.ReadTree(source, cts.Token));
});
Check("hash stable", () => Assert(SafeFiles.HashFile(Path.Combine(source, "zero.dat"), default) == "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"));
Check("verification reports distinct difference types without paths in summary", () =>
{
    var expected = new TreeSnapshot([
        new("missing", 1, "A"), new("size", 1, "A"), new("content", 1, "A")], ["gone-dir"]);
    var actual = new TreeSnapshot([
        new("extra", 1, "A"), new("size", 2, "B"), new("content", 1, "B")], ["new-dir"]);
    var report = SafeFiles.Compare(expected, actual);
    Assert(!report.Matches && report.Differences.Count == 6);
    Assert(report.Differences.Select(d => d.Kind).Distinct().Count() == 6);
    Assert(!report.Summary.Contains("missing") && report.Summary.Contains("内容相違"));
    Reject<ContentVerificationException>(() => SafeFiles.Verify(expected, actual));
});
Check("verification preserves case sensitivity and reports name change", () =>
{
    var expected = new TreeSnapshot([new("a.dcm", 1, "A")], []);
    var actual = new TreeSnapshot([new("A.dcm", 1, "A")], []);
    var report = SafeFiles.Compare(expected, actual);
    Assert(report.Differences.Count == 2);
    Assert(report.Differences.Any(d => d.Kind == "MissingFile"));
    Assert(report.Differences.Any(d => d.Kind == "UnexpectedFile"));
});
Check("source disappearance fails closed", () => Reject<IOException>(() => SafeFiles.ReadTree(Path.Combine(root, "missing"), default)));
Check("reparse point rejected", () =>
{
    var link = Path.Combine(root, "junction");
    var p = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
    p.ArgumentList.Add("/c"); p.ArgumentList.Add("mklink"); p.ArgumentList.Add("/J"); p.ArgumentList.Add(link); p.ArgumentList.Add(source);
    using var process = System.Diagnostics.Process.Start(p)!;
    process.StandardOutput.ReadToEnd(); process.WaitForExit();
    if (process.ExitCode != 0) throw new Exception("junction creation failed");
    Reject<IOException>(() => SafeFiles.ReadTree(link, default));
    Directory.Delete(link); // remove only the junction, never recurse
});

var dicomRoot = Path.Combine(root, "dicom-fixture");
Directory.CreateDirectory(dicomRoot);
void WriteDicom(string relative, string? date, string? seriesDate = null)
{
    var path = Path.Combine(dicomRoot, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var data = new DicomDataset
    {
        { DicomTag.SOPClassUID, DicomUID.CTImageStorage.UID },
        { DicomTag.SOPInstanceUID, DicomUID.Generate().UID },
        { DicomTag.StudyInstanceUID, DicomUID.Generate().UID },
        { DicomTag.SeriesInstanceUID, "1.2.3.4.5" },
        { DicomTag.SpecificCharacterSet, "ISO_IR 192" },
        { DicomTag.PatientName, "試験^患者" },
        { DicomTag.SeriesNumber, "2" },
        { DicomTag.InstanceNumber, "1" },
        { DicomTag.SeriesDescription, "AXIAL TEST" },
        { DicomTag.SequenceName, "SE" }
    };
    if (date != null) data.Add(DicomTag.StudyDate, date);
    if (seriesDate != null) data.Add(DicomTag.SeriesDate, seriesDate);
    new DicomFile(data).Save(path);
}
WriteDicom("nested/IM0001", "20261031");
WriteDicom("IM0002.dcm", null, "20261101");
WriteDicom("IM0003.IMA", null);
File.WriteAllText(Path.Combine(dicomRoot, "viewer.exe"), "dummy viewer bytes");
File.WriteAllText(Path.Combine(dicomRoot, "readme"), "not DICOM");
var dicomManifest = SafeFiles.ReadTree(dicomRoot, default);
Check("parse extensionless DICOM and UTF-8 patient/sequence; skip ordinary files", () =>
{
    var entries = new DicomCatalog().Read(dicomRoot, dicomManifest, new(2026, 11, 5), quiet, default);
    Assert(entries.Count == 3);
    Assert(entries[0].PatientName == "試験^患者" && entries[0].Sequence == "AXIAL TEST / SE");
    Assert(entries[0].StudyDate == new DateOnly(2026, 10, 31) && entries[0].DateSource == "StudyDate");
    Assert(entries[1].StudyDate == new DateOnly(2026, 11, 1) && entries[1].DateSource == "SeriesDate");
    Assert(entries[2].DateSource == "ImportDate" && entries[2].StudyDate == new DateOnly(2026, 11, 5));
});
Check("DICOMDIR is not an image output", () =>
{
    var dir = Path.Combine(root, "directory-fixture"); Directory.CreateDirectory(dir);
    var directory = new FellowOakDicom.Media.DicomDirectory();
    directory.AddFile(DicomFile.Open(Path.Combine(dicomRoot, "IM0002.dcm")), "IM0002");
    directory.Save(Path.Combine(dir, "DICOMDIR"));
    Assert(new DicomCatalog().Read(dir, SafeFiles.ReadTree(dir, default), new(2026, 11, 5), quiet, default).Count == 0);
});
Check("DICOM without Part 10 preamble is recognized by parser", () =>
{
    var dir = Path.Combine(root, "no-preamble"); Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "IMAGE"), File.ReadAllBytes(Path.Combine(dicomRoot, "IM0002.dcm"))[132..]);
    Assert(new DicomCatalog().Read(dir, SafeFiles.ReadTree(dir, default), new(2026, 11, 5), quiet, default).Count == 1);
});
Check("invalid StudyDate falls back to valid SeriesDate", () =>
{
    WriteDicom("fallback-test", "20261031", "20261101");
    var bytes = File.ReadAllBytes(Path.Combine(dicomRoot, "fallback-test"));
    var marker = System.Text.Encoding.ASCII.GetBytes("20261031");
    var index = bytes.AsSpan().IndexOf(marker);
    Assert(index >= 0);
    System.Text.Encoding.ASCII.GetBytes("20261399").CopyTo(bytes, index);
    var dir = Path.Combine(root, "invalid-date"); Directory.CreateDirectory(dir);
    File.WriteAllBytes(Path.Combine(dir, "IMAGE"), bytes);
    var entry = new DicomCatalog().Read(dir, SafeFiles.ReadTree(dir, default), new(2026, 11, 5), quiet, default).Single();
    Assert(entry.DateSource == "SeriesDate" && entry.StudyDate == new DateOnly(2026, 11, 1));
    File.Delete(Path.Combine(dicomRoot, "fallback-test"));
});
Check("malformed .dcm blocks output instead of silently skipping", () =>
{
    var dir = Path.Combine(root, "bad-dicom"); Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "broken.dcm"), "invalid");
    Reject<IOException>(() => new DicomCatalog().Read(dir, SafeFiles.ReadTree(dir, default), new(2026, 11, 5), quiet, default));
});
Check("cancellation interrupts catalog", () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    Reject<OperationCanceledException>(() => new DicomCatalog().Read(dicomRoot, dicomManifest, new(2026, 11, 5), quiet, cts.Token));
});

(PrototypeResult Source, ImportOptions Options) Fixture(string name)
{
    var work = Path.Combine(root, name, "work"); Directory.CreateDirectory(work);
    var tree = Path.Combine(work, "source"); Directory.CreateDirectory(tree);
    var snapshot = SafeFiles.ReadTree(dicomRoot, default, tree);
    File.WriteAllText(Path.Combine(work, "manifest.json"), JsonSerializer.Serialize(snapshot));
    var iso = Path.Combine(work, "test.iso"); File.WriteAllText(iso, "dummy ISO bytes; no mounting in this test");
    var job = new JobRecord { PatientId = "00123", ContentsVerified = true, Unmounted = true, IsoSha256 = SafeFiles.HashFile(iso, default) };
    return (new(job, work, iso), new() { IsoDirectory = Path.Combine(root, name, "iso"), DicomDirectory = Path.Combine(root, name, "dicom") });
}
ImportResult Complete((PrototypeResult Source, ImportOptions Options) f, IMalwareScanner scan, CancellationToken token = default) =>
    new ImportPipeline(scan).CompleteAsync(f.Source, f.Options, quiet, token).GetAwaiter().GetResult();
Check("scan pass publishes verified ISO and only DICOM with names/bytes preserved", () =>
{
    var f = Fixture("publish"); var r = Complete(f, new StubScanner("Passed"));
    Assert(r.Error == null && r.Record.IsoPublished && r.Record.DicomPublished && r.Record.DicomCount == 3);
    var output = r.Record.PublishedDicomDirectory!;
    Assert(File.Exists(Path.Combine(output, "nested", "IM0001.dcm")));
    Assert(File.Exists(Path.Combine(output, "IM0003.IMA")) && Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length == 3);
    Assert(SafeFiles.HashFile(Path.Combine(output, "nested", "IM0001.dcm"), default) == dicomManifest.Files.Single(f => f.RelativePath.EndsWith("IM0001")).Sha256);
    Assert(File.Exists(r.Record.PublishedIsoPath));
});
foreach (var state in new[] { "ThreatDetected", "Unknown", "Disabled", "NotScanned", "Cancelled" })
    Check($"scan {state} blocks all output", () =>
    {
        var f = Fixture("blocked-" + state); var r = Complete(f, new StubScanner(state));
        Assert(r.Error != null && !r.Record.IsoPublished && !r.Record.DicomPublished);
        Assert(!Directory.EnumerateFileSystemEntries(f.Options.IsoDirectory).Any() && !Directory.EnumerateFileSystemEntries(f.Options.DicomDirectory).Any());
    });
Check("successful remediation changing source cannot pass gate", () =>
{
    var f = Fixture("changed-after-scan");
    var r = Complete(f, new StubScanner("Passed", dir => File.Delete(Path.Combine(dir, "viewer.exe"))));
    Assert(r.Error != null && !r.Record.IsoPublished);
});
Check("ISO changed after verification cannot publish", () =>
{
    var f = Fixture("changed-iso");
    var r = Complete(f, new StubScanner("Passed", _ => File.AppendAllText(f.Source.IsoPath, "changed")));
    Assert(r.Error != null && !r.Record.IsoPublished);
});
Check("name collisions after .dcm suffix block both outputs", () =>
{
    var f = Fixture("name-collision"); var tree = Path.Combine(f.Source.WorkDirectory, "source");
    File.Copy(Path.Combine(tree, "nested", "IM0001"), Path.Combine(tree, "nested", "IM0001.dcm"));
    File.WriteAllText(Path.Combine(f.Source.WorkDirectory, "manifest.json"), JsonSerializer.Serialize(SafeFiles.ReadTree(tree, default)));
    var r = Complete(f, new StubScanner("Passed")); Assert(r.Error != null && !r.Record.IsoPublished);
});
Check("existing ISO preserved and next ordinal allocated", () =>
{
    var f = Fixture("iso-collision"); f.Options.CheckWritable();
    var old = Path.Combine(f.Options.IsoDirectory, InputRules.IsoFileName("00123", DateOnly.FromDateTime(f.Source.Record.Started.LocalDateTime)));
    File.WriteAllText(old, "existing");
    var r = Complete(f, new StubScanner("Passed"));
    Assert(r.Error == null && r.Record.PublishedIsoPath!.EndsWith("-02.iso") && File.ReadAllText(old) == "existing");
});
Check("DICOM failure after ISO is recorded as partial output", () =>
{
    var f = Fixture("partial"); f.Options.CheckWritable();
    Directory.CreateDirectory(Path.Combine(f.Options.DicomDirectory, $".{f.Source.Record.JobId}.partial"));
    var r = Complete(f, new StubScanner("Passed"));
    Assert(r.Error != null && r.Record.State == "PartialOutput" && r.Record.IsoPublished && !r.Record.DicomPublished);
    var saved = JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(Path.Combine(f.Source.WorkDirectory, "job.json")))!;
    Assert(saved.IsoPublished && saved.Finished.HasValue && saved.State == "PartialOutput");
});
Check("cancelled scan never publishes", () =>
{
    var f = Fixture("cancel-scan"); using var cts = new CancellationTokenSource();
    var r = Complete(f, new StubScanner("Passed", _ => cts.Cancel()), cts.Token);
    Assert(r.Record.State == "Cancelled" && !r.Record.IsoPublished);
});
Check("no DICOM still saves ISO and explicitly reports no DICOM", () =>
{
    var f = Fixture("no-dicom");
    var tree = Path.Combine(f.Source.WorkDirectory, "source");
    foreach (var file in Directory.GetFiles(tree, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith("viewer.exe") && !p.EndsWith("readme"))) File.Delete(file);
    File.WriteAllText(Path.Combine(f.Source.WorkDirectory, "manifest.json"), JsonSerializer.Serialize(SafeFiles.ReadTree(tree, default)));
    var r = Complete(f, new StubScanner("Passed"));
    Assert(r.Error == null && r.Record.State == "IsoOnly_NoDicom" && r.Record.IsoPublished && !r.Record.DicomPublished);
});
Check("nested/relative/quarantine output paths rejected", () =>
{
    Reject<ArgumentException>(() => new ImportOptions { IsoDirectory = "relative", DicomDirectory = root }.Validate());
    Reject<ArgumentException>(() => new ImportOptions { IsoDirectory = root, DicomDirectory = Path.Combine(root, "sub") }.Validate());
    Reject<ArgumentException>(() => new ImportOptions { IsoDirectory = PrototypeImporter.WorkRoot, DicomDirectory = root }.Validate());
});
if (args.Contains("--defender"))
{
    Check("real Defender clean directory completes with correlated events", () =>
    {
        var scan = new DefenderScanner().ScanAsync(dicomRoot, 7, default).GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(scan));
        Assert(scan.Passed && scan.StartEventId.HasValue && scan.EndEventId.HasValue && !string.IsNullOrEmpty(scan.ScanId));
    });
}
if (args.Contains("--defender-blocked"))
{
    Check("real Defender insufficient rights blocks output with visible reason", () =>
    {
        var f = Fixture("real-defender-blocked"); var r = Complete(f, new DefenderScanner());
        Console.WriteLine(r.Error);
        Assert(r.Error != null && r.Record.ScanState == "Unknown" && !r.Record.IsoPublished && !r.Record.DicomPublished);
    });
}

if (args.Contains("--imapi"))
{
    Check("real Windows IMAPI builds UDF image", () =>
    {
        var iso = Path.Combine(root, "dummy.iso");
        new ImapiImageBuilder().Build(source, iso, quiet, default);
        Assert(new FileInfo(iso).Length > 0);
        Reject<IOException>(() => new ImapiImageBuilder().Build(source, iso, quiet, default));
    });
    if (args.Contains("--mount"))
    {
        Check("real mount verifies all bytes and names; always dismounts", () =>
        {
            var mounter = new WindowsImageMounter();
            var iso = Path.Combine(root, "dummy.iso");
            try
            {
                var mounted = mounter.MountAsync(iso, default).GetAwaiter().GetResult();
                SafeFiles.Verify(original!, SafeFiles.ReadTree(mounted, default));
            }
            finally { mounter.DismountAsync(iso).GetAwaiter().GetResult(); }
        });
    }
}
Console.WriteLine($"Failures: {failures}; dummy artifacts: {root}");
return failures == 0 ? 0 : 1;

sealed class QuietProgress : IProgress<ImportProgress>
{
    public void Report(ImportProgress value) { }
}

sealed class StubScanner(string state, Action<string>? action = null) : IMalwareScanner
{
    public Task<ScanResult> ScanAsync(string directory, int signatureMaxAgeDays, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        action?.Invoke(directory);
        return Task.FromResult(new ScanResult(state, "Synthetic test verdict"));
    }
}
