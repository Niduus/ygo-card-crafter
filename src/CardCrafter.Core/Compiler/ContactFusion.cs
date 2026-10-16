using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Contact Fusion: "Must first be Special Summoned (from your Extra Deck) by shuffling the above cards you control into
/// the Deck. (You do not use "Polymerization".)" The engine's Contact Fusion procedure, and the Special Summon
/// condition the card's summoning rule asks for:
/// <list type="bullet">
/// <item>Normal: "Must first be either Fusion Summoned, or Special Summoned from your Extra Deck by ..."</item>
/// <item>Special Summon only: "Must first be Special Summoned (from your Extra Deck) by ..." (no Fusion Summon).</item>
/// <item>Own procedure only: "Must be either Fusion Summoned, or Special Summoned by ..." (never revived).</item>
/// <item>Cannot be Special Summoned: "Must be Special Summoned (from your Extra Deck) by ..." (only this way, ever).</item>
/// </list>
/// </summary>
public static class ContactFusion
{
    public static bool Applies(CardProject project) =>
        project.Card.IsMonster && project.Card.Frame == MonsterFrame.Fusion && project.Procedure is { Kind: SummonProcedureKind.Fusion, Contact: true };

    /// <summary>The materials filter and what is done with them, as official scripts pass them to the procedure.</summary>
    private static (string Filter, string Move) Of(ContactFusionMove move) => move switch
    {
        ContactFusionMove.Banish => ("Card.IsAbleToRemoveAsCost", "Duel.Remove,POS_FACEUP,REASON_COST"),
        ContactFusionMove.SendToGrave => ("Card.IsAbleToGraveAsCost", "Duel.SendtoGrave,REASON_COST"),
        ContactFusionMove.Tribute => ("aux.FilterBoolFunction(Card.IsReleasable,REASON_SPSUMMON)", "Duel.Release,REASON_SPSUMMON+REASON_MATERIAL"),
        _ => ("Card.IsAbleToDeckOrExtraAsCost", "aux.ContactFusionSendToDeck(c)"),
    };

    /// <summary>The lines for initial_effect, after the Fusion Materials.</summary>
    public static List<string> Lines(CardProject project, ScriptBuilder script)
    {
        if (!Applies(project)) return [];
        var proc = project.Procedure;
        var card = project.Card;
        var zones = proc.ContactZones & (Zone.Field | Zone.Hand | Zone.Graveyard);
        if ((zones & Zone.Field) == Zone.None)
        {
            script.Diagnostics.Add(new(Severity.Error, "A Contact Fusion takes its materials from the field (and maybe the hand or GY)."));
            return [];
        }
        var self = Lua.Locations(zones);
        var opp = proc.ContactOpponent ? Lua.Locations(zones & Zone.Field) : "0";
        var (filter, move) = Of(proc.ContactMove);
        var lines = new List<string> { $"aux.AddContactFusionProcedure(c,{filter},{self},{opp},{move})" };
        // "Must be ..." rules are written with the other summoning rules; "Must first be ..." ones here.
        if (card.CannotBeSpecialSummoned || card.OnlyByOwnProcedure) return lines;
        var limit = card.SummonRule == SummonRule.SpecialSummonOnly
            ? "return not e:GetHandler():IsLocation(LOCATION_EXTRA)"
            : "return not e:GetHandler():IsLocation(LOCATION_EXTRA) or aux.fuslimit(e,se,sp,st)";
        var fn = script.AddFunction("contactlimit", "e,se,sp,st", [limit]);
        lines.AddRange(
        [
            "--Special Summon condition",
            "local cf=Effect.CreateEffect(c)",
            "cf:SetType(EFFECT_TYPE_SINGLE)",
            "cf:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)",
            "cf:SetCode(EFFECT_SPSUMMON_CONDITION)",
            $"cf:SetValue({fn})",
            "c:RegisterEffect(cf)",
        ]);
        return lines;
    }

    /// <summary>"shuffling the above cards you control into the Deck", "banishing the above cards from your hand, field, and/or GY".</summary>
    public static string How(SummonProcedure proc)
    {
        var zones = proc.ContactZones;
        string where;
        if (proc.ContactOpponent) where = "from either field";
        else if ((zones & (Zone.Hand | Zone.Graveyard)) == Zone.None) where = "you control";
        else if ((zones & Zone.Hand) == Zone.None) where = "you control and/or from your GY";
        else
        {
            var places = new List<string> { "hand", "field" };
            if (zones.HasFlag(Zone.Graveyard)) places.Add("GY");
            where = places.Count == 2 ? "from your hand and/or field" : "from your hand, field, and/or GY";
        }
        return proc.ContactMove switch
        {
            ContactFusionMove.Banish => $"banishing the above cards {where}",
            ContactFusionMove.SendToGrave => $"sending the above cards {where} to the GY",
            ContactFusionMove.Tribute => $"Tributing the above cards {where}",
            _ => $"shuffling the above cards {where} into the Deck",
        };
    }

    /// <summary>The summoning rule line of a Contact Fusion Monster, or "" for other cards.</summary>
    public static string Text(CardProject project)
    {
        if (!Applies(project)) return "";
        var how = How(project.Procedure);
        var card = project.Card;
        if (card.CannotBeSpecialSummoned) return $"Must be Special Summoned (from your Extra Deck) by {how}.";
        return card.SummonRule switch
        {
            SummonRule.ProcedureOnly => $"Must be either Fusion Summoned, or Special Summoned by {how} (in which case you do not use \"Polymerization\").",
            SummonRule.SpecialSummonOnly => $"Must first be Special Summoned (from your Extra Deck) by {how}. (You do not use \"Polymerization\".)",
            _ => $"Must first be either Fusion Summoned, or Special Summoned from your Extra Deck by {how}.",
        };
    }
}
