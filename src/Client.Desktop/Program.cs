namespace SchoolBell.Client.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // One companion per Windows session (the logon task and a manual start must not double up).
        using var single = new Mutex(true, @"Local\SchoolBellCompanion", out bool first);
        if (!first) return 0;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        using var context = new CompanionContext(args.Contains("--show-history"));
        Application.Run(context);
        return context.ExitCode;
    }
}
