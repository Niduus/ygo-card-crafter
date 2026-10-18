using CardCrafter.Core.Database;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Omega;

public sealed class DatabaseOpenResult
{
    public required CardPack Pack { get; init; }
    /// <summary>True when the database was saved by Card Crafter, so the event sheets came back too.</summary>
    public bool HadCardCrafterData { get; init; }
    public int Scripts { get; init; }
    public int Arts { get; init; }
    public required string ScriptFolder { get; init; }
    public string? ArtFolder { get; init; }
}

public sealed class DatabaseSaveResult
{
    public List<string> Written { get; } = [];
    public List<string> BackedUp { get; } = [];
    public string? BackupFolder { get; set; }
    public string? ScriptFolder { get; set; }
    public string? ArtFolder { get; set; }
    /// <summary>Cards with errors whose scripts were not written.</summary>
    public List<string> Skipped { get; } = [];
    public bool NeedsAdmin { get; set; }
    public string? Error { get; set; }
    public bool Success => Error is null && !NeedsAdmin;
}

/// <summary>
/// Opens a card database (.db or .cdb) as a pack and saves a pack back into one, together with its
/// scripts and art in the folders that go with the database:
/// YGO Omega's <c>Files\Databases\x.db</c> uses <c>Files\Scripts</c> and <c>Files\Arts</c>;
/// the classic layout uses <c>script</c> and <c>pics</c> next to the database.
/// </summary>
public static class DatabasePack
{
    public static bool IsDatabasePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".db" or ".cdb";

    /// <summary>The script folder for a database: the first one that exists, else where one would go.</summary>
    public static string ScriptFolderFor(string dbPath, OmegaInventory? omega = null) =>
        Candidates(dbPath, omega, "Scripts", "script").FirstOrDefault(Directory.Exists) ?? DefaultFolder(dbPath, omega, "Scripts", "script");

    /// <summary>The art folder for a database: the first one that exists, else where one would go.</summary>
    public static string ArtFolderFor(string dbPath, OmegaInventory? omega = null) =>
        Candidates(dbPath, omega, "Arts", "pics").FirstOrDefault(Directory.Exists) ?? DefaultFolder(dbPath, omega, "Arts", "pics");

    /// <summary>True when the database sits in Omega's own custom database folder.</summary>
    public static bool IsInOmega(string dbPath, OmegaInventory? omega) =>
        omega?.BestDatabaseFolder is { } folder && SamePath(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, folder);

    private static IEnumerable<string> Candidates(string dbPath, OmegaInventory? omega, string omegaName, string classicName)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
        if (IsInOmega(dbPath, omega))
        {
            var best = omegaName == "Scripts" ? omega!.BestScriptFolder : omega!.BestImageFolder;
            if (best is not null) yield return best;
        }
        // Omega: Files\Databases\x.db next to Files\Scripts and Files\Arts.
        if (Path.GetFileName(dir).Equals("Databases", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(dir) is { } parent)
            yield return Path.Combine(parent, omegaName);
        yield return Path.Combine(dir, classicName);
        yield return Path.Combine(dir, omegaName);
    }

    private static string DefaultFolder(string dbPath, OmegaInventory? omega, string omegaName, string classicName)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
        if (Path.GetFileName(dir).Equals("Databases", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(dir) is { } parent)
            return Path.Combine(parent, omegaName);
        return Path.Combine(dir, classicName);
    }

    public static DatabaseOpenResult Open(string dbPath, OmegaInventory? omega = null)
    {
        var rows = CdbFile.Read(dbPath);
        var scriptFolder = ScriptFolderFor(dbPath, omega);
        var artFolder = ArtFolderFor(dbPath, omega);

        CardPack? saved = null;
        try
        {
            if (CdbFile.ReadPackJson(dbPath) is { Length: > 0 } json) saved = PackSerializer.FromJson(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            // Damaged Card Crafter data: fall back to reading the cards like any other database.
        }

        var pack = new CardPack { Name = saved?.Name ?? Path.GetFileNameWithoutExtension(dbPath) };
        if (saved is not null)
        {
            pack.Author = saved.Author;
            pack.Ot = saved.Ot;
            pack.Archetypes = saved.Archetypes;
            pack.CustomTypes = saved.CustomTypes;
            pack.Combos = saved.Combos;
        }
        else if (rows.Count > 0)
        {
            // Use the most common "ot" as the pack's, so only unusual cards keep their own.
            pack.Ot = (int)rows.GroupBy(r => r.Ot).OrderByDescending(g => g.Count()).First().Key;
        }

        int scripts = 0, arts = 0;
        var savedById = saved?.Cards.GroupBy(c => c.Card.Id).ToDictionary(g => g.Key, g => g.First()) ?? [];
        foreach (var row in rows)
        {
            // Cards Card Crafter saved come back with their event sheets; anything else is read from the row.
            if (!savedById.TryGetValue(row.Id, out var project))
            {
                project = new CardProject { Card = CdbEncoder.Decode(row) };
                if (project.Card.Ot == pack.Ot) project.Card.Ot = null;
                project.ScriptStrings = row.Strings.ToList();
                while (project.ScriptStrings.Count > 0 && project.ScriptStrings[^1].Length == 0) project.ScriptStrings.RemoveAt(project.ScriptStrings.Count - 1);
                var lua = Path.Combine(scriptFolder, $"c{row.Id}.lua");
                if (File.Exists(lua))
                {
                    project.UseCustomLua = true;
                    project.CustomLua = File.ReadAllText(lua);
                    scripts++;
                }
            }
            if (PackExporter.ResolveArtwork(project.ArtworkPath, null) is null && FindArt(artFolder, row.Id) is { } art)
            {
                project.ArtworkPath = art;
                arts++;
            }
            pack.Cards.Add(project);
        }

        return new DatabaseOpenResult
        {
            Pack = pack, HadCardCrafterData = saved is not null, Scripts = scripts, Arts = arts,
            ScriptFolder = scriptFolder, ArtFolder = Directory.Exists(artFolder) ? artFolder : null,
        };
    }

    private static string? FindArt(string folder, long id)
    {
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png" })
        {
            var p = Path.Combine(folder, id + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Saves the pack into a database, replacing its cards, and writes each card's script and art into the
    /// database's folders. Every file it replaces is backed up first. Omega's own card database is never touched.
    /// </summary>
    public static DatabaseSaveResult Save(CardPack pack, string dbPath, string? packFolder, string backupRoot, OmegaInventory? omega = null)
    {
        var result = new DatabaseSaveResult();
        dbPath = Path.GetFullPath(dbPath);
        if (File.Exists(dbPath) && CdbFile.IsCardDatabase(dbPath, out var count) && count >= OmegaInventory.OfficialDatabaseMinCards)
        {
            result.Error = $"{Path.GetFileName(dbPath)} holds {count} cards: it is the game's own card database. Save your cards to a database of their own.";
            return result;
        }

        // Keep the database's layout: Omega's own inside Omega, otherwise whatever layout the file already has.
        CdbSchema? schema = null;
        try
        {
            schema = IsInOmega(dbPath, omega) && omega!.GameSchema is { } game ? game : File.Exists(dbPath) ? CdbSchema.Read(dbPath) : null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }

        var temp = Path.Combine(Path.GetTempPath(), "CardCrafter-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var export = PackExporter.Export(pack, temp, packFolder, schema);
            var scriptFolder = ScriptFolderFor(dbPath, omega);
            var artFolder = ArtFolderFor(dbPath, omega);
            result.ScriptFolder = scriptFolder;

            // The saved pack points at the art where it was installed, so it opens with its art anywhere.
            var stored = PackSerializer.Clone(pack);
            var plan = new List<(string From, string To)> { (export.DatabasePath, dbPath) };
            foreach (var card in export.Cards)
            {
                if (card.Skipped)
                {
                    result.Skipped.Add($"{card.Card.Card.Id} {card.Card.Card.Name}");
                    continue;
                }
                var script = Path.Combine(scriptFolder, Path.GetFileName(card.ScriptPath!));
                // Never replace a script with an empty one: a card with nothing to compile only gets one if it has none.
                bool hasScript = card.Card.UseCustomLua || card.Card.Effects.Count > 0 || card.Card.Procedure.Kind != SummonProcedureKind.None
                                 || card.Card.Card.SummonRule != SummonRule.Normal;
                if (hasScript || !File.Exists(script)) plan.Add((card.ScriptPath!, script));
                if (card.ImagePath is not null)
                {
                    var art = Path.Combine(artFolder, Path.GetFileName(card.ImagePath));
                    plan.Add((card.ImagePath, art));
                    result.ArtFolder = artFolder;
                    stored.Cards.First(c => c.Card.Id == card.Card.Card.Id).ArtworkPath = art;
                }
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var (from, to) in plan)
            {
                if (File.Exists(to))
                {
                    result.BackupFolder ??= Path.Combine(backupRoot, stamp);
                    var backup = Path.Combine(result.BackupFolder, Path.GetFileName(Path.GetDirectoryName(to)!), Path.GetFileName(to));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(to, backup, overwrite: true);
                    result.BackedUp.Add(to);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: true);
                result.Written.Add(to);
            }
            CdbFile.WritePackJson(dbPath, PackSerializer.ToJson(stored));
        }
        catch (UnauthorizedAccessException)
        {
            result.NeedsAdmin = true;
        }
        catch (IOException ex)
        {
            result.Error = ex.Message;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
