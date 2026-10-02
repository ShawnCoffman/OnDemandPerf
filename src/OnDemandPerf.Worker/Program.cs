using System.Diagnostics;
using OnDemandPerf.Core;

namespace OnDemandPerf.Worker;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("/help"))
        {
            Console.WriteLine("OnDemand Perf worker: /run | /inventory | /cpu | /memory | /disk [/output <directory>] [/diskpath <test directory>]\nExit codes: 0 success; 1 partial failure/cancelled; 2 fatal failure; 3 already running.\nSend 'cancel' on standard input or press Ctrl+C to cancel.");
            return 0;
        }
        string? mode = null;
        string directory = ResultStore.DefaultDirectory;
        string diskDirectory = DiskBenchmark.DefaultDirectory;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "/run" or "/cpu" or "/inventory" or "/memory" or "/disk" && mode is null) mode = args[i];
            else if (args[i] == "/output" && i + 1 < args.Length) directory = args[++i];
            else if (args[i] == "/diskpath" && i + 1 < args.Length) diskDirectory = args[++i];
            else { Console.Error.WriteLine($"Invalid argument: {args[i]}"); return 2; }
        }
        if (mode is null) { Console.Error.WriteLine("Specify /run, /cpu, /memory, /disk, or /inventory."); return 2; }
        try { return Execute(mode, Path.GetFullPath(directory), Path.GetFullPath(diskDirectory)); }
        catch (Exception ex) { Console.Error.WriteLine($"OnDemand Perf failed: {ex.Message}"); return 2; }
    }

    private static int Execute(string mode, string directory, string diskDirectory)
    {
        // Fail closed if another identity owns the global mutex; never overlap machine workloads.
        using var mutex = new Mutex(false, @"Global\OnDemandPerf.Worker.v1");
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { Console.Error.WriteLine("An OnDemand Perf worker is already running."); return 3; }
        try
        {
            var store = new ResultStore(directory);
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
            try { result.Device = SystemInventory.Collect(result.Warnings); }
            catch (Exception ex) { result.Status = "Failed"; result.Errors.Add("Inventory: " + ex.Message); }
            using var activity = new ActivitySampler();
            try
            {
                Console.WriteLine("Sampling system conditions before testing...");
                result.Environment = new TestEnvironment();
                EnvironmentCollector.Collect(result.Environment, result.Warnings, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                void Stage(string name, Action action)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    activity.Stage = name;
                    store.Log($"INFO {name} stage started");
                    try { action(); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result.Status = "Partial"; result.Errors.Add($"{name}: {ex.Message}"); }
                    store.Log($"INFO {name} stage finished");
                }
                if (mode is "/cpu" or "/run") Stage("CPU", () => result.Cpu = CpuBenchmark.Run(cancellation.Token, Console.WriteLine));
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
            store.Save(result);
            foreach (var warning in result.Warnings) store.Log("WARN " + warning);
            if (result.Memory is { } memory)
                foreach (var note in memory.Notes) store.Log($"INFO RAM ({memory.Status}): {note}");
            if (result.Disk is { } disk)
            {
                store.Log($"INFO Disk ({disk.Status}): target {disk.TargetDirectory}; {disk.TotalBytesWritten} bytes written; cleanup {disk.CleanupSucceeded}");
                foreach (var note in disk.Notes) store.Log("INFO Disk: " + note);
            }
            store.Log($"INFO Run {result.RunId} finished: {result.Status}; {string.Join("; ", result.Errors)}");
            Console.WriteLine($"{result.Status}. Results: {directory}");
            return result.Status == "Success" ? 0 : 1;
        }
        finally { mutex.ReleaseMutex(); }
    }
}

