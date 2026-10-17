using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>
/// "(This card is always treated as a "HERO" card.)": official cards print it for each archetype they belong to whose
/// name isn't in their own name, as the first line of their text (after the materials of an Extra Deck monster, after
/// the flavor text of a Normal Monster, in the Monster Effect box of a Pendulum Monster). Two or more read "a "Magistus"
/// and "Endymion" card" and "a "Zubaba", "Gagaga", "Gogogo", and "Dododo" card", in the order of the card's setcodes.
/// </summary>
public static partial class ArchetypeReminder
{
    /// <summary>
    /// Whether a card's name says the archetype, so it needs no reminder. Official cards compare letters and digits only,
    /// whatever the case: "Ankuriboh" is a "Kuriboh" card, "Lord Gaia the Fierce Knight" a "Gaia The Fierce Knight" card
    /// and "Counter Gem" a "Gem-" card, all without the reminder; "Favorite HERO Flame Wingman" still prints it for
    /// "Elemental HERO".
    /// </summary>
    public static bool IsNamed(string cardName, string archetypeName)
    {
        var archetype = Letters(archetypeName);
        return archetype.Length == 0 || Letters(cardName).Contains(archetype, StringComparison.Ordinal);
    }

    private static string Letters(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// The names of the card's archetypes that its name doesn't say: the Card tab's and the ones a "This card is always
    /// treated as" block puts in its database entry, in that order. One without a name reads as its code ("0x8"), as the
    /// block's own reminder did, so a missing name shows.
    /// </summary>
    public static List<string> Unnamed(CardProject project, CardPack? pack)
    {
        var names = new List<string>();
        foreach (var code in TreatedAsPassive.Setcodes(project))
        {
            var name = pack?.ArchetypeName(code) ?? ArchetypeNames.Find(code) ?? $"0x{code:x}";
            if (IsNamed(project.Card.Name, name) || names.Contains(name)) continue;
            names.Add(name);
        }
        return names;
    }

    /// <summary>"(This card is always treated as a "X" card.)", "... a "X" and "Y" card.)", "... a "X", "Y", and "Z" card.)".</summary>
    public static string Line(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return "";
        var quoted = names.Select(n => $"\"{n}\"").ToList();
        var list = quoted.Count <= 2 ? string.Join(" and ", quoted) : string.Join(", ", quoted.Take(quoted.Count - 1)) + ", and " + quoted[^1];
        return $"(This card is always treated as {Article(names[0])} {list} card.)";
    }

    /// <summary>The reminder the card's text gets: empty when its name says every archetype, or the card has it turned off.</summary>
    public static string Line(CardProject project, CardPack? pack) =>
        project.Card.ArchetypeReminder ? Line(Unnamed(project, pack)) : "";

    /// <summary>"an "Elemental HERO"", "an "X-Saber"", "a "Utopic"" and "a "F.A."", as official reminders print them.</summary>
    private static string Article(string name)
    {
        if (name.Length == 0) return "a";
        if (name[0] is 'X' or 'x') return "an";
        if (name.StartsWith("Uto", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Uni", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Use", StringComparison.OrdinalIgnoreCase) || name.StartsWith("One", StringComparison.OrdinalIgnoreCase)) return "a";
        return "AEIOUaeiou".Contains(name[0]) ? "an" : "a";
    }

    [GeneratedRegex(@"\(This card is (also )?always treated as [^()]*?card\.\)")]
    private static partial Regex Existing();

    /// <summary>True when the text has a reminder that names every one of these archetypes.</summary>
    public static bool Says(string text, IReadOnlyCollection<string> names)
    {
        var said = Existing().Matches(text ?? "").SelectMany(m => Regex.Matches(m.Value, "\"([^\"]+)\"").Select(q => q.Groups[1].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.All(said.Contains);
    }

    /// <summary>A "This card is always treated as" block's own reminder at the start of its effect's text, which the card's line replaces.</summary>
    internal static string WithoutReminder(string effectText)
    {
        var m = Existing().Match(effectText);
        return m.Success && m.Index == 0 ? effectText[m.Length..].TrimStart() : effectText;
    }

    /// <summary>
    /// An Info when the card's typed text doesn't print the reminder its archetypes need. Cards whose text is left empty get
    /// the suggested text, which has it; cards with the reminder turned off are left alone.
    /// </summary>
    public static Diagnostic? Check(CardProject project, CardPack? pack)
    {
        var card = project.Card;
        if (!card.ArchetypeReminder || string.IsNullOrWhiteSpace(card.Text)) return null;
        var names = Unnamed(project, pack);
        if (names.Count == 0 || Says(card.Text + "\n" + card.PendulumText, names)) return null;
        var which = names.Count == 1 ? $"\"{names[0]}\" isn't" : $"{string.Join(", ", names.Select(n => $"\"{n}\""))} aren't";
        return new Diagnostic(Severity.Info,
            $"The card text doesn't say {Line(names)} {which} in the card's name, so official cards print that line first. " +
            "\"Add the archetype line\" on the Card tab puts it in, or untick the reminder there.");
    }

    /// <summary>
    /// The card's typed text with the reminder where official cards print it: replacing a reminder that misses some of the
    /// archetypes, or as the first line (after an Extra Deck monster's materials, after a Normal Monster's flavor text).
    /// </summary>
    public static string AddTo(CardProject project, CardPack? pack)
    {
        var text = project.Card.Text ?? "";
        var names = Unnamed(project, pack);
        var line = Line(names);
        if (line.Length == 0 || Says(text, names)) return text;
        if (text.Trim().Length == 0) return line;
        var m = Existing().Match(text);
        if (m.Success) return text[..m.Index] + line + text[(m.Index + m.Length)..];
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').ToList();
        var card = project.Card;
        if (card.IsMonster && card.Frame == MonsterFrame.Normal) lines.Add(line);
        else if (card.IsExtraDeck && IsMaterials(lines[0], project, pack)) lines.Insert(1, line);
        else lines.Insert(0, line);
        return string.Join(newline, lines);
    }

    /// <summary>An Extra Deck monster's first line is its materials: the generated ones, or a line that isn't a sentence.</summary>
    private static bool IsMaterials(string line, CardProject project, CardPack? pack)
    {
        var t = line.Trim();
        return t.Length > 0 && (t == MaterialText.Describe(project, pack) || !t.EndsWith('.') && !t.EndsWith(".)", StringComparison.Ordinal));
    }
}
