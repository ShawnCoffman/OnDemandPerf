using System.Diagnostics;
using System.Text.Json;
using OnDemandPerf.Core;

namespace OnDemandPerf;

internal sealed class TestWindow : Form
{
    private readonly Button run = new PerfButton { Text = "Run Perf Test", Primary = true };
    private readonly Button cancel = new PerfButton { Text = "Cancel", Width = 80, Enabled = false };
    private readonly Button results = new PerfButton { Text = "Open Results", Width = 140 };
    private readonly Label status = new() { Text = "Ready to test", Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label elapsed = new() { Text = "CPU, RAM and disk · usually 45–55 seconds", Dock = DockStyle.Fill };
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
    private StageCard? activeCard;
    private Process? worker;
    private Task? runningTask;
    private bool cancelling;

    internal bool IsRunning { get; private set; }
    internal string StatusText => status.Text;
    internal event EventHandler? StateChanged;

    public TestWindow()
    {
        Text = "OnDemand Perf";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        ClientSize = new Size(480, 480);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = PerfTheme.Background;
        ForeColor = PerfTheme.Text;
        Padding = new Padding(18);
        Icon = PerfTheme.LoadIcon();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10 };
        foreach (var height in new[] { 42, 112, 48, 24, 8, 28 })
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
        buttons.Controls.AddRange([run, cancel, results]);
        status.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        status.TextAlign = ContentAlignment.MiddleLeft;
        elapsed.ForeColor = PerfTheme.Muted;
        output.Margin = new Padding(10, 7, 10, 7);
        var activityPanel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = PerfTheme.Panel, ColumnCount = 1, RowCount = 1, Margin = new Padding(3, 0, 3, 0) };
        activityPanel.Controls.Add(output);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(cards, 0, 1);
        layout.Controls.Add(status, 0, 2);
        layout.Controls.Add(elapsed, 0, 3);
        layout.Controls.Add(progress, 0, 4);
        layout.Controls.Add(new Label { Text = "ACTIVITY / RESULTS", Font = new Font("Consolas", 9, FontStyle.Bold), ForeColor = PerfTheme.Cyan, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 5);
        layout.Controls.Add(activityPanel, 0, 6);
        layout.Controls.Add(buttons, 0, 8);
        layout.Controls.Add(new Label { Text = "ON DEMAND ONLY   ·   Close to hide, tests continue", ForeColor = PerfTheme.Muted, Font = new Font("Segoe UI", 8), Dock = DockStyle.Fill }, 0, 9);
        Controls.Add(layout);
        output.Text = "Run one performance test to check CPU, RAM and disk.\r\n\r\nEach operation will appear here as it runs.";
        results.Enabled = File.Exists(Path.Combine(ResultStore.DefaultDirectory, "Latest.txt"));
        run.Click += async (_, _) => await RunAsync();
        cancel.Click += async (_, _) => await StopAsync();
        results.Click += (_, _) => TrayContext.OpenResults(true);
        timer.Tick += (_, _) => { elapsed.Text = $"{clock.Elapsed.TotalSeconds:F0}s elapsed · CPU, RAM and disk"; progress.Advance(); cpuCard.Advance(); ramCard.Advance(); diskCard.Advance(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; } };
    }

    internal void ShowNearTray()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Show();
        Location = new Point(Math.Max(area.Left, area.Right - Width - 12), Math.Max(area.Top, area.Bottom - Height - 12));
        Activate();
    }

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
        run.Enabled = results.Enabled = false;
        cancel.Enabled = true;
        output.Clear();
        foreach (var card in new[] { cpuCard, ramCard, diskCard }) card.UpdateState("waiting", "—", "Waiting" );
        clock.Restart();
        timer.Start();
        progress.Style = ProgressBarStyle.Marquee;
        SetStatus("Starting performance test…");
        var started = DateTimeOffset.Now;
        Task readers = Task.CompletedTask;
        try
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "worker", "OnDemandPerf.Worker.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            start.ArgumentList.Add("/run");
            worker = Process.Start(start) ?? throw new InvalidOperationException("Unable to start worker.");
            var current = worker;
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
                var json = await File.ReadAllTextAsync(Path.Combine(ResultStore.DefaultDirectory, "Latest.json"));
                using var report = JsonDocument.Parse(json);
                if (report.RootElement.GetProperty("timestamp").GetDateTimeOffset() < started)
                    throw new InvalidOperationException("No report was saved for this test.");
                ShowResult(report.RootElement);
                results.Enabled = File.Exists(Path.Combine(ResultStore.DefaultDirectory, "Latest.txt"));
            }
            else
            {
                SetStatus(timedOut ? "Test timed out" : cancelling ? "Test cancelled" : "Test could not finish");
                Append(timedOut ? "Stopped after the 60-second deadline. No complete result is available."
                    : $"Worker exited with code {current.ExitCode}. No new report is available.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Test could not finish");
            Append(ex.Message);
        }
        finally
        {
            if (worker is { } current && !current.HasExited)
            {
                try { current.Kill(entireProcessTree: true); await current.WaitForExitAsync(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Append(ex.Message); }
            }
            try { await readers; } catch (IOException) { }
            worker?.Dispose(); worker = null;
            clock.Stop(); timer.Stop();
            elapsed.Text = $"{clock.Elapsed.TotalSeconds:F0}s elapsed · {DateTime.Now:t}";
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = 0;
            if (activeCard is not null) { activeCard.UpdateState("partial", "—", cancelling ? "Cancelled" : "Incomplete"); activeCard = null; }
            IsRunning = false;
            run.Enabled = true; cancel.Enabled = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ShowResult(JsonElement report)
    {
        activeCard = null;
        SetCardResult(cpuCard, report.GetProperty("cpu"), "multiThread", "mBps", "Multi · MB/s");
        SetCardResult(ramCard, report.GetProperty("memory"), null, "copyGBps", "Copy · GB/s");
        var diskResult = report.GetProperty("disk");
        JsonElement? sequentialRead = diskResult.ValueKind == JsonValueKind.Object
            ? diskResult.GetProperty("measurements").EnumerateArray().FirstOrDefault(m => m.GetProperty("operation").GetString() == "SequentialRead") : null;
        SetCardResult(diskCard, diskResult, null, null, "Seq read · MB/s", sequentialRead);
        var outcome = report.GetProperty("status").GetString();
        var warnings = report.GetProperty("warnings").GetArrayLength();
        SetStatus(outcome switch
        {
            "Success" => warnings > 0 ? "Test complete — review warnings" : "Test complete",
            "Cancelled" => "Test cancelled",
            "Partial" => "Test complete — review incomplete tests",
            _ => "Test failed"
        });
        var lines = new List<string>();
        if (report.GetProperty("cpu") is { ValueKind: JsonValueKind.Object } cpu)
        {
            foreach (var (key, label) in new[] { ("singleThread", "CPU single-thread"), ("multiThread", "CPU multithread") })
                if (cpu.GetProperty(key) is { ValueKind: JsonValueKind.Object } measurement)
                    lines.Add($"{label}: {measurement.GetProperty("mBps").GetDouble():F1} MB/s");
        }
        if (report.GetProperty("memory") is { ValueKind: JsonValueKind.Object } memory)
        {
            string Rate(string key) => memory.GetProperty(key) is { ValueKind: JsonValueKind.Number } value
                ? $"{value.GetDouble():F1}" : "—";
            lines.Add($"RAM: copy/read/write {Rate("copyGBps")} / {Rate("readGBps")} / {Rate("writeGBps")} GB/s");
            if (memory.TryGetProperty("latency", out var latency) && latency.ValueKind == JsonValueKind.Object &&
                latency.TryGetProperty("nanosecondsPerAccess", out var ns) && ns.ValueKind == JsonValueKind.Number)
                lines.Add($"RAM dependent-access latency: {ns.GetDouble():F1} ns");
            if (memory.GetProperty("status").GetString() != "Success") lines.Add("RAM: " + memory.GetProperty("status").GetString());
        }
        if (report.GetProperty("disk") is { ValueKind: JsonValueKind.Object } disk)
        {
            if (disk.GetProperty("status").GetString() != "Success") lines.Add("Disk: " + disk.GetProperty("status").GetString());
            foreach (var measurement in disk.GetProperty("measurements").EnumerateArray().OrderBy(m => m.GetProperty("operation").GetString() switch { "RandomRead" => 0, "SequentialRead" => 1, _ => 2 }))
            {
                var operation = measurement.GetProperty("operation").GetString() switch
                {
                    "SequentialWrite" => "sequential write", "SequentialRead" => "sequential read",
                    "RandomRead" => "random read", "RandomWrite" => "random write", _ => "measurement"
                };
                var measurementStatus = measurement.GetProperty("status").GetString();
                var note = measurementStatus == "Success" ? "" : $" ({measurementStatus})";
                if (measurement.TryGetProperty("shortBurst", out var burst) && burst.GetBoolean()) note += " · short burst";
                if (measurement.GetProperty("operation").GetString() == "RandomRead")
                    lines.Add($"Disk random read: {measurement.GetProperty("iops").GetDouble():N0} IOPS · p95 {measurement.GetProperty("p95LatencyMs").GetDouble():F3} ms{note}");
                if (measurement.GetProperty("operation").GetString() == "RandomRead") continue;
                lines.Add($"Disk: {operation} {measurement.GetProperty("mBps").GetDouble():F1} MB/s{note}");
            }
        }
        foreach (var error in report.GetProperty("errors").EnumerateArray()) lines.Add("Error: " + error.GetString());
        if (warnings > 0) lines.Add($"{warnings} warning(s). Open Results for details.");
        lines.Add("\r\nOpen Results for the full report, measurement notes and test conditions.");
        var activity = output.Text;
        output.Text = string.Join(Environment.NewLine, lines) + "\r\n\r\nActivity\r\n" + activity;
        output.SelectionStart = 0;
        output.SelectionLength = 0;
        output.ScrollToCaret();
    }

    private async Task ReadAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            Append(line);
            if (!cancelling)
            {
                StageCard? next = line.StartsWith("Single-thread") || line.StartsWith("Multithread") ? cpuCard
                    : line.StartsWith("RAM ") ? ramCard : line.StartsWith("Disk ") ? diskCard : null;
                if (next is not null && next != activeCard)
                {
                    activeCard?.UpdateState("waiting", "—", "Awaiting result");
                    activeCard = next;
                    activeCard.UpdateState("running", "Testing", "In progress");
                }
                SetStatus(line);
            }
        }
    }

    private static void SetCardResult(StageCard card, JsonElement stage, string? nested, string? metric, string caption, JsonElement? measurement = null)
    {
        if (stage.ValueKind != JsonValueKind.Object) { card.UpdateState("waiting", "—", "Not completed"); return; }
        string state = stage.GetProperty("status").GetString() ?? "Incomplete";
        JsonElement value = measurement ?? (nested is null ? stage : stage.GetProperty(nested));
        bool valid = value.ValueKind == JsonValueKind.Object && value.TryGetProperty(metric ?? "mBps", out var rate) && rate.ValueKind == JsonValueKind.Number;
        if (state == "Success" && valid)
            card.UpdateState("success", value.GetProperty(metric ?? "mBps").GetDouble().ToString("N1"), caption);
        else card.UpdateState("partial", "—", state == "Success" ? "Not completed" : state);
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
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}




