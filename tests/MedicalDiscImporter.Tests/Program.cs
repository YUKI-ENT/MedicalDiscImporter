using MedicalDiscImporter.Core;

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
