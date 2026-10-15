namespace CardCrafter.Core.Text;

/// <summary>
/// Names of cards that are not in the pack, such as every card in YGO Omega's databases, so that card text and
/// the card pickers can say "Blue-Eyes White Dragon" rather than a passcode.
/// </summary>
public static class CardNames
{
    private static Dictionary<long, string> _byId = [];
    private static IReadOnlyList<(long Id, string Name)> _all = [];

    /// <summary>Every known card, sorted by name.</summary>
    public static IReadOnlyList<(long Id, string Name)> All => _all;

    public static void Set(IEnumerable<(long Id, string Name)> cards)
    {
        var byId = new Dictionary<long, string>();
        foreach (var (id, name) in cards)
            if (name.Length > 0) byId.TryAdd(id, name);
        _all = byId.Select(kv => (kv.Key, kv.Value)).OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase).ToList();
        _byId = byId;
    }

    public static string? Find(long id) => _byId.GetValueOrDefault(id);

    /// <summary>The card's name from the pack first, then from the known cards.</summary>
    public static string? Find(long id, Model.CardPack? pack) =>
        pack?.Cards.FirstOrDefault(c => c.Card.Id == id)?.Card.Name is { Length: > 0 } n ? n : Find(id);
}

/// <summary>Archetype names that are not in the pack, such as the ones found in YGO Omega's databases.</summary>
public static class ArchetypeNames
{
    private static Dictionary<int, string> _byCode = [];

    public static void Set(IEnumerable<(int Code, string Name)> archetypes)
    {
        var byCode = new Dictionary<int, string>();
        foreach (var (code, name) in archetypes)
            if (name.Length > 0 && !name.StartsWith("0x", StringComparison.Ordinal)) byCode.TryAdd(code, name);
        _byCode = byCode;
    }

    public static string? Find(int code) => _byCode.GetValueOrDefault(code);

    /// <summary>Every known archetype, sorted by name.</summary>
    public static IEnumerable<(int Code, string Name)> All => _byCode.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).Select(kv => (kv.Key, kv.Value));
}
