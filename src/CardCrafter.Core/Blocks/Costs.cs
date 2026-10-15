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
            new("discard", "Discard")),
    ];

    /// <summary>"banish this card from your GY", "send this card from your hand to the GY", from the WHEN block's zone.</summary>
    public override string Describe(BlockArgs args)
    {
        var from = args.TriggerZone is { } z ? Zones.From(z) : null;
        return args.Choice("how") switch
        {
            "grave" => from is null || args.TriggerZone == "graveyard" ? "send this card to the GY" : $"send this card {from} to the GY",
            "banish" => from is null ? "banish this card" : $"banish this card {from}",
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
            _ => ("e:GetHandler():IsReleasable()", "Duel.Release(e:GetHandler(),REASON_COST)"),
        };
        ctx.Effect.CostChecks.Add(check);
        ctx.Effect.CostLines.Add(line);
    }
}

public sealed class TributeCost : CostBlock
{
    public override string Id => "cost.tribute";
    public override string Group => "Cards";
    public override string Name => "Tribute monsters";
    public override string Sentence => "Tribute {count} {cards}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 1, 1, 5),
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone),
    ];

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards").Clone();
        filter.Owner = CardOwner.You;
        filter.Zones = Zone.MonsterZone;
        var f = ctx.Script.Filter(filter, null, "tributefilter");
        var n = ctx.Args.Int("count");
        var g = ctx.Effect.NewLocal("g");
        ctx.Effect.CostChecks.Add($"Duel.CheckReleaseGroup(tp,{f.Function},{n},{f.Exclude},e,tp)");
        ctx.Effect.CostLines.Add($"local {g}=Duel.SelectReleaseGroup(tp,{f.Function},{n},{n},{f.Exclude},e,tp)");
        ctx.Effect.CostLines.Add($"Duel.Release({g},REASON_COST)");
    }
}

/// <summary>"Banish 1 card from your GY" or "send 1 card from your hand to the GY" as a cost.</summary>
public sealed class MoveCardsCost : CostBlock
{
    public override string Id => "cost.move_cards";
    public override string Group => "Cards";
    public override string Name => "Banish or send cards to the GY";
    public override string Sentence => "{how} {count} {cards}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How", new("banish", "Banish"), new("grave", "Send to the GY")),
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard }),
    ];

    public override string Describe(BlockArgs args) => args.Choice("how") == "grave"
        ? $"send {args.Int("count")} {args.Describe("cards")} to the GY"
        : $"banish {args.Int("count")} {args.Describe("cards")}";

    public override void Compile(BlockContext ctx)
    {
        var banish = ctx.Args.Choice("how") == "banish";
        var filter = ctx.Args.Cards("cards").Clone();
        if (!banish && filter.Zones.HasFlag(Zone.Graveyard))
        {
            ctx.Effect.Error("\"Send to the GY\" can't use cards that are already in the GY. Pick the hand, Deck or field.");
            return;
        }
        filter.Owner = CardOwner.You;
        var f = ctx.Script.Filter(filter, banish ? "c:IsAbleToRemoveAsCost()" : "c:IsAbleToGraveAsCost()", banish ? "rmcostfilter" : "gycostfilter");
        var n = ctx.Args.Int("count");
        var g = ctx.Effect.NewLocal("g");
        // The activated card itself can never pay its own cost this way.
        ctx.Effect.CostChecks.Add($"Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},0,{n},e:GetHandler(),e,tp)");
        ctx.Effect.CostLines.Add($"Duel.Hint(HINT_SELECTMSG,tp,{(banish ? "HINTMSG_REMOVE" : "HINTMSG_TOGRAVE")})");
        ctx.Effect.CostLines.Add($"local {g}=Duel.SelectMatchingCard(tp,{f.Function},tp,{f.Self},0,{n},{n},e:GetHandler(),e,tp)");
        ctx.Effect.CostLines.Add(banish ? $"Duel.Remove({g},POS_FACEUP,REASON_COST)" : $"Duel.SendtoGrave({g},REASON_COST)");
    }
}
