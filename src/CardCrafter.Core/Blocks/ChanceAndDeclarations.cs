using System.Globalization;
using System.Runtime.CompilerServices;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// What a coin toss, a die roll or a declaration left for the blocks after it in the same effect: the Lua locals its
/// results are kept in. A toss or roll is followed by result IF blocks among the DO blocks ("if the result is heads"),
/// each one starting what happens on that result; linked with "●" they read as "● Heads: ..." lines.
/// </summary>
public static class Chance
{
    private sealed class State
    {
        /// <summary>The number of heads of the last coin toss (-1 when no coin was tossed), and how many coins.</summary>
        public string? Heads;
        public int Coins;
        /// <summary>For "toss a coin and call it": true when called right, false when wrong, nil when not tossed.</summary>
        public string? Right;
        /// <summary>The total of the last die roll (0 when no die was rolled), and how many dice.</summary>
        public string? Roll;
        public int Dice;
        /// <summary>The lines of the last toss and roll, which the value blocks add their copy of the result to.</summary>
        public List<string>? CoinLines, RollLines;
        /// <summary>What the effect declared at activation: "name", "monster_name", "race", "attribute", "level" or "type".</summary>
        public string? Declared;
    }

    private static readonly ConditionalWeakTable<EffectBuilder, State> States = new();
    private static State Of(EffectBuilder e) => States.GetValue(e, _ => new State());

    /// <summary>The name of the field the toss or roll copies its result to, for the value blocks ("the result x 100").</summary>
    public const string RollField = "s.ccroll", HeadsField = "s.ccheads";

    /// <summary>True for an IF block that tests a coin toss or a die roll: it starts one of the results' steps.</summary>
    public static bool IsGate(BlockInstance b) => BlockRegistry.Find(b.BlockId) is CoinResultCondition or DieResultCondition;

    /// <summary>True for a block that tosses coins or rolls dice, which starts a new set of results.</summary>
    public static bool IsSource(BlockInstance b) => b.BlockId is TossCoinAction.BlockId or RollDieAction.BlockId;

    /// <summary>
    /// "Toss a coin. ● Heads: ... ● Tails: ...": the DO blocks' "●" options are the results of a toss or roll (each starts
    /// with a result IF block), not effects the player chooses from.
    /// </summary>
    public static bool HasResultOptions(IReadOnlyList<BlockInstance> blocks)
    {
        var options = blocks.Skip(1).Where(b => b.Link == ActionLink.Option).ToList();
        return options.Count > 0 && options.All(IsGate);
    }

    /// <summary>An effect's "●" lines are all a toss or roll's results, or all options the player chooses from.</summary>
    public static void CheckOptions(EffectBuilder effect, IReadOnlyList<BlockInstance> blocks)
    {
        var options = blocks.Skip(1).Where(b => b.Link == ActionLink.Option).ToList();
        if (options.Any(IsGate) && !options.All(IsGate))
            effect.Error("\"●\" lines are either the results of a coin toss or die roll (each starting with a result IF block) or effects the player chooses from, not both.");
    }

    /// <summary>
    /// The guard every toss and roll makes, as official cards do: when the effect targets, the coin is tossed only while
    /// a target is still there ("if not tc:IsRelateToEffect(e) then return end").
    /// </summary>
    internal static List<string> Guard(EffectBuilder e, IEnumerable<string> lines)
    {
        var tg = e.NewLocal("tg");
        return
        [
            $"local {tg}=Duel.GetChainInfo(0,CHAININFO_TARGET_CARDS)",
            $"if not {tg} or {tg}:IsExists(Card.IsRelateToEffect,1,nil,e) then",
            .. lines.Select(l => "\t" + l),
            "end",
        ];
    }

    /// <summary>
    /// A toss or roll is a step of its own: its results are locals the steps after it read, so it can't be one the player
    /// may skip ("you can"), which would keep them inside that choice.
    /// </summary>
    internal static void NotOptional(BlockContext ctx)
    {
        if (ctx.Args.Instance.Optional) ctx.Effect.Error("A coin toss or die roll can't be ticked \"you can\"; make the effect optional instead (its WHEN).");
    }

    internal static void Tossed(EffectBuilder e, string heads, string? right, int coins, List<string> lines)
    {
        var s = Of(e);
        (s.Heads, s.Right, s.Coins, s.CoinLines) = (heads, right, coins, lines);
    }

    internal static void Rolled(EffectBuilder e, string roll, int dice, List<string> lines)
    {
        var s = Of(e);
        (s.Roll, s.Dice, s.RollLines) = (roll, dice, lines);
    }

    /// <summary>The last coin toss of the effect: its heads and called-right locals and its number of coins, or null.</summary>
    internal static (string Heads, string? Right, int Coins)? Coin(EffectBuilder e) =>
        States.TryGetValue(e, out var s) && s.Heads is { } h ? (h, s.Right, s.Coins) : null;

    /// <summary>The last die roll of the effect: its total local and its number of dice, or null.</summary>
    internal static (string Roll, int Dice)? Die(EffectBuilder e) =>
        States.TryGetValue(e, out var s) && s.Roll is { } r ? (r, s.Dice) : null;

    /// <summary>
    /// "the result" or "the number of heads" for a value block: the field the toss or roll copies its result to, which
    /// helper functions can read too. An error when no toss or roll comes before.
    /// </summary>
    public static string ValueLua(BlockContext ctx, string source)
    {
        var s = States.TryGetValue(ctx.Effect, out var st) ? st : null;
        if (source == "heads")
        {
            if (s?.CoinLines is not { } coin) { ctx.Effect.Error("\"The number of heads\" needs a \"Toss a coin\" block before it among the DO blocks."); return "0"; }
            if (!coin.Contains($"{HeadsField}=math.max({s.Heads},0)")) coin.Add($"{HeadsField}=math.max({s.Heads},0)");
            return HeadsField;
        }
        if (s?.RollLines is not { } roll) { ctx.Effect.Error("\"The result\" needs a \"Roll a six-sided die\" block before it among the DO blocks."); return "0"; }
        if (!roll.Contains($"{RollField}={s.Roll}")) roll.Add($"{RollField}={s.Roll}");
        return RollField;
    }

    /// <summary>What the effect declares at activation, or null.</summary>
    public static string? Declared(EffectBuilder e) => States.TryGetValue(e, out var s) ? s.Declared : null;

    internal static void Declare(EffectBuilder e, string kind) => Of(e).Declared = kind;

    /// <summary>
    /// The activation checks of the blocks after a toss or roll's results: the effect can be activated when one result
    /// could do what it says ("Draw 2 cards" or "Your opponent draws 2 cards"), as official cards check it. Checks on
    /// the effect's targets stay as they are: targets are chosen whatever the result.
    /// </summary>
    public sealed class CaseChecks(EffectBuilder effect)
    {
        private readonly List<List<string>> _cases = [];
        private int _start = -1;

        /// <summary>Call before each DO block compiles.</summary>
        public void Before(BlockInstance b)
        {
            if (IsGate(b)) { Close(); _start = effect.TargetChecks.Count; }
            else if (IsSource(b)) End();
        }

        private void Close()
        {
            if (_start < 0) return;
            var added = effect.TargetChecks.Skip(_start).ToList();
            var taken = added.Where(c => !c.Contains("IsExistingTarget(", StringComparison.Ordinal)).ToList();
            foreach (var c in taken) effect.TargetChecks.RemoveAt(effect.TargetChecks.LastIndexOf(c));
            _cases.Add(taken);
            _start = -1;
        }

        /// <summary>Call after the last DO block.</summary>
        public void End()
        {
            Close();
            if (_cases.Count > 0 && _cases.All(c => c.Count > 0))
                effect.TargetChecks.Add(_cases.Count == 1 ? string.Join(" and ", _cases[0])
                    : "(" + string.Join(" or ", _cases.Select(c => "(" + string.Join(" and ", c) + ")")) + ")");
            _cases.Clear();
        }
    }

    /// <summary>"1 or 6", "2, 3, or 4": die results as card text lists them.</summary>
    internal static string OrList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} or {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + ", or " + items[^1],
    };
}

// ---------------------------------------------------------------- coins and dice

/// <summary>"Toss a coin", "toss a coin 3 times", "toss a coin and call it": the result IF blocks after it say what happens.</summary>
public sealed class TossCoinAction : ActionBlock
{
    public const string BlockId = "do.toss_coin";
    public override string Id => BlockId;
    public override string Group => "Chance";
    public override string Name => "Toss a coin (then result IF blocks: heads, tails, called right...)";
    public override string Sentence => "Toss a coin {count} time(s) {call}";
    public override string Help => "Follow it with \"The coin toss result is ...\" IF blocks among the DO blocks: the blocks after each one happen " +
                                   "on that result. Link the IF blocks with \"●\" for \"● Heads: ...\" lines. When the effect targets, the coin " +
                                   "is only tossed while a target is still there, as official cards do.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many coins", 1, 1, 5),
        ParamSpec.Flag("call", "and call it", false).ShowIf(b => b.Values.GetValueOrDefault("count", "1") == "1"),
    ];

    public static string Times(int n, string verb) => n switch { 1 => verb, 2 => verb + " twice", _ => $"{verb} {n.ToString(CultureInfo.InvariantCulture)} times" };

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        return Times(n, "toss a coin") + (n == 1 && args.Bool("call") ? " and call it" : "");
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        Chance.NotOptional(ctx);
        var n = ctx.Args.Int("count");
        var call = n == 1 && ctx.Args.Bool("call");
        if (ctx.Args.Bool("call") && n > 1) e.Warn("\"Call it\" is for one coin; it was ignored.");
        e.AddCategory("CATEGORY_COIN");
        e.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_COIN,nil,0,tp,{n.ToString(CultureInfo.InvariantCulture)})");
        var heads = e.NewLocal("heads");
        var right = call ? e.NewLocal("right") : null;
        var called = call ? e.NewLocal("call") : null;
        var res = Enumerable.Range(1, n).Select(i => e.NewLocal("coin")).ToList();
        var toss = new List<string>();
        if (call) toss.AddRange(["Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_COIN)", $"local {called}=Duel.AnnounceCoin(tp)"]);
        toss.Add($"local {string.Join(",", res)}=Duel.TossCoin(tp,{n.ToString(CultureInfo.InvariantCulture)})");
        toss.Add($"{heads}={string.Join("+", res)}");
        // The call is an option of "Heads"/"Tails" (0 or 1), the coin is 1 for heads: different numbers mean called right.
        if (call) toss.Add($"{right}={called}~={res[0]}");
        var lines = new List<string> { call ? $"local {heads},{right}=-1,nil" : $"local {heads}=-1" };
        lines.AddRange(Chance.Guard(e, toss));
        Chance.Tossed(e, heads, right, n, e.AddStep(lines, null, ctx.Link).Lines);
    }
}

/// <summary>"Roll a six-sided die": the result IF blocks after it, or "the result" in a value block, use what was rolled.</summary>
public sealed class RollDieAction : ActionBlock
{
    public const string BlockId = "do.roll_die";
    public override string Id => BlockId;
    public override string Group => "Chance";
    public override string Name => "Roll a six-sided die (then result IF blocks, or \"the result\" as an amount)";
    public override string Sentence => "Roll a six-sided die {count} time(s)";
    public override string Help => "Follow it with \"The die roll result is ...\" IF blocks among the DO blocks (link them with \"●\" for \"● 1 or 2: ...\" " +
                                   "lines), or use \"the result\" as the amount of a value block: \"inflict damage equal to the result x 100\". " +
                                   "With several dice, the result is their total.";
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Int("count", "How many dice", 1, 1, 3)];

    public override string Describe(BlockArgs args) => TossCoinAction.Times(args.Int("count"), "roll a six-sided die");

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        Chance.NotOptional(ctx);
        var n = ctx.Args.Int("count");
        e.AddCategory("CATEGORY_DICE");
        e.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_DICE,nil,0,tp,{n.ToString(CultureInfo.InvariantCulture)})");
        var roll = e.NewLocal("roll");
        var dice = Enumerable.Range(1, n).Select(i => e.NewLocal("die")).ToList();
        var lines = new List<string> { $"local {roll}=0" };
        lines.AddRange(Chance.Guard(e,
        [
            $"local {string.Join(",", dice)}=Duel.TossDice(tp,{n.ToString(CultureInfo.InvariantCulture)})",
            $"{roll}={string.Join("+", dice)}",
        ]));
        Chance.Rolled(e, roll, n, e.AddStep(lines, null, ctx.Link).Lines);
    }
}

/// <summary>
/// "If the result is heads", "If you call it right", "if at least 2 of the results are heads": among the DO blocks after
/// "Toss a coin", the blocks after it happen on that result. Linked with "●" it reads "● Heads: ...".
/// </summary>
public sealed class CoinResultCondition : ConditionBlock
{
    public const string BlockId = "if.coin_result";
    public override string Id => BlockId;
    public override string Group => "Chance";
    public override string Name => "The coin toss result is ... (among the DO blocks, after \"Toss a coin\")";
    public override string Sentence => "The result is {result} {n}";
    public override string Help => "Put it among the DO blocks after \"Toss a coin\": the blocks after it happen on that result, up to the next result block. " +
                                   "Link it with \"●\" for \"● Heads: ...\" lines.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("result", "Result",
            new("heads", "heads"), new("tails", "tails"),
            new("right", "called right", "you called it right"), new("wrong", "called wrong", "you called it wrong"),
            new("heads_n", "exactly this many heads:"), new("heads_min", "at least this many heads:"), new("tails_n", "exactly this many tails:")),
        ParamSpec.Int("n", "How many", 2, 1, 5).ShowIf(b => b.Values.GetValueOrDefault("result", "heads") is "heads_n" or "heads_min" or "tails_n"),
    ];

    private static string Count(int n, string side) => n == 1 ? $"1 {side}" : $"{n.ToString(CultureInfo.InvariantCulture)} {side}s";

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("n");
        return args.Choice("result") switch
        {
            "tails" => "the result is tails",
            "right" => "you call it right",
            "wrong" => "you call it wrong",
            "heads_n" => $"the result is {Count(n, "head")}",
            "heads_min" => $"at least {n.ToString(CultureInfo.InvariantCulture)} of the results are heads",
            "tails_n" => $"the result is {Count(n, "tail")}",
            _ => "the result is heads",
        };
    }

    /// <summary>How a "●" line names the result: "Heads", "Tails", "3 Heads", "1 Head".</summary>
    public static string Label(BlockArgs args)
    {
        var n = args.Int("n");
        return args.Choice("result") switch
        {
            "tails" => "Tails",
            "right" => "If you call it right",
            "wrong" => "If you call it wrong",
            "heads_n" => Count(n, "Head"),
            "heads_min" => $"{n.ToString(CultureInfo.InvariantCulture)} or more Heads",
            "tails_n" => Count(n, "Tail"),
            _ => "Heads",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        if (Chance.Coin(ctx.Effect) is not { } coin)
        {
            ctx.Effect.Error("\"The coin toss result is ...\" goes among the DO blocks, after a \"Toss a coin\" block.");
            return;
        }
        var n = ctx.Args.Int("n");
        var result = ctx.Args.Choice("result");
        if (result is "heads" or "tails" && coin.Coins > 1)
            ctx.Effect.Error($"With {coin.Coins} coins, say how many heads or tails: \"exactly this many heads\" or \"at least this many heads\".");
        if (result is "right" or "wrong" && coin.Right is null)
            ctx.Effect.Error("\"Called it right / wrong\" needs the \"Toss a coin\" block to have \"and call it\" ticked.");
        if (result is "heads_n" or "heads_min" or "tails_n" && n > coin.Coins)
            ctx.Effect.Error($"Only {coin.Coins} coin(s) are tossed, so the result can't have {n}.");
        var h = coin.Heads;
        string N(int x) => x.ToString(CultureInfo.InvariantCulture);
        ctx.Effect.Conditions.Add(result switch
        {
            "tails" => $"{h}==0",
            "right" => $"{coin.Right}==true",
            "wrong" => $"{coin.Right}==false",
            "heads_n" => $"{h}=={N(n)}",
            "heads_min" => $"{h}>={N(n)}",
            "tails_n" => $"{h}=={N(coin.Coins - n)}",
            _ => $"{h}==1",
        });
    }
}

/// <summary>"If you roll a 1 or 6": among the DO blocks after "Roll a six-sided die", the blocks after it happen on those results.</summary>
public sealed class DieResultCondition : ConditionBlock
{
    public const string BlockId = "if.die_result";
    public override string Id => BlockId;
    public override string Group => "Chance";
    public override string Name => "The die roll result is ... (among the DO blocks, after \"Roll a six-sided die\")";
    public override string Sentence => "You roll a {r1} {r2} {r3} {r4} {r5} {r6}";
    public override string Help => "Tick the results. Put it among the DO blocks after \"Roll a six-sided die\": the blocks after it happen on those results, " +
                                   "up to the next result block. Link it with \"●\" for \"● 1 or 2: ...\" lines.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
        Enumerable.Range(1, 6).Select(i => ParamSpec.Flag("r" + i.ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture), false)).ToList();

    public static List<int> Results(BlockArgs args) => Enumerable.Range(1, 6).Where(i => args.Bool("r" + i.ToString(CultureInfo.InvariantCulture))).ToList();

    public override string Describe(BlockArgs args) => $"you roll a {Label(args)}";

    /// <summary>"1 or 2", "2, 3, or 4": the results as a "●" line names them.</summary>
    public static string Label(BlockArgs args) => Chance.OrList(Results(args).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList());

    public override void Compile(BlockContext ctx)
    {
        if (Chance.Die(ctx.Effect) is not { } die)
        {
            ctx.Effect.Error("\"The die roll result is ...\" goes among the DO blocks, after a \"Roll a six-sided die\" block.");
            return;
        }
        if (die.Dice > 1) ctx.Effect.Error("\"The die roll result is ...\" tests one die; this one rolls several.");
        var results = Results(ctx.Args);
        if (results.Count == 0) { ctx.Effect.Error("Tick the die results this block is for."); return; }
        string N(int x) => x.ToString(CultureInfo.InvariantCulture);
        var r = die.Roll;
        // 2, 3, 4 and 5 are one range; 1 and 6 two results.
        var range = results.Count > 2 && results[^1] - results[0] == results.Count - 1;
        ctx.Effect.Conditions.Add(results.Count == 1 ? $"{r}=={N(results[0])}"
            : range ? $"{r}>={N(results[0])} and {r}<={N(results[^1])}"
            : string.Join(" or ", results.Select(x => $"{r}=={N(x)}")));
    }
}

// ---------------------------------------------------------------- declarations

/// <summary>
/// "Declare 1 card name; ...", "declare 1 Monster Type", "declare 1 Attribute", "declare a Level from 1 to 12": the
/// declaration happens when the effect is activated and is kept with it. Card pickers after it can ask for "the declared"
/// name, Type, Attribute, Level or card type; "Cards with that name cannot be used" uses a declared name.
/// </summary>
public sealed class DeclareAction : ActionBlock
{
    public const string BlockId = "do.declare";
    public override string Id => BlockId;
    public override string Group => "Chance";
    public override string Name => "Declare a card name, Type, Attribute, Level or card type";
    public override string Sentence => "Declare {what} {min} {max} {names}";
    public override string Help => "Declared when the effect is activated (before the semicolon). Then add a requirement \"has the declared ...\" to a card " +
                                   "picker, like \"destroy all monsters with the declared Type\", or use \"Cards with the declared name cannot be used\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "Declare",
            new("name", "1 card name"), new("monster_name", "1 Monster Card name"), new("race", "1 Monster Type"), new("attribute", "1 Attribute"),
            new("level", "a Level from", "a Level"), new("type", "1 card type (Monster, Spell, or Trap)")),
        ParamSpec.Int("min", "From", 1, 1, 12).ShowIf(b => b.Values.GetValueOrDefault("what") == "level"),
        ParamSpec.Int("max", "To", 12, 1, 12).ShowIf(b => b.Values.GetValueOrDefault("what") == "level"),
        // Ars Magna Academic Arcane Apparition Acropolis: "declare 1 "Ars Magna" Link Monster Card name that has not been
        // declared for "..." this turn": only the names of the cards picked below (the ones there are), not one declared before.
        ParamSpec.Flag("limited", "only the name of a card picked below", false).ShowIf(Names),
        ParamSpec.Cards("names", "The names of", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster })
            .Unprinted().WhenUsed().ShowIf(b => Names(b) && b.Values.GetValueOrDefault("limited") == "true"),
        ParamSpec.Flag("fresh", "that has not been declared for this card this turn", false).ShowIf(Names),
    ];

    private static bool Names(BlockInstance b) => b.Values.GetValueOrDefault("what", "name") is "name" or "monster_name";

    /// <summary>"1 "Ars Magna" Link Monster Card name", "1 Spell Card name": the names the declaration may be.</summary>
    private static string LimitedNoun(BlockArgs args)
    {
        var words = Text.FilterText.Describe(args.Cards("names"), args.Pack, withLocation: false, ownName: args.CardName);
        if (words.EndsWith(" monster", StringComparison.Ordinal) || words == "monster") words = words[..^"monster".Length] + "Monster Card";
        else if (words.EndsWith("Monster", StringComparison.Ordinal) || words.EndsWith("Spell", StringComparison.Ordinal) || words.EndsWith("Trap", StringComparison.Ordinal)
                 || words.EndsWith("Spell/Trap", StringComparison.Ordinal)) words += " Card";
        return $"1 {words} name";
    }

    public override string Describe(BlockArgs args)
    {
        if (Names(args.Instance) && (args.Bool("limited") || args.Bool("fresh")))
        {
            var noun = args.Bool("limited") ? LimitedNoun(args) : args.Describe("what");
            var name = args.CardName is { Length: > 0 } n ? $"\"{n}\"" : "this card";
            return "declare " + noun + (args.Bool("fresh") ? $" that has not been declared for {name} this turn" : "");
        }
        if (args.Choice("what") != "level") return "declare " + args.Describe("what");
        var (min, max) = (Math.Min(args.Int("min"), args.Int("max")), Math.Max(args.Int("min"), args.Int("max")));
        return $"declare a Level from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}";
    }

    public override (string Target, string Action)? DescribeTargeting(BlockArgs args) => (Describe(args), "");

    /// <summary>The declaration's kind for the "declared" requirements: names count as names.</summary>
    public static string KindOf(string what) => what == "monster_name" ? "name" : what;

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (Chance.Declared(e) is not null) { e.Error("An effect can declare one thing; this one already declares."); return; }
        if (e.TargetLines.Any(l => l.StartsWith("Duel.SetTargetParam(", StringComparison.Ordinal)))
        {
            e.Error($"\"{Name}\": another block of this effect already keeps a number with the chain link (an amount worked out when activated).");
            return;
        }
        var what = ctx.Args.Choice("what");
        var v = e.NewLocal("decl");
        var (min, max) = (Math.Min(ctx.Args.Int("min"), ctx.Args.Int("max")), Math.Max(ctx.Args.Int("min"), ctx.Args.Int("max")));
        string N(int x) => x.ToString(CultureInfo.InvariantCulture);
        var (hint, announce) = what switch
        {
            "monster_name" => ("HINTMSG_CODE", "Duel.AnnounceCard(tp,TYPE_MONSTER,OPCODE_ISTYPE)"),
            "race" => ("HINTMSG_RACE", "Duel.AnnounceRace(tp,1,RACE_ALL)"),
            "attribute" => ("HINTMSG_ATTRIBUTE", "Duel.AnnounceAttribute(tp,1,ATTRIBUTE_ALL)"),
            "level" => ("HINTMSG_LVRANK", $"Duel.AnnounceLevel(tp,{N(min)},{N(max)})"),
            // Kept as the card type's bit (TYPE_MONSTER, TYPE_SPELL, TYPE_TRAP), never 0.
            "type" => ("HINTMSG_CARDTYPE", "1<<Duel.AnnounceType(tp)"),
            _ => ("HINTMSG_CODE", "Duel.AnnounceCard(tp)"),
        };
        var limited = Names(ctx.Args.Instance) && ctx.Args.Bool("limited");
        var fresh = Names(ctx.Args.Instance) && ctx.Args.Bool("fresh");
        if (limited || fresh)
        {
            // The names it may be, as the announce filter's opcodes: those of the cards picked, and none declared this turn.
            var lines = new List<string> { "local used=s.ccdecl and s.ccdecl[tp] and s.ccdecl[tp][Duel.GetTurnCount()] or {}", "local t={}" };
            if (limited)
            {
                var f = ctx.Script.Filter(ctx.Args.Cards("names"), null, "declfilter");
                lines.AddRange(
                [
                    "local seen={}",
                    $"for tc in aux.Next(Duel.GetMatchingGroup({f.Function},tp,{f.Self},{f.Opp},nil,e,tp)) do",
                    "\tlocal code=tc:GetCode()",
                    $"\tif not seen[code]{(fresh ? " and not used[code]" : "")} then",
                    "\t\tseen[code]=true",
                    "\t\ttable.insert(t,code)",
                    "\t\ttable.insert(t,OPCODE_ISCODE)",
                    "\t\tif #t>2 then table.insert(t,OPCODE_OR) end",
                    "\tend",
                    "end",
                ]);
            }
            else
            {
                if (what == "monster_name") lines.AddRange(["table.insert(t,TYPE_MONSTER)", "table.insert(t,OPCODE_ISTYPE)"]);
                lines.AddRange(
                [
                    "for code,_ in pairs(used) do",
                    "\ttable.insert(t,code)",
                    "\ttable.insert(t,OPCODE_ISCODE)",
                    "\ttable.insert(t,OPCODE_NOT)",
                    "\tif #t>3 then table.insert(t,OPCODE_AND) end",
                    "end",
                ]);
            }
            lines.Add("return t");
            var names = ctx.Script.AddFunction("declnames", "e,tp", lines);
            if (limited) e.TargetChecks.Add($"#{names}(e,tp)>0");
            var t = e.NewLocal("dt");
            e.TargetLines.Add($"local {t}={names}(e,tp)");
            announce = $"(#{t}>0 and Duel.AnnounceCard(tp,table.unpack({t})) or {announce})";
        }
        e.TargetLines.Add($"Duel.Hint(HINT_SELECTMSG,tp,{hint})");
        e.TargetLines.Add($"local {v}={announce}");
        e.TargetLines.Add($"Duel.SetTargetParam({v})");
        // Remembered for the turn, by player: "that has not been declared for this card this turn".
        if (fresh)
            e.TargetLines.AddRange(
            [
                "s.ccdecl=s.ccdecl or {}",
                "s.ccdecl[tp]=s.ccdecl[tp] or {}",
                "s.ccdecl[tp][Duel.GetTurnCount()]=s.ccdecl[tp][Duel.GetTurnCount()] or {}",
                $"s.ccdecl[tp][Duel.GetTurnCount()][{v}]=true",
            ]);
        if (what is "name" or "monster_name") e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_ANNOUNCE,nil,0,tp,0)");
        Chance.Declare(e, KindOf(what));
    }
}

/// <summary>
/// "Cards with that name, and their effects, cannot be used" (Psi-Blocker, Prohibition), "your opponent cannot activate
/// cards, or the effects of cards, with that original name" (Sales Ban), "neither player can Special Summon monsters with
/// that original name" (The Black Goat Laughs): a lock on the card name the effect declared.
/// </summary>
public sealed class DeclaredLockAction : ActionBlock
{
    public const string BlockId = "do.declared_lock";
    public override string Id => BlockId;
    public override string Group => "Chance";
    public override string Name => "Cards with the declared name cannot be used / activated / Summoned";
    public override string Sentence => "{who} {what} {duration}";
    public override string Help => "Needs a \"Declare 1 card name\" block before it. \"While this card is face-up\" is for a Continuous Spell/Trap's activation, " +
                                   "like Prohibition: cards already on the field are not affected.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "What",
            new("used", "cards with that name, and their effects, cannot be used"),
            new("activate", "activate cards, or the effects of cards, with that original name", "cannot activate cards or effects with that name"),
            new("special", "Special Summon monsters with that original name", "cannot Special Summon monsters with that name"),
            new("summon", "Normal or Special Summon monsters with that original name", "cannot Normal or Special Summon monsters with that name")),
        ParamSpec.Choice("who", "Who", Locks.Who).ShowIf(b => b.Values.GetValueOrDefault("what", "used") != "used"),
        ParamSpec.Choice("duration", "For",
            new("turn", "for the rest of this turn"), new("opp_next", "until the end of your opponent's next turn"),
            new("face_up", "while this card is face-up", "while this card is face-up (Prohibition)")),
    ];

    public override string Describe(BlockArgs args)
    {
        var what = args.Choice("what");
        var duration = args.Choice("duration");
        if (what == "used")
            return duration switch
            {
                "face_up" => "cards with that name, and their effects, cannot be used. Cards already on the field are not affected (including face-down cards)",
                "opp_next" => "cards with that name, and their effects, cannot be used until the end of your opponent's next turn",
                _ => "cards with that name, and their effects, cannot be used for the rest of this turn",
            };
        var subject = args.Choice("who") switch { "both" => "neither player can", "opponent" => "your opponent cannot", _ => "you cannot" };
        var lead = duration switch { "turn" => "for the rest of this turn, ", "opp_next" => "until the end of your opponent's next turn, ", _ => "" };
        return $"{lead}{subject} {args.Describe("what")}";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (Chance.Declared(e) != "name") { e.Error($"\"{Name}\" needs a \"Declare 1 card name\" block before it."); return; }
        var what = ctx.Args.Choice("what");
        var duration = ctx.Args.Choice("duration");
        var (self, opp) = ctx.Args.Choice("who") switch { "both" => ("1", "1"), "opponent" => ("0", "1"), _ => ("1", "0") };
        var range = e.Range ?? Zones.Resolve("auto", ctx.Card);
        var faceUp = duration == "face_up";
        var reset = duration switch { "opp_next" => "RESET_PHASE+PHASE_END+RESET_OPPO_TURN", "face_up" => "RESET_EVENT+RESETS_STANDARD", _ => "RESET_PHASE+PHASE_END" };
        var ac = e.NewLocal("ac");
        var lines = new List<string> { $"local {ac}=Duel.GetChainInfo(0,CHAININFO_TARGET_PARAM)" };
        if (what == "used") lines.Add($"c:SetHint(CHINT_CARD,{ac})");
        var codes = what switch { "used" => ["EFFECT_FORBIDDEN"], "activate" => ["EFFECT_CANNOT_ACTIVATE"], "special" => ["EFFECT_CANNOT_SPECIAL_SUMMON"], _ => new[] { "EFFECT_CANNOT_SUMMON", "EFFECT_CANNOT_SPECIAL_SUMMON" } };
        var n = 0;
        foreach (var code in codes)
        {
            var le = e.NewLocal("le");
            n++;
            lines.Add($"local {le}=Effect.CreateEffect(c)");
            lines.Add($"{le}:SetType(EFFECT_TYPE_FIELD)");
            if (what == "used")
            {
                lines.Add($"{le}:SetProperty(EFFECT_FLAG_SET_AVAILABLE+EFFECT_FLAG_IGNORE_IMMUNE)");
                lines.Add($"{le}:SetCode(EFFECT_FORBIDDEN)");
                if (faceUp) lines.Add($"{le}:SetRange({range})");
                lines.Add($"{le}:SetTargetRange(0xff,0xff)");
                // While face-up, as Prohibition: cards already on the field when it was activated are not affected.
                var ban = ctx.Script.AddFunction("bantg", "e,c", [faceUp
                    ? "return c:IsOriginalCodeRule(e:GetLabel()) and (not c:IsOnField() or c:GetRealFieldID()>e:GetFieldID())"
                    : "return c:IsOriginalCodeRule(e:GetLabel())"]);
                lines.Add($"{le}:SetTarget({ban})");
            }
            else
            {
                lines.Add($"{le}:SetCode({code})");
                lines.Add($"{le}:SetProperty(EFFECT_FLAG_PLAYER_TARGET)");
                if (faceUp) lines.Add($"{le}:SetRange({range})");
                lines.Add($"{le}:SetTargetRange({self},{opp})");
                if (what == "activate")
                    lines.Add($"{le}:SetValue({ctx.Script.AddFunction("aclimit", "e,re,tp", ["return re:GetHandler():IsOriginalCodeRule(e:GetLabel())"])})");
                else
                    lines.Add($"{le}:SetTarget({ctx.Script.AddFunction("sumlimit", "e,c", ["return c:IsOriginalCodeRule(e:GetLabel())"])})");
            }
            lines.Add($"{le}:SetLabel({ac})");
            lines.Add($"{le}:SetReset({reset})");
            lines.Add(faceUp ? $"c:RegisterEffect({le})" : $"Duel.RegisterEffect({le},tp)");
        }
        if (what == "used" && faceUp) lines.Add("Duel.AdjustInstantly(c)");
        var r = e.NewLocal("r");
        lines.Add($"local {r}=1");
        e.AddStep(lines, r, ctx.Link);
    }
}

// ---------------------------------------------------------------- Toon monsters

/// <summary>
/// The rules Toon monsters print, one per block: "Cannot attack the turn it is Summoned", "If "Toon World" on the field
/// is destroyed, destroy this card", "While you control "Toon World" and your opponent controls no Toon monsters, this
/// card can attack your opponent directly", and the older Toons' wording. Each is written as the official Toons write it.
/// </summary>
public sealed class ToonRulesPassive : PassiveBlock
{
    public const string BlockId = "passive.toon";
    public const long ToonWorld = 15259703;
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "Toon monster rule (Toon World)";
    public override string Sentence => "{rule}";
    public override string Help => "Add one effect per rule a Toon monster prints, each with \"While this card is face-up\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("rule", "Rule",
            new("summon_turn", "Cannot attack the turn it is Summoned"),
            new("spsummon_turn", "Cannot attack the turn it is Special Summoned"),
            new("world_destroyed", "If \"Toon World\" on the field is destroyed, destroy this card"),
            new("direct_world", "While you control \"Toon World\" and your opponent controls no Toon monsters, this card can attack your opponent directly"),
            new("direct_unless", "Can attack your opponent directly, unless they control a Toon monster, in which case this card must target a Toon monster for its attacks"),
            new("attack_cost", "You must pay 500 LP to declare an attack with this monster")),
    ];

    public override string Describe(BlockArgs args) => args.Describe("rule");

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var e = ctx.Effect;
        if (e.Conditions.Count > 0) { e.Error("A Toon monster rule takes no IF blocks: it says when it applies itself."); return; }
        var s = ctx.Script;
        var world = ToonWorld.ToString(CultureInfo.InvariantCulture);
        string Toons() => s.AddFunction("toonfilter", "c", ["return c:IsFaceup() and c:IsType(TYPE_TOON)"]);
        string OppToons() => s.AddFunction("tooncon", "e", [$"return Duel.IsExistingMatchingCard({Toons()},e:GetHandlerPlayer(),0,LOCATION_MZONE,1,nil)"]);
        switch (ctx.Args.Choice("rule"))
        {
            case "summon_turn" or "spsummon_turn":
            {
                var op = s.AddFunction("atklimit", "e,tp,eg,ep,ev,re,r,rp",
                [
                    "local e1=Effect.CreateEffect(e:GetHandler())",
                    "e1:SetType(EFFECT_TYPE_SINGLE)",
                    "e1:SetCode(EFFECT_CANNOT_ATTACK)",
                    "e1:SetReset(RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END)",
                    "e:GetHandler():RegisterEffect(e1)",
                ]);
                var codes = ctx.Args.Choice("rule") == "spsummon_turn"
                    ? new[] { "EVENT_SPSUMMON_SUCCESS" } : ["EVENT_SUMMON_SUCCESS", "EVENT_SPSUMMON_SUCCESS", "EVENT_FLIP_SUMMON_SUCCESS"];
                foreach (var code in codes)
                    e.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_SINGLE+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetCode({code})", $"{v}:SetOperation({op})"]);
                break;
            }
            case "world_destroyed":
            {
                var filter = s.AddFunction("worldfilter", "c",
                    [$"return c:IsReason(REASON_DESTROY) and c:IsPreviousPosition(POS_FACEUP) and c:GetPreviousCodeOnField()=={world} and c:IsPreviousLocation(LOCATION_ONFIELD)"]);
                var con = s.AddFunction("worldcon", "e,tp,eg,ep,ev,re,r,rp", [$"return eg:IsExists({filter},1,nil)"]);
                var op = s.AddFunction("worldop", "e,tp,eg,ep,ev,re,r,rp", ["Duel.Destroy(e:GetHandler(),REASON_EFFECT)"]);
                e.AddPassive(v =>
                [
                    $"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetRange(LOCATION_MZONE)", $"{v}:SetCode(EVENT_LEAVE_FIELD)",
                    $"{v}:SetCondition({con})", $"{v}:SetOperation({op})",
                ]);
                break;
            }
            case "direct_world":
            {
                var worldUp = s.AddFunction("worldup", "c", [$"return c:IsFaceup() and c:IsCode({world})"]);
                var con = s.AddFunction("dircon", "e",
                [
                    $"return Duel.IsExistingMatchingCard({worldUp},e:GetHandlerPlayer(),LOCATION_ONFIELD,0,1,nil)",
                    $"\tand not Duel.IsExistingMatchingCard({Toons()},e:GetHandlerPlayer(),0,LOCATION_MZONE,1,nil)",
                ]);
                e.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_SINGLE)", $"{v}:SetCode(EFFECT_DIRECT_ATTACK)", $"{v}:SetCondition({con})"]);
                break;
            }
            case "direct_unless":
            {
                var con = OppToons();
                var dir = s.AddFunction("dircon", "e", [$"return not {con}(e)"]);
                var limit = s.AddFunction("atlimit", "e,c", ["return not c:IsType(TYPE_TOON) or c:IsFacedown()"]);
                e.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_SINGLE)", $"{v}:SetCode(EFFECT_DIRECT_ATTACK)", $"{v}:SetCondition({dir})"]);
                e.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_SINGLE)", $"{v}:SetCode(EFFECT_CANNOT_SELECT_BATTLE_TARGET)", $"{v}:SetCondition({con})", $"{v}:SetValue({limit})"]);
                e.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_SINGLE)", $"{v}:SetCode(EFFECT_CANNOT_DIRECT_ATTACK)", $"{v}:SetCondition({con})"]);
                break;
            }
            default:
            {
                var cost = s.AddFunction("atcost", "e,c,tp", ["return Duel.CheckLPCost(tp,500)"]);
                var op = s.AddFunction("atop", "e,tp,eg,ep,ev,re,r,rp", ["Duel.PayLPCost(tp,500)"]);
                e.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_SINGLE)", $"{v}:SetCode(EFFECT_ATTACK_COST)", $"{v}:SetCost({cost})", $"{v}:SetOperation({op})"]);
                break;
            }
        }
    }
}
