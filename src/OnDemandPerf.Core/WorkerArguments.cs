namespace OnDemandPerf.Core;

/// <summary>Command-line parsing for the worker, kept in Core so it can be unit tested. Switches are case-insensitive.</summary>
public sealed record WorkerArguments(string? Mode, string? OutputDirectory, string? DiskDirectory, bool Compact, bool Help, string? Error)
{
    private static readonly string[] Modes = ["/run", "/cpu", "/inventory", "/memory", "/disk"];

    public static WorkerArguments Parse(string[] args)
    {
        string? mode = null, output = null, disk = null;
        bool compact = false, help = args.Length == 0;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();
            if (arg == "/help") help = true;
            else if (arg == "/compact") compact = true;
            else if (Modes.Contains(arg) && mode is null) mode = arg;
            else if (arg == "/output" && i + 1 < args.Length) output = args[++i];
            else if (arg == "/diskpath" && i + 1 < args.Length) disk = args[++i];
            else return new(null, null, null, false, false, $"Invalid argument: {args[i]}");
        }
        if (!help && mode is null) return new(null, output, disk, compact, false, "Specify /run, /cpu, /memory, /disk, or /inventory.");
        return new(mode, output, disk, compact, help, null);
    }
}
