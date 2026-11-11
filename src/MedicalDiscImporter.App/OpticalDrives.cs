using System.Runtime.InteropServices;

namespace MedicalDiscImporter.App;

internal sealed record OpticalDriveSnapshot(string Root, bool Ready, string Status, string VolumeLabel = "")
{
    public string DisplayLabel
    {
        get
        {
            var label = VolumeLabel.Trim();
            if (label.Length == 0) return "ラベルなし";
            return label;
        }
    }

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
                    ? $"{drive.DriveFormat} / {drive.TotalSize / 1048576:N0} MiB"
                    : "ディスクを挿入してください", ready ? drive.VolumeLabel : ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                drives.Add(new(drive.Name, false, "ドライブを確認中です。しばらくお待ちください"));
            }
        }
        return drives.OrderBy(d => d.Root, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // IMAPI device calls can block. Create, use and release COM objects on the worker thread.
    public static Task EjectAsync(string root) => Task.Run(() =>
    {
        object? master = null;
        try
        {
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (new DriveInfo(root).DriveType != DriveType.CDRom)
                throw new IOException("取込元の光学ドライブが見つかりません。");
            master = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2", true)!)!;
            dynamic devices = master;
            for (var i = 0; i < (int)devices.Count; i++)
            {
                object? recorder = null;
                try
                {
                    recorder = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2", true)!)!;
                    dynamic device = recorder;
                    device.InitializeDiscRecorder((string)devices[i]);
                    var paths = (Array)device.VolumePathNames;
                    if (!paths.Cast<string>().Any(path => Path.TrimEndingDirectorySeparator(path)
                            .Equals(target, StringComparison.OrdinalIgnoreCase))) continue;
                    device.EjectMedia();
                    return;
                }
                finally
                {
                    if (recorder != null && Marshal.IsComObject(recorder)) Marshal.FinalReleaseComObject(recorder);
                }
            }
            throw new IOException("取込元の光学ドライブが見つかりません。");
        }
        finally
        {
            if (master != null && Marshal.IsComObject(master)) Marshal.FinalReleaseComObject(master);
        }
    });

    public static string? SelectRoot(IReadOnlyList<OpticalDriveSnapshot> available, string? selected)
    {
        // Keep a usable user selection. Otherwise prefer a drive that already has media.
        return available.FirstOrDefault(d => d.Root == selected && d.Ready)?.Root
            ?? available.FirstOrDefault(d => d.Ready)?.Root
            ?? available.FirstOrDefault(d => d.Root == selected)?.Root
            ?? available.FirstOrDefault()?.Root;
    }
}
