using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Combos;

/// <summary>A deck read from a file or a list: the deck, and what couldn't be matched to a card.</summary>
public sealed class DeckImportResult
{
    public ComboDeck Deck { get; } = new();
    /// <summary>Lines whose card wasn't found, as written ("3 Ash Blosom"), and passcodes no database has.</summary>
    public List<string> Unknown { get; } = [];
    public int Cards => Deck.Main.Count + Deck.Extra.Count + Deck.Side.Count;

    public string Summary =>
        $"{Deck.Main.Count} Main, {Deck.Extra.Count} Extra, {Deck.Side.Count} Side"
        + (Unknown.Count > 0 ? $"; not found: {string.Join(", ", Unknown)}" : "");
}

/// <summary>
/// Reads decks: .ydk files (#main, #extra, !side and a passcode per line), deck lists as people paste them
/// ("Monster", "Spell", "Trap", "Extra", "Side" with lines like "3 Ash Blossom &amp; Joyous Spring"), and a pack's own cards.
/// Names and passcodes are looked up in the catalog; what isn't found is listed instead of stopping the import.
/// </summary>
public static partial class DeckImport
{
    public static bool LooksLikeYdk(string text) =>
        text.Split('\n').Select(l => l.Trim()).Any(l => l.Equals("#main", StringComparison.OrdinalIgnoreCase) || l.Equals("!side", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads either format.</summary>
    public static DeckImportResult Read(string text, ComboCatalog catalog) => LooksLikeYdk(text) ? FromYdk(text, catalog) : FromText(text, catalog);

    public static DeckImportResult FromYdk(string text, ComboCatalog catalog)
    {
        var result = new DeckImportResult();
        var section = result.Deck.Main;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#') || line.StartsWith('!'))
            {
                var tag = line.ToLowerInvariant();
                if (tag.StartsWith("#main")) section = result.Deck.Main;
                else if (tag.StartsWith("#extra")) section = result.Deck.Extra;
                else if (tag.StartsWith("!side")) section = result.Deck.Side;
                continue;
            }
            if (LeadingNumber().Match(line) is not { Success: true } m || !long.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code)) continue;
            var canon = catalog.Canonical(code);
            if (catalog.Find(canon) is null && !result.Unknown.Contains(code.ToString(CultureInfo.InvariantCulture)))
                result.Unknown.Add(code.ToString(CultureInfo.InvariantCulture));
            // Unknown passcodes stay in the deck: the cards may just not be installed here.
            section.Add(canon);
        }
        return result;
    }

    public static DeckImportResult FromText(string text, ComboCatalog catalog)
    {
        var result = new DeckImportResult();
        var section = DeckSection.Main;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '•', '●').Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (Header().Match(line) is { Success: true } h)
            {
                var word = h.Groups[1].Value.ToLowerInvariant();
                section = word.StartsWith("extra") ? DeckSection.Extra : word.StartsWith("side") ? DeckSection.Side : DeckSection.Main;
                continue;
            }
            var (count, name, card) = ParseLine(line, catalog);
            if (card is null)
            {
                result.Unknown.Add(line);
                continue;
            }
            var list = section switch
            {
                DeckSection.Side => result.Deck.Side,
                DeckSection.Extra => result.Deck.Extra,
                // Lists that put Extra Deck monsters under "Monster" still get them into the Extra Deck.
                _ => card.Data.IsExtraDeck ? result.Deck.Extra : result.Deck.Main,
            };
            for (int i = 0; i < count; i++) list.Add(card.Id);
        }
        return result;
    }

    private enum DeckSection { Main, Extra, Side }

    /// <summary>"3 Ash Blossom", "3x Ash Blossom", "Ash Blossom x3", or just a name for one copy.</summary>
    private static (int Count, string Name, ComboCard? Card) ParseLine(string line, ComboCatalog catalog)
    {
        var candidates = new List<(int, string)>();
        if (CountFirst().Match(line) is { Success: true } a) candidates.Add((int.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture), a.Groups[2].Value));
        if (CountLast().Match(line) is { Success: true } b) candidates.Add((int.Parse(b.Groups[2].Value, CultureInfo.InvariantCulture), b.Groups[1].Value));
        candidates.Add((1, line));
        foreach (var (count, name) in candidates)
        {
            var n = name.Trim().Trim('"').Trim();
            var card = catalog.FindByName(n) ?? (long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? catalog.Find(code) : null);
            if (card is not null) return (Math.Clamp(count, 1, 60), n, card);
        }
        return (0, line, null);
    }

    /// <summary>The pack's cards, one copy each: Extra Deck monsters in the Extra Deck, Tokens left out.</summary>
    public static DeckImportResult FromPack(CardPack pack)
    {
        var result = new DeckImportResult();
        foreach (var c in pack.Cards)
        {
            if (c.Card.IsMonster && c.Card.Frame == MonsterFrame.Token) continue;
            (c.Card.IsExtraDeck ? result.Deck.Extra : result.Deck.Main).Add(c.Card.Id);
        }
        return result;
    }

    /// <summary>The deck as a .ydk file, which YGO Omega and other simulators read.</summary>
    public static string ToYdk(ComboDeck deck)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#created by YGO Card Crafter");
        sb.AppendLine("#main");
        foreach (var c in deck.Main) sb.AppendLine(c.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("#extra");
        foreach (var c in deck.Extra) sb.AppendLine(c.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("!side");
        foreach (var c in deck.Side) sb.AppendLine(c.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^(main|monsters?|spells?|traps?|extra|side)\b(\s*deck)?(\s*cards?)?\s*[:\-]?\s*[\(\[]?\s*\d*\s*(cards?)?[\)\]]?\s*:?$", RegexOptions.IgnoreCase)]
    private static partial Regex Header();

    [GeneratedRegex(@"^(\d{1,2})\s*[xX×]?\s+(.+)$")]
    private static partial Regex CountFirst();

    [GeneratedRegex(@"^(.+?)\s*[xX×]\s*(\d{1,2})$")]
    private static partial Regex CountLast();
}
