using System.Globalization;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>Small helpers that turn model values into Lua expressions.</summary>
public static class Lua
{
    private static readonly (Zone Zone, string Name)[] ZoneNames =
    [
        (Zone.Deck, "LOCATION_DECK"),
        (Zone.Hand, "LOCATION_HAND"),
        (Zone.MonsterZone, "LOCATION_MZONE"),
        (Zone.SpellTrapZone, "LOCATION_SZONE"),
        (Zone.Graveyard, "LOCATION_GRAVE"),
        (Zone.Banished, "LOCATION_REMOVED"),
        (Zone.ExtraDeck, "LOCATION_EXTRA"),
        (Zone.PendulumZone, "LOCATION_PZONE"),
    ];

    /// <summary>LOCATION_ constants joined with +, or "0" when empty.</summary>
    public static string Locations(Zone zones)
    {
        var parts = new List<string>();
        var rest = zones;
        if ((rest & Zone.Field) == Zone.Field)
        {
            parts.Add("LOCATION_ONFIELD");
            rest &= ~Zone.Field;
        }
        // The Pendulum Zones are part of the Spell & Trap Zones.
        if (zones.HasFlag(Zone.SpellTrapZone)) rest &= ~Zone.PendulumZone;
        foreach (var (zone, name) in ZoneNames)
            if (rest.HasFlag(zone)) parts.Add(name);
        // Keep a stable, readable order: on-field first, then the rest as listed.
        return parts.Count == 0 ? "0" : string.Join("+", parts);
    }

    /// <summary>The (your side, opponent's side) location pair the engine's search functions take.</summary>
    public static (string Self, string Opp) Range(CardFilter f)
    {
        var loc = Locations(f.Zones);
        return f.Owner switch
        {
            CardOwner.You => (loc, "0"),
            CardOwner.Opponent => ("0", loc),
            _ => (loc, loc),
        };
    }

    public static string Player(CardOwner owner) => owner switch
    {
        CardOwner.You => "tp",
        CardOwner.Opponent => "1-tp",
        _ => "PLAYER_ALL",
    };

    public static string Hex(long value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);

    public static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Text flattened to one line so it is safe inside a -- comment.</summary>
    public static string Comment(string text) =>
        string.Join(" ", text.Split('\r', '\n', StringSplitOptions.RemoveEmptyEntries)).Trim();

    public static string Join(IEnumerable<string> parts, string op = "+")
    {
        var list = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        return list.Count == 0 ? "0" : string.Join(op, list);
    }
}
