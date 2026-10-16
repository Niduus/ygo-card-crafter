using System.Globalization;
using CardCrafter.Core.Compiler;

namespace CardCrafter.Core.Blocks;

/// <summary>"If this card's ATK is 3000 or more", "if this card's ATK is higher than its original ATK", "if its Level is 5 or less".</summary>
public sealed class SelfStatCondition : ConditionBlock
{
    public const string BlockId = "if.self_stat";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card's ATK / DEF / Level is ...";
    public override string Sentence => "This card's {stat} is {cmp} {amount}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("stat", "Stat", new("atk", "ATK"), new("def", "DEF"), new("level", "Level"), new("rank", "Rank")),
        ParamSpec.Choice("cmp", "Is",
            new("at_least", "at least"), new("at_most", "at most"), new("exactly", "exactly"),
            new("above_orig", "higher than its original", "higher than its original value"),
            new("below_orig", "lower than its original", "lower than its original value"),
            new("not_orig", "different from its original", "different from its original value")),
        ParamSpec.Int("amount", "Number", 2000, 0, 999999).ShowIf(b => b.Values.GetValueOrDefault("cmp", "at_least") is "at_least" or "at_most" or "exactly"),
    ];

    public override string Describe(BlockArgs args)
    {
        var stat = args.Describe("stat");
        var n = args.Int("amount").ToString(CultureInfo.InvariantCulture);
        return args.Choice("cmp") switch
        {
            "at_most" => $"this card's {stat} is {n} or less",
            "exactly" => $"this card's {stat} is {n}",
            "above_orig" => $"this card's {stat} is higher than its original {stat}",
            "below_orig" => $"this card's {stat} is lower than its original {stat}",
            "not_orig" => $"this card's {stat} is different from its original {stat}",
            _ => $"this card's {stat} is {n} or more",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var (now, orig) = ctx.Args.Choice("stat") switch
        {
            "def" => ("GetDefense()", "GetBaseDefense()"),
            "level" => ("GetLevel()", "GetOriginalLevel()"),
            "rank" => ("GetRank()", "GetOriginalRank()"),
            _ => ("GetAttack()", "GetBaseAttack()"),
        };
        var n = ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture);
        var test = ctx.Args.Choice("cmp") switch
        {
            "at_most" => $"c:{now}<={n}",
            "exactly" => $"c:{now}=={n}",
            "above_orig" => $"c:{now}>c:{orig}",
            "below_orig" => $"c:{now}<c:{orig}",
            "not_orig" => $"c:{now}~=c:{orig}",
            _ => $"c:{now}>={n}",
        };
        ctx.Effect.Conditions.Add($"e:GetHandler():IsFaceup() and ({test.Replace("c:", "e:GetHandler():", StringComparison.Ordinal)})");
    }
}
