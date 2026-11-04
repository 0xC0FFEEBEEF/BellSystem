using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SchoolBell.Contracts;

return Deploy(args);

static int Deploy(string[] args)
{
    string? temp = null;
    bool silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("/VERYSILENT", StringComparison.OrdinalIgnoreCase) || a.Equals("/SILENT", StringComparison.OrdinalIgnoreCase));
    try
    {
        // Require an explicit address: school-specific downloads never choose a server for you.
        if (silent && !args.Any(a => a.Equals("--server", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--server=", StringComparison.OrdinalIgnoreCase) || a.StartsWith("/SERVER=", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Supply the school server address with --server HOST:PORT or /SERVER=HOST:PORT.");
        using var own = File.OpenRead(Environment.ProcessPath!);
        var payload = DeploymentPackage.Read(own);
        // An unpredictable directory under the administrator's temp location holds enrollment only until setup exits.
        temp = Path.Combine(Path.GetTempPath(), "SchoolBell-deploy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var acl = new ProcessStartInfo("icacls.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { temp, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F" })
            acl.ArgumentList.Add(value);
        using (var secure = Process.Start(acl) ?? throw new IOException("Could not secure deployment files."))
        {
            secure.WaitForExit();
            if (secure.ExitCode != 0) throw new IOException("Could not secure deployment files.");
        }
        string setup = Path.Combine(temp, "SchoolBellSetup.exe"), token = Path.Combine(temp, "schoolbell-enroll.json");
        using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("SchoolBellSetup.exe")
            ?? throw new InvalidDataException("The embedded installer is missing."))
        using (var output = File.Create(setup)) source.CopyTo(output);
        File.WriteAllText(token, payload.EnrollmentJson);
        var start = new ProcessStartInfo(setup) { UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("/ENROLLMENTFILE=" + token);
        if (payload.CaCertificateBase64 is { Length: > 0 } cert)
        {
            string ca = Path.Combine(temp, "school-ca.cer");
            File.WriteAllBytes(ca, Convert.FromBase64String(cert));
            start.ArgumentList.Add("/CAFILE=" + ca);
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start SchoolBell setup.");
        process.WaitForExit();
        return process.ExitCode;
    }
    catch (Exception ex)
    {
        // Never include payloads or credentials in errors.
        string message = ex is ArgumentException ? ex.Message : "SchoolBell deployment could not start. Download a fresh installer from /admin and try again.";
        Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SchoolBell", "logs"));
        File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SchoolBell", "logs", "deployment.log"), message);
        if (!silent) MessageBoxW(IntPtr.Zero, message, "SchoolBell deployment", 0x10);
        return 2;
    }
    finally
    {
        if (temp is not null) { try { Directory.Delete(temp, recursive: true); } catch { } }
    }
}

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
