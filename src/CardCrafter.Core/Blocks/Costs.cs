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
    public override string Sentence => "Discard {count} {cards}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand }, Zone.Hand),
    ];

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards").Clone();
        filter.Owner = CardOwner.You;
        filter.Zones = Zone.Hand;
        var f = ctx.Script.Filter(filter, "c:IsDiscardable()", "discardfilter");
        var n = ctx.Args.Int("count");
        // The card itself never counts: it is the one being activated.
        ctx.Effect.CostChecks.Add($"Duel.IsExistingMatchingCard({f.Function},tp,LOCATION_HAND,0,{n},e:GetHandler(),e,tp)");
        ctx.Effect.CostLines.Add($"Duel.DiscardHand(tp,{f.Function},{n},{n},REASON_COST+REASON_DISCARD,e:GetHandler(),e,tp)");
    }
}

public sealed class PayLifePointsCost : CostBlock
{
    public override string Id => "cost.pay_lp";
    public override string Group => "Life Points";
    public override string Name => "Pay Life Points";
    public override string Sentence => "Pay {amount} LP";
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Int("amount", "Amount", 1000, 1, 99999)];

    public override void Compile(BlockContext ctx)
    {
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
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How",
            new("tribute", "Tribute"),
            new("banish", "Banish"),
            new("grave", "Send to the GY"),
            new("discard", "Discard"),
            new("deck", "Shuffle into the Deck:"),
            new("extra", "Return to the Extra Deck:"),
            new("hand", "Return to the hand:")),
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
            _ => $"{args.Describe("how").ToLowerInvariant()} this card",
        };
    }

    public override void Compile(BlockContext ctx)
    {
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
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone),
        ParamSpec.Choice("from", "From", new("field", "", "you control"), new("hand_or_field", "from your hand or field")),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var cards = Text.FilterText.Describe(args.Cards("cards"), args.Pack, n != 1, withLocation: args.Choice("from") != "hand_or_field");
        return args.Choice("from") == "hand_or_field" ? $"Tribute {n} {cards} from your hand or field" : $"Tribute {n} {cards}";
    }

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards").Clone();
        filter.Owner = CardOwner.You;
        filter.Zones = Zone.MonsterZone;
        var f = ctx.Script.Filter(filter, null, "tributefilter");
        var n = ctx.Args.Int("count");
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
    public override string Sentence => "{how} {count} {cards}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How",
            new("banish", "Banish"), new("grave", "Send to the GY:"), new("deck", "Shuffle into the Deck:"),
            new("deck_bottom", "Place on the bottom of the Deck:"), new("hand", "Return to the hand:"), new("extra", "Return to the Extra Deck:")),
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ParamSpec.Choice("mode", "Exactly / up to", new("exact", "", "exactly"), new("up_to", "up to")),
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard }),
    ];

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
        return $"{verb} {args.Describe("count")} {args.Describe("cards")}{tail}{args.ExceptText()}";
    }

    public override void Compile(BlockContext ctx)
    {
        var how = ctx.Args.Choice("how");
        var filter = ctx.Args.Cards("cards").Clone();
        var zone = how switch { "grave" => Zone.Graveyard, "deck" or "deck_bottom" => Zone.Deck, "hand" => Zone.Hand, "extra" => Zone.ExtraDeck, _ => Zone.Banished };
        if (filter.Zones.HasFlag(zone))
        {
            ctx.Effect.Error($"\"{Name}\" can't move cards to where they already are. Pick other places to take them from.");
            return;
        }
        filter.Owner = CardOwner.You;
        var (_, _, able, hint, call) = Way(how);
        var f = ctx.Script.Filter(filter, able, how + "costfilter");
        var n = ctx.Args.Int("count");
        var min = ctx.Args.Choice("mode") == "up_to" ? 1 : n;
        var g = ctx.Effect.NewLocal("g");
        // The activated card itself can never pay its own cost this way.
        ctx.Effect.CostChecks.Add($"Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},0,{min},e:GetHandler(),e,tp)");
        ctx.Effect.CostLines.Add($"Duel.Hint(HINT_SELECTMSG,tp,{hint})");
        ctx.Effect.CostLines.Add($"local {g}=Duel.SelectMatchingCard(tp,{f.Function},tp,{f.Self},0,{min},{n},e:GetHandler(),e,tp)");
        if ((filter.Zones & Zone.Field) != Zone.None) ctx.Effect.CostLines.Add($"Duel.HintSelection({g})");
        // Kept for "for each card used for the cost".
        ctx.Effect.CostLines.Add($"e:SetLabel({string.Format(System.Globalization.CultureInfo.InvariantCulture, call, g)})");
        ctx.Effect.CostCounts = true;
    }
}
