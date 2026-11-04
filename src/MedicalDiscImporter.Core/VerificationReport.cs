namespace MedicalDiscImporter.Core;

public sealed record VerificationDifference(string Kind, string RelativePath,
    FileRecord? Expected = null, FileRecord? Actual = null);

public sealed record VerificationReport(IReadOnlyList<VerificationDifference> Differences)
{
    public bool Matches => Differences.Count == 0;

    public string Summary => string.Join("、", Differences.GroupBy(d => d.Kind).Select(g =>
        $"{g.Key switch
        {
            "MissingFile" => "ファイル欠落",
            "UnexpectedFile" => "追加ファイル",
            "LengthMismatch" => "サイズ相違",
            "HashMismatch" => "内容相違",
            "MissingDirectory" => "フォルダ欠落",
            "UnexpectedDirectory" => "追加フォルダ",
            _ => g.Key
        }} {g.Count():N0}件"));
}

public sealed class ContentVerificationException(VerificationReport report)
    : IOException("ISO内容不一致：" + report.Summary + "。詳細は作業領域のverification.jsonを確認してください。")
{
    public VerificationReport Report { get; } = report;
}
