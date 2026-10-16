using System.Security.Cryptography;
using System.Text;
using CardCrafter.Core.Omega;

namespace CardCrafter.Core.Assistant;

/// <summary>
/// Keeps the user's API key on this computer only, apart from the settings and never in a pack. On Windows it is
/// encrypted for the signed-in user (DPAPI); elsewhere the file can only be read by the user.
/// </summary>
public static class ApiKeyStore
{
    public static string FilePath => Path.Combine(AppSettings.Folder, "assistant.key");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bytes = File.ReadAllBytes(FilePath);
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    public static bool Save(string key)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var bytes = Encoding.UTF8.GetBytes(key);
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    public static void Forget()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to do: the key just stays where it was.
        }
    }
}
