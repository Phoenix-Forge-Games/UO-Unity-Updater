using System.Security.Cryptography;
using System.Text;

namespace Updater.Core;

/// <summary>
/// Where the game is installed, and remembering the player's choice.
/// </summary>
/// <remarks>
/// Order: <c>--install-dir</c> argument, then a saved choice, then <c>installDir</c> from BritanniaReborn.json,
/// then "Game" next to the launcher. The choice is saved in <see cref="FileName"/> next to the launcher when
/// that folder is writable, else per user under %LOCALAPPDATA%\Britannia Reborn (keyed by the launcher's
/// folder, so two copies of the launcher don't share one choice).
/// </remarks>
public static class InstallLocation
{
    public const string FileName = "BritanniaReborn.install.json";
    /// <summary>Folder added under a picked folder that already holds other things.</summary>
    public const string DefaultFolderName = "Britannia Reborn";

    public sealed record Resolved(string Dir, bool Chosen, string Source);

    public sealed class Saved
    {
        public string InstallDir { get; set; } = "";
    }

    /// <summary>Per-user folder for the choice when the launcher's folder is read-only. Settable for tests.</summary>
    public static string PerUserRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Defaults.GameName);

    public static Resolved Resolve(UpdaterConfig config, string exeDir, string? argDir)
    {
        if (!string.IsNullOrWhiteSpace(argDir))
            return new(Path.GetFullPath(Path.Combine(exeDir, argDir)), true, "--install-dir");
        if (LoadSaved(exeDir) is { } saved)
            return new(saved, true, "saved choice");
        var fromConfig = config.ResolveInstallDir(exeDir);
        if (config.InstallDir != null) return new(fromConfig, true, UpdaterConfig.FileName);
        // Launchers before 0.5 installed to "Game" without asking: an install already there counts as chosen.
        return new(fromConfig, HasInstall(fromConfig), "default");
    }

    public static bool HasInstall(string dir) => File.Exists(Path.Combine(dir, PathSafety.StateDir, "installed.json"));

    public static string? LoadSaved(string exeDir)
    {
        try
        {
            var local = Path.Combine(exeDir, FileName);
            if (File.Exists(local))
                return Usable(Json.Deserialize<Saved>(File.ReadAllBytes(local), FileName).InstallDir, exeDir);
            var user = PerUserFile(exeDir);
            if (File.Exists(user))
                return Usable(Json.Deserialize<Saved>(File.ReadAllBytes(user), FileName).InstallDir, exeDir);
        }
        catch (Exception ex) when (ex is UpdaterException or IOException or UnauthorizedAccessException) { }
        return null; // unreadable: ask again rather than fail
    }

    private static string? Usable(string dir, string exeDir) =>
        string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(Path.Combine(exeDir, dir));

    /// <summary>Saves the choice; returns the file written.</summary>
    public static string Save(string exeDir, string installDir)
    {
        var bytes = Json.Serialize(new Saved { InstallDir = Path.GetFullPath(installDir) });
        var local = Path.Combine(exeDir, FileName);
        if (IsWritable(exeDir))
        {
            try
            {
                PatchPublisher.WriteAtomic(local, bytes);
                TryDeleteFile(PerUserFile(exeDir)); // one place only, so the two can't disagree
                return local;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var user = PerUserFile(exeDir);
        Directory.CreateDirectory(Path.GetDirectoryName(user)!);
        PatchPublisher.WriteAtomic(user, bytes);
        return user;
    }

    public static string PerUserFile(string exeDir)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exeDir).TrimEnd('\\', '/').ToUpperInvariant())))[..16];
        return Path.Combine(PerUserRoot, $"install-{key}.json");
    }

    /// <summary>
    /// The folder to install into when the player picks <paramref name="picked"/>: the folder itself when it is
    /// empty, missing, or already holds this game; otherwise a "Britannia Reborn" folder inside it.
    /// </summary>
    public static string TargetFor(string picked)
    {
        picked = Path.GetFullPath(picked);
        if (!Directory.Exists(picked) || HasInstall(picked) || !Directory.EnumerateFileSystemEntries(picked).Any())
            return picked;
        return Path.Combine(picked, DefaultFolderName);
    }

    /// <summary>A reason the folder can't hold the game, or null when it can.</summary>
    public static string? Problem(string dir, string exeDir)
    {
        dir = Path.GetFullPath(dir);
        var root = Path.GetPathRoot(dir);
        if (string.Equals(dir.TrimEnd('\\'), root?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "Pick a folder, not the root of a drive.";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && IsSameOrInside(dir, windows)) return "The Windows folder can't hold the game.";
        if (IsSameOrInside(Path.GetFullPath(exeDir), dir))
            return "That folder holds the launcher itself. Pick a folder inside or beside it.";
        // Nearest existing folder must be writable (the rest gets created).
        var probe = dir;
        while (!Directory.Exists(probe))
        {
            probe = Path.GetDirectoryName(probe);
            if (probe == null) return "That drive isn't available.";
        }
        if (!IsWritable(probe)) return "Windows won't let the launcher write there. Pick another folder (not Program Files).";
        return null;
    }

    public static bool IsSameOrInside(string path, string folder)
    {
        path = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";
        folder = Path.GetFullPath(folder).TrimEnd('\\', '/') + "\\";
        return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    /// <summary>Free bytes on the drive that would hold <paramref name="dir"/>, or null if unknown.</summary>
    public static long? FreeSpace(string dir)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Disk space a fresh install of <paramref name="m"/> needs: the files, plus downloads cached while it builds them.</summary>
    public static long SpaceNeeded(Manifest m)
    {
        var blobs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in m.Files)
            foreach (var b in f.Blobs()) blobs[b.Sha256] = b.Stored;
        return m.Files.Sum(f => f.Size) + blobs.Values.Sum() + (64L << 20); // same margin as the update itself
    }

    private static void TryDeleteFile(string f)
    {
        try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Moves an install to a new folder without ever leaving a half-moved game: the new copy appears only once
/// complete, the choice is saved, and only then is the old copy removed. Any failure puts everything back.
/// </summary>
public static class InstallMover
{
    private const string ParkedSuffix = ".moving";
    private const string PartialSuffix = ".partial";

    /// <summary>
    /// Moves <paramref name="from"/> to <paramref name="to"/>. <paramref name="commit"/> runs once the game is
    /// complete at the new place (save the choice there); if it throws, the move is undone.
    /// Returns a warning when the old copy could not be fully removed afterwards, else null.
    /// </summary>
    public static async Task<string?> MoveAsync(string from, string to, Action commit, IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        from = Path.GetFullPath(from).TrimEnd('\\');
        to = Path.GetFullPath(to).TrimEnd('\\');
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) { commit(); return null; }
        if (InstallLocation.IsSameOrInside(to, from) || InstallLocation.IsSameOrInside(from, to))
            throw new UpdaterException("The new folder can't be inside the old one (or the other way round).");

        if (!Directory.Exists(from) || !Directory.EnumerateFileSystemEntries(from).Any())
        {
            Directory.CreateDirectory(to);
            commit();
            return null;
        }
        if (Directory.Exists(to))
        {
            if (Directory.EnumerateFileSystemEntries(to).Any()) throw new UpdaterException($"{to} is not empty.");
            Directory.Delete(to);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        // Same drive: one rename, nothing to copy.
        if (SameVolume(from, to))
        {
            var renamed = false;
            try { Directory.Move(from, to); renamed = true; }
            catch (IOException ex) when (IsCrossDevice(ex)) { } // a junction or mount point after all: copy instead
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw InUse(ex); }
            if (renamed)
            {
                try { commit(); }
                catch { Directory.Move(to, from); throw; }
                progress?.Report((1, 1));
                return null;
            }
        }

        // Other drive: park the old folder (fails now, not halfway, if the game is running), copy, then swap in.
        var parked = from + ParkedSuffix;
        var partial = to + PartialSuffix;
        if (Directory.Exists(parked)) throw new UpdaterException($"{parked} is in the way. Delete or rename it and try again.");
        var need = DirSize(from);
        if (InstallLocation.FreeSpace(to) is { } free && free < need + (64L << 20))
            throw new UpdaterException($"Not enough disk space: need {PatchPublisher.Fmt(need)}, {PatchPublisher.Fmt(free)} free.");
        try { Directory.Move(from, parked); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw InUse(ex); }
        try
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, true);
            await Task.Run(() => CopyTree(parked, partial, need, progress, ct), ct);
            Directory.Move(partial, to);
            try { commit(); }
            catch { TryDelete(to); throw; }
        }
        catch
        {
            TryDelete(partial);
            Directory.Move(parked, from);
            throw;
        }
        try { Directory.Delete(parked, true); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The game now runs from {to}, but the old copy could not be fully removed ({ex.Message}). You can delete {parked}.";
        }
    }

    /// <summary>Undoes a move that was cut off (power loss, killed process) before it finished. Call before using an install folder.</summary>
    public static void Recover(string dir)
    {
        dir = Path.GetFullPath(dir).TrimEnd('\\');
        var parked = dir + ParkedSuffix;
        try
        {
            if (!Directory.Exists(dir) && Directory.Exists(parked)) Directory.Move(parked, dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static UpdaterException InUse(Exception ex) =>
        new($"The game folder is in use (is the game running?). Close it and try again. ({ex.Message})", ex);

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsCrossDevice(IOException ex) => (ex.HResult & 0xFFFF) == 17; // ERROR_NOT_SAME_DEVICE

    private static long DirSize(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    private static void CopyTree(string src, string dst, long total, IProgress<(long, long)>? progress, CancellationToken ct)
    {
        long done = 0;
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories).Prepend(src))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, dir));
            Directory.CreateDirectory(target);
            new DirectoryInfo(target).Attributes = new DirectoryInfo(dir).Attributes; // keeps .patch hidden
        }
        var buf = new byte[1 << 20];
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            using (var i = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var o = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                int r;
                while ((r = i.Read(buf)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    o.Write(buf, 0, r);
                    done += r;
                    progress?.Report((done, total));
                }
            }
            // Same timestamps, so the updater's size+time stamps still match and nothing is re-hashed.
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
            File.SetAttributes(target, File.GetAttributes(file));
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
