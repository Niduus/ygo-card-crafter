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
    /// <summary>
    /// Converts an image to JPEG (source path, destination path; false if it can't). YGO Omega keeps card art as .jpg,
    /// so PNG and other art is converted on export. Set by the app, which has an image library; null copies as is.
    /// </summary>
    public static Func<string, string, bool>? ConvertToJpeg { get; set; }

    /// <param name="schema">The database layout to write; YGO Omega's own when installing into Omega, ygopro's otherwise.</param>
    public static ExportResult Export(CardPack pack, string outputFolder, string? packFolder = null, CdbSchema? schema = null)
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
            // A card whose text was never written gets its suggested text, so it never shows up blank in the game.
            var data = card.Card;
            if (string.IsNullOrWhiteSpace(data.Text) && Text.EffectTextGenerator.Generate(card, pack) is { Length: > 0 } suggested)
            {
                data = PackSerializer.Clone(data);
                data.Text = suggested;
            }
            rows.Add(CdbEncoder.Encode(data, compiled.Strings, pack.Ot));

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
                var ext = Path.GetExtension(art).ToLowerInvariant();
                var jpg = Path.Combine(picsDir, card.Card.Id + ".jpg");
                if (ext is ".jpg" or ".jpeg") File.Copy(art, jpg, overwrite: true);
                else if (ConvertToJpeg is null || !ConvertToJpeg(art, jpg))
                {
                    jpg = Path.Combine(picsDir, card.Card.Id + ext);
                    File.Copy(art, jpg, overwrite: true);
                    export.Checks.Add(new Diagnostic(Severity.Warning, $"Art for \"{card.Card.Name}\" is {ext}; YGO Omega expects .jpg."));
                }
                export.ImagePath = jpg;
            }
        }
        CdbFile.WriteFresh(result.DatabasePath, rows, schema);
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
