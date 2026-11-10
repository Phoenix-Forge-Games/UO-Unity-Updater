using System.IO.Compression;
using System.Security.Cryptography;

namespace Updater.Core;

public enum UpdatePhase { Checking, Scanning, Downloading, Assembling, Applying, Done }

public sealed record UpdateProgress(UpdatePhase Phase, long Done, long Total, string Message);

/// <summary>A verified remote version: signed channel pointer plus its manifest.</summary>
public sealed record RemoteVersion(Uri ChannelUri, ChannelInfo Channel, Manifest Manifest, Uri BlobBase);

public sealed record LocalSource(string Path, long Offset, long Size);

public sealed class UpdatePlan
{
    public required RemoteVersion Remote { get; init; }
    public required string InstallDir { get; init; }
    public string? InstalledVersion { get; init; }
    /// <summary>Files whose content must change (new or modified).</summary>
    public List<FileEntry> Changed { get; } = [];
    /// <summary>Files from the previous version that are no longer in the manifest.</summary>
    public List<string> Deleted { get; } = [];
    public int Unchanged { get; set; }
    /// <summary>Blobs to download, by hash.</summary>
    public Dictionary<string, ChunkEntry> Downloads { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Content already on disk that can be reused, by hash.</summary>
    public Dictionary<string, LocalSource> LocalSources { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Stamps for files found unchanged, written to installed state.</summary>
    public Dictionary<string, FileStamp> UnchangedStamps { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long DownloadBytes => Downloads.Values.Sum(b => b.Stored);
    public long ReusedBytes { get; set; }
    public bool UpToDate => Changed.Count == 0 && Deleted.Count == 0;
}

public sealed record UpdateResult(string Version, int FilesWritten, int FilesDeleted, long BytesDownloaded, long BytesReused);

/// <summary>
/// Brings an install folder to the channel's current version, downloading only blobs (whole small files
/// or chunks of large files) that are not already on disk. Every byte is verified against the signed
/// manifest's SHA-256 before it is used, and files are swapped in only once everything is staged,
/// with rollback if the swap fails.
/// </summary>
public sealed class PatchClient(Fetcher fetcher, IReadOnlyCollection<string> trustedKeys)
{
    public int Parallelism { get; init; } = 4;

    private static string StateDir(string install) => Path.Combine(install, PathSafety.StateDir);
    private static string StatePath(string install) => Path.Combine(StateDir(install), "installed.json");

    public static InstalledState? LoadState(string installDir)
    {
        var p = StatePath(installDir);
        if (!File.Exists(p)) return null;
        try { return Json.Deserialize<InstalledState>(File.ReadAllBytes(p), "installed.json"); }
        catch (UpdaterException) { return null; } // corrupt state: fall back to a full scan
    }

    public async Task<RemoteVersion> CheckAsync(Uri channelUri, CancellationToken ct = default)
    {
        var channelBytes = await fetcher.GetBytesAsync(channelUri, 1 << 20, ct);
        var sig = await fetcher.GetBytesAsync(new Uri(channelUri + ".sig"), 4096, ct);
        Signing.Verify(channelBytes, System.Text.Encoding.ASCII.GetString(sig), trustedKeys, "channel.json");
        var channel = Json.Deserialize<ChannelInfo>(channelBytes, "channel.json");
        if (channel.Format != 1) throw new UpdaterException($"Unsupported channel format {channel.Format}. Update the updater.");

        var manifestUri = new Uri(channelUri, channel.Manifest);
        var raw = await fetcher.GetBytesAsync(manifestUri, 512L << 20, ct);
        var json = manifestUri.AbsolutePath.EndsWith(".gz") ? PatchPublisher.Gunzip(raw) : raw;
        if (!string.Equals(GearChunker.Sha256Hex(json), channel.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdaterException("Manifest hash does not match the signed channel file.");
        var manifest = Json.Deserialize<Manifest>(json, "manifest");
        if (manifest.Format != 1) throw new UpdaterException($"Unsupported manifest format {manifest.Format}. Update the updater.");
        if (manifest.Version != channel.Version) throw new UpdaterException("Manifest version does not match channel.");
        manifest.Chunking.Validate();
        foreach (var f in manifest.Files) PathSafety.Check(f.Path);

        var blobBase = new Uri(channelUri, channel.BlobBase.EndsWith('/') ? channel.BlobBase : channel.BlobBase + "/");
        return new RemoteVersion(channelUri, channel, manifest, blobBase);
    }

    public Task<UpdatePlan> PlanAsync(string installDir, RemoteVersion remote, bool fullVerify, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Plan(installDir, remote, fullVerify, progress, ct), ct);

    private UpdatePlan Plan(string installDir, RemoteVersion remote, bool fullVerify, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        installDir = Path.GetFullPath(installDir);
        var state = LoadState(installDir);
        var m = remote.Manifest;
        var plan = new UpdatePlan { Remote = remote, InstallDir = installDir, InstalledVersion = state?.Manifest.Version };
        var oldEntries = state?.Manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase)
                         ?? new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var newPaths = new HashSet<string>(m.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);

        long scanTotal = m.Files.Sum(f => f.Size), scanned = 0;
        void Report(string msg) => progress?.Report(new UpdateProgress(UpdatePhase.Scanning, scanned, scanTotal, msg));

        // What does the local copy of a path contain? Trust installed state when size+mtime still match.
        (string sha, IReadOnlyList<ChunkEntry>? chunks)? Inspect(string rel, string full, bool wantChunks)
        {
            var fi = new FileInfo(full);
            if (!fi.Exists) return null;
            if (!fullVerify && state != null && oldEntries.TryGetValue(rel, out var old) &&
                state.Stamps.TryGetValue(rel, out var st) && st.Size == fi.Length && st.MtimeTicks == fi.LastWriteTimeUtc.Ticks &&
                old.Size == fi.Length && (!wantChunks || old.Chunks != null) && SameChunking(state.Manifest.Chunking, m.Chunking))
                return (old.Sha256, old.Chunks != null ? old.Blobs() : null);
            Report($"Checking {rel}");
            var scan = GearChunker.Scan(full, m.Chunking, wantChunks, null, ct);
            return (scan.Sha256, scan.Chunks);
        }

        void Index(string full, string sha, long size, IReadOnlyList<ChunkEntry>? chunks)
        {
            plan.LocalSources.TryAdd(sha, new LocalSource(full, 0, size));
            if (chunks != null)
                foreach (var c in chunks) plan.LocalSources.TryAdd(c.Sha256, new LocalSource(full, c.Offset, c.Size));
        }

        foreach (var f in m.Files)
        {
            ct.ThrowIfCancellationRequested();
            var full = PathSafety.Resolve(installDir, f.Path);
            var local = Inspect(f.Path, full, f.Chunks != null);
            if (local is { } l && l.sha == f.Sha256)
            {
                plan.Unchanged++;
                var fi = new FileInfo(full);
                plan.UnchangedStamps[f.Path] = new FileStamp { Size = fi.Length, MtimeTicks = fi.LastWriteTimeUtc.Ticks };
                Index(full, f.Sha256, f.Size, l.chunks); // identical content elsewhere in the build can be copied from here
            }
            else
            {
                plan.Changed.Add(f);
                if (local is { } o) Index(full, o.sha, new FileInfo(full).Length, o.chunks);
            }
            scanned += f.Size;
        }

        // Files of the previous version that are going away: delete them, but reuse their content first (renames).
        foreach (var (rel, old) in oldEntries)
        {
            if (newPaths.Contains(rel)) continue;
            string full;
            try { full = PathSafety.Resolve(installDir, rel); } catch (UpdaterException) { continue; }
            if (!File.Exists(full)) continue;
            plan.Deleted.Add(rel);
            var local = Inspect(rel, full, old.Chunks != null);
            if (local is { } l) Index(full, l.sha, new FileInfo(full).Length, l.chunks);
        }

        // Blobs left from an interrupted run.
        var cache = Path.Combine(StateDir(installDir), "cache");
        if (Directory.Exists(cache))
            foreach (var c in Directory.EnumerateFiles(cache))
            {
                var name = Path.GetFileName(c);
                if (name.Length != 64) { TryDeleteFile(c); continue; }
                using (var fs = File.OpenRead(c))
                    if (GearChunker.Hex(SHA256.HashData(fs)) != name) { fs.Dispose(); TryDeleteFile(c); continue; }
                plan.LocalSources.TryAdd(name, new LocalSource(c, 0, new FileInfo(c).Length));
            }

        foreach (var f in plan.Changed)
            foreach (var b in f.Blobs())
            {
                if (plan.LocalSources.TryGetValue(b.Sha256, out var src) && src.Size == b.Size) plan.ReusedBytes += b.Size;
                else plan.Downloads.TryAdd(b.Sha256, b);
            }

        Report(plan.UpToDate ? "Up to date" : $"{plan.Changed.Count} files to update");
        return plan;
    }

    private static bool SameChunking(ChunkingParams a, ChunkingParams b) =>
        a.Algorithm == b.Algorithm && a.Threshold == b.Threshold && a.Min == b.Min && a.Avg == b.Avg && a.Max == b.Max;

    public async Task<UpdateResult> ApplyAsync(UpdatePlan plan, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        var install = plan.InstallDir;
        var stateDir = StateDir(install);
        var cache = Path.Combine(stateDir, "cache");
        var staging = Path.Combine(stateDir, "staging");
        var backup = Path.Combine(stateDir, "backup");
        Directory.CreateDirectory(cache);
        if (OperatingSystem.IsWindows()) File.SetAttributes(stateDir, File.GetAttributes(stateDir) | FileAttributes.Hidden);

        // A previous run that died mid-swap leaves a backup folder: put those files back first.
        RecoverInterruptedSwap(install, backup);

        if (plan.UpToDate)
        {
            WriteState(plan, new());
            progress?.Report(new UpdateProgress(UpdatePhase.Done, 1, 1, "Up to date"));
            return new UpdateResult(plan.Remote.Manifest.Version, 0, 0, 0, 0);
        }

        CheckDiskSpace(install, plan);
        CheckNotInUse(install, plan);

        // 1. Download missing blobs into the cache, verifying each one.
        long dlTotal = plan.DownloadBytes, dlDone = 0;
        progress?.Report(new UpdateProgress(UpdatePhase.Downloading, 0, dlTotal, $"Downloading {plan.Downloads.Count} pieces ({PatchPublisher.Fmt(dlTotal)})"));
        await Parallel.ForEachAsync(plan.Downloads.Values, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (b, token) =>
        {
            var dest = Path.Combine(cache, b.Sha256);
            await DownloadBlobAsync(plan.Remote.BlobBase, b, dest, token);
            var done = Interlocked.Add(ref dlDone, b.Stored);
            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, done, dlTotal, $"Downloaded {PatchPublisher.Fmt(done)} of {PatchPublisher.Fmt(dlTotal)}"));
        });

        // 2. Build every changed file in staging from cached blobs and reusable local bytes.
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        long asTotal = plan.Changed.Sum(f => f.Size), asDone = 0;
        foreach (var f in plan.Changed)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new UpdateProgress(UpdatePhase.Assembling, asDone, asTotal, $"Building {f.Path}"));
            await Task.Run(() => Assemble(plan, f, PathSafety.Resolve(staging, f.Path), cache, ct), ct);
            asDone += f.Size;
        }

        // 3. Swap staged files in. Originals go to backup until everything has moved, then are deleted.
        progress?.Report(new UpdateProgress(UpdatePhase.Applying, 0, 1, "Installing files"));
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        var done = new List<(string dest, string? backedUp, string? staged)>();
        try
        {
            foreach (var f in plan.Changed)
            {
                var dest = PathSafety.Resolve(install, f.Path);
                var staged = PathSafety.Resolve(staging, f.Path);
                string? bak = null;
                if (File.Exists(dest))
                {
                    bak = PathSafety.Resolve(backup, f.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                    File.Move(dest, bak);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(staged, dest);
                done.Add((dest, bak, staged));
            }
            foreach (var rel in plan.Deleted)
            {
                var dest = PathSafety.Resolve(install, rel);
                if (!File.Exists(dest)) continue;
                var bak = PathSafety.Resolve(backup, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                File.Move(dest, bak);
                done.Add((dest, bak, null));
            }
        }
        catch (Exception ex)
        {
            Rollback(done);
            throw new UpdaterException($"Installing files failed and was rolled back: {ex.Message}", ex);
        }

        WriteState(plan, plan.Changed.Select(f => f.Path).ToList());
        TryDelete(backup); TryDelete(staging); TryDelete(cache);
        RemoveEmptyDirs(install, plan.Deleted);

        progress?.Report(new UpdateProgress(UpdatePhase.Done, 1, 1, $"Updated to {plan.Remote.Manifest.Version}"));
        return new UpdateResult(plan.Remote.Manifest.Version, plan.Changed.Count, plan.Deleted.Count, dlDone, plan.ReusedBytes);
    }

    private async Task DownloadBlobAsync(Uri blobBase, ChunkEntry b, string dest, CancellationToken ct)
    {
        if (File.Exists(dest)) return; // cache entries are hash-checked when the plan is made
        var uri = new Uri(blobBase, PatchPublisher.BlobUrl(b.Sha256, b.Enc));
        var tmp = dest + ".tmp";
        await fetcher.WithStreamAsync(uri, async s =>
        {
            Stream src = b.Enc switch
            {
                BlobEncoding.Gzip => new GZipStream(s, CompressionMode.Decompress, leaveOpen: true),
                BlobEncoding.None => s,
                _ => throw new UpdaterException($"Unknown blob encoding '{b.Enc}'."),
            };
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long n = 0;
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                var buf = new byte[1 << 16];
                int r;
                while ((r = await src.ReadAsync(buf, ct)) > 0)
                {
                    n += r;
                    if (n > b.Size) throw new UpdaterException($"Blob {b.Sha256} is larger than the manifest says.");
                    hash.AppendData(buf, 0, r);
                    await fs.WriteAsync(buf.AsMemory(0, r), ct);
                }
            }
            if (src != s) await src.DisposeAsync();
            var got = GearChunker.Hex(hash.GetHashAndReset());
            if (n != b.Size || got != b.Sha256)
            {
                File.Delete(tmp);
                throw new UpdaterException($"Downloaded data failed verification (blob {b.Sha256[..12]}, got {got[..12]}). The patch host may be corrupt.");
            }
            File.Move(tmp, dest, overwrite: true);
            return 0;
        }, ct);
    }

    private static void Assemble(UpdatePlan plan, FileEntry f, string stagedPath, string cache, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 16];
        using (var outFs = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            outFs.SetLength(f.Size);
            foreach (var b in f.Blobs())
            {
                ct.ThrowIfCancellationRequested();
                var cached = Path.Combine(cache, b.Sha256);
                LocalSource src = File.Exists(cached) ? new LocalSource(cached, 0, b.Size)
                    : plan.LocalSources.TryGetValue(b.Sha256, out var s) ? s
                    : throw new UpdaterException($"Missing data for {f.Path}.");
                using var inFs = new FileStream(src.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                inFs.Position = src.Offset;
                long left = b.Size;
                while (left > 0)
                {
                    int r = inFs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (r <= 0) throw new UpdaterException($"{src.Path} is shorter than expected.");
                    chunkHash.AppendData(buf, 0, r);
                    fileHash.AppendData(buf, 0, r);
                    outFs.Write(buf, 0, r);
                    left -= r;
                }
                if (GearChunker.Hex(chunkHash.GetHashAndReset()) != b.Sha256)
                    throw new UpdaterException($"Local data for {f.Path} changed during the update. Run the update again.");
            }
        }
        if (GearChunker.Hex(fileHash.GetHashAndReset()) != f.Sha256)
            throw new UpdaterException($"{f.Path} failed verification after assembly.");
    }

    private static void Rollback(List<(string dest, string? backedUp, string? staged)> done)
    {
        for (int i = done.Count - 1; i >= 0; i--)
        {
            var (dest, bak, staged) = done[i];
            try
            {
                if (staged != null && File.Exists(dest)) File.Move(dest, staged, overwrite: true);
                if (bak != null && File.Exists(bak)) File.Move(bak, dest, overwrite: true);
            }
            catch { /* best effort; RecoverInterruptedSwap tries again next run */ }
        }
    }

    private static void RecoverInterruptedSwap(string install, string backup)
    {
        if (!Directory.Exists(backup)) return;
        // installed.json is only written after a complete swap, so a leftover backup means the swap never finished.
        foreach (var bak in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
        {
            var rel = PathSafety.ToRel(backup, bak);
            var dest = PathSafety.Resolve(install, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(bak, dest, overwrite: true);
        }
        Directory.Delete(backup, true);
    }

    private static void WriteState(UpdatePlan plan, List<string> written)
    {
        var st = new InstalledState { Manifest = plan.Remote.Manifest, Channel = plan.Remote.Channel.Channel };
        foreach (var (k, v) in plan.UnchangedStamps) st.Stamps[k] = v;
        foreach (var rel in written)
        {
            var fi = new FileInfo(PathSafety.Resolve(plan.InstallDir, rel));
            st.Stamps[rel] = new FileStamp { Size = fi.Length, MtimeTicks = fi.LastWriteTimeUtc.Ticks };
        }
        PatchPublisher.WriteAtomic(StatePath(plan.InstallDir), Json.Serialize(st, indented: false));
    }

    private static void CheckDiskSpace(string install, UpdatePlan plan)
    {
        try
        {
            var need = plan.DownloadBytes + plan.Changed.Sum(f => f.Size) + (64L << 20);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(install))!);
            if (drive.IsReady && drive.AvailableFreeSpace < need)
                throw new UpdaterException($"Not enough disk space: need {PatchPublisher.Fmt(need)}, {PatchPublisher.Fmt(drive.AvailableFreeSpace)} free.");
        }
        catch (ArgumentException) { }
    }

    private static void CheckNotInUse(string install, UpdatePlan plan)
    {
        foreach (var rel in plan.Changed.Select(f => f.Path).Concat(plan.Deleted))
        {
            var p = PathSafety.Resolve(install, rel);
            if (!File.Exists(p)) continue;
            try { using var _ = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new UpdaterException($"{rel} is in use. Close the game and try again."); }
            catch (UnauthorizedAccessException) { throw new UpdaterException($"No permission to write {rel}. Is the game folder read-only?"); }
        }
    }

    private static void RemoveEmptyDirs(string install, List<string> deleted)
    {
        var root = Path.GetFullPath(install).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var rel in deleted)
        {
            var dir = Path.GetDirectoryName(PathSafety.Resolve(install, rel));
            while (dir != null && dir.Length > root.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
    }

    private static void TryDeleteFile(string f)
    {
        try { File.Delete(f); } catch { }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
