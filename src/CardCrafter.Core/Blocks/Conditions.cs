using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

public abstract class ConditionBlock : BlockDefinition
{
    public override BlockKind Kind => BlockKind.Condition;
}

public sealed class CardsExistCondition : ConditionBlock
{
    public override string Id => "if.cards_exist";
    public override string Group => "Cards";
    public override string Name => "There are cards somewhere";
    public override string Sentence => "There are {count} or more {cards}";
    public override string Help => "For example: you control 2 or more Dragon monsters, or there are 3 or more Spells in your GY.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 1, 1, 60),
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true }),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var cards = args.Cards("cards");
        var noun = FilterText.Describe(cards, args.Pack, plural: n != 1, preposition: "in");
        return FilterText.IsFieldOnly(cards) && cards.Owner != CardOwner.Either
            ? $"{(cards.Owner == CardOwner.You ? "you control" : "your opponent controls")} {(n == 1 ? "a" : n + " or more")} {FilterText.Describe(cards, args.Pack, n != 1, "", withLocation: false)}"
            : $"there {(n == 1 ? "is" : "are")} {(n == 1 ? "a" : n + " or more")} {noun}";
    }

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Script.Filter(ctx.Args.Cards("cards"));
        ctx.Effect.Conditions.Add($"Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},{f.Opp},{ctx.Args.Int("count")},{f.Exclude},e,tp)");
    }
}

public sealed class NoCardsCondition : ConditionBlock
{
    public override string Id => "if.no_cards";
    public override string Group => "Cards";
    public override string Name => "There are no matching cards";
    public override string Sentence => "There are no {cards}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
    ];

    public override string Describe(BlockArgs args)
    {
        var cards = args.Cards("cards");
        return FilterText.IsFieldOnly(cards) && cards.Owner != CardOwner.Either
            ? $"{(cards.Owner == CardOwner.You ? "you control no" : "your opponent controls no")} {FilterText.Describe(cards, args.Pack, true, "", withLocation: false)}"
            : $"there are no {FilterText.Describe(cards, args.Pack, true, "in")}";
    }

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Script.Filter(ctx.Args.Cards("cards"));
        ctx.Effect.Conditions.Add($"not Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},{f.Opp},1,{f.Exclude},e,tp)");
    }
}

public sealed class SummonedThisTurnCondition : ConditionBlock
{
    public override string Id => "if.summoned_this_turn";
    public override string Group => "This card";
    public override string Name => "This card was Summoned this turn";
    public override string Sentence => "This card was {how} Summoned this turn";
    public override string Help => "Pair it with a Quick Effect for \"If this card was Summoned this turn (Quick Effect): ...\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How",
            new("any", "Normal, Flip or Special", "Normal, Flip or Special"),
            new("normal", "Normal"),
            new("special", "Special"),
            new("flip", "Flip")),
    ];

    public override string Describe(BlockArgs args) => args.Choice("how") == "any"
        ? "this card was Summoned this turn"
        : $"this card was {args.Describe("how")} Summoned this turn";

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        ctx.Effect.Conditions.Add(ctx.Args.Choice("how") switch
        {
            "normal" => "e:GetHandler():IsStatus(STATUS_SUMMON_TURN)",
            "special" => "e:GetHandler():IsStatus(STATUS_SPSUMMON_TURN)",
            "flip" => "e:GetHandler():IsStatus(STATUS_FLIP_SUMMON_TURN)",
            _ => "(e:GetHandler():IsStatus(STATUS_SUMMON_TURN) or e:GetHandler():IsStatus(STATUS_FLIP_SUMMON_TURN) or e:GetHandler():IsStatus(STATUS_SPSUMMON_TURN))",
        });
    }
}

public sealed class OpponentMoreMonstersCondition : ConditionBlock
{
    public override string Id => "if.opponent_more_monsters";
    public override string Group => "Cards";
    public override string Name => "Your opponent controls more monsters";
    public override string Sentence => "Your opponent controls more monsters than you do";

    public override string Describe(BlockArgs args) => "your opponent controls more monsters than you do";

    public override void Compile(BlockContext ctx) =>
        ctx.Effect.Conditions.Add("Duel.GetFieldGroupCount(tp,LOCATION_MZONE,0)<Duel.GetFieldGroupCount(tp,0,LOCATION_MZONE)");
}

public sealed class LifePointsCondition : ConditionBlock
{
    public override string Id => "if.life_points";
    public override string Group => "Life Points";
    public override string Name => "Life Points compared to a number";
    public override string Sentence => "{who} LP is {cmp} {amount}";

    public override string Describe(BlockArgs args) =>
        $"{(args.Choice("who") == "you" ? "your" : "your opponent's")} LP are {args.Int("amount")} or {(args.Choice("cmp") == "le" ? "less" : "more")}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Whose", new("you", "Your"), new("opponent", "Your opponent's")),
        ParamSpec.Choice("cmp", "Compare", new("le", "at most"), new("ge", "at least")),
        ParamSpec.Int("amount", "Amount", 2000, 0, 999999),
    ];

    public override void Compile(BlockContext ctx)
    {
        var p = ctx.Args.Choice("who") == "you" ? "tp" : "1-tp";
        var op = ctx.Args.Choice("cmp") == "le" ? "<=" : ">=";
        ctx.Effect.Conditions.Add($"Duel.GetLP({p}){op}{ctx.Args.Int("amount")}");
    }
}

public sealed class TurnCondition : ConditionBlock
{
    public override string Id => "if.turn";
    public override string Group => "Turn";
    public override string Name => "Whose turn it is";
    public override string Sentence => "It is {whose} turn";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("whose", "Whose", new("your", "your"), new("opponent", "your opponent's")),
    ];

    public override string Describe(BlockArgs args) => $"during {args.Describe("whose")} turn";

    public override void Compile(BlockContext ctx) =>
        ctx.Effect.Conditions.Add(ctx.Args.Choice("whose") == "your" ? "Duel.GetTurnPlayer()==tp" : "Duel.GetTurnPlayer()==1-tp");
}

public sealed class PhaseCondition : ConditionBlock
{
    public override string Id => "if.phase";
    public override string Group => "Turn";
    public override string Name => "It is a certain phase";
    public override string Sentence => "It is the {phase}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("phase", "Phase",
            new("main", "Main Phase"),
            new("battle", "Battle Phase"),
            new("standby", "Standby Phase"),
            new("end", "End Phase")),
    ];

    public override string Describe(BlockArgs args) => $"during the {args.Describe("phase")}";

    public override void Compile(BlockContext ctx) => ctx.Effect.Conditions.Add(ctx.Args.Choice("phase") switch
    {
        "battle" => "(Duel.GetCurrentPhase()>=PHASE_BATTLE_START and Duel.GetCurrentPhase()<=PHASE_BATTLE)",
        "standby" => "Duel.GetCurrentPhase()==PHASE_STANDBY",
        "end" => "Duel.GetCurrentPhase()==PHASE_END",
        _ => "(Duel.GetCurrentPhase()==PHASE_MAIN1 or Duel.GetCurrentPhase()==PHASE_MAIN2)",
    });
}
