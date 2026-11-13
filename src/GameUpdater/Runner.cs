using System.Diagnostics;
using Updater.Core;

namespace GameUpdater;

/// <summary>What the runner tells its front end (window or console).</summary>
internal interface IUpdaterUi
{
    void Status(string text);
    void Progress(long done, long total);
    void Log(string text);
}

internal sealed record RunOptions(bool SelfUpdate, bool SelfUpdateOnly, bool Verify, bool ForceSelfUpdate, string[] OriginalArgs);

internal enum RunOutcome { Ready, Restarting, Failed, NotConfigured }

/// <summary>Self-update, then game update. Shared by the window and --headless mode.</summary>
internal sealed class Runner(UpdaterConfig config, string exePath, IUpdaterUi ui)
{
    public string ExeDir => Path.GetDirectoryName(exePath)!;
    public string InstallDir => config.ResolveInstallDir(ExeDir);

    public async Task<RunOutcome> RunAsync(RunOptions o, CancellationToken ct)
    {
        using var http = Fetcher.CreateHttpClient($"BritanniaReborn/{Signing.CurrentVersion()}");
        var fetcher = new Fetcher(http);
        var current = Signing.CurrentVersion();
        ui.Log($"Britannia Reborn launcher {current}");

        if (o.SelfUpdate && config.SelfUpdate && !string.IsNullOrEmpty(config.UpdaterFeedUrl))
        {
            if (SemVer.IsDev(current) && !o.ForceSelfUpdate)
                ui.Log("Development build: skipping self-update.");
            else
            {
                try
                {
                    ui.Status("Checking for updater updates...");
                    var self = new SelfUpdater(fetcher, config.UpdaterKeys());
                    var found = await self.CheckAsync(new Uri(config.UpdaterFeedUrl), current, ct);
                    if (found is { } f)
                    {
                        ui.Status($"Updating the updater to {f.release.Version}...");
                        ui.Log($"New updater {f.release.Version} available ({PatchPublisher.Fmt(f.release.Size)}).");
                        var staged = await self.DownloadAsync(f.release, f.url, exePath, new Progress<(long done, long total)>(p => ui.Progress(p.done, p.total)), ct);
                        ui.Log("New updater downloaded and verified; restarting.");
                        SelfUpdater.StartFinisher(staged, exePath, o.OriginalArgs);
                        return RunOutcome.Restarting;
                    }
                    ui.Log("Updater is up to date.");
                }
                catch (Exception ex) when (ex is UpdaterException or IOException or UnauthorizedAccessException)
                {
                    // A broken feed must never block playing: carry on with the current updater.
                    ui.Log($"Updater self-update check failed: {ex.Message}");
                }
            }
        }
        if (o.SelfUpdateOnly) return RunOutcome.Ready;

        if (string.IsNullOrWhiteSpace(config.ChannelUrl))
        {
            ui.Status("No game channel is configured.");
            ui.Log($"Set \"channelUrl\" in {UpdaterConfig.FileName} next to the updater.");
            return RunOutcome.NotConfigured;
        }

        try
        {
            var client = new PatchClient(fetcher, config.PatchKeys());
            ui.Status("Checking for game updates...");
            var remote = await client.CheckAsync(new Uri(config.ChannelUrl), ct);
            ui.Log($"Latest {remote.Manifest.Product} version: {remote.Manifest.Version}");

            Directory.CreateDirectory(InstallDir);
            // Progress<T> posts reports; one can arrive after the final status below. Drop those.
            var finished = false;
            var progress = new Progress<UpdateProgress>(p =>
            {
                if (Volatile.Read(ref finished)) return;
                ui.Status(p.Message);
                ui.Progress(p.Done, p.Total);
            });
            var plan = await client.PlanAsync(InstallDir, remote, o.Verify, progress, ct);
            if (plan.UpToDate)
            {
                await client.ApplyAsync(plan, progress, ct); // records state after a verify
                Volatile.Write(ref finished, true);
                ui.Status($"Ready to play ({remote.Manifest.Version}).");
                ui.Log("Game is up to date.");
                return RunOutcome.Ready;
            }

            ui.Log($"Updating {plan.InstalledVersion ?? "new install"} -> {remote.Manifest.Version}: {plan.Changed.Count} files to write, " +
                   $"{plan.Deleted.Count} to remove; downloading {PatchPublisher.Fmt(plan.DownloadBytes)}, reusing {PatchPublisher.Fmt(plan.ReusedBytes)} already on disk.");
            var result = await client.ApplyAsync(plan, progress, ct);
            Volatile.Write(ref finished, true);
            ui.Log($"Updated to {result.Version}. Downloaded {PatchPublisher.Fmt(result.BytesDownloaded)}.");
            ui.Status($"Ready to play ({result.Version}).");
            ui.Progress(1, 1);
            return RunOutcome.Ready;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is UpdaterException or IOException or UnauthorizedAccessException)
        {
            ui.Status("Update failed.");
            ui.Log("ERROR: " + ex.Message);
            return RunOutcome.Failed;
        }
    }

    /// <summary>Starts the installed game. Returns false if there is nothing to launch.</summary>
    public bool Launch()
    {
        var state = PatchClient.LoadState(InstallDir);
        if (state?.Manifest.Launch is not { } launch) { ui.Log("The installed version has no launch file."); return false; }
        var exe = PathSafety.Resolve(InstallDir, launch);
        if (!File.Exists(exe)) { ui.Log($"{launch} is missing. Run Verify files."); return false; }
        Process.Start(new ProcessStartInfo(exe, state.Manifest.LaunchArgs ?? "") { WorkingDirectory = InstallDir, UseShellExecute = false });
        return true;
    }
}
