using CardCrafter.Core.Compiler;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

// ---------------------------------------------------------------- this card (or a card) leaves and comes back

internal static class BanishAndReturn
{
    /// <summary>
    /// Interdimensional Matter Forwarder and Speedroid Clear Wing Wonder: "banish it, then return it to the field". The card
    /// is banished for now (REASON_TEMPORARY, in the position it is in) and, if it really went to banishment, returned to
    /// the field at once. The call on <paramref name="g"/>, returning 1 when it came back.
    /// </summary>
    public static string Call(BlockContext ctx, string g)
    {
        if (!ctx.Args.Cards("cards").ThisCard && (ctx.Args.Int("count") != 1 || ctx.Args.CountMode != "exact"))
            ctx.Effect.Error("\"Banish it, then return it to the field\" is for 1 card: set how many to exactly 1.");
        return ctx.Script.AddFunction("banishreturn", "g,e,tp",
        [
            "local tc=g:GetFirst()",
            "if Duel.Remove(tc,0,REASON_EFFECT+REASON_TEMPORARY)==0 or tc:IsReason(REASON_REDIRECT) or not tc:IsLocation(LOCATION_REMOVED) then return 0 end",
            "Duel.BreakEffect()",
            "return Duel.ReturnToField(tc) and 1 or 0",
        ]) + $"({g},e,tp)";
    }
}

/// <summary>
/// "You can banish this card; Special Summon this banished card": Special Summoning this card from where the effect's own
/// cost put it (banished face-up, the GY or the hand). Written as Union Pilot's "return this card to your hand; ...
/// Special Summon this card from your hand": the activation checks the player can Special Summon this monster and that
/// a Monster Zone is free once the cost has moved it, the card is related to the effect again after the cost, and it must
/// still be there, face-up, when the effect resolves.
/// </summary>
internal static class SummonFromCost
{
    /// <summary>Where the effect's cost puts this card, as a location, or null when no cost moves it there.</summary>
    public static string? Destination(EffectEvent? effect)
    {
        if (effect is null) return null;
        var places = new List<string>();
        foreach (var b in effect.Cost)
        {
            if (BlockRegistry.Find(b.BlockId) is not { } def) continue;
            var args = new BlockArgs(def, b);
            var place = b.BlockId switch
            {
                "cost.this_card" => args.Choice("how") switch
                {
                    "banish" => "LOCATION_REMOVED",
                    "grave" or "discard" or "tribute" => "LOCATION_GRAVE",
                    "hand" => "LOCATION_HAND",
                    _ => "",
                },
                "cost.move_cards" when args.Cards("cards").ThisCard => args.Choice("how") switch
                {
                    "banish" => "LOCATION_REMOVED",
                    "grave" => "LOCATION_GRAVE",
                    "hand" => "LOCATION_HAND",
                    _ => "",
                },
                "cost.tribute" when args.Cards("cards").ThisCard => "LOCATION_GRAVE",
                _ => null,
            };
            if (place is not null) places.Add(place);
        }
        return places is [{ Length: > 0 } one] ? one : null;
    }

    /// <summary>"Special Summon this banished card", "Special Summon this card from your GY".</summary>
    public static string Text(BlockArgs args) => Destination(args.Effect) switch
    {
        "LOCATION_REMOVED" => "Special Summon this banished card",
        "LOCATION_GRAVE" => "Special Summon this card from your GY",
        "LOCATION_HAND" => "Special Summon this card from your hand",
        _ => "Special Summon this card",
    };

    public static void Compile(BlockContext ctx, string pos, string ignore)
    {
        var e = ctx.Effect;
        if (Destination(e.Source) is not { } place)
        {
            e.Error("\"Special Summon this card from where the cost put it\" needs one COST that banishes this card, sends it to the GY or returns it to the hand.");
            return;
        }
        if (ctx.Args.Choice("as") != "none") e.Error("\"Special Summon this card from where the cost put it\" is a plain Special Summon: set \"treated as\" to none.");
        if (ctx.Args.Bool("banish") && place == "LOCATION_REMOVED") e.Warn("This card is Special Summoned from banishment, so \"banish it when it leaves the field\" sends it back there.");
        var card = ctx.Card;
        var setcode = Lua.Hex(CdbEncoder.PackSetcodes(TreatedAsPassive.Setcodes(ctx.Script.Project)));
        var link = card.IsLink;
        // The cost moves this card before the effect resolves: whether it can be Summoned is the player's check on this
        // monster, and its own Monster Zone counts as free (it may be paying the cost from there).
        e.AddCategory("CATEGORY_SPECIAL_SUMMON");
        e.TargetChecks.Add("Duel.GetMZoneCount(tp,e:GetHandler())>0");
        e.TargetChecks.Add($"Duel.IsPlayerCanSpecialSummonMonster(tp,id,{setcode},{Lua.Hex(CdbEncoder.TypeOf(card))},{card.Atk},{(link ? 0 : card.Def)},{(link ? card.LinkRating : card.Level)},"
                           + $"{EnumNames.LuaConstant(card.Race)},{EnumNames.LuaConstant(card.Attribute)},{pos})");
        // Moved by the cost, it is a new card to the effect: related to it again, as Union Pilot does.
        e.TargetLines.Add("e:GetHandler():CreateEffectRelation(e)");
        e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,e:GetHandler(),1,0,0)");
        var there = place == "LOCATION_REMOVED" ? "c:IsLocation(LOCATION_REMOVED) and c:IsFaceup()" : $"c:IsLocation({place})";
        var r = e.NewLocal("r");
        var lines = new List<string>
        {
            $"local {r}=0",
            $"if c:IsRelateToEffect(e) and {there} and Duel.GetLocationCount(tp,LOCATION_MZONE)>0 then",
            $"\t{r}=Duel.SpecialSummon(c,SUMMON_VALUE_SELF,tp,tp,{ignore},{ignore},{pos})",
        };
        if (ctx.Args.Bool("banish"))
        {
            lines.Add($"\tif {r}>0 then");
            lines.AddRange(SpecialSummonSelfAction.BanishWhenLeaves("c", "rd").Select(l => "\t\t" + l));
            lines.Add("\tend");
        }
        lines.Add("end");
        e.AddStep(lines, r, ctx.Link);
    }
}
