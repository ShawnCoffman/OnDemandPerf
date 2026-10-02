namespace OnDemandPerf;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\OnDemandPerf.Tray.v1", out bool created);
        if (!created) return;
        ApplicationConfiguration.Initialize();
        using var context = new TrayContext();
        if (args.Contains("/show", StringComparer.OrdinalIgnoreCase)) context.ShowWindow();
        Application.Run(context);
    }
}
