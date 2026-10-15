using System.Runtime.InteropServices;
using CardCrafter.Core.Omega;

namespace CardCrafter.App;

/// <summary>Writes unexpected errors to %AppData%\YGO Card Crafter\crash.log so a crash never disappears silently.</summary>
public static class CrashLog
{
    public static string FilePath => Path.Combine(AppSettings.Folder, "crash.log");

    public static void Write(string where, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(FilePath,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {where}  (YGO Card Crafter {typeof(CrashLog).Assembly.GetName().Version})\n{ex}\n\n");
        }
        catch (Exception)
        {
            // Nowhere left to report to.
        }
    }

    /// <summary>A plain Windows message box, used when the app can't even show its own window.</summary>
    public static void ShowFatal(string message)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { MessageBoxW(IntPtr.Zero, message, "YGO Card Crafter", 0x10); }
        catch (Exception) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
