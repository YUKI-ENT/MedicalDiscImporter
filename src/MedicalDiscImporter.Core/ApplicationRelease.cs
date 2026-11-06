using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MedicalDiscImporter.Core;

public readonly partial record struct ReleaseVersion(DateOnly Date, int Revision = 0) : IComparable<ReleaseVersion>
{
    public string Display => "v" + Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
        (Revision == 0 ? "" : "." + Revision.ToString(CultureInfo.InvariantCulture));
    public int CompareTo(ReleaseVersion other) => Date != other.Date ? Date.CompareTo(other.Date) : Revision.CompareTo(other.Revision);

    [GeneratedRegex(@"\A[vV]?(?<date>[0-9]{8})(?:\.(?<revision>0|[1-9][0-9]{0,4}))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (value == null || value.Length > 32) return false;
        var match = VersionPattern().Match(value.Trim());
        if (!match.Success || !DateOnly.TryParseExact(match.Groups["date"].Value, "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
        var revision = 0;
        if (match.Groups["revision"].Success && (!int.TryParse(match.Groups["revision"].Value, out revision) || revision > 65534)) return false;
        version = new(date, revision);
        return true;
    }
}

public static class ApplicationRelease
{
    public static ReleaseVersion Current { get; } = ReadCurrent();
    public static string DisplayVersion => Current.Display;

    private static ReleaseVersion ReadCurrent()
    {
        var value = typeof(ApplicationRelease).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return ReleaseVersion.TryParse(value, out var release) ? release
            : throw new InvalidOperationException("Version.propsのリリースバージョンが不正です。");
    }
}
