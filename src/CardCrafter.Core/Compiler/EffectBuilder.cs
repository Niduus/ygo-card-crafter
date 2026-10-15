using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>One step of an effect's resolution, produced by one DO block.</summary>
public sealed class OperationStep
{
    public required List<string> Lines { get; init; }
    /// <summary>Lua variable holding how many cards/points the step affected, for "if you do" links.</summary>
    public string? Result { get; init; }
    public ActionLink Link { get; init; }
}

/// <summary>
/// Accumulates everything the blocks of one effect contribute, then writes the effect object
/// and its condition, cost, target and operation functions.
/// </summary>
public sealed class EffectBuilder(ScriptBuilder script, EffectEvent source, int index)
{
    private int _localCounter;
    private readonly List<OperationStep> _steps = [];
    private readonly List<(Func<string, IEnumerable<string>> Write, bool Limited)> _passives = [];

    public ScriptBuilder Script { get; } = script;
    public EffectEvent Source { get; } = source;
    /// <summary>1-based position of the effect on the card.</summary>
    public int Index { get; } = index;
    public string Var => $"e{Index}";
    public CardData Card => Script.Card;

    // Effect object settings, filled in by the trigger block.
    public List<string> Types { get; } = [];
    public string? Code { get; set; }
    public List<string> ExtraCodes { get; } = [];
    public string? Range { get; set; }
    public string? HintTiming { get; set; }
    public bool IsMandatory { get; set; }
    public bool IsActivation { get; set; }
    /// <summary>True for "while face-up" effects that never activate.</summary>
    public bool IsPassive { get; set; }
    /// <summary>True for a Special Summon procedure (EFFECT_SPSUMMON_PROC), which never starts a chain.</summary>
    public bool IsSummonProcedure { get; set; }
    /// <summary>For a Summon procedure: the zones it Summons to, as a mask ("0x4" is the center Main Monster Zone), or null for any.</summary>
    public string? ProcedureZone { get; set; }

    public List<string> Categories { get; } = [];
    public List<string> Properties { get; } = [];
    public List<string> Conditions { get; } = [];
    public List<string> CostChecks { get; } = [];
    public List<string> CostLines { get; } = [];
    public List<string> TargetChecks { get; } = [];
    public List<string> TargetLines { get; } = [];
    public string? Chkc { get; private set; }

    public void AddCategory(string c) { if (!Categories.Contains(c)) Categories.Add(c); }
    public void AddProperty(string p) { if (!Properties.Contains(p)) Properties.Add(p); }

    public string NewLocal(string prefix) => prefix + (++_localCounter);

    public void Error(string message) => Script.Diagnostics.Add(new(Severity.Error, message, Index));
    public void Warn(string message) => Script.Diagnostics.Add(new(Severity.Warning, message, Index));

    /// <summary>Registers the effect's single targeting check. Effects can target with one block only.</summary>
    public void SetChkc(string check)
    {
        if (Chkc is not null)
        {
            Error("Only one block per effect can target. Turn off \"target\" on the other blocks.");
            return;
        }
        Chkc = check;
        AddProperty("EFFECT_FLAG_CARD_TARGET");
    }

    public void AddStep(IEnumerable<string> lines, string? result, ActionLink link) =>
        _steps.Add(new OperationStep { Lines = lines.ToList(), Result = result, Link = link });

    /// <summary>Adds a lasting effect. The callback writes the effect given its variable name.</summary>
    public void AddPassive(Func<string, IEnumerable<string>> write, bool limited = false) => _passives.Add((write, limited));

    /// <summary>True when a lasting effect takes the effect's "once per turn", like "Tribute this card instead".</summary>
    public bool HasLimitedPassive => _passives.Any(p => p.Limited);

    public bool HasSteps => _steps.Count > 0;

    /// <summary>The Lua variable with how many cards the last DO step affected, for "for each of those cards".</summary>
    public string? LastResult => _steps.LastOrDefault(s => s.Result is not null)?.Result;

    /// <summary>True once a COST block has stored how many cards it used (in the effect's label).</summary>
    public bool CostCounts { get; set; }
    public bool HasPassives => _passives.Count > 0;

    /// <summary>Writes the lines that go inside initial_effect for this effect.</summary>
    public List<string> Build(string? countLimit)
    {
        return IsPassive ? BuildPassives(countLimit) : IsSummonProcedure ? BuildProcedure(countLimit) : BuildActivated(countLimit);
    }

    /// <summary>
    /// A Special Summon procedure: the condition gets the card being Summoned and checks the IF blocks and that
    /// the costs can be paid; the operation pays them. Costs are paid "to Special Summon", not as an effect's cost.
    /// </summary>
    private List<string> BuildProcedure(string? countLimit)
    {
        var lines = new List<string> { $"--{Lua.Comment(Source.Label)}", $"local {Var}=Effect.CreateEffect(c)" };
        if (Index <= 16) lines.Add($"{Var}:SetDescription(aux.Stringid(id,{Index - 1}))");
        lines.Add($"{Var}:SetType({Lua.Join(Types)})");
        lines.Add($"{Var}:SetCode({Code})");
        if (Properties.Count > 0) lines.Add($"{Var}:SetProperty({Lua.Join(Properties)})");
        if (Range is not null) lines.Add($"{Var}:SetRange({Range})");
        if (countLimit is not null) lines.Add($"{Var}:SetCountLimit({countLimit})");

        var payLines = CostLines.Select(l => l.Replace("REASON_COST", "REASON_SPSUMMON")).ToList();
        // Tributing frees a zone, so only check for an empty one when nothing on the field is Tributed.
        var checks = new List<string>();
        if (ProcedureZone is not null)
            checks.Add($"Duel.GetLocationCount(tp,LOCATION_MZONE,tp,LOCATION_REASON_TOFIELD,{ProcedureZone})>0");
        else if (!payLines.Any(l => l.Contains("Duel.Release(", StringComparison.Ordinal)))
            checks.Add("Duel.GetLocationCount(tp,LOCATION_MZONE)>0");
        checks.AddRange(Conditions);
        checks.AddRange(CostChecks);
        var con = new List<string> { "if c==nil then return true end", "local tp=c:GetControler()" };
        con.AddRange(ReturnAll(checks));
        lines.Add($"{Var}:SetCondition({Script.AddFunction(Var + "con", "e,c", con)})");
        if (payLines.Count > 0)
            lines.Add($"{Var}:SetOperation({Script.AddFunction(Var + "op", "e,tp,eg,ep,ev,re,r,rp,c", payLines)})");
        if (ProcedureZone is not null)
            lines.Add($"{Var}:SetValue({Script.AddFunction(Var + "zone", "e,c", [$"return 0,{ProcedureZone}"])})");
        lines.Add($"c:RegisterEffect({Var})");
        return lines;
    }

    private List<string> BuildActivated(string? countLimit)
    {
        var p = $"e{Index}";
        var lines = new List<string> { $"--{Lua.Comment(Source.Label)}", $"local {Var}=Effect.CreateEffect(c)" };
        if (Index <= 16) lines.Add($"{Var}:SetDescription(aux.Stringid(id,{Index - 1}))");
        if (Categories.Count > 0) lines.Add($"{Var}:SetCategory({Lua.Join(Categories)})");
        lines.Add($"{Var}:SetType({Lua.Join(Types)})");
        if (Code is not null) lines.Add($"{Var}:SetCode({Code})");
        if (Properties.Count > 0) lines.Add($"{Var}:SetProperty({Lua.Join(Properties)})");
        if (Range is not null) lines.Add($"{Var}:SetRange({Range})");
        if (HintTiming is not null) lines.Add($"{Var}:SetHintTiming(0,{HintTiming})");
        if (countLimit is not null) lines.Add($"{Var}:SetCountLimit({countLimit})");

        if (Conditions.Count > 0)
        {
            var fn = Script.AddFunction(p + "con", "e,tp,eg,ep,ev,re,r,rp", ReturnAll(Conditions));
            lines.Add($"{Var}:SetCondition({fn})");
        }
        if (CostChecks.Count > 0 || CostLines.Count > 0)
        {
            var body = new List<string> { $"if chk==0 then return {AllOrTrue(CostChecks)} end" };
            body.AddRange(CostLines);
            var fn = Script.AddFunction(p + "cost", "e,tp,eg,ep,ev,re,r,rp,chk", body);
            lines.Add($"{Var}:SetCost({fn})");
        }
        if (TargetChecks.Count > 0 || TargetLines.Count > 0 || Chkc is not null)
        {
            var body = new List<string>();
            if (Chkc is not null) body.Add($"if chkc then return {Chkc} end");
            // Mandatory effects always activate, even if they will not fully resolve.
            body.Add($"if chk==0 then return {(IsMandatory ? "true" : AllOrTrue(TargetChecks))} end");
            body.AddRange(TargetLines);
            var args = "e,tp,eg,ep,ev,re,r,rp,chk" + (Chkc is not null ? ",chkc" : "");
            var fn = Script.AddFunction(p + "tg", args, body);
            lines.Add($"{Var}:SetTarget({fn})");
        }
        if (_steps.Count > 0)
        {
            var fn = Script.AddFunction(p + "op", "e,tp,eg,ep,ev,re,r,rp", BuildOperation());
            lines.Add($"{Var}:SetOperation({fn})");
        }
        lines.Add($"c:RegisterEffect({Var})");

        char suffix = 'b';
        foreach (var code in ExtraCodes)
        {
            var clone = $"{Var}{suffix++}";
            lines.Add($"local {clone}={Var}:Clone()");
            lines.Add($"{clone}:SetCode({code})");
            lines.Add($"c:RegisterEffect({clone})");
        }
        return lines;
    }

    private List<string> BuildPassives(string? countLimit)
    {
        var lines = new List<string> { $"--{Lua.Comment(Source.Label)}" };
        string? con = null;
        if (Conditions.Count > 0)
        {
            var body = new List<string> { "local tp=e:GetHandlerPlayer()" };
            body.AddRange(ReturnAll(Conditions));
            con = Script.AddFunction($"e{Index}con", "e", body);
        }
        char suffix = 'a';
        foreach (var (write, limited) in _passives)
        {
            var v = _passives.Count == 1 ? Var : $"{Var}{suffix++}";
            var body = write(v).ToList();
            lines.Add($"local {v}=Effect.CreateEffect(c)");
            lines.AddRange(body);
            if (limited && countLimit is not null) lines.Add($"{v}:SetCountLimit({countLimit})");
            if (con is not null) lines.Add($"{v}:SetCondition({con})");
            lines.Add($"c:RegisterEffect({v})");
        }
        return lines;
    }

    /// <summary>Chains the DO steps, nesting the ones that depend on the step before.</summary>
    private List<string> BuildOperation()
    {
        var body = new List<string> { "local c=e:GetHandler()" };
        var indent = "";
        int opened = 0;
        string? previous = null;
        foreach (var step in _steps)
        {
            if (previous is not null)
            {
                switch (step.Link)
                {
                    case ActionLink.IfYouDo:
                        body.Add($"{indent}if {previous}>0 then");
                        indent += "\t";
                        opened++;
                        break;
                    case ActionLink.Then:
                        body.Add($"{indent}if {previous}>0 then");
                        indent += "\t";
                        opened++;
                        body.Add($"{indent}Duel.BreakEffect()");
                        break;
                    case ActionLink.AlsoAfterThat:
                        body.Add($"{indent}Duel.BreakEffect()");
                        break;
                }
            }
            body.AddRange(step.Lines.Select(l => indent + l));
            previous = step.Result ?? previous;
        }
        for (; opened > 0; opened--)
        {
            indent = indent[..^1];
            body.Add($"{indent}end");
        }
        return body;
    }

    private static string AllOrTrue(List<string> checks) =>
        checks.Count == 0 ? "true" : string.Join("\n\t\tand ", checks);

    private static List<string> ReturnAll(List<string> checks) => [$"return {string.Join("\n\t\tand ", checks)}"];
}
