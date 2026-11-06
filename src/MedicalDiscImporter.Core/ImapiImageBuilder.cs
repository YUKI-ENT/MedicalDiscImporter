using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MedicalDiscImporter.Core;

// Windows inbox COM API, no third-party binary or native sector I/O.
public sealed class ImapiImageBuilder : IImageBuilder
{
    public void Build(string sourceDirectory, string outputPath, IProgress<ImportProgress> progress, CancellationToken token)
    {
        object? image = null, root = null, result = null, streamObject = null;
        var count = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            token.ThrowIfCancellationRequested();
            var type = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage", throwOnError: true)!;
            image = Activator.CreateInstance(type)!;
            dynamic fsi = image;
            fsi.StrictFileSystemCompliance = true;
            fsi.FileSystemsToCreate = 4; // FsiFileSystemUDF
            fsi.FreeMediaBlocks = 0; // unlimited; source already bounded to MaxBytes
            fsi.VolumeName = "MEDICAL_DISC";
            root = fsi.Root;
            progress.Report(new ImportProgress("ISOの構成を準備しています", Phase: ImportPhase.CreatingIso));
            ((dynamic)root).AddTree(sourceDirectory, false);
            token.ThrowIfCancellationRequested();
            result = fsi.CreateResultImage();
            streamObject = ((dynamic)result).ImageStream;
            var stream = (IStream)streamObject;
            stream.Stat(out var stat, 1);
            if (stat.cbSize <= 0 || stat.cbSize > SafeFiles.MaxBytes + 1024L * 1024 * 1024)
                throw new IOException("ISO容量が範囲外です。");
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[1024 * 1024];
            long written = 0;
            var reportClock = System.Diagnostics.Stopwatch.StartNew();
            while (written < stat.cbSize)
            {
                token.ThrowIfCancellationRequested();
                stream.Read(buffer, (int)Math.Min(buffer.Length, stat.cbSize - written), count);
                var n = Marshal.ReadInt32(count);
                if (n <= 0 || n > buffer.Length) throw new IOException("ISOストリーム読取失敗");
                output.Write(buffer, 0, n);
                written += n;
                if (reportClock.ElapsedMilliseconds >= 200 || written == stat.cbSize)
                {
                    progress.Report(new ImportProgress("ISOを作成しています", (int)(written * 100 / stat.cbSize), ImportPhase.CreatingIso));
                    reportClock.Restart();
                }
            }
            output.Flush(true);
        }
        finally
        {
            Marshal.FreeCoTaskMem(count);
            foreach (var obj in new[] { streamObject, result, root, image })
                if (obj != null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
        }
    }
}
