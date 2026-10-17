using System.Diagnostics;
using OnDemandPerf.Core;

namespace OnDemandPerf;

internal sealed class TestWindow : Form
{
    private readonly Button run = new PerfButton { Text = "Run Perf Test", Primary = true, Width = 116 };
    private readonly Button cancel = new PerfButton { Text = "Cancel", Width = 72, Enabled = false };
    private readonly Button results = new PerfButton { Text = "Open Results", Width = 112 };
    private readonly Button copy = new PerfButton { Text = "Copy Summary", Width = 112, AccessibleDescription = "Copies a short summary for a support ticket" };
    private readonly Label status = new() { Text = "Ready to test", Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label elapsed = new() { Text = "CPU, RAM and disk · about 50 seconds", Dock = DockStyle.Fill };
    private readonly SegmentedActivity progress = new() { Dock = DockStyle.Fill };
    private readonly TextBox output = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = PerfTheme.Panel, ForeColor = PerfTheme.Text,
        AccessibleName = "Test activity and results"
    };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly Stopwatch clock = new();
    private readonly StageCard cpuCard = new() { Heading = "01 / CPU", Accent = PerfTheme.Green };
    private readonly StageCard ramCard = new() { Heading = "02 / RAM", Accent = PerfTheme.Purple };
    private readonly StageCard diskCard = new() { Heading = "03 / DISK", Accent = PerfTheme.Cyan };
    private readonly Label[] systemValues = new Label[4];
    private readonly Label systemHint = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = PerfTheme.Amber, Font = new Font("Segoe UI", 8) };
    private readonly ToolTip tips = new();
    private bool systemInfoRequested;
    private StageCard? activeCard;
    private Process? worker;
    private Task? runningTask;
    private bool cancelling;
    private string? reportPath;
    /// <summary>The result shown after this session's last run; Copy Summary falls back to Latest.json before the first run.</summary>
    private AnalysisResult? lastResult;
    /// <summary>The text report of <see cref="lastResult"/>; Open Results uses it so another run replacing Latest.txt cannot swap the file.</summary>
    private string? lastReportText;
    private string? outcome;
    private WorkerJob? job;
    private Screen? placementScreen;
    private int preferredHeight;

    internal bool IsRunning { get; private set; }
    internal string StatusText => status.Text;
    internal event EventHandler? StateChanged;
    /// <summary>
    /// Raised when a run ends for any reason other than the user cancelling it, including failures and timeouts; the argument
    /// is a one-line summary suitable for a tray notification.
    /// </summary>
    internal event EventHandler<string>? RunCompleted;

    public TestWindow()
    {
        Text = "OnDemand Perf";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(480, 590);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = PerfTheme.Background;
        ForeColor = PerfTheme.Text;
        Padding = new Padding(18);
        Icon = PerfTheme.LoadIcon();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11 };
        foreach (var height in new[] { 42, 112, 112, 48, 24, 8, 28 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        var header = new Panel { Dock = DockStyle.Fill };
        var title = new Label
        {
            Text = "ONDEMAND / PERF", Dock = DockStyle.Fill,
            Font = new Font("Consolas", 13, FontStyle.Bold), ForeColor = PerfTheme.Cyan,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var close = new PerfButton { Text = "×", AccessibleName = "Hide test panel", Dock = DockStyle.Right, Width = 32 };
        close.Click += (_, _) => Hide();
        header.Controls.Add(title); header.Controls.Add(close);
        Point dragOrigin = Point.Empty;
        title.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) dragOrigin = e.Location; };
        title.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - dragOrigin.X, Top + e.Y - dragOrigin.Y); };
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 6, 0, 6) };
        foreach (var card in new[] { cpuCard, ramCard, diskCard })
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(3, 0, 3, 0);
            cards.Controls.Add(card);
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.AddRange([run, cancel, results, copy]);
        status.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        status.TextAlign = ContentAlignment.MiddleLeft;
        elapsed.ForeColor = PerfTheme.Muted;
        output.Margin = new Padding(10, 7, 10, 7);
        var activityPanel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = PerfTheme.Panel, ColumnCount = 1, RowCount = 1, Margin = new Padding(3, 0, 3, 0) };
        activityPanel.Controls.Add(output);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(BuildSystemPanel(), 0, 1);
        layout.Controls.Add(cards, 0, 2);
        layout.Controls.Add(status, 0, 3);
        layout.Controls.Add(elapsed, 0, 4);
        layout.Controls.Add(progress, 0, 5);
        layout.Controls.Add(new Label { Text = "ACTIVITY / RESULTS", Font = new Font("Consolas", 9, FontStyle.Bold), ForeColor = PerfTheme.Cyan, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 6);
        layout.Controls.Add(activityPanel, 0, 7);
        layout.Controls.Add(buttons, 0, 9);
        layout.Controls.Add(new Label { Text = "ON DEMAND ONLY   ·   Close to hide, tests continue", ForeColor = PerfTheme.Muted, Font = new Font("Segoe UI", 8), Dock = DockStyle.Fill }, 0, 10);
        Controls.Add(layout);
        output.Text = "Run one performance test to check CPU, RAM and disk.\r\n\r\nEach operation will appear here as it runs.";
        results.Enabled = File.Exists(LatestText);
        copy.Enabled = File.Exists(LatestJson);
        copy.Click += (_, _) => CopySummary();
        run.Click += async (_, _) => await RunAsync();
        cancel.Click += async (_, _) => await StopAsync();
        results.Click += (_, _) => OpenShownResults();
        timer.Tick += (_, _) => { elapsed.Text = $"{clock.Elapsed.TotalSeconds:F0}s elapsed · CPU, RAM and disk"; progress.Advance(); cpuCard.Advance(); ramCard.Advance(); diskCard.Advance(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; } };
    }

    /// <summary>The "this machine" block: identity of the system, CPU, RAM and test drive, shown above the stage cards.</summary>
    private Control BuildSystemPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = PerfTheme.Panel, ColumnCount = 2, RowCount = 5,
            Margin = new Padding(3, 0, 3, 6), Padding = new Padding(10, 6, 10, 4)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) panel.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        var labels = new[] { "SYSTEM", "CPU", "RAM", "DISK" };
        for (int i = 0; i < 4; i++)
        {
            panel.Controls.Add(new Label
            {
                Text = labels[i], Dock = DockStyle.Fill, ForeColor = PerfTheme.Cyan, TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Consolas", 8, FontStyle.Bold)
            }, 0, i);
            systemValues[i] = new Label
            {
                Text = "Reading…", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = PerfTheme.Text, AccessibleName = labels[i] + " details"
            };
            panel.Controls.Add(systemValues[i], 1, i);
        }
        panel.Controls.Add(systemHint, 0, 4);
        panel.SetColumnSpan(systemHint, 2);
        return panel;
    }

    private void ShowSystem(DeviceInfo? device)
    {
        var lines = HardwareSummary.Lines(device);
        for (int i = 0; i < systemValues.Length; i++)
        {
            systemValues[i].Text = lines[i].Text;
            tips.SetToolTip(systemValues[i], lines[i].Text); // full text when the line is ellipsized
        }
        ShowAlerts(HardwareSummary.Hints(device), false);
    }

    /// <summary>The line under the machine identity: the most serious alert, the rest in its tooltip and the results text.</summary>
    private void ShowAlerts(IReadOnlyList<string> alerts, bool invalid)
    {
        systemHint.ForeColor = invalid ? PerfTheme.Red : PerfTheme.Amber;
        systemHint.Text = alerts.Count == 0 ? "" : (invalid ? "✖ " : "⚠ ") + alerts[0] + (alerts.Count > 1 ? $" (+{alerts.Count - 1} more)" : "");
        tips.SetToolTip(systemHint, string.Join(Environment.NewLine, alerts));
    }

    /// <summary>
    /// Identity is cheap (firmware table, registry, two drive queries) and read-only, so it is read the first time the panel is
    /// shown rather than at tray startup. It never touches the benchmark worker or its single-instance lock.
    /// </summary>
    private async Task LoadSystemInfoAsync()
    {
        DeviceInfo? device = null;
        try { device = await Task.Run(() => SystemInventory.Collect([], DiskBenchmark.DefaultDirectory)); }
        catch (Exception ex) { Append("Hardware details unavailable: " + ex.Message); }
        if (!IsDisposed) ShowSystem(device);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && !systemInfoRequested) { systemInfoRequested = true; _ = LoadSystemInfoAsync(); }
    }

    /// <param name="fromTrayIcon">
    /// True when the user clicked the tray icon or its menu, so the cursor is on the taskbar's monitor. Otherwise (a <c>/show</c>
    /// launch) the cursor can be anywhere, and the primary monitor, which holds the notification area by default, is used.
    /// </param>
    internal void ShowNearTray(bool fromTrayIcon)
    {
        placementScreen = fromTrayIcon ? Screen.FromPoint(Cursor.Position) : Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position);
        if (preferredHeight > 0) PlaceNearTray(); // the first time, OnLoad places it once the form is scaled
        Show();
        Activate();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e); // DPI scaling is applied here, before the form becomes visible
        preferredHeight = Height;
        PlaceNearTray();
    }

    /// <summary>
    /// Bottom-right of the working area, shortened (the activity box absorbs it) when the screen is too short. It never extends
    /// past the working area: below the minimum height the content keeps that height and the panel scrolls instead.
    /// </summary>
    private void PlaceNearTray()
    {
        var area = (placementScreen ?? Screen.FromControl(this)).WorkingArea;
        int margin = LogicalToDeviceUnits(12);
        var (height, scrollHeight) = PanelFit.Compute(preferredHeight, LogicalToDeviceUnits(MinimumPanelHeight), area.Height - margin * 2);
        Height = height;
        AutoScrollMinSize = new Size(0, scrollHeight); // setting this switches AutoScroll on, so AutoScroll is set after it
        AutoScroll = scrollHeight > 0;
        Location = new Point(Math.Max(area.Left, area.Right - Width - margin), Math.Max(area.Top, area.Bottom - Height - margin));
    }

    /// <summary>Every fixed row plus a few lines of activity; below this the layout would overlap.</summary>
    private const int MinimumPanelHeight = 520;

    internal Task RunAsync()
    {
        if (IsRunning) return runningTask ?? Task.CompletedTask;
        runningTask = RunCoreAsync();
        return runningTask;
    }

    private async Task RunCoreAsync()
    {
        IsRunning = true;
        cancelling = false;
        reportPath = null;
        outcome = null;
        run.Enabled = results.Enabled = copy.Enabled = false;
        cancel.Enabled = true;
        output.Clear();
        status.ForeColor = PerfTheme.Text;
        foreach (var card in new[] { cpuCard, ramCard, diskCard }) card.UpdateState("waiting", "—", "Waiting" );
        clock.Restart();
        timer.Start();
        progress.Style = ProgressBarStyle.Marquee;
        SetStatus("Starting performance test…");
        Task readers = Task.CompletedTask;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "worker", "OnDemandPerf.Worker.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = new System.Text.UTF8Encoding(false), StandardErrorEncoding = new System.Text.UTF8Encoding(false)
            };
            start.ArgumentList.Add("/run");
            worker = Process.Start(start) ?? throw new InvalidOperationException("Unable to start worker.");
            var current = worker;
            try { job ??= new WorkerJob(); job.Add(current); }
            catch (System.ComponentModel.Win32Exception ex) { Append("Note: the worker will not stop by itself if the tray closes unexpectedly (" + ex.Message + ")."); }
            readers = Task.WhenAll(ReadAsync(current.StandardOutput), ReadAsync(current.StandardError));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            bool timedOut = false;
            try { await current.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                if (!current.HasExited) current.Kill(entireProcessTree: true);
                await current.WaitForExitAsync();
            }
            await readers;
            if (!timedOut && current.ExitCode is 0 or 1)
            {
                // The worker names the exact file it wrote; never guess from Latest.json and timestamps.
                var path = reportPath ?? throw new InvalidOperationException("No report was saved for this test.");
                ShowResult(ResultStore.Load(path));
                lastReportText = ResultStore.TextReportPath(path);
            }
            else
            {
                SetStatus(timedOut ? "Test timed out" : cancelling ? "Test cancelled" : "Test could not finish");
                Append(timedOut ? "Stopped after the 60-second deadline. No complete result is available."
                    : $"Worker exited with code {current.ExitCode}. No new report is available.");
                outcome = status.Text + ". No new results.";
            }
        }
        catch (Exception ex)
        {
            SetStatus("Test could not finish");
            Append(ex.Message);
            outcome = "Test could not finish: " + ex.Message;
        }
        finally
        {
            if (worker is { } current && !current.HasExited)
            {
                try { current.Kill(entireProcessTree: true); await current.WaitForExitAsync(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Append(ex.Message); }
            }
            // Any reader failure was already reported by the catch above; this only observes it. Rethrowing from here would
            // escape the async void click handlers and crash the tray.
            try { await readers; } catch (Exception) { }
            worker?.Dispose(); worker = null;
            clock.Stop(); timer.Stop();
            elapsed.Text = $"{clock.Elapsed.TotalSeconds:F0}s elapsed · {DateTime.Now:t}";
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = 0;
            if (activeCard is not null) { activeCard.UpdateState("partial", "—", cancelling ? "Cancelled" : "Incomplete"); activeCard = null; }
            IsRunning = false;
            run.Enabled = true; cancel.Enabled = false;
            copy.Enabled = lastResult is not null || File.Exists(LatestJson);
            results.Enabled = File.Exists(ShownReportText);
            StateChanged?.Invoke(this, EventArgs.Empty);
            // A user who cancelled (or chose Exit) already knows; everyone else hears about every outcome, not just success.
            if (!cancelling) RunCompleted?.Invoke(this, outcome ?? status.Text);
        }
    }

    private static string LatestJson => Path.Combine(ResultStore.DefaultDirectory, "Latest.json");
    private static string LatestText => Path.Combine(ResultStore.DefaultDirectory, "Latest.txt");

    /// <summary>
    /// The report for the run the panel shows, matching Copy Summary. Before the first run, or once history pruning has removed
    /// that run's file, it is Latest.txt.
    /// </summary>
    private string ShownReportText => lastReportText is { } shown && File.Exists(shown) ? shown : LatestText;

    private void OpenShownResults() => TrayContext.Open(ShownReportText);

    /// <summary>Puts the ticket summary on the clipboard and confirms on the button itself, leaving the headline alone.</summary>
    private void CopySummary()
    {
        try
        {
            var report = lastResult ?? ResultStore.Load(LatestJson);
            Clipboard.SetText(string.Join(Environment.NewLine, ResultSummary.TicketSummary(report)));
            copy.Text = "Copied ✓";
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException)
        {
            copy.Text = "Copy failed";
            Append("Copy Summary: " + ex.Message);
        }
        var reset = new System.Windows.Forms.Timer { Interval = 2000 };
        reset.Tick += (_, _) => { reset.Dispose(); if (!copy.IsDisposed) copy.Text = "Copy Summary"; };
        reset.Start();
    }

    private void ShowResult(AnalysisResult report)
    {
        activeCard = null;
        lastResult = report;
        if (report.Device is not null) ShowSystem(report.Device);
        // After a run the findings (power, background load, memory, disk space, restarts, hardware hints) replace the hints.
        if (report.Findings.Count > 0) ShowAlerts(ResultSummary.FindingLines(report), report.Validity == "Invalid");
        var cpu = ResultSummary.CpuCard(report);
        var ram = ResultSummary.RamCard(report);
        var disk = ResultSummary.DiskCard(report);
        foreach (var (card, value) in new[] { (cpuCard, cpu), (ramCard, ram), (diskCard, disk) })
            card.UpdateState(value.State, value.Value, value.Caption);
        SetStatus(ResultSummary.Headline(report));
        status.ForeColor = report.Validity == "Invalid" ? PerfTheme.Red
            : report.Environment?.PowerSource == "Battery" ? PerfTheme.Amber : PerfTheme.Text;
        outcome = ResultSummary.Notification(report);
        var activity = output.Text;
        output.Text = string.Join(Environment.NewLine, ResultSummary.Lines(report)) + "\r\n\r\nActivity\r\n" + activity;
        output.SelectionStart = 0;
        output.SelectionLength = 0;
        output.ScrollToCaret();
    }

    private async Task ReadAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            // "##" lines are the worker's structured protocol; everything else is human-readable activity.
            if (line.StartsWith("##stage ", StringComparison.Ordinal)) { if (!cancelling) ActivateStage(line[8..]); continue; }
            if (line.StartsWith("##report ", StringComparison.Ordinal)) { reportPath = line[9..]; continue; }
            Append(line);
            if (!cancelling) SetStatus(line);
        }
    }

    private void ActivateStage(string stage)
    {
        StageCard? next = stage switch { "CPU" => cpuCard, "RAM" => ramCard, "Disk" => diskCard, _ => null };
        if (next is null || next == activeCard) return;
        activeCard?.UpdateState("waiting", "—", "Awaiting result");
        activeCard = next;
        activeCard.UpdateState("running", "Testing", "In progress");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(PerfTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    private void SetStatus(string text) { status.Text = text; StateChanged?.Invoke(this, EventArgs.Empty); }
    private void Append(string message) { if (!IsDisposed) output.AppendText(message + Environment.NewLine); }

    internal async Task StopAsync()
    {
        var completion = runningTask;
        var current = worker;
        if (current is null) { if (completion is not null) await completion; return; }
        cancelling = true;
        cancel.Enabled = false;
        SetStatus("Cancelling — waiting for cleanup…");
        try
        {
            if (!current.HasExited)
            {
                await current.StandardInput.WriteLineAsync("cancel");
                await current.StandardInput.FlushAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await current.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { if (!current.HasExited) current.Kill(entireProcessTree: true); }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        { Append("Cancellation: " + ex.Message); }
        if (completion is not null) await completion;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tips.Dispose(); job?.Dispose(); }
        base.Dispose(disposing);
    }
}




