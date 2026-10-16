using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>Writes the built-in summoning procedures and ability setups at the top of initial_effect.</summary>
internal static class Procedures
{
    public static int XyzLevel(SummonProcedure proc, CardData card) => proc.XyzMaterialLevel > 0 ? proc.XyzMaterialLevel : card.Level;

    /// <summary>The player flag of "once per turn" for the other way to Xyz Summon, and the one set when an Xyz Monster battles.</summary>
    private const string AlternateUsedFlag = "id+7200", XyzBattledFlag = "id+7201";

    /// <summary>
    /// "You can also Xyz Summon this card by using 1 Xyz Monster you control": the engine's Xyz procedure with its
    /// alternative material, the prompt asking which way, and the check and cost of that way.
    /// </summary>
    private static IEnumerable<string> XyzAlternate(CardProject project, ScriptBuilder script, string f, int min)
    {
        var proc = project.Procedure;
        var alt = proc.XyzAlternateFilter;
        var parts = FilterCompiler.Conditions(new CardFilter
        {
            CardType = FilterCardType.Monster, MonsterKind = alt.MonsterKind, Race = alt.Race, Attribute = alt.Attribute, Archetype = alt.Archetype,
            Archetype2 = alt.Archetype2, CardCode = alt.CardCode, AltCodes = alt.AltCodes, ExceptOwnName = alt.ExceptOwnName, ExceptCode = alt.ExceptCode,
            LevelMin = alt.LevelMin, LevelMax = alt.LevelMax, AtkMin = alt.AtkMin, AtkMax = alt.AtkMax, DefMin = alt.DefMin, DefMax = alt.DefMax,
            Requirements = alt.Requirements,
        });
        parts.Insert(0, "c:IsFaceup()");
        if (proc.XyzAlternateDetach > 0) parts.Add($"c:CheckRemoveOverlayCard(tp,{proc.XyzAlternateDetach},REASON_COST)");
        var altf = script.AddFunction("xyzaltfilter", "c,e,tp,xyzc", [$"return {string.Join(" and ", parts)}"]);

        var checks = new List<string>();
        var pay = new List<string>();
        if (proc.XyzAlternateAfterXyzBattle) checks.Add($"Duel.GetFlagEffect(tp,{XyzBattledFlag})>0");
        if (proc.XyzAlternateOncePerTurn)
        {
            checks.Add($"Duel.GetFlagEffect(tp,{AlternateUsedFlag})==0");
            pay.Add($"Duel.RegisterFlagEffect(tp,{AlternateUsedFlag},RESET_PHASE+PHASE_END,EFFECT_FLAG_OATH,1)");
        }
        if (proc.XyzAlternateDetach > 0) pay.Insert(0, $"mc:RemoveOverlayCard(tp,{proc.XyzAlternateDetach},{proc.XyzAlternateDetach},REASON_COST)");
        var op = checks.Count + pay.Count == 0 ? "nil"
            : script.AddFunction("xyzaltop", "e,tp,chk,mc", [$"if chk==0 then return {(checks.Count == 0 ? "true" : string.Join(" and ", checks))} end", .. pay]);

        var slot = ScriptCompiler.StringSlots(project, script.Pack).FindIndex(s => s.Use == ScriptCompiler.XyzAlternateUse);
        // 1165 is the game's own "Xyz Summon" text, for a card whose 16 texts are all taken.
        var desc = slot is >= 0 and < 16 ? $"aux.Stringid(id,{slot})" : "1165";
        var max = Math.Max(min, proc.MaxMaterials);
        var lines = new List<string> { $"aux.AddXyzProcedure(c,{f},{XyzLevel(proc, project.Card)},{min},{altf},{desc},{max},{op})" };
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
            lines.AddRange(
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
        return lines;
    }

    /// <summary>
    /// Fusion Materials: listed one by one ("Aleister" monster + LIGHT or EARTH monster), with at most one slot that
    /// takes several cards; or the older exact passcodes; or "N+ monsters" that pass one filter.
    /// </summary>
    private static IEnumerable<string> Fusion(SummonProcedure proc, ScriptBuilder script)
    {
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
                yield return $"aux.AddFusionProcMix(c,true,true,{string.Join(",", parts)})";
                yield break;
            }
            var (rep, at) = repeating[0];
            var max = rep.Max == 0 ? 127 : rep.Max;
            var others = parts.Where((_, i) => i != at).ToList();
            if (others.Count == 0)
            {
                if (rep.Min < 2) { script.Diagnostics.Add(new(Severity.Error, "A Fusion Monster needs at least 2 materials.")); yield break; }
                yield return $"aux.AddFusionProcFunRep2(c,{parts[at]},{rep.Min},{max},true)";
                yield break;
            }
            yield return $"aux.AddFusionProcMixRep(c,true,true,{parts[at]},{Math.Max(1, rep.Min)},{max},{string.Join(",", others)})";
            yield break;
        }
        if (proc.FusionMaterialCodes.Count >= 2)
        {
            yield return $"aux.AddFusionProcMix(c,true,true,{string.Join(",", proc.FusionMaterialCodes.Select(Lua.Num))})";
            yield break;
        }
        var f = script.MaterialFilter(proc.MaterialFilter, "fusfilter", SummonProcedureKind.Fusion);
        yield return $"aux.AddFusionProcFunRep(c,{Fn(f)},{Math.Max(2, proc.MinMaterials)},true)";

        static string Fn(string f) => f == "nil" ? "aux.TRUE" : f;
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

        if (card.IsExtraDeck || card.Frame == MonsterFrame.Ritual || card.Abilities.HasFlag(MonsterAbility.SpecialSummonOnly))
            lines.Add("c:EnableReviveLimit()");

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
                    break;
                case SummonProcedureKind.Synchro:
                {
                    var tuner = script.MaterialFilter(proc.TunerFilter, "tunerfilter", SummonProcedureKind.Synchro);
                    var non = script.MaterialFilter(proc.MaterialFilter, "nontunerfilter", SummonProcedureKind.Synchro);
                    var max = proc.MaxMaterials > 0 ? "," + proc.MaxMaterials : "";
                    lines.Add($"aux.AddSynchroProcedure(c,{tuner},aux.NonTuner({non}),{Math.Max(1, proc.MinMaterials)}{max})");
                    break;
                }
                case SummonProcedureKind.Xyz:
                {
                    var f = script.MaterialFilter(proc.MaterialFilter, "xyzfilter", SummonProcedureKind.Xyz);
                    var min = Math.Max(2, proc.MinMaterials);
                    if (proc.XyzAlternate)
                    {
                        lines.AddRange(XyzAlternate(project, script, f, min));
                        break;
                    }
                    var max = proc.MaxMaterials > min ? $",nil,nil,{proc.MaxMaterials}" : "";
                    lines.Add($"aux.AddXyzProcedure(c,{f},{XyzLevel(proc, card)},{min}{max})");
                    break;
                }
                case SummonProcedureKind.Link:
                {
                    var f = script.MaterialFilter(proc.MaterialFilter, "linkfilter", SummonProcedureKind.Link);
                    var min = Math.Max(1, proc.MinMaterials);
                    if (proc.Including)
                    {
                        var inc = script.MaterialFilter(proc.IncludeFilter, "linkincfilter", SummonProcedureKind.Link);
                        var check = script.AddFunction("linkcheck", "g", [$"return g:IsExists({(inc == "nil" ? "aux.TRUE" : inc)},1,nil)"]);
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
