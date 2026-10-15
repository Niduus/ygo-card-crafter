using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CardCrafter.Core.Compiler;

/// <summary>One effect of a card script, with the functions it needs, ready to be added to another card.</summary>
public sealed class ScriptPiece
{
    /// <summary>A name for the piece: the effect's description string, its comment, or what it does.</summary>
    public required string Title { get; init; }
    /// <summary>The lines inside initial_effect that create and register the effect.</summary>
    public required string Code { get; init; }
    /// <summary>The script's top-level functions and values the code uses, in script order.</summary>
    public required IReadOnlyList<string> Functions { get; init; }
    /// <summary>The prefix the source script uses for its functions ("s" or "c12345678").</summary>
    public required string Prefix { get; init; }
    public required long SourceId { get; init; }

    public string Preview => Functions.Count == 0 ? Code : Code + "\n\n" + string.Join("\n", Functions);
    public override string ToString() => Title;
}

/// <summary>
/// Takes card scripts apart effect by effect, so a card can be built from pieces of other cards:
/// each effect registered in initial_effect becomes a piece carrying every function it calls. A piece can then
/// be added to another card's script, with its functions renamed for that card so nothing clashes.
/// </summary>
public static partial class LuaPieces
{
    [GeneratedRegex(@"^function\s+([A-Za-z_]\w*)\.initial_effect\s*\(", RegexOptions.Multiline)]
    private static partial Regex InitialEffect();

    [GeneratedRegex(@"^\s*local\s+(\w+)\s*=\s*Effect\.CreateEffect\(")]
    private static partial Regex CreateEffect();

    [GeneratedRegex(@"^\s*local\s+(\w+)\s*=\s*(\w+):Clone\(\)")]
    private static partial Regex CloneEffect();

    [GeneratedRegex(@"aux\.Stringid\(\s*(?:id|\d+)\s*,\s*(\d+)\s*\)")]
    private static partial Regex Stringid();

    /// <summary>The function prefix of a script: "s" for <c>local s,id=GetID()</c> style, or "c12345678".</summary>
    public static string? PrefixOf(string lua) => InitialEffect().Match(lua) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Splits a script into its effects. <paramref name="strings"/> are the card's str1..str16, used to name effects.</summary>
    public static List<ScriptPiece> Split(string lua, long id, IReadOnlyList<string>? strings = null)
    {
        lua = lua.Replace("\r\n", "\n");
        var prefix = PrefixOf(lua);
        if (prefix is null) return [];
        var lines = lua.Split('\n');
        var start = Array.FindIndex(lines, l => InitialEffect().IsMatch(l));
        var end = TopLevelEnd(lines, start);
        if (start < 0 || end < 0) return [];
        var top = TopLevelDefinitions(lines, prefix);

        // Group the body into statements: an effect from CreateEffect to its RegisterEffect, a whole block
        // (if ... end, for ... end), or a single line.
        var chunks = new List<List<string>>();
        var comments = new List<string>();
        List<string>? open = null;
        int depth = 0;
        for (int i = start + 1; i < end; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (open is null && depth == 0 && trimmed.StartsWith("--", StringComparison.Ordinal)) { comments.Add(line); continue; }
            var wasDepth = depth;
            depth = Math.Max(0, depth + BlockDelta(trimmed));
            if (open is null)
            {
                open = [.. comments, line];
                comments.Clear();
                var isEffect = CreateEffect().IsMatch(line) || CloneEffect().IsMatch(line);
                if (!isEffect && depth == 0)
                {
                    chunks.Add(open); // a single statement, such as a summon procedure
                    open = null;
                }
                continue;
            }
            open.Add(line);
            // An effect ends at its RegisterEffect; a block ends where its nesting closes.
            var effect = CreateEffect().IsMatch(open[comments.Count > 0 ? 0 : 0].Trim().StartsWith("--", StringComparison.Ordinal) ? open.First(l => !l.Trim().StartsWith("--", StringComparison.Ordinal)) : open[0])
                         || CloneEffect().IsMatch(open.First(l => !l.Trim().StartsWith("--", StringComparison.Ordinal)));
            if (depth == 0 && (!effect && wasDepth > 0 || effect && trimmed.Contains("RegisterEffect(", StringComparison.Ordinal)))
            {
                chunks.Add(open);
                open = null;
            }
        }
        if (open is not null) chunks.Add(open);

        // A clone needs the effect it copies, so it travels with it.
        var merged = new List<List<string>>();
        foreach (var chunk in chunks)
        {
            var clone = chunk.Select(l => CloneEffect().Match(l)).FirstOrDefault(m => m.Success);
            var owner = clone is null ? null : merged.LastOrDefault(c => c.Any(l => Regex.IsMatch(l, $@"^\s*local\s+{Regex.Escape(clone.Groups[2].Value)}\s*=")));
            if (owner is not null) owner.AddRange(chunk);
            else merged.Add([.. chunk]);
        }

        var pieces = new List<ScriptPiece>();
        foreach (var chunk in merged)
        {
            var code = string.Join("\n", chunk);
            pieces.Add(new ScriptPiece
            {
                Title = TitleOf(chunk, strings, pieces.Count + 1),
                Code = code,
                Functions = Needed(code, top, prefix),
                Prefix = prefix,
                SourceId = id,
            });
        }
        return pieces;
    }

    /// <summary>Names a piece by its description string, then its comment, then what kind of effect it is.</summary>
    private static string TitleOf(List<string> chunk, IReadOnlyList<string>? strings, int number)
    {
        var code = string.Join("\n", chunk);
        if (Stringid().Match(code) is { Success: true } s && strings is not null
            && int.TryParse(s.Groups[1].Value, CultureInfo.InvariantCulture, out var n) && n < strings.Count && strings[n].Trim().Length > 0)
            return strings[n].Trim();
        var comment = chunk.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("--", StringComparison.Ordinal))?.TrimStart('-').Trim();
        if (!string.IsNullOrEmpty(comment) && comment.Any(char.IsLetter)) return char.ToUpperInvariant(comment[0]) + comment[1..];
        if (code.Contains("EFFECT_SPSUMMON_PROC", StringComparison.Ordinal)) return "Special Summon procedure";
        if (code.Contains("EFFECT_TYPE_ACTIVATE", StringComparison.Ordinal)) return "Activate";
        if (Regex.Match(code, @"aux\.(Add\w+Proc\w*)") is { Success: true } p) return p.Groups[1].Value;
        if (code.Contains("EnableReviveLimit", StringComparison.Ordinal)) return "Revive limit (must be properly Summoned first)";
        return $"Effect {number}";
    }

    /// <summary>Opening minus closing brackets on a line, outside strings and comments.</summary>
    private static int Brackets(string line)
    {
        var code = Regex.Replace(line, "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'", "\"\"");
        var comment = code.IndexOf("--", StringComparison.Ordinal);
        if (comment >= 0) code = code[..comment];
        return code.Count(ch => ch is '{' or '(') - code.Count(ch => ch is '}' or ')');
    }

    /// <summary>How a line changes Lua block nesting: if/function/do/repeat open a block, end/until close one.</summary>
    private static int BlockDelta(string line)
    {
        var code = Regex.Replace(line, "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'", "\"\"");
        var comment = code.IndexOf("--", StringComparison.Ordinal);
        if (comment >= 0) code = code[..comment];
        int opens = Regex.Matches(code, @"\b(if|function|do|repeat)\b").Count;
        int closes = Regex.Matches(code, @"\b(end|until)\b").Count;
        return opens - closes;
    }

    /// <summary>The line of the "end" that closes the top-level function starting at <paramref name="start"/>.</summary>
    private static int TopLevelEnd(string[] lines, int start)
    {
        if (start < 0) return -1;
        // Count blocks rather than trusting indentation: some scripts put inner "end"s at the start of the line.
        int depth = 0;
        for (int i = start; i < lines.Length; i++)
        {
            depth += BlockDelta(lines[i]);
            if (depth <= 0 && i > start || depth <= 0 && i == start && Regex.IsMatch(lines[i], @"\bend\s*$")) return i;
        }
        return -1;
    }

    /// <summary>The script's top-level functions and values, by name, with their full text.</summary>
    private static Dictionary<string, (int Line, string Text)> TopLevelDefinitions(string[] lines, string prefix)
    {
        var defs = new Dictionary<string, (int, string)>();
        var p = Regex.Escape(prefix);
        for (int i = 0; i < lines.Length; i++)
        {
            var fn = Regex.Match(lines[i], $@"^function\s+{p}\.(\w+)\s*\(");
            if (fn.Success)
            {
                var name = fn.Groups[1].Value;
                if (name == "initial_effect") continue;
                var end = TopLevelEnd(lines, i);
                if (end < 0) end = lines.Length - 1;
                defs[name] = (i, string.Join("\n", lines[i..(end + 1)]));
                i = end;
                continue;
            }
            // Values such as s.listed_names={...} or c123.spchecks=aux.CreateChecks(...).
            var value = Regex.Match(lines[i], $@"^{p}\.(\w+)\s*=");
            if (value.Success)
            {
                // A table or call can run over several lines: take lines until its brackets close.
                int last = i, balance = Brackets(lines[i]);
                while (balance > 0 && last + 1 < lines.Length) balance += Brackets(lines[++last]);
                defs[value.Groups[1].Value] = (i, string.Join("\n", lines[i..(last + 1)]));
                i = last;
            }
        }
        return defs;
    }

    /// <summary>Every definition the code uses, following calls between the script's own functions.</summary>
    private static List<string> Needed(string code, Dictionary<string, (int Line, string Text)> top, string prefix)
    {
        var needed = new HashSet<string>();
        var queue = new Queue<string>([code]);
        var reference = new Regex($@"\b{Regex.Escape(prefix)}\.(\w+)");
        while (queue.Count > 0)
            foreach (Match m in reference.Matches(queue.Dequeue()))
            {
                var name = m.Groups[1].Value;
                if (top.TryGetValue(name, out var def) && needed.Add(name)) queue.Enqueue(def.Text);
            }
        return needed.Select(n => top[n]).OrderBy(d => d.Line).Select(d => d.Text).ToList();
    }

    /// <summary>
    /// A whole script moved to a new passcode: c&lt;old&gt; and the old number become the new ones.
    /// Scripts written with <c>GetID()</c> follow their passcode on their own.
    /// </summary>
    public static string Retarget(string lua, long oldId, long newId)
    {
        if (oldId == newId) return lua;
        var o = oldId.ToString(CultureInfo.InvariantCulture);
        var n = newId.ToString(CultureInfo.InvariantCulture);
        return Regex.Replace(lua, $@"(?<![\w.]){o}(?!\w)|\bc{o}\b", m => m.Value.StartsWith('c') ? "c" + n : n);
    }

    /// <summary>
    /// Adds a piece to a card script: its code goes at the end of initial_effect in its own <c>do ... end</c> block,
    /// and its functions after the script, renamed with the target's prefix (and a number if the name is taken).
    /// The source card's passcode in the piece becomes the target's <c>id</c>.
    /// </summary>
    public static string Insert(string targetLua, ScriptPiece piece)
    {
        targetLua = targetLua.Replace("\r\n", "\n");
        var targetPrefix = PrefixOf(targetLua) ?? throw new InvalidOperationException("The card's script has no initial_effect.");
        var lines = targetLua.Split('\n').ToList();
        var start = lines.FindIndex(l => InitialEffect().IsMatch(l));
        var end = TopLevelEnd(lines.ToArray(), start);
        if (end < 0) throw new InvalidOperationException("The card's script has no end to initial_effect.");

        var taken = TopLevelDefinitions(lines.ToArray(), targetPrefix).Keys.ToHashSet();
        var sourceNames = TopLevelDefinitions(string.Join("\n", piece.Functions).Split('\n'), piece.Prefix).Keys.ToList();
        var renames = new Dictionary<string, string>();
        foreach (var name in sourceNames)
        {
            var newName = name;
            for (int k = 2; taken.Contains(newName); k++) newName = name + k.ToString(CultureInfo.InvariantCulture);
            taken.Add(newName);
            renames[name] = newName;
        }

        string Adapt(string text)
        {
            var refs = new Regex($@"\b{Regex.Escape(piece.Prefix)}\.(\w+)");
            text = refs.Replace(text, m => renames.TryGetValue(m.Groups[1].Value, out var r) ? $"{targetPrefix}.{r}" : $"{targetPrefix}.{m.Groups[1].Value}");
            // The source card's own passcode means "this card" in its script.
            var id = piece.SourceId.ToString(CultureInfo.InvariantCulture);
            return Regex.Replace(text, $@"(?<![\w.]){id}(?!\w)", "id");
        }

        var body = new StringBuilder();
        body.AppendLine($"\t--from {piece.SourceId}: {piece.Title.Replace('\n', ' ')}");
        body.AppendLine("\tdo");
        foreach (var line in Adapt(piece.Code).Split('\n')) body.AppendLine("\t" + line);
        body.Append("\tend");
        lines.Insert(end, body.ToString());

        var result = string.Join("\n", lines).TrimEnd('\n');
        if (piece.Functions.Count > 0) result += "\n" + string.Join("\n", piece.Functions.Select(Adapt));
        return result + "\n";
    }
}
