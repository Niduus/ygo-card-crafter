using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>
/// The materials line printed first on an Extra Deck monster: "2 Effect Monsters", "2 Level 12 monsters",
/// "1 Tuner + 1+ non-Tuner monsters", "1 "Aleister" monster + 1 EARTH or LIGHT monster".
/// </summary>
public static class MaterialText
{
    public static string Describe(CardProject project, CardPack? pack = null)
    {
        var card = project.Card;
        var proc = project.Procedure;
        if (!card.IsExtraDeck || proc.Kind == SummonProcedureKind.None) return "";
        string F(CardFilter f, bool plural) => FilterText.Describe(f, pack, plural, withLocation: false);

        switch (proc.Kind)
        {
            case SummonProcedureKind.Link:
            {
                var min = Math.Max(1, proc.MinMaterials);
                var max = proc.MaxMaterials > 0 ? proc.MaxMaterials : Math.Max(min, card.LinkRating);
                var rule = RuleText(proc.MaterialRule);
                // "2+ monsters with different names, except Tokens" (Apollousa).
                var except = "";
                var f = rule.Length > 0 ? WithoutExcept(proc.MaterialFilter, out except, pack) : proc.MaterialFilter;
                var text = $"{Count(min, max)} {F(f, max != 1)}{rule}{except}";
                if (proc.Including) text += ", including " + Article(F(proc.IncludeFilter, false));
                return text;
            }
            case SummonProcedureKind.Xyz:
            {
                var min = Math.Max(2, proc.MinMaterials);
                var count = Count(min, proc.MaxMaterials > min ? proc.MaxMaterials : min);
                var rule = RuleText(proc.MaterialRule);
                // "2 Xyz Monsters with the same Rank, except "Number" monsters" (Number F0: Utopic Future): no Level.
                if (proc.XyzSameRank)
                {
                    var same = WithoutExcept(proc.MaterialFilter, out var except, pack);
                    return $"{count} {F(same, true)} with the same Rank{(rule.Length > 0 ? " and" + rule[" with".Length..] : "")}{except}";
                }
                var f = proc.MaterialFilter.Clone();
                f.LevelMin = f.LevelMax = Procedures.XyzLevel(proc, card);
                return $"{count} {F(f, true)}{rule}";
            }
            case SummonProcedureKind.Synchro:
            {
                var tuner = proc.TunerFilter.Clone();
                if (tuner.MonsterKind == MonsterKind.Any) tuner.MonsterKind = MonsterKind.Tuner;
                var non = proc.MaterialFilter.Clone();
                if (non.MonsterKind == MonsterKind.Any) non.MonsterKind = MonsterKind.NonTuner;
                var min = Math.Max(1, proc.MinMaterials);
                var max = proc.MaxMaterials > 0 ? proc.MaxMaterials : int.MaxValue;
                return $"1 {F(tuner, false)} + {Count(min, max)} {F(non, max != 1)}";
            }
            default:
            {
                // "2+ Fusion Monsters with different Attributes" (Hyperinvoked Aeon).
                if (proc.Materials.Count == 1 && RuleText(proc.MaterialRule) is { Length: > 0 } rule)
                    return Slot(proc.Materials[0], pack) + rule;
                if (proc.Materials.Count > 0)
                    return string.Join(" + ", proc.Materials.Select(m => Slot(m, pack)));
                if (proc.FusionMaterialCodes.Count >= 2)
                    return string.Join(" + ", proc.FusionMaterialCodes.Select(c => Name(c, pack)));
                var n = Math.Max(2, proc.MinMaterials);
                return $"{n} {F(proc.MaterialFilter, true)}";
            }
        }
    }

    /// <summary>
    /// "Once per turn, if an Xyz Monster battled this turn, you can also Xyz Summon "Divine Arsenal AA-ZEUS - Sky Thunder" by
    /// using 1 Xyz Monster you control. (Transfer its materials to this card.)"; "" without another way to Xyz Summon.
    /// </summary>
    public static string Alternate(CardProject project, CardPack? pack = null)
    {
        var proc = project.Procedure;
        if (!project.Card.IsExtraDeck || proc is not { Kind: SummonProcedureKind.Xyz, XyzAlternate: true }) return "";
        var lead = (proc.XyzAlternateOncePerTurn, proc.XyzAlternateAfterXyzBattle) switch
        {
            (true, true) => "Once per turn, if an Xyz Monster battled this turn, you",
            (true, false) => "Once per turn, you",
            (false, true) => "If an Xyz Monster battled this turn, you",
            _ => "You",
        };
        var name = project.Card.Name.Length > 0 ? $"\"{project.Card.Name}\"" : "this card";
        var detach = proc.XyzAlternateDetach switch
        {
            0 => "",
            1 => " and detaching 1 material from it",
            var n => $" and detaching {n} materials from it",
        };
        // Gustav Rocket: "by discarding 1 card and using 1 "Superdreadnought Rail Cannon Gustav Max" you control".
        var discard = proc.XyzAlternateDiscard switch
        {
            0 => "",
            1 => "discarding 1 card and ",
            var n => $"discarding {n} cards and ",
        };
        // Kashtira Arise-Heart: "..., if an effect of "Kashtira Shangri-Ira" was successfully activated this turn."
        var after = proc.XyzAlternateAfterActivationOf > 0 ? $", if an effect of {Name(proc.XyzAlternateAfterActivationOf, pack)} was successfully activated this turn" : "";
        return $"{lead} can also Xyz Summon {name} by {discard}using {Using(proc, pack)}{detach}{after}. (Transfer its materials to this card.)";
    }

    /// <summary>"For this card's Synchro Summon, you can treat 1 Link-1 monster you control as a Level 1 Tuner."; "" without it.</summary>
    public static string SynchroLinkTuner(CardProject project) =>
        project.Card.IsExtraDeck && project.Procedure is { Kind: SummonProcedureKind.Synchro, SynchroLinkAsTuner: true }
            ? "For this card's Synchro Summon, you can treat 1 Link-1 monster you control as a Level 1 Tuner."
            : "";

    /// <summary>" with different names", " with different Types and different Attributes"..., or "".</summary>
    private static string RuleText(MaterialGroupRule rule) => rule switch
    {
        MaterialGroupRule.DifferentNames => " with different names",
        MaterialGroupRule.DifferentTypesAndAttributes => " with different Types and different Attributes",
        MaterialGroupRule.DifferentAttributes => " with different Attributes",
        MaterialGroupRule.DifferentTypes => " with different Types",
        _ => "",
    };

    /// <summary>
    /// The filter without what material lines say last with "except": not a Token (", except Tokens"), not of an
    /// archetype (", except "Number" monsters").
    /// </summary>
    private static CardFilter WithoutExcept(CardFilter filter, out string except, CardPack? pack)
    {
        var f = filter.Clone();
        var parts = new List<string>();
        if (f.MonsterKind == MonsterKind.NonToken)
        {
            f.MonsterKind = MonsterKind.Any;
            parts.Add("Tokens");
        }
        if (!f.AnyRequirement)
            foreach (var r in f.Requirements.Where(r => r is { Property: "archetype", Op: RequirementOp.IsNot } && r.Values.Any(v => v != 0)).ToList())
            {
                f.Requirements.Remove(r);
                parts.AddRange(r.Values.Where(v => v != 0).Select(v => $"\"{(pack is not null ? pack.ArchetypeName((int)v) : ArchetypeNames.Find((int)v) ?? $"0x{v:x}")}\" monsters"));
            }
        except = parts.Count == 0 ? "" : ", except " + string.Join(" or ", parts);
        return f;
    }

    /// <summary>The prompt asking which way to Xyz Summon: "Xyz Summon by using 1 Xyz Monster you control".</summary>
    public static string AlternatePrompt(CardProject project, CardPack? pack = null) =>
        $"Xyz Summon by using {Using(project.Procedure, pack)}";

    private static string Using(SummonProcedure proc, CardPack? pack)
    {
        var f = proc.XyzAlternateFilter.Clone();
        f.Owner = CardOwner.You;
        f.Zones = Zone.MonsterZone;
        f.FaceUpOnly = false;
        return $"1 {FilterText.Describe(f, pack, false, withLocation: false)} you control";
    }

    /// <summary>"2", "2+" or "2-3".</summary>
    private static string Count(int min, int max) =>
        max == min ? $"{min}" : $"{min}+";

    private static string Slot(MaterialSlot m, CardPack? pack) =>
        m.Or is { } alt ? $"{One(m, m.Filter, pack)} or {One(m, alt, pack)}" : One(m, m.Filter, pack);

    /// <summary>""Dark Magician"", "1 Dragon Effect Monster", "2+ Fiend monsters".</summary>
    private static string One(MaterialSlot m, CardFilter f, CardPack? pack)
    {
        var onlyCode = f.CardCode != 0;
        var plural = m.Max != 1;
        var noun = FilterText.Describe(f, pack, plural, withLocation: false);
        if (onlyCode && !m.Repeats) return noun; // "Dark Magician" + ...
        var count = m.Max == 0 ? $"{m.Min}+" : m.Min == m.Max ? $"{m.Min}" : $"{m.Min}+";
        return $"{count} {noun}";
    }

    private static string Name(long code, CardPack? pack) =>
        CardNames.Find(code, pack) is { } n ? $"\"{n}\"" : $"card with passcode {code}";

    /// <summary>"a "Sky Striker" monster", "an Effect Monster".</summary>
    private static string Article(string noun) =>
        noun.Length > 0 && "AEIOUaeiou".Contains(noun[0]) ? "an " + noun : "a " + noun;
}
