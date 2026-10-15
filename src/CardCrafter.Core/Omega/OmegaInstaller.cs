namespace CardCrafter.Core.Omega;

public sealed class InstallResult
{
    public List<string> Copied { get; } = [];
    public List<string> BackedUp { get; } = [];
    public string? BackupFolder { get; set; }
    /// <summary>True when Windows refused the copy, usually because Omega is under Program Files.</summary>
    public bool NeedsAdmin { get; set; }
    public string? Error { get; set; }
    public bool Success => Error is null && !NeedsAdmin;
}

/// <summary>Copies an exported pack into the Omega folders the scan found, backing up anything it replaces.</summary>
public static class OmegaInstaller
{
    public static InstallResult Install(ExportResult export, OmegaInventory inventory, string backupRoot)
    {
        var result = new InstallResult();
        if (!inventory.SupportsLooseFiles)
        {
            result.Error = "This Omega install has no loose script folder or readable card database, so there is nowhere safe to copy cards. " +
                           $"Your cards were exported to {export.OutputFolder}.";
            return result;
        }

        var plan = new List<(string From, string To)>
        {
            (export.DatabasePath, Path.Combine(inventory.BestDatabaseFolder!, Path.GetFileName(export.DatabasePath))),
        };
        foreach (var card in export.Cards.Where(c => !c.Skipped))
        {
            plan.Add((card.ScriptPath!, Path.Combine(inventory.BestScriptFolder!, Path.GetFileName(card.ScriptPath!))));
            if (card.ImagePath is not null && inventory.BestImageFolder is not null)
                plan.Add((card.ImagePath, Path.Combine(inventory.BestImageFolder, Path.GetFileName(card.ImagePath))));
        }

        // Never overwrite an official card database: our file name must not already be one of theirs.
        var dbTarget = plan[0].To;
        if (inventory.CardDatabases.Any(d => string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(dbTarget), StringComparison.OrdinalIgnoreCase)
                                             && !IsOurs(d.Path, export)))
        {
            result.Error = $"A database named {Path.GetFileName(dbTarget)} already exists in Omega and was not made by Card Crafter. Rename your pack and try again.";
            return result;
        }

        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var (from, to) in plan)
            {
                if (File.Exists(to))
                {
                    result.BackupFolder ??= Path.Combine(backupRoot, stamp);
                    var rel = Path.GetRelativePath(inventory.Root, to);
                    var backup = Path.Combine(result.BackupFolder, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(to, backup, overwrite: true);
                    result.BackedUp.Add(to);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: true);
                result.Copied.Add(to);
            }
        }
        catch (UnauthorizedAccessException)
        {
            result.NeedsAdmin = true;
        }
        catch (IOException ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    /// <summary>A database is ours when every card in it is one of this pack's cards.</summary>
    private static bool IsOurs(string path, ExportResult export)
    {
        try
        {
            var ids = export.Cards.Select(c => c.Card.Card.Id).ToHashSet();
            var rows = Database.CdbFile.Read(path);
            return rows.Count > 0 && rows.All(r => ids.Contains(r.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return false;
        }
    }
}
