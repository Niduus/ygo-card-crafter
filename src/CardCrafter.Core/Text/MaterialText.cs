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
                var text = $"{Count(min, max)} {F(proc.MaterialFilter, max != 1)}";
                if (proc.Including) text += ", including " + Article(F(proc.IncludeFilter, false));
                return text;
            }
            case SummonProcedureKind.Xyz:
            {
                var f = proc.MaterialFilter.Clone();
                f.LevelMin = f.LevelMax = Procedures.XyzLevel(proc, card);
                var min = Math.Max(2, proc.MinMaterials);
                return $"{Count(min, proc.MaxMaterials > min ? proc.MaxMaterials : min)} {F(f, true)}";
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
        return $"{lead} can also Xyz Summon {name} by using {Using(proc, pack)}{detach}. (Transfer its materials to this card.)";
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
