using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// The top of the Deck: banishing its top cards as a cost or an effect (Kashtira, Card Crawl, Omega Vidolium, Number 89:
// Diablosis the Mind Hacker), as official scripts do it: Duel.GetDecktopGroup, then Duel.Remove without shuffling.

/// <summary>
/// "Banish the top 3 cards of your opponent's Deck face-down, and if you banish any, ...", "banish cards from the top of
/// your opponent's Deck, face-down, equal to their face-down banished cards": the top cards of a Deck, a fixed number or
/// as many as a value says.
/// </summary>
public sealed class BanishTopAction : ActionBlock
{
    public const string BlockId = "do.banish_top";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "Banish the top cards of a Deck";
    public override string Sentence => "Banish the top {count} {by} {how} {amount} {value} {mem} {counter} {counter_id} {of} card(s) of {whose} Deck {facedown}";
    public override string Help => "The cards must all be there and be banishable to activate it. \"Equal to a value\" counts as the effect resolves " +
                                   "(Number 89: Diablosis: \"equal to their face-down banished cards\"). A step after it linked with \"and if you do\" reads \"and if you banish any\".";

    private static bool ByValue(BlockInstance b) => b.Values.GetValueOrDefault("by", "number") == "value";

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 3, 1, 40).ShowIf(b => !ByValue(b)),
        ParamSpec.Choice("by", "How many", new("number", "", "a number"), new("value", "", "equal to a value:")),
        .. ValueSources.ParamsWhen(ByValue, amountDefault: 1),
        ParamSpec.Choice("whose", "Whose Deck", new("you", "your"), new("opponent", "your opponent's")),
        ParamSpec.Flag("facedown", "face-down", false),
    ];

    public override string? IfYouDoText(BlockArgs args) => "if you banish any";

    public override string Describe(BlockArgs args)
    {
        var whose = args.Describe("whose");
        if (args.Choice("by") == "value")
            // Number 89: Diablosis the Mind Hacker: "banish cards from the top of your opponent's Deck, face-down, equal to ...".
            return $"banish cards from the top of {whose} Deck{(args.Bool("facedown") ? ", face-down," : "")} {Banished(args) ?? ValueSources.Phrase(args, "", "this card", forEach: false)}";
        var n = args.Int("count");
        return $"banish the top {(n == 1 ? "card" : n.ToString(CultureInfo.InvariantCulture) + " cards")} of {whose} Deck{(args.Bool("facedown") ? " face-down" : "")}";
    }

    /// <summary>"equal to their face-down banished cards": the number of a player's banished cards, as Diablosis says it.</summary>
    private static string? Banished(BlockArgs args)
    {
        var f = args.Cards(ValueSources.CardsKey);
        if (args.Choice("value") != "count" || args.Choice("how") != "equal" || f.Zones != Zone.Banished || f.Owner == CardOwner.Either) return null;
        var kind = FilterText.Describe(f, args.Pack, plural: true, preposition: "", withLocation: false);
        var space = kind.LastIndexOf(' ');
        kind = space < 0 ? "banished " + kind : $"{kind[..space]} banished{kind[space..]}";
        var whose = f.Owner == CardOwner.You ? "your" : args.Choice("whose") == "opponent" ? "their" : "your opponent's";
        return $"equal to {whose} {kind}";
    }

    /// <summary>Whether the top <c>n</c> cards of player p's Deck are there and can all be banished in position pos.</summary>
    public static string Removable(ScriptBuilder script) => script.AddFunction("topremovable", "tp,p,n,pos",
    [
        "if n<=0 then return false end",
        "local g=Duel.GetDecktopGroup(p,n)",
        "return g:GetCount()==n and g:FilterCount(Card.IsAbleToRemove,nil,tp,pos)==n",
    ]);

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var p = PlayerOf(ctx.Args.Choice("whose"));
        var pos = ctx.Args.Bool("facedown") ? "POS_FACEDOWN" : "POS_FACEUP";
        var byValue = ctx.Args.Choice("by") == "value";
        var n = byValue ? ValueSources.Lua(ctx, "e:GetHandler()", "tp") : ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture);
        e.AddCategory("CATEGORY_REMOVE");
        // A value known only as the effect resolves (the LP paid, a die roll) is not checked when it is activated.
        if (!byValue || !ValueSources.AtResolution(ctx.Args.Choice("value"))) e.TargetChecks.Add($"{Removable(ctx.Script)}(tp,{p},{n},{pos})");
        e.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_REMOVE,nil,{(byValue ? "1" : n)},{p},LOCATION_DECK)");
        var r = e.NewLocal("r");
        var ct = e.NewLocal("ct");
        var g = e.NewLocal("g");
        e.AddStep(
        [
            $"local {r}=0",
            $"local {ct}={n}",
            $"if {ct}>0 then",
            $"\tlocal {g}=Duel.GetDecktopGroup({p},{ct})",
            $"\tif {g}:GetCount()>0 then",
            "\t\tDuel.DisableShuffleCheck()",
            $"\t\t{r}=Duel.Remove({g},{pos},REASON_EFFECT)",
            "\tend",
            "end",
        ], r, ctx.Link);
    }
}

/// <summary>
/// "Banish (face-down) the top 3 cards of your Deck;" as a cost (Omega Vidolium), also as one "●" option's own cost: Card
/// Crawl's "banish (face-down) the top 5, 10, or 15 cards of your Deck" is an option for each number.
/// </summary>
public sealed class BanishTopCost : CostBlock
{
    public const string BlockId = "cost.banish_top";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "Banish the top cards of your Deck (cost)";
    public override string Sentence => "Banish {facedown} the top {count} card(s) of your Deck";
    public override string Help => "For \"the top 5, 10, or 15 cards\" (Card Crawl), put a \"Choose 1 of these effects\" block first and this block at the start of each option.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 3, 1, 40),
        ParamSpec.Flag("facedown", "(face-down)", false),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        return $"banish{(args.Bool("facedown") ? " (face-down)" : "")} the top {(n == 1 ? "card" : n.ToString(CultureInfo.InvariantCulture) + " cards")} of your Deck";
    }

    public override void Compile(BlockContext ctx)
    {
        var n = ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture);
        var pos = ctx.Args.Bool("facedown") ? "POS_FACEDOWN" : "POS_FACEUP";
        ctx.Effect.CostChecks.Add($"Duel.GetDecktopGroup(tp,{n}):FilterCount(Card.IsAbleToRemoveAsCost,nil,{pos})=={n}");
        ctx.Effect.CostLines.Add("Duel.DisableShuffleCheck()");
        ctx.Effect.CostLines.Add($"Duel.Remove(Duel.GetDecktopGroup(tp,{n}),{pos},REASON_COST)");
    }
}
