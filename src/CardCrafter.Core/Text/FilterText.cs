using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>Turns a card filter into card-text English, e.g. "Level 4 or lower Warrior monster from your Deck".</summary>
public static class FilterText
{
    public static bool IsFieldOnly(CardFilter f) => f.Zones != Zone.None && (f.Zones & ~Zone.Field) == Zone.None;

    public static string Describe(CardFilter f, CardPack? pack, bool plural = false, string preposition = "from", bool withLocation = true)
    {
        var words = new List<string>();
        if (f.ExcludeThisCard) words.Add("other");
        if (f.FaceUpOnly) words.Add("face-up");
        if (f.LevelMin is { } lo && f.LevelMax is { } hi)
            words.Add(lo == hi ? $"Level {lo}" : $"Level {lo} to {hi}");
        else if (f.LevelMax is { } max) words.Add($"Level {max} or lower");
        else if (f.LevelMin is { } min) words.Add($"Level {min} or higher");
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
            noun = f.CardType switch
            {
                FilterCardType.Monster => "monster",
                FilterCardType.Spell => "Spell",
                FilterCardType.Trap => "Trap",
                _ => (f.Race != CardRace.None || f.Attribute != CardAttribute.None || f.LevelMin is not null || f.LevelMax is not null) ? "monster" : "card",
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
        return string.Join(" ", words);
    }

    private static string Location(CardFilter f, string preposition)
    {
        if (f.Zones == Zone.None) return "";
        if (IsFieldOnly(f))
        {
            // Monsters on the field are always in a Monster Zone, so only Spell & Trap Zones need naming.
            if (f.Zones == Zone.SpellTrapZone)
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
