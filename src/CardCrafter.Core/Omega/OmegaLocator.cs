using Microsoft.Win32;

namespace CardCrafter.Core.Omega;

/// <param name="Root">The Omega install folder.</param>
/// <param name="FoundBy">How it was found, shown to the user, e.g. "Windows uninstall registry".</param>
public sealed record OmegaInstall(string Root, string FoundBy);

/// <summary>Finds the YGO Omega install folder without asking the user, falling back through several places.</summary>
public static class OmegaLocator
{
    public const string FolderName = "YGO Omega";

    /// <summary>Returns the first candidate folder that looks like an Omega install, or null.</summary>
    public static OmegaInstall? Find(string? savedPath = null, bool scanDrives = true)
    {
        foreach (var (path, how) in Candidates(savedPath, scanDrives))
        {
            if (LooksLikeOmega(path)) return new OmegaInstall(Path.GetFullPath(path), how);
        }
        return null;
    }

    /// <summary>
    /// True when the folder contains an Omega executable or a Unity "...Omega..._Data" folder.
    /// Checking contents, not just the name, avoids picking an empty leftover folder.
    /// </summary>
    public static bool LooksLikeOmega(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        try
        {
            if (Directory.EnumerateFiles(dir, "*.exe").Any(f => Path.GetFileName(f).Contains("omega", StringComparison.OrdinalIgnoreCase)))
                return true;
            return Directory.EnumerateDirectories(dir, "*_Data").Any(d => Path.GetFileName(d).Contains("omega", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IEnumerable<(string Path, string How)> Candidates(string? savedPath, bool scanDrives)
    {
        if (!string.IsNullOrWhiteSpace(savedPath)) yield return (savedPath, "your saved setting");

        var env = Environment.GetEnvironmentVariable("YGO_OMEGA_PATH");
        if (!string.IsNullOrWhiteSpace(env)) yield return (env, "the YGO_OMEGA_PATH environment variable");

        foreach (var p in RegistryCandidates()) yield return (p, "the Windows uninstall registry");

        foreach (var p in KnownFolders()) yield return (p, "a standard install folder");

        if (scanDrives)
            foreach (var p in ScanDrives()) yield return (p, "a search of your drives");
    }

    private static IEnumerable<string> KnownFolders()
    {
        var special = new[]
        {
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.UserProfile,
        };
        foreach (var sf in special)
        {
            var root = Environment.GetFolderPath(sf);
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, FolderName);
            if (sf == Environment.SpecialFolder.LocalApplicationData) yield return Path.Combine(root, "Programs", FolderName);
            if (sf == Environment.SpecialFolder.UserProfile)
            {
                yield return Path.Combine(root, "Downloads", FolderName);
                yield return Path.Combine(root, "Games", FolderName);
            }
        }
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files (x86)\YGO Omega";
            yield return @"C:\Program Files\YGO Omega";
        }
        foreach (var drive in FixedDrives())
        {
            yield return Path.Combine(drive, FolderName);
            yield return Path.Combine(drive, "Games", FolderName);
            yield return Path.Combine(drive, "Program Files (x86)", FolderName);
            yield return Path.Combine(drive, "Program Files", FolderName);
        }
    }

    private static IEnumerable<string> RegistryCandidates()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var found = new List<string>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var key = hive.OpenSubKey(path);
                    if (key is null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var app = key.OpenSubKey(name);
                        var display = app?.GetValue("DisplayName") as string;
                        if (display is null || !display.Contains("omega", StringComparison.OrdinalIgnoreCase)) continue;
                        if (app!.GetValue("InstallLocation") is string loc && loc.Length > 0) found.Add(loc.Trim('"'));
                        if (app.GetValue("DisplayIcon") is string icon && icon.Length > 0)
                        {
                            var exe = icon.Split(',')[0].Trim('"');
                            var dir = Path.GetDirectoryName(exe);
                            if (!string.IsNullOrEmpty(dir)) found.Add(dir);
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // An unreadable registry branch is not fatal; other strategies still run.
                }
            }
        }
        foreach (var f in found) yield return f;
    }

    /// <summary>Looks two folders deep on each fixed drive for a folder with "omega" in its name.</summary>
    private static IEnumerable<string> ScanDrives()
    {
        foreach (var drive in FixedDrives())
        {
            foreach (var level1 in SafeDirs(drive))
            {
                if (IsOmegaName(level1)) yield return level1;
                foreach (var level2 in SafeDirs(level1))
                    if (IsOmegaName(level2)) yield return level2;
            }
        }
    }

    private static bool IsOmegaName(string dir) => Path.GetFileName(dir).Contains("omega", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> FixedDrives()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
