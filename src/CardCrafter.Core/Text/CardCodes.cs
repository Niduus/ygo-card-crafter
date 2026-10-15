using System.Globalization;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>One number a card is stored with, as a database or a script uses it: "Passcode 46986414", "Type 0x2 (Spellcaster)".</summary>
/// <param name="Value">What gets copied: "46986414", "0x2".</param>
/// <param name="Note">What it means, or "".</param>
public sealed record CardCode(string Label, string Value, string Note);

/// <summary>The codes behind a card: its passcode, card type bits, Type, Attribute and archetypes.</summary>
public static class CardCodes
{
    private static string Hex(long v) => "0x" + v.ToString("x", CultureInfo.InvariantCulture);

    public static List<CardCode> Of(CardData card, CardPack? pack = null)
    {
        var row = CdbEncoder.Encode(card, [], card.Ot ?? 0);
        var codes = new List<CardCode>
        {
            new("Passcode", card.Id.ToString(CultureInfo.InvariantCulture), ""),
            new("Card type", Hex(row.Type), card.IsMonster ? $"{card.Frame} Monster" : card.Kind.ToString()),
        };
        if (card.Alias != 0) codes.Add(new("Alias of", card.Alias.ToString(CultureInfo.InvariantCulture), CardNames.Find(card.Alias, pack) ?? ""));
        if (card.IsMonster)
        {
            codes.Add(new("Type", Hex(row.Race), pack?.TypeName(card.Race) ?? EnumNames.Display(card.Race)));
            codes.Add(new("Attribute", Hex(row.Attribute), EnumNames.Display(card.Attribute)));
        }
        foreach (var a in card.Archetypes.Where(a => a != 0))
            codes.Add(new("Archetype", Hex(a), (pack is not null ? pack.ArchetypeName(a) : ArchetypeNames.Find(a)) is { } an && !an.StartsWith("0x", StringComparison.Ordinal) ? an : ""));
        if (card.Archetypes.Count(a => a != 0) > 1) codes.Add(new("Setcode (all)", Hex(row.Setcode), "the archetypes packed together, as the database stores them"));
        return codes;
    }
}
