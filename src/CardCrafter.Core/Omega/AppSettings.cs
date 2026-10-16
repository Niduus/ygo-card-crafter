using System.Text.Json;

namespace CardCrafter.Core.Omega;

/// <summary>Per-user settings, stored in %AppData%\YGO Card Crafter\settings.json.</summary>
public sealed class AppSettings
{
    public string? OmegaPath { get; set; }
    public string? LastPackPath { get; set; }
    /// <summary>Folder of card-frame images for the preview, or null for the built-in drawing.</summary>
    public string? FrameFolder { get; set; }
    /// <summary>Look for a newer release on GitHub at startup.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Install the pack into YGO Omega every time it is saved.</summary>
    public bool InstallOnSave { get; set; }
    /// <summary>A release the user chose to skip ("Later"); it isn't offered again at startup.</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>"owner/repo" whose public GitHub Releases provide updates. Null uses Card Crafter's own.</summary>
    public string? UpdateRepository { get; set; }
    public List<string> RecentPacks { get; set; } = [];
    /// <summary>The Claude model the assistant uses, by id.</summary>
    public string? AssistantModel { get; set; }
    /// <summary>How hard the assistant thinks: low, medium, high, xhigh or max.</summary>
    public string AssistantEffort { get; set; } = "medium";

    public static string Folder
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "YGO Card Crafter");
        }
    }

    public static string FilePath => Path.Combine(Folder, "settings.json");
    public static string BackupFolder => Path.Combine(Folder, "Backups");
    public static string DefaultExportFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "YGO Card Crafter", "Exports");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save them must not break the app.
        }
    }

    public void AddRecent(string path)
    {
        RecentPacks.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentPacks.Insert(0, path);
        if (RecentPacks.Count > 10) RecentPacks.RemoveRange(10, RecentPacks.Count - 10);
        LastPackPath = path;
    }
}
