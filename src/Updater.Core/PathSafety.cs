namespace Updater.Core;

public static class PathSafety
{
    /// <summary>Folder inside the install that holds updater state. Never part of a manifest.</summary>
    public const string StateDir = ".patch";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>
    /// Validates a manifest path and maps it under <paramref name="root"/>. Rejects anything that could
    /// escape the install folder or touch updater state, so a bad manifest can't write elsewhere.
    /// </summary>
    public static string Resolve(string root, string relPath)
    {
        Check(relPath);
        var full = Path.GetFullPath(Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new UpdaterException($"Manifest path escapes the install folder: {relPath}");
        return full;
    }

    public static void Check(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath) || relPath.Length > 400)
            throw new UpdaterException($"Bad manifest path: '{relPath}'");
        if (relPath.Contains('\\') || relPath.Contains(':') || relPath.StartsWith('/') || relPath.Contains('\0'))
            throw new UpdaterException($"Bad manifest path: '{relPath}'");
        foreach (var part in relPath.Split('/'))
        {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith('.') || part.EndsWith(' '))
                throw new UpdaterException($"Bad manifest path: '{relPath}'");
            if (Reserved.Contains(Path.GetFileNameWithoutExtension(part)))
                throw new UpdaterException($"Bad manifest path: '{relPath}'");
            if (part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
                throw new UpdaterException($"Bad manifest path: '{relPath}'");
        }
        if (relPath.Split('/')[0].Equals(StateDir, StringComparison.OrdinalIgnoreCase))
            throw new UpdaterException($"Manifest may not touch {StateDir}: '{relPath}'");
    }

    public static string ToRel(string root, string full) =>
        Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
}
