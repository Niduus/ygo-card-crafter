using System.Text.RegularExpressions;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Omega;

/// <summary>Where a search looks.</summary>
public enum SearchIn { NameAndText, Name, Text }

/// <summary>One card in the game's databases.</summary>
public sealed class LibraryCard(CdbRow row, string source, string sourcePath)
{
    public CdbRow Row { get; } = row;
    /// <summary>The database it came from, relative to the Omega folder.</summary>
    public string Source { get; } = source;
    public string SourcePath { get; } = sourcePath;
    public long Id => Row.Id;
    /// <summary>The card's name, or its passcode when the database has no name for it.</summary>
    public string Name => Row.Name.Length > 0 ? Row.Name : $"(no name) {Row.Id}";
    public string Text => Row.Desc;

    private CardData? _data;
    public CardData Data => _data ??= CdbEncoder.Decode(Row);
    public string TypeLine
    {
        get
        {
            if (Data.IsMonster) return CardFaceText.TypeLine(Data, null);
            var (label, kind) = CardFaceText.SpellTrapLine(Data);
            return kind.Length > 0 ? $"[{label} · {kind}]" : $"[{label}]";
        }
    }
    public string Stats => !Data.IsMonster ? "" : Data.IsLink
        ? $"ATK/{CardFaceText.Stat(Data.Atk)}  LINK-{Data.LinkRating}"
        : $"ATK/{CardFaceText.Stat(Data.Atk)}  DEF/{CardFaceText.Stat(Data.Def)}";
    public override string ToString() => Name;
}

/// <summary>
/// Every card in YGO Omega's databases (the game's own and the custom ones), searchable by name and by
/// words in the card text, e.g. "negate" finds every card whose text says Negate.
/// </summary>
public sealed class CardLibrary
{
    public List<LibraryCard> Cards { get; } = [];
    public List<string> Problems { get; } = [];

    public static CardLibrary Empty { get; } = new();

    public static CardLibrary Load(IEnumerable<string> databases, string? root = null)
    {
        var library = new CardLibrary();
        var seen = new HashSet<long>();
        foreach (var db in databases)
        {
            List<CdbRow> rows;
            try { rows = CdbFile.Read(db); }
            catch (Exception ex)
            {
                library.Problems.Add($"{Path.GetFileName(db)}: {ex.Message}");
                continue;
            }
            var source = root is null ? Path.GetFileName(db) : Path.GetRelativePath(root, db);
            foreach (var row in rows)
            {
                // Alternate artworks repeat the original card; list each card once.
                if (row.Alias != 0 && Math.Abs(row.Id - row.Alias) < 20) continue;
                if (!seen.Add(row.Id)) continue;
                library.Cards.Add(new LibraryCard(row, source, db));
            }
        }
        library.Cards.Sort((a, b) => (a.Row.Name.Length == 0).CompareTo(b.Row.Name.Length == 0) is var c and not 0 ? c
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return library;
    }

    /// <summary>
    /// The search words: each word must appear, and "quoted phrases" must appear as written.
    /// Case doesn't matter.
    /// </summary>
    public static List<string> Terms(string query)
    {
        var terms = new List<string>();
        foreach (Match m in Regex.Matches(query, "\"([^\"]+)\"|(\\S+)"))
        {
            var t = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (t.Length > 0) terms.Add(t);
        }
        return terms;
    }

    /// <summary>Cards matching every term, best first: exact names, then names starting with it, then other name matches, then text matches.</summary>
    public List<LibraryCard> Search(string query, SearchIn where = SearchIn.NameAndText, CardKind? kind = null, int limit = int.MaxValue)
    {
        var terms = Terms(query);
        var q = query.Trim().Trim('"');
        IEnumerable<LibraryCard> found = Cards;
        if (kind is { } k) found = found.Where(c => c.Data.Kind == k);
        if (terms.Count == 0) return found.Take(limit).ToList();

        static bool Has(string s, string t) => s.Contains(t, StringComparison.OrdinalIgnoreCase);
        return found
            .Select(c =>
            {
                bool inName = where != SearchIn.Text && terms.All(t => Has(c.Name, t));
                bool inText = where != SearchIn.Name && terms.All(t => Has(c.Text, t) || (where == SearchIn.NameAndText && Has(c.Name, t)));
                var rank = !inName && !inText ? -1
                    : c.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0
                    : inName && c.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1
                    : inName ? 2 : 3;
                return (Card: c, Rank: rank);
            })
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Card.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => x.Card)
            .ToList();
    }

    public LibraryCard? Find(long id) => Cards.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// A card's script: c&lt;id&gt;.lua in one of the given folders, or the script stored in its database row.
    /// Returns the text and where it came from, or null when there is none.
    /// </summary>
    public static (string Lua, string From)? FindScript(LibraryCard card, IEnumerable<string> scriptFolders)
    {
        foreach (var folder in scriptFolders.Where(Directory.Exists))
        {
            var path = Path.Combine(folder, $"c{card.Id}.lua");
            if (File.Exists(path)) return (File.ReadAllText(path), path);
        }
        if (!string.IsNullOrWhiteSpace(card.Row.Script) && card.Row.Script.Contains("initial_effect", StringComparison.Ordinal))
            return (card.Row.Script, $"{card.Source} (stored in the database)");
        return null;
    }
}
