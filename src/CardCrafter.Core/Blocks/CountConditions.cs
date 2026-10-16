using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>Wording and Lua shared by the conditions that count or compare cards.</summary>
internal static class CardCounts
{
    /// <summary>"monsters you control", "cards in your GY", "monsters on the field".</summary>
    public static string Group(CardFilter f, CardPack? pack, bool plural = true)
    {
        if (FilterText.IsFieldOnly(f))
        {
            var noun = FilterText.Describe(f, pack, plural, "", withLocation: false);
            return noun + f.Owner switch { CardOwner.You => " you control", CardOwner.Opponent => " your opponent controls", _ => " on the field" };
        }
        return FilterText.Describe(f, pack, plural, "in");
    }

    /// <summary>"Dragon monsters", "LIGHT Fairy monsters": what the cards must be, wherever they are.</summary>
    public static string Kind(CardFilter f, CardPack? pack, bool plural = true) =>
        FilterText.Describe(f, pack, plural, "", withLocation: false);

    public static string Count(BlockContext ctx, FilterRef f) =>
        $"Duel.GetMatchingGroupCount({f.Function},tp,{f.Self},{f.Opp},{f.Exclude},e,tp)";

    public static string Group(BlockContext ctx, FilterRef f) =>
        $"Duel.GetMatchingGroup({f.Function},tp,{f.Self},{f.Opp},{f.Exclude},e,tp)";

    public static readonly ChoiceOption[] Compare =
    [
        new("at_least", "at least"),
        new("exactly", "exactly"),
        new("at_most", "at most"),
    ];

    public static string Op(string cmp) => cmp switch { "exactly" => "==", "at_most" => "<=", _ => ">=" };

    /// <summary>"2 or more", "exactly 2", "2 or fewer".</summary>
    public static string Amount(string cmp, int n) => cmp switch
    {
        "exactly" => $"exactly {n}",
        "at_most" => $"{n} or fewer",
        _ => $"{n} or more",
    };
}

/// <summary>
/// "If all monsters you control are Dragon monsters", "If you control no monsters, or all monsters you control are
/// WATER monsters", and "If you control a monster that isn't a Warrior monster". Face-down monsters are never of a kind.
/// </summary>
public sealed class AllAreCondition : ConditionBlock
{
    public const string BlockId = "if.all_are";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "All cards somewhere are of a kind (or none are there)";
    public override string Sentence => "{mode} {cards} are {kind}";
    public override string Help => "For example: \"If all monsters you control are Dragon monsters\" or \"If you control no monsters, or all monsters you control are WATER monsters\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("mode", "Which",
            new("all", "All", "all (and there is at least 1)"),
            new("none_or_all", "None, or all", "none, or all"),
            new("not_all", "Not all", "not all (one isn't)")),
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
        ParamSpec.Cards("kind", "What they must be", () => new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Dragon }),
    ];

    public override string Describe(BlockArgs args)
    {
        var group = args.Cards("cards");
        var all = CardCounts.Group(group, args.Pack);
        var kind = CardCounts.Kind(args.Cards("kind"), args.Pack);
        var none = FilterText.IsFieldOnly(group) && group.Owner != CardOwner.Either
            ? $"{(group.Owner == CardOwner.You ? "you control" : "your opponent controls")} no {FilterText.Describe(group, args.Pack, true, "", withLocation: false)}"
            : $"there are no {all}";
        return args.Choice("mode") switch
        {
            "none_or_all" => $"{none}, or all {all} are {kind}",
            "not_all" => $"not all {all} are {kind}",
            _ => $"all {all} are {kind}",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var group = ctx.Args.Cards("cards");
        var g = ctx.Script.Filter(group);
        var kind = ctx.Args.Cards("kind").Clone();
        kind.Zones = group.Zones;
        // A face-down card on the field is no kind at all.
        if ((group.Zones & (Zone.Field | Zone.Banished)) != Zone.None) kind.FaceUpOnly = true;
        var tests = FilterCompiler.Conditions(kind);
        var odd = ctx.Script.AddFunction("notkindfilter", "c,e,tp",
            [$"return {g.Function}(c,e,tp) and not ({(tests.Count == 0 ? "true" : string.Join(" and ", tests))})"]);
        var odds = $"Duel.IsExistingMatchingCard({odd},tp,{g.Self},{g.Opp},1,{g.Exclude},e,tp)";
        ctx.Effect.Conditions.Add(ctx.Args.Choice("mode") switch
        {
            "none_or_all" => $"not {odds}",
            "not_all" => odds,
            _ => $"Duel.IsExistingMatchingCard({g.Function},tp,{g.Self},{g.Opp},1,{g.Exclude},e,tp) and not {odds}",
        });
    }
}

/// <summary>"If you control more monsters than your opponent does", "if there are fewer cards in your GY than in your opponent's".</summary>
public sealed class CompareCountsCondition : ConditionBlock
{
    public override string Id => "if.compare_counts";
    public override string Group => "Cards";
    public override string Name => "Compare how many cards there are (more, fewer, the same)";
    public override string Sentence => "There are {cmp} {cards} than {other}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("cmp", "Compared",
            new("more", "more"), new("fewer", "fewer"), new("same", "the same number of"),
            new("at_least", "at least as many"), new("at_most", "at most as many")),
        ParamSpec.Cards("cards", "These cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
        ParamSpec.Cards("other", "Than these", () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
    ];

    public override string Describe(BlockArgs args)
    {
        var a = args.Cards("cards");
        var b = args.Cards("other");
        var cmp = args.Choice("cmp");
        // "you control more monsters than your opponent does": the same cards on each side of the field.
        if (FilterText.IsFieldOnly(a) && FilterText.IsFieldOnly(b) && a.Owner != b.Owner && a.Owner != CardOwner.Either && b.Owner != CardOwner.Either
            && a.Zones == b.Zones && FilterCompiler.Conditions(a).SequenceEqual(FilterCompiler.Conditions(b)))
        {
            var noun = FilterText.Describe(a, args.Pack, true, "", withLocation: false);
            var (who, them) = a.Owner == CardOwner.You ? ("you control", "your opponent does") : ("your opponent controls", "you do");
            return cmp switch
            {
                "same" => $"{who} the same number of {noun} as {them}",
                "at_least" => $"{who} at least as many {noun} as {them}",
                "at_most" => $"{who} at most as many {noun} as {them}",
                _ => $"{who} {cmp} {noun} than {them}",
            };
        }
        var (x, y) = (CardCounts.Group(a, args.Pack), CardCounts.Group(b, args.Pack));
        return cmp switch
        {
            "same" => $"there are as many {x} as {y}",
            "at_least" => $"there are at least as many {x} as {y}",
            "at_most" => $"there are at most as many {x} as {y}",
            _ => $"there are {cmp} {x} than {y}",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var a = CardCounts.Count(ctx, ctx.Script.Filter(ctx.Args.Cards("cards")));
        var b = CardCounts.Count(ctx, ctx.Script.Filter(ctx.Args.Cards("other")));
        var op = ctx.Args.Choice("cmp") switch { "fewer" => "<", "same" => "==", "at_least" => ">=", "at_most" => "<=", _ => ">" };
        ctx.Effect.Conditions.Add($"{a}{op}{b}");
    }
}

/// <summary>"If there are 3 or more different Attributes among the monsters in your GY", "... monsters with different names".</summary>
public sealed class DifferentCondition : ConditionBlock
{
    public override string Id => "if.different";
    public override string Group => "Cards";
    public override string Name => "Different Attributes / Types / names / Levels among cards";
    public override string Sentence => "There are {cmp} {count} different {what} among {cards}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("cmp", "How many", CardCounts.Compare),
        ParamSpec.Int("count", "Number", 2, 1, 30),
        ParamSpec.Choice("what", "Different",
            new("attribute", "Attributes"), new("race", "Types"), new("name", "card names"), new("level", "Levels"), new("rank", "Ranks"), new("kind", "card types (Monster, Spell, Trap)")),
        ParamSpec.Cards("cards", "Among", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster }),
    ];

    public override string Describe(BlockArgs args) =>
        $"there are {CardCounts.Amount(args.Choice("cmp"), args.Int("count"))} different {args.Describe("what")} among the {CardCounts.Group(args.Cards("cards"), args.Pack)}";

    public override void Compile(BlockContext ctx)
    {
        var g = CardCounts.Group(ctx, ctx.Script.Filter(ctx.Args.Cards("cards")));
        var by = ctx.Args.Choice("what") switch
        {
            "race" => "Card.GetRace",
            "name" => "Card.GetCode",
            "level" => "Card.GetLevel",
            "rank" => "Card.GetRank",
            "kind" => ctx.Script.AddFunction("cardkind", "c", ["return c:GetType()&(TYPE_MONSTER+TYPE_SPELL+TYPE_TRAP)"]),
            _ => "Card.GetAttribute",
        };
        ctx.Effect.Conditions.Add($"{g}:GetClassCount({by}){CardCounts.Op(ctx.Args.Choice("cmp"))}{ctx.Args.Int("count")}");
    }
}

/// <summary>"If the total ATK of monsters you control is 3000 or more", "... the total Levels of monsters in your GY is 12 or less".</summary>
public sealed class TotalCondition : ConditionBlock
{
    public override string Id => "if.total";
    public override string Group => "Cards";
    public override string Name => "The total ATK / DEF / Levels of cards";
    public override string Sentence => "The total {stat} of {cards} is {cmp} {amount}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("stat", "Total", new("atk", "ATK"), new("def", "DEF"), new("level", "Levels"), new("rank", "Ranks"), new("link", "Link Ratings"), new("count", "Xyz Materials")),
        ParamSpec.Cards("cards", "Of", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true }),
        ParamSpec.Choice("cmp", "Is", CardCounts.Compare),
        ParamSpec.Int("amount", "Amount", 3000, 0, 999999),
    ];

    public override string Describe(BlockArgs args)
    {
        var amount = args.Int("amount").ToString(CultureInfo.InvariantCulture);
        var value = args.Choice("cmp") switch { "exactly" => $"exactly {amount}", "at_most" => $"{amount} or less", _ => $"{amount} or more" };
        var stat = args.Choice("stat") == "count" ? "number of materials attached to" : $"total {args.Describe("stat")} of";
        return $"the {stat} {CardCounts.Group(args.Cards("cards"), args.Pack)} is {value}";
    }

    public override void Compile(BlockContext ctx)
    {
        var g = CardCounts.Group(ctx, ctx.Script.Filter(ctx.Args.Cards("cards")));
        var by = ctx.Args.Choice("stat") switch
        {
            "def" => "Card.GetDefense",
            "level" => "Card.GetLevel",
            "rank" => "Card.GetRank",
            "link" => "Card.GetLink",
            "count" => "Card.GetOverlayCount",
            _ => "Card.GetAttack",
        };
        ctx.Effect.Conditions.Add($"{g}:GetSum({by}){CardCounts.Op(ctx.Args.Choice("cmp"))}{ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture)}");
    }
}
