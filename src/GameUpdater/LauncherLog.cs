using System.Text;
using Updater.Core;

namespace GameUpdater;

/// <summary>
/// The launcher's log file: BritanniaReborn.log next to the exe, or per user when that folder is read-only.
/// Holds everything the window's details box shows, plus full exception details. Writing it never fails the launcher.
/// </summary>
internal static class LauncherLog
{
    public const string FileName = "BritanniaReborn.log";
    private const long MaxBytes = 1 << 20; // past this, the old log moves to BritanniaReborn.old.log
    private static readonly object Lock = new();

    /// <summary>The log file in use, or null before <see cref="Open"/> or when no folder is writable.</summary>
    public static string? Path { get; private set; }

    public static void Open(string exeDir)
    {
        var dir = InstallLocation.IsWritable(exeDir) ? exeDir : InstallLocation.PerUserRoot;
        try
        {
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, FileName);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(path, System.IO.Path.ChangeExtension(path, ".old.log"), overwrite: true);
            Path = path;
            Append($"{Environment.NewLine}===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} launcher {Signing.CurrentVersion()} started =====");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Path = null; }
    }

    /// <summary>One log line, timestamped like the details box.</summary>
    public static void Write(string text) => Append($"[{DateTime.Now:HH:mm:ss}] {text}");

    /// <summary>The full exception (type, message, inner exceptions, stack) for diagnosing a failure.</summary>
    public static void Exception(Exception ex) => Append("Details: " + ex);

    private static void Append(string text)
    {
        if (Path == null) return;
        lock (Lock)
        {
            try { File.AppendAllText(Path, text + Environment.NewLine); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// One line for the player: every message in the exception chain, never blank. An exception without a
    /// message shows its type and error code instead.
    /// </summary>
    public static string Describe(Exception ex)
    {
        if (ex is AggregateException agg && agg.Flatten().InnerExceptions is { Count: > 0 } all)
            return string.Join(" Also: ", all.Select(Describe));
        var sb = new StringBuilder();
        for (var e = ex; e != null; e = e.InnerException)
        {
            var msg = e.Message.Trim();
            if (msg.Length > 0 && sb.ToString().Contains(msg)) continue; // already quoted by the outer message
            if (sb.Length > 0) sb.Append(" Cause: ");
            sb.Append(msg.Length > 0 ? msg : $"{e.GetType().Name} with no message (code 0x{e.HResult:X8}).");
        }
        return sb.ToString();
    }
}
