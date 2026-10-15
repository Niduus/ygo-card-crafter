using System.Text;
using System.Text.RegularExpressions;
using CardCrafter.Core.Database;

namespace CardCrafter.Core.Omega;

public sealed record FolderCount(string Path, int Count);
public sealed record DatabaseInfo(string Path, int Cards, long Bytes);

/// <summary>
/// What is inside an Omega install that matters for custom cards: card databases, script folders and
/// card image folders. Found by looking at file contents and names, not by assuming a layout.
/// </summary>
public sealed partial class OmegaInventory
{
    public required string Root { get; init; }
    public List<DatabaseInfo> CardDatabases { get; } = [];
    public List<string> OtherSqliteFiles { get; } = [];
    public List<FolderCount> ScriptFolders { get; } = [];
    public List<string> EngineLibraries { get; } = [];
    public List<FolderCount> ImageFolders { get; } = [];
    public Dictionary<string, int> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int FilesScanned { get; private set; }
    public bool Truncated { get; private set; }
    public bool RootWritable { get; private set; }

    /// <summary>Where Card Crafter would install files, from what the scan found. Null parts were not found.</summary>
    public string? BestScriptFolder =>
        EngineLibraries.Select(Path.GetDirectoryName).FirstOrDefault(d => d is not null)
        ?? ScriptFolders.OrderByDescending(s => s.Count).FirstOrDefault()?.Path;

    public string? BestDatabaseFolder =>
        CardDatabases.OrderByDescending(d => d.Cards).Select(d => Path.GetDirectoryName(d.Path)).FirstOrDefault();

    public string? BestImageFolder => ImageFolders.OrderByDescending(i => i.Count).FirstOrDefault()?.Path;

    public bool SupportsLooseFiles => BestScriptFolder is not null && BestDatabaseFolder is not null;

    [GeneratedRegex(@"^c\d+\.lua$", RegexOptions.IgnoreCase)]
    private static partial Regex CardScript();

    [GeneratedRegex(@"^\d+\.(jpg|jpeg|png)$", RegexOptions.IgnoreCase)]
    private static partial Regex CardImage();

    private static readonly HashSet<string> EngineLibraryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "constant.lua", "utility.lua", "procedure.lua",
    };

    public static OmegaInventory Scan(string root, int maxFiles = 400_000, int maxDepth = 10)
    {
        var inv = new OmegaInventory { Root = Path.GetFullPath(root) };
        inv.RootWritable = CanWrite(inv.Root);
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((inv.Root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            int scripts = 0, images = 0;
            foreach (var file in files)
            {
                if (++inv.FilesScanned > maxFiles) { inv.Truncated = true; return inv; }
                var name = Path.GetFileName(file);
                var ext = Path.GetExtension(file);
                inv.Extensions[ext.Length == 0 ? "(none)" : ext] = inv.Extensions.GetValueOrDefault(ext.Length == 0 ? "(none)" : ext) + 1;
                if (CardScript().IsMatch(name)) scripts++;
                else if (CardImage().IsMatch(name)) images++;
                else if (EngineLibraryNames.Contains(name)) inv.EngineLibraries.Add(file);

                if (ext.Equals(".cdb", StringComparison.OrdinalIgnoreCase) || ext.Equals(".db", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".sqlite", StringComparison.OrdinalIgnoreCase) || ext.Length == 0)
                {
                    if (CdbFile.IsCardDatabase(file, out var count))
                        inv.CardDatabases.Add(new DatabaseInfo(file, count, new FileInfo(file).Length));
                    else if (CdbFile.LooksLikeSqlite(file))
                        inv.OtherSqliteFiles.Add(file);
                }
            }
            if (scripts > 0) inv.ScriptFolders.Add(new FolderCount(dir, scripts));
            if (images >= 20) inv.ImageFolders.Add(new FolderCount(dir, images));

            if (depth >= maxDepth) continue;
            try
            {
                foreach (var sub in Directory.GetDirectories(dir)) stack.Push((sub, depth + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return inv;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".cardcrafter-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A plain-text report users can paste into Discord or a bug report.</summary>
    public string Report()
    {
        string Rel(string p) => Path.GetRelativePath(Root, p);
        var sb = new StringBuilder();
        sb.AppendLine("YGO Card Crafter - Omega folder scan");
        sb.AppendLine($"Folder: {Root}");
        sb.AppendLine($"Files scanned: {FilesScanned}{(Truncated ? " (stopped early, folder is very large)" : "")}");
        sb.AppendLine($"Can write here without admin: {(RootWritable ? "yes" : "no")}");
        sb.AppendLine();
        sb.AppendLine($"Card databases ({CardDatabases.Count}):");
        foreach (var d in CardDatabases) sb.AppendLine($"  {Rel(d.Path)}  [{d.Cards} cards, {d.Bytes / 1024} KB]");
        if (OtherSqliteFiles.Count > 0)
        {
            sb.AppendLine($"Other SQLite files ({OtherSqliteFiles.Count}):");
            foreach (var f in OtherSqliteFiles.Take(20)) sb.AppendLine($"  {Rel(f)}");
        }
        sb.AppendLine($"Card script folders ({ScriptFolders.Count}):");
        foreach (var s in ScriptFolders.OrderByDescending(s => s.Count).Take(20)) sb.AppendLine($"  {Rel(s.Path)}  [{s.Count} scripts]");
        sb.AppendLine($"Engine libraries ({EngineLibraries.Count}):");
        foreach (var l in EngineLibraries) sb.AppendLine($"  {Rel(l)}");
        sb.AppendLine($"Card image folders ({ImageFolders.Count}):");
        foreach (var i in ImageFolders.OrderByDescending(i => i.Count).Take(20)) sb.AppendLine($"  {Rel(i.Path)}  [{i.Count} images]");
        sb.AppendLine("Most common file types:");
        foreach (var kv in Extensions.OrderByDescending(kv => kv.Value).Take(15)) sb.AppendLine($"  {kv.Key}: {kv.Value}");
        sb.AppendLine();
        sb.AppendLine(SupportsLooseFiles
            ? "Result: found a script folder and a card database. Card Crafter can install custom cards here."
            : "Result: no loose card scripts or readable card database were found. Omega may pack its card data, " +
              "so Card Crafter will export to a folder instead. Please share this report so support can be added.");
        return sb.ToString();
    }
}
