using System.Text.Json;

namespace MedicalDiscImporter.Core;

public sealed record ImportOptions
{
    public string IsoDirectory { get; init; } = "";
    public string DicomDirectory { get; init; } = "";
    public int SignatureMaxAgeDays { get; init; } = 7;
    public string RsBaseExamName { get; init; } = "CTMRI";

    public void Validate()
    {
        if (SignatureMaxAgeDays is < 1 or > 30) throw new ArgumentException("Defender定義の有効日数は1～30日です。");
        RsBasePublisher.ValidateExamName(RsBaseExamName);
        foreach (var path in new[] { IsoDirectory, DicomDirectory })
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("設定でISO・DICOMの出力先を絶対パスで指定してください。");
            var full = Path.GetFullPath(path);
            if (IsWithin(full, PrototypeImporter.WorkRoot) || IsWithin(PrototypeImporter.WorkRoot, full))
                throw new ArgumentException("出力先は隔離作業領域と重ならないフォルダにしてください。");
            // Check every ancestor; never traverse an output junction or symlink.
            for (var dir = new DirectoryInfo(full); dir != null; dir = dir.Parent)
                if (dir.Exists) SafeFiles.RejectLink(dir);
        }
        if (IsWithin(IsoDirectory, DicomDirectory) || IsWithin(DicomDirectory, IsoDirectory))
            throw new ArgumentException("ISOとDICOMには別々の、包含関係のない出力フォルダを指定してください。");
    }

    private static bool IsWithin(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public void CheckWritable()
    {
        Validate();
        foreach (var path in new[] { IsoDirectory, DicomDirectory })
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, Guid.NewGuid().ToString("N") + ".write-test");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
    }

    public static string SettingsPath => Path.Combine(Path.GetDirectoryName(PrototypeImporter.WorkRoot)!, "settings.json");
    public static ImportOptions Load() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<ImportOptions>(File.ReadAllText(SettingsPath)) ?? throw new IOException("設定ファイルが不正です。") : new();

    public void Save()
    {
        CheckWritable();
        SafeFiles.CreatePrivateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(SettingsPath + ".tmp", SettingsPath, true);
    }
}
