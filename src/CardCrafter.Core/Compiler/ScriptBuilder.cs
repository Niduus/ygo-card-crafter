using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>Collects the helper functions of one card script and keeps their names unique.</summary>
public sealed class ScriptBuilder(CardProject project, CardPack? pack)
{
    private readonly List<string> _functions = [];
    private readonly HashSet<string> _names = [];
    private readonly Dictionary<string, string> _byBody = [];

    public CardProject Project { get; } = project;
    public CardPack? Pack { get; } = pack;
    public CardData Card => Project.Card;
    public List<Diagnostic> Diagnostics { get; } = [];

    public IReadOnlyList<string> Functions => _functions;

    /// <summary>
    /// Adds "function s.name(args) ... end" and returns "s.name".
    /// Identical functions are shared, so two blocks with the same filter reuse one function.
    /// </summary>
    public string AddFunction(string baseName, string args, IEnumerable<string> body)
    {
        var bodyText = string.Join("\n", body.Select(l => "\t" + l));
        var key = args + "\n" + bodyText;
        if (_byBody.TryGetValue(key, out var existing)) return existing;

        var name = baseName;
        for (int n = 2; !_names.Add(name); n++) name = baseName + n;
        var full = "s." + name;
        _functions.Add($"function {full}({args})\n{bodyText}\nend");
        _byBody[key] = full;
        return full;
    }

    /// <summary>Compiles a card filter to a function taking (c,e,tp). <paramref name="extra"/> adds a check such as "c:IsAbleToHand()".</summary>
    public FilterRef Filter(CardFilter f, string? extra = null, string baseName = "filter")
    {
        var parts = FilterCompiler.Conditions(f);
        if (!string.IsNullOrEmpty(extra)) parts.Add(extra);
        var expr = parts.Count == 0 ? "true" : string.Join(" and ", parts);
        var fn = AddFunction(baseName, "c,e,tp", [$"return {expr}"]);
        var (self, opp) = Lua.Range(f);
        return new FilterRef
        {
            Function = fn,
            Self = self,
            Opp = opp,
            Exclude = f.ExcludeThisCard ? "e:GetHandler()" : "nil",
            Filter = f,
        };
    }

    /// <summary>A material filter for summon procedures: a function of (c), or "nil" when anything goes.</summary>
    public string MaterialFilter(CardFilter f, string baseName = "matfilter")
    {
        var parts = FilterCompiler.Conditions(f);
        parts.Remove("c:IsType(TYPE_MONSTER)");
        if (parts.Count == 0) return "nil";
        return AddFunction(baseName, "c", [$"return {string.Join(" and ", parts)}"]);
    }
}
