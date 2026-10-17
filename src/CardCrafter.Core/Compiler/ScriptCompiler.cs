using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

public sealed class CompileResult
{
    public required string Lua { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    /// <summary>The effect labels, in order, for the database's str1..str16 columns.</summary>
    public required IReadOnlyList<string> Strings { get; init; }
    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);
}

/// <summary>One of the database's str1..str16: what uses it, the text it gets, and the generated text it replaces.</summary>
public sealed record StringSlot(string Use, string Generated)
{
    public string Text { get; init; } = Generated;
    public bool Overridden { get; init; }
}

/// <summary>Turns a card project's event sheet into a complete card script.</summary>
public static class ScriptCompiler
{
    public static CompileResult Compile(CardProject project, CardPack? pack = null)
    {
        // Older combined WHEN choices compile as one WHEN per event, without changing the card being edited.
        if (project.Effects.Any(TriggerSplit.NeedsSplit))
        {
            project = PackSerializer.Clone(project);
            TriggerSplit.Normalize(project);
        }
        var script = new ScriptBuilder(project, pack);
        var card = project.Card;
        var strings = project.UseCustomLua && project.ScriptStrings.Count > 0
            ? project.ScriptStrings.Take(16).ToList()
            : StringSlots(project, pack).Select(s => s.Text).ToList();

        CheckCard(project, script.Diagnostics);
        // "(This card is always treated as a "X" card.)" missing from the typed card text.
        if (Text.ArchetypeReminder.Check(project, pack) is { } reminder) script.Diagnostics.Add(reminder);

        if (project.UseCustomLua)
        {
            return new CompileResult { Lua = project.CustomLua, Diagnostics = script.Diagnostics, Strings = strings };
        }

        var init = new List<string>();
        init.AddRange(Procedures.Write(project, script));

        var built = new List<EffectBuilder>();
        for (int i = 0; i < project.Effects.Count; i++)
        {
            var effect = new EffectBuilder(script, project.Effects[i], i + 1);
            CompileEffect(effect, pack);
            built.Add(effect);
        }

        var equip = NeedsActivationEffect(card, built) && card.Kind == CardKind.Spell && card.SpellKind == SpellKind.Equip;
        var plain = NeedsActivationEffect(card, built) && !equip;
        // "If the activation of this card ... was negated": the activation notices its negation (Cyber Emergency).
        var watch = NegatedActivationTrigger.Watches(project);
        if (watch) NegatedActivationTrigger.Watch(script, built, plain);
        // "If this card was activated this turn": the activation leaves a flag on the card.
        var activated = ActivatedThisTurnCondition.Watches(project);
        if (activated) ActivatedThisTurnCondition.Watch(script, built, plain || equip);
        if (equip)
        {
            if (activated) script.Diagnostics.Add(new(Severity.Error, "\"This card was activated this turn\" isn't available for Equip Spells."));
            init.AddRange(EquipActivation(project, script));
        }
        else if (plain)
        {
            init.Add("--Activate");
            init.Add("local e0=Effect.CreateEffect(c)");
            init.Add("e0:SetType(EFFECT_TYPE_ACTIVATE)");
            init.Add("e0:SetCode(EVENT_FREE_CHAIN)");
            var e0Cost = new List<string>();
            if (activated) e0Cost.Add(ActivatedThisTurnCondition.FlagLine);
            if (watch) e0Cost.AddRange(NegatedActivationTrigger.WatchLines(script));
            if (e0Cost.Count > 0) init.Add($"e0:SetCost({script.AddFunction("e0cost", "e,tp,eg,ep,ev,re,r,rp,chk", ["if chk==0 then return true end", .. e0Cost])})");
            init.Add("c:RegisterEffect(e0)");
        }

        init.InsertRange(0, script.InitialLines);
        // Each option of "Activate 1 of these effects" gets a description string after the effects' own.
        var nextString = Math.Min(16, project.Effects.Count);
        foreach (var effect in built)
            foreach (var branch in effect.Branches.Values)
                branch.StringIndex = nextString < 16 ? nextString++ : Math.Min(effect.Index, 16) - 1;
        var limits = CountLimits(card, built, script.Diagnostics);
        foreach (var effect in built)
            init.AddRange(effect.Build(limits[effect.Index]));

        var lines = new List<string>
        {
            $"--{Lua.Comment(card.Name)}",
            "--Made with YGO Card Crafter",
            $"local s,id=c{Lua.Num(card.Id)},{Lua.Num(card.Id)}",
            "function s.initial_effect(c)",
        };
        // Older script sets (like some YGO Omega versions) lack a few newer constants and helpers; each one the script
        // uses is defined here if the game doesn't have it. Without SUMMON_VALUE_SELF, for example, "Special Summon
        // this card" fails after its effect is already used.
        var body = string.Join("\n", init.Concat(script.Functions));
        lines.InsertRange(3, Compat.Where(f => body.Contains(f.Uses, StringComparison.Ordinal)).SelectMany(f => f.Lines));
        lines.AddRange(init.Select(l => "\t" + l));
        lines.Add("end");
        lines.AddRange(script.Functions);

        return new CompileResult
        {
            Lua = string.Join("\n", lines) + "\n",
            Diagnostics = script.Diagnostics,
            Strings = strings,
        };
    }

    /// <summary>Newer constants and helpers, and how to define them for script sets that don't have them.</summary>
    private static readonly (string Uses, string[] Lines)[] Compat =
    [
        ("SUMMON_VALUE_SELF", ["if not SUMMON_VALUE_SELF then SUMMON_VALUE_SELF=0x1 end"]),
        ("aux.SelectFromOptions", [
            "if not aux.SelectFromOptions then",
            "\tfunction aux.SelectFromOptions(tp,...)",
            "\t\tlocal ops,vals={},{}",
            "\t\tfor i,o in ipairs({...}) do if o[1] then table.insert(ops,o[2]) table.insert(vals,o[3] or i) end end",
            "\t\tif #ops==0 then return nil end",
            "\t\treturn vals[Duel.SelectOption(tp,table.unpack(ops))+1]",
            "\tend",
            "end",
        ]),
        ("aux.dncheck", ["if not aux.dncheck then function aux.dncheck(g) return g:GetClassCount(Card.GetCode)==#g end end"]),
        ("aux.dabcheck", ["if not aux.dabcheck then function aux.dabcheck(g) return g:GetClassCount(Card.GetAttribute)==#g end end"]),
        ("aux.drccheck", ["if not aux.drccheck then function aux.drccheck(g) return g:GetClassCount(Card.GetRace)==#g end end"]),
        ("aux.dlvcheck", ["if not aux.dlvcheck then function aux.dlvcheck(g) return g:GetClassCount(Card.GetLevel)==#g end end"]),
        ("aux.SameValueCheck", ["if not aux.SameValueCheck then function aux.SameValueCheck(g,f) local v=nil for tc in aux.Next(g) do v=v and v&f(tc) or f(tc) if v==0 then return false end end return true end end"]),
        ("aux.IsCodeOrListed", ["if not aux.IsCodeOrListed then function aux.IsCodeOrListed(c,code) return c:IsCode(code) or aux.IsCodeListed(c,code) end end"]),
        // The official "Mulcharmy" count: a flag on the player for each "Mulcharmy" monster effect activated this turn.
        ("aux.EnableMulcharmyGlobalCheck", [
            "if not aux.EnableMulcharmyGlobalCheck then",
            "\tfunction aux.EnableMulcharmyGlobalCheck()",
            "\t\tif CardCrafterMulcharmyCheck then return end",
            "\t\tCardCrafterMulcharmyCheck=true",
            "\t\tlocal ge=Effect.GlobalEffect()",
            "\t\tge:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "\t\tge:SetCode(EVENT_CHAINING)",
            "\t\tge:SetOperation(function(e,tp,eg,ep,ev,re,r,rp) if re:IsActiveType(TYPE_MONSTER) and re:GetHandler():IsSetCard(0x1b2) then Duel.RegisterFlagEffect(ep,84192580,RESET_PHASE+PHASE_END,0,1) end end)",
            "\t\tDuel.RegisterEffect(ge,0)",
            "\tend",
            "end",
        ]),
        // "Instead of conducting your normal draw": the draw is given up for this Draw Phase.
        ("NormalDraw", ["if not FLAG_ID_NO_NORMAL_DRAW then FLAG_ID_NO_NORMAL_DRAW=3 end"]),
        ("aux.IsPlayerCanNormalDraw", [
            "if not aux.IsPlayerCanNormalDraw then",
            "\tfunction aux.IsPlayerCanNormalDraw(tp) return Duel.GetDrawCount(tp)>0 and Duel.GetFieldGroupCount(tp,LOCATION_DECK,0)>0 and Duel.GetFlagEffect(tp,FLAG_ID_NO_NORMAL_DRAW)==0 end",
            "end",
        ]),
        ("aux.GiveUpNormalDraw", [
            "if not aux.GiveUpNormalDraw then",
            "\tfunction aux.GiveUpNormalDraw(e,tp)",
            "\t\tlocal e1=Effect.CreateEffect(e:GetHandler())",
            "\t\te1:SetType(EFFECT_TYPE_FIELD)",
            "\t\te1:SetProperty(EFFECT_FLAG_PLAYER_TARGET)",
            "\t\te1:SetCode(EFFECT_DRAW_COUNT)",
            "\t\te1:SetTargetRange(1,0)",
            "\t\te1:SetReset(RESET_PHASE+PHASE_DRAW)",
            "\t\te1:SetValue(0)",
            "\t\tDuel.RegisterEffect(e1,tp)",
            "\t\tDuel.RegisterFlagEffect(tp,FLAG_ID_NO_NORMAL_DRAW,RESET_PHASE+PHASE_DRAW,0,1)",
            "\tend",
            "end",
        ]),
        ("aux.DimensionalFissureTarget", [
            "if not aux.DimensionalFissureTarget then",
            "\tfunction aux.DimensionalFissureTarget(e,c) return bit.band(c:GetOriginalType(),TYPE_MONSTER)>0 and not c:IsLocation(LOCATION_OVERLAY) and not c:IsType(TYPE_SPELL+TYPE_TRAP) end",
            "end",
        ]),
    ];

    private static void CompileEffect(EffectBuilder effect, CardPack? pack)
    {
        var src = effect.Source;
        if (src.When is null)
        {
            effect.Error("Pick a WHEN block: what starts this effect.");
            return;
        }
        if (!Run(effect, src.When, BlockKind.Trigger, pack)) return;
        if (src.AlsoWhen.Count > 0) AddAlsoWhens(effect, src.AlsoWhen, pack);
        // What the WHEN itself checks, which the clones for an option's own WHEN don't share.
        else effect.TriggerConditionCount = effect.Conditions.Count;

        foreach (var b in src.If)
        {
            CardRefs.Check(effect, b);
            Run(effect, b, BlockKind.Condition, pack);
        }

        if (effect.IsPassive && src.Cost.Count > 0)
            effect.Error("\"While face-up\" effects are never activated, so they cannot have a COST.");
        else
            foreach (var b in src.Cost)
            {
                var from = effect.CostLines.Count;
                // The cards it moves are kept when a DO block uses "the cards used for the cost" (CardRefs.cs).
                if (Run(effect, b, BlockKind.Cost, pack)) CardRefs.KeepCost(effect, b, from);
            }

        if (!effect.IsPassive) LimitExtras(effect);

        if (effect.IsSummonProcedure)
        {
            if (src.Do.Count > 0)
                effect.Error("A Special Summon procedure only Summons this card, so it has no DO blocks. " +
                             "For something that happens after, add an effect with \"If this card is Special Summoned\".");
            return;
        }

        if (effect.IsMandatory && src.Cost.Count > 0)
            effect.Warn("Mandatory effects usually have no cost. If the cost can't be paid the effect still has to activate.");

        var doKind = effect.IsPassive ? BlockKind.Passive : BlockKind.Action;
        if (effect.IsPassive)
        {
            foreach (var b in src.Do) Run(effect, b, doKind, pack);
        }
        else
        {
            // "Activate 1 of these effects": each "●" link starts another option. A "●" on a coin toss or die roll's result
            // ("● Heads: ...") is that result's steps instead, not an option the player picks.
            // A "Choose 1 of these effects" block first says how the option is chosen (Selections.cs); the options follow it.
            var header = ChooseEffectsAction.Header(src.Do);
            // "Target 1 monster; your opponent chooses 1 of these effects": the targets every option shares, before the header.
            foreach (var b in ChooseEffectsAction.Shared(src.Do))
            {
                CardRefs.Check(effect, b);
                Run(effect, b, doKind, pack);
            }
            if (header is not null) Run(effect, header, doKind, pack);
            var dos = ChooseEffectsAction.Options(src.Do);
            Chance.CheckOptions(effect, dos);
            var cases = new Chance.CaseChecks(effect);
            var branch = header is not null || dos.Skip(1).Any(b => b.Link == ActionLink.Option && !Chance.IsGate(b)) ? 1 : 0;
            var acted = false;
            // An option's own WHEN: the effect's event while its blocks compile, and what they need of it, checked for that option.
            var (mainCode, timed, timedFrom) = (effect.Code, false, 0);
            void EndTimed()
            {
                if (!timed) return;
                var needs = effect.Conditions.Skip(timedFrom).ToList();
                effect.Conditions.RemoveRange(timedFrom, needs.Count);
                effect.BranchOf(branch).Checks.AddRange(needs);
                effect.Code = mainCode;
                timed = false;
            }
            for (int i = 0; i < dos.Count; i++)
            {
                var b = dos[i];
                if (i > 0 && b.Link == ActionLink.Option && !Chance.IsGate(b)) { EndTimed(); branch++; acted = false; }
                cases.Before(b);
                effect.CurrentBranch = branch;
                effect.CurrentOptional = b.Optional;
                var (checks, lines, cats) = (effect.TargetChecks.Count, effect.TargetLines.Count, effect.Categories.Count);
                // An IF block among the DO blocks gates the steps after it: "then if you control ..., destroy ...".
                if (BlockRegistry.Find(b.BlockId) is { Kind: BlockKind.Condition })
                {
                    if (b.Optional) effect.Warn("An IF block among the DO blocks can't be optional; \"you can\" was ignored.");
                    var before = effect.Conditions.Count;
                    if (Run(effect, b, BlockKind.Condition, pack))
                    {
                        var gate = effect.Conditions.Skip(before).ToList();
                        effect.Conditions.RemoveRange(before, gate.Count);
                        // "● If you control no monsters: ...": what the option needs to be chosen.
                        if (ChooseEffectsAction.IsOptionCondition(src.Do, b)) effect.BranchOf(branch).Checks.AddRange(gate);
                        else effect.AddGate(gate.Count == 0 ? "true" : string.Join(" and ", gate), b.Link, Chance.IsGate(b));
                    }
                }
                // "● When your opponent activates a monster effect: Negate that effect": a WHEN that starts an option is its own timing.
                else if (BlockRegistry.Find(b.BlockId) is { Kind: BlockKind.Trigger })
                {
                    if (branch == 0 || !ChooseEffectsAction.IsOptionTrigger(src.Do, b))
                        effect.Error("A WHEN block among the DO blocks is an option's own timing: put it at the start of a \"●\" option.");
                    else
                    {
                        var timing = new EffectBuilder(effect.Script, effect.Source, effect.Index);
                        if (Run(timing, b, BlockKind.Trigger, pack) && ChooseEffectsAction.OptionTrigger(effect, timing, branch))
                            (effect.Code, timed, timedFrom) = (timing.Code, true, effect.Conditions.Count);
                    }
                }
                // "● Discard 1 card; draw 1 card": a COST block that starts an option is that option's own cost.
                else if (BlockRegistry.Find(b.BlockId) is { Kind: BlockKind.Cost })
                {
                    if (header is null || branch == 0 || acted)
                        effect.Error("A COST block among the DO blocks is an option's own cost: put a \"Choose 1 of these effects\" block first, and the COST block at the start of its option.");
                    else OptionCost(effect, b, branch, pack);
                }
                else
                {
                    CardRefs.Check(effect, b);
                    var (steps, chkc) = (effect.StepCount, effect.Chkc);
                    Run(effect, b, doKind, pack);
                    acted = true;
                    // "When this card is activated: You can ...": a step the player may skip never stops the card's activation.
                    if (b.Optional && effect.IsActivation && branch == 0 && chkc == effect.Chkc) effect.AskOnResolution(steps, checks);
                }
                if (branch > 0)
                {
                    // What this block checks and sets up at activation belongs to its option.
                    var target = effect.BranchOf(branch);
                    target.Checks.AddRange(effect.TargetChecks.Skip(checks));
                    effect.TargetChecks.RemoveRange(checks, effect.TargetChecks.Count - checks);
                    target.Lines.AddRange(effect.TargetLines.Skip(lines));
                    effect.TargetLines.RemoveRange(lines, effect.TargetLines.Count - lines);
                    foreach (var c in effect.Categories.Skip(cats)) if (!target.Categories.Contains(c)) target.Categories.Add(c);
                }
            }
            EndTimed();
            cases.End();
            if (header is not null) ChooseEffectsAction.CheckOptions(effect);
            effect.CurrentBranch = 0;
            effect.CurrentOptional = false;
        }

        if (effect.LpPerTarget is not null)
            effect.Warn("\"Pay LP for each target\" is paid by a DO block that targets after it; none does, so nothing is paid.");
        // "Activate this card by sending 1 ... to the GY": a Continuous or Field card's activation can be only its cost.
        if (src.Do.Count == 0 && !(effect.IsActivation && src.Cost.Count > 0 && Lingers(effect.Card) && effect.Card is not { Kind: CardKind.Spell, SpellKind: SpellKind.Equip }))
            effect.Error("Add at least one DO block: what the effect does.");
        if (effect.IsPassive && src.Limit != OncePerTurn.None)
            effect.Warn("Once-per-turn limits do nothing on \"while face-up\" effects and were ignored.");
    }

    /// <summary>
    /// What a limit needs beside its count: "once while face-up on the field" keeps its count used from turn to turn, and
    /// "once per turn" for each copy beside uses by card name is a flag on the card, set as the effect is activated.
    /// </summary>
    private static void LimitExtras(EffectBuilder effect)
    {
        var src = effect.Source;
        if (src.Limit is OncePerTurn.FaceUp or OncePerTurn.FaceUpAndHard)
        {
            if (effect.IsSummonProcedure || effect.IsActivation)
                effect.Error("\"Once while face-up on the field\" is for effects of a card already on the field, not its activation or a Summon procedure.");
            effect.AddProperty("EFFECT_FLAG_NO_TURN_RESET");
        }
        if (src.Limit == OncePerTurn.SoftAndHard)
        {
            if (effect.IsSummonProcedure) { effect.Error("A Summon procedure can't have \"once per turn\" for each copy beside its limit by name."); return; }
            var flag = CopyFlag(effect.Index);
            effect.CostChecks.Add($"e:GetHandler():GetFlagEffect({flag})==0");
            effect.CostLines.Insert(0, $"e:GetHandler():RegisterFlagEffect({flag},RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,0,1)");
        }
    }

    /// <summary>
    /// "Or when ...": more triggers that start the same effect, sharing its once per turn. Triggers of the same kind
    /// used from the same place share the effect object, one registration per event; when they check different
    /// things, the effect asks which event started it (e:GetCode()). Any other trigger becomes a clone of the effect
    /// with its own type, zone and checks, like official scripts that clone an effect for another timing.
    /// </summary>
    private static void AddAlsoWhens(EffectBuilder effect, IEnumerable<BlockInstance> alsos, CardPack? pack)
    {
        if (effect.IsPassive || effect.IsSummonProcedure || effect.Code is null)
        {
            effect.Error("\"Or when\" only works with WHEN blocks that activate an effect. Lasting effects and Summon procedures have one WHEN.");
            return;
        }
        var main = effect.Conditions.ToList();
        effect.Conditions.Clear();
        effect.TriggerProperties.AddRange(effect.Properties);
        var shared = new List<(string Code, List<string> Conds)>();
        foreach (var code in new[] { effect.Code }.Concat(effect.ExtraCodes)) shared.Add((code, main));

        foreach (var also in alsos)
        {
            // Compiled into the card's own script, so the helper functions it writes (filters) are part of it.
            var scratch = new EffectBuilder(effect.Script, effect.Source, effect.Index);
            if (!Run(scratch, also, BlockKind.Trigger, pack)) continue;
            var name = BlockRegistry.Find(also.BlockId)?.Name ?? also.BlockId;
            if (scratch.IsPassive || scratch.IsSummonProcedure || scratch.Code is null)
            {
                effect.Error($"\"Or when\": \"{name}\" isn't a trigger that activates an effect, so it can't start this one. Make it a separate effect.");
                continue;
            }
            if (scratch.IsMandatory != effect.IsMandatory)
            {
                effect.Error($"\"Or when\": \"{name}\" is {(scratch.IsMandatory ? "mandatory" : "optional (\"You can\")")} but the first WHEN isn't. Tick or untick \"optional\" so they match.");
                continue;
            }
            var codes = new[] { scratch.Code }.Concat(scratch.ExtraCodes).ToList();
            bool sameObject = scratch.Types.ToHashSet().SetEquals(effect.Types) && scratch.Range == effect.Range
                              && scratch.Properties.ToHashSet().SetEquals(effect.TriggerProperties) && scratch.HintTiming == effect.HintTiming;
            if (sameObject)
                foreach (var code in codes) shared.Add((code, scratch.Conditions.ToList()));
            else
                effect.Variants.Add(new TriggerVariant
                {
                    Types = scratch.Types.ToList(), Code = codes[0], ExtraCodes = codes.Skip(1).Distinct().ToList(), Range = scratch.Range,
                    Properties = scratch.Properties.ToList(), HintTiming = scratch.HintTiming, Conditions = scratch.Conditions.ToList(),
                });
        }

        // The events the effect object itself is registered for.
        var byCode = shared.GroupBy(s => s.Code).ToList();
        effect.ExtraCodes.Clear();
        effect.ExtraCodes.AddRange(byCode.Select(g => g.Key).Where(c => c != effect.Code));
        static string All(List<string> conds) => conds.Count == 1 ? conds[0] : string.Join(" and ", conds.Select(c => $"({c})"));
        var lists = shared.Select(s => s.Conds).ToList();
        if (lists.All(l => l.SequenceEqual(lists[0])))
            effect.Conditions.AddRange(lists[0]);
        else
        {
            string Group(IGrouping<string, (string Code, List<string> Conds)> g) =>
                g.Any(x => x.Conds.Count == 0) ? $"e:GetCode()=={g.Key}"
                : $"(e:GetCode()=={g.Key} and ({string.Join(" or ", g.Select(x => All(x.Conds)).Distinct())}))";
            effect.Conditions.Add(byCode.Count == 1 && byCode[0].All(x => x.Conds.Count > 0)
                ? string.Join(" or ", byCode[0].Select(x => $"({All(x.Conds)})").Distinct())
                : "(" + string.Join(" or ", byCode.Select(Group)) + ")");
        }
        effect.TriggerConditionCount = effect.Conditions.Count;
    }

    /// <summary>
    /// An option's own cost: compiled as a COST block, then kept with its option, which pays it when it is chosen. The
    /// count a cost keeps in the label ("for each card discarded") is kept beside the chosen option.
    /// </summary>
    private static void OptionCost(EffectBuilder effect, BlockInstance b, int branch, CardPack? pack)
    {
        var (checks, lines, props, range) = (effect.CostChecks.Count, effect.CostLines.Count, effect.Properties.Count, effect.Range);
        if (b.Optional) effect.Warn("A cost can't be optional; \"you can\" was ignored.");
        if (!Run(effect, b, BlockKind.Cost, pack)) return;
        var target = effect.BranchOf(branch);
        target.CostChecks.AddRange(effect.CostChecks.Skip(checks));
        effect.CostChecks.RemoveRange(checks, effect.CostChecks.Count - checks);
        target.CostLines.AddRange(effect.CostLines.Skip(lines).Select(l =>
            Regex.IsMatch(l, @"^e:SetLabel\((.*)\)$") ? l[..^1] + ",op)" : l));
        effect.CostLines.RemoveRange(lines, effect.CostLines.Count - lines);
        if (effect.Properties.Count != props || effect.Range != range)
            effect.Error($"\"{BlockRegistry.Find(b.BlockId)!.Name}\" changes the whole effect, so it can't be one option's own cost.");
    }

    private static bool Run(EffectBuilder effect, BlockInstance inst, BlockKind expected, CardPack? pack)
    {
        var def = BlockRegistry.Find(inst.BlockId);
        if (def is null)
        {
            effect.Error($"Unknown block \"{inst.BlockId}\". It may come from a newer version of Card Crafter.");
            return false;
        }
        if (def.Kind != expected)
        {
            var where = expected switch
            {
                BlockKind.Passive => "This effect is a \"while face-up\" effect, so its DO blocks must be lasting effects",
                BlockKind.Action when def.Kind == BlockKind.Passive => "Lasting effects only work with the \"While this card is face-up\" trigger",
                _ => $"This block belongs in {Column(def.Kind)}, not {Column(expected)}",
            };
            effect.Error($"{where} (\"{def.Name}\").");
            return false;
        }
        def.Compile(new BlockContext(new BlockArgs(def, inst, pack), effect));
        return true;
    }

    private static string Column(BlockKind k) => k switch
    {
        BlockKind.Trigger => "WHEN",
        BlockKind.Condition => "IF",
        BlockKind.Cost => "COST",
        _ => "DO",
    };

    /// <summary>
    /// An Equip Spell's activation: target a face-up monster and equip this card to it, plus the equip limit that
    /// keeps it on monsters <see cref="CardProject.EquipFilter"/> allows.
    /// </summary>
    private static IEnumerable<string> EquipActivation(CardProject project, ScriptBuilder script)
    {
        var f = project.EquipFilter;
        var self = f.Owner == CardOwner.Opponent ? "0" : "LOCATION_MZONE";
        var opp = f.Owner == CardOwner.You ? "0" : "LOCATION_MZONE";
        var conds = FilterCompiler.Conditions(new CardFilter
        {
            CardType = FilterCardType.Monster, MonsterKind = f.MonsterKind, Race = f.Race, Attribute = f.Attribute, Archetype = f.Archetype, Archetype2 = f.Archetype2,
            CardCode = f.CardCode, AltCodes = f.AltCodes, ExceptOwnName = f.ExceptOwnName, ExceptCode = f.ExceptCode, LevelMin = f.LevelMin, LevelMax = f.LevelMax,
            AtkMin = f.AtkMin, AtkMax = f.AtkMax, DefMin = f.DefMin, DefMax = f.DefMax, MentionsCode = f.MentionsCode, MentionsOrIs = f.MentionsOrIs,
            Requirements = f.Requirements, AnyRequirement = f.AnyRequirement,
        }, hasEffect: false);
        conds.Remove("c:IsType(TYPE_MONSTER)");
        var test = conds.Count == 0 ? "true" : string.Join(" and ", conds);
        var limit = conds.Count == 0 ? "1" : script.AddFunction("eqlimit", "e,c", [$"return {test}"]);
        var filter = script.AddFunction("eqfilter", "c", [$"return c:IsFaceup() and {test}"]);
        var tg = script.AddFunction("eqtg", "e,tp,eg,ep,ev,re,r,rp,chk,chkc",
        [
            $"if chkc then return chkc:IsLocation(LOCATION_MZONE) and {filter}(chkc) end",
            $"if chk==0 then return Duel.IsExistingTarget({filter},tp,{self},{opp},1,nil) end",
            "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_EQUIP)",
            $"Duel.SelectTarget(tp,{filter},tp,{self},{opp},1,1,nil)",
            "Duel.SetOperationInfo(0,CATEGORY_EQUIP,e:GetHandler(),1,0,0)",
        ]);
        var op = script.AddFunction("eqop", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local c=e:GetHandler()",
            "local tc=Duel.GetFirstTarget()",
            "if c:IsRelateToEffect(e) and tc:IsRelateToEffect(e) and tc:IsFaceup() then",
            "	Duel.Equip(tp,c,tc)",
            "end",
        ]);
        return
        [
            "--Activate: equip to a monster",
            "local e0=Effect.CreateEffect(c)",
            "e0:SetCategory(CATEGORY_EQUIP)",
            "e0:SetType(EFFECT_TYPE_ACTIVATE)",
            "e0:SetCode(EVENT_FREE_CHAIN)",
            "e0:SetProperty(EFFECT_FLAG_CARD_TARGET+EFFECT_FLAG_CONTINUOUS_TARGET)",
            $"e0:SetTarget({tg})",
            $"e0:SetOperation({op})",
            "c:RegisterEffect(e0)",
            "local el=Effect.CreateEffect(c)",
            "el:SetType(EFFECT_TYPE_SINGLE)",
            "el:SetCode(EFFECT_EQUIP_LIMIT)",
            "el:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
            $"el:SetValue({limit})",
            "c:RegisterEffect(el)",
        ];
    }

    /// <summary>
    /// The database's str1..str16: the effect descriptions the game shows in its activation lists and "you can"
    /// prompts. Each is the effect's own card text, so the prompt says the whole effect; an effect with no text gets
    /// its name. Then the options of "Activate 1 of these effects", in the order the compiler numbers them. Strings
    /// the user typed over (<see cref="CardProject.StringOverrides"/>) replace the generated ones.
    /// </summary>
    /// <summary>The string asking whether to Xyz Summon the other way ("by using 1 Xyz Monster you control").</summary>
    public const string XyzAlternateUse = "Xyz Summon the other way";

    public static List<StringSlot> StringSlots(CardProject project, CardPack? pack = null)
    {
        var slots = project.Effects.Take(16).Select((e, i) =>
            new StringSlot($"Effect {i + 1}", Text.EffectTextGenerator.Describe(e, project.Card, pack) is { Length: > 0 } t ? t : e.Label)).ToList();
        for (int i = 0; i < project.Effects.Count; i++)
        {
            var options = Text.EffectTextGenerator.OptionTexts(project.Effects[i], project.Card, pack);
            for (int o = 0; o < options.Count && slots.Count < 16; o++) slots.Add(new StringSlot($"Effect {i + 1}, option {o + 1}", options[o]));
        }
        if (project.Card.IsExtraDeck && project.Procedure is { Kind: SummonProcedureKind.Xyz, XyzAlternate: true } && slots.Count < 16)
            slots.Add(new StringSlot(XyzAlternateUse, Text.MaterialText.AlternatePrompt(project, pack)));
        for (int i = 0; i < slots.Count && i < project.StringOverrides.Count; i++)
            if (project.StringOverrides[i] is { Length: > 0 } typed) slots[i] = slots[i] with { Text = typed, Overridden = true };
        return slots;
    }

    /// <summary>Continuous, Field and Equip cards need a plain "activate this card" effect to be placed on the field.</summary>
    private static bool NeedsActivationEffect(CardData card, List<EffectBuilder> effects) => Lingers(card) && !effects.Any(e => e.IsActivation);

    /// <summary>A card that stays on the field after it is activated: a Continuous, Field or Equip Spell, or a Continuous Trap.</summary>
    private static bool Lingers(CardData card) => card.Kind switch
    {
        CardKind.Spell => card.SpellKind is SpellKind.Continuous or SpellKind.Field or SpellKind.Equip,
        CardKind.Trap => card.TrapKind == TrapKind.Continuous,
        _ => false,
    };

    /// <summary>How many uses an effect's limit allows ("twice per turn"): 1 to 9.</summary>
    public static int Times(EffectEvent e) => Math.Clamp(e.Times, 1, 9);

    /// <summary>The flag that keeps "once per turn" for each copy beside a limit by card name (Perfect Toon World).</summary>
    public static string CopyFlag(int index) => $"id+{(8250 + index).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Works out SetCountLimit arguments, giving each hard once-per-turn group its own code. "Once per turn" effects that
    /// share a group share the card's own count (EFFECT_COUNT_CODE_SINGLE), which a card has only one of.
    /// </summary>
    private static Dictionary<int, string?> CountLimits(CardData card, List<EffectBuilder> effects, List<Diagnostic> diagnostics)
    {
        var result = new Dictionary<int, string?>();
        var groups = new List<int>();
        // Only a group of two or more shares: a lone effect keeps the group number it may have had as a hard limit.
        var softGroups = effects.Where(e => e.Source.Limit == OncePerTurn.Soft && e.Source.SharedLimitGroup > 0 && (!e.IsPassive || e.HasLimitedPassive))
            .GroupBy(e => e.Source.SharedLimitGroup).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (softGroups.Count > 1)
            diagnostics.Add(new(Severity.Error, "Only one group of \"Once per turn\" (each copy) effects can share their use: give the others group 0, or a limit by card name."));
        var softShared = softGroups.FirstOrDefault();
        var singleUsed = softShared > 0;
        foreach (var e in effects)
        {
            string? limit = null;
            var n = Times(e.Source).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!e.IsPassive || e.HasLimitedPassive)
            {
                switch (e.Source.Limit)
                {
                    case OncePerTurn.Soft:
                        // Effects that share a "once per turn" count once for all of them, on this card only.
                        if (softShared > 0 && e.Source.SharedLimitGroup == softShared) limit = $"{n},EFFECT_COUNT_CODE_SINGLE";
                        // An effect registered for several events counts once for all of them (one per card).
                        else if (e.HasClones && !singleUsed) { limit = $"{n},EFFECT_COUNT_CODE_SINGLE"; singleUsed = true; }
                        else limit = n;
                        break;
                    case OncePerTurn.Chain:
                        limit = "1,EFFECT_COUNT_CODE_CHAIN";
                        break;
                    // "Once while face-up on the field": the effect's own count, which the turn doesn't give back.
                    case OncePerTurn.FaceUp:
                        limit = "1";
                        break;
                    case OncePerTurn.Hard or OncePerTurn.Duel or OncePerTurn.FaceUpAndHard or OncePerTurn.SoftAndHard:
                        var group = e.Source.SharedLimitGroup > 0 ? e.Source.SharedLimitGroup : -e.Index;
                        if (!groups.Contains(group)) groups.Add(group);
                        var slot = groups.IndexOf(group);
                        var code = slot == 0 ? "id" : $"id+{Lua.Num(slot * card.CountCodeOffset)}";
                        // For Spell/Trap activations, "you can only activate 1 per turn" ignores negated activations,
                        // and a Summon procedure only counts once it is used.
                        if (e.Source.Limit == OncePerTurn.Duel) code += "+EFFECT_COUNT_CODE_DUEL";
                        // Once while face-up, the effect's own count of 1 stays used; the code counts uses by name.
                        var uses = e.Source.Limit == OncePerTurn.FaceUpAndHard ? "1" : n;
                        limit = e.IsActivation || e.IsSummonProcedure ? $"{uses},{code}+EFFECT_COUNT_CODE_OATH" : $"{uses},{code}";
                        break;
                }
            }
            result[e.Index] = limit;
        }
        return result;
    }

    private static void CheckCard(CardProject project, List<Diagnostic> d)
    {
        var card = project.Card;
        if (card.Id <= 0 || card.Id > uint.MaxValue)
            d.Add(new(Severity.Error, "The card's passcode must be a positive number up to 4294967295."));
        else if (card.Id < 100000000)
            d.Add(new(Severity.Warning, "Passcodes under 9 digits can clash with official cards. Custom cards usually use 9-digit codes."));
        else if (card.Id > PasscodeGenerator.MaxForStrings)
            d.Add(new(Severity.Warning, $"Passcode {card.Id} is too large for the game's effect texts (the most is {PasscodeGenerator.MaxForStrings}): " +
                                        "its prompts show \"hand\" or \"Unknown (...)\" instead of the effect. Click Generate for a passcode that fits, then save again."));
        if (string.IsNullOrWhiteSpace(card.Name))
            d.Add(new(Severity.Error, "The card needs a name."));
        // A custom Type mixed with standard Types' bits shows as those Types in the game.
        if (card.IsMonster && ((long)card.Race & ~EnumNames.StandardRaceMask) != 0 && PackCodes.TypeValueProblem((long)card.Race) is { } typeProblem)
            d.Add(new(Severity.Warning, "Its Type " + typeProblem));
        if (project.Effects.Count > 16)
            d.Add(new(Severity.Error, "A card can have at most 16 effects (the database has 16 text slots)."));
        if (card.Archetypes.Count > 4)
            d.Add(new(Severity.Error, "A card can belong to at most 4 archetypes."));
        if (card.IsMonster && card.Frame is MonsterFrame.Normal or MonsterFrame.Token && project.Effects.Count > 0)
            d.Add(new(Severity.Warning, "Normal monsters have no effects. Change the frame to Effect so the game runs them."));
        if (card.IsLink && card.LinkMarkers == LinkMarker.None)
            d.Add(new(Severity.Error, "Link monsters need at least one Link Arrow."));
        if (card.IsMonster && card.SummonRule is not (SummonRule.Normal or SummonRule.ByCardEffect) && card.IsMainDeckNonRitual
            && !project.Effects.Any(e => e.When?.BlockId == SummonProcedureTrigger.BlockId))
        {
            d.Add(new(Severity.Warning, card.SummonRule == SummonRule.ProcedureOnly
                ? "This monster can only be Special Summoned by its own procedure, but it has none, so it can never be Summoned. Add an effect with the WHEN block \"Special Summon procedure\"."
                : "This monster can't be Normal Summoned and has no Special Summon procedure, so only other cards can Special Summon it. Add an effect with the WHEN block \"Special Summon procedure\" if it should Summon itself."));
        }
        if (card.IsExtraDeck && project.Procedure.Kind == SummonProcedureKind.None)
            d.Add(new(Severity.Warning, $"This {card.Frame} monster has no summon procedure, so it can't be summoned. Set one on the Card tab."));
    }
}
