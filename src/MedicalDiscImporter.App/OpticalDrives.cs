namespace MedicalDiscImporter.App;

internal sealed record OpticalDriveSnapshot(string Root, bool Ready, string Status)
{
    public override string ToString() => $"{Root}  CD / DVDドライブ";
}

internal static class OpticalDrives
{
    // Optical hardware can take time to respond. All device I/O runs off the UI thread.
    public static IReadOnlyList<OpticalDriveSnapshot> Read()
    {
        var drives = new List<OpticalDriveSnapshot>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.CDRom) continue;
                var ready = drive.IsReady;
                drives.Add(new(drive.Name, ready, ready
                    ? $"ディスク認識済み  ·  {drive.DriveFormat}  /  {drive.TotalSize / 1048576:N0} MiB"
                    : "ディスクを挿入してください"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                drives.Add(new(drive.Name, false, "ドライブを確認中です。しばらくお待ちください"));
            }
        }
        return drives.OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string? SelectRoot(IReadOnlyList<OpticalDriveSnapshot> available, string? selected)
    {
        // Keep a usable user selection. Otherwise prefer a drive that already has media.
        return available.FirstOrDefault(d => d.Root == selected && d.Ready)?.Root
            ?? available.FirstOrDefault(d => d.Ready)?.Root
            ?? available.FirstOrDefault(d => d.Root == selected)?.Root
            ?? available.FirstOrDefault()?.Root;
    }
}
