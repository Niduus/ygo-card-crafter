using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "X" in "pay LP so that you only have X left" and "draw until you have X cards in your hand": a number set on the block,
/// a number the player declares as the effect is used, or a value from the Duel ("your opponent's LP", "the number of
/// cards in your opponent's hand"). Saved before X was offered: the number set on the block.
/// </summary>
public static class AmountX
{
    public const string Key = "x";
    public const string Number = "number";
    public const string Declare = "declare";
    public const string Value = "value";

    private const string ValueKey = "x_value";
    private const string CardsKey = "x_of";
    private const string HowKey = "x_how";
    private const string AmountKey = "x_amount";
    private const string Suffix = "x";

    /// <summary>The blanks of X, for the block's sentence.</summary>
    public const string SentenceKeys = "{x} {x_min} {x_max} {x_step} {x_how} {x_amount} {x_value} {memx} {counterx} {counter_idx} {x_of}";

    public static string Mode(BlockInstance b) => b.Values.GetValueOrDefault(Key, Number);

    public static string Mode(BlockArgs args) => args.Definition.Param(Key) is null ? Number : args.Choice(Key);

    /// <summary>
    /// The blanks: how X is decided, the range a declared X comes from, and the value X is equal to.
    /// <paramref name="shown"/> says when X applies; <paramref name="inCost"/> leaves out the values that only exist once
    /// the effect resolves (a target, the card the step before affected, a die roll).
    /// </summary>
    public static IEnumerable<ParamSpec> Params(Func<BlockInstance, bool> shown, bool inCost, int min, int max, int step)
    {
        bool Declared(BlockInstance b) => shown(b) && Mode(b) == Declare;
        bool Valued(BlockInstance b) => shown(b) && Mode(b) == Value;
        string Source(BlockInstance b) => b.Values.GetValueOrDefault(ValueKey, "opp_lp");
        var sources = inCost
            ? ValueSources.Sources.Where(o => o.Value == "event" || !ValueSources.AtResolution(o.Value)).ToArray()
            : ValueSources.Sources;
        return
        [
            ParamSpec.Choice(Key, "X is", new(Number, "", "a number set here"), new(Declare, "", "a number the player declares"),
                new(Value, "", "a value from the Duel (LP, cards, ATK...)")).ShowIf(shown),
            ParamSpec.Int("x_min", "Lowest the player can declare", min, 0, 99999).ShowIf(Declared),
            ParamSpec.Int("x_max", "Highest the player can declare", max, 1, 99999).ShowIf(Declared),
            ParamSpec.Int("x_step", "In steps of", step, 1, 10000).ShowIf(Declared),
            ParamSpec.Choice(HowKey, "X is equal to", ValueSources.Hows).ShowIf(Valued),
            ParamSpec.Int(AmountKey, "Times", 2, 1, 99999).ShowIf(b => Valued(b) && b.Values.GetValueOrDefault(HowKey) == "per"),
            ParamSpec.Choice(ValueKey, "Of", sources).ShowIf(Valued),
            ValueSources.Also(ValueSources.MemoryParam(ValueKey, Suffix), Valued),
            .. Counters.Params(withAny: true, b => Valued(b) && ValueSources.UsesCounter(Source(b)), Suffix),
            ParamSpec.Cards(CardsKey, "Which cards (for counts and totals)", () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Hand })
                .ShowIf(b => Valued(b) && ValueSources.UsesCards(Source(b))),
        ];
    }

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>"declare a number from 1 to 6", "declare an amount from 100 to 8000 in multiples of 100".</summary>
    public static string DeclareText(BlockArgs args, string noun)
    {
        var step = args.Int("x_step");
        var from = $"declare {noun} from {N(args.Int("x_min"))} to {N(args.Int("x_max"))}";
        return step > 1 ? $"{from} in multiples of {N(step)}" : from;
    }

    /// <summary>"your opponent's LP", "half the number of cards in your opponent's hand", "the number of ... x 2".</summary>
    public static string ValueText(BlockArgs args)
    {
        var source = args.Choice(ValueKey);
        var of = source == "cost_count" ? "the number of " + ValueSources.CostNoun(args.Effect, plural: true)
            : ValueSources.Of(source, args.Cards(CardsKey), args.Pack, "this card", Counters.Name(args, Suffix), args.Effect);
        return args.Choice(HowKey) switch
        {
            "half" => "half " + of,
            "double" => "double " + of,
            "per" => $"{of} x {N(args.Int(AmountKey))}",
            _ => of,
        };
    }

    /// <summary>The value as Lua, read for the player "you" means (tp).</summary>
    public static string ValueLua(BlockContext ctx) =>
        ValueSources.Lua(ctx, "e:GetHandler()", "tp", ev: true, valueKey: ValueKey, cardsKey: CardsKey, howKey: HowKey, amountKey: AmountKey, counterSuffix: Suffix);

    /// <summary>Whether the value is only known as the effect resolves, so it can't be checked as it is activated.</summary>
    public static bool AtResolution(BlockArgs args) => ValueSources.AtResolution(args.Choice(ValueKey));

    /// <summary>
    /// Lines that let the player declare X into <paramref name="v"/>: the numbers from the lowest to the highest in steps,
    /// keeping only those <paramref name="allowed"/> (a Lua test of <c>i</c>) lets through, as official cards build the list.
    /// </summary>
    public static List<string> DeclareLines(BlockContext ctx, string v, string allowed = "true")
    {
        var (min, max, step) = (ctx.Args.Int("x_min"), ctx.Args.Int("x_max"), ctx.Args.Int("x_step"));
        if (max < min) ctx.Effect.Error($"X: the highest number the player can declare ({N(max)}) is lower than the lowest ({N(min)}).");
        else if ((max - min) / step + 1 > 200) ctx.Effect.Warn($"X: the player chooses from {N((max - min) / step + 1)} numbers; a bigger step keeps the list short.");
        var t = ctx.Effect.NewLocal("xt");
        return
        [
            $"local {t}={{}}",
            $"for i={N(min)},{N(max)},{N(step)} do if {allowed} then {t}[#{t}+1]=i end end",
            $"local {v}=Duel.AnnounceNumber(tp,table.unpack({t}))",
        ];
    }
}
