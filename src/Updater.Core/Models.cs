using System.Text.Json;
using System.Text.Json.Serialization;

namespace Updater.Core;

/// <summary>Encoding of a blob as stored on the host.</summary>
public static class BlobEncoding
{
    public const string None = "none";
    public const string Gzip = "gzip";
}

/// <summary>Content-defined chunking parameters. Publisher and client must use identical values.</summary>
public sealed class ChunkingParams
{
    public string Algorithm { get; set; } = GearChunker.AlgorithmName;
    /// <summary>Files at or above this size are split into chunks; smaller files are one blob.</summary>
    public long Threshold { get; set; } = 8 * 1024 * 1024;
    public int Min { get; set; } = 256 * 1024;
    public int Avg { get; set; } = 1024 * 1024;
    public int Max { get; set; } = 4 * 1024 * 1024;

    public void Validate()
    {
        if (Algorithm != GearChunker.AlgorithmName)
            throw new UpdaterException($"Unsupported chunking algorithm '{Algorithm}'.");
        if (Min <= 0 || Avg <= Min || Max <= Avg || (Avg & (Avg - 1)) != 0)
            throw new UpdaterException("Invalid chunking parameters (need 0 < min < avg < max, avg a power of two).");
    }
}

/// <summary>One stored blob: a whole small file, or one chunk of a large file.</summary>
public sealed class ChunkEntry
{
    /// <summary>SHA-256 (hex, lower case) of the uncompressed bytes. This is also the blob's name.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>Uncompressed size in bytes.</summary>
    public long Size { get; set; }
    /// <summary>"none" or "gzip".</summary>
    public string Enc { get; set; } = BlobEncoding.None;
    /// <summary>Size of the blob as stored on the host (after compression).</summary>
    public long Stored { get; set; }

    [JsonIgnore] public long Offset { get; set; }
}

public sealed class FileEntry
{
    /// <summary>Relative path with forward slashes.</summary>
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";

    /// <summary>Present only for chunked (large) files. Small files are a single blob named by <see cref="Sha256"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChunkEntry>? Chunks { get; set; }

    /// <summary>Single-blob files only: blob encoding and stored size.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Enc { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Stored { get; set; }

    /// <summary>Every blob of this file, in order, with offsets filled in.</summary>
    public IReadOnlyList<ChunkEntry> Blobs()
    {
        if (Chunks is { Count: > 0 })
        {
            long off = 0;
            foreach (var c in Chunks) { c.Offset = off; off += c.Size; }
            return Chunks;
        }
        return [new ChunkEntry { Sha256 = Sha256, Size = Size, Enc = Enc ?? BlobEncoding.None, Stored = Stored ?? Size, Offset = 0 }];
    }
}

/// <summary>The full file list of one game version.</summary>
public sealed class Manifest
{
    public int Format { get; set; } = 1;
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    /// <summary>Executable to start, relative to the install folder.</summary>
    public string? Launch { get; set; }
    public string? LaunchArgs { get; set; }
    public ChunkingParams Chunking { get; set; } = new();
    public List<FileEntry> Files { get; set; } = [];
}

/// <summary>Small signed pointer to the current manifest of a channel. This is what clients poll.</summary>
public sealed class ChannelInfo
{
    public int Format { get; set; } = 1;
    public string Product { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Manifest URL, relative to channel.json.</summary>
    public string Manifest { get; set; } = "";
    /// <summary>SHA-256 of the manifest's uncompressed JSON bytes.</summary>
    public string ManifestSha256 { get; set; } = "";
    /// <summary>Blob store URL, relative to channel.json (e.g. "../blobs/").</summary>
    public string BlobBase { get; set; } = "../blobs/";
    public DateTimeOffset Published { get; set; }
    /// <summary>Total bytes of the version, and bytes added since the previous one, for display.</summary>
    public long TotalSize { get; set; }
}

/// <summary>Signed description of the latest updater release (updater.json).</summary>
public sealed class UpdaterRelease
{
    public int Format { get; set; } = 1;
    public string Version { get; set; } = "";
    /// <summary>Absolute URL, or relative to updater.json.</summary>
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string? Notes { get; set; }
}

/// <summary>What the client last installed, kept in &lt;install&gt;/.patch/installed.json.</summary>
public sealed class InstalledState
{
    public Manifest Manifest { get; set; } = new();
    public string Channel { get; set; } = "";
    /// <summary>File stamps at install time, so unchanged files need not be re-hashed.</summary>
    public Dictionary<string, FileStamp> Stamps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FileStamp
{
    public long Size { get; set; }
    public long MtimeTicks { get; set; }
}

public class UpdaterException(string message, Exception? inner = null) : Exception(message, inner);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static byte[] Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.SerializeToUtf8Bytes(value, indented ? Options : Compact);

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8, string what)
    {
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) utf8 = utf8[3..]; // files saved by Notepad / PowerShell 5
        try
        {
            return JsonSerializer.Deserialize<T>(utf8, Options) ?? throw new UpdaterException($"{what} is empty.");
        }
        catch (JsonException ex)
        {
            throw new UpdaterException($"{what} is not valid JSON: {ex.Message}", ex);
        }
    }
}
