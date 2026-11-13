using System.Runtime.InteropServices;
using Updater.Core;

namespace GameUpdater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var exeDir = Path.GetDirectoryName(exePath)!;
        bool headless = args.Contains("--headless");
        string? logFile = ArgValue(args, "--log");

        // Second half of a self-update: this process is the new exe, staged next to the old one.
        if (args.Length >= 3 && args[0] == SelfUpdater.FinishArg)
            return FinishSelfUpdate(exePath, target: args[1], oldPid: int.Parse(args[2]), passThrough: args[3..], headless, logFile);

        SelfUpdater.Cleanup(exePath);
        var opts = new RunOptions(
            SelfUpdate: !args.Contains("--no-self-update"),
            SelfUpdateOnly: args.Contains("--self-update-only"),
            Verify: args.Contains("--verify"),
            ForceSelfUpdate: args.Contains("--force-self-update"),
            OriginalArgs: args);
        bool launch = args.Contains("--launch");

        if (headless || args.Contains("--version")) AttachConsole(-1);
        UpdaterConfig config;
        try { config = UpdaterConfig.Load(exeDir); }
        catch (UpdaterException ex)
        {
            if (headless)
            {
                AttachConsole(-1);
                Console.Error.WriteLine("error: " + ex.Message);
                if (logFile != null) File.AppendAllText(logFile, "ERROR: " + ex.Message + Environment.NewLine);
            }
            else MessageBox.Show(ex.Message, Defaults.GameName + " Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        if (args.Contains("--version"))
        {
            AttachConsole(-1);
            Console.WriteLine(Signing.CurrentVersion());
            if (logFile != null) File.WriteAllText(logFile, Signing.CurrentVersion());
            return 0;
        }

        if (headless)
        {
            AttachConsole(-1);
            var ui = new ConsoleUi(logFile);
            var runner = new Runner(config, exePath, ui);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var outcome = runner.RunAsync(opts, cts.Token).GetAwaiter().GetResult();
            if (outcome == RunOutcome.Ready && launch && !opts.SelfUpdateOnly) runner.Launch();
            return outcome switch { RunOutcome.Ready or RunOutcome.Restarting => 0, RunOutcome.NotConfigured => 3, _ => 1 };
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(config, exePath, opts, launch));
        return 0;
    }

    private static int FinishSelfUpdate(string selfPath, string target, int oldPid, string[] passThrough, bool headless, string? logFile)
    {
        string? error = null;
        try { SelfUpdater.Finish(selfPath, target, oldPid); }
        catch (Exception ex) when (ex is UpdaterException or IOException or UnauthorizedAccessException) { error = ex.Message; }

        if (error != null)
        {
            if (logFile != null) File.AppendAllText(logFile, "Self-update failed: " + error + Environment.NewLine);
            if (!headless) MessageBox.Show("Updating the updater failed:\n" + error + "\n\nThe previous version will start instead.", Defaults.GameName + " Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        // Start the (new or, on failure, old) updater; it deletes this staged copy on startup.
        var psi = new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        foreach (var a in passThrough) psi.ArgumentList.Add(a);
        if (error != null) psi.ArgumentList.Add("--no-self-update"); // don't loop on a failing update
        System.Diagnostics.Process.Start(psi);
        return error == null ? 0 : 1;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // WinExe has no console; attach to the parent's so --headless output shows in a terminal.
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}

internal sealed class ConsoleUi(string? logFile) : IUpdaterUi
{
    private string _last = "";
    private readonly object _lock = new();

    public void Status(string text)
    {
        if (text == _last) return;
        _last = text;
        if (text.StartsWith("Downloaded ") || text.StartsWith("Building ") || text.StartsWith("Checking ")) return; // too chatty
        Log(text);
    }

    public void Progress(long done, long total) { }

    public void Log(string text)
    {
        lock (_lock)
        {
            Console.WriteLine(text);
            if (logFile != null) File.AppendAllText(logFile, text + Environment.NewLine);
        }
    }
}
