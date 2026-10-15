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

    private readonly List<string> _initial = [];
    private readonly Dictionary<string, string> _counters = [];

    /// <summary>Lines that go first in initial_effect, once each, such as an activity counter.</summary>
    public IReadOnlyList<string> InitialLines => _initial;

    public void AddInitialLine(string line)
    {
        if (!_initial.Contains(line)) _initial.Add(line);
    }

    /// <summary>
    /// Counts what each player did this turn (Special Summons, activations, attacks) for "the turn you activate this"
    /// locks. One counter per activity: two locks on the same activity must count the same cards.
    /// </summary>
    public bool AddActivityCounter(string activity, string filter)
    {
        if (_counters.TryGetValue(activity, out var existing)) return existing == filter;
        _counters[activity] = filter;
        AddInitialLine($"Duel.AddCustomActivityCounter(id,{activity},{filter})");
        return true;
    }

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
        if (f.PointedToByThisCard && !Card.IsLink && !Diagnostics.Any(d => d.Message.StartsWith("\"Cards this card points to\"", StringComparison.Ordinal)))
            Diagnostics.Add(new(Severity.Warning, "\"Cards this card points to\" only works on Link monsters; other cards point to nothing."));
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
    public string MaterialFilter(CardFilter f, string baseName = "matfilter", SummonProcedureKind kind = SummonProcedureKind.None)
    {
        var parts = FilterCompiler.Conditions(new CardFilter
        {
            CardType = f.CardType, MonsterKind = f.MonsterKind, Race = f.Race, Attribute = f.Attribute, Archetype = f.Archetype, CardCode = f.CardCode,
            ExceptOwnName = f.ExceptOwnName, ExceptCode = f.ExceptCode,
            LevelMin = f.LevelMin, LevelMax = f.LevelMax, AtkMin = f.AtkMin, AtkMax = f.AtkMax,
        }, "c", kind);
        parts.Remove("c:IsType(TYPE_MONSTER)");
        if (parts.Count == 0) return "nil";
        return AddFunction(baseName, "c", [$"return {string.Join(" and ", parts)}"]);
    }
}
