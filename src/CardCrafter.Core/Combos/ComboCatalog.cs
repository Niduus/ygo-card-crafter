using System.Text;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Combos;

/// <summary>A card a combo can show: its name, its data (for its frame colour and rules) and its card text.</summary>
public sealed record ComboCard(long Id, string Name, CardData Data, string Text);

/// <summary>
/// Finds the cards combos use, by passcode or by name: the open pack's own cards first, then every card in YGO Omega's
/// databases (the card browser's library). Alternate artworks resolve to their card.
/// </summary>
public sealed class ComboCatalog
{
    private readonly Dictionary<long, ComboCard> _byId = [];
    private readonly Dictionary<string, ComboCard> _byName = [];
    private readonly Dictionary<long, long> _altArts = [];
    private readonly List<ComboCard> _all = [];

    public static ComboCatalog Empty { get; } = new([]);

    public ComboCatalog(IEnumerable<ComboCard> cards, IReadOnlyDictionary<long, long>? altArts = null)
    {
        foreach (var card in cards)
        {
            if (!_byId.TryAdd(card.Id, card)) continue;
            _all.Add(card);
            _byName.TryAdd(NameKey(card.Name), card);
        }
        if (altArts is not null) foreach (var (alt, main) in altArts) _altArts.TryAdd(alt, main);
    }

    /// <summary>The pack's cards (with their current text) ahead of the library's.</summary>
    public static ComboCatalog Build(CardPack? pack, CardLibrary? library)
    {
        var cards = new List<ComboCard>();
        if (pack is not null)
            foreach (var p in pack.Cards)
                cards.Add(new ComboCard(p.Card.Id, p.Card.Name, p.Card, PackCardText(p, pack)));
        if (library is not null)
            foreach (var c in library.Cards)
                cards.Add(new ComboCard(c.Id, c.Name, c.Data, c.Text));
        return new ComboCatalog(cards, library?.AltArts);
    }

    private static string PackCardText(CardProject p, CardPack pack)
    {
        var text = string.IsNullOrWhiteSpace(p.Card.Text) ? SafeGenerate(p, pack) : p.Card.Text;
        return p.Card.IsPendulum && p.Card.PendulumText.Trim().Length > 0
            ? $"[ Pendulum Effect ]\n{p.Card.PendulumText.Trim()}\n[ Monster Effect ]\n{text}"
            : text;
    }

    private static string SafeGenerate(CardProject p, CardPack pack)
    {
        try { return EffectTextGenerator.Generate(p, pack); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or NullReferenceException) { return ""; }
    }

    public IReadOnlyList<ComboCard> All => _all;
    public int Count => _all.Count;

    /// <summary>The card's own passcode for an alternate artwork's.</summary>
    public long Canonical(long id) => _byId.ContainsKey(id) ? id : _altArts.TryGetValue(id, out var main) ? main : id;

    public ComboCard? Find(long id) => _byId.TryGetValue(id, out var c) ? c : _altArts.TryGetValue(id, out var main) ? _byId.GetValueOrDefault(main) : null;

    /// <summary>A card by its name, ignoring case, quote styles, dashes and spacing.</summary>
    public ComboCard? FindByName(string name) => _byName.GetValueOrDefault(NameKey(name));

    /// <summary>The card's name; for one this catalog doesn't have, the name the combo remembers, or its passcode.</summary>
    public string Name(long id, IReadOnlyDictionary<long, string>? remembered = null) =>
        Find(id)?.Name ?? (remembered is not null && remembered.TryGetValue(id, out var n) ? n : $"Card {id}");

    /// <summary>Cards whose name has every word of the query, exact and starting matches first.</summary>
    public List<ComboCard> Search(string query, int limit = 30)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var q = query.Trim();
        return _all.Where(c => words.All(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0 : c.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>A name made comparable: lower case, one kind of quote and dash, no doubled spaces.</summary>
    public static string NameKey(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            var c = ch switch
            {
                '‘' or '’' or '`' or '´' => '\'',
                '“' or '”' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                '　' or '\t' => ' ',
                _ => ch,
            };
            if (c == ' ' && sb.Length > 0 && sb[^1] == ' ') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
