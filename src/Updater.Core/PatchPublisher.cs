using System.IO.Compression;
using System.Text;

namespace Updater.Core;

public sealed class PublishOptions
{
    /// <summary>The finished game build folder (what players should end up with).</summary>
    public required string BuildDir { get; init; }
    /// <summary>Local mirror of the patch host. Upload it to the host after publishing (blobs first, channel.json last).</summary>
    public required string HostRoot { get; init; }
    public required string Channel { get; init; }
    public required string Version { get; init; }
    public required string Product { get; init; }
    public required string PrivateKeyPem { get; init; }
    public string? Launch { get; init; }
    public string? LaunchArgs { get; init; }
    public List<string> Exclude { get; init; } = [];
    public ChunkingParams Chunking { get; init; } = new();
    public bool Force { get; init; }
    /// <summary>Only store gzip if it saves at least this fraction (Unity bundles are already compressed).</summary>
    public double MinGzipSaving { get; init; } = 0.05;
}

public sealed record PublishResult(int Files, long TotalBytes, int NewBlobs, long NewBlobBytes, int ReusedBlobs, string ManifestPath, string ChannelPath);

/// <summary>
/// Turns a build folder into a content-addressed patch: one manifest per version plus a shared blob store.
/// Blobs that already exist on the host (from any earlier version) are reused, so each release only adds
/// the files and chunks that actually changed.
/// </summary>
public static class PatchPublisher
{
    /// <summary>Always excluded: updater state and Unity "do not ship" output (burst/il2cpp debug folders).</summary>
    public static readonly string[] DefaultExcludes =
    [
        ".patch/**", "**/*_DoNotShip/**", "**/*DoNotShip*", "**/*_BackUpThisFolder_ButDontShipItWithYourGame/**",
        "**/Thumbs.db", "**/.DS_Store",
    ];

    public static PublishResult Publish(PublishOptions o, Action<string>? log = null)
    {
        log ??= _ => { };
        o.Chunking.Validate();
        if (!Directory.Exists(o.BuildDir)) throw new UpdaterException($"Build folder not found: {o.BuildDir}");
        CheckName(o.Channel, "channel");
        CheckName(o.Version, "version");

        var channelDir = Path.Combine(o.HostRoot, o.Channel);
        var manifestRel = $"manifests/{o.Version}.json.gz";
        var manifestPath = Path.Combine(channelDir, "manifests", o.Version + ".json.gz");
        if (File.Exists(manifestPath) && !o.Force)
            throw new UpdaterException($"Version {o.Version} is already published on channel '{o.Channel}'. Bump the version (or pass --force).");

        var excludes = DefaultExcludes.Concat(o.Exclude).Select(p => new Glob(p)).ToList();
        var blobRoot = Path.Combine(o.HostRoot, "blobs");
        var known = IndexBlobs(blobRoot);
        log($"Blob store has {known.Count} blobs.");

        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var full in Directory.EnumerateFiles(o.BuildDir, "*", SearchOption.AllDirectories))
        {
            var rel = PathSafety.ToRel(o.BuildDir, full);
            if (excludes.Any(g => g.IsMatch(rel))) continue;
            PathSafety.Check(rel);
            if (!seen.Add(rel)) throw new UpdaterException($"Two files differ only by case: {rel}");
            files.Add(rel);
        }
        files.Sort(StringComparer.Ordinal);
        if (o.Launch != null && !seen.Contains(o.Launch))
            throw new UpdaterException($"Launch file '{o.Launch}' is not in the build.");

        var manifest = new Manifest
        {
            Product = o.Product, Version = o.Version, Created = DateTimeOffset.UtcNow,
            Launch = o.Launch, LaunchArgs = o.LaunchArgs, Chunking = o.Chunking,
        };

        int newBlobs = 0, reused = 0; long newBytes = 0, total = 0;
        foreach (var rel in files)
        {
            var full = Path.Combine(o.BuildDir, rel);
            var scan = GearChunker.Scan(full, o.Chunking);
            total += scan.Size;
            var entry = new FileEntry { Path = rel, Size = scan.Size, Sha256 = scan.Sha256 };
            var blobs = scan.Chunks ?? [new ChunkEntry { Sha256 = scan.Sha256, Size = scan.Size, Offset = 0 }];

            foreach (var b in blobs)
            {
                if (known.TryGetValue(b.Sha256, out var k))
                {
                    b.Enc = k.enc; b.Stored = k.stored; reused++;
                    continue;
                }
                var data = ReadRange(full, b.Offset, b.Size);
                if (GearChunker.Sha256Hex(data) != b.Sha256)
                    throw new UpdaterException($"{rel} changed while publishing.");
                var gz = Gzip(data);
                bool useGz = gz.Length <= data.Length * (1 - o.MinGzipSaving);
                var stored = useGz ? gz : data;
                b.Enc = useGz ? BlobEncoding.Gzip : BlobEncoding.None;
                b.Stored = stored.Length;
                WriteAtomic(BlobPath(blobRoot, b.Sha256, b.Enc), stored);
                known[b.Sha256] = (b.Enc, b.Stored);
                newBlobs++; newBytes += stored.Length;
            }

            if (scan.Chunks != null) entry.Chunks = blobs;
            else { entry.Enc = blobs[0].Enc; entry.Stored = blobs[0].Stored; }
            manifest.Files.Add(entry);
        }

        var manifestJson = Json.Serialize(manifest, indented: false);
        WriteAtomic(manifestPath, Gzip(manifestJson));

        var channel = new ChannelInfo
        {
            Product = o.Product, Channel = o.Channel, Version = o.Version, Manifest = manifestRel,
            ManifestSha256 = GearChunker.Sha256Hex(manifestJson), BlobBase = "../blobs/",
            Published = DateTimeOffset.UtcNow, TotalSize = total,
        };
        var channelJson = Json.Serialize(channel);
        var channelPath = Path.Combine(channelDir, "channel.json");
        // Signature first, then the pointer: a client that reads the pair mid-write fails the check and retries later.
        var sig = Encoding.ASCII.GetBytes(Signing.Sign(channelJson, o.PrivateKeyPem));
        var hist = Path.Combine(channelDir, "history", o.Version + ".channel.json");
        WriteAtomic(hist + ".sig", sig);
        WriteAtomic(hist, channelJson);
        WriteAtomic(channelPath + ".sig", sig);
        WriteAtomic(channelPath, channelJson);

        log($"Published {o.Product} {o.Version} to channel '{o.Channel}': {files.Count} files, {Fmt(total)}.");
        log($"New blobs: {newBlobs} ({Fmt(newBytes)} to upload). Reused: {reused}.");
        return new PublishResult(files.Count, total, newBlobs, newBytes, reused, manifestPath, channelPath);
    }

    /// <summary>Points a channel back at an earlier published version (its signed channel.json is kept in history/).</summary>
    public static void Rollback(string hostRoot, string channel, string version, Action<string>? log = null)
    {
        CheckName(channel, "channel"); CheckName(version, "version");
        var channelDir = Path.Combine(hostRoot, channel);
        var hist = Path.Combine(channelDir, "history", version + ".channel.json");
        if (!File.Exists(hist) || !File.Exists(hist + ".sig"))
            throw new UpdaterException($"No published version {version} in channel '{channel}'.");
        var channelPath = Path.Combine(channelDir, "channel.json");
        WriteAtomic(channelPath + ".sig", File.ReadAllBytes(hist + ".sig"));
        WriteAtomic(channelPath, File.ReadAllBytes(hist));
        log?.Invoke($"Channel '{channel}' now points at {version}. Upload {channel}/channel.json and channel.json.sig.");
    }

    public static string BlobPath(string blobRoot, string sha, string enc) =>
        Path.Combine(blobRoot, sha[..2], enc == BlobEncoding.Gzip ? sha + ".gz" : sha);

    public static string BlobUrl(string sha, string enc) =>
        $"{sha[..2]}/{sha}{(enc == BlobEncoding.Gzip ? ".gz" : "")}";

    private static Dictionary<string, (string enc, long stored)> IndexBlobs(string blobRoot)
    {
        var d = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(blobRoot)) return d;
        foreach (var f in Directory.EnumerateFiles(blobRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            if (name.EndsWith(".tmp")) continue;
            bool gz = name.EndsWith(".gz");
            var sha = gz ? name[..^3] : name;
            if (sha.Length != 64) continue;
            d[sha] = (gz ? BlobEncoding.Gzip : BlobEncoding.None, new FileInfo(f).Length);
        }
        return d;
    }

    /// <summary>Deletes blobs no longer referenced by the newest <paramref name="keep"/> versions of any channel.</summary>
    public static (int deleted, long bytes) Prune(string hostRoot, int keep, bool dryRun, Action<string>? log = null)
    {
        log ??= _ => { };
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chanDir in Directory.EnumerateDirectories(hostRoot))
        {
            var mdir = Path.Combine(chanDir, "manifests");
            if (!Directory.Exists(mdir)) continue;
            var ms = Directory.GetFiles(mdir, "*.json.gz")
                .Select(f => (f, v: Path.GetFileName(f)[..^".json.gz".Length]))
                .OrderByDescending(x => x.v, Comparer<string>.Create(SemVer.Compare)).Take(keep);
            foreach (var (f, _) in ms)
            {
                var m = Json.Deserialize<Manifest>(Gunzip(File.ReadAllBytes(f)), f);
                foreach (var fe in m.Files) foreach (var b in fe.Blobs()) referenced.Add(b.Sha256);
            }
        }
        int n = 0; long bytes = 0;
        var blobRoot = Path.Combine(hostRoot, "blobs");
        if (!Directory.Exists(blobRoot)) return (0, 0);
        foreach (var f in Directory.EnumerateFiles(blobRoot, "*", SearchOption.AllDirectories).ToList())
        {
            var name = Path.GetFileName(f);
            var sha = name.EndsWith(".gz") ? name[..^3] : name;
            if (referenced.Contains(sha)) continue;
            n++; bytes += new FileInfo(f).Length;
            if (!dryRun) File.Delete(f);
        }
        log($"{(dryRun ? "Would delete" : "Deleted")} {n} unreferenced blobs ({Fmt(bytes)}).");
        return (n, bytes);
    }

    private static byte[] ReadRange(string path, long offset, long size)
    {
        var data = new byte[size];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = offset;
        fs.ReadExactly(data);
        return data;
    }

    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(data);
        return ms.ToArray();
    }

    public static byte[] Gunzip(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    public static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    private static void CheckName(string s, string what)
    {
        if (string.IsNullOrWhiteSpace(s) || s.IndexOfAny(['/', '\\', ':', ' ', '*', '?']) >= 0 || s.Contains(".."))
            throw new UpdaterException($"Bad {what} name '{s}'.");
    }

    public static string Fmt(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };
}
