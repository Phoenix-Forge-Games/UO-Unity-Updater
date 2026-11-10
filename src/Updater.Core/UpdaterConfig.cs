namespace Updater.Core;

/// <summary>
/// Settings for GameUpdater.exe. Defaults are compiled in; an optional GameUpdater.json next to the
/// executable overrides them (handy for testing or pointing at another channel).
/// </summary>
public sealed class UpdaterConfig
{
    public const string FileName = "GameUpdater.json";

    /// <summary>Shown in the window title.</summary>
    public string GameName { get; set; } = Defaults.GameName;
    /// <summary>URL of the game channel's channel.json (http, https or file).</summary>
    public string ChannelUrl { get; set; } = Defaults.ChannelUrl;
    /// <summary>URL of the updater's own updater.json.</summary>
    public string UpdaterFeedUrl { get; set; } = Defaults.UpdaterFeedUrl;
    /// <summary>Game folder, relative to the updater executable or absolute.</summary>
    public string InstallDir { get; set; } = "Game";
    public bool SelfUpdate { get; set; } = true;
    /// <summary>Close the updater after starting the game.</summary>
    public bool CloseOnLaunch { get; set; } = true;
    /// <summary>Extra trusted public keys (PEM), added to the compiled-in ones.</summary>
    public List<string> ExtraPatchKeys { get; set; } = [];
    public List<string> ExtraUpdaterKeys { get; set; } = [];

    public static UpdaterConfig Load(string exeDir)
    {
        var p = Path.Combine(exeDir, FileName);
        return File.Exists(p) ? Json.Deserialize<UpdaterConfig>(File.ReadAllBytes(p), FileName) : new UpdaterConfig();
    }

    public string ResolveInstallDir(string exeDir) => Path.GetFullPath(Path.Combine(exeDir, InstallDir));

    public List<string> PatchKeys() => [.. Signing.EmbeddedKeys("patch"), .. ExtraPatchKeys];
    public List<string> UpdaterKeys() => [.. Signing.EmbeddedKeys("updater"), .. ExtraUpdaterKeys];
}

public static class Defaults
{
    public const string GameName = "UO Unity Client";
    // Game patches live on phoenix-server (hybrid hosting). Set once the host's public URL exists.
    public const string ChannelUrl = "";
    // Updater releases come from GitHub Releases; "latest/download" always points at the newest release.
    public const string UpdaterFeedUrl = "https://github.com/Phoenix-Forge-Games/UO-Unity-Updater/releases/latest/download/updater.json";
}
