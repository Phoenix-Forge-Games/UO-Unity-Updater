using Updater.Core;

namespace GameUpdater;

/// <summary>Small window: status, progress bar, log, Play and Verify buttons.</summary>
internal sealed class MainForm : Form, IUpdaterUi
{
    private readonly Runner _runner;
    private readonly RunOptions _opts;
    private readonly bool _autoLaunch;
    private readonly UpdaterConfig _config;
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 10f) };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 18, Maximum = 1000 };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window };
    private readonly Button _play = new() { Text = "Play", Width = 120, Height = 34, Enabled = false };
    private readonly Button _verify = new() { Text = "Verify files", Width = 120, Height = 34, Enabled = false };
    private CancellationTokenSource _cts = new();
    private bool _busy;

    public MainForm(UpdaterConfig config, string exePath, RunOptions opts, bool autoLaunch)
    {
        _config = config;
        _opts = opts;
        _autoLaunch = autoLaunch;
        _runner = new Runner(config, exePath, this);

        Text = $"{config.GameName} Launcher {Signing.CurrentVersion()}";
        ClientSize = new Size(640, 380);
        MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(10);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(_play);
        buttons.Controls.Add(_verify);
        var spacer = new Panel { Dock = DockStyle.Top, Height = 8 };

        Controls.Add(_log);
        Controls.Add(spacer);
        Controls.Add(_bar);
        Controls.Add(_status);
        Controls.Add(buttons);

        _play.Click += (_, _) => Play();
        _verify.Click += async (_, _) => await RunUpdate(_opts with { SelfUpdate = false, Verify = true });
        Shown += async (_, _) => await RunUpdate(_opts);
        FormClosing += (_, e) =>
        {
            if (_busy && MessageBox.Show(this, "An update is running. Stop it and close?", Text, MessageBoxButtons.YesNo) == DialogResult.No)
                e.Cancel = true;
            else _cts.Cancel();
        };
    }

    private async Task RunUpdate(RunOptions o)
    {
        _busy = true;
        _play.Enabled = _verify.Enabled = false;
        _cts = new CancellationTokenSource();
        RunOutcome outcome;
        try { outcome = await _runner.RunAsync(o, _cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log("ERROR: " + ex);
            Status("Update failed.");
            outcome = RunOutcome.Failed;
        }
        finally { _busy = false; }

        if (outcome == RunOutcome.Restarting) { Close(); return; }
        _verify.Enabled = outcome != RunOutcome.NotConfigured;
        _play.Enabled = outcome == RunOutcome.Ready;
        if (outcome == RunOutcome.Ready) { _play.Focus(); if (_autoLaunch) Play(); }
    }

    private void Play()
    {
        if (_runner.Launch() && _config.CloseOnLaunch) Close();
    }

    public void Status(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => Status(text)); return; }
        _status.Text = text;
    }

    public void Progress(long done, long total)
    {
        if (InvokeRequired) { BeginInvoke(() => Progress(done, total)); return; }
        _bar.Value = total <= 0 ? 0 : (int)Math.Clamp(done * 1000 / total, 0, 1000);
    }

    public void Log(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(text)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
    }
}
