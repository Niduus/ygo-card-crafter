using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>Writes the built-in summoning procedures and ability setups at the top of initial_effect.</summary>
internal static class Procedures
{
    public static List<string> Write(CardProject project, ScriptBuilder script)
    {
        var card = project.Card;
        var proc = project.Procedure;
        var lines = new List<string>();
        if (!card.IsMonster) return lines;

        if (card.IsExtraDeck || card.Frame == MonsterFrame.Ritual || card.Abilities.HasFlag(MonsterAbility.SpecialSummonOnly))
            lines.Add("c:EnableReviveLimit()");

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
                    if (proc.FusionMaterialCodes.Count >= 2)
                    {
                        var codes = string.Join(",", proc.FusionMaterialCodes.Select(Lua.Num));
                        lines.Add($"aux.AddFusionProcMix(c,true,true,{codes})");
                    }
                    else
                    {
                        var f = script.MaterialFilter(proc.MaterialFilter, "fusfilter");
                        lines.Add($"aux.AddFusionProcFunRep(c,{(f == "nil" ? "aux.TRUE" : f)},{Math.Max(2, proc.MinMaterials)},true)");
                    }
                    break;
                case SummonProcedureKind.Synchro:
                {
                    var tuner = script.MaterialFilter(proc.TunerFilter, "tunerfilter");
                    var non = script.MaterialFilter(proc.MaterialFilter, "nontunerfilter");
                    var max = proc.MaxMaterials > 0 ? "," + proc.MaxMaterials : "";
                    lines.Add($"aux.AddSynchroProcedure(c,aux.Tuner({tuner}),aux.NonTuner({non}),{Math.Max(1, proc.MinMaterials)}{max})");
                    break;
                }
                case SummonProcedureKind.Xyz:
                {
                    var f = script.MaterialFilter(proc.MaterialFilter, "xyzfilter");
                    var min = Math.Max(2, proc.MinMaterials);
                    var max = proc.MaxMaterials > min ? $",nil,nil,{proc.MaxMaterials}" : "";
                    lines.Add($"aux.AddXyzProcedure(c,{f},{card.Level},{min}{max})");
                    break;
                }
                case SummonProcedureKind.Link:
                {
                    var f = script.MaterialFilter(proc.MaterialFilter, "linkfilter");
                    var min = Math.Max(1, proc.MinMaterials);
                    var max = proc.MaxMaterials > 0 ? "," + proc.MaxMaterials : "";
                    lines.Add($"aux.AddLinkProcedure(c,{f},{min}{max})");
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
