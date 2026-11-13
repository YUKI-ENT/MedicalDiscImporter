using System.Security.Cryptography;
using System.Text;

namespace MedicalDiscImporter.Core;

public static class OutputStorage
{
    public static string Root => Path.Combine(Path.GetDirectoryName(PrototypeImporter.WorkRoot)!, "OutputState");

    public static string MetadataDirectory(string destination)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(Root, key);
    }

    internal static void CreateMetadataDirectory(string destination)
    {
        SafeFiles.CreatePrivateDirectory(Root);
        SafeFiles.CreatePrivateDirectory(MetadataDirectory(destination));
    }

    internal static string PendingOutputPath(string destination, string jobId, int index) =>
        Path.Combine(Path.GetFullPath(destination), $".MedicalDiscImporter-{jobId}-{index:00000000}.pending");

    internal static bool IsPendingOutput(string path, string jobId)
    {
        var name = Path.GetFileName(path);
        var prefix = $".MedicalDiscImporter-{jobId}-";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".pending", StringComparison.Ordinal) &&
            name.Length == prefix.Length + 8 + ".pending".Length && name.Substring(prefix.Length, 8).All(char.IsAsciiDigit);
    }

    internal static bool IsLocalStaging(string path, string jobId)
    {
        var parent = Path.GetDirectoryName(path)!;
        var metadata = Path.GetDirectoryName(parent)!;
        var key = Path.GetFileName(metadata);
        return Path.GetFileName(path) == jobId && Path.GetFileName(parent) == "staging" &&
            string.Equals(Path.GetDirectoryName(metadata), Root, StringComparison.OrdinalIgnoreCase) &&
            key.Length == 64 && key.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');
    }
}
