namespace CardCrafter.Core.Model;

/// <summary>
/// Random codes for a pack's own archetypes and Types, and changing a code everywhere the pack uses it: the cards
/// that belong to an archetype or are of a Type, and every card filter that looks for them.
/// </summary>
public static class PackCodes
{
    /// <summary>
    /// A random archetype code from 0x100 to 0xfff whose archetype (the low 12 bits) nothing in <paramref name="taken"/>
    /// uses. Random so that packs made by different people are unlikely to pick the same code; 0 when all are taken.
    /// </summary>
    public static int RandomArchetypeCode(IEnumerable<int> taken, Random? rng = null)
    {
        var used = taken.Select(c => c & 0xfff).ToHashSet();
        var free = Enumerable.Range(0x100, 0xf00).Where(c => !used.Contains(c)).ToList();
        return free.Count == 0 ? 0 : free[(rng ?? Random.Shared).Next(free.Count)];
    }

    /// <summary>
    /// A random Type value: one bit no standard Type, nothing in <paramref name="taken"/> and no other bit of a taken
    /// value uses, within the 32 bits the game keeps (above them only when those are all taken); 0 when none is left.
    /// </summary>
    public static long RandomTypeValue(IEnumerable<long> taken, Random? rng = null)
    {
        var used = taken.Aggregate(EnumNames.StandardRaceMask, (m, v) => m | v);
        var free = Enumerable.Range(0, 32).Select(b => 1L << b).Where(v => (v & used) == 0).ToList();
        if (free.Count == 0) free = Enumerable.Range(32, 31).Select(b => 1L << b).Where(v => (v & used) == 0).ToList();
        return free.Count == 0 ? 0 : free[(rng ?? Random.Shared).Next(free.Count)];
    }

    /// <summary>
    /// What YGO Omega prints on the Type line for a Type value: the names of the Types it knows (the standard ones and
    /// the Rush Duel ones up to Galaxy, 0x80000000), or "None" when it knows none of its bits. Omega has no way to be
    /// given new Type names.
    /// </summary>
    public static string OmegaShows(long value)
    {
        var names = new List<string>();
        for (var bit = 0; bit < 32; bit++)
        {
            var v = 1L << bit;
            if ((value & v) == 0) continue;
            if ((v & EnumNames.StandardRaceMask) != 0) names.Add(EnumNames.Display((CardRace)v));
            else if (Omega.UnknownType.LikelyNames.TryGetValue(v, out var rush)) names.Add(rush);
        }
        return names.Count == 0 ? "None" : string.Join(", ", names);
    }

    /// <summary>
    /// Why a custom Type won't read as <paramref name="name"/> in YGO Omega, or null when it is a value nothing else
    /// uses that Omega can't name either way. A card's Type is a set of bits, so a value holding standard Types' bits
    /// (0x100000230 has 0x10, 0x20 and 0x200) shows as those Types.
    /// </summary>
    public static string? TypeValueProblem(long value, string? name = null)
    {
        var standard = value & EnumNames.StandardRaceMask;
        var called = string.IsNullOrWhiteSpace(name) ? "the custom Type" : $"\"{name}\"";
        if (standard != 0)
        {
            var names = Enum.GetValues<CardRace>().Where(r => r != CardRace.None && (standard & (long)r) != 0).Select(EnumNames.Display).ToList();
            return $"0x{value:x} contains the bits of {string.Join(", ", names)} (0x{standard:x}), so YGO Omega shows the card as {(names.Count == 1 ? "that Type" : "those Types")} " +
                   $"and treats it as {(names.Count == 1 ? "that Type" : "those Types")} in duels. Click 🎲 for a value without them.";
        }
        if (OmegaShows(value) is var shown && shown != "None")
            return $"YGO Omega shows 0x{value:x} as {shown}, its own Rush Duel Type, not as {called}.";
        return null;
    }

    /// <summary>Changes archetype <paramref name="from"/> to <paramref name="to"/> on every card and filter; how many changed.</summary>
    public static int ChangeArchetype(CardPack pack, int from, int to)
    {
        if (from == to || from == 0) return 0;
        var n = 0;
        foreach (var card in pack.Cards)
        {
            var list = card.Card.Archetypes;
            for (var i = 0; i < list.Count; i++)
                if (list[i] == from) { list[i] = to; n++; }
            foreach (var f in PasscodeFix.Filters(card))
            {
                if (f.Archetype == from) { f.Archetype = to; n++; }
                if (f.Archetype2 == from) { f.Archetype2 = to; n++; }
            }
        }
        return n;
    }

    /// <summary>Changes Type <paramref name="from"/> to <paramref name="to"/> on every card and filter; how many changed.</summary>
    public static int ChangeType(CardPack pack, long from, long to)
    {
        if (from == to || from == 0) return 0;
        var n = 0;
        CardRace Swap(CardRace race)
        {
            var v = (long)race;
            // A filter may look for several Types at once: change only this one.
            if (v == from || (v & from) == from && (from & (from - 1)) == 0) { n++; return (CardRace)((v & ~from) | to); }
            return race;
        }
        foreach (var card in pack.Cards)
        {
            card.Card.Race = Swap(card.Card.Race);
            foreach (var f in PasscodeFix.Filters(card)) f.Race = Swap(f.Race);
        }
        return n;
    }
}
