using System.Diagnostics;
using OnDemandPerf.Core;

namespace OnDemandPerf.Worker;

internal static class Program
{
    private const string Usage = "OnDemand Perf worker: /run | /inventory | /cpu | /memory | /disk [/output <directory>] [/diskpath <test directory>] [/compact]\n" +
        "Exit codes: 0 success; 1 partial failure/cancelled; 2 fatal failure; 3 already running.\n" +
        "Send 'cancel' on standard input or press Ctrl+C to cancel. Lines starting with '##' are machine-readable progress for the tray.";

    private static int Main(string[] args)
    {
        // The tray reads our stdout; paths in "##report" lines must survive non-ASCII user names.
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch (IOException) { }
        var options = WorkerArguments.Parse(args);
        if (options.Error is not null) { Console.Error.WriteLine(options.Error); return 2; }
        if (options.Help) { Console.WriteLine(Usage); return 0; }
        try
        {
            return Execute(options.Mode!, Path.GetFullPath(options.OutputDirectory ?? ResultStore.DefaultDirectory),
                Path.GetFullPath(options.DiskDirectory ?? DiskBenchmark.DefaultDirectory), options.Compact);
        }
        catch (Exception ex) { Console.Error.WriteLine($"OnDemand Perf failed: {ex.Message}"); return 2; }
    }

    private static int Execute(string mode, string directory, string diskDirectory, bool compact)
    {
        // Fail closed if another identity owns the global mutex; never overlap machine workloads.
        // A mutex created by another account typically cannot be opened at all, which is "already running", not a crash.
        Mutex mutex;
        try { mutex = new Mutex(false, @"Global\OnDemandPerf.Worker.v1"); }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("An OnDemand Perf worker owned by another account is already running."); return 3; }
        using (mutex)
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { Console.Error.WriteLine("An OnDemand Perf worker is already running."); return 3; }
            try { return Run(mode, directory, diskDirectory, compact); }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private static int Run(string mode, string directory, string diskDirectory, bool compact)
    {
        var store = new ResultStore(directory, compact);
        var result = new AnalysisResult();
        var timer = Stopwatch.StartNew();
        // Leave five seconds of the overall 60-second budget for cleanup and output.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        if (Console.IsInputRedirected)
            _ = Task.Run(async () =>
            {
                if (await Console.In.ReadLineAsync() == "cancel")
                    try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            });
        store.Log($"INFO Run {result.RunId} started ({mode}), version {result.ApplicationVersion}");
        try { result.Device = SystemInventory.Collect(result.Warnings, diskDirectory); }
        catch (Exception ex) { result.Status = "Failed"; result.Errors.Add("Inventory: " + ex.Message); }
        using var activity = new ActivitySampler();
        try
        {
            Console.WriteLine("Sampling system conditions before testing...");
            result.Environment = new TestEnvironment { ProcessPowerThrottling = ProcessQos.OptOutOfPowerThrottling() };
            if (result.Environment.ProcessPowerThrottling != "OptedOut")
                result.Warnings.Add("Process power throttling: " + result.Environment.ProcessPowerThrottling);
            EnvironmentCollector.Collect(result.Environment, result.Warnings, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            void Stage(string name, Action action)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                activity.Stage = name;
                Console.WriteLine("##stage " + name); // structured marker; the tray must not infer stages from prose
                store.Log($"INFO {name} stage started");
                try { action(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result.Status = "Partial"; result.Errors.Add($"{name}: {ex.Message}"); }
                store.Log($"INFO {name} stage finished");
            }
            if (mode is "/cpu" or "/run")
            {
                Stage("CPU", () => result.Cpu = CpuBenchmark.Run(cancellation.Token, Console.WriteLine));
                if (result.Cpu is { Status: not "Success" }) { result.Status = "Partial"; result.Errors.Add("CPU: " + result.Cpu.Status); }
            }
            if (mode is "/memory" or "/run")
            {
                Stage("RAM", () => result.Memory = MemoryBenchmark.Run(cancellation.Token, Console.WriteLine));
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                if (result.Memory is { Status: not "Success" }) { result.Status = "Partial"; result.Errors.Add("RAM: " + result.Memory.Status); }
            }
            if (mode is "/disk" or "/run")
            {
                Stage("Disk", () => result.Disk = DiskBenchmark.Run(diskDirectory, cancellation.Token, Console.WriteLine));
                if (result.Disk is { Status: not "Success" }) { result.Status = "Partial"; result.Errors.Add("Disk: " + result.Disk.Status); }
            }
            cancellation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { result.Status = "Cancelled"; result.Errors.Add("Run cancelled or reached the 55-second work deadline (60 seconds including cleanup/output)."); }
        catch (Exception ex) { result.Status = "Failed"; result.Errors.Add("Run: " + ex.Message); }
        activity.Dispose();
        result.Activity = activity.Result;
        timer.Stop();
        result.DurationSeconds = timer.Elapsed.TotalSeconds;
        string reportPath = store.Save(result);
        foreach (var warning in result.Warnings) store.Log("WARN " + warning);
        if (result.Memory is { } memory)
            foreach (var note in memory.Notes) store.Log($"INFO RAM ({memory.Status}): {note}");
        if (result.Disk is { } disk)
        {
            store.Log($"INFO Disk ({disk.Status}): target {disk.TargetDirectory}; {disk.TotalBytesWritten} bytes written; cleanup {disk.CleanupSucceeded}");
            foreach (var note in disk.Notes) store.Log("INFO Disk: " + note);
        }
        store.Log($"INFO Run {result.RunId} finished: {result.Status}; {string.Join("; ", result.Errors)}");
        // The tray reads exactly this file instead of guessing from Latest.json and timestamps.
        Console.WriteLine("##report " + reportPath);
        Console.WriteLine($"{result.Status}. Results: {directory}");
        return result.Status == "Success" ? 0 : 1;
    }
}
