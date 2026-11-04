using System.Text.RegularExpressions;

namespace MedicalDiscImporter.Core;

public sealed record ImportProgress(string Stage, int? Percent = null);
public sealed record FileRecord(string RelativePath, long Length, string Sha256);
public sealed record TreeSnapshot(IReadOnlyList<FileRecord> Files, IReadOnlyList<string> Directories)
{
    public long Bytes => Files.Sum(f => f.Length);
}

public sealed class JobRecord
{
    public string JobId { get; init; } = Guid.NewGuid().ToString("N");
    public required string PatientId { get; init; }
    public DateTimeOffset Started { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? Finished { get; set; }
    public string ImageKind { get; init; } = "Reconstructed";
    public string DiscType { get; set; } = "Unknown";
    public string State { get; set; } = "Started";
    public string ScanState { get; init; } = "NotImplemented_FormalOutputBlocked";
    public string? IsoSha256 { get; set; }
    public int FileCount { get; set; }
    public long SourceBytes { get; set; }
    public bool ContentsVerified { get; set; }
    public bool Unmounted { get; set; }
    public bool IsoPublished { get; init; } = false;
    public bool DicomPublished { get; init; } = false;
    public string? ErrorCode { get; set; }
    public string Warning { get; init; } = "再構築ISO。原本のトラック・セッション・起動情報等は保持しません。未検査・本番利用不可。";
}

public sealed record PrototypeResult(JobRecord Record, string WorkDirectory, string IsoPath);

public static partial class InputRules
{
    [GeneratedRegex("\\A[0-9]{1,32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PatientPattern();

    public static void ValidatePatientId(string id)
    {
        if (!PatientPattern().IsMatch(id))
            throw new ArgumentException("患者IDは半角数字1～32桁で入力してください。");
    }

    public static string IsoFileName(string id, DateOnly date, int ordinal = 1)
    {
        ValidatePatientId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        return $"{id}-{date:yyyyMMdd}{(ordinal == 1 ? "" : $"-{ordinal:00}")}.iso";
    }
}

// Implement production engines behind these boundaries after hardware validation.
public interface IImageBuilder
{
    void Build(string sourceDirectory, string outputPath, IProgress<ImportProgress> progress, CancellationToken token);
}

public interface IImageMounter
{
    Task<string> MountAsync(string imagePath, CancellationToken token);
    Task DismountAsync(string imagePath);
}
