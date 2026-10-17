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
        var lines = proc.ContactToOpponent || proc.ContactOneMoreSummon
            ? OwnProcedure(proc, script, filter, self, opp)
            : [$"aux.AddContactFusionProcedure(c,{filter},{self},{opp},{move})"];
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

    /// <summary>
    /// The procedure written out, as aux.AddContactFusionProcedure would make it, for what it can't do: Summoning to the
    /// opponent's Main Monster Zone (a zone of theirs must be free once the materials are gone), and "you can only Special
    /// Summon once for the rest of this turn" afterwards, as Lava Golem, Steelcage of the Sun God does.
    /// </summary>
    private static List<string> OwnProcedure(SummonProcedure proc, ScriptBuilder script, string filter, string self, string opp)
    {
        string Room(string g) => proc.ContactToOpponent
            ? $"Duel.GetMZoneCount(1-tp,{g},tp,LOCATION_REASON_TOFIELD,0x1f)>0"
            : $"Duel.GetLocationCountFromEx(tp,tp,{g},c)>0";
        var materials = $"Duel.GetMatchingGroup(aux.ContactFusionMaterialFilter,tp,{self},{opp},c,c,{filter})";
        var con = script.AddFunction("contactcon", "e,c",
        [
            "if c==nil then return true end",
            "if c:IsType(TYPE_PENDULUM) and c:IsFaceup() then return false end",
            "local tp=c:GetControler()",
            $"local mg={materials}",
            $"return {Room("mg")} and c:CheckFusionMaterial(mg,nil,tp|0x200)",
        ]);
        var tg = script.AddFunction("contacttg", "e,tp,eg,ep,ev,re,r,rp,chk,c",
        [
            $"local mg={materials}",
            "local g=Duel.SelectFusionMaterial(tp,c,mg,nil,tp|0x200)",
            // The materials chosen must leave a zone for this card.
            $"if #g>0 and {Room("g")} then",
            "\tg:KeepAlive()",
            "\te:SetLabelObject(g)",
            "\treturn true",
            "end",
            "return false",
        ]);
        var move = proc.ContactMove switch
        {
            ContactFusionMove.Banish => "Duel.Remove(g,POS_FACEUP,REASON_COST)",
            ContactFusionMove.SendToGrave => "Duel.SendtoGrave(g,REASON_COST)",
            ContactFusionMove.Tribute => "Duel.Release(g,REASON_SPSUMMON+REASON_MATERIAL)",
            _ => "aux.ContactFusionSendToDeck(c)(g)",
        };
        var body = new List<string> { "local g=e:GetLabelObject()", "c:SetMaterial(g)", move, "g:DeleteGroup()" };
        if (proc.ContactOneMoreSummon)
        {
            // Counted before this Summon: this card and one more Special Summon are allowed.
            var limit = script.AddFunction("contactlimit", "e,c,sump", ["return Duel.GetActivityCount(sump,ACTIVITY_SPSUMMON)>e:GetLabel()+1"]);
            body.AddRange(
            [
                "local e1=Effect.CreateEffect(c)",
                "e1:SetType(EFFECT_TYPE_FIELD)",
                "e1:SetProperty(EFFECT_FLAG_PLAYER_TARGET)",
                "e1:SetCode(EFFECT_CANNOT_SPECIAL_SUMMON)",
                "e1:SetTargetRange(1,0)",
                "e1:SetLabel(Duel.GetActivityCount(tp,ACTIVITY_SPSUMMON))",
                $"e1:SetTarget({limit})",
                "e1:SetReset(RESET_PHASE+PHASE_END)",
                "Duel.RegisterEffect(e1,tp)",
            ]);
        }
        var op = script.AddFunction("contactop", "e,tp,eg,ep,ev,re,r,rp,c", body);
        var lines = new List<string>
        {
            "--Contact Fusion",
            "local cp=Effect.CreateEffect(c)",
            "cp:SetType(EFFECT_TYPE_FIELD)",
            "cp:SetCode(EFFECT_SPSUMMON_PROC)",
            proc.ContactToOpponent ? "cp:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE+EFFECT_FLAG_SPSUM_PARAM)" : "cp:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)",
        };
        // To the opponent's field, in one of their Main Monster Zones.
        if (proc.ContactToOpponent) lines.AddRange(["cp:SetTargetRange(POS_FACEUP,1)", $"cp:SetValue({script.AddFunction("contactzone", "e,c", ["return 0,0x1f"])})"]);
        lines.AddRange(["cp:SetRange(LOCATION_EXTRA)", $"cp:SetCondition({con})", $"cp:SetTarget({tg})", $"cp:SetOperation({op})", "c:RegisterEffect(cp)"]);
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
        var proc = project.Procedure;
        var how = How(proc);
        var card = project.Card;
        // "..., but if you Special Summon this card with the second method, you can only Special Summon once for the rest of this turn."
        var but = proc.ContactOneMoreSummon ? ", but if you Special Summon this card with the second method, you can only Special Summon once for the rest of this turn" : "";
        // Lava Golem, Steelcage of the Sun God: "Special Summoned (from your Extra Deck) to your opponent's Main Monster Zone by ...".
        var to = proc.ContactToOpponent ? " to your opponent's Main Monster Zone" : "";
        if (card.CannotBeSpecialSummoned) return $"Must be Special Summoned (from your Extra Deck){to} by {how}{but}.";
        return card.SummonRule switch
        {
            SummonRule.ProcedureOnly when to.Length > 0 || but.Length > 0 => $"Must be either Fusion Summoned, or Special Summoned (from your Extra Deck){to} by {how}{but}.",
            SummonRule.ProcedureOnly => $"Must be either Fusion Summoned, or Special Summoned by {how} (in which case you do not use \"Polymerization\").",
            SummonRule.SpecialSummonOnly => $"Must first be Special Summoned (from your Extra Deck){to} by {how}{but}. (You do not use \"Polymerization\".)",
            _ => $"Must first be either Fusion Summoned, or Special Summoned from your Extra Deck{to} by {how}{but}.",
        };
    }
}
