using System.Globalization;
using CardCrafter.Core.Compiler;

namespace CardCrafter.Core.Blocks;

/// <summary>"Roll a six-sided die and inflict damage to your opponent equal to the result x 500" and other uses of the roll.</summary>
public sealed class DiceAction : ActionBlock
{
    public const string BlockId = "do.dice";
    public override string Id => BlockId;
    public override string Group => "Chance";
    public override string Name => "Roll a die and use the result";
    public override string Sentence => "Roll a six-sided die, then {use} equal to the result x {amount}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("use", "Then",
            new("damage", "inflict damage to your opponent"),
            new("gain", "gain LP"),
            new("atk", "this card gains ATK"),
            new("level", "this card's Level becomes", "this card's Level becomes the result")),
        ParamSpec.Int("amount", "x", 500, 1, 99999).ShowIf(b => b.Values.GetValueOrDefault("use", "damage") != "level"),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("amount").ToString(CultureInfo.InvariantCulture);
        return args.Choice("use") switch
        {
            "gain" => $"roll a six-sided die and gain LP equal to the result x {n}",
            "atk" => $"roll a six-sided die, and this card gains ATK equal to the result x {n} until the end of this turn",
            "level" => "roll a six-sided die, and this card's Level becomes the result",
            _ => $"roll a six-sided die and inflict damage to your opponent equal to the result x {n}",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        e.AddCategory("CATEGORY_DICE");
        var use = ctx.Args.Choice("use");
        var n = ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture);
        if (use == "damage") { e.AddCategory("CATEGORY_DAMAGE"); e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_DAMAGE,nil,0,1-tp,0)"); }
        if (use == "gain") { e.AddCategory("CATEGORY_RECOVER"); e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_RECOVER,nil,0,tp,0)"); }
        if (use is "atk" or "level") Zones.RequireMonster(ctx, Name);
        e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_DICE,nil,0,tp,1)");
        var d = e.NewLocal("d");
        var r = e.NewLocal("r");
        var body = new List<string> { $"local {d}=Duel.TossDice(tp,1)", $"local {r}=1" };
        body.AddRange(use switch
        {
            "gain" => [$"Duel.Recover(tp,{d}*{n},REASON_EFFECT)"],
            "atk" =>
            [
                "if c:IsFaceup() and c:IsRelateToEffect(e) then",
                "\tlocal de=Effect.CreateEffect(c)", "\tde:SetType(EFFECT_TYPE_SINGLE)", "\tde:SetCode(EFFECT_UPDATE_ATTACK)",
                $"\tde:SetValue({d}*{n})", "\tde:SetReset(RESET_EVENT+RESETS_STANDARD+RESET_DISABLE+RESET_PHASE+PHASE_END)", "\tc:RegisterEffect(de)",
                "end",
            ],
            "level" =>
            [
                "if c:IsFaceup() and c:IsRelateToEffect(e) then",
                "\tlocal de=Effect.CreateEffect(c)", "\tde:SetType(EFFECT_TYPE_SINGLE)", "\tde:SetCode(EFFECT_CHANGE_LEVEL)",
                $"\tde:SetValue({d})", "\tde:SetReset(RESET_EVENT+RESETS_STANDARD+RESET_DISABLE)", "\tc:RegisterEffect(de)",
                "end",
            ],
            _ => [$"Duel.Damage(1-tp,{d}*{n},REASON_EFFECT)"],
        });
        e.AddStep(body, r, ctx.Link);
    }
}

/// <summary>"Declare a Level from 1 to 8; this card's Level becomes the declared Level" (and Rank, for Xyz Monsters' effects on others).</summary>
public sealed class DeclareLevelAction : ActionBlock
{
    public const string BlockId = "do.declare_level";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "Declare a Level; this card becomes that Level";
    public override string Sentence => "Declare a Level from {min} to {max}; this card becomes that Level {duration}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("min", "From", 1, 1, 12),
        ParamSpec.Int("max", "To", 8, 1, 12),
        ParamSpec.Choice("duration", "Until", Resets.Duration),
    ];

    public override string Describe(BlockArgs args)
    {
        var d = args.Describe("duration");
        return $"declare a Level from {args.Int("min")} to {args.Int("max")}, and this card becomes that Level{(d.Length > 0 ? " " + d : "")}";
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var (min, max) = (Math.Min(ctx.Args.Int("min"), ctx.Args.Int("max")), Math.Max(ctx.Args.Int("min"), ctx.Args.Int("max")));
        var levels = string.Join(",", Enumerable.Range(min, max - min + 1));
        var lv = ctx.Effect.NewLocal("lv");
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddStep(
        [
            $"local {r}=0",
            "if c:IsFaceup() and c:IsRelateToEffect(e) then",
            "\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_LVRANK)",
            $"\tlocal {lv}=Duel.AnnounceNumber(tp,{levels})",
            "\tlocal le=Effect.CreateEffect(c)", "\tle:SetType(EFFECT_TYPE_SINGLE)", "\tle:SetCode(EFFECT_CHANGE_LEVEL)",
            $"\tle:SetValue({lv})", $"\tle:SetReset({Resets.Of(ctx, "+RESET_DISABLE")})", "\tc:RegisterEffect(le)",
            $"\t{r}=1",
            "end",
        ], r, ctx.Link);
    }
}
