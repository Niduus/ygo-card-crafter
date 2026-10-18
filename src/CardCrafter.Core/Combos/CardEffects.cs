using System.Text;
using System.Text.RegularExpressions;

namespace CardCrafter.Core.Combos;

/// <summary>
/// One paragraph of a card's text: an effect the player can use (numbered from 1), or a line that isn't one, such as the
/// materials, a "You can only use each effect ... once per turn" limit or a summoning condition.
/// </summary>
public sealed record EffectParagraph(string Text, int Number, string Section = "")
{
    public bool IsEffect => Number > 0;
}

/// <summary>Splits card text into its effects, so a combo step can say which one it used.</summary>
public static partial class CardEffects
{
    /// <summary>
    /// The paragraphs of the text in order. Pendulum Effects come first, as printed; "①" style numbers, line breaks and
    /// sentences start new effects, except sentences that carry on the one before ("Then...", "It ...", "that card").
    /// </summary>
    public static List<EffectParagraph> Split(string? text)
    {
        var result = new List<EffectParagraph>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var section = "";
        var number = 0;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var first = true;
        foreach (var raw in lines)
        {
            var line = raw.Trim().Trim('\t').Trim();
            if (line.Length == 0 || line.All(c => c is '-' or '─' or '—')) continue;
            if (SectionHeader().Match(line) is { Success: true } header)
            {
                section = header.Groups[1].Value.Trim();
                first = false;
                continue;
            }
            if (line.StartsWith("Pendulum Scale", StringComparison.OrdinalIgnoreCase)) { first = false; continue; }
            // Bullet points belong to the effect above them.
            if (line[0] is '●' or '•' && result.Count > 0)
            {
                result[^1] = result[^1] with { Text = result[^1].Text + "\n" + line };
                continue;
            }
            // The materials line of an Extra Deck monster ("2+ Effect Monsters") has no full stop.
            if (first && !line.EndsWith('.') && !line.EndsWith(')') && !line.Contains(':'))
            {
                result.Add(new EffectParagraph(line, 0, section));
                first = false;
                continue;
            }
            first = false;
            foreach (var sentence in Sentences(line))
            {
                var prev = result.Count > 0 && result[^1].Section == section ? result[^1] : null;
                if (IsRule(sentence)) result.Add(new EffectParagraph(sentence, 0, section));
                else if (prev is { IsEffect: true } && CarriesOn(sentence)) result[^1] = prev with { Text = prev.Text + " " + sentence };
                else result.Add(new EffectParagraph(sentence, ++number, section));
            }
        }
        return result;
    }

    /// <summary>The text of effect <paramref name="number"/>, or null when the card has no such effect.</summary>
    public static string? Effect(string? text, int number) => Split(text).FirstOrDefault(p => p.Number == number)?.Text;

    /// <summary>The sentences of one line: full stops outside quotes and brackets end them, and so do "①"-style numbers.</summary>
    private static IEnumerable<string> Sentences(string line)
    {
        var sb = new StringBuilder();
        bool quoted = false;
        int depth = 0;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c is >= '①' and <= '⑳' && sb.ToString().Trim().Length > 0)
            {
                yield return sb.ToString().Trim();
                sb.Clear();
            }
            sb.Append(c);
            if (c == '"') quoted = !quoted;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
            else if (c is '.' or '。' && !quoted && depth == 0 && (i + 1 == line.Length || char.IsWhiteSpace(line[i + 1])))
            {
                yield return sb.ToString().Trim();
                sb.Clear();
            }
        }
        if (sb.ToString().Trim() is { Length: > 0 } rest) yield return rest;
    }

    /// <summary>Lines that aren't effects: limits, summoning conditions and reminders in brackets.</summary>
    private static bool IsRule(string s) =>
        s.StartsWith("You can only ", StringComparison.Ordinal) || s.StartsWith("You cannot ", StringComparison.Ordinal)
        || s.StartsWith("Cannot be ", StringComparison.Ordinal) || s.StartsWith("Must be ", StringComparison.Ordinal)
        || s.StartsWith("Must first be ", StringComparison.Ordinal) || (s.StartsWith('(') && s.TrimEnd('.').EndsWith(')'));

    /// <summary>A sentence that finishes the effect before it rather than starting one.</summary>
    private static bool CarriesOn(string s)
    {
        if (s.Contains(':') || s.Contains(';') || s.StartsWith("You can", StringComparison.Ordinal)) return false;
        return ContinuationStart().IsMatch(s) || BackReference().IsMatch(s);
    }

    [GeneratedRegex(@"^\[\s*([^\]]+?)\s*\]$")]
    private static partial Regex SectionHeader();

    [GeneratedRegex(@"^(Then|Also|Otherwise|After that|If you do|It |Its |They |Their |This effect|That |Those )")]
    private static partial Regex ContinuationStart();

    [GeneratedRegex(@"\b(that card|that monster|those cards|those monsters|that target|the banished card)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BackReference();
}
