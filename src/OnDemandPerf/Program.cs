
namespace OnDemandPerf;

internal static class Program
{
    internal const string ShowSignalName = @"Local\OnDemandPerf.Tray.Show.v1";

    [STAThread]
    private static void Main(string[] args)
    {
        bool show = args.Contains("/show", StringComparer.OrdinalIgnoreCase);
        using var mutex = new Mutex(true, @"Local\OnDemandPerf.Tray.v1", out bool created);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        if (!created)
        {
            // Another tray is running: ask it to raise its panel instead of silently doing nothing.
            if (show) showSignal.Set();
            return;
        }
        ApplicationConfiguration.Initialize();
        using var context = new TrayContext(showSignal);
        if (show) context.ShowWindow();
        Application.Run(context);
    }
}
