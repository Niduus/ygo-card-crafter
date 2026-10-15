using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>Turns a card filter into card-text English, e.g. "Level 4 or lower Warrior monster from your Deck".</summary>
public static class FilterText
{
    public static bool IsFieldOnly(CardFilter f) => f.Zones != Zone.None && (f.Zones & ~Zone.Field) == Zone.None;

    /// <summary>", except "Card Name"" for filters that leave cards out, or "".</summary>
    public static string ExceptText(CardFilter f, CardPack? pack, string? ownName)
    {
        var except = new List<string>();
        if (f.ExceptOwnName) except.Add(ownName is { Length: > 0 } n ? $"\"{n}\"" : "cards with this card's name");
        if (f.ExceptCode != 0)
            except.Add(pack?.Cards.FirstOrDefault(c => c.Card.Id == f.ExceptCode)?.Card.Name is { Length: > 0 } en ? $"\"{en}\"" : $"passcode {f.ExceptCode}");
        return except.Count == 0 || f.ThisCard ? "" : ", except " + string.Join(" or ", except);
    }

    /// <param name="withExcept">False when the caller puts ", except ..." at the end of its own sentence, as cards print it.</param>
    private static string Lv(CardFilter f) => f.MonsterKind == MonsterKind.Xyz ? "Rank" : "Level";

    public static string Describe(CardFilter f, CardPack? pack, bool plural = false, string preposition = "from", bool withLocation = true, string? ownName = null, bool withExcept = true)
    {
        if (f.ThisCard) return "this card";
        var words = new List<string>();
        if (f.ExcludeThisCard) words.Add("other");
        if (f.FaceUpOnly) words.Add("face-up");
        // "non-Tuner WIND monsters": a "non-" kind comes first, before the Level and Attribute.
        if (f.CardCode == 0 && KindNoun(f.MonsterKind) is { Non: true } non) words.Add(non.Noun);
        if (f.LevelMin is { } lo && f.LevelMax is { } hi)
            words.Add(lo == hi ? $"{Lv(f)} {lo}" : $"{Lv(f)} {lo} to {hi}");
        else if (f.LevelMax is { } max) words.Add($"{Lv(f)} {max} or lower");
        else if (f.LevelMin is { } min) words.Add($"{Lv(f)} {min} or higher");
        if (f.Attribute != CardAttribute.None) words.Add(EnumNames.Display(f.Attribute));
        if (f.Race != CardRace.None) words.Add(pack?.TypeName(f.Race) ?? EnumNames.Display(f.Race));
        if (f.Archetype != 0) words.Add($"\"{pack?.ArchetypeName(f.Archetype) ?? $"0x{f.Archetype:x}"}\"");

        string noun;
        if (f.CardCode != 0)
        {
            var name = pack?.Cards.FirstOrDefault(c => c.Card.Id == f.CardCode)?.Card.Name;
            noun = name is null ? $"card with passcode {f.CardCode}" : $"\"{name}\"";
            words.Clear();
            if (f.ExcludeThisCard) words.Add("other");
        }
        else
        {
            noun = KindNoun(f.MonsterKind) is { Non: false } kind ? kind.Noun : f.CardType switch
            {
                FilterCardType.Monster => "monster",
                FilterCardType.Spell => "Spell",
                FilterCardType.Trap => "Trap",
                // Only the Monster Zones hold only monsters; only the Spell & Trap Zones, only Spells and Traps.
                _ when f.Zones == Zone.MonsterZone => "monster",
                _ when f.Zones == Zone.SpellTrapZone => "Spell/Trap",
                _ => (f.Race != CardRace.None || f.Attribute != CardAttribute.None || f.LevelMin is not null || f.LevelMax is not null
                      || f.MonsterKind != MonsterKind.Any) ? "monster" : "card",
            };
            if (plural) noun += "s";
        }
        words.Add(noun);

        if (f.AtkMin is { } amin && f.AtkMax is { } amax) words.Add($"with {amin} to {amax} ATK");
        else if (f.AtkMax is { } amx) words.Add($"with {amx} or less ATK");
        else if (f.AtkMin is { } amn) words.Add($"with {amn} or more ATK");

        if (withLocation)
        {
            var where = Location(f, preposition);
            if (where.Length > 0) words.Add(where);
        }
        var text = string.Join(" ", words);
        // "1 Warrior monster from your Deck, except "Card Name"".
        return withExcept ? text + ExceptText(f, pack, ownName) : text;
    }

    /// <summary>
    /// The card-text name of a monster kind: "Effect Monster", "Tuner", "Flip monster". "Non-" kinds give the word
    /// that goes before the rest ("non-Tuner") and the noun stays "monster".
    /// </summary>
    private static (string Noun, bool Non)? KindNoun(MonsterKind kind) => kind switch
    {
        MonsterKind.Effect => ("Effect Monster", false),
        MonsterKind.NonEffect => ("non-Effect", true),
        MonsterKind.Normal => ("Normal Monster", false),
        MonsterKind.Tuner => ("Tuner", false),
        MonsterKind.NonTuner => ("non-Tuner", true),
        MonsterKind.Fusion => ("Fusion Monster", false),
        MonsterKind.Synchro => ("Synchro Monster", false),
        MonsterKind.Xyz => ("Xyz Monster", false),
        MonsterKind.Link => ("Link Monster", false),
        MonsterKind.Ritual => ("Ritual Monster", false),
        MonsterKind.Pendulum => ("Pendulum Monster", false),
        MonsterKind.Flip => ("Flip monster", false),
        MonsterKind.Toon => ("Toon monster", false),
        MonsterKind.Spirit => ("Spirit monster", false),
        MonsterKind.Union => ("Union monster", false),
        MonsterKind.Gemini => ("Gemini monster", false),
        MonsterKind.Token => ("Token", false),
        MonsterKind.NonToken => ("non-Token", true),
        _ => null,
    };

    private static string Location(CardFilter f, string preposition)
    {
        if (f.Zones == Zone.None) return "";
        if (f.PointedToByThisCard)
        {
            return f.Owner switch
            {
                CardOwner.You => "you control that this card points to",
                CardOwner.Opponent => "your opponent controls that this card points to",
                _ => "this card points to",
            };
        }
        if (IsFieldOnly(f))
        {
            // Monsters on the field are always in a Monster Zone, so only Spell & Trap Zones need naming.
            if (f.Zones == Zone.SpellTrapZone && f.CardType == FilterCardType.Monster)
            {
                return f.Owner switch
                {
                    CardOwner.You => "in your Spell & Trap Zone",
                    CardOwner.Opponent => "in your opponent's Spell & Trap Zone",
                    _ => "in a Spell & Trap Zone",
                };
            }
            return f.Owner switch
            {
                CardOwner.You => "you control",
                CardOwner.Opponent => "your opponent controls",
                _ => "on the field",
            };
        }

        var owner = f.Owner switch
        {
            CardOwner.You => "your",
            CardOwner.Opponent => "your opponent's",
            _ => "either player's",
        };
        var places = new List<string>();
        if (f.Zones.HasFlag(Zone.Hand)) places.Add("hand");
        if (f.Zones.HasFlag(Zone.Deck)) places.Add("Deck");
        if (f.Zones.HasFlag(Zone.ExtraDeck)) places.Add("Extra Deck");
        if (f.Zones.HasFlag(Zone.Graveyard)) places.Add("GY");
        if (f.Zones.HasFlag(Zone.Banished)) places.Add("banished cards");
        if ((f.Zones & Zone.Field) != Zone.None) places.Add("field");
        var list = places.Count switch
        {
            1 => places[0],
            2 => $"{places[0]} or {places[1]}",
            _ => string.Join(", ", places.Take(places.Count - 1)) + ", or " + places[^1],
        };
        var prefix = preposition.Length == 0 ? "" : preposition + " ";
        return $"{prefix}{owner} {list}";
    }
}
