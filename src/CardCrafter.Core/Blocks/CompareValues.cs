using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// The general comparison: any live value against a number or another value. "If your LP are at least double your
/// opponent's", "if the total ATK of monsters you control is more than your opponent's LP", "if the number of cards in
/// your hand is less than the number of cards in your opponent's hand", "if the difference in LP is between 1000 and 3000".
/// </summary>
public sealed class CompareValuesCondition : ConditionBlock
{
    public const string BlockId = "if.compare_values";
    public override string Id => BlockId;
    public override string Group => "Values";
    public override string Name => "Compare values (LP, counts, totals, ...) with a number or each other";
    public override string Sentence => "{value} {mem} {counter} {counter_id} {of} {op} {right} {amount} {amount2} {scale} {value2} {mem2} {counter2} {counter_id2} {of2}";
    public override string Help => "For example: your LP compared with double your opponent's LP, or the number of cards in your hand with the number in your opponent's.";

    private static IEnumerable<ChoiceOption> NoEvent => ValueSources.Sources.Where(o => !ValueSources.AtResolution(o.Value) && !o.Value.StartsWith("its_", StringComparison.Ordinal));

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("value", "Value", NoEvent.ToArray()),
        ValueSources.MemoryParam(),
        ParamSpec.Cards(ValueSources.CardsKey, "Which cards (for counts and totals)", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand })
            .ShowIf(b => ValueSources.UsesCards(b.Values.GetValueOrDefault("value", "lp_diff"))),
        .. Counters.Params(withAny: true, b => ValueSources.UsesCounter(b.Values.GetValueOrDefault("value", "lp_diff"))),
        ParamSpec.Choice("op", "Is",
            new("ge", "at least"), new("le", "at most"), new("gt", "more than"), new("lt", "less than"), new("eq", "exactly"), new("ne", "not"),
            new("between", "between"), new("outside", "outside")),
        ParamSpec.Choice("right", "Than", new("number", "", "a number"), new("value", "", "another value:")).ShowIf(b => b.Values.GetValueOrDefault("op") is not ("between" or "outside")),
        ParamSpec.Int("amount", "Number", 2000, 0, 999999).ShowIf(b => b.Values.GetValueOrDefault("right", "number") == "number" || b.Values.GetValueOrDefault("op") is "between" or "outside"),
        ParamSpec.Int("amount2", "and", 4000, 0, 999999).ShowIf(b => b.Values.GetValueOrDefault("op") is "between" or "outside"),
        ParamSpec.Choice("scale", "Of", new("one", "", "1x"), new("half", "half", "half of"), new("double", "double", "double")).ShowIf(Other),
        ParamSpec.Choice("value2", "Other value", NoEvent.ToArray()).ShowIf(Other),
        ValueSources.MemoryParam("value2", "2").ShowIf(b => Other(b) && ValueSources.Remembered(b.Values.GetValueOrDefault("value2", "lp_diff"))),
        ParamSpec.Cards("of2", "Which cards (other value)", () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Hand })
            .ShowIf(b => Other(b) && ValueSources.UsesCards(b.Values.GetValueOrDefault("value2", "lp_diff"))),
        .. Counters.Params(withAny: true, b => Other(b) && ValueSources.UsesCounter(b.Values.GetValueOrDefault("value2", "lp_diff")), suffix: "2"),
    ];

    private static bool Other(BlockInstance b) => b.Values.GetValueOrDefault("right") == "value" && b.Values.GetValueOrDefault("op") is not ("between" or "outside");

    public override string Describe(BlockArgs args)
    {
        var left = ValueSources.Of(args.Choice("value"), args.Cards(ValueSources.CardsKey), args.Pack, "this card", Counters.Name(args), args.Effect);
        var (n, m) = (args.Int("amount"), args.Int("amount2"));
        string N(int x) => x.ToString(CultureInfo.InvariantCulture);
        var op = args.Choice("op");
        if (op is "between") return $"{left} is between {N(Math.Min(n, m))} and {N(Math.Max(n, m))}";
        if (op is "outside") return $"{left} is less than {N(Math.Min(n, m))} or more than {N(Math.Max(n, m))}";
        var right = args.Choice("right") == "value"
            ? (args.Choice("scale") switch { "half" => "half ", "double" => "double ", _ => "" }) + ValueSources.Of(args.Choice("value2"), args.Cards("of2"), args.Pack, "this card", Counters.Name(args, "2"), args.Effect)
            : N(n);
        return op switch
        {
            "le" => $"{left} is {right} or less",
            "gt" => $"{left} is more than {right}",
            "lt" => $"{left} is less than {right}",
            "eq" => $"{left} is exactly {right}",
            "ne" => $"{left} is not {right}",
            _ => $"{left} is {right} or more",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var left = ValueSources.Lua(ctx, "e:GetHandler()", "tp", ev: false);
        var (n, m) = (ctx.Args.Int("amount"), ctx.Args.Int("amount2"));
        string N(int x) => x.ToString(CultureInfo.InvariantCulture);
        var op = ctx.Args.Choice("op");
        if (op is "between" or "outside")
        {
            var (lo, hi) = (N(Math.Min(n, m)), N(Math.Max(n, m)));
            ctx.Effect.Conditions.Add(op == "between" ? $"({left})>={lo} and ({left})<={hi}" : $"(({left})<{lo} or ({left})>{hi})");
            return;
        }
        var right = N(n);
        if (ctx.Args.Choice("right") == "value")
        {
            var other = ValueSources.Lua(ctx, "e:GetHandler()", "tp", ev: false, valueKey: "value2", cardsKey: "of2", howKey: "none", counterSuffix: "2");
            right = ctx.Args.Choice("scale") switch { "half" => $"math.floor(({other})/2)", "double" => $"({other})*2", _ => other };
        }
        var lua = op switch { "le" => "<=", "gt" => ">", "lt" => "<", "eq" => "==", "ne" => "~=", _ => ">=" };
        ctx.Effect.Conditions.Add($"({left}){lua}({right})");
    }
}
