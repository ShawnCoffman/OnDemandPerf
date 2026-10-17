using System.Diagnostics;
using OnDemandPerf.Core;

namespace OnDemandPerf;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly Icon trayIcon = PerfTheme.LoadIcon();
    private readonly TestWindow window = new();
    private readonly Control marshal = new();
    private readonly RegisteredWaitHandle showRegistration;

    public TrayContext(EventWaitHandle showSignal)
    {
        _ = marshal.Handle; // force handle creation on the UI thread so other threads can post work to it
        showRegistration = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) =>
        {
            try { marshal.BeginInvoke(new Action(() => ShowWindow(fromTrayIcon: false))); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
        }, null, Timeout.Infinite, false);
        var menu = new ContextMenuStrip
        {
            BackColor = PerfTheme.Panel, ForeColor = PerfTheme.Text,
            Renderer = new ToolStripProfessionalRenderer(new PerfMenuColors()),
            ShowImageMargin = false, Padding = new Padding(5)
        };
        var run = menu.Items.Add("Run Perf Test", null, async (_, _) => { ShowWindow(fromTrayIcon: true); await window.RunAsync(); });
        menu.Items.Add("Show Status", null, (_, _) => ShowWindow(fromTrayIcon: true));
        menu.Items.Add("Open Results", null, (_, _) => OpenResults(true));
        menu.Items.Add("Open Results Folder", null, (_, _) => OpenResults(false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => { menu.Enabled = false; await window.StopAsync(); ExitThread(); });
        icon = new NotifyIcon { Icon = trayIcon, Text = "OnDemand Perf — ready", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(fromTrayIcon: true); };
        window.RunCompleted += (_, summary) =>
        {
            // The panel can be hidden while a test runs, so tell the user how it ended, including failures.
            if (!window.Visible) icon.ShowBalloonTip(6000, "OnDemand Perf", summary.Length > 250 ? summary[..250] : summary, ToolTipIcon.Info);
        };
        window.StateChanged += (_, _) =>
        {
            run.Enabled = !window.IsRunning;
            var text = "OnDemand Perf — " + window.StatusText;
            icon.Text = text.Length > 63 ? text[..63] : text;
        };
    }

    internal void ShowWindow(bool fromTrayIcon) => window.ShowNearTray(fromTrayIcon);

    internal static void OpenResults(bool latest) =>
        Open(latest ? Path.Combine(ResultStore.DefaultDirectory, "Latest.txt") : ResultStore.DefaultDirectory);

    /// <summary>Opens a text report or the results folder; a missing report means no run has saved one yet.</summary>
    internal static void Open(string path)
    {
        try
        {
            Directory.CreateDirectory(ResultStore.DefaultDirectory);
            if (!File.Exists(path) && !Directory.Exists(path)) { MessageBox.Show("Choose Run Perf Test to create results.", "OnDemand Perf"); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Unable to open results"); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { showRegistration.Unregister(null); marshal.Dispose(); icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); trayIcon.Dispose(); window.Dispose(); }
        base.Dispose(disposing);
    }
}
