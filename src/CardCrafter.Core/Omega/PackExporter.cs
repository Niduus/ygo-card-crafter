using CardCrafter.Core.Compiler;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Validation;

namespace CardCrafter.Core.Omega;

public sealed class CardExport
{
    public required CardProject Card { get; init; }
    public required CompileResult Compile { get; init; }
    public List<Diagnostic> Checks { get; } = [];
    public string? ScriptPath { get; set; }
    public string? ImagePath { get; set; }
    public bool Skipped => ScriptPath is null;
}

public sealed class ExportResult
{
    public required string OutputFolder { get; init; }
    public required string DatabasePath { get; init; }
    public List<CardExport> Cards { get; } = [];
    public int Written => Cards.Count(c => !c.Skipped);
}

/// <summary>
/// Writes a pack in the standard ygopro layout:
/// <c>{name}.cdb</c>, <c>script/c{id}.lua</c> and <c>pics/{id}.jpg</c>.
/// </summary>
public static class PackExporter
{
    public static ExportResult Export(CardPack pack, string outputFolder, string? packFolder = null)
    {
        Directory.CreateDirectory(outputFolder);
        var scriptDir = Path.Combine(outputFolder, "script");
        var picsDir = Path.Combine(outputFolder, "pics");
        Directory.CreateDirectory(scriptDir);

        var result = new ExportResult
        {
            OutputFolder = Path.GetFullPath(outputFolder),
            DatabasePath = Path.Combine(outputFolder, SafeFileName(pack.Name) + ".cdb"),
        };

        var rows = new List<CdbRow>();
        foreach (var card in pack.Cards)
        {
            var compiled = ScriptCompiler.Compile(card, pack);
            var export = new CardExport { Card = card, Compile = compiled };
            result.Cards.Add(export);
            rows.Add(CdbEncoder.Encode(card.Card, compiled.Strings, pack.Ot));

            export.Checks.AddRange(ScriptChecker.Check(compiled.Lua));
            if (compiled.HasErrors || export.Checks.Any(c => c.Severity == Severity.Error))
                continue; // Never write a script the game would fail to load.

            var scriptPath = Path.Combine(scriptDir, $"c{card.Card.Id}.lua");
            File.WriteAllText(scriptPath, compiled.Lua.Replace("\r\n", "\n"));
            export.ScriptPath = scriptPath;

            var art = ResolveArtwork(card.ArtworkPath, packFolder);
            if (art is not null)
            {
                Directory.CreateDirectory(picsDir);
                var ext = Path.GetExtension(art).ToLowerInvariant() == ".png" ? ".png" : ".jpg";
                var dest = Path.Combine(picsDir, card.Card.Id + ext);
                File.Copy(art, dest, overwrite: true);
                export.ImagePath = dest;
            }
        }
        CdbFile.WriteFresh(result.DatabasePath, rows);
        return result;
    }

    public static string? ResolveArtwork(string? path, string? packFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.IsPathRooted(path) || packFolder is null ? path : Path.Combine(packFolder, path);
        return File.Exists(full) ? full : null;
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "custom-cards" : clean;
    }
}
