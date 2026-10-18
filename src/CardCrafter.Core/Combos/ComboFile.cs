using System.Text.Json;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Combos;

/// <summary>One combo on its own, as a .yccombo file to share: the same JSON a pack keeps, with the card names written in.</summary>
public static class ComboFile
{
    public const string Extension = ".yccombo";
    public const int CurrentFormat = 1;

    private sealed class Envelope
    {
        public int FormatVersion { get; set; } = CurrentFormat;
        public string App { get; set; } = "YGO Card Crafter";
        public Combo? Combo { get; set; }
    }

    public static string ToJson(Combo combo, ComboCatalog? catalog = null)
    {
        var copy = PackSerializer.Clone(combo);
        RememberNames(copy, catalog);
        return JsonSerializer.Serialize(new Envelope { Combo = copy }, PackSerializer.Options);
    }

    public static Combo FromJson(string json)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(json, PackSerializer.Options); }
        catch (JsonException ex) { throw new InvalidDataException($"This isn't a combo file: {ex.Message}"); }
        if (envelope?.Combo is not { } combo) throw new InvalidDataException("This file has no combo in it.");
        if (envelope.FormatVersion > CurrentFormat) throw new InvalidDataException("This combo was saved by a newer version of Card Crafter.");
        return combo;
    }

    public static void Save(Combo combo, string path, ComboCatalog? catalog = null) => File.WriteAllText(path, ToJson(combo, catalog));

    public static Combo Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>Every card the combo uses, so it still reads right where the catalog doesn't have them.</summary>
    public static IEnumerable<long> Codes(Combo combo) =>
        combo.Deck.Main.Concat(combo.Deck.Extra).Concat(combo.Deck.Side).Concat(combo.StartingHand)
            .Concat(combo.Steps.SelectMany(s => s.Materials.Select(m => m.Code).Prepend(s.Code)))
            .Where(c => c != 0).Distinct();

    /// <summary>Writes the names the catalog knows into <see cref="Combo.Names"/>, keeping remembered ones it doesn't know, and drops names no longer used.</summary>
    public static void RememberNames(Combo combo, ComboCatalog? catalog)
    {
        var names = new Dictionary<long, string>();
        foreach (var code in Codes(combo))
        {
            if (catalog?.Find(code) is { } card) names[code] = card.Name;
            else if (combo.Names.TryGetValue(code, out var known)) names[code] = known;
        }
        combo.Names = names;
    }
}
