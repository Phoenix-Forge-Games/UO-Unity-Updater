using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Updater.Core;

/// <summary>
/// Keeps the updater executable current.
/// <list type="number">
/// <item>Read the signed updater.json from the release feed.</item>
/// <item>If it lists a newer version, download that exe to "&lt;name&gt;.update.exe" next to the running one and check its SHA-256.</item>
/// <item>Start it with <c>--finish-self-update &lt;target&gt; &lt;pid&gt;</c> and exit.</item>
/// <item>The new exe waits for the old process to end, copies itself over the target, and starts the target.</item>
/// <item>The target deletes the leftover ".update.exe" on startup.</item>
/// </list>
/// A copy (not a rename) is used because a running single-file .NET exe can't be renamed.
/// </summary>
public sealed class SelfUpdater(Fetcher fetcher, IReadOnlyCollection<string> trustedKeys)
{
    public const string FinishArg = "--finish-self-update";

    public static string StagedPath(string exePath) =>
        Path.Combine(Path.GetDirectoryName(exePath)!, Path.GetFileNameWithoutExtension(exePath) + ".update.exe");

    public async Task<(UpdaterRelease release, Uri url)?> CheckAsync(Uri feed, string currentVersion, CancellationToken ct = default)
    {
        var json = await fetcher.GetBytesAsync(feed, 1 << 20, ct);
        var sig = await fetcher.GetBytesAsync(new Uri(feed + ".sig"), 4096, ct);
        Signing.Verify(json, Encoding.ASCII.GetString(sig), trustedKeys, "updater.json");
        var rel = Json.Deserialize<UpdaterRelease>(json, "updater.json");
        if (rel.Format != 1 || string.IsNullOrEmpty(rel.Url) || rel.Sha256.Length != 64)
            throw new UpdaterException("updater.json is incomplete.");
        if (SemVer.Compare(rel.Version, currentVersion) <= 0) return null;
        return (rel, new Uri(feed, rel.Url));
    }

    /// <summary>Downloads and verifies the new exe to <see cref="StagedPath"/>. Returns that path.</summary>
    public async Task<string> DownloadAsync(UpdaterRelease rel, Uri url, string exePath, IProgress<(long done, long total)>? progress = null, CancellationToken ct = default)
    {
        var staged = StagedPath(exePath);
        var tmp = staged + ".part";
        await fetcher.WithStreamAsync(url, async s =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long n = 0;
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                var buf = new byte[1 << 16];
                int r;
                while ((r = await s.ReadAsync(buf, ct)) > 0)
                {
                    n += r;
                    if (rel.Size > 0 && n > rel.Size) throw new UpdaterException("Updater download is larger than expected.");
                    hash.AppendData(buf, 0, r);
                    await fs.WriteAsync(buf.AsMemory(0, r), ct);
                    progress?.Report((n, rel.Size));
                }
            }
            if (!string.Equals(GearChunker.Hex(hash.GetHashAndReset()), rel.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                throw new UpdaterException("The downloaded updater failed its hash check. Not installed.");
            }
            return 0;
        }, ct);
        DeleteWithRetry(staged);
        File.Move(tmp, staged);
        return staged;
    }

    /// <summary>Starts the staged exe, which finishes the install once this process has exited.</summary>
    public static void StartFinisher(string staged, string targetExe, IEnumerable<string> passThroughArgs)
    {
        var psi = new ProcessStartInfo(staged) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(targetExe)! };
        psi.ArgumentList.Add(FinishArg);
        psi.ArgumentList.Add(targetExe);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        foreach (var a in passThroughArgs) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    /// <summary>Run by the staged exe: wait for the old process, then copy this exe over the target.</summary>
    public static void Finish(string selfPath, string targetExe, int oldPid)
    {
        try
        {
            using var old = Process.GetProcessById(oldPid);
            old.WaitForExit(30_000);
        }
        catch (ArgumentException) { } // already gone

        Exception? last = null;
        for (int i = 0; i < 60; i++)
        {
            try
            {
                File.Copy(selfPath, targetExe + ".tmp", overwrite: true);
                File.Move(targetExe + ".tmp", targetExe, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(500);
            }
        }
        throw new UpdaterException($"Could not replace {Path.GetFileName(targetExe)}: {last?.Message}", last);
    }

    /// <summary>Deletes files left behind by a self-update (the staged exe, partial downloads).</summary>
    public static void Cleanup(string exePath)
    {
        var staged = StagedPath(exePath);
        DeleteWithRetry(staged);
        DeleteWithRetry(staged + ".part");
        DeleteWithRetry(exePath + ".tmp");
    }

    private static void DeleteWithRetry(string path)
    {
        for (int i = 0; i < 20 && File.Exists(path); i++)
        {
            try { File.Delete(path); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }
}
