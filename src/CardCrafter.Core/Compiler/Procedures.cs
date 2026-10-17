using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>Writes the built-in summoning procedures and ability setups at the top of initial_effect.</summary>
internal static class Procedures
{
    public static int XyzLevel(SummonProcedure proc, CardData card) => proc.XyzMaterialLevel > 0 ? proc.XyzMaterialLevel : card.Level;

    /// <summary>The player flag of "once per turn" for the other way to Xyz Summon, and the one set when an Xyz Monster battles.</summary>
    private const string AlternateUsedFlag = "id+7200", XyzBattledFlag = "id+7201";

    /// <summary>
    /// "You can also Xyz Summon this card by using 1 Xyz Monster you control": the engine's alternative material, the
    /// prompt asking which way, and the check and cost of that way (once per turn, after an Xyz Monster battled, detaching,
    /// discarding, after an effect of a card was activated), plus the lines that watch for what the check needs.
    /// </summary>
    private static (string Filter, string Desc, string Op, List<string> Extra) XyzAlternate(CardProject project, ScriptBuilder script)
    {
        var proc = project.Procedure;
        var alt = proc.XyzAlternateFilter;
        var parts = FilterCompiler.Conditions(new CardFilter
        {
            CardType = FilterCardType.Monster, MonsterKind = alt.MonsterKind, Race = alt.Race, Attribute = alt.Attribute, Archetype = alt.Archetype,
            Archetype2 = alt.Archetype2, CardCode = alt.CardCode, AltCodes = alt.AltCodes, ExceptOwnName = alt.ExceptOwnName, ExceptCode = alt.ExceptCode,
            LevelMin = alt.LevelMin, LevelMax = alt.LevelMax, AtkMin = alt.AtkMin, AtkMax = alt.AtkMax, DefMin = alt.DefMin, DefMax = alt.DefMax,
            Requirements = alt.Requirements, AnyRequirement = alt.AnyRequirement,
        });
        parts.Insert(0, "c:IsFaceup()");
        if (proc.XyzAlternateDetach > 0) parts.Add($"c:CheckRemoveOverlayCard(tp,{proc.XyzAlternateDetach},REASON_COST)");
        var altf = script.AddFunction("xyzaltfilter", "c,e,tp,xyzc", [$"return {string.Join(" and ", parts)}"]);

        var checks = new List<string>();
        var pay = new List<string>();
        var extra = new List<string>();
        if (proc.XyzAlternateAfterXyzBattle) checks.Add($"Duel.GetFlagEffect(tp,{XyzBattledFlag})>0");
        if (proc.XyzAlternateOncePerTurn) checks.Add($"Duel.GetFlagEffect(tp,{AlternateUsedFlag})==0");
        // "if an effect of "Kashtira Shangri-Ira" was successfully activated this turn": either player's activations of it.
        if (proc.XyzAlternateAfterActivationOf > 0)
        {
            var chain = script.AddFunction("xyzchainfilter", "re,tp,cid", [$"return not re:GetHandler():IsCode({Lua.Num(proc.XyzAlternateAfterActivationOf)})"]);
            var counter = script.ActivityCounter("ACTIVITY_CHAIN", chain);
            checks.Add($"(Duel.GetCustomActivityCount({counter},tp,ACTIVITY_CHAIN)>0 or Duel.GetCustomActivityCount({counter},1-tp,ACTIVITY_CHAIN)>0)");
        }
        if (proc.XyzAlternateDetach > 0) pay.Add($"mc:RemoveOverlayCard(tp,{proc.XyzAlternateDetach},{proc.XyzAlternateDetach},REASON_COST)");
        // "by discarding 1 card and using ...", as Gustav Rocket discards.
        if (proc.XyzAlternateDiscard > 0)
        {
            var n = proc.XyzAlternateDiscard;
            var discard = script.AddFunction("xyzdiscardfilter", "c", ["return c:IsDiscardable(REASON_SPSUMMON)"]);
            checks.Add($"Duel.IsExistingMatchingCard({discard},tp,LOCATION_HAND,0,{n},nil)");
            pay.Add($"Duel.DiscardHand(tp,{discard},{n},{n},REASON_COST+REASON_DISCARD)");
        }
        if (proc.XyzAlternateOncePerTurn) pay.Add($"Duel.RegisterFlagEffect(tp,{AlternateUsedFlag},RESET_PHASE+PHASE_END,EFFECT_FLAG_OATH,1)");
        var op = checks.Count + pay.Count == 0 ? "nil"
            : script.AddFunction("xyzaltop", "e,tp,chk,mc", [$"if chk==0 then return {(checks.Count == 0 ? "true" : string.Join(" and ", checks))} end", .. pay]);

        var slot = ScriptCompiler.StringSlots(project, script.Pack).FindIndex(s => s.Use == ScriptCompiler.XyzAlternateUse);
        // 1165 is the game's own "Xyz Summon" text, for a card whose 16 texts are all taken.
        var desc = slot is >= 0 and < 16 ? $"aux.Stringid(id,{slot})" : "1165";
        if (proc.XyzAlternateAfterXyzBattle)
        {
            var battled = script.AddFunction("xyzbattled", "e,tp,eg,ep,ev,re,r,rp",
            [
                "local a,d=Duel.GetAttacker(),Duel.GetAttackTarget()",
                "if a and a:IsType(TYPE_XYZ) or d and d:IsType(TYPE_XYZ) then",
                $"\tDuel.RegisterFlagEffect(0,{XyzBattledFlag},RESET_PHASE+PHASE_END,0,1)",
                $"\tDuel.RegisterFlagEffect(1,{XyzBattledFlag},RESET_PHASE+PHASE_END,0,1)",
                "end",
            ]);
            // Every battle of the Duel is watched once, by the first copy of this card.
            extra.AddRange(
            [
                "if not s.cc_xyz_battled then",
                "\ts.cc_xyz_battled=true",
                "\tlocal ge=Effect.CreateEffect(c)",
                "\tge:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
                "\tge:SetCode(EVENT_BATTLED)",
                $"\tge:SetOperation({battled})",
                "\tDuel.RegisterEffect(ge,0)",
                "end",
            ]);
        }
        return (altf, desc, op, extra);
    }

    /// <summary>
    /// The check of a <see cref="MaterialGroupRule"/> on a group g of materials, or null for none. Link Summons read the
    /// materials' Link names, Types and Attributes (as Apollousa and Aleister the Invoker of Madness do).
    /// </summary>
    internal static string? GroupRule(MaterialGroupRule rule, SummonProcedureKind kind)
    {
        var link = kind == SummonProcedureKind.Link;
        string Distinct(string getter, string linkGetter) => $"g:GetClassCount(Card.{(link ? linkGetter : getter)})==#g";
        return rule switch
        {
            MaterialGroupRule.DifferentNames => Distinct("GetCode", "GetLinkCode"),
            MaterialGroupRule.DifferentTypesAndAttributes => $"{Distinct("GetRace", "GetLinkRace")} and {Distinct("GetAttribute", "GetLinkAttribute")}",
            MaterialGroupRule.DifferentAttributes => Distinct("GetAttribute", "GetLinkAttribute"),
            MaterialGroupRule.DifferentTypes => Distinct("GetRace", "GetLinkRace"),
            _ => null,
        };
    }

    /// <summary>
    /// Xyz: the engine's procedure for materials of one Level, or the level-free one for "2 Xyz Monsters with the same
    /// Rank" and for materials checked as a group; each with the other way to Xyz Summon when there is one.
    /// </summary>
    private static IEnumerable<string> Xyz(CardProject project, ScriptBuilder script)
    {
        var proc = project.Procedure;
        var f = script.MaterialFilter(proc.MaterialFilter, "xyzfilter", SummonProcedureKind.Xyz);
        var min = Math.Max(2, proc.MinMaterials);
        var max = Math.Max(min, proc.MaxMaterials);
        var lv = XyzLevel(proc, project.Card);
        (string Filter, string Desc, string Op, List<string> Extra)? alt = proc.XyzAlternate ? XyzAlternate(project, script) : null;
        if (proc.XyzSameRank || proc.MaterialRule != MaterialGroupRule.None)
        {
            var checks = new List<string>();
            if (proc.XyzSameRank) checks.Add("g:GetClassCount(Card.GetRank)==1");
            if (GroupRule(proc.MaterialRule, SummonProcedureKind.Xyz) is { } rule) checks.Add(rule);
            var gf = script.AddFunction("xyzcheck", "g", [$"return {string.Join(" and ", checks)}"]);
            // Materials of one Level checked as a group: the level-free procedure, with the Level in the filter.
            if (!proc.XyzSameRank)
                f = script.AddFunction("xyzlvfilter", "c,xyzc", [$"return c:IsXyzLevel(xyzc,{lv}){(f == "nil" ? "" : $" and {f}(c)")}"]);
            var tail = alt is { } a ? $",{a.Filter},{a.Desc}{(a.Op == "nil" ? "" : "," + a.Op)}" : "";
            yield return $"aux.AddXyzProcedureLevelFree(c,{f},{gf},{min},{max}{tail})";
        }
        else if (alt is { } a)
            yield return $"aux.AddXyzProcedure(c,{f},{lv},{min},{a.Filter},{a.Desc},{max},{a.Op})";
        else
            yield return $"aux.AddXyzProcedure(c,{f},{lv},{min}{(proc.MaxMaterials > min ? $",nil,nil,{proc.MaxMaterials}" : "")})";
        if (alt is { } x)
            foreach (var l in x.Extra) yield return l;
    }

    /// <summary>
    /// Synchro: 1 Tuner + non-Tuners, or, with "you can treat 1 Link-1 monster you control as a Level 1 Tuner", the mixed
    /// procedure and the Level the Link-1 monster has for this card (EFFECT_SYNCHRO_LEVEL_EX), as Lollipo★Yummy Way does.
    /// </summary>
    private static IEnumerable<string> Synchro(SummonProcedure proc, ScriptBuilder script)
    {
        var tuner = script.MaterialFilter(proc.TunerFilter, "tunerfilter", SummonProcedureKind.Synchro);
        var non = script.MaterialFilter(proc.MaterialFilter, "nontunerfilter", SummonProcedureKind.Synchro);
        if (proc.MaterialRule != MaterialGroupRule.None)
            script.Diagnostics.Add(new(Severity.Warning, "Synchro materials can't be checked as a group (\"with different names\"...); the rule is left out."));
        if (!proc.SynchroLinkAsTuner)
        {
            var max = proc.MaxMaterials > 0 ? "," + proc.MaxMaterials : "";
            yield return $"aux.AddSynchroProcedure(c,{tuner},aux.NonTuner({non}),{Math.Max(1, proc.MinMaterials)}{max})";
            yield break;
        }
        string And(string f) => f == "nil" ? "" : $" and {f}(c)";
        var f1 = script.AddFunction("synmatfilter1", "c,syncard", [$"return c:IsTuner(syncard){And(tuner)} or c:IsType(TYPE_LINK) and c:IsLink(1)"]);
        var f2 = script.AddFunction("synmatfilter2", "c,syncard", [$"return c:IsNotTuner(syncard) and not c:IsType(TYPE_LINK){And(non)}"]);
        var target = script.AddFunction("synlinktg", "e,c", ["return c:IsType(TYPE_LINK) and c:IsLink(1)"]);
        var value = script.AddFunction("synlinkval", "e,syncard", ["if e:GetHandler()==syncard then", "\treturn 1", "else", "\treturn 0", "end"]);
        yield return $"aux.AddSynchroMixProcedure(c,{f1},nil,nil,{f2},{Math.Max(1, proc.MinMaterials)},{(proc.MaxMaterials > 0 ? proc.MaxMaterials : 99)})";
        yield return "--A Link-1 monster is a Level 1 Tuner for this card's Synchro Summon";
        yield return "local slv=Effect.CreateEffect(c)";
        yield return "slv:SetType(EFFECT_TYPE_FIELD)";
        yield return "slv:SetCode(EFFECT_SYNCHRO_LEVEL_EX)";
        yield return "slv:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_IGNORE_IMMUNE)";
        yield return "slv:SetRange(LOCATION_EXTRA)";
        yield return "slv:SetTargetRange(LOCATION_MZONE,LOCATION_MZONE)";
        yield return $"slv:SetTarget({target})";
        yield return $"slv:SetValue({value})";
        yield return "c:RegisterEffect(slv)";
    }

    /// <summary>
    /// "2+ Fusion Monsters with different Attributes": one kind of material taken as a group, written as the Fusion
    /// Material effect of Hyperinvoked Aeon (the engine's helpers can't check a group).
    /// </summary>
    private static IEnumerable<string> FusionGroup(ScriptBuilder script, CardFilter filter, int min, int max, string rule)
    {
        var f = script.MaterialFilter(filter, "fusfilter1", SummonProcedureKind.Fusion);
        var mat = script.AddFunction("fusmatfilter", "c,fc", [$"return c:IsCanBeFusionMaterial(fc){(f == "nil" ? "" : $" and {f}(c)")}"]);
        var check = script.AddFunction("fusgroupcheck", "g", [$"return {rule}"]);
        var goal = script.AddFunction("fusgoal", "g,fc,gc,tp,chkf",
        [
            "if gc and not g:IsContains(gc) then return false end",
            "if g:IsExists(aux.TuneMagicianCheckX,1,nil,g,EFFECT_TUNE_MAGICIAN_F) then return false end",
            "if not aux.MustMaterialCheck(g,tp,EFFECT_MUST_BE_FMATERIAL) then return false end",
            "if aux.FCheckAdditional and not aux.FCheckAdditional(tp,g,fc)",
            "\tor aux.FGoalCheckAdditional and not aux.FGoalCheckAdditional(tp,g,fc) then return false end",
            "return chkf==PLAYER_NONE or Duel.GetLocationCountFromEx(tp,tp,g,fc)>0",
        ]);
        var con = script.AddFunction("fuscon", "e,g,gc,chkf",
        [
            "if g==nil then return aux.MustMaterialCheck(nil,e:GetHandlerPlayer(),EFFECT_MUST_BE_FMATERIAL) end",
            "local c=e:GetHandler()",
            $"local mg=g:Filter({mat},nil,c)",
            "local tp=e:GetHandlerPlayer()",
            "if gc and not mg:IsContains(gc) then return false end",
            $"aux.GCheckAdditional={check}",
            $"local res=mg:CheckSubGroup({goal},{min},{max},c,gc,tp,chkf)",
            "aux.GCheckAdditional=nil",
            "return res",
        ]);
        var op = script.AddFunction("fusop", "e,tp,eg,ep,ev,re,r,rp,gc,chkf",
        [
            "local c=e:GetHandler()",
            $"local mg=eg:Filter({mat},nil,c)",
            $"aux.GCheckAdditional={check}",
            "local g=nil",
            "while not g do",
            "\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_FMATERIAL)",
            $"\tg=mg:SelectSubGroup(tp,{goal},true,{min},{max},c,gc,tp,chkf)",
            "end",
            "aux.GCheckAdditional=nil",
            "Duel.SetFusionMaterial(g)",
        ]);
        yield return "--Fusion Materials, checked as a group";
        yield return "local fm=Effect.CreateEffect(c)";
        yield return "fm:SetType(EFFECT_TYPE_SINGLE)";
        yield return "fm:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)";
        yield return "fm:SetCode(EFFECT_FUSION_MATERIAL)";
        yield return $"fm:SetCondition({con})";
        yield return $"fm:SetOperation({op})";
        yield return "c:RegisterEffect(fm)";
    }

    /// <summary>
    /// Fusion Materials: listed one by one ("Aleister" monster + LIGHT or EARTH monster), with at most one slot that
    /// takes several cards; or the older exact passcodes; or "N+ monsters" that pass one filter.
    /// </summary>
    private static IEnumerable<string> Fusion(SummonProcedure proc, ScriptBuilder script)
    {
        if (GroupRule(proc.MaterialRule, SummonProcedureKind.Fusion) is { } rule)
        {
            // "2+ Fusion Monsters with different Attributes": one material that takes several cards, checked as a group.
            if (proc.Materials.Count > 1 || proc.Materials.Count == 1 && proc.Materials[0].Or is not null || proc.Materials.Count == 0 && proc.FusionMaterialCodes.Count >= 2)
            {
                script.Diagnostics.Add(new(Severity.Error, "Fusion Materials checked as a group (\"with different Attributes\"...) must be one kind of material that takes several cards."));
                yield break;
            }
            var (filter, min, max) = proc.Materials is [var one] ? (one.Filter, one.Min, one.Max == 0 ? 99 : one.Max) : (proc.MaterialFilter, Math.Max(2, proc.MinMaterials), Math.Max(2, proc.MinMaterials));
            if (min < 2) { script.Diagnostics.Add(new(Severity.Error, "A Fusion Monster needs at least 2 materials.")); yield break; }
            foreach (var l in FusionGroup(script, filter, min, max, rule)) yield return l;
            yield break;
        }
        if (proc.Materials.Count > 0)
        {
            var parts = new List<string>();
            string Part(CardFilter f, string name) => OnlyCode(f) ? Lua.Num(f.CardCode) : Fn(script.MaterialFilter(f, name, SummonProcedureKind.Fusion));
            // A slot with an "or" takes either: {74677422,s.fusfilter2b}.
            foreach (var (slot, i) in proc.Materials.Select((m, i) => (m, i)))
                parts.Add(slot.Or is { } alt ? $"{{{Part(slot.Filter, $"fusfilter{i + 1}")},{Part(alt, $"fusfilter{i + 1}b")}}}" : Part(slot.Filter, $"fusfilter{i + 1}"));
            var repeating = proc.Materials.Select((m, i) => (m, i)).Where(x => x.m.Repeats).ToList();
            if (repeating.Count > 1)
            {
                script.Diagnostics.Add(new(Severity.Error, "Only one Fusion Material can be \"2+\" or more than 1; the others must be 1 card each."));
                yield break;
            }
            if (repeating.Count == 0)
            {
                if (parts.Count < 2) { script.Diagnostics.Add(new(Severity.Error, "A Fusion Monster needs at least 2 materials.")); yield break; }
                yield return $"aux.AddFusionProcMix(c,{Sub(proc)},true,{string.Join(",", parts)})";
                yield break;
            }
            var (rep, at) = repeating[0];
            var max = rep.Max == 0 ? 127 : rep.Max;
            var others = parts.Where((_, i) => i != at).ToList();
            if (others.Count == 0)
            {
                if (rep.Min < 2) { script.Diagnostics.Add(new(Severity.Error, "A Fusion Monster needs at least 2 materials.")); yield break; }
                // "2+ "Blue-Eyes White Dragon"": a passcode repeats with the helper for names.
                yield return rep.Or is null && OnlyCode(rep.Filter)
                    ? $"aux.AddFusionProcCodeRep2(c,{parts[at]},{rep.Min},{max},{Sub(proc)},true)"
                    : $"aux.AddFusionProcFunRep2(c,{parts[at]},{rep.Min},{max},true)";
                yield break;
            }
            yield return $"aux.AddFusionProcMixRep(c,{Sub(proc)},true,{parts[at]},{Math.Max(1, rep.Min)},{max},{string.Join(",", others)})";
            yield break;
        }
        if (proc.FusionMaterialCodes.Count >= 2)
        {
            yield return $"aux.AddFusionProcMix(c,{Sub(proc)},true,{string.Join(",", proc.FusionMaterialCodes.Select(Lua.Num))})";
            yield break;
        }
        var f = script.MaterialFilter(proc.MaterialFilter, "fusfilter", SummonProcedureKind.Fusion);
        yield return $"aux.AddFusionProcFunRep(c,{Fn(f)},{Math.Max(2, proc.MinMaterials)},true)";

        static string Fn(string f) => f == "nil" ? "aux.TRUE" : f;
        // Whether a Fusion substitute can stand in for a named material.
        static string Sub(SummonProcedure p) => p.FusionNoSubstitutes ? "false" : "true";
    }

    /// <summary>The archetypes the Fusion Materials name ("Elemental HERO" monster), as hexadecimal setcodes.</summary>
    private static List<string> MaterialSetcodes(SummonProcedure proc)
    {
        var codes = new List<int>();
        foreach (var f in proc.Materials.SelectMany(m => m.Or is { } o ? new[] { m.Filter, o } : [m.Filter]))
        {
            if (f.Archetype != 0) codes.Add(f.Archetype);
            if (f.Archetype2 != 0) codes.Add(f.Archetype2);
            foreach (var r in f.Requirements.Where(r => r is { Property: "archetype", Op: RequirementOp.Is }))
                codes.AddRange(r.Values.Where(v => v != 0).Select(v => (int)v));
        }
        return codes.Distinct().Select(c => $"0x{c:x}").ToList();
    }

    /// <summary>True when the filter names one card and nothing else, so it can be written as its passcode (which allows substitutes).</summary>
    private static bool OnlyCode(CardFilter f) =>
        f.CardCode != 0 && f.Archetype == 0 && f.Race == CardRace.None && f.Attribute == CardAttribute.None && f.MonsterKind == MonsterKind.Any
        && f.LevelMin is null && f.LevelMax is null && f.AtkMin is null && f.AtkMax is null;

    public static List<string> Write(CardProject project, ScriptBuilder script)
    {
        var card = project.Card;
        var proc = project.Procedure;
        var lines = new List<string>();
        // "You can only control 1 "Card Name"." works on any card; the rest is for monsters.
        if (card.OnlyOneOnField) lines.Add("c:SetUniqueOnField(1,0,id)");
        if (!card.IsMonster) return lines;
        if (card.SpecialSummonOncePerTurn) lines.Add("c:SetSPSummonOnce(id)");

        // "Must be Special Summoned by a card effect" has no revive limit: any effect can Special Summon it, from anywhere.
        var byEffect = card.SummonRule == SummonRule.ByCardEffect && card.IsMainDeckNonRitual;
        if (card.IsExtraDeck || card.Frame == MonsterFrame.Ritual || card.Abilities.HasFlag(MonsterAbility.SpecialSummonOnly) && !byEffect)
            lines.Add("c:EnableReviveLimit()");
        if (byEffect)
        {
            lines.Add("--Must be Special Summoned by a card effect");
            lines.Add("local esc=Effect.CreateEffect(c)");
            lines.Add("esc:SetType(EFFECT_TYPE_SINGLE)");
            lines.Add("esc:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)");
            lines.Add("esc:SetCode(EFFECT_SPSUMMON_CONDITION)");
            lines.Add($"esc:SetValue({script.AddFunction("splimit", "e,se,sp,st", ["return se:IsHasType(EFFECT_TYPE_ACTIONS)"])})");
            lines.Add("c:RegisterEffect(esc)");
        }

        if (card.OnlyByOwnProcedure)
        {
            // Main-deck monsters: no value means "never", which their own procedure is exempt from.
            var limit = card.Frame switch
            {
                MonsterFrame.Fusion => "aux.fuslimit",
                MonsterFrame.Synchro => "aux.synlimit",
                MonsterFrame.Xyz => "aux.xyzlimit",
                MonsterFrame.Link => "aux.linklimit",
                MonsterFrame.Ritual => "aux.ritlimit",
                _ => null,
            };
            lines.Add("--Cannot be Special Summoned by other ways");
            lines.Add("local sc=Effect.CreateEffect(c)");
            lines.Add("sc:SetType(EFFECT_TYPE_SINGLE)");
            lines.Add("sc:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)");
            lines.Add("sc:SetCode(EFFECT_SPSUMMON_CONDITION)");
            if (limit is not null) lines.Add($"sc:SetValue({limit})");
            lines.Add("c:RegisterEffect(sc)");
        }

        if (card.CannotBeSpecialSummoned && !card.OnlyByOwnProcedure)
        {
            lines.Add("--Cannot be Special Summoned");
            lines.Add("local nsc=Effect.CreateEffect(c)");
            lines.Add("nsc:SetType(EFFECT_TYPE_SINGLE)");
            lines.Add("nsc:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)");
            lines.Add("nsc:SetCode(EFFECT_SPSUMMON_CONDITION)");
            lines.Add("c:RegisterEffect(nsc)");
        }

        var expected = card.Frame switch
        {
            MonsterFrame.Fusion => SummonProcedureKind.Fusion,
            MonsterFrame.Synchro => SummonProcedureKind.Synchro,
            MonsterFrame.Xyz => SummonProcedureKind.Xyz,
            MonsterFrame.Link => SummonProcedureKind.Link,
            _ => SummonProcedureKind.None,
        };
        if (proc.Kind != SummonProcedureKind.None && proc.Kind != expected)
        {
            script.Diagnostics.Add(new(Severity.Error, $"A {proc.Kind} summon procedure needs a {proc.Kind} monster, but this card is {card.Frame}."));
        }
        else
        {
            switch (proc.Kind)
            {
                case SummonProcedureKind.Fusion:
                    lines.AddRange(Fusion(proc, script));
                    if (MaterialSetcodes(proc) is { Count: > 0 } sets)
                        // The archetypes its materials name, which "a Fusion Monster that mentions a "HERO" monster as material" reads.
                        lines.Add($"s.material_setcode={(sets.Count == 1 ? sets[0] : "{" + string.Join(",", sets) + "}")}");
                    lines.AddRange(ContactFusion.Lines(project, script));
                    break;
                case SummonProcedureKind.Synchro:
                    lines.AddRange(Synchro(proc, script));
                    break;
                case SummonProcedureKind.Xyz:
                    lines.AddRange(Xyz(project, script));
                    break;
                case SummonProcedureKind.Link:
                {
                    var f = script.MaterialFilter(proc.MaterialFilter, "linkfilter", SummonProcedureKind.Link);
                    var min = Math.Max(1, proc.MinMaterials);
                    // "including a "Sky Striker" monster", "with different names": a check of the materials as a group.
                    var checks = new List<string>();
                    if (proc.Including)
                    {
                        var inc = script.MaterialFilter(proc.IncludeFilter, "linkincfilter", SummonProcedureKind.Link);
                        checks.Add($"g:IsExists({(inc == "nil" ? "aux.TRUE" : inc)},1,nil)");
                    }
                    if (GroupRule(proc.MaterialRule, SummonProcedureKind.Link) is { } rule) checks.Add(rule);
                    if (checks.Count > 0)
                    {
                        var check = script.AddFunction("linkcheck", "g", [$"return {string.Join(" and ", checks)}"]);
                        lines.Add($"aux.AddLinkProcedure(c,{f},{min},{(proc.MaxMaterials > 0 ? proc.MaxMaterials.ToString(System.Globalization.CultureInfo.InvariantCulture) : "nil")},{check})");
                    }
                    else
                    {
                        var max = proc.MaxMaterials > 0 ? "," + proc.MaxMaterials : "";
                        lines.Add($"aux.AddLinkProcedure(c,{f},{min}{max})");
                    }
                    break;
                }
            }
        }

        if (card.Abilities.HasFlag(MonsterAbility.Pendulum)) lines.Add("aux.EnablePendulumAttribute(c)");
        if (card.Abilities.HasFlag(MonsterAbility.Gemini)) lines.Add("aux.EnableDualAttribute(c)");
        if (card.Abilities.HasFlag(MonsterAbility.Spirit)) lines.Add("aux.EnableSpiritReturn(c,EVENT_SUMMON_SUCCESS,EVENT_FLIP)");
        return lines;
    }
}
