using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// What happened to each player's LP this turn: damage (battle or effect), LP paid, LP lost and LP gained. A watcher
/// registered once per card adds every amount up for the turn; conditions read it back.
/// </summary>
internal static class LifeWatch
{
    /// <summary>Lua that reads player <paramref name="p"/>'s total of <paramref name="kind"/> this turn.</summary>
    public static string Total(BlockContext ctx, string p, string kind)
    {
        var add = ctx.Script.AddFunction("lpadd", "p,kind,v",
            ["local k=Duel.GetTurnCount()..\"_\"..p..\"_\"..kind", "s.cc_lp[k]=(s.cc_lp[k] or 0)+v"]);
        var get = ctx.Script.AddFunction("lpget", "p,kind", ["return s.cc_lp and s.cc_lp[Duel.GetTurnCount()..\"_\"..p..\"_\"..kind] or 0"]);
        var damage = ctx.Script.AddFunction("lpdamage", "e,tp,eg,ep,ev,re,r,rp",
        [
            $"{add}(ep,\"damage\",ev)",
            $"{add}(ep,\"lost\",ev)",
            $"if r&REASON_BATTLE~=0 then {add}(ep,\"battle\",ev) else {add}(ep,\"effect\",ev) end",
            $"if r&REASON_EFFECT~=0 and rp==1-ep then {add}(ep,\"opp_effect\",ev) end",
        ]);
        var pay = ctx.Script.AddFunction("lppay", "e,tp,eg,ep,ev,re,r,rp", [$"{add}(ep,\"paid\",ev)", $"{add}(ep,\"lost\",ev)"]);
        var recover = ctx.Script.AddFunction("lprecover", "e,tp,eg,ep,ev,re,r,rp", [$"{add}(ep,\"gained\",ev)"]);
        // Every amount of the Duel is counted once, by the first copy of this card.
        ctx.Script.AddInitialLine(string.Join("\n\t",
        [
            "if not s.cc_lp then",
            "\ts.cc_lp={}",
            .. new[] { ("EVENT_DAMAGE", damage), ("EVENT_PAY_LPCOST", pay), ("EVENT_RECOVER", recover) }.SelectMany((w, i) => new[]
            {
                $"\tlocal lw{i}=Effect.CreateEffect(c)",
                $"\tlw{i}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
                $"\tlw{i}:SetCode({w.Item1})",
                $"\tlw{i}:SetOperation({w.Item2})",
                $"\tDuel.RegisterEffect(lw{i},0)",
            }),
            "end",
        ]));
        return $"{get}({p},\"{kind}\")";
    }
}

/// <summary>
/// "If you took damage this turn", "if your opponent has not taken battle damage this turn", "if you paid 2000 or more LP
/// this turn", "if a player gained LP this turn" and the rest.
/// </summary>
public sealed class LifeThisTurnCondition : ConditionBlock
{
    public const string BlockId = "if.lp_this_turn";
    public override string Id => BlockId;
    public override string Group => "Life Points";
    public override string Name => "Took damage / paid / lost / gained LP this turn";
    public override string Sentence => "{who} {did} {what} this turn (at least {amount})";
    public override string Help => "Counts from the start of the turn, whatever caused it. Leave the amount at 0 for any amount.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("you", "You"), new("opponent", "Your opponent"), new("either", "Either player"), new("both", "Both players")),
        ParamSpec.Choice("did", "Did it happen", new("did", "did"), new("not", "did not")),
        ParamSpec.Choice("what", "What",
            new("damage", "take damage"),
            new("battle", "take battle damage"),
            new("effect", "take effect damage"),
            new("opp_effect", "take damage from an opponent's card effect"),
            new("paid", "pay LP"),
            new("lost", "lose LP (damage or paying)"),
            new("gained", "gain LP")),
        ParamSpec.Int("amount", "At least (0 = any)", 0, 0, 999999),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("amount");
        var amt = n > 0 ? $"{n.ToString(CultureInfo.InvariantCulture)} or more " : "";
        var what = args.Choice("what");
        var (past, perfect) = what switch
        {
            "battle" => ($"took {amt}battle damage", $"taken {amt}battle damage"),
            "effect" => ($"took {amt}effect damage", $"taken {amt}effect damage"),
            "opp_effect" => ($"took {amt}damage from an opponent's card effect", $"taken {amt}damage from an opponent's card effect"),
            "paid" => ($"paid {amt}LP", $"paid {amt}LP"),
            "lost" => ($"lost {amt}LP", $"lost {amt}LP"),
            "gained" => ($"gained {amt}LP", $"gained {amt}LP"),
            _ => ($"took {amt}damage", $"taken {amt}damage"),
        };
        var not = args.Choice("did") == "not";
        return (args.Choice("who"), not) switch
        {
            ("opponent", false) => $"your opponent {past} this turn",
            ("opponent", true) => $"your opponent has not {perfect} this turn",
            ("either", false) => $"a player {past} this turn",
            ("either", true) => $"neither player has {perfect} this turn",
            ("both", false) => $"both players {past} this turn",
            ("both", true) => $"both players have not {perfect} this turn",
            (_, false) => $"you {past} this turn",
            _ => $"you have not {perfect} this turn",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var what = ctx.Args.Choice("what");
        var n = Math.Max(1, ctx.Args.Int("amount")).ToString(CultureInfo.InvariantCulture);
        string Has(string p) => $"{LifeWatch.Total(ctx, p, what)}>={n}";
        var test = ctx.Args.Choice("who") switch
        {
            "opponent" => Has("1-tp"),
            "either" => $"({Has("tp")} or {Has("1-tp")})",
            "both" => $"{Has("tp")} and {Has("1-tp")}",
            _ => Has("tp"),
        };
        ctx.Effect.Conditions.Add(ctx.Args.Choice("did") == "not" ? $"not ({test})" : test);
    }
}

/// <summary>"If the difference between the players' LP is 3000 or more".</summary>
public sealed class LifeDifferenceCondition : ConditionBlock
{
    public override string Id => "if.lp_difference";
    public override string Group => "Life Points";
    public override string Name => "The difference between both players' LP";
    public override string Sentence => "The difference between both players' LP is {cmp} {amount}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("cmp", "Is", CardCounts.Compare),
        ParamSpec.Int("amount", "Amount", 2000, 0, 999999),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("amount").ToString(CultureInfo.InvariantCulture);
        var value = args.Choice("cmp") switch { "exactly" => $"exactly {n}", "at_most" => $"{n} or less", _ => $"{n} or more" };
        return $"the difference between your LP and your opponent's LP is {value}";
    }

    public override void Compile(BlockContext ctx) =>
        ctx.Effect.Conditions.Add($"math.abs(Duel.GetLP(tp)-Duel.GetLP(1-tp)){CardCounts.Op(ctx.Args.Choice("cmp"))}{ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture)}");
}
