using System.Globalization;
using System.Text;
using FellowOakDicom;

namespace MedicalDiscImporter.Core;

public sealed record DicomEntry(string RelativePath, string PatientName, DateOnly StudyDate, string DateSource,
    string Sequence, int SeriesNumber, int InstanceNumber, string SeriesUid, string SopInstanceUid, FileRecord File);

public sealed class DicomCatalog
{
    static DicomCatalog() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public IReadOnlyList<DicomEntry> Read(string root, TreeSnapshot manifest, DateOnly importDate,
        IProgress<ImportProgress> progress, CancellationToken token)
    {
        var entries = new List<DicomEntry>();
        var count = 0;
        foreach (var record in manifest.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(root, record.RelativePath);
            SafeFiles.RejectLink(new FileInfo(path));
            var header = DicomFile.HasValidHeader(path);
            var candidate = header || Path.GetExtension(path).Equals(".dcm", StringComparison.OrdinalIgnoreCase);
            try
            {
                // No image decoding or native codecs. Do not load pixel buffers into memory.
                var file = DicomFile.Open(path, Encoding.ASCII, _ => { token.ThrowIfCancellationRequested(); return false; },
                    FileReadOption.SkipLargeTags, 64 * 1024);
                if (file.IsPartial) throw new InvalidDataException("DICOM読取が不完全です。");
                var d = file.Dataset;
                var sopClass = Text(d, DicomTag.SOPClassUID);
                if (sopClass == DicomUID.MediaStorageDirectoryStorage.UID ||
                    Text(file.FileMetaInfo, DicomTag.MediaStorageSOPClassUID) == DicomUID.MediaStorageDirectoryStorage.UID) continue;
                var sop = Text(d, DicomTag.SOPInstanceUID);
                if (sopClass.Length == 0 || sop.Length == 0)
                {
                    if (candidate) throw new InvalidDataException("DICOMのSOP識別子がありません。");
                    continue;
                }
                var date = ResolveDate(d, importDate);
                var sequence = new[] { Text(d, DicomTag.SeriesDescription), Text(d, DicomTag.SequenceName), Text(d, DicomTag.ProtocolName) }
                    .Where(s => s.Length > 0).Distinct().ToArray();
                entries.Add(new(record.RelativePath, Text(d, DicomTag.PatientName), date.Date, date.Source,
                    string.Join(" / ", sequence), Number(d, DicomTag.SeriesNumber), Number(d, DicomTag.InstanceNumber),
                    Text(d, DicomTag.SeriesInstanceUID), sop, record));
            }
            catch (DicomFileException) when (!candidate) { /* Non-DICOM file, including extensionless viewer assets. */ }
            catch (Exception ex) when (ex is DicomFileException or DicomDataException or InvalidDataException)
            {
                throw new IOException("DICOM候補ファイルの解析に失敗しました。正式出力を中止します。", ex);
            }
            if (++count % 50 == 0) progress.Report(new($"DICOM解析中：{count:N0}/{manifest.Files.Count:N0}ファイル"));
        }
        return entries.OrderBy(e => e.StudyDate).ThenBy(e => e.SeriesNumber).ThenBy(e => e.InstanceNumber)
            .ThenBy(e => e.SopInstanceUid, StringComparer.Ordinal).ThenBy(e => e.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string Text(DicomDataset dataset, DicomTag tag) => dataset.GetSingleValueOrDefault(tag, "").Trim();
    private static int Number(DicomDataset dataset, DicomTag tag) =>
        int.TryParse(Text(dataset, tag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;

    private static (DateOnly Date, string Source) ResolveDate(DicomDataset d, DateOnly fallback)
    {
        foreach (var (tag, name) in new[] { (DicomTag.StudyDate, "StudyDate"), (DicomTag.SeriesDate, "SeriesDate"),
                     (DicomTag.AcquisitionDate, "AcquisitionDate"), (DicomTag.ContentDate, "ContentDate") })
            if (DateOnly.TryParseExact(Text(d, tag), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return (date, name);
        return (fallback, "ImportDate");
    }

}
