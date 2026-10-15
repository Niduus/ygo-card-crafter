using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>Writes the built-in summoning procedures and ability setups at the top of initial_effect.</summary>
internal static class Procedures
{
    public static int XyzLevel(SummonProcedure proc, CardData card) => proc.XyzMaterialLevel > 0 ? proc.XyzMaterialLevel : card.Level;

    /// <summary>
    /// Fusion Materials: listed one by one ("Aleister" monster + LIGHT or EARTH monster), with at most one slot that
    /// takes several cards; or the older exact passcodes; or "N+ monsters" that pass one filter.
    /// </summary>
    private static IEnumerable<string> Fusion(SummonProcedure proc, ScriptBuilder script)
    {
        if (proc.Materials.Count > 0)
        {
            var parts = new List<string>();
            foreach (var (slot, i) in proc.Materials.Select((m, i) => (m, i)))
                parts.Add(OnlyCode(slot.Filter) ? Lua.Num(slot.Filter.CardCode) : Fn(script.MaterialFilter(slot.Filter, $"fusfilter{i + 1}", SummonProcedureKind.Fusion)));
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
