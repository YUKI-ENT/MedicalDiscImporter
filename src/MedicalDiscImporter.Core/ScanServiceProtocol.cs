using System.Buffers.Binary;
using System.Text.Json;

namespace MedicalDiscImporter.Core;

// Length-prefixed, bounded messages; file bytes follow in manifest order. No commands or source paths.
public sealed record ScanServiceRequest(int Version, string Operation, int SignatureDays = 7, TreeSnapshot? Manifest = null);

public static class ScanServiceProtocol
{
    public const string PipeName = "MedicalDiscImporter.Defender.v2";
    public const int Version = 2;
    public const int MaxMessageBytes = 32 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaxMessageBytes) throw new IOException("検査依頼のサイズ上限を超えました。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaxMessageBytes) throw new IOException("不正な検査メッセージです。");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new IOException("検査メッセージが空です。");
    }

    // FlushAsync does not wait for a named-pipe client to consume buffered data. Require an ACK
    // before DisconnectNamedPipe, which otherwise discards a response that has not yet been read.
    public static async Task WriteResponseAsync(Stream stream, ScanResult result, CancellationToken token)
    {
        await WriteAsync(stream, result, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var ack = new byte[1];
        await stream.ReadExactlyAsync(ack, timeout.Token);
        if (ack[0] != 6) throw new IOException("検査結果の受信確認が不正です。");
    }

    public static async Task<ScanResult> ReadResponseAsync(Stream stream, CancellationToken token)
    {
        var result = await ReadAsync<ScanResult>(stream, token);
        await stream.WriteAsync(new byte[] { 6 }, token);
        await stream.FlushAsync(token);
        return result;
    }

    public static void Validate(ScanServiceRequest request, string root)
    {
        if (request.Version != Version || request.Operation is not ("Ping" or "Scan"))
            throw new IOException("検査サービスのバージョンまたは操作が不正です。");
        if (request.Operation == "Ping")
        {
            if (request.Manifest != null) throw new IOException("不正な動作確認です。");
            return;
        }
        if (request.SignatureDays is < 1 or > 30 || request.Manifest is not { } tree ||
            tree.Files == null || tree.Directories == null || tree.Files.Count + tree.Directories.Count > SafeFiles.MaxEntries)
            throw new IOException("検査依頼の上限または形式が不正です。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void CheckName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Split('\\').Any(p =>
                p.Length == 0 || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                IsDeviceName(p))) throw new IOException("検査依頼のファイル名が不正です。");
            var full = Path.GetFullPath(Path.Combine(root, name));
            if (SafeFiles.SafeRelative(root, full) != name || !names.Add(name))
                throw new IOException("検査依頼に重複または不正なパスがあります。");
        }
        foreach (var dir in tree.Directories) CheckName(dir);
        long total = 0;
        foreach (var file in tree.Files)
        {
            if (file == null) throw new IOException("不正なファイル項目です。");
            CheckName(file.RelativePath);
            if (file.Length < 0 || file.Length > SafeFiles.MaxBytes - total || file.Sha256 == null ||
                file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
                throw new IOException("検査依頼の容量またはハッシュが不正です。");
            total += file.Length;
        }
        var directories = tree.Directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            for (var parent = Path.GetDirectoryName(name); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (!directories.Contains(parent)) throw new IOException("検査依頼の親フォルダがありません。");
    }

    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
             (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'));
    }

    public static async Task CopyExactlyAsync(Stream input, Stream output, long length, CancellationToken token)
    {
        var buffer = new byte[1024 * 1024];
        while (length > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), token);
            if (read == 0) throw new EndOfStreamException("検査データの転送が途切れました。");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            length -= read;
        }
    }
}
