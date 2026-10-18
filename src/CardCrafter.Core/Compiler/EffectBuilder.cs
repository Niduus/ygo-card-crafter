using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>One step of an effect's resolution, produced by one DO block.</summary>
public sealed class OperationStep
{
    public required List<string> Lines { get; init; }
    /// <summary>Lua variable holding how many cards/points the step affected, for "if you do" links.</summary>
    public string? Result { get; init; }
    public ActionLink Link { get; init; }
    /// <summary>"You can ...": the player is asked before it happens.</summary>
    public bool Optional { get; init; }
    /// <summary>For an optional step: what must hold for the player to be asked at all (cards to add are there).</summary>
    public List<string> AskIf { get; } = [];
    /// <summary>Which option of "Activate 1 of these effects" it belongs to; 0 when the effect has no options.</summary>
    public int Branch { get; init; }
    /// <summary>For an IF block among the DO blocks: the condition the following steps need ("then if you control ...").</summary>
    public string? Gate { get; init; }
    /// <summary>
    /// The Lua group of the cards the step affected, for "that card" after it, when the engine doesn't keep them as its
    /// operated group (placing cards face-up). Null for Duel.GetOperatedGroup().
    /// </summary>
    public string? Affected { get; set; }
    /// <summary>
    /// For a gate on a coin toss or die roll's result ("if the result is heads"): it ends the steps of the result before
    /// it, so "● Heads: ..." and "● Tails: ..." are each their own.
    /// </summary>
    public bool Case { get; init; }
}

/// <summary>What one option of "Activate 1 of these effects" checks and does at activation.</summary>
public sealed class BranchTarget
{
    public List<string> Checks { get; } = [];
    public List<string> Lines { get; } = [];
    public List<string> Categories { get; } = [];
    public string? Chkc { get; set; }
    /// <summary>The option's own cost ("● Discard 1 card; ..."): paid when it is chosen, if the effect pays costs.</summary>
    public List<string> CostChecks { get; } = [];
    public List<string> CostLines { get; } = [];
    /// <summary>"1 or both": the options this choice does both of, in order; null for an option of its own.</summary>
    public List<int>? Combines { get; set; }
    /// <summary>Index of its aux.Stringid description.</summary>
    public int StringIndex { get; set; }
}

/// <summary>
/// An "or when" trigger that can't share the effect object (another kind of trigger, used from another place, or
/// "When" next to "If"): a clone of the effect with its own type, event, zone, properties and checks.
/// </summary>
public sealed class TriggerVariant
{
    public required List<string> Types { get; init; }
    public required string Code { get; init; }
    public List<string> ExtraCodes { get; init; } = [];
    public string? Range { get; init; }
    public List<string> Properties { get; init; } = [];
    public string? HintTiming { get; init; }
    /// <summary>What this trigger itself checks; the IF blocks' checks are added to it.</summary>
    public List<string> Conditions { get; init; } = [];
}

/// <summary>
/// Accumulates everything the blocks of one effect contribute, then writes the effect object
/// and its condition, cost, target and operation functions.
/// </summary>
public sealed class EffectBuilder(ScriptBuilder script, EffectEvent source, int index)
{
    private int _localCounter;
    private readonly List<OperationStep> _steps = [];
    private readonly List<(Func<string, IEnumerable<string>> Write, bool Limited, string? Condition)> _passives = [];

    public ScriptBuilder Script { get; } = script;
    public EffectEvent Source { get; } = source;
    /// <summary>1-based position of the effect on the card.</summary>
    public int Index { get; } = index;
    public string Var => $"e{Index}";
    public CardData Card => CardOverride ?? Script.Card;
    /// <summary>The card the effect's blocks see as "this card", when it isn't the card itself: a monster given the effect (Blaze Cannon).</summary>
    public CardData? CardOverride { get; set; }

    // Effect object settings, filled in by the trigger block.
    public List<string> Types { get; } = [];
    public string? Code { get; set; }
    public List<string> ExtraCodes { get; } = [];
    /// <summary>"Or when" triggers written as clones with their own settings.</summary>
    public List<TriggerVariant> Variants { get; } = [];
    /// <summary>How many of <see cref="Conditions"/> come first from the WHEN blocks (the rest are shared by every trigger).</summary>
    public int TriggerConditionCount { get; set; }
    /// <summary>The properties the first WHEN block set, which its variants replace with their own.</summary>
    public List<string> TriggerProperties { get; } = [];
    /// <summary>True when the effect is registered more than once (several events), so a soft once per turn must be shared.</summary>
    public bool HasClones => ExtraCodes.Count > 0 || Variants.Count > 0;
    public string? Range { get; set; }
    public string? HintTiming { get; set; }
    public bool IsMandatory { get; set; }
    public bool IsActivation { get; set; }
    /// <summary>True for "while face-up" effects that never activate.</summary>
    public bool IsPassive { get; set; }
    /// <summary>
    /// For lasting effects an Xyz Monster gains while this card is its material (EFFECT_TYPE_XMATERIAL): what that Xyz
    /// Monster must be, as a check of e:GetHandler(); null for this card's own lasting effects.
    /// </summary>
    public string? XyzMaterialCondition { get; set; }
    /// <summary>True for a Special Summon procedure (EFFECT_SPSUMMON_PROC), which never starts a chain.</summary>
    public bool IsSummonProcedure { get; set; }
    /// <summary>For a Summon procedure: the zones it Summons to, as a mask ("0x4" is the center Main Monster Zone), or null for any.</summary>
    public string? ProcedureZone { get; set; }
    /// <summary>For a Summon procedure: it Summons this card from the Extra Deck, so it needs a zone an Extra Deck monster can go to.</summary>
    public bool ProcedureFromExtra { get; set; }
    /// <summary>For a Summon procedure: the position it Summons in ("POS_FACEUP_ATTACK"), or null for the player's choice.</summary>
    public string? ProcedurePosition { get; set; }

    public List<string> Categories { get; } = [];
    public List<string> Properties { get; } = [];
    public List<string> Conditions { get; } = [];
    public List<string> CostChecks { get; } = [];
    public List<string> CostLines { get; } = [];
    public List<string> TargetChecks { get; } = [];
    public List<string> TargetLines { get; } = [];
    public string? Chkc { get; private set; }

    /// <summary>The option of "Activate 1 of these effects" the block being compiled belongs to (0 for none).</summary>
    public int CurrentBranch { get; set; }
    /// <summary>Whether the DO block being compiled is optional ("you can ...").</summary>
    public bool CurrentOptional { get; set; }
    /// <summary>Per option: its activation checks, targeting and categories.</summary>
    public SortedDictionary<int, BranchTarget> Branches { get; } = [];
    public BranchTarget BranchOf(int branch) => Branches.TryGetValue(branch, out var b) ? b : Branches[branch] = new BranchTarget();

    /// <summary>Who chooses the option of "Activate 1 of these effects": "tp", or "1-tp" for "your opponent chooses".</summary>
    public string OptionChooser { get; set; } = "tp";
    /// <summary>The option is chosen as the effect resolves ("apply 1 of these effects"), not as it is activated.</summary>
    public bool OptionsOnResolution { get; set; }
    /// <summary>"You can only use each effect once per turn": the player flag each option keeps, or null.</summary>
    public Func<int, string>? OptionFlag { get; set; }
    /// <summary>"Activate 1 or both of these effects": a third choice does both options.</summary>
    public bool OptionBoth { get; set; }
    /// <summary>"Apply these effects in sequence": every option that can be applied is, in order, as the effect resolves.</summary>
    public bool OptionsInSequence { get; set; }
    /// <summary>"Activate this effect based on where it was sent from": the one option that can be used is taken, without asking.</summary>
    public bool OptionsAutomatic { get; set; }

    public void AddCategory(string c) { if (!Categories.Contains(c)) Categories.Add(c); }
    public void AddProperty(string p) { if (!Properties.Contains(p)) Properties.Add(p); }

    public string NewLocal(string prefix) => prefix + (++_localCounter);

    public void Error(string message) => Script.Diagnostics.Add(new(Severity.Error, message, Index));
    public void Warn(string message) => Script.Diagnostics.Add(new(Severity.Warning, message, Index));

    /// <summary>Registers the effect's single targeting check. Effects can target with one block only.</summary>
    public void SetChkc(string check)
    {
        if (CurrentBranch > 0)
        {
            // Each option can target on its own.
            var branch = BranchOf(CurrentBranch);
            if (branch.Chkc is not null) { Error("Only one block per option can target. Turn off \"target\" on the other blocks."); return; }
            branch.Chkc = check;
            AddProperty("EFFECT_FLAG_CARD_TARGET");
            return;
        }
        if (Chkc is not null)
        {
            Error("Only one block per effect can target. Turn off \"target\" on the other blocks.");
            return;
        }
        Chkc = check;
        AddProperty("EFFECT_FLAG_CARD_TARGET");
    }

    public OperationStep AddStep(IEnumerable<string> lines, string? result, ActionLink link)
    {
        var step = new OperationStep { Lines = lines.ToList(), Result = result, Link = link, Optional = CurrentOptional, Branch = CurrentBranch };
        _steps.Add(step);
        return step;
    }

    private readonly List<string> _finally = [];

    /// <summary>Lines the operation starts with, before any step: "instead of conducting your normal draw" gives the draw up.</summary>
    public List<string> OperationPrologue { get; } = [];

    /// <summary>
    /// Lines that run at the end of the operation whatever happened before, like Dominus' "If you activated this card
    /// from your hand, you cannot ... for the rest of this Duel."
    /// </summary>
    public void AddFinally(IEnumerable<string> lines) => _finally.AddRange(lines);

    /// <summary>An IF block among the DO blocks: the steps after it in its option happen only if the condition holds.</summary>
    /// <param name="isCase">A coin toss or die roll's result: it ends the steps of the result gate before it.</param>
    public void AddGate(string condition, ActionLink link, bool isCase = false) =>
        _steps.Add(new OperationStep { Lines = [], Link = link, Branch = CurrentBranch, Gate = condition, Case = isCase });

    /// <summary>
    /// Adds a lasting effect. The callback writes the effect given its variable name. <paramref name="condition"/> is what this
    /// lasting effect alone needs besides the IF blocks ("a "Therion" monster equipped with this card"), checked with them.
    /// </summary>
    public void AddPassive(Func<string, IEnumerable<string>> write, bool limited = false, string? condition = null) => _passives.Add((write, limited, condition));

    /// <summary>True when a lasting effect takes the effect's "once per turn", like "Tribute this card instead".</summary>
    public bool HasLimitedPassive => _passives.Any(p => p.Limited);

    public bool HasSteps => _steps.Count > 0 || _finally.Count > 0;

    /// <summary>The number of steps so far, to find the ones a block just added.</summary>
    public int StepCount => _steps.Count;

    /// <summary>
    /// "When this card is activated: You can add 1 ...": the checks an optional step made for the activation are asked
    /// when it resolves instead, so a card with nothing to add can still be activated.
    /// </summary>
    public void AskOnResolution(int fromStep, int fromCheck)
    {
        var checks = TargetChecks.Skip(fromCheck).ToList();
        TargetChecks.RemoveRange(fromCheck, checks.Count);
        foreach (var step in _steps.Skip(fromStep).Where(s => s.Optional)) step.AskIf.AddRange(checks);
    }

    /// <summary>The Lua variable with how many cards the last DO step affected, for "for each of those cards".</summary>
    public string? LastResult => _steps.LastOrDefault(s => s.Result is not null)?.Result;

    /// <summary>The DO step <see cref="LastResult"/> belongs to, for a later step that needs it kept ("the damage inflicted").</summary>
    public OperationStep? LastResultStep => _steps.LastOrDefault(s => s.Result is not null);

    /// <summary>
    /// "Pay 800 LP for each target": the LP the next DO block that targets pays for each of its targets, as they are
    /// chosen (no more targets than the LP can pay for). Null when the effect has no such cost.
    /// </summary>
    public string? LpPerTarget { get; set; }

    /// <summary>The DO step of the effect that Tributed cards, for "the combined original ATK of the Tributed monsters".</summary>
    public OperationStep? TributeStep { get; set; }

    /// <summary>True once a COST block has stored how many cards it used (in the effect's label).</summary>
    public bool CostCounts { get; set; }

    /// <summary>
    /// In a COST line, the most cards a cost of "any number" may take: what a later "target that many" block can take
    /// (<see cref="CostCap"/>), or the cost's own most (<see cref="CostCapFallback"/>).
    /// </summary>
    public const string CostCapToken = "%costcap%";
    public string? CostCap { get; set; }
    public string? CostCapFallback { get; set; }
    public bool HasPassives => _passives.Count > 0;

    /// <summary>Writes the lines that go inside initial_effect for this effect.</summary>
    public List<string> Build(string? countLimit)
    {
        for (var i = 0; i < CostLines.Count; i++)
            CostLines[i] = CostLines[i].Replace(CostCapToken, CostCap ?? CostCapFallback ?? "1", StringComparison.Ordinal);
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
        if (ProcedurePosition is not null) lines.Add($"{Var}:SetTargetRange({ProcedurePosition},0)");
        if (Range is not null) lines.Add($"{Var}:SetRange({Range})");
        if (countLimit is not null) lines.Add($"{Var}:SetCountLimit({countLimit})");

        var payLines = CostLines.Select(l => l.Replace("REASON_COST", "REASON_SPSUMMON")).ToList();
        // Tributing frees a zone, so only check for an empty one when nothing on the field is Tributed.
        var checks = new List<string>();
        var tributes = payLines.Any(l => l.Contains("Duel.Release(", StringComparison.Ordinal));
        if (ProcedureFromExtra)
        {
            // From the Extra Deck: a Main Monster Zone it may use (a Link Arrow's) or an Extra Monster Zone.
            if (!tributes) checks.Add("Duel.GetLocationCountFromEx(tp,tp,nil,c)>0");
        }
        else if (ProcedureZone is not null)
            checks.Add($"Duel.GetLocationCount(tp,LOCATION_MZONE,tp,LOCATION_REASON_TOFIELD,{ProcedureZone})>0");
        else if (!tributes)
            checks.Add("Duel.GetLocationCount(tp,LOCATION_MZONE)>0");
        checks.AddRange(Conditions);
        checks.AddRange(CostChecks);
        var con = new List<string> { "if c==nil then return true end", "local tp=c:GetControler()" };
        con.AddRange(ReturnAll(checks));
        lines.Add($"{Var}:SetCondition({Script.AddFunction(Var + "con", "e,c", con)})");
        if (payLines.Count > 0)
            lines.Add($"{Var}:SetOperation({Script.AddFunction(Var + "op", "e,tp,eg,ep,ev,re,r,rp,c", payLines)})");
        // "Special Summoned this way": the Summon carries SUMMON_VALUE_SELF, as official procedures do.
        lines.Add(ProcedureZone is not null
            ? $"{Var}:SetValue({Script.AddFunction(Var + "zone", "e,c", [$"return SUMMON_VALUE_SELF,{ProcedureZone}"])})"
            : $"{Var}:SetValue(SUMMON_VALUE_SELF)");
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
        if (Branches.Count > 0 && OptionsOnResolution)
        {
            var fn = Script.AddFunction(p + "tg", "e,tp,eg,ep,ev,re,r,rp,chk" + (Chkc is not null ? ",chkc" : ""), BuildResolvingTarget());
            lines.Add($"{Var}:SetTarget({fn})");
        }
        else if (Branches.Count > 0)
        {
            var fn = Script.AddFunction(p + "tg", "e,tp,eg,ep,ev,re,r,rp,chk" + (Chkc is not null || Branches.Values.Any(b => b.Chkc is not null) ? ",chkc" : ""), BuildBranchedTarget());
            lines.Add($"{Var}:SetTarget({fn})");
        }
        else if (TargetChecks.Count > 0 || TargetLines.Count > 0 || Chkc is not null)
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
        if (_steps.Count > 0 || _finally.Count > 0 || OperationPrologue.Count > 0)
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
        foreach (var v in Variants)
        {
            var clone = $"{Var}{suffix++}";
            lines.Add($"local {clone}={Var}:Clone()");
            if (!v.Types.ToHashSet().SetEquals(Types)) lines.Add($"{clone}:SetType({Lua.Join(v.Types)})");
            lines.Add($"{clone}:SetCode({v.Code})");
            var props = Properties.Except(TriggerProperties).Union(v.Properties).ToList();
            if (!props.ToHashSet().SetEquals(Properties)) lines.Add($"{clone}:SetProperty({(props.Count == 0 ? "0" : Lua.Join(props))})");
            if (v.Range is not null && v.Range != Range) lines.Add($"{clone}:SetRange({v.Range})");
            if (v.HintTiming is not null && v.HintTiming != HintTiming) lines.Add($"{clone}:SetHintTiming(0,{v.HintTiming})");
            var conds = v.Conditions.Concat(Conditions.Skip(TriggerConditionCount)).ToList();
            if (!conds.SequenceEqual(Conditions))
                lines.Add($"{clone}:SetCondition({(conds.Count == 0 ? "aux.TRUE" : Script.AddFunction(p + "con", "e,tp,eg,ep,ev,re,r,rp", ReturnAll(conds)))})");
            lines.Add($"c:RegisterEffect({clone})");
            foreach (var code in v.ExtraCodes)
            {
                var more = $"{Var}{suffix++}";
                lines.Add($"local {more}={clone}:Clone()");
                lines.Add($"{more}:SetCode({code})");
                lines.Add($"c:RegisterEffect({more})");
            }
        }
        return lines;
    }

    /// <summary>
    /// "Activate 1 of these effects": the player picks an option that can be used, kept as the effect's second label;
    /// that option's targeting and categories apply.
    /// </summary>
    private List<string> BuildBranchedTarget()
    {
        var body = new List<string>();
        // The target the options share ("Target cards" before them).
        if (Chkc is not null) body.Add($"if chkc then return {Chkc} end");
        else if (Branches.Values.Any(b => b.Chkc is not null))
        {
            body.Add("if chkc then");
            body.Add("\tlocal _,op=e:GetLabel()");
            foreach (var (n, b) in Branches.Where(x => x.Value.Chkc is not null)) body.Add($"\tif op=={n} then return {b.Chkc} end");
            body.Add("\treturn false");
            body.Add("end");
        }
        foreach (var (n, b) in Branches)
            body.Add($"local b{n}={AllOrTrue(b.Checks)}" + (OptionFlag is { } flag && b.Combines is null ? $"\n\t\tand (not e:IsCostChecked() or Duel.GetFlagEffect(tp,{flag(n)})==0)" : "")
                     + (b.CostChecks.Count > 0 ? $"\n\t\tand (not e:IsCostChecked() or {string.Join(" and ", b.CostChecks)})" : ""));
        var any = "(" + string.Join(" or ", Branches.Keys.Select(n => $"b{n}")) + ")";
        var checks = new List<string>(TargetChecks) { any };
        body.Add($"if chk==0 then return {(IsMandatory ? "true" : AllOrTrue(checks))} end");
        body.AddRange(TargetLines);
        var options = string.Join(",", Branches.Select(x => $"{{b{x.Key},aux.Stringid(id,{x.Value.StringIndex}),{x.Key}}}"));
        // "Based on where it was sent from": the option that can be used, without asking.
        body.Add(OptionsAutomatic
            ? "local op=" + string.Join(" or ", Branches.Keys.Select(n => $"b{n} and {n}")) + " or nil"
            : $"local op=aux.SelectFromOptions({OptionChooser},{options})");
        body.Add("e:SetLabel(e:GetLabel(),op)");
        var shared = Categories.Where(c => !Branches.Values.Any(b => b.Categories.Contains(c))).ToList();
        var untargeted = Properties.Where(x => x != "EFFECT_FLAG_CARD_TARGET").ToList();
        var first = true;
        foreach (var (n, b) in Branches)
        {
            body.Add($"{(first ? "if" : "elseif")} op=={n} then");
            first = false;
            var cats = shared.Concat(b.Categories).Distinct().ToList();
            body.Add($"\te:SetCategory({(cats.Count == 0 ? "0" : Lua.Join(cats))})");
            var props = b.Chkc is not null ? Properties : untargeted;
            body.Add($"\te:SetProperty({(props.Count == 0 ? "0" : Lua.Join(props))})");
            // "Each effect once per turn": the option is used once it is chosen for an activation.
            if (OptionFlag is { } flag && b.Combines is null) body.Add($"\tif e:IsCostChecked() then Duel.RegisterFlagEffect(tp,{flag(n)},RESET_PHASE+PHASE_END,0,1) end");
            // The option's own cost, paid when the effect is activated (not when it is copied).
            if (b.CostLines.Count > 0)
            {
                body.Add("\tif e:IsCostChecked() then");
                body.AddRange(b.CostLines.Select(l => "\t\t" + l));
                body.Add("\tend");
            }
            body.AddRange(b.Lines.Select(l => "\t" + l));
        }
        body.Add("end");
        return body;
    }

    /// <summary>
    /// "Apply 1 of these effects", "your opponent chooses 1 of these effects for you to apply": the effect can be
    /// activated while any option could be applied, and the option is chosen as it resolves (<see cref="BuildOperation"/>).
    /// </summary>
    private List<string> BuildResolvingTarget()
    {
        var checks = new List<string>(TargetChecks) { "(" + string.Join(" or ", Branches.Keys.Select(OptionCheck)) + ")" };
        var body = new List<string>();
        // The target the options share ("Target 1 face-down monster your opponent controls; your opponent chooses ...").
        if (Chkc is not null) body.Add($"if chkc then return {Chkc} end");
        body.Add($"if chk==0 then return {(IsMandatory ? "true" : AllOrTrue(checks))} end");
        body.AddRange(TargetLines);
        foreach (var b in Branches.Values.Where(b => b.Combines is null)) body.AddRange(b.Lines);
        return body;
    }

    /// <summary>What one option needs to be chosen, as one expression.</summary>
    private string OptionCheck(int n)
    {
        var checks = new List<string>(Branches[n].Checks);
        if (OptionFlag is { } flag && Branches[n].Combines is null) checks.Add($"Duel.GetFlagEffect(tp,{flag(n)})==0");
        return checks.Count == 0 ? "true" : "(" + string.Join(" and ", checks) + ")";
    }

    private List<string> BuildPassives(string? countLimit)
    {
        var lines = new List<string> { $"--{Lua.Comment(Source.Label)}" };
        string? con = null;
        var conditions = XyzMaterialCondition is null ? Conditions : [XyzMaterialCondition, .. Conditions];
        if (conditions.Count > 0)
        {
            var body = new List<string> { "local tp=e:GetHandlerPlayer()" };
            body.AddRange(ReturnAll(conditions));
            con = Script.AddFunction($"e{Index}con", "e", body);
        }
        char suffix = 'a';
        foreach (var (write, limited, own) in _passives)
        {
            var v = _passives.Count == 1 ? Var : $"{Var}{suffix++}";
            var body = write(v).ToList();
            // An Xyz Monster that has this card as material gains it: the same effect, given to that monster.
            if (XyzMaterialCondition is not null)
                body = body.Select(l => l.StartsWith($"{v}:SetType(", StringComparison.Ordinal)
                    ? l.Replace("EFFECT_TYPE_SINGLE", "EFFECT_TYPE_XMATERIAL", StringComparison.Ordinal).Replace("EFFECT_TYPE_FIELD", "EFFECT_TYPE_XMATERIAL+EFFECT_TYPE_FIELD", StringComparison.Ordinal)
                    : l).ToList();
            lines.Add($"local {v}=Effect.CreateEffect(c)");
            lines.AddRange(body);
            if (limited && countLimit is not null) lines.Add($"{v}:SetCountLimit({countLimit})");
            if (own is not null)
                lines.Add($"{v}:SetCondition({Script.AddFunction($"e{Index}con", "e", ["local tp=e:GetHandlerPlayer()", .. ReturnAll([.. conditions, own])])})");
            // A lasting effect with a condition of its own ("while that player controls a face-down monster") needs it
            // as well as the IF blocks.
            else if (con is not null && lines.FindIndex(l => l.StartsWith($"{v}:SetCondition(", StringComparison.Ordinal)) is var written and >= 0)
            {
                var inner = lines[written][$"{v}:SetCondition(".Length..^1];
                lines[written] = $"{v}:SetCondition({Script.AddFunction($"e{Index}con", "e,...", [$"return {con}(e) and {inner}(e,...)"])})";
            }
            else if (con is not null) lines.Add($"{v}:SetCondition({con})");
            lines.Add($"c:RegisterEffect({v})");
        }
        return lines;
    }

    /// <summary>Chains the DO steps, nesting the ones that depend on the step before; one chain per option.</summary>
    private List<string> BuildOperation()
    {
        var body = new List<string> { "local c=e:GetHandler()" };
        body.AddRange(OperationPrologue);
        var branches = _steps.Select(s => s.Branch).Distinct().OrderBy(b => b).ToList();
        if (branches.Count == 0 || branches.Count == 1 && branches[0] == 0)
        {
            body.AddRange(Chain(_steps, ""));
            body.AddRange(_finally);
            return body;
        }
        if (OptionsInSequence)
        {
            // "Apply these effects in sequence": each one that can be applied now, one after the other.
            body.Add("local applied=false");
            foreach (var b in branches.Where(b => b > 0))
            {
                body.Add($"if {OptionCheck(b)} then");
                body.Add("	if applied then Duel.BreakEffect() end");
                body.AddRange(Chain(_steps.Where(s => s.Branch == b).ToList(), "	"));
                body.Add("	applied=true");
                body.Add("end");
            }
            body.AddRange(_finally);
            return body;
        }
        if (OptionsOnResolution)
        {
            // Chosen now, among the options that can be applied now ("based on the number of ...": the one that applies).
            foreach (var n in Branches.Keys) body.Add($"local b{n}={OptionCheck(n)}");
            body.Add(OptionsAutomatic
                ? "local op=" + string.Join(" or ", Branches.Keys.Select(n => $"b{n} and {n}")) + " or nil"
                : $"local op=aux.SelectFromOptions({OptionChooser},{string.Join(",", Branches.Select(x => $"{{b{x.Key},aux.Stringid(id,{x.Value.StringIndex}),{x.Key}}}"))})");
        }
        else body.Add("local _,op=e:GetLabel()");
        var first = true;
        foreach (var b in branches)
        {
            body.Add($"{(first ? "if" : "elseif")} op=={b} then");
            first = false;
            if (OptionsOnResolution && OptionFlag is { } flag) body.Add($"\tDuel.RegisterFlagEffect(tp,{flag(b)},RESET_PHASE+PHASE_END,0,1)");
            body.AddRange(Chain(_steps.Where(s => s.Branch == b).ToList(), "\t"));
        }
        // "1 or both": both options' steps, each as its own chain.
        foreach (var (n, both) in Branches.Where(x => x.Value.Combines is not null))
        {
            body.Add($"elseif op=={n} then");
            foreach (var part in both.Combines!) body.AddRange(Chain(_steps.Where(s => s.Branch == part).ToList(), "\t"));
        }
        body.Add("end");
        body.AddRange(_finally);
        return body;
    }

    private List<string> Chain(IReadOnlyList<OperationStep> steps, string baseIndent)
    {
        var body = new List<string>();
        var indent = baseIndent;
        int opened = 0, n = 0;
        string? previous = null;
        var started = false;
        // How many ifs were open before the current result gate ("if the result is heads"), which the next one closes.
        int? caseLevel = null;
        void Open(string cond)
        {
            body.Add($"{indent}if {cond} then");
            indent += "\t";
            opened++;
        }
        foreach (var step in steps)
        {
            n++;
            // The first step of each option links to nothing.
            var link = started && step.Link != ActionLink.Option ? step.Link : ActionLink.And;
            started = true;
            if (step.Gate is not null && step.Case)
            {
                for (; caseLevel is { } level && opened > level; opened--)
                {
                    indent = indent[..^1];
                    body.Add($"{indent}end");
                }
                caseLevel = opened;
                Open($"({step.Gate})");
                if (link == ActionLink.Then) body.Add($"{indent}Duel.BreakEffect()");
                continue;
            }
            if (step.Gate is not null)
            {
                switch (link)
                {
                    case ActionLink.IfYouDo: Open(previous is null ? $"({step.Gate})" : $"{previous}>0 and ({step.Gate})"); break;
                    case ActionLink.IfYouDont: Open(previous is null ? "false" : $"{previous}==0 and ({step.Gate})"); break;
                    case ActionLink.Then:
                        Open(previous is null ? $"({step.Gate})" : $"{previous}>0 and ({step.Gate})");
                        body.Add($"{indent}Duel.BreakEffect()");
                        break;
                    case ActionLink.AlsoAfterThat:
                        body.Add($"{indent}Duel.BreakEffect()");
                        Open($"({step.Gate})");
                        break;
                    default: Open($"({step.Gate})"); break;
                }
                continue;
            }
            switch (link)
            {
                case ActionLink.IfYouDo:
                    if (previous is not null) Open($"{previous}>0");
                    break;
                case ActionLink.IfYouDont:
                    Open(previous is null ? "false" : $"{previous}==0");
                    break;
                case ActionLink.Then:
                    if (previous is not null) Open($"{previous}>0");
                    body.Add($"{indent}Duel.BreakEffect()");
                    break;
                case ActionLink.AlsoAfterThat:
                    body.Add($"{indent}Duel.BreakEffect()");
                    break;
            }
            var result = step.Result;
            if (step.Optional)
            {
                // "You can ...": ask, and count a declined step as not done.
                result ??= $"ok{Index}_{n}";
                body.Add($"{indent}local {result}=0");
                var available = step.AskIf.Count == 0 ? "" : string.Join(" and ", step.AskIf) + " and ";
                body.Add($"{indent}if {available}Duel.SelectYesNo(tp,aux.Stringid(id,{Math.Min(Index, 16) - 1})) then");
                var inner = step.Result is null
                    ? step.Lines.Append($"{result}=1")
                    : step.Lines.Select(l => l.StartsWith($"local {result}=", StringComparison.Ordinal) ? l["local ".Length..] : l);
                body.AddRange(inner.Select(l => indent + "\t" + l));
                body.Add($"{indent}end");
            }
            else body.AddRange(step.Lines.Select(l => indent + l));
            previous = result;
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
