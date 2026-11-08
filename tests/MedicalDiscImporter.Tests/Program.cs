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

Check("release versions parse calendar dates and compare same-day revisions numerically", () =>
{
    Assert(ReleaseVersion.TryParse("v20261106", out var a) && a.Display == "v20261106");
    Assert(ReleaseVersion.TryParse("20261106.2", out var b));
    Assert(ReleaseVersion.TryParse("v20261106.10", out var c) && c.CompareTo(b) > 0);
    Assert(ReleaseVersion.TryParse("v20261107", out var d) && d.CompareTo(c) > 0);
    Assert(ReleaseVersion.TryParse("v20261106.0", out var same) && same.CompareTo(a) == 0);
    foreach (var value in new string?[] { null, "", "1.0.0.0", "v20260230", "v20261301", "v20261106-preview", "v20261106.-1", "v20261106.01", "v20261106.65535", new string('1', 100) })
        Assert(!ReleaseVersion.TryParse(value, out _));
});
Check("service versions are independent integers and known date releases remain compatible", () =>
{
    Assert(ReleaseVersion.TryParse(ApplicationRelease.DisplayVersion, out var current) && current == ApplicationRelease.Current);
    Assert(ServiceRelease.Compare(ServiceRelease.DisplayVersion) == ServiceVersionMatch.Same);
    Assert(ServiceRelease.Compare("1", 2) == ServiceVersionMatch.Older);
    Assert(ServiceRelease.Compare("10", 2) == ServiceVersionMatch.Newer);
    foreach (var legacy in new[] { "v20261106", "v20261106.1" })
        Assert(ServiceRelease.TryGetCompatibleVersion(legacy, out var version) && version == 1 && ServiceRelease.Compare(legacy) == ServiceVersionMatch.Same);
    foreach (var invalid in new string?[] { null, "", "0", "01", "-1", "1.0", "1.0.0.0", "v1", "１", "65535", "v20261107", "v20261106.2" })
        Assert(ServiceRelease.Compare(invalid) == ServiceVersionMatch.Unknown);
    Assert(ServiceRelease.TryParse("65534", out var maximum) && maximum == 65534);
});Check("legacy service responses remain readable without version metadata", () =>
{
    var legacy = JsonSerializer.Deserialize<ScanResult>("{\"State\":\"Ready\",\"Reason\":\"Legacy response\"}")!;
    Assert(legacy.State == "Ready" && legacy.ServiceVersion == null && !legacy.Passed);
    var response = new ScanResult("Ready", "Current response", ServiceVersion: ServiceRelease.DisplayVersion);
    var restored = JsonSerializer.Deserialize<ScanResult>(JsonSerializer.Serialize(response))!;
    Assert(restored.ServiceVersion == ServiceRelease.DisplayVersion && restored.State == "Ready");
});

Check("auto eject defaults off for existing settings and round-trips both states", () =>
{
    Assert(!new ImportOptions().EjectAfterImport);
    Assert(!JsonSerializer.Deserialize<ImportOptions>("{}")!.EjectAfterImport);
    foreach (var enabled in new[] { true, false })
    {
        var options = new ImportOptions { EjectAfterImport = enabled };
        var restored = JsonSerializer.Deserialize<ImportOptions>(JsonSerializer.Serialize(options))!;
        Assert(restored.EjectAfterImport == enabled);
        Assert(!(restored with { CreateIso = false, ScanForMalware = false, SendDicom = false }).HasAnyOperation);
    }
});

Check("patient ID rejects unsafe and full-width input", () =>
{
    foreach (var id in new[] { "", "../1", "1~2", "CON", "１２３", "1\n", new string('1', 33) }) Reject<ArgumentException>(() => InputRules.ValidatePatientId(id));
    InputRules.ValidatePatientId("000123");
});
Check("RSBase ISO naming preserves ID and uses six-digit ordinals and import date", () =>
{
    Assert(RsBasePublisher.IsoFileName("0001", 1, new DateOnly(2026, 11, 4), "CDROM") == "0001~000001~2026_11_04~CDROM~RSB.iso");
    Assert(RsBasePublisher.IsoFileName("0001", 2, new DateOnly(2026, 11, 4), "紹介CD") == "0001~000002~2026_11_04~紹介CD~RSB.iso");
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

(PrototypeResult Source, ImportOptions Options) Fixture(string name, DateTimeOffset? started = null)
{
    var work = Path.Combine(root, name, "work"); Directory.CreateDirectory(work);
    var tree = Path.Combine(work, "source"); Directory.CreateDirectory(tree);
    var snapshot = SafeFiles.ReadTree(dicomRoot, default, tree);
    File.WriteAllText(Path.Combine(work, "manifest.json"), JsonSerializer.Serialize(snapshot));
    var iso = Path.Combine(work, "test.iso"); File.WriteAllText(iso, "dummy ISO bytes; no mounting in this test");
    var job = new JobRecord { PatientId = "00123", Started = started ?? DateTimeOffset.Now, ContentsVerified = true, Unmounted = true, IsoSha256 = SafeFiles.HashFile(iso, default) };
    return (new(job, work, iso), new() { IsoDirectory = Path.Combine(root, name, "iso"), DicomDirectory = Path.Combine(root, name, "dicom") });
}
ImportResult Complete((PrototypeResult Source, ImportOptions Options) f, IMalwareScanner scan, CancellationToken token = default) =>
    new ImportPipeline(scan).CompleteAsync(f.Source, f.Options, quiet, token).GetAwaiter().GetResult();
Check("scan pass publishes verified ISO and flat RSB-named DICOM with bytes preserved", () =>
{
    var f = Fixture("publish"); var r = Complete(f, new StubScanner("Passed"));
    Assert(r.Error == null && r.Record.IsoPublished && r.Record.DicomPublished && r.Record.DicomCount == 3);
    var output = r.Record.PublishedDicomDirectory!;
    var named = Path.Combine(output, "00123~000001~2026_10_31~CTMRI~RSB.dcm");
    Assert(File.Exists(named) && Directory.GetFiles(output, "*~RSB.dcm", SearchOption.TopDirectoryOnly).Length == 3);
    Assert(SafeFiles.HashFile(named, default) == dicomManifest.Files.Single(f => f.RelativePath.EndsWith("IM0001")).Sha256);
    Assert(r.Record.DicomFilesPublished == 3 && r.Record.RsBaseExamName == "CTMRI");
    var ledger = JsonSerializer.Deserialize<List<RsBaseOutput>>(File.ReadAllText(Path.Combine(f.Source.WorkDirectory, "rsbase-output.json")))!;
    Assert(ledger.Count == 3 && ledger.All(e => e.State == "Published"));
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
    var r = Complete(f, new StubScanner("Passed", _ => File.AppendAllText(f.Source.IsoPath!, "changed")));
    Assert(r.Error != null && !r.Record.IsoPublished);
});
Check("identical original basenames receive distinct RSB ordinals", () =>
{
    var f = Fixture("name-collision"); var tree = Path.Combine(f.Source.WorkDirectory, "source");
    File.Copy(Path.Combine(tree, "nested", "IM0001"), Path.Combine(tree, "nested", "IM0001.dcm"));
    File.WriteAllText(Path.Combine(f.Source.WorkDirectory, "manifest.json"), JsonSerializer.Serialize(SafeFiles.ReadTree(tree, default)));
    var r = Complete(f, new StubScanner("Passed"));
    Assert(r.Error == null && r.Record.DicomFilesPublished == 4 &&
        File.Exists(Path.Combine(f.Options.DicomDirectory, "00123~000002~2026_10_31~CTMRI~RSB.dcm")));
});
Check("existing ISO preserved and next ordinal allocated", () =>
{
    var f = Fixture("iso-collision"); f.Options.CheckWritable();
    var old = Path.Combine(f.Options.IsoDirectory, RsBasePublisher.IsoFileName("00123", 1, DateOnly.FromDateTime(f.Source.Record.Started.LocalDateTime), "CDROM"));
    File.WriteAllText(old, "existing");
    var r = Complete(f, new StubScanner("Passed"));
    Assert(r.Error == null && Path.GetFileName(r.Record.PublishedIsoPath!).Split('~')[1] == "000002" && File.ReadAllText(old) == "existing");
});
Check("ISO uses import start date and independent configurable exam with verified bytes", () =>
{
    var started = new DateTimeOffset(2026, 10, 20, 23, 59, 0, TimeSpan.FromHours(9));
    var f = Fixture("iso-date-exam", started);
    f = (f.Source, f.Options with { IsoExamName = "紹介ディスク" });
    var r = Complete(f, new StubScanner("Passed"));
    var date = DateOnly.FromDateTime(started.LocalDateTime);
    Assert(r.Error == null && Path.GetFileName(r.Record.PublishedIsoPath) == RsBasePublisher.IsoFileName("00123", 1, date, "紹介ディスク"));
    Assert(r.Record.IsoExamName == "紹介ディスク" && r.Record.RsBaseExamName == "CTMRI");
    Assert(SafeFiles.HashFile(r.Record.PublishedIsoPath!, default) == r.Record.IsoSha256);
    var ledger = JsonSerializer.Deserialize<List<RsBaseOutput>>(File.ReadAllText(Path.Combine(f.Source.WorkDirectory, "rsbase-iso-output.json")))!;
    Assert(ledger.Count == 1 && ledger[0].State == "Published" && ledger[0].FileName == Path.GetFileName(r.Record.PublishedIsoPath));
});
Check("ISO sequence survives immediate watcher consumption and exam changes", () =>
{
    var first = Fixture("iso-consumed-first");
    var a = Complete(first, new StubScanner("Passed"));
    Assert(a.Error == null);
    File.Delete(a.Record.PublishedIsoPath!); // Simulate RSBase consuming the output.
    var second = Fixture("iso-consumed-second", first.Source.Record.Started);
    second = (second.Source, second.Options with { IsoDirectory = first.Options.IsoDirectory, IsoExamName = "別検査" });
    var b = Complete(second, new StubScanner("Passed"));
    Assert(b.Error == null && Path.GetFileName(b.Record.PublishedIsoPath!).Split('~')[1] == "000002");
});
Check("concurrent ISO publications reserve distinct ordinals", () =>
{
    var a = Fixture("iso-concurrent-a");
    var b = Fixture("iso-concurrent-b", a.Source.Record.Started);
    b = (b.Source, b.Options with { IsoDirectory = a.Options.IsoDirectory });
    var runs = Task.WhenAll(Task.Run(() => Complete(a, new StubScanner("Passed"))), Task.Run(() => Complete(b, new StubScanner("Passed")))).GetAwaiter().GetResult();
    Assert(runs.All(r => r.Error == null) && runs.Select(r => r.Record.PublishedIsoPath).Distinct().Count() == 2);
});
Check("ISO sequence ledger corruption blocks publication rather than reusing numbers", () =>
{
    var f = Fixture("iso-corrupt");
    Directory.CreateDirectory(Path.Combine(f.Options.IsoDirectory, ".MedicalDiscImporter"));
    File.WriteAllText(Path.Combine(f.Options.IsoDirectory, ".MedicalDiscImporter", "sequence.json"), "broken");
    var r = Complete(f, new StubScanner("Passed"));
    Assert(r.Error != null && !r.Record.IsoPublished && !Directory.GetFiles(f.Options.IsoDirectory, "*.iso").Any());
});
Check("legacy settings default ISO exam to CDROM and custom values survive JSON", () =>
{
    var old = JsonSerializer.Deserialize<ImportOptions>("{\"RsBaseExamName\":\"旧検査\"}")!;
    Assert(old.IsoExamName == "CDROM" && old.RsBaseExamName == "旧検査");
    var restored = JsonSerializer.Deserialize<ImportOptions>(JsonSerializer.Serialize(old with { IsoExamName = "紹介CD" }))!;
    Assert(restored.IsoExamName == "紹介CD" && restored.RsBaseExamName == "旧検査");
    foreach (var exam in new[] { "", "bad~exam", "bad/exam", "bad\nexam", new string('a', 41) })
        Reject<ArgumentException>(() => (restored with { IsoExamName = exam, SendDicom = false }).Validate());
});
Check("ISO and DICOM share a folder and continue shared ordinals after watcher consumption", () =>
{
    var a = Fixture("shared-outputs-a", new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    // Case and trailing separator differences still refer to the same Windows folder.
    a = (a.Source, a.Options with { DicomDirectory = a.Options.IsoDirectory.ToUpperInvariant() + Path.DirectorySeparatorChar });
    var first = Complete(a, new StubScanner("Passed"));
    Assert(first.Error == null && first.Record.IsoPublished && first.Record.DicomPublished && first.Record.DicomFilesPublished == 3);
    Assert(SafeFiles.HashFile(first.Record.PublishedIsoPath!, default) == first.Record.IsoSha256);
    var isoName = Path.GetFileName(first.Record.PublishedIsoPath!);
    var dicomLedger = JsonSerializer.Deserialize<List<RsBaseOutput>>(File.ReadAllText(Path.Combine(a.Source.WorkDirectory, "rsbase-output.json")))!;
    Assert(dicomLedger.All(e => e.State == "Published" && SafeFiles.HashFile(Path.Combine(a.Options.IsoDirectory, e.FileName), default) == e.Sha256));
    var names = dicomLedger.Select(e => e.FileName).Append(isoName).ToArray();
    Assert(names.Select(n => string.Join("~", n.Split('~').Take(3))).Distinct().Count() == 4);
    foreach (var name in names) File.Delete(Path.Combine(a.Options.IsoDirectory, name));
    var b = Fixture("shared-outputs-b", a.Source.Record.Started);
    b = (b.Source, b.Options with { IsoDirectory = a.Options.IsoDirectory, DicomDirectory = a.Options.IsoDirectory });
    var second = Complete(b, new StubScanner("Passed"));
    Assert(second.Error == null && Path.GetFileName(second.Record.PublishedIsoPath!).Split('~')[1] == "000004");
    Assert(Directory.GetFiles(a.Options.IsoDirectory, "*~RSB.*").Length == 4);
});
Check("concurrent imports share one ISO DICOM output folder without collisions", () =>
{
    var a = Fixture("shared-concurrent-a");
    a = (a.Source, a.Options with { DicomDirectory = a.Options.IsoDirectory });
    var b = Fixture("shared-concurrent-b", a.Source.Record.Started);
    b = (b.Source, b.Options with { IsoDirectory = a.Options.IsoDirectory, DicomDirectory = a.Options.IsoDirectory });
    var runs = Task.WhenAll(Task.Run(() => Complete(a, new StubScanner("Passed"))), Task.Run(() => Complete(b, new StubScanner("Passed")))).GetAwaiter().GetResult();
    Assert(runs.All(r => r.Error == null && r.Record.IsoPublished && r.Record.DicomFilesPublished == 3));
    var names = Directory.GetFiles(a.Options.IsoDirectory, "*~RSB.*").Select(Path.GetFileName).ToArray();
    Assert(names.Length == 8 && names.Select(n => string.Join("~", n!.Split('~').Take(3))).Distinct().Count() == 8);
});
Check("DICOM failure after ISO is recorded as partial output", () =>
{
    var f = Fixture("partial"); f.Options.CheckWritable();
    Directory.CreateDirectory(Path.Combine(f.Options.DicomDirectory, ".MedicalDiscImporter", "staging", f.Source.Record.JobId));
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
Check("legacy settings default all operations on and selection survives JSON roundtrip", () =>
{
    var legacy = JsonSerializer.Deserialize<ImportOptions>("{\"IsoDirectory\":\"\",\"DicomDirectory\":\"\"}")!;
    Assert(legacy.CreateIso && legacy.ScanForMalware && legacy.SendDicom);
    var selected = legacy with { CreateIso = false, ScanForMalware = false, SendDicom = true };
    var restored = JsonSerializer.Deserialize<ImportOptions>(JsonSerializer.Serialize(selected))!;
    Assert(!restored.CreateIso && !restored.ScanForMalware && restored.SendDicom);
});
PrototypeResult WithoutIso(PrototypeResult source)
{
    File.Delete(source.IsoPath!);
    return source with { IsoPath = null, Record = new JobRecord
    {
        PatientId = source.Record.PatientId, ContentsVerified = true, ImageKind = "None", CreateIsoRequested = false
    } };
}
for (var mask = 0; mask < 8; mask++)
{
    var choice = mask;
    Check($"operation selection {choice:000} only invokes selected scan and outputs", () =>
    {
        var f = Fixture("selection-" + choice);
        var iso = (choice & 1) != 0; var scan = (choice & 2) != 0; var dicom = (choice & 4) != 0;
        var selected = f.Options with
        {
            CreateIso = iso, ScanForMalware = scan, SendDicom = dicom,
            IsoDirectory = iso ? f.Options.IsoDirectory : "unused-invalid-ISO-path",
            DicomDirectory = dicom ? f.Options.DicomDirectory : "unused-invalid-DICOM-path",
            RsBaseExamName = dicom ? "CTMRI" : "invalid~unused"
        };
        var sourceJob = iso ? f.Source : WithoutIso(f.Source);
        var scanner = new CountingScanner();
        var r = Complete((sourceJob, selected), scanner);
        Assert(scanner.Calls == (scan ? 1 : 0));
        if (choice == 0)
        {
            Assert(r.Error != null && !r.Record.IsoPublished && !r.Record.DicomPublished);
            Assert(!Directory.Exists(f.Options.IsoDirectory) && !Directory.Exists(f.Options.DicomDirectory));
            return;
        }
        Assert(r.Error == null && r.Record.IsoPublished == iso && r.Record.DicomPublished == dicom);
        Assert(Directory.Exists(f.Options.IsoDirectory) == iso && Directory.Exists(f.Options.DicomDirectory) == dicom);
        Assert(r.Record.ScanState == (scan ? "Passed" : "SkippedByUser"));
        Assert(r.Record.CreateIsoRequested == iso && r.Record.ScanRequested == scan && r.Record.DicomSendRequested == dicom);
        Assert(r.Record.DicomCount == (dicom ? 3 : 0));
        if (!scan) Assert(r.Record.Scan?.Passed == false && r.Record.Scan.Describe().Contains("未検査"));
        if (!iso) Assert(r.Record.IsoSha256 == null && !r.Record.Unmounted);
        var saved = JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(Path.Combine(r.WorkDirectory, "job.json")))!;
        Assert(saved.ScanRequested == scan && saved.CreateIsoRequested == iso && saved.DicomSendRequested == dicom);
    });
}
Check("file integrity remains mandatory with malware scan explicitly off", () =>
{
    var f = Fixture("unscanned-changed-tree");
    File.AppendAllText(Path.Combine(f.Source.WorkDirectory, "source", "viewer.exe"), "changed after read");
    var scanner = new CountingScanner();
    var r = Complete((f.Source, f.Options with { ScanForMalware = false }), scanner);
    Assert(r.Error != null && scanner.Calls == 0 && !r.Record.IsoPublished && !r.Record.DicomPublished);
});
Check("ISO integrity remains mandatory with malware scan explicitly off", () =>
{
    var f = Fixture("unscanned-changed-iso"); File.AppendAllText(f.Source.IsoPath!, "changed");
    var r = Complete((f.Source, f.Options with { ScanForMalware = false }), new CountingScanner());
    Assert(r.Error != null && !r.Record.IsoPublished && !r.Record.DicomPublished);
});
Check("scan failure still blocks DICOM when ISO creation is off", () =>
{
    var f = Fixture("dicom-only-scan-failure"); var sourceJob = WithoutIso(f.Source);
    var r = Complete((sourceJob, f.Options with { CreateIso = false, IsoDirectory = "unused" }), new StubScanner("ThreatDetected"));
    Assert(r.Error != null && !r.Record.DicomPublished && !r.Record.IsoPublished && r.Record.ScanState == "ThreatDetected");
});
Check("scan-only never parses or sends DICOM and needs no output paths", () =>
{
    var f = Fixture("scan-only-malformed-dicom"); var sourceJob = WithoutIso(f.Source);
    var tree = Path.Combine(sourceJob.WorkDirectory, "source");
    File.WriteAllText(Path.Combine(tree, "broken.dcm"), "intentionally malformed DICOM");
    File.WriteAllText(Path.Combine(sourceJob.WorkDirectory, "manifest.json"), JsonSerializer.Serialize(SafeFiles.ReadTree(tree, default)));
    var options = new ImportOptions { CreateIso = false, SendDicom = false, IsoDirectory = "", DicomDirectory = "" };
    var r = Complete((sourceJob, options), new CountingScanner());
    Assert(r.Error == null && r.Record.ScanState == "Passed" && r.Record.DicomCount == 0 && !r.Record.IsoPublished && !r.Record.DicomPublished);
});
Check("RSB exam names reject separators, traversal and blank names", () =>
{
    foreach (var name in new[] { "", " ", "CT~MR", "CT/MR", "CT\\MR", "CT\nMR", "CT.", new string('a', 41) })
        Reject<ArgumentException>(() => RsBasePublisher.ValidateExamName(name));
    Assert(RsBasePublisher.FileName("0001", 1, new(2026, 11, 5), "紹介CT") == "0001~000001~2026_11_05~紹介CT~RSB.dcm");
    Assert(RsBasePublisher.FileName("0001", 1000000, new(2026, 11, 5), "CTMRI").Contains("~1000000~"));
});
Check("RSB uses custom exam name and continues existing day sequence without overwrite", () =>
{
    var f = Fixture("rsb-existing");
    f.Options.CheckWritable();
    var previous = Path.Combine(f.Options.DicomDirectory, "00123~999999~2026_10_31~旧検査~RSB.dcm");
    File.WriteAllText(previous, "existing bytes");
    var imported = Complete((f.Source, f.Options with { RsBaseExamName = "紹介MRI" }), new StubScanner("Passed"));
    Assert(imported.Error == null && File.ReadAllText(previous) == "existing bytes");
    Assert(File.Exists(Path.Combine(f.Options.DicomDirectory, "00123~1000000~2026_10_31~紹介MRI~RSB.dcm")));
    Assert(File.Exists(Path.Combine(f.Options.DicomDirectory, "00123~000001~2026_11_01~紹介MRI~RSB.dcm")));
});
Check("RSB sequence remains monotonic after the watcher consumes all output files", () =>
{
    var f = Fixture("rsb-watcher"); f.Options.CheckWritable();
    var entries = new DicomCatalog().Read(Path.Combine(f.Source.WorkDirectory, "source"),
        SafeFiles.ReadTree(Path.Combine(f.Source.WorkDirectory, "source"), default), new(2026, 11, 5), quiet, default);
    var names = new List<string>();
    for (var pass = 0; pass < 2; pass++)
    {
        var work = Path.Combine(root, "watcher-job-" + pass); Directory.CreateDirectory(work);
        RsBasePublisher.Publish(Path.Combine(f.Source.WorkDirectory, "source"), f.Options.DicomDirectory, work,
            Guid.NewGuid().ToString("N"), "00123", "CTMRI", entries, quiet,
            path => { names.Add(Path.GetFileName(path)); File.Delete(path); }, default);
    }
    Assert(names.Count == 6 && names.Distinct().Count() == 6 && names.Skip(3).All(n => n.Contains("~000002~")));
});
Check("concurrent RSB jobs share durable sequence reservations", () =>
{
    var a = Fixture("rsb-parallel-a"); var b = Fixture("rsb-parallel-b");
    var second = (b.Source, b.Options with { DicomDirectory = a.Options.DicomDirectory });
    var results = Task.WhenAll(Task.Run(() => Complete(a, new StubScanner("Passed"))),
        Task.Run(() => Complete(second, new StubScanner("Passed")))).GetAwaiter().GetResult();
    Assert(results.All(r => r.Error == null && r.Record.DicomFilesPublished == 3));
    Assert(Directory.GetFiles(a.Options.DicomDirectory, "*~RSB.dcm").Length == 6);
});
Check("RSB collision from an external producer never overwrites its file", () =>
{
    var f = Fixture("rsb-external"); f.Options.CheckWritable();
    var sourceTree = Path.Combine(f.Source.WorkDirectory, "source");
    var entries = new DicomCatalog().Read(sourceTree, SafeFiles.ReadTree(sourceTree, default), new(2026, 11, 5), quiet, default)
        .Select(e => e with { StudyDate = new DateOnly(2026, 10, 31) }).ToArray();
    var external = Path.Combine(f.Options.DicomDirectory, "00123~000002~2026_10_31~CTMRI~RSB.dcm");
    var count = 0;
    RsBasePublisher.Publish(sourceTree, f.Options.DicomDirectory, f.Source.WorkDirectory, f.Source.Record.JobId,
        "00123", "CTMRI", entries, quiet, _ => { if (++count == 1) File.WriteAllText(external, "external bytes"); }, default);
    Assert(count == 3 && File.ReadAllText(external) == "external bytes" &&
        File.Exists(Path.Combine(f.Options.DicomDirectory, "00123~000004~2026_10_31~CTMRI~RSB.dcm")) &&
        File.Exists(Path.Combine(f.Options.DicomDirectory, "00123~000005~2026_10_31~CTMRI~RSB.dcm")));
});
Check("RSB cancellation after first rename preserves an auditable partial handoff", () =>
{
    var f = Fixture("rsb-cancel-partial"); f.Options.CheckWritable();
    var sourceTree = Path.Combine(f.Source.WorkDirectory, "source");
    var entries = new DicomCatalog().Read(sourceTree, SafeFiles.ReadTree(sourceTree, default), new(2026, 11, 5), quiet, default);
    using var cts = new CancellationTokenSource(); var published = 0;
    Reject<OperationCanceledException>(() => RsBasePublisher.Publish(sourceTree, f.Options.DicomDirectory,
        f.Source.WorkDirectory, f.Source.Record.JobId, "00123", "CTMRI", entries, quiet,
        _ => { published++; cts.Cancel(); }, cts.Token));
    var ledger = JsonSerializer.Deserialize<List<RsBaseOutput>>(File.ReadAllText(Path.Combine(f.Source.WorkDirectory, "rsbase-output.json")))!;
    Assert(published == 1 && ledger.Count(e => e.State == "Published") == 1 &&
        Directory.GetFiles(f.Options.DicomDirectory, "*~RSB.dcm").Length == 1);
});
Check("corrupt RSB sequence metadata blocks handoff without resetting numbers", () =>
{
    var f = Fixture("rsb-bad-state"); f.Options.CheckWritable();
    var state = Path.Combine(f.Options.DicomDirectory, ".MedicalDiscImporter"); Directory.CreateDirectory(state);
    File.WriteAllText(Path.Combine(state, "sequence.json"), "not JSON");
    var imported = Complete(f, new StubScanner("Passed"));
    Assert(imported.Error != null && imported.Record.State == "PartialOutput" && imported.Record.DicomFilesPublished == 0 &&
        Directory.GetFiles(f.Options.DicomDirectory, "*~RSB.dcm").Length == 0);
});
Check("scan summaries retain unavailable values instead of inventing zeroes", () =>
{
    Assert(new ScanResult("Unknown", "unavailable").Describe().Contains("件数取得不可"));
    var scan = new ScanResult("Passed", "ok", TargetFileCount: 194, ThreatDetectionCount: 0, ElapsedSeconds: 7.5);
    var restored = JsonSerializer.Deserialize<ScanResult>(JsonSerializer.Serialize(scan))!;
    Assert(restored.TargetFileCount == 194 && restored.ThreatDetectionCount == 0 && restored.ElapsedSeconds == 7.5);
});
Check("service start waits for automatic startup without issuing a second start", () =>
{
    var service = new ScriptedService([new(2), new(2), new(4)]);
    new ServiceStateTransitions(service, TimeSpan.Zero).StartAsync().GetAwaiter().GetResult();
    Assert(service.Starts == 0 && service.Stops == 0);
});
Check("service start tolerates an already-running race", () =>
{
    var service = new ScriptedService([new(1), new(4)], [1056]);
    new ServiceStateTransitions(service, TimeSpan.Zero).StartAsync().GetAwaiter().GetResult();
    Assert(service.Starts == 1);
});
Check("service start waits for prior stop and starts once", () =>
{
    var service = new ScriptedService([new(3), new(1), new(2), new(4)]);
    new ServiceStateTransitions(service, TimeSpan.Zero).StartAsync().GetAwaiter().GetResult();
    Assert(service.Starts == 1);
});
Check("service stop waits for startup then for complete stop", () =>
{
    var service = new ScriptedService([new(2), new(4), new(3), new(3), new(1)]);
    new ServiceStateTransitions(service, TimeSpan.Zero).StopAsync().GetAwaiter().GetResult();
    Assert(service.Stops == 1 && service.Starts == 0);
});
Check("service stop tolerates not-active and cannot-accept-control races", () =>
{
    var service = new ScriptedService([new(4), new(4), new(1)], stopErrors: [1061, 1062]);
    new ServiceStateTransitions(service, TimeSpan.Zero).StopAsync().GetAwaiter().GetResult();
    Assert(service.Stops == 2);
});
Check("stopped service removal does not send another stop", () =>
{
    var service = new ScriptedService([new(1)]);
    new ServiceStateTransitions(service, TimeSpan.Zero).StopAsync().GetAwaiter().GetResult();
    Assert(service.Stops == 0);
});
Check("failed startup and disabled service preserve the Windows error", () =>
{
    foreach (var service in new[] { new ScriptedService([new(1), new(2), new(1, 1064)]),
        new ScriptedService([new(1)], [1058]) })
    {
        try { new ServiceStateTransitions(service, TimeSpan.Zero).StartAsync().GetAwaiter().GetResult(); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1064 or 1058) { continue; }
        throw new Exception("Expected Windows startup error");
    }
});
Check("pending service transition has a bounded wait", () =>
{
    var service = new ScriptedService([new(3)]);
    Reject<IOException>(() => new ServiceStateTransitions(service, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10))
        .StopAsync().GetAwaiter().GetResult());
});
Check("service listener preserves delayed responses and repeated connections", () =>
{
    var work = Path.Combine(root, "listener");
    SafeFiles.CreatePrivateDirectory(work);
    var name = "MedicalDiscImporter.Test." + Guid.NewGuid().ToString("N");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut,
        1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 65536, 65536);
    var listening = new ScanServiceListener(new ScanServiceWorker(new StubScanner("Passed")), work).RunAsync(server, deadline.Token);
    try
    {
        for (var i = 0; i < 30; i++)
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous);
            client.ConnectAsync(deadline.Token).GetAwaiter().GetResult();
            ScanServiceProtocol.WriteAsync(client, new ScanServiceRequest(ScanServiceProtocol.Version, "Ping"), deadline.Token).GetAwaiter().GetResult();
            Thread.Sleep(10); // Let the server finish writing before the client starts reading.
            var result = ScanServiceProtocol.ReadResponseAsync(client, deadline.Token).GetAwaiter().GetResult();
            Assert(result.State == "Ready" && result.ServiceVersion == ServiceRelease.DisplayVersion);
        }
    }
    finally
    {
        deadline.Cancel();
        try { listening.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
    }
});
Check("service listener survives client closure before accepting a connection", () =>
{
    var work = Path.Combine(root, "listener-early-close");
    SafeFiles.CreatePrivateDirectory(work);
    var name = "MedicalDiscImporter.Test." + Guid.NewGuid().ToString("N");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    System.IO.Pipes.NamedPipeServerStream CreatePipe() => new(name, System.IO.Pipes.PipeDirection.InOut,
        1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 65536, 65536);
    var server = CreatePipe();
    using (var abandoned = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut,
        System.IO.Pipes.PipeOptions.Asynchronous)) abandoned.ConnectAsync(deadline.Token).GetAwaiter().GetResult();
    var listening = new ScanServiceListener(new ScanServiceWorker(new StubScanner("Passed")), work)
        .RunAsync(server, deadline.Token, CreatePipe);
    try
    {
        using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        client.ConnectAsync(deadline.Token).GetAwaiter().GetResult();
        ScanServiceProtocol.WriteAsync(client, new ScanServiceRequest(ScanServiceProtocol.Version, "Ping"), deadline.Token).GetAwaiter().GetResult();
        Assert(ScanServiceProtocol.ReadResponseAsync(client, deadline.Token).GetAwaiter().GetResult().State == "Ready");
    }
    finally
    {
        deadline.Cancel();
        try { listening.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
    }
});
Check("service stop interrupts idle connection wait promptly", () =>
{
    var work = Path.Combine(root, "listener-idle-stop");
    SafeFiles.CreatePrivateDirectory(work);
    using var stopping = new CancellationTokenSource();
    using var server = new System.IO.Pipes.NamedPipeServerStream("MedicalDiscImporter.Test." + Guid.NewGuid().ToString("N"),
        System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
        System.IO.Pipes.PipeOptions.Asynchronous, 65536, 65536);
    var listening = new ScanServiceListener(new ScanServiceWorker(new StubScanner("Passed")), work).RunAsync(server, stopping.Token);
    stopping.CancelAsync().GetAwaiter().GetResult();
    listening.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
});
Check("service stop interrupts pending response acknowledgment promptly", () =>
{
    var work = Path.Combine(root, "listener-ack-stop");
    SafeFiles.CreatePrivateDirectory(work);
    var name = "MedicalDiscImporter.Test." + Guid.NewGuid().ToString("N");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut,
        1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 65536, 65536);
    var listening = new ScanServiceListener(new ScanServiceWorker(new StubScanner("Passed")), work).RunAsync(server, deadline.Token);
    try
    {
        using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);
        client.ConnectAsync(deadline.Token).GetAwaiter().GetResult();
        ScanServiceProtocol.WriteAsync(client, new ScanServiceRequest(ScanServiceProtocol.Version, "Ping"), deadline.Token).GetAwaiter().GetResult();
        // Deliberately do not ACK. STOP must cancel the service's pending read.
        Assert(ScanServiceProtocol.ReadAsync<ScanResult>(client, deadline.Token).GetAwaiter().GetResult().State == "Ready");
        deadline.CancelAsync().GetAwaiter().GetResult();
        listening.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    }
    finally { deadline.Cancel(); listening.GetAwaiter().GetResult(); }
});
// Exercise the production worker over a real, unique duplex pipe without elevation or Defender changes.
(ScanResult? Result, Exception? Error, string Work) ExerciseWorker(TreeSnapshot tree, byte[] payload,
    IMalwareScanner scanner, bool disconnect = false)
{
    var work = Path.Combine(root, "service-" + Guid.NewGuid().ToString("N"));
    SafeFiles.CreatePrivateDirectory(work);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var name = "MedicalDiscImporter.Test." + Guid.NewGuid().ToString("N");
    using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut,
        1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
    var handling = Task.Run(async () =>
    {
        try
        {
            await server.WaitForConnectionAsync(deadline.Token);
            await new ScanServiceWorker(scanner).HandleAsync(server, work, deadline.Token);
            return (Exception?)null;
        }
        catch (Exception ex) { return ex; }
        finally { server.Dispose(); }
    });
    ScanResult? response = null;
    using (var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut,
        System.IO.Pipes.PipeOptions.Asynchronous))
    {
        client.ConnectAsync(deadline.Token).GetAwaiter().GetResult();
        ScanServiceProtocol.WriteAsync(client, new ScanServiceRequest(ScanServiceProtocol.Version, "Scan", 7, tree), deadline.Token).GetAwaiter().GetResult();
        client.WriteAsync(payload, deadline.Token).AsTask().GetAwaiter().GetResult();
        client.Flush();
        if (!disconnect)
        {
            try { response = ScanServiceProtocol.ReadResponseAsync(client, deadline.Token).GetAwaiter().GetResult(); }
            catch (EndOfStreamException) { }
        }
    }
    return (response, handling.GetAwaiter().GetResult(), work);
}
var serviceBytes = new byte[] { 1, 2, 3, 4 };
var serviceTree = new TreeSnapshot([new("日本語\\dummy.dat", 4,
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(serviceBytes)))], ["日本語", "empty"]);
Check("service worker scans matching private bytes and removes staging", () =>
{
    var invoked = false;
    var run = ExerciseWorker(serviceTree, serviceBytes, new StubScanner("Passed", path =>
    {
        invoked = true;
        SafeFiles.Verify(serviceTree, SafeFiles.ReadTree(path, default));
    }));
    Assert(invoked && run.Result!.Passed && run.Result.ServiceVersion == ServiceRelease.DisplayVersion &&
        run.Error == null && !Directory.EnumerateFileSystemEntries(run.Work).Any());
});
Check("service worker rejects corrupted upload before scanner and removes staging", () =>
{
    var invoked = false;
    var run = ExerciseWorker(serviceTree, [9, 9, 9, 9], new StubScanner("Passed", _ => invoked = true));
    Assert(!invoked && run.Result == null && run.Error is IOException && !Directory.EnumerateFileSystemEntries(run.Work).Any());
});
Check("service worker rejects scanner mutations and removes staging", () =>
{
    var run = ExerciseWorker(serviceTree, serviceBytes, new StubScanner("Passed", path =>
        File.WriteAllText(Path.Combine(path, "unexpected"), "changed")));
    Assert(run.Result == null && run.Error is IOException && !Directory.EnumerateFileSystemEntries(run.Work).Any());
});
Check("service worker reports threat without retaining uploaded data", () =>
{
    var run = ExerciseWorker(serviceTree, serviceBytes, new StubScanner("ThreatDetected"));
    Assert(run.Result!.State == "ThreatDetected" && run.Error == null && !Directory.EnumerateFileSystemEntries(run.Work).Any());
});
Check("service worker cancels disconnected scan and removes staging", () =>
{
    var run = ExerciseWorker(serviceTree, serviceBytes, new WaitingScanner(), true);
    Assert(run.Result == null && run.Error is OperationCanceledException && !Directory.EnumerateFileSystemEntries(run.Work).Any());
});
Check("service worker removes incomplete uploads after disconnect", () =>
{
    var run = ExerciseWorker(serviceTree, [1], new StubScanner("Passed"), true);
    Assert(run.Result == null && run.Error is IOException && !Directory.EnumerateFileSystemEntries(run.Work).Any());
});
Check("service request accepts complete bounded tree", () =>
    ScanServiceProtocol.Validate(new(ScanServiceProtocol.Version, "Scan", 7, original), source));
Check("service rejects command and incompatible protocol", () =>
{
    Reject<IOException>(() => ScanServiceProtocol.Validate(new(ScanServiceProtocol.Version, "Execute"), source));
    Reject<IOException>(() => ScanServiceProtocol.Validate(new(ScanServiceProtocol.Version + 1, "Ping"), source));
});
Check("service rejects traversal, devices, ADS and noncanonical names", () =>
{
    foreach (var name in new[] { "..\\escape", "C:\\escape", "file:stream", "CON.txt", "LPT1", "COM¹.txt", "a/b", "a.", "a ", "a\\\\b" })
        Reject<IOException>(() => ScanServiceProtocol.Validate(new(ScanServiceProtocol.Version, "Scan", 7,
            new TreeSnapshot([new(name, 0, new string('0', 64))], [])), source));
});
Check("service rejects duplicate, missing parent and file-directory conflicts", () =>
{
    foreach (var tree in new[] {
        new TreeSnapshot([new("A", 0, new string('0', 64)), new("a", 0, new string('0', 64))], []),
        new TreeSnapshot([new("missing\\a", 0, new string('0', 64))], []),
        new TreeSnapshot([new("a", 0, new string('0', 64))], ["a"]) })
        Reject<IOException>(() => ScanServiceProtocol.Validate(new(ScanServiceProtocol.Version, "Scan", 7, tree), source));
});
Check("service rejects excessive size, invalid hash and negative length", () =>
{
    foreach (var file in new FileRecord[] { new("a", -1, new string('0', 64)),
        new("a", SafeFiles.MaxBytes + 1, new string('0', 64)), new("a", 0, "invalid") })
        Reject<IOException>(() => ScanServiceProtocol.Validate(new(ScanServiceProtocol.Version, "Scan", 7, new([file], [])), source));
    Reject<IOException>(() => ScanServiceProtocol.Validate(new(ScanServiceProtocol.Version, "Scan", 31, original), source));
});
Check("service rejects aggregate size and entry count limits", () =>
{
    Reject<IOException>(() => ScanServiceProtocol.Validate(new(ScanServiceProtocol.Version, "Scan", 7, new([
        new("a", SafeFiles.MaxBytes, new string('0', 64)), new("b", 1, new string('0', 64))], [])), source));
    Reject<IOException>(() => ScanServiceProtocol.Validate(new(ScanServiceProtocol.Version, "Scan", 7,
        new([], Enumerable.Range(0, SafeFiles.MaxEntries + 1).Select(i => "d" + i).ToArray())), source));
});
Check("service bounds framing before allocation and rejects truncated headers", () =>
{
    foreach (var size in new[] { -1, 0, ScanServiceProtocol.MaxMessageBytes + 1 })
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(size));
        Reject<IOException>(() => ScanServiceProtocol.ReadAsync<ScanServiceRequest>(stream, default).GetAwaiter().GetResult());
    }
    using var truncated = new MemoryStream(new byte[2]);
    Reject<EndOfStreamException>(() => ScanServiceProtocol.ReadAsync<ScanServiceRequest>(truncated, default).GetAwaiter().GetResult());
});
Check("service upload detects disconnect and copies only declared bytes", () =>
{
    using var input = new MemoryStream(new byte[] { 1, 2, 3 });
    using var output = new MemoryStream();
    ScanServiceProtocol.CopyExactlyAsync(input, output, 2, default).GetAwaiter().GetResult();
    Assert(output.ToArray().SequenceEqual(new byte[] { 1, 2 }) && input.Position == 2);
    Reject<EndOfStreamException>(() => ScanServiceProtocol.CopyExactlyAsync(input, output, 2, default).GetAwaiter().GetResult());
});
Check("service framing preserves Japanese paths and empty directories", () =>
{
    using var stream = new MemoryStream();
    ScanServiceProtocol.WriteAsync(stream, new ScanServiceRequest(ScanServiceProtocol.Version, "Scan", 7, original), default).GetAwaiter().GetResult();
    stream.Position = 0;
    var request = ScanServiceProtocol.ReadAsync<ScanServiceRequest>(stream, default).GetAwaiter().GetResult();
    SafeFiles.Verify(original!, request.Manifest!);
});
Check("cancelled service request cannot authorize output", () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    Reject<OperationCanceledException>(() => new ServiceMalwareScanner().ScanAsync(source, 7, cts.Token).GetAwaiter().GetResult());
});
if (args.Contains("--service-spoof"))
{
    Check("client rejects a standard-user-owned fake service endpoint", () =>
    {
        using var fake = new System.IO.Pipes.NamedPipeServerStream(ScanServiceProtocol.PipeName,
            System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.FirstPipeInstance);
        var connected = fake.WaitForConnectionAsync();
        var scan = new ServiceMalwareScanner().ScanAsync(source, 7, default).GetAwaiter().GetResult();
        connected.GetAwaiter().GetResult();
        Assert(!scan.Passed && scan.State == "Unknown");
    });
}
if (args.Contains("--service"))
{
    Check("installed service scans clean dummy DICOM with correlated evidence", () =>
    {
        ServiceMalwareScanner.EnsureAvailableAsync(default).GetAwaiter().GetResult();
        var scan = new ServiceMalwareScanner().ScanAsync(dicomRoot, 7, default).GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(scan));
        Assert(scan.Passed && scan.StartEventId > 0 && scan.EndEventId > scan.StartEventId);
        Assert(scan.TargetFileCount == dicomManifest.Files.Count && scan.ThreatDetectionCount == 0 && scan.ElapsedSeconds >= 0);
    });
    Check("real service verdict authorizes dummy fixture ISO and DICOM publication", () =>
    {
        // Optical reading/ISO building is outside this test; use the existing verified fixture bytes.
        var f = Fixture("real-service-publish");
        var imported = Complete(f, new ServiceMalwareScanner());
        Assert(imported.Error == null && imported.Record.ScanState == "Passed" &&
            imported.Record.IsoPublished && imported.Record.DicomPublished && imported.Record.DicomCount == 3);
        Assert(File.Exists(imported.Record.PublishedIsoPath));
        Assert(Directory.GetFiles(imported.Record.PublishedDicomDirectory!, "*~RSB.dcm", SearchOption.TopDirectoryOnly).Length == 3);
        Assert(imported.Record.DicomFilesPublished == 3 && imported.Record.Scan?.ThreatDetectionCount == 0);
        var saved = JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(Path.Combine(f.Source.WorkDirectory, "job.json")))!;
        Assert(saved.Scan?.TargetFileCount == dicomManifest.Files.Count && saved.Scan.ElapsedSeconds >= 0 && saved.RsBaseExamName == "CTMRI");
    });
}
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
        Check("ISO off reads and verifies a dummy disc without building or mounting another image", () =>
        {
            var mounter = new WindowsImageMounter();
            var iso = Path.Combine(root, "dummy.iso");
            try
            {
                var mounted = mounter.MountAsync(iso, default).GetAwaiter().GetResult();
                var phases = new RecordingProgress();
                var prepared = new PrototypeImporter(new ForbiddenImageBuilder(), new ForbiddenImageMounter())
                    .RunAsync(mounted, "000123", phases, default, new ImportOptions { CreateIso = false, ScanForMalware = false })
                    .GetAwaiter().GetResult();
                Assert(prepared.IsoPath == null && prepared.Record.IsoSha256 == null && prepared.Record.ContentsVerified && !prepared.Record.Unmounted);
                Assert(prepared.Record.ImageKind == "None" && !prepared.Record.CreateIsoRequested);
                Assert(!Directory.EnumerateFiles(prepared.WorkDirectory, "*.iso*").Any());
                Assert(phases.Values.All(p => p.Phase is null or ImportPhase.ReadingDisc));
                SafeFiles.Verify(original!, SafeFiles.ReadTree(Path.Combine(prepared.WorkDirectory, "source"), default));
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

sealed class WaitingScanner : IMalwareScanner
{
    public async Task<ScanResult> ScanAsync(string directory, int signatureMaxAgeDays, CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        throw new Exception("Unreachable");
    }
}

sealed class CountingScanner : IMalwareScanner
{
    public int Calls { get; private set; }
    public Task<ScanResult> ScanAsync(string directory, int signatureMaxAgeDays, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(new ScanResult("Passed", "Synthetic test verdict"));
    }
}

sealed class RecordingProgress : IProgress<ImportProgress>
{
    public List<ImportProgress> Values { get; } = [];
    public void Report(ImportProgress value) => Values.Add(value);
}

sealed class ForbiddenImageBuilder : IImageBuilder
{
    public void Build(string sourceDirectory, string outputPath, IProgress<ImportProgress> progress, CancellationToken token) =>
        throw new Exception("ISO OFF must not invoke the image builder");
}

sealed class ForbiddenImageMounter : IImageMounter
{
    public Task<string> MountAsync(string imagePath, CancellationToken token) => throw new Exception("ISO OFF must not mount an image");
    public Task DismountAsync(string imagePath) => throw new Exception("ISO OFF must not dismount an image");
}

sealed class ScriptedService(ServiceState[] states, int[]? startErrors = null, int[]? stopErrors = null) : IServiceControlOperations
{
    private int position;
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public ServiceState Query() => states[Math.Min(position++, states.Length - 1)];
    public int RequestStart()
    {
        var error = startErrors?.ElementAtOrDefault(Starts) ?? 0;
        Starts++;
        return error;
    }
    public int RequestStop()
    {
        var error = stopErrors?.ElementAtOrDefault(Stops) ?? 0;
        Stops++;
        return error;
    }
}
