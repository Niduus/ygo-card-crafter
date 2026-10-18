using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

public abstract class CostBlock : BlockDefinition
{
    public override BlockKind Kind => BlockKind.Cost;
}

public sealed class DiscardCost : CostBlock
{
    public override string Id => "cost.discard";
    public override string Group => "Cards";
    public override string Name => "Discard cards";
    public override string Sentence => "Discard {mode} {count} {cards}";
    public override string Help => "\"Any number\" discards as many as the player likes (at least 1); a DO block can then use how many: \"that many\" " +
                                   "(The Winged Dragon of Ra, Sun God of Darkness), or \"the number of cards used for the cost\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 1, 1, 10).ShowIf(b => b.Values.GetValueOrDefault("mode") != "any"),
        new ParamSpec
        {
            Key = "cards", Label = "Which cards", Type = ParamType.Cards, AllowThisCard = true, AllowedZones = Zone.Hand,
            DefaultFilter = () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand },
        },
        // The Winged Dragon of Ra, Sun God of Darkness: "discard any number of "Sun God" cards, then choose that many ...".
        ParamSpec.Choice("mode", "Exactly / up to / any number", new("exact", "", "exactly"), new("up_to", "up to"), new("any", "any number of")),
    ];

    public override string Describe(BlockArgs args)
    {
        if (args.Cards("cards").ThisCard) return "discard this card";
        if (args.Choice("mode") == "any")
            return "discard any number of " + Text.FilterText.Describe(args.Cards("cards"), args.Pack, plural: true, withLocation: false, ownName: args.CardName, withExcept: false) + args.ExceptText();
        return base.Describe(args);
    }

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards").Clone();
        if (filter.ThisCard)
        {
            ctx.Effect.CostChecks.Add("e:GetHandler():IsDiscardable()");
            ctx.Effect.CostLines.Add("Duel.SendtoGrave(e:GetHandler(),REASON_COST+REASON_DISCARD)");
            return;
        }
        filter.Owner = CardOwner.You;
        filter.Zones = Zone.Hand;
        var n = ctx.Args.Int("count");
        // Only a card a DO block can then be used with ("with the same Type as the discarded monster"), for 1 card.
        var pair = n == 1 && ctx.Args.Choice("mode") == "exact" ? CardRefs.PairCheck(ctx, CardRef.Cost) : null;
        var f = ctx.Script.Filter(filter, "c:IsDiscardable()" + (pair is null ? "" : " and " + pair), "discardfilter");
        // The card being activated doesn't count ("discard 1 card; Special Summon this card"), unless the cost names it:
        // "discard 1 "Sundering - Inferno"" on Sundering - Inferno can discard this very card.
        var namesItself = filter.CardCode == ctx.Card.Id || filter.AltCodes.Contains(ctx.Card.Id);
        var exclude = namesItself && !filter.ExcludeThisCard ? "nil" : "e:GetHandler()";
        var mode = ctx.Args.Choice("mode");
        if (mode == "exact")
        {
            ctx.Effect.CostChecks.Add($"Duel.IsExistingMatchingCard({f.Function},tp,LOCATION_HAND,0,{n},{exclude},e,tp)");
            ctx.Effect.CostLines.Add($"Duel.DiscardHand(tp,{f.Function},{n},{n},REASON_COST+REASON_DISCARD,{exclude},e,tp)");
            return;
        }
        // "Up to" or "any number": how many were discarded is kept, for "that many" and "for each card discarded". Any
        // number is capped by what a later step can use ("target that many"), as cost.detach is.
        if (mode == "any")
        {
            ctx.Effect.CostChecks.Add($"Duel.IsExistingMatchingCard({f.Function},tp,LOCATION_HAND,0,1,{exclude},e,tp)");
            ctx.Effect.CostLines.Add($"e:SetLabel(Duel.DiscardHand(tp,{f.Function},1,{EffectBuilder.CostCapToken},REASON_COST+REASON_DISCARD,{exclude},e,tp))");
            ctx.Effect.CostCapFallback = "99";
            ctx.Effect.CostCounts = true;
            return;
        }
        var max = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ctx.Effect.CostChecks.Add($"Duel.IsExistingMatchingCard({f.Function},tp,LOCATION_HAND,0,1,{exclude},e,tp)");
        ctx.Effect.CostLines.Add($"e:SetLabel(Duel.DiscardHand(tp,{f.Function},1,{max},REASON_COST+REASON_DISCARD,{exclude},e,tp))");
        ctx.Effect.CostCounts = true;
    }
}

public sealed class PayLifePointsCost : CostBlock
{
    public override string Id => "cost.pay_lp";
    public override string Group => "Life Points";
    public override string Name => "Pay Life Points";
    public override string Sentence => "Pay {how} {amount} {step} {max} LP";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How much", new("amount", "", "a number"), new("half", "half your", "half your LP"),
            new("multiple", "LP in multiples of", "any multiple of (the LP paid can be used as an amount)"),
            // The Winged Dragon of Ra, Sun God of Darkness: "pay LP so that you only have 1 left" (the LP paid can be used as an amount).
            new("to_one", "LP so that you only have 1 left", "so that you only have 1 left (the LP paid can be used as an amount)"),
            // Ultimate Sky: "Target ... and pay 800 LP for each target": paid as the targets are chosen, which the LP caps.
            new("per_target", "", "a number for each target (of the DO block that targets)")),
        ParamSpec.Int("amount", "Amount", 1000, 1, 99999).ShowIf(b => b.Values.GetValueOrDefault("how", "amount") is "amount" or "per_target"),
        ParamSpec.Choice("step", "Multiples of", new("100", "100"), new("500", "500"), new("1000", "1000")).ShowIf(Multiple),
        ParamSpec.Int("max", "At most (0: as much as you have)", 0, 0, 99999).ShowIf(Multiple),
    ];

    private static bool Multiple(BlockInstance b) => b.Values.GetValueOrDefault("how", "amount") == "multiple";

    public override string Describe(BlockArgs args) => args.Choice("how") switch
    {
        "half" => "pay half your LP",
        // Psychic Blade: "Pay LP in multiples of 100 (max. 2000)".
        "multiple" => $"pay LP in multiples of {args.Choice("step")}" + (args.Int("max") > 0 ? $" (max. {args.Int("max")})" : ""),
        "to_one" => "pay LP so that you only have 1 left",
        "per_target" => $"pay {args.Int("amount")} LP for each target",
        _ => $"pay {args.Int("amount")} LP",
    };

    /// <summary>
    /// Whether the effect pays an amount of LP it chooses (in multiples, or down to 1), which its blocks can use as "the LP
    /// paid" (kept in the effect's label).
    /// </summary>
    public static bool PaysMultiple(EffectEvent effect) =>
        effect.Cost.Any(b => b.BlockId == "cost.pay_lp" && b.Values.GetValueOrDefault("how") is "multiple" or "to_one");

    /// <summary>"Pay 800 LP for each target": the cost is said with the targets ("target ... and pay 800 LP for each target").</summary>
    public static bool PerTarget(BlockInstance b) => b.BlockId == "cost.pay_lp" && b.Values.GetValueOrDefault("how") == "per_target";

    public override void Compile(BlockContext ctx)
    {
        // "Pay 800 LP for each target": the DO block that targets pays it as its targets are chosen (CardGroupAction).
        if (ctx.Args.Choice("how") == "per_target")
        {
            var each = ctx.Args.Int("amount").ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ctx.Effect.LpPerTarget is not null) ctx.Effect.Error("Only one \"pay LP for each target\" can go with a DO block that targets.");
            ctx.Effect.CostChecks.Add($"Duel.CheckLPCost(tp,{each})");
            ctx.Effect.LpPerTarget = each;
            return;
        }
        // "Pay LP so that you only have 1 left": all but 1, kept for "the LP paid" (as Cost.PayLP(1,true) keeps it).
        if (ctx.Args.Choice("how") == "to_one")
        {
            if (ctx.Effect.Source.Cost.Any(b => b.BlockId == "cost.move_cards") || ctx.Effect.Source.Cost.Count(b => b.BlockId == Id && b.Values.GetValueOrDefault("how") is "multiple" or "to_one") > 1)
            {
                ctx.Effect.Error("Only one COST block of an effect can keep its amount: paying LP so that you have 1 left can't go with another cost that counts its cards, or a second one.");
                return;
            }
            ctx.Effect.CostChecks.Add("Duel.GetLP(tp)>1 and Duel.CheckLPCost(tp,Duel.GetLP(tp)-1)");
            ctx.Effect.CostLines.AddRange(["local lpc=Duel.GetLP(tp)-1", "Duel.PayLPCost(tp,lpc)", "e:SetLabel(lpc)"]);
            return;
        }
        // "Pay half your LP": rounded down, as the game does.
        if (ctx.Args.Choice("how") == "half")
        {
            ctx.Effect.CostLines.Add("Duel.PayLPCost(tp,math.floor(Duel.GetLP(tp)/2))");
            return;
        }
        // "Pay LP in multiples of 100 (max. 2000)": the player declares the amount; it is kept in the effect's label, as
        // official cards keep it ("Duel.PayLPCost(tp,ac,true) e:SetLabel(ac)").
        if (ctx.Args.Choice("how") == "multiple")
        {
            if (ctx.Effect.Source.Cost.Any(b => b.BlockId == "cost.move_cards") || ctx.Effect.Source.Cost.Count(b => b.BlockId == Id && b.Values.GetValueOrDefault("how") is "multiple" or "to_one") > 1)
            {
                ctx.Effect.Error("Only one COST block of an effect can keep its amount: paying LP in multiples can't go with another cost that counts its cards, or a second one.");
                return;
            }
            var step = ctx.Args.Choice("step");
            var max = ctx.Args.Int("max");
            if (max > 0 && max < int.Parse(step, System.Globalization.CultureInfo.InvariantCulture))
                ctx.Effect.Error($"\"Pay LP in multiples of {step}\": the most that can be paid ({max}) is less than {step}.");
            ctx.Effect.CostChecks.Add($"Duel.CheckLPCost(tp,{step},true)");
            ctx.Effect.CostLines.AddRange(
            [
                $"local lpm=math.floor({(max > 0 ? $"math.min(Duel.GetLP(tp),{max})" : "Duel.GetLP(tp)")}/{step})",
                "local lpt={}",
                $"for i=1,lpm do lpt[i]=i*{step} end",
                "local lpc=Duel.AnnounceNumber(tp,table.unpack(lpt))",
                "Duel.PayLPCost(tp,lpc,true)",
                "e:SetLabel(lpc)",
            ]);
            return;
        }
        var n = ctx.Args.Int("amount");
        ctx.Effect.CostChecks.Add($"Duel.CheckLPCost(tp,{n})");
        ctx.Effect.CostLines.Add($"Duel.PayLPCost(tp,{n})");
    }
}

public sealed class SelfCost : CostBlock
{
    public override string Id => "cost.this_card";
    public override string Group => "This card";
    public override string Name => "Tribute, banish, send or discard this card";
    public override string Sentence => "{how} this card";

    public override IReadOnlyList<MenuShortcut> Shortcuts { get; } =
    [
        new("Discard this card", new Dictionary<string, string> { ["how"] = "discard" }),
        new("Tribute this card", new Dictionary<string, string> { ["how"] = "tribute" }),
        new("Banish this card", new Dictionary<string, string> { ["how"] = "banish" }),
        new("Send this card to the GY", new Dictionary<string, string> { ["how"] = "grave" }),
        new("Shuffle this card into the Deck", new Dictionary<string, string> { ["how"] = "deck" }),
        new("Return this card to the hand", new Dictionary<string, string> { ["how"] = "hand" }),
        new("Return this card to the Extra Deck", new Dictionary<string, string> { ["how"] = "extra" }),
    ];
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How",
            new("tribute", "Tribute"),
            new("banish", "Banish"),
            new("grave", "Send to the GY"),
            new("discard", "Discard"),
            new("deck", "Shuffle into the Deck:"),
            new("extra", "Return to the Extra Deck:"),
            new("hand", "Return to the hand:"),
            // W:P Fancy Ball: "You can banish this card that was Special Summoned this turn until the End Phase".
            new("banish_until_end", "Banish until the End Phase:", "Banish until the End Phase (it comes back)")),
    ];

    /// <summary>"banish this card from your GY", "send this card from your hand to the GY", from the WHEN block's zone.</summary>
    public override string Describe(BlockArgs args)
    {
        var from = args.TriggerZone is { } z ? Zones.From(z) : null;
        return args.Choice("how") switch
        {
            "grave" => from is null || args.TriggerZone == "graveyard" ? "send this card to the GY" : $"send this card {from} to the GY",
            "banish" => from is null ? "banish this card" : $"banish this card {from}",
            "deck" => "shuffle this card into the Deck",
            "extra" => "return this card to the Extra Deck",
            "hand" => "return this card to the hand",
            "banish_until_end" => "banish this card until the End Phase",
            // "Tribute" is a card term: it stays capitalised ("You can Tribute this card;").
            "tribute" => "Tribute this card",
            _ => $"{args.Describe("how").ToLowerInvariant()} this card",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Args.Choice("how") == "banish_until_end")
        {
            UntilEndPhase(ctx);
            return;
        }
        var (check, line) = ctx.Args.Choice("how") switch
        {
            "banish" => ("e:GetHandler():IsAbleToRemoveAsCost()", "Duel.Remove(e:GetHandler(),POS_FACEUP,REASON_COST)"),
            "grave" => ("e:GetHandler():IsAbleToGraveAsCost()", "Duel.SendtoGrave(e:GetHandler(),REASON_COST)"),
            "discard" => ("e:GetHandler():IsDiscardable()", "Duel.SendtoGrave(e:GetHandler(),REASON_COST+REASON_DISCARD)"),
            "deck" => ("e:GetHandler():IsAbleToDeckAsCost()", "Duel.SendtoDeck(e:GetHandler(),nil,SEQ_DECKSHUFFLE,REASON_COST)"),
            "extra" => ("e:GetHandler():IsAbleToExtraAsCost()", "Duel.SendtoDeck(e:GetHandler(),nil,SEQ_DECKTOP,REASON_COST)"),
            "hand" => ("e:GetHandler():IsAbleToHandAsCost()", "Duel.SendtoHand(e:GetHandler(),nil,REASON_COST)"),
            _ => ("e:GetHandler():IsReleasable()", "Duel.Release(e:GetHandler(),REASON_COST)"),
        };
        if (ctx.Args.Choice("how") == "extra" && !ctx.Card.IsExtraDeck)
            ctx.Effect.Warn("Only Fusion, Synchro, Xyz and Link Monsters can return to the Extra Deck, so this cost can never be paid.");
        ctx.Effect.CostChecks.Add(check);
        ctx.Effect.CostLines.Add(line);
    }

    /// <summary>
    /// "Banish this card until the End Phase": banished for now (REASON_TEMPORARY), and back on the field during this
    /// turn's End Phase, if it is still the card that was banished, as W:P Fancy Ball's cost does it.
    /// </summary>
    private static void UntilEndPhase(BlockContext ctx)
    {
        if (!ctx.Card.IsMonster) ctx.Effect.Error("\"Banish this card until the End Phase\" brings a monster back to the field; it is for monsters.");
        var flag = $"id+{8520 + ctx.Script.Slot("banishuntilend")}";
        var con = ctx.Script.AddFunction("endretcon", "e,tp,eg,ep,ev,re,r,rp", [$"return e:GetLabelObject():GetFlagEffect({flag})~=0"]);
        var op = ctx.Script.AddFunction("endretop", "e,tp,eg,ep,ev,re,r,rp", ["Duel.ReturnToField(e:GetLabelObject())"]);
        ctx.Effect.CostChecks.Add("e:GetHandler():IsAbleToRemoveAsCost()");
        ctx.Effect.CostLines.AddRange(
        [
            "if Duel.Remove(e:GetHandler(),0,REASON_COST+REASON_TEMPORARY)~=0 and e:GetHandler():IsLocation(LOCATION_REMOVED) then",
            $"\te:GetHandler():RegisterFlagEffect({flag},RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,0,1)",
            "\tlocal re1=Effect.CreateEffect(e:GetHandler())",
            "\tre1:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "\tre1:SetCode(EVENT_PHASE+PHASE_END)",
            "\tre1:SetReset(RESET_PHASE+PHASE_END)",
            "\tre1:SetLabelObject(e:GetHandler())",
            "\tre1:SetCountLimit(1)",
            $"\tre1:SetCondition({con})",
            $"\tre1:SetOperation({op})",
            "\tDuel.RegisterEffect(re1,tp)",
            "end",
        ]);
    }
}

public sealed class TributeCost : CostBlock
{
    public override string Id => "cost.tribute";
    public override string Group => "Cards";
    public override string Name => "Tribute monsters";
    public override string Sentence => "Tribute {count} {cards} {from}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 1, 1, 5),
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone, thisCard: true),
        ParamSpec.Choice("from", "From", new("field", "", "you control"), new("hand_or_field", "from your hand or field")),
    ];

    public override string Describe(BlockArgs args)
    {
        if (args.Cards("cards").ThisCard) return "Tribute this card";
        var n = args.Int("count");
        var cards = Text.FilterText.Describe(args.Cards("cards"), args.Pack, n != 1, withLocation: args.Choice("from") != "hand_or_field");
        return args.Choice("from") == "hand_or_field" ? $"Tribute {n} {cards} from your hand or field" : $"Tribute {n} {cards}";
    }

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards").Clone();
        if (filter.ThisCard)
        {
            ctx.Effect.CostChecks.Add("e:GetHandler():IsReleasable()");
            ctx.Effect.CostLines.Add("Duel.Release(e:GetHandler(),REASON_COST)");
            return;
        }
        filter.Owner = CardOwner.You;
        filter.Zones = Zone.MonsterZone;
        var n = ctx.Args.Int("count");
        // The Book of the Law: only a monster a DO block can then be used with ("with a different original Attribute"), for 1 monster.
        var f = ctx.Script.Filter(filter, n == 1 ? CardRefs.PairCheck(ctx, CardRef.Cost) : null, "tributefilter");
        var g = ctx.Effect.NewLocal("g");
        if (ctx.Args.Choice("from") == "hand_or_field")
        {
            // Mitsurugi: Tributing from the hand as well as the field.
            ctx.Effect.CostChecks.Add($"Duel.CheckReleaseGroupEx(tp,{f.Function},{n},REASON_COST,true,{f.Exclude},e,tp)");
            ctx.Effect.CostLines.Add($"local {g}=Duel.SelectReleaseGroupEx(tp,{f.Function},{n},{n},REASON_COST,true,{f.Exclude},e,tp)");
        }
        else
        {
            ctx.Effect.CostChecks.Add($"Duel.CheckReleaseGroup(tp,{f.Function},{n},{f.Exclude},e,tp)");
            ctx.Effect.CostLines.Add($"local {g}=Duel.SelectReleaseGroup(tp,{f.Function},{n},{n},{f.Exclude},e,tp)");
        }
        ctx.Effect.CostLines.Add($"Duel.Release({g},REASON_COST)");
    }
}

/// <summary>
/// "Banish 1 card from your GY", "send 2 cards from your hand to the GY", "shuffle up to 3 cards from your GY into the
/// Deck" as a cost. How many it used is kept, for DO blocks that count "for each card used for the cost".
/// </summary>
public sealed class MoveCardsCost : CostBlock
{
    public override string Id => "cost.move_cards";
    public override string Group => "Cards";
    public override string Name => "Banish, send or return cards (cost)";
    public override string Sentence => "{how} {count} {cards} {same_type} {no_repeat}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How",
            new("banish", "Banish"), new("grave", "Send to the GY:"), new("deck", "Shuffle into the Deck:"),
            new("deck_bottom", "Place on the bottom of the Deck:"), new("hand", "Return to the hand:"), new("extra", "Return to the Extra Deck:")),
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ParamSpec.Choice("mode", "Exactly / up to", new("exact", "", "exactly"), new("up_to", "up to")),
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard }, thisCard: true),
        // Invoked Mechaba: "send the same type of card (Monster, Spell, or Trap) from your hand to the GY".
        ParamSpec.Flag("same_type", "of the same card type (Monster, Spell, or Trap) as the card or effect activated", false),
        // Marincess Truth Heart: "also for the rest of this turn, you cannot banish monsters with that same Link Rating to activate this effect".
        ParamSpec.Choice("no_repeat", "Again this turn",
            new("none", "", "(any cards, each time)"),
            new("link", "Link Rating", "not with the same Link Rating as one used for it this turn"),
            new("level", "Level", "not with the same Level as one used for it this turn"),
            new("name", "name", "not with the same name as one used for it this turn")),
    ];

    /// <summary>"For the rest of this turn, you cannot banish monsters with that same Link Rating to activate this effect of "X"."</summary>
    public override string Afterword(BlockArgs args)
    {
        if (args.Choice("no_repeat") == "none" || args.Cards("cards").ThisCard) return "";
        var (verb, tail, _, _, _) = Way(args.Choice("how"));
        var what = args.Choice("no_repeat") == "name" ? "cards with that same name" : $"monsters with that same {args.Describe("no_repeat")}";
        var of = args.CardName is { Length: > 0 } n ? $" of \"{n}\"" : "";
        return $"For the rest of this turn, you cannot {verb} {what}{tail} to activate this effect{of}.";
    }

    /// <summary>The value a card used for the cost is remembered by, for "not with the same ... again this turn".</summary>
    private static string? RepeatValue(string kind) => kind switch { "link" => "c:GetLink()", "level" => "c:GetLevel()", "name" => "c:GetCode()", _ => null };

    private static (string Verb, string Tail, string Able, string Hint, string Call) Way(string how) => how switch
    {
        "grave" => ("send", " to the GY", "c:IsAbleToGraveAsCost()", "HINTMSG_TOGRAVE", "Duel.SendtoGrave({0},REASON_COST)"),
        "deck" => ("shuffle", " into the Deck", "c:IsAbleToDeckAsCost()", "HINTMSG_TODECK", "Duel.SendtoDeck({0},nil,SEQ_DECKSHUFFLE,REASON_COST)"),
        "deck_bottom" => ("place", " on the bottom of the Deck", "c:IsAbleToDeckAsCost()", "HINTMSG_TODECK", "Duel.SendtoDeck({0},nil,SEQ_DECKBOTTOM,REASON_COST)"),
        "hand" => ("return", " to the hand", "c:IsAbleToHandAsCost()", "HINTMSG_RTOHAND", "Duel.SendtoHand({0},nil,REASON_COST)"),
        "extra" => ("return", " to the Extra Deck", "c:IsAbleToExtraAsCost()", "HINTMSG_TODECK", "Duel.SendtoDeck({0},nil,SEQ_DECKSHUFFLE,REASON_COST)"),
        _ => ("banish", "", "c:IsAbleToRemoveAsCost()", "HINTMSG_REMOVE", "Duel.Remove({0},POS_FACEUP,REASON_COST)"),
    };

    public override string Describe(BlockArgs args)
    {
        var (verb, tail, _, _, _) = Way(args.Choice("how"));
        // "banish this card from your GY", "send this card from your hand to the GY".
        if (args.Cards("cards").ThisCard)
        {
            var from = args.TriggerZone is { } z && !(z == "graveyard" && args.Choice("how") == "grave") ? Zones.From(z) : null;
            return $"{verb} this card{(from is null ? "" : " " + from)}{tail}";
        }
        var cards = args.Describe("cards");
        if (args.Bool("same_type"))
        {
            // "the same type of card (Monster, Spell, or Trap) from your hand", for 1 card of any kind.
            if (args.Int("count") == 1 && args.Choice("mode") == "exact" && cards.StartsWith("card ", StringComparison.Ordinal))
                return $"{verb} the same type of card (Monster, Spell, or Trap) {cards[5..]}{tail}{args.ExceptText()}";
            cards += " of the same card type (Monster, Spell, or Trap) as the activated card";
        }
        return $"{verb} {args.Describe("count")} {cards}{tail}{args.ExceptText()}";
    }

    public override void Compile(BlockContext ctx)
    {
        var how = ctx.Args.Choice("how");
        var filter = ctx.Args.Cards("cards").Clone();
        if (filter.ThisCard)
        {
            var (_, _, selfAble, _, selfCall) = Way(how);
            if (how == "extra" && !ctx.Card.IsExtraDeck)
                ctx.Effect.Warn("Only Fusion, Synchro, Xyz and Link Monsters can return to the Extra Deck, so this cost can never be paid.");
            ctx.Effect.CostChecks.Add(selfAble.Replace("c:", "e:GetHandler():", StringComparison.Ordinal));
            ctx.Effect.CostLines.Add($"e:SetLabel({string.Format(System.Globalization.CultureInfo.InvariantCulture, selfCall, "e:GetHandler()")})");
            ctx.Effect.CostCounts = true;
            return;
        }
        var zone = how switch { "grave" => Zone.Graveyard, "deck" or "deck_bottom" => Zone.Deck, "hand" => Zone.Hand, "extra" => Zone.ExtraDeck, _ => Zone.Banished };
        if (filter.Zones.HasFlag(zone))
        {
            ctx.Effect.Error($"\"{Name}\" can't move cards to where they already are. Pick other places to take them from.");
            return;
        }
        filter.Owner = CardOwner.You;
        var (_, _, able, hint, call) = Way(how);
        var sameType = ctx.Args.Bool("same_type");
        if (sameType && ctx.Effect.Code != "EVENT_CHAINING")
            ctx.Effect.Error($"\"{Name}\": \"the same card type as the card or effect activated\" needs a WHEN that responds to an activation.");
        // The card type (Monster, Spell or Trap) of the chain link it responds to, passed to the filter.
        // "Not with the same Link Rating as one used for this effect this turn": the values used are kept by player and turn.
        var repeat = RepeatValue(ctx.Args.Choice("no_repeat"));
        var key = $"{ctx.Effect.Index}";
        if (repeat is not null) able += $" and not (s.ccused and s.ccused[tp] and s.ccused[tp][Duel.GetTurnCount()] and s.ccused[tp][Duel.GetTurnCount()][\"{key}:\"..{repeat}])";
        // Elfnotes: Welcome Home: only a card a DO block can then be used with ("with a different original Attribute"), for 1 card.
        if (ctx.Args.Int("count") == 1 && ctx.Args.Choice("mode") == "exact" && CardRefs.PairCheck(ctx, CardRef.Cost) is { } pair) able += " and " + pair;
        var f = ctx.Script.Filter(filter, able, how + "costfilter");
        var function = sameType
            ? ctx.Script.AddFunction(how + "typefilter", "c,e,tp,rtype", [$"return {string.Join(" and ", FilterCompiler.Conditions(filter).Append(able).Append("c:IsType(rtype)"))}"])
            : f.Function;
        var args = sameType ? "e,tp,bit.band(re:GetActiveType(),0x7)" : "e,tp";
        var n = ctx.Args.Int("count");
        var min = ctx.Args.Choice("mode") == "up_to" ? 1 : n;
        var g = ctx.Effect.NewLocal("g");
        // The activated card itself can never pay its own cost this way.
        ctx.Effect.CostChecks.Add($"Duel.IsExistingMatchingCard({function},tp,{f.Self},0,{min},e:GetHandler(),{args})");
        ctx.Effect.CostLines.Add($"Duel.Hint(HINT_SELECTMSG,tp,{hint})");
        ctx.Effect.CostLines.Add($"local {g}=Duel.SelectMatchingCard(tp,{function},tp,{f.Self},0,{min},{n},e:GetHandler(),{args})");
        if ((filter.Zones & Zone.Field) != Zone.None) ctx.Effect.CostLines.Add($"Duel.HintSelection({g})");
        if (repeat is not null)
            ctx.Effect.CostLines.AddRange(
            [
                "s.ccused=s.ccused or {}",
                "s.ccused[tp]=s.ccused[tp] or {}",
                "s.ccused[tp][Duel.GetTurnCount()]=s.ccused[tp][Duel.GetTurnCount()] or {}",
                $"for c in aux.Next({g}) do s.ccused[tp][Duel.GetTurnCount()][\"{key}:\"..{repeat}]=true end",
            ]);
        // Kept for "for each card used for the cost".
        ctx.Effect.CostLines.Add($"e:SetLabel({string.Format(System.Globalization.CultureInfo.InvariantCulture, call, g)})");
        ctx.Effect.CostCounts = true;
    }
}
