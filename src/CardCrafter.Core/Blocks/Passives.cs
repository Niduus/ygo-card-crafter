using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

public abstract class PassiveBlock : BlockDefinition
{
    public override BlockKind Kind => BlockKind.Passive;

    /// <summary>Lines for a lasting effect that applies to this card only.</summary>
    protected static IEnumerable<string> SelfEffect(string v, string range, string code, string value) =>
    [
        $"{v}:SetType(EFFECT_TYPE_SINGLE)",
        $"{v}:SetProperty(EFFECT_FLAG_SINGLE_RANGE)",
        $"{v}:SetRange({range})",
        $"{v}:SetCode({code})",
        $"{v}:SetValue({value})",
    ];

    protected static string RangeOf(BlockContext ctx) => ctx.Effect.Range ?? "LOCATION_MZONE";
}

public sealed class BoostCardsPassive : PassiveBlock
{
    public override string Id => "passive.boost_cards";
    public override string Group => "Monsters";
    public override string Name => "Monsters gain or lose ATK/DEF";
    public override string Sentence => "{cards} {dir} {amount} {stat}";
    public override string Help => "For example: Dragon monsters you control gain 500 ATK.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone),
        ParamSpec.Choice("dir", "Gain/lose", new("gain", "gain"), new("lose", "lose")),
        ParamSpec.Int("amount", "Amount", 500, 1, 99999),
        ParamSpec.Choice("stat", "Stat", new("atk", "ATK"), new("def", "DEF"), new("both", "ATK/DEF")),
    ];

    public override string Describe(BlockArgs args)
    {
        var cards = args.Cards("cards");
        var noun = Text.FilterText.Describe(cards, args.Pack, plural: true, preposition: "");
        return $"{noun} {args.Describe("dir")} {args.Int("amount")} {args.Describe("stat")}";
    }

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards").Clone();
        filter.Zones = Zone.MonsterZone;
        var f = ctx.Script.Filter(filter);
        var target = ctx.Script.AddFunction("boosttg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")]);
        var amount = ctx.Args.Int("amount") * (ctx.Args.Choice("dir") == "lose" ? -1 : 1);
        var codes = ctx.Args.Choice("stat") switch
        {
            "def" => new[] { "EFFECT_UPDATE_DEFENSE" },
            "both" => ["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"],
            _ => ["EFFECT_UPDATE_ATTACK"],
        };
        var range = RangeOf(ctx);
        foreach (var code in codes)
        {
            ctx.Effect.AddPassive(v =>
            [
                $"{v}:SetType(EFFECT_TYPE_FIELD)",
                $"{v}:SetCode({code})",
                $"{v}:SetRange({range})",
                $"{v}:SetTargetRange({f.Self},{f.Opp})",
                $"{v}:SetTarget({target})",
                $"{v}:SetValue({amount})",
            ]);
        }
    }
}

public sealed class SelfStatsPassive : PassiveBlock
{
    public override string Id => "passive.self_stats";
    public override string Group => "This card";
    public override string Name => "This card gains or loses ATK/DEF";
    public override string Sentence => "This card {dir} {amount} {stat}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("dir", "Gains/loses", new("gain", "gains"), new("lose", "loses")),
        ParamSpec.Int("amount", "Amount", 500, 1, 99999),
        ParamSpec.Choice("stat", "Stat", new("atk", "ATK"), new("def", "DEF"), new("both", "ATK/DEF")),
    ];

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var amount = (ctx.Args.Int("amount") * (ctx.Args.Choice("dir") == "lose" ? -1 : 1)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var codes = ctx.Args.Choice("stat") switch
        {
            "def" => new[] { "EFFECT_UPDATE_DEFENSE" },
            "both" => ["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"],
            _ => ["EFFECT_UPDATE_ATTACK"],
        };
        var range = RangeOf(ctx);
        foreach (var code in codes)
            ctx.Effect.AddPassive(v => SelfEffect(v, range, code, amount));
    }
}

public sealed class IndestructiblePassive : PassiveBlock
{
    public override string Id => "passive.indestructible";
    public override string Group => "This card";
    public override string Name => "This card can't be destroyed";
    public override string Sentence => "This card cannot be destroyed {by}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("by", "By",
            new("battle", "by battle"),
            new("effect", "by card effects"),
            new("both", "by battle or card effects"),
            new("opponent_effect", "by your opponent's card effects")),
    ];

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        var by = ctx.Args.Choice("by");
        if (by is "battle" or "both")
        {
            Zones.RequireMonster(ctx, "Cannot be destroyed by battle");
            ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_INDESTRUCTABLE_BATTLE", "1"));
        }
        if (by is "effect" or "both")
            ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_INDESTRUCTABLE_EFFECT", "1"));
        if (by == "opponent_effect")
            ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_INDESTRUCTABLE_EFFECT", "aux.indoval"));
    }
}

public sealed class UntargetablePassive : PassiveBlock
{
    public override string Id => "passive.untargetable";
    public override string Group => "This card";
    public override string Name => "Your opponent can't target this card";
    public override string Sentence => "Your opponent cannot target this card with card effects";

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        ctx.Effect.AddPassive(v => SelfEffect(v, range, "EFFECT_CANNOT_BE_EFFECT_TARGET", "aux.tgoval"));
    }
}

public sealed class BattleAbilityPassive : PassiveBlock
{
    public override string Id => "passive.battle_ability";
    public override string Group => "This card";
    public override string Name => "Battle ability (direct attack, piercing, extra attack)";
    public override string Sentence => "This card {ability}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("ability", "Ability",
            new("direct", "can attack directly"),
            new("pierce", "inflicts piercing battle damage"),
            new("extra", "can make a second attack during each Battle Phase")),
    ];

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var range = RangeOf(ctx);
        var (code, value) = ctx.Args.Choice("ability") switch
        {
            "pierce" => ("EFFECT_PIERCE", "1"),
            "extra" => ("EFFECT_EXTRA_ATTACK", "1"),
            _ => ("EFFECT_DIRECT_ATTACK", "1"),
        };
        ctx.Effect.AddPassive(v => SelfEffect(v, range, code, value));
    }
}
