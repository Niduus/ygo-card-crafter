using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

/// <summary>"Your opponent's LP become half", "your LP become 4000", "halve both players' LP": LP set, not damage.</summary>
public sealed class SetLifeAction : ActionBlock
{
    public const string BlockId = "do.set_lp";
    public override string Id => BlockId;
    public override string Group => "Life Points";
    public override string Name => "LP become half / a number / the same as the other player's";
    public override string Sentence => "{who} LP become {how} {amount}";
    public override string Help => "Not damage: effects that stop or change damage don't apply, and it doesn't count as damage taken.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Whose", new("opponent", "Your opponent's"), new("you", "Your"), new("both", "Both players'")),
        ParamSpec.Choice("how", "Become",
            new("half", "half", "half (rounded up)"),
            new("value", "", "a number:"),
            new("other", "equal to the other player's", "the same as the other player's LP")),
        ParamSpec.Int("amount", "Number", 4000, 0, 999999).ShowIf(b => b.Values.GetValueOrDefault("how") == "value"),
    ];

    public override string Describe(BlockArgs args)
    {
        var whose = args.Choice("who") switch { "you" => "your LP", "both" => "both players' LP", _ => "your opponent's LP" };
        return args.Choice("how") switch
        {
            "value" => $"{whose} become {args.Int("amount").ToString(CultureInfo.InvariantCulture)}",
            "other" => args.Choice("who") switch
            {
                "you" => "your LP become equal to your opponent's",
                "both" => "both players' LP become equal to the lower of them",
                _ => "your opponent's LP become equal to yours",
            },
            _ => $"halve {whose}",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var players = ctx.Args.Choice("who") switch { "you" => new[] { "tp" }, "both" => ["tp", "1-tp"], _ => ["1-tp"] };
        var lines = new List<string>();
        switch (ctx.Args.Choice("how"))
        {
            case "value":
                lines.AddRange(players.Select(p => $"Duel.SetLP({p},{ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture)})"));
                break;
            case "other":
                lines.Add(ctx.Args.Choice("who") switch
                {
                    "you" => "Duel.SetLP(tp,Duel.GetLP(1-tp))",
                    "both" => "local low=math.min(Duel.GetLP(tp),Duel.GetLP(1-tp)) Duel.SetLP(tp,low) Duel.SetLP(1-tp,low)",
                    _ => "Duel.SetLP(1-tp,Duel.GetLP(tp))",
                });
                break;
            default:
                lines.AddRange(players.Select(p => $"Duel.SetLP({p},math.ceil(Duel.GetLP({p})/2))"));
                break;
        }
        ctx.Effect.AddStep(lines, null, ctx.Link);
    }
}

/// <summary>"The equipped monster gains ATK equal to the number of cards in your hand x 300": an Equip Card's live value.</summary>
public sealed class EquipValuePassive : PassiveBlock
{
    public const string BlockId = "passive.equip_value";
    public override string Id => BlockId;
    public override string Group => "Equip";
    public override string Name => "The equipped monster gains ATK/DEF/Level equal to a live value";
    public override string Sentence => "The equipped monster {dir} {stat} {how} {amount} {value} {of}";
    public override string Help => "For Equip Cards. \"Its\" values (its Level, its original ATK...) are the equipped monster's.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("dir", "Gains / loses / becomes", ValueSources.Dirs),
        ParamSpec.Choice("stat", "Stat", ValueSources.Stats),
        .. ValueSources.Params(withEvent: false),
    ];

    public override string Describe(BlockArgs args) => ValueSources.Sentence(args, "the equipped monster", false);

    public override void Compile(BlockContext ctx)
    {
        var lose = ctx.Args.Choice("dir") == "lose";
        var value = ctx.Script.AddFunction("equipval", "e,c", [$"return {(lose ? "-" : "")}({ValueSources.Lua(ctx, "c", "e:GetHandlerPlayer()", ev: false)})"]);
        foreach (var code in ValueSources.Codes(ctx.Args.Choice("stat"), ctx.Args.Choice("dir")))
            ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_EQUIP)", $"{v}:SetCode({code})", $"{v}:SetValue({value})"]);
    }
}
