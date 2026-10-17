using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Compares two compiled card scripts by what the game does with them, not by how they are spelled: the same effects
/// registered (each with the same type, event, range, limits, condition, cost, target and operation), and the lasting
/// effects in the same order, since the game applies those in the order they were registered. What only depends on an
/// effect's place on the card doesn't count: effect variables and helper function names, which description string
/// number an effect uses (its text counts), which once-per-turn code each limit has (which effects share one counts),
/// and the order of the checks of one condition, which are all tested together. Organize uses it to keep only the
/// changes that leave the card's behaviour as it was.
/// </summary>
public static partial class ScriptEquivalence
{
    /// <summary>Null when the two scripts do the same thing, otherwise the first difference found.</summary>
    /// <param name="ignoreDescriptions">The effects' description strings don't count, for changes that only reword them.</param>
    /// <param name="costOrder">The steps of each cost may come in another order (costs that don't depend on each other).</param>
    public static string? Difference(CompileResult a, CompileResult b, bool ignoreDescriptions = false, bool costOrder = false)
    {
        var x = Canonical(a, ignoreDescriptions, costOrder);
        var y = Canonical(b, ignoreDescriptions, costOrder);
        if (!x.Prelude.SequenceEqual(y.Prelude)) return "The lines before the effects differ.";
        var onlyX = Except(x.Registrations, y.Registrations);
        var onlyY = Except(y.Registrations, x.Registrations);
        if (onlyX.Count > 0 || onlyY.Count > 0)
            return $"Different effects: {onlyX.Count} only in the first script, {onlyY.Count} only in the second." + (onlyX.Count > 0 ? "\n" + onlyX[0] : "") + (onlyY.Count > 0 ? "\n" + onlyY[0] : "");
        if (!x.Lasting.SequenceEqual(y.Lasting)) return "The lasting effects are registered in another order.";
        return null;
    }

    public static bool Same(CompileResult a, CompileResult b, bool ignoreDescriptions = false, bool costOrder = false) =>
        Difference(a, b, ignoreDescriptions, costOrder) is null;

    private static List<string> Except(List<string> from, List<string> remove)
    {
        var left = from.ToList();
        foreach (var r in remove) left.Remove(r);
        return left;
    }

    /// <summary>A script as what it registers: lines outside the effects (sorted), every registration, and the lasting ones in order.</summary>
    public sealed record Form(List<string> Prelude, List<string> Registrations, List<string> Lasting);

    public static Form Canonical(CompileResult result, bool ignoreDescriptions = false, bool costOrder = false)
    {
        var lines = result.Lua.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var functions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var prelude = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (FunctionStart().Match(line) is { Success: true } f)
            {
                var body = new List<string>();
                for (i++; i < lines.Length && lines[i] != "end"; i++) body.Add(lines[i]);
                functions[f.Groups[1].Value] = [f.Groups[2].Value, .. body];
            }
            else if (line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal) && !line.StartsWith("local s,id=", StringComparison.Ordinal)) prelude.Add(line);
        }
        var c = new Canonicalizer(functions, result.Strings, ignoreDescriptions, costOrder);
        c.FindCounters(functions.GetValueOrDefault("initial_effect") ?? []);

        var objects = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        var registrations = new List<(string Text, bool Lasting, string? Code)>();
        foreach (var raw in (functions.GetValueOrDefault("initial_effect") ?? []).Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal)) continue;
            if (Create().Match(line) is { Success: true } create) { objects[create.Groups[1].Value] = new(StringComparer.Ordinal); continue; }
            if (Clone().Match(line) is { Success: true } clone && objects.TryGetValue(clone.Groups[2].Value, out var parent))
            {
                objects[clone.Groups[1].Value] = new(parent, StringComparer.Ordinal);
                continue;
            }
            if (Setter().Match(line) is { Success: true } set && objects.TryGetValue(set.Groups[1].Value, out var props))
            {
                var method = set.Groups[2].Value;
                if (method == "SetDescription" && ignoreDescriptions) continue;
                // The once-per-turn code is compared below, by which effects share it.
                props[method] = method == "SetCountLimit" ? set.Groups[3].Value : c.Line(set.Groups[3].Value, Variables);
                continue;
            }
            if (Register().Match(line) is { Success: true } reg && objects.TryGetValue(reg.Groups[2].Value, out var registered))
            {
                var code = registered.TryGetValue("SetCountLimit", out var limit) && CountCode().Match(limit) is { Success: true } m ? m.Groups[1].Value : null;
                var type = registered.GetValueOrDefault("SetType") ?? "";
                var text = $"{reg.Groups[1].Value}:{reg.Groups[3].Value}|" + string.Join("|", registered.Select(p => $"{p.Key}({(p.Key == "SetCountLimit" && code is not null ? CountCode().Replace(p.Value, "<code>") : p.Value)})"));
                // A Summon procedure is a way to Summon the card, not something applied in turn with the lasting effects.
                var procedure = registered.GetValueOrDefault("SetCode")?.StartsWith("EFFECT_SPSUMMON_PROC", StringComparison.Ordinal) == true;
                registrations.Add((text, !Activated().IsMatch(type) && !procedure, code));
                continue;
            }
            registrations.Add(("line:" + c.Line(line, Variables), false, null));
        }

        // Which effects share a once-per-turn code is what counts, not the code: each code gets a name from the effects
        // that use it, the same whatever order they are in.
        var names = registrations.Where(r => r.Code is not null).GroupBy(r => r.Code!)
            .Select(g => (Code: g.Key, Key: string.Join("\n", g.Select(r => r.Text).Order(StringComparer.Ordinal))))
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select((g, i) => (g.Code, Name: $"<code{i}>")).ToDictionary(g => g.Code, g => g.Name);
        string Named((string Text, bool Lasting, string? Code) r) => r.Code is null ? r.Text : r.Text.Replace("<code>", names[r.Code], StringComparison.Ordinal);
        var all = registrations.Select(Named).Order(StringComparer.Ordinal).ToList();
        var lasting = registrations.Where(r => r.Lasting).Select(Named).ToList();
        return new Form(prelude.Order(StringComparer.Ordinal).ToList(), all, lasting);
    }

    /// <summary>Effect variables (e1, e2b...) are named after the effect's place on the card.</summary>
    private static readonly Regex Variables = EffectVariable();

    private sealed class Canonicalizer(Dictionary<string, List<string>> functions, IReadOnlyList<string> strings, bool ignoreDescriptions, bool costOrder)
    {
        private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _counters = new(StringComparer.Ordinal);

        /// <summary>"Duel.AddCustomActivityCounter(id+1000000000,...)": a counter is named by what it counts, not its number.</summary>
        public void FindCounters(List<string> init)
        {
            foreach (var line in init)
                if (Counter().Match(line.Trim()) is { Success: true } m) _counters[m.Groups[1].Value] = "<counter:" + m.Groups[2].Value + ">";
        }

        /// <summary>One line with what depends on the effect's place replaced: functions by their contents, strings by their text.</summary>
        public string Line(string text, Regex? variables = null)
        {
            text = FunctionRef().Replace(text, m => functions.ContainsKey(m.Groups[1].Value) ? "<fn:" + Hash(m.Groups[1].Value) + ">" : m.Value);
            text = StringId().Replace(text, m =>
            {
                if (ignoreDescriptions) return "<str>";
                var n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                return n < strings.Count ? $"<str:{strings[n]}>" : $"<str#{n}>";
            });
            foreach (var (code, name) in _counters) text = text.Replace(code + ",", name + ",", StringComparison.Ordinal).Replace(code + ")", name + ")", StringComparison.Ordinal);
            text = Flag().Replace(text, "id+<flag>");
            if (variables is not null) text = variables.Replace(text, "<e>");
            return text;
        }

        private string Hash(string name)
        {
            if (_hashes.TryGetValue(name, out var h)) return h;
            if (!_busy.Add(name)) return "rec:" + name;
            var body = functions[name];
            var cost = costOrder && CostName().IsMatch(name);
            var lines = Conjunctions(body.Skip(1).Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)).ToList())
                .Select(l => Line(l)).ToList();
            lines = Locals(lines, cost);
            // A cost's steps in any order: "discard this card and pay 1000 LP" is "pay 1000 LP and discard this card".
            if (cost) lines = lines.Order(StringComparer.Ordinal).ToList();
            var text = body[0] + "\n" + string.Join("\n", lines);
            _busy.Remove(name);
            return _hashes[name] = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        }

        /// <summary>
        /// "return A\n\t\tand B" (and "if chk==0 then return A\n\t\tand B end"): checks tested together, in any order and once
        /// each, read the same.
        /// </summary>
        private static List<string> Conjunctions(List<string> body)
        {
            var result = new List<string>();
            for (int i = 0; i < body.Count; i++)
            {
                var line = body[i];
                var at = line.IndexOf("return ", StringComparison.Ordinal);
                if (at < 0) { result.Add(line); continue; }
                var head = line[..(at + 7)];
                var parts = new List<string> { line[(at + 7)..] };
                while (i + 1 < body.Count && body[i + 1].StartsWith("\t\tand ", StringComparison.Ordinal)) parts.Add(body[++i]["\t\tand ".Length..]);
                var tail = "";
                if (parts[^1].EndsWith(" end", StringComparison.Ordinal) && head.TrimStart().StartsWith("if ", StringComparison.Ordinal))
                {
                    parts[^1] = parts[^1][..^4];
                    tail = " end";
                }
                result.Add(head + string.Join(" and ", parts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) + tail);
            }
            return result;
        }

        /// <summary>Local variables named in the order they first appear (all alike for a cost whose steps may be reordered).</summary>
        private static List<string> Locals(List<string> lines, bool alike)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines)
                foreach (Match m in LocalDeclaration().Matches(line))
                    foreach (var v in m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries))
                        if (v.Length > 0 && v != "c" && v != "tp" && !names.ContainsKey(v)) names[v] = alike ? "<v>" : $"<v{names.Count}>";
            if (names.Count == 0) return lines;
            var pattern = new Regex(@"\b(" + string.Join("|", names.Keys.Select(Regex.Escape)) + @")\b");
            return lines.Select(l => pattern.Replace(l, m => names[m.Value])).ToList();
        }
    }

    [GeneratedRegex(@"^function s\.(\w+)(\(.*\))$")]
    private static partial Regex FunctionStart();

    [GeneratedRegex(@"^local (\w+)=Effect\.(?:CreateEffect\(c\)|GlobalEffect\(\))$")]
    private static partial Regex Create();

    [GeneratedRegex(@"^local (\w+)=(\w+):Clone\(\)$")]
    private static partial Regex Clone();

    [GeneratedRegex(@"^(\w+):(Set\w+)\((.*)\)$")]
    private static partial Regex Setter();

    [GeneratedRegex(@"^(c|Duel):RegisterEffect\((\w+)(.*)\)$")]
    private static partial Regex Register();

    /// <summary>"1,id+100+EFFECT_COUNT_CODE_OATH": the once-per-turn code, "id+100".</summary>
    [GeneratedRegex(@"(?<=^\d+,)(id(?:\+\d+)?)(?=\+EFFECT_COUNT_CODE_|$)")]
    private static partial Regex CountCode();

    [GeneratedRegex(@"EFFECT_TYPE_(TRIGGER_O|TRIGGER_F|QUICK_O|QUICK_F|IGNITION|ACTIVATE|FLIP)")]
    private static partial Regex Activated();

    [GeneratedRegex(@"\bs\.(\w+)")]
    private static partial Regex FunctionRef();

    [GeneratedRegex(@"aux\.Stringid\(id,(\d+)\)")]
    private static partial Regex StringId();

    [GeneratedRegex(@"^Duel\.AddCustomActivityCounter\((id\+\d+),(.+)\)$")]
    private static partial Regex Counter();

    [GeneratedRegex(@"\bid\+\d+\b")]
    private static partial Regex Flag();

    [GeneratedRegex(@"\be\d+[a-z]?\b")]
    private static partial Regex EffectVariable();

    [GeneratedRegex(@"^e\d+cost\d*$")]
    private static partial Regex CostName();

    [GeneratedRegex(@"\blocal\s+([\w\s,]+?)\s*(?:=|$)")]
    private static partial Regex LocalDeclaration();
}
