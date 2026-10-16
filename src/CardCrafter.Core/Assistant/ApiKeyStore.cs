using System.Security.Cryptography;
using System.Text;
using CardCrafter.Core.Omega;

namespace CardCrafter.Core.Assistant;

/// <summary>
/// Keeps the user's API keys on this computer only, one per provider, apart from the settings and never in a pack. On
/// Windows they are encrypted for the signed-in user (DPAPI); elsewhere the files can only be read by the user.
/// </summary>
public static class ApiKeyStore
{
    /// <summary>The file for a provider's key; Anthropic's keeps the name it had before there were other providers.</summary>
    public static string FilePath(string provider = "anthropic") =>
        Path.Combine(AppSettings.Folder, provider == "anthropic" ? "assistant.key" : $"assistant-{provider}.key");

    public static string? Load(string provider = "anthropic")
    {
        var path = FilePath(provider);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    public static bool Save(string key, string provider = "anthropic")
    {
        var path = FilePath(provider);
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var bytes = Encoding.UTF8.GetBytes(key);
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    public static void Forget(string provider = "anthropic")
    {
        var path = FilePath(provider);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to do: the key just stays where it was.
        }
    }
}
