using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CardCrafter.Core.Compiler;

namespace CardCrafter.Core.Validation;

/// <summary>
/// Checks a card script before it ever reaches the game: real Lua 5.4 syntax checking,
/// plus a warning for any engine function or constant that doesn't exist.
/// </summary>
public static partial class ScriptChecker
{
    private static readonly Lazy<KnownApi> Api = new(KnownApi.Load);

    public static List<Diagnostic> Check(string lua)
    {
        var result = new List<Diagnostic>();
        var syntax = SyntaxError(lua);
        if (syntax is not null)
        {
            result.Add(new Diagnostic(Severity.Error, "Lua syntax error: " + syntax));
            return result;
        }
        foreach (var name in UnknownNames(lua))
            result.Add(new Diagnostic(Severity.Warning, $"\"{name}\" is not a known engine function or constant. Check the spelling."));
        if (!lua.Contains("initial_effect", StringComparison.Ordinal))
            result.Add(new Diagnostic(Severity.Error, "The script has no initial_effect function, so the game can't load the card."));
        return result;
    }

    /// <summary>The Lua compiler's error message, or null when the script parses.</summary>
    public static string? SyntaxError(string lua)
    {
        using var state = new KeraLua.Lua();
        var status = state.LoadString(lua, "card");
        if (status == KeraLua.LuaStatus.OK) return null;
        var message = state.ToString(-1) ?? "unknown error";
        // Messages look like [string "card"]:12: ... ; keep the line number, drop the chunk name.
        return ChunkName().Replace(message, "line ");
    }

    public static IEnumerable<string> UnknownNames(string lua)
    {
        var api = Api.Value;
        var code = StripStringsAndComments(lua);
        var seen = new HashSet<string>();
        foreach (Match m in Qualified().Matches(code))
        {
            var lib = m.Groups[1].Value == "Auxiliary" ? "aux" : m.Groups[1].Value;
            var name = $"{lib}.{m.Groups[2].Value}";
            if (!api.Names.Contains(name) && seen.Add(name)) yield return name;
        }
        foreach (Match m in MethodCall().Matches(code))
        {
            var name = m.Groups[1].Value;
            if (!api.Methods.Contains(name) && seen.Add(":" + name)) yield return ":" + name;
        }
        foreach (Match m in Constant().Matches(code))
        {
            var name = m.Value;
            if (!api.Constants.Contains(name) && seen.Add(name)) yield return name;
        }
    }

    /// <summary>Blanks out comments and string literals so names inside them are not checked.</summary>
    public static string StripStringsAndComments(string lua)
    {
        var sb = new StringBuilder(lua.Length);
        int i = 0;
        while (i < lua.Length)
        {
            if (lua[i] == '-' && i + 1 < lua.Length && lua[i + 1] == '-')
            {
                i += 2;
                if (TryLongBracket(lua, i, out var end)) { i = end; continue; }
                while (i < lua.Length && lua[i] != '\n') i++;
                continue;
            }
            if (lua[i] == '[' && TryLongBracket(lua, i, out var strEnd))
            {
                sb.Append("\"\"");
                i = strEnd;
                continue;
            }
            if (lua[i] is '"' or '\'')
            {
                var quote = lua[i++];
                while (i < lua.Length && lua[i] != quote && lua[i] != '\n')
                {
                    if (lua[i] == '\\') i++;
                    i++;
                }
                i++;
                sb.Append("\"\"");
                continue;
            }
            sb.Append(lua[i++]);
        }
        return sb.ToString();
    }

    /// <summary>Recognises [[...]] and [==[...]==] starting at <paramref name="start"/>.</summary>
    private static bool TryLongBracket(string s, int start, out int end)
    {
        end = start;
        if (start >= s.Length || s[start] != '[') return false;
        int j = start + 1, level = 0;
        while (j < s.Length && s[j] == '=') { level++; j++; }
        if (j >= s.Length || s[j] != '[') return false;
        var close = "]" + new string('=', level) + "]";
        var idx = s.IndexOf(close, j + 1, StringComparison.Ordinal);
        end = idx < 0 ? s.Length : idx + close.Length;
        return true;
    }

    [GeneratedRegex(@"\[string ""card""\]:")]
    private static partial Regex ChunkName();

    [GeneratedRegex(@"\b(Duel|Card|Effect|Group|Debug|aux|Auxiliary)\.([A-Za-z_]\w*)")]
    private static partial Regex Qualified();

    [GeneratedRegex(@":([A-Za-z_]\w*)\s*\(")]
    private static partial Regex MethodCall();

    [GeneratedRegex(@"\b[A-Z][A-Z0-9]*_[A-Z0-9_]+\b")]
    private static partial Regex Constant();

    private sealed class KnownApi
    {
        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Methods { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Constants { get; } = new(StringComparer.Ordinal);

        public static KnownApi Load()
        {
            var api = new KnownApi();
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KnownApi.txt")
                ?? throw new InvalidOperationException("KnownApi.txt resource is missing.");
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("const.", StringComparison.Ordinal))
                {
                    api.Constants.Add(line[6..]);
                    continue;
                }
                api.Names.Add(line);
                var dot = line.IndexOf('.');
                var lib = line[..dot];
                // Card, Effect and Group functions are also called as methods: c:IsCode(...).
                if (lib is "Card" or "Effect" or "Group") api.Methods.Add(line[(dot + 1)..]);
            }
            return api;
        }
    }
}
