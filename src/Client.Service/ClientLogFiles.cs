using SchoolBell.Client.Core;

namespace SchoolBell.Client.Service;

internal static class ClientLogFiles
{
    public static IEnumerable<ClientLogFile> Discover()
    {
        var common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SchoolBell");
        foreach (var file in Logs(common, recursive: true))
            yield return new(file, "machine/" + Normalize(Path.GetRelativePath(common, file)));

        var profiles = Path.Combine(Path.GetPathRoot(common)!, "Users");
        foreach (var profile in Profiles(profiles))
        {
            var companion = Path.Combine(profile, "AppData", "Local", "SchoolBell");
            foreach (var file in Logs(companion))
                yield return new(file, "companion/" + Path.GetFileName(profile) + "/" + Normalize(Path.GetFileName(file)));
        }
        var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        foreach (var file in Logs(temp).Where(f => Path.GetFileName(f).StartsWith("SchoolBell", StringComparison.OrdinalIgnoreCase)))
            yield return new(file, "setup/" + Normalize(Path.GetFileName(file)));
    }

    private static IEnumerable<string> Profiles(string fallback)
    {
        var paths = new HashSet<string>(Directories(fallback), StringComparer.OrdinalIgnoreCase);
        using var list = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (list is not null)
            foreach (var name in list.GetSubKeyNames())
            {
                using var profile = list.OpenSubKey(name);
                if (profile?.GetValue("ProfileImagePath") is string path) paths.Add(Environment.ExpandEnvironmentVariables(path));
            }
        return paths;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('1', '2', '.');
    private static string[] Directories(string dir)
    {
        try { return Directory.GetDirectories(dir).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).ToArray(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
    private static IEnumerable<string> Logs(string dir, bool recursive = false)
    {
        string[] files;
        try { files = Directory.GetFiles(dir, "*.log*"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var file in files.OrderByDescending(f => f.EndsWith(".2") ? 2 : f.EndsWith(".1") ? 1 : 0))
            if (file.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".log.1") || file.EndsWith(".log.2"))
                yield return file;
        if (recursive)
            foreach (var child in Directories(dir))
                foreach (var file in Logs(child, true)) yield return file;
    }
}
