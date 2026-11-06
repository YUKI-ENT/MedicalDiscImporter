using System.Globalization;

namespace MedicalDiscImporter.Core;

public enum ServiceVersionMatch { Unknown, Same, Older, Newer }

// Increment only when the scan service changes. Application date/revision is independent.
public static class ServiceRelease
{
    public const int Current = 1;
    public static string DisplayVersion => Current.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? value, out int version)
    {
        version = 0;
        return value is { Length: > 0 and <= 5 } && value[0] != '0' && value.All(char.IsAsciiDigit) &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out version) && version <= 65534;
    }

    public static bool TryGetCompatibleVersion(string? value, out int version)
    {
        if (TryParse(value, out version)) return true;
        // These released date-numbered services have the same scan implementation as service 1.
        // Do not assume compatibility for arbitrary unknown/future date-numbered builds.
        if (ReleaseVersion.TryParse(value, out var legacy) && legacy.Date == new DateOnly(2026, 11, 6) && legacy.Revision <= 1)
        {
            version = 1;
            return true;
        }
        return false;
    }

    public static ServiceVersionMatch Compare(string? version, int referenceVersion = Current)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(referenceVersion, 1);
        if (!TryGetCompatibleVersion(version, out var service)) return ServiceVersionMatch.Unknown;
        return service.CompareTo(referenceVersion) switch { < 0 => ServiceVersionMatch.Older, > 0 => ServiceVersionMatch.Newer, _ => ServiceVersionMatch.Same };
    }
}
