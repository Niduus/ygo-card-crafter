using CardCrafter.Core.Database;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Omega;

/// <param name="Name">A guess from the shared start of its cards' names, e.g. "Elemental HERO".</param>
public sealed record KnownArchetype(int Code, string Name, int Cards, IReadOnlyList<string> Examples)
{
    public string Label => $"{Name} (0x{Code:x}, {Cards} cards)";
    public override string ToString() => Label;
}

/// <summary>A monster Type bit used in Omega's databases that isn't one of the standard Types.</summary>
public sealed record UnknownType(long Value, int Cards, IReadOnlyList<string> Examples)
{
    /// <summary>
    /// The Rush Duel Types use these values in the ygopro-family engines (EDOPro's constant list).
    /// Shown as a likely name; the example cards let the user confirm it.
    /// </summary>
    public static readonly IReadOnlyDictionary<long, string> LikelyNames = new Dictionary<long, string>
    {
        [0x4000000] = "Cyborg",
        [0x8000000] = "Magical Knight",
        [0x10000000] = "High Dragon",
        [0x20000000] = "Omega Psychic",
        [0x40000000] = "Celestial Warrior",
        [0x80000000] = "Galaxy",
    };

    public string? LikelyName => LikelyNames.GetValueOrDefault(Value);
    public string Title => LikelyName is { } n ? $"{n}? (0x{Value:x})" : $"Type 0x{Value:x}";
    public string Detail => $"{Cards} cards, e.g. {string.Join(", ", Examples)}";
    public string Label => $"{Title} — {Detail}";
    public override string ToString() => Label;
}

/// <summary>
/// What is already in the game: every passcode in use, the archetypes cards belong to, and any
/// custom monster Types. Read from Omega's official database and the custom databases next to it.
/// </summary>
public sealed class OmegaCatalog
{
    /// <summary>Passcode to "Card Name (database file)".</summary>
    public Dictionary<long, string> Passcodes { get; } = [];
    public List<KnownArchetype> Archetypes { get; } = [];
    public List<UnknownType> UnknownTypes { get; } = [];
    /// <summary>Databases that couldn't be read, with the reason. Reading other tools' files must never crash the app.</summary>
    public List<string> Problems { get; } = [];

    public static OmegaCatalog Empty { get; } = new();

    public string? Clash(long passcode) => Passcodes.GetValueOrDefault(passcode);

    public static OmegaCatalog Load(OmegaInventory inventory, string? excludeDatabaseName = null) =>
        Load(inventory.CardDatabases.Select(d => d.Path)
                .Where(p => excludeDatabaseName is null || !Path.GetFileNameWithoutExtension(p).Equals(excludeDatabaseName, StringComparison.OrdinalIgnoreCase)),
            inventory.Root);

    public static OmegaCatalog Load(IEnumerable<string> databases, string root)
    {
        var catalog = new OmegaCatalog();
        var byCode = new Dictionary<int, List<string>>();
        var types = new Dictionary<long, List<string>>();
        // Names that card texts put in quotes: "add 1 "Mitsurugi" monster" names the archetype.
        var quoted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var db in databases)
        {
            List<CdbRow> rows;
            try { rows = CdbFile.Read(db); }
            catch (Exception ex)
            {
                catalog.Problems.Add($"{Path.GetRelativePath(root, db)}: {ex.Message}");
                continue;
            }
            var file = Path.GetRelativePath(root, db);
            foreach (var row in rows)
            {
                catalog.Passcodes.TryAdd(row.Id, $"{row.Name} ({file})");
                // Alternate artworks share a name and setcodes with the original; count each card once.
                if (row.Alias != 0) continue;
                AddQuoted(row.Desc, quoted);
                foreach (var code in CdbEncoder.UnpackSetcodes(row.Setcode))
                {
                    if (!byCode.TryGetValue(code, out var names)) byCode[code] = names = [];
                    names.Add(row.Name);
                }
                if ((row.Type & TypeBits.Monster) != 0)
                {
                    var extra = row.Race & ~EnumNames.StandardRaceMask;
                    for (long bit = 1; extra != 0 && bit != 0; bit <<= 1)
                    {
                        if ((extra & bit) == 0) continue;
                        extra &= ~bit;
                        if (!types.TryGetValue(bit, out var list)) types[bit] = list = [];
                        list.Add(row.Name);
                    }
                }
            }
        }
        foreach (var (code, names) in byCode)
            catalog.Archetypes.Add(new KnownArchetype(code, GuessName(names, quoted) ?? $"0x{code:x}", names.Count, names.Distinct().Take(3).ToList()));
        catalog.Archetypes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var (value, names) in types.OrderBy(t => t.Key))
            catalog.UnknownTypes.Add(new UnknownType(value, names.Count, names.Distinct().Take(3).ToList()));
        return catalog;
    }

    /// <summary>
    /// Every "quoted" name in a card's text. Names used as archetypes (""Mitsurugi" monster", ""Dominators" or
    /// "Dominus Purge" Trap") are also marked with "#" in front.
    /// </summary>
    public static void AddQuoted(string text, HashSet<string> into)
    {
        text ??= "";
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\"([^\"\r\n]{2,60})\""))
            into.Add(m.Groups[1].Value.Trim());
        const string noun = @"(?:cards?|monsters?|Spells?|Traps?|Spell/Traps?|Ritual|Fusion|Synchro|Xyz|Link|Pendulum|Tuners?|Normal|Effect|Continuous|Quick-Play|Field|Equip|Counter|Tokens?)\b";
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,
                     "((?:\"[^\"\r\n]{2,60}\"(?:,| or| and|, or|, and)? ?)+)" + noun))
            foreach (System.Text.RegularExpressions.Match q in System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, "\"([^\"\r\n]{2,60})\""))
                into.Add("#" + q.Groups[1].Value.Trim());
    }

    /// <summary>
    /// Guesses an archetype's name from its cards' names and the names card texts put in quotes: the quoted name found
    /// in the most of its cards' names, such as "Mitsurugi" in "Ame no Murakumo no Mitsurugi" and "Mitsurugi Ritual".
    /// Falls back to the shared leading words.
    /// </summary>
    public static string? GuessName(IReadOnlyList<string> names, IReadOnlySet<string> quoted)
    {
        var distinct = names.Where(n => n.Length > 0).Distinct().ToList();
        if (distinct.Count == 0) return null;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in distinct)
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < words.Length; i++)
                for (int len = 1; len <= Math.Min(5, words.Length - i); len++)
                {
                    var gram = string.Join(' ', words.Skip(i).Take(len)).Trim(',', ':', '!', '?', '.');
                    if (quoted.Contains(gram) && seen.Add(gram)) counts[gram] = counts.GetValueOrDefault(gram) + 1;
                }
            // A word-part such as "Dominus" in "Dominusteer".
            foreach (var q in WordStarts(words, quoted))
                if (seen.Add(q)) counts[q] = counts.GetValueOrDefault(q) + 1;
        }
        var needed = Math.Max(1, (int)Math.Ceiling(distinct.Count * 0.4));
        bool Archetypal(string name) => quoted.Contains("#" + name);
        var ranked = counts.Where(kv => kv.Value >= needed)
            .OrderByDescending(kv => kv.Value).ThenByDescending(kv => Archetypal(kv.Key)).ThenByDescending(kv => kv.Key.Length)
            .ToList();
        if (ranked.Count == 0) return GuessName(distinct);
        var best = ranked[0];
        // "Elemental HERO" over "HERO": a longer name used as an archetype that covers nearly as many cards.
        foreach (var kv in ranked)
            if (kv.Key.Length > best.Key.Length && kv.Key.Contains(best.Key, StringComparison.OrdinalIgnoreCase)
                && Archetypal(kv.Key) && kv.Value >= best.Value * 0.8)
                best = kv;
        // A whole card name only quoted to name that card ("You can only activate 1 "Songs of the Dominators" per turn")
        // loses to a name used as an archetype inside it.
        if (!Archetypal(best.Key) && ranked.FirstOrDefault(kv => kv.Value == best.Value && Archetypal(kv.Key)) is { Key: not null } arch)
            best = arch;
        return best.Key;
    }

    private static IEnumerable<string> WordStarts(string[] words, IReadOnlySet<string> quoted)
    {
        foreach (var w in words)
            for (int n = 4; n < w.Length; n++)
                if (quoted.Contains(w[..n])) yield return w[..n];
    }

    /// <summary>
    /// Guesses an archetype's name: the longest run of leading words shared by a good share of its cards,
    /// such as "Blue-Eyes" or "Elemental HERO". Returns null when the names have nothing in common.
    /// </summary>
    public static string? GuessName(IReadOnlyList<string> names)
    {
        var distinct = names.Where(n => n.Length > 0).Distinct().ToList();
        if (distinct.Count == 0) return null;
        if (distinct.Count == 1) return distinct[0];
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in distinct)
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int len = 1; len <= Math.Min(3, words.Length); len++)
            {
                var prefix = string.Join(' ', words.Take(len)).TrimEnd(',', ':', '-');
                counts[prefix] = counts.GetValueOrDefault(prefix) + 1;
            }
        }
        var needed = Math.Max(2, (int)Math.Ceiling(distinct.Count * 0.4));
        return counts.Where(kv => kv.Value >= needed)
            .OrderByDescending(kv => kv.Value * (1 + 0.35 * (kv.Key.Count(c => c == ' '))))
            .ThenByDescending(kv => kv.Key.Length)
            .Select(kv => kv.Key)
            .FirstOrDefault();
    }

    /// <summary>A setcode for a new archetype that no card in Omega uses yet.</summary>
    public int FreeArchetypeCode(IEnumerable<int> alsoTaken)
    {
        var taken = Archetypes.Select(a => a.Code & 0xfff).Concat(alsoTaken.Select(c => c & 0xfff)).ToHashSet();
        for (int code = 0xa00; code <= 0xfff; code++) if (!taken.Contains(code)) return code;
        for (int code = 0x100; code < 0xa00; code++) if (!taken.Contains(code)) return code;
        return 0xfff;
    }
}
