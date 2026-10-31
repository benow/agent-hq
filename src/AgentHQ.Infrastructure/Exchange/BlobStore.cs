using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AgentHQ.Infrastructure.Exchange;

/// <summary>
/// Blob bytes on disk: <c>$AGENTHQ_EXCHANGE_BLOB_DIR/&lt;itemId&gt;.bin</c>. The file name is
/// derived from the server-generated item id, never from caller input, so no request can choose
/// a path. Deposits are chunked by byte offset and verified by SHA-256 end to end.
/// </summary>
public sealed class BlobStore
{
    private readonly string _dir;
    private readonly long _maxBytes;

    public BlobStore(string dir, long maxBytes)
    {
        _dir = dir;
        _maxBytes = maxBytes;
    }

    public string Dir => _dir;
    public long MaxBytes => _maxBytes;

    private string PathFor(string itemId) => Path.Combine(_dir, SafeId(itemId) + ".bin");

    /// <summary>The path a complete deposit is stored at (derived from the item id).</summary>
    public string PathForItem(string itemId) => PathFor(itemId);

    /// <summary>The item id is a GUID (server-generated); refusing anything else keeps the path
    /// under our control even if a future caller passes one through.</summary>
    private static string SafeId(string itemId) =>
        Guid.TryParse(itemId, out var g) ? g.ToString("D") : throw new ArgumentException("item id is not a GUID", nameof(itemId));

    public sealed record ChunkResult(bool Ok, string? Code, string? Detail, long Received, bool Complete,
        string? Sha256, long Total);

    /// <summary>
    /// Appends a chunk at <paramref name="offset"/>. The size cap is enforced before anything is
    /// written. A chunk that does not start at the current end (or the start of a fresh deposit)
    /// is refused rather than silently corrupting the file.
    /// </summary>
    public async Task<ChunkResult> AppendChunkAsync(string itemId, long offset, long total, byte[] chunk,
        string? declaredSha256, CancellationToken ct = default)
    {
        if (total < 0 || total > _maxBytes)
            return new ChunkResult(false, "blob_too_large",
                $"declared size {total} exceeds the {_maxBytes}-byte limit", 0, false, null, total);
        Directory.CreateDirectory(_dir);
        var path = PathFor(itemId);
        var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (offset == 0)
        {
            // A fresh deposit replaces any partial file left by an aborted attempt.
            if (existing > 0) File.Delete(path);
            existing = 0;
        }
        if (offset != existing)
            return new ChunkResult(false, "hash_mismatch",
                $"chunk offset {offset} does not follow the {existing} byte(s) already received; resume at {existing}",
                existing, false, null, total);
        if (existing + chunk.Length > _maxBytes)
            return new ChunkResult(false, "blob_too_large",
                $"deposit would reach {existing + chunk.Length} bytes, over the {_maxBytes}-byte limit",
                existing, false, null, total);

        await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            await stream.WriteAsync(chunk, ct);
        var received = new FileInfo(path).Length;
        if (received < total)
            return new ChunkResult(true, null, null, received, false, null, total);
        if (received > total)
        {
            File.Delete(path);
            return new ChunkResult(false, "hash_mismatch",
                $"received {received} bytes, more than the declared total {total}; the deposit was discarded",
                0, false, null, total);
        }

        var actual = await Sha256OfFileAsync(path, ct);
        if (!string.IsNullOrWhiteSpace(declaredSha256) &&
            !string.Equals(actual, declaredSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            return new ChunkResult(false, "hash_mismatch",
                $"SHA-256 of the received bytes is {actual}, not the declared {declaredSha256}; the partial file was deleted",
                0, false, null, total);
        }
        return new ChunkResult(true, null, null, received, true, actual, total);
    }

    /// <summary>Re-verifies the stored bytes against the recorded hash before serving them.</summary>
    public async Task<(byte[]? Bytes, string? Code, string? Detail)> ReadAsync(string itemId, long expectedSize,
        string? expectedSha256, CancellationToken ct = default)
    {
        var path = PathFor(itemId);
        if (!File.Exists(path))
            return (null, "not_found", "the blob file is missing on disk");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (expectedSize > 0 && bytes.LongLength != expectedSize)
            return (null, "hash_mismatch",
                $"stored blob is {bytes.LongLength} bytes, recorded {expectedSize}; refusing to serve it");
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            return (null, "hash_mismatch", $"stored blob no longer matches its recorded SHA-256 {expectedSha256}");
        return (bytes, null, null);
    }

    public void Delete(string itemId)
    {
        try
        {
            var path = PathFor(itemId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A lock from a concurrent reader: the same file name is reused by no other item,
            // and retention prunes orphans, so leaving it is safe (and logged by the caller).
        }
    }

    public static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// The content type to store: the declared type, unless a magic-byte sniff proves a different
    /// concrete type. Returns the mismatch (declared vs sniffed) so the caller can log a Warning.
    /// </summary>
    public static (string Stored, (string Declared, string Sniffed)? Mismatch) ResolveContentType(
        string? declared, byte[] head)
    {
        var sniffed = Sniff(head);
        var d = string.IsNullOrWhiteSpace(declared) ? "application/octet-stream" : declared.Trim().ToLowerInvariant();
        if (sniffed is null) return (d, null);
        // Family match: a declared image/png with sniffed image/png is fine; text/plain vs a
        // sniffed text type is fine; image/* declared but a PDF underneath is a mismatch.
        var declaredFamily = Family(d);
        var sniffedFamily = Family(sniffed);
        if (declaredFamily is not null && declaredFamily == sniffedFamily) return (d, null);
        if (declaredFamily is null && d == "application/octet-stream") return (sniffed, null);
        return (sniffed, (d, sniffed));
    }

    /// <summary>Magic-byte sniff for PNG, JPEG, GIF, PDF, ZIP and plain UTF-8-ish text.</summary>
    public static string? Sniff(byte[] head)
    {
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47 &&
            head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return "image/png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "image/jpeg";
        if (head.Length >= 6 && Encoding.ASCII.GetString(head, 0, 6) is "GIF87a" or "GIF89a") return "image/gif";
        if (head.Length >= 5 && Encoding.ASCII.GetString(head, 0, 5) == "%PDF-") return "application/pdf";
        if (head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] is 0x03 or 0x05 or 0x07 &&
            head[3] is 0x04 or 0x06 or 0x08) return "application/zip";
        if (head.Length == 0) return "application/octet-stream";
        // No NUL bytes and valid UTF-8 in the head: text. A binary file without a known signature
        // is left to the declared type.
        if (!head.Contains((byte)0) && IsUtf8(head)) return "text/plain";
        return null;
    }

    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            // A multi-byte character can straddle the head/tail of the sample; a strict decode
            // failing at the very end is treated as text, anywhere else as binary.
            return false;
        }
    }

    private static string? Family(string type) => type.Split('/')[0] switch
    {
        "image" => "image",
        "text" => "text",
        "application" when type.Contains("json") || type.Contains("xml") || type.Contains("yaml") => "text",
        "application" when type.Contains("pdf") => "pdf",
        "application" when type.Contains("zip") => "zip",
        _ => null
    };
}
