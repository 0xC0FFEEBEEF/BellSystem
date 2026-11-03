namespace SchoolBell.Client.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Placeholder entry point; owned by the Windows worker (milestone C3).
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    public MainForm()
    {
        Text = "SchoolBell companion (skeleton)";
        Width = 480; Height = 320;
    }
}
