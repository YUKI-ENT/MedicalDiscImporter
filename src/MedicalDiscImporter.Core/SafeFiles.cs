using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace MedicalDiscImporter.Core;

public static class SafeFiles
{
    public const long MaxBytes = 20L * 1024 * 1024 * 1024;
    public const int MaxEntries = 100_000;

    public static void CreatePrivateDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        for (var parent = info.Parent; parent != null; parent = parent.Parent)
            if (parent.Exists) RejectLink(parent);
        if (info.Exists) RejectLink(info);
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Windows SID不明");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
        if (!info.Exists) FileSystemAclExtensions.Create(info, acl);
        else info.SetAccessControl(acl);
    }

    public static void RejectLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("リンク・再解析ポイントは取り込めません。");
    }

    public static void ValidateDirectoryPath(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
            if (directory.Exists) RejectLink(directory);
    }

    // Destructive operations must be confined to a direct child of the explicitly supplied parent.
    public static void DeleteOwnedDirectory(string directory, string parent) => DeleteOwnedEntry(directory, parent);

    public static void DeleteOwnedEntry(string path, string parent)
    {
        var full = Path.GetFullPath(path);
        var owner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (!string.Equals(Path.GetDirectoryName(full), owner, StringComparison.OrdinalIgnoreCase))
            throw new IOException("削除対象が作業領域の外を指しています。");
        ValidateDirectoryPath(owner);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(full); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("リンク・再解析ポイントは削除対象にできません。");
        FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(full) : new FileInfo(full);
        RejectLink(entry);
        if (entry is DirectoryInfo directory)
        {
            List<Exception> failures = [];
            foreach (var child in directory.EnumerateFileSystemInfos())
            {
                try { DeleteOwnedEntry(child.FullName, full); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(ex); }
            }
            if (failures.Count > 0) throw new IOException("一部の作業ファイルを削除できませんでした。", failures[0]);
            directory.Delete(false);
        }
        else
        {
            // Read-only files copied from optical media must also be removed.
            entry.Attributes &= ~FileAttributes.ReadOnly;
            entry.Delete();
        }
    }

    public static string SafeRelative(string root, string fullPath)
    {
        if (fullPath.Split(Path.DirectorySeparatorChar).Any(p => p.EndsWith('.') || p.EndsWith(' ')))
            throw new IOException("対応できない末尾文字を含むパスです。");
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new IOException("作業領域の外を指すパスです。");
        if (relative.Length > 220 || relative.Split(Path.DirectorySeparatorChar).Any(p =>
                p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Contains(':')))
            throw new IOException("対応できないファイル名・長いパスです。");
        return relative;
    }

    // Iterate explicitly: do not follow reparse points or ignore access errors.
    public static TreeSnapshot ReadTree(string root, CancellationToken token,
        string? copyTo = null, IProgress<ImportProgress>? progress = null)
    {
        RejectLink(new DirectoryInfo(root));
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        var files = new List<FileRecord>();
        var dirs = new List<string>();
        var reportClock = System.Diagnostics.Stopwatch.StartNew();
        long total = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                RejectLink(entry);
                var relative = SafeRelative(root, entry.FullName);
                if (files.Count + dirs.Count >= MaxEntries) throw new IOException("ファイル数上限超過");
                if (entry is DirectoryInfo child)
                {
                    dirs.Add(relative);
                    if (copyTo != null) Directory.CreateDirectory(Path.Combine(copyTo, relative));
                    pending.Push(child);
                    continue;
                }
                var file = (FileInfo)entry;
                total = checked(total + file.Length);
                if (total > MaxBytes) throw new IOException("容量上限20 GiBを超えています。");
                using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var output = copyTo == null ? null : new FileStream(Path.Combine(copyTo, relative), FileMode.CreateNew);
                var buffer = new byte[1024 * 1024];
                int read;
                long length = 0;
                while ((read = input.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    length += read;
                    if (length > file.Length) throw new IOException("読取中にファイルが変化しました。");
                    hash.AppendData(buffer, 0, read);
                    output?.Write(buffer, 0, read);
                }
                if (length != file.Length) throw new IOException("ファイルの読み取りが不完全です。");
                files.Add(new FileRecord(relative, length, Convert.ToHexString(hash.GetHashAndReset())));
                if (reportClock.ElapsedMilliseconds >= 200)
                {
                    progress?.Report(new ImportProgress($"ファイル読取・照合中：{files.Count:N0}件 / {total / 1048576:N0} MiB"));
                    reportClock.Restart();
                }
            }
        }
        return new TreeSnapshot(files, dirs);
    }

    public static void Verify(TreeSnapshot expected, TreeSnapshot actual)
    {
        var report = Compare(expected, actual);
        if (!report.Matches) throw new ContentVerificationException(report);
    }

    public static VerificationReport Compare(TreeSnapshot expected, TreeSnapshot actual)
    {
        var differences = new List<VerificationDifference>();
        var expectedFiles = expected.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var actualFiles = actual.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        foreach (var file in expectedFiles.Values.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            if (!actualFiles.TryGetValue(file.RelativePath, out var found))
                differences.Add(new("MissingFile", file.RelativePath, file));
            else if (file.Length != found.Length)
                differences.Add(new("LengthMismatch", file.RelativePath, file, found));
            else if (file.Sha256 != found.Sha256)
                differences.Add(new("HashMismatch", file.RelativePath, file, found));
        }
        foreach (var file in actualFiles.Values.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            if (!expectedFiles.ContainsKey(file.RelativePath))
                differences.Add(new("UnexpectedFile", file.RelativePath, Actual: file));
        foreach (var path in expected.Directories.Except(actual.Directories, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            differences.Add(new("MissingDirectory", path));
        foreach (var path in actual.Directories.Except(expected.Directories, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            differences.Add(new("UnexpectedDirectory", path));
        return new VerificationReport(differences);
    }

    public static string HashFile(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int n;
        while ((n = stream.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, n);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
