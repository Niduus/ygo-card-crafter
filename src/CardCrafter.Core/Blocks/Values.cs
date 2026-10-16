using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// Live numbers a block can use as an amount: the difference in LP, a player's LP, how many cards a picker finds, their
/// total ATK, the Level or materials of the monster affected, the damage taken this turn... Used "equal to" the
/// number, as half or double of it, or "for each".
/// </summary>
public static class ValueSources
{
    /// <summary>The picker that says which cards are counted, summed or compared.</summary>
    public const string CardsKey = "of";

    public static readonly ChoiceOption[] Sources =
    [
        new("lp_diff", "the difference between both players' LP"),
        new("lp_below", "how much lower your LP is than your opponent's"),
        new("lp_above", "how much higher your LP is than your opponent's"),
        new("your_lp", "your LP"),
        new("opp_lp", "your opponent's LP"),
        new("count", "the number of these cards:", "the number of cards picked below"),
        new("different_attr", "the number of different Attributes among these cards:", "different Attributes among the cards picked below"),
        new("different_race", "the number of different Types among these cards:", "different Types among the cards picked below"),
        new("different_name", "the number of different card names among these cards:", "different names among the cards picked below"),
        new("sum_atk", "the total ATK of these cards:", "total ATK of the cards picked below"),
        new("sum_def", "the total DEF of these cards:", "total DEF of the cards picked below"),
        new("sum_level", "the total Levels of these cards:", "total Levels of the cards picked below"),
        new("its_level", "its Level"),
        new("its_rank", "its Rank"),
        new("its_link", "its Link Rating"),
        new("its_materials", "the number of materials attached to it"),
        new("its_counters", "the number of counters on it"),
        new("its_equips", "the number of Equip Cards equipped to it"),
        new("its_linked", "the number of monsters it points to"),
        new("its_used", "the number of materials used for its Summon"),
        new("its_orig_atk", "its original ATK"),
        new("its_orig_def", "its original DEF"),
        new("damage_turn", "the damage you took this turn"),
        new("opp_damage_turn", "the damage your opponent took this turn"),
        new("gained_turn", "the LP you gained this turn"),
        new("event", "that amount (the damage or LP that started this effect)"),
    ];

    public static readonly ChoiceOption[] Hows =
    [
        new("equal", "equal to"),
        new("half", "equal to half of"),
        new("double", "equal to double"),
        new("per", "for each (amount below)"),
    ];

    public static bool UsesCards(string source) => source is "count" or "different_attr" or "different_race" or "different_name" or "sum_atk" or "sum_def" or "sum_level";

    /// <summary>The value's own words for "for each": "card in your hand", "of its Levels", "counter on it".</summary>
    private static string Each(string source, CardFilter cards, CardPack? pack, string? ownName) => source switch
    {
        "count" => CardCounts.Group(cards, pack, plural: false),
        "different_attr" => $"different Attribute among the {CardCounts.Group(cards, pack)}",
        "different_race" => $"different Type among the {CardCounts.Group(cards, pack)}",
        "different_name" => $"card with a different name among the {CardCounts.Group(cards, pack)}",
        "its_level" => "of its Levels",
        "its_rank" => "of its Ranks",
        "its_link" => "point of its Link Rating",
        "its_materials" => "material attached to it",
        "its_counters" => "counter on it",
        "its_equips" => "Equip Card equipped to it",
        "its_linked" => "monster it points to",
        "its_used" => "material used for its Summon",
        _ => "1 of " + Of(source, cards, pack),
    };

    /// <summary>The value itself: "the number of cards in your hand", "the total ATK of monsters you control".</summary>
    public static string Of(string source, CardFilter cards, CardPack? pack) => source switch
    {
        "count" => $"the number of {CardCounts.Group(cards, pack)}",
        "different_attr" => $"the number of different Attributes among the {CardCounts.Group(cards, pack)}",
        "different_race" => $"the number of different Types among the {CardCounts.Group(cards, pack)}",
        "different_name" => $"the number of different card names among the {CardCounts.Group(cards, pack)}",
        "sum_atk" => $"the total ATK of the {CardCounts.Group(cards, pack)}",
        "sum_def" => $"the total DEF of the {CardCounts.Group(cards, pack)}",
        "sum_level" => $"the total Levels of the {CardCounts.Group(cards, pack)}",
        "event" => "that amount",
        var s => Sources.First(o => o.Value == s).Label,
    };

    /// <summary>"equal to the difference between both players' LP", "300 for each card in your hand", "equal to half its original ATK".</summary>
    public static string Phrase(BlockArgs args, string unit)
    {
        var source = args.Choice("value");
        var cards = args.Cards(CardsKey);
        var of = Of(source, cards, args.Pack);
        return args.Choice("how") switch
        {
            "per" => $"{args.Int("amount").ToString(CultureInfo.InvariantCulture)}{unit} for each {Each(source, cards, args.Pack, args.CardName)}",
            "half" => $"{unit.Trim()} equal to half {(of.StartsWith("the ", StringComparison.Ordinal) ? of : of)}".Trim(),
            "double" => $"{unit.Trim()} equal to double {of}".Trim(),
            _ => $"{unit.Trim()} equal to {of}".Trim(),
        };
    }

    /// <summary>
    /// The Lua number: <paramref name="card"/> is the monster "its" means and <paramref name="p"/> the player "you" means.
    /// <paramref name="ev"/> is false inside a lasting effect, which has no event to take an amount from.
    /// </summary>
    public static string Lua(BlockContext ctx, string card, string p, bool ev = true)
    {
        var source = ctx.Args.Choice("value");
        string Group()
        {
            var f = ctx.Script.Filter(ctx.Args.Cards(CardsKey));
            return $"Duel.GetMatchingGroup({f.Function},{p},{f.Self},{f.Opp},{(f.Exclude == "nil" ? "nil" : "e:GetHandler()")},e,{p})";
        }
        var raw = source switch
        {
            "lp_diff" => $"math.abs(Duel.GetLP({p})-Duel.GetLP(1-{p}))",
            "lp_below" => $"math.max(0,Duel.GetLP(1-{p})-Duel.GetLP({p}))",
            "lp_above" => $"math.max(0,Duel.GetLP({p})-Duel.GetLP(1-{p}))",
            "your_lp" => $"Duel.GetLP({p})",
            "opp_lp" => $"Duel.GetLP(1-{p})",
            "count" => $"{Group()}:GetCount()",
            "different_attr" => $"{Group()}:GetClassCount(Card.GetAttribute)",
            "different_race" => $"{Group()}:GetClassCount(Card.GetRace)",
            "different_name" => $"{Group()}:GetClassCount(Card.GetCode)",
            "sum_atk" => $"{Group()}:GetSum(Card.GetAttack)",
            "sum_def" => $"{Group()}:GetSum(Card.GetDefense)",
            "sum_level" => $"{Group()}:GetSum(Card.GetLevel)",
            "its_level" => $"{card}:GetLevel()",
            "its_rank" => $"{card}:GetRank()",
            "its_link" => $"{card}:GetLink()",
            "its_materials" => $"{card}:GetOverlayCount()",
            "its_counters" => $"{card}:GetCounter(0)",
            "its_equips" => $"{card}:GetEquipCount()",
            "its_linked" => $"{card}:GetLinkedGroupCount()",
            "its_used" => $"{card}:GetMaterialCount()",
            "its_orig_atk" => $"math.max(0,{card}:GetBaseAttack())",
            "its_orig_def" => $"math.max(0,{card}:GetBaseDefense())",
            "damage_turn" => LifeWatch.Total(ctx, p, "damage"),
            "opp_damage_turn" => LifeWatch.Total(ctx, $"1-{p}", "damage"),
            "gained_turn" => LifeWatch.Total(ctx, p, "gained"),
            "event" when ev => "ev",
            _ => "0",
        };
        if (source == "event" && !ev) ctx.Effect.Error("\"That amount\" only exists while an effect resolves after damage or an LP change; a lasting effect can't use it.");
        return ctx.Args.Choice("how") switch
        {
            "half" => $"math.floor(({raw})/2)",
            "double" => $"({raw})*2",
            "per" => $"({raw})*{ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture)}",
            _ => raw,
        };
    }

    /// <summary>The value's parameters, for blocks that use one.</summary>
    /// <param name="withEvent">Offer "that amount": only effects that resolve after damage or an LP change have one.</param>
    public static IEnumerable<ParamSpec> Params(int amountDefault = 300, bool withEvent = true) =>
    [
        ParamSpec.Choice("how", "How much", Hows),
        ParamSpec.Int("amount", "Amount for each", amountDefault, 1, 99999).ShowIf(b => b.Values.GetValueOrDefault("how") == "per"),
        ParamSpec.Choice("value", "Of", withEvent ? Sources : Sources.Where(o => o.Value != "event").ToArray()),
        ParamSpec.Cards(CardsKey, "Which cards (for counts and totals)", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand })
            .ShowIf(b => UsesCards(b.Values.GetValueOrDefault("value", "lp_diff"))),
    ];

    /// <summary>The engine codes for gaining, losing or becoming a stat.</summary>
    public static string[] Codes(string stat, string dir) => (stat, dir) switch
    {
        ("def", "becomes") => ["EFFECT_SET_DEFENSE_FINAL"],
        ("both", "becomes") => ["EFFECT_SET_ATTACK_FINAL", "EFFECT_SET_DEFENSE_FINAL"],
        (_, "becomes") when stat != "level" => ["EFFECT_SET_ATTACK_FINAL"],
        ("level", "becomes") => ["EFFECT_CHANGE_LEVEL"],
        ("level", _) => ["EFFECT_UPDATE_LEVEL"],
        ("def", _) => ["EFFECT_UPDATE_DEFENSE"],
        ("both", _) => ["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"],
        _ => ["EFFECT_UPDATE_ATTACK"],
    };

    public static readonly ChoiceOption[] Dirs = [new("gain", "gain(s)"), new("lose", "lose(s)"), new("becomes", "becomes")];
    public static readonly ChoiceOption[] Stats = [new("atk", "ATK"), new("def", "DEF"), new("both", "ATK/DEF"), new("level", "Level")];

    /// <summary>"this card gains ATK equal to ...", "the Level of monsters you control becomes equal to ...".</summary>
    public static string Sentence(BlockArgs args, string subject, bool plural)
    {
        var stat = args.Choice("stat");
        var dir = args.Choice("dir");
        var unit = stat == "level" ? "" : " " + args.Describe("stat");
        var how = Phrase(args, stat == "level" || dir == "becomes" ? "" : unit);
        // "Monsters you control gain 100 ATK for each of their Levels", "... for each material attached to them".
        if (plural)
            how = System.Text.RegularExpressions.Regex.Replace(how, @"\bits\b", "their")
                .Replace(" to it", " to them", StringComparison.Ordinal).Replace(" on it", " on them", StringComparison.Ordinal)
                .Replace(" it points to", " they point to", StringComparison.Ordinal);
        if (stat == "level")
        {
            var whose = subject switch { "this card" => "this card's Level", "it" => "its Level", "they" => "their Levels", _ => $"the Levels of {subject}" };
            var verb = dir switch { "lose" => plural ? "decrease by" : "decreases by", "becomes" => plural ? "become" : "becomes", _ => plural ? "increase by" : "increases by" };
            return $"{whose} {verb} {how.Replace("equal to ", dir == "becomes" ? "equal to " : "", StringComparison.Ordinal)}";
        }
        if (dir == "becomes")
        {
            var whose = subject switch { "this card" => $"this card's{unit}", "it" => $"its{unit}", "they" => $"their{unit}", _ => $"the{unit} of {subject}" };
            return $"{whose} {(plural ? "become" : "becomes")} {how}";
        }
        var gain = dir == "lose" ? (plural ? "lose" : "loses") : (plural ? "gain" : "gains");
        // "gains ATK equal to ..." but "gains 300 ATK for each ...".
        return args.Choice("how") == "per" ? $"{subject} {gain} {how}" : $"{subject} {gain}{unit} {how.Replace(unit.Trim() + " equal", "equal", StringComparison.Ordinal)}";
    }
}

/// <summary>
/// "While this card is face-up, it gains ATK equal to the difference between both players' LP", "monsters you control gain
/// 300 ATK for each card in your hand", "this card's Level becomes equal to the number of Spells in your GY". The amount
/// is worked out again whenever it changes.
/// </summary>
public sealed class StatsFromValuePassive : PassiveBlock
{
    public const string BlockId = "passive.stats_value";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Gains ATK/DEF/Level equal to a live value (LP difference, cards, materials...)";
    public override string Sentence => "{cards} {dir} {stat} {how} {amount} {value} {of}";
    public override string Help => "The amount changes as the game does: LP, cards counted by the picker, the monster's own Level or materials, damage taken this turn...";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { ThisCard = true, Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone, thisCard: true),
        ParamSpec.Choice("dir", "Gains / loses / becomes", ValueSources.Dirs),
        ParamSpec.Choice("stat", "Stat", ValueSources.Stats),
        .. ValueSources.Params(withEvent: false),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        return f.ThisCard
            ? ValueSources.Sentence(args, "this card", false)
            : ValueSources.Sentence(args, FilterText.Describe(f, args.Pack, plural: true, preposition: ""), true);
    }

    public override void Compile(BlockContext ctx)
    {
        var lose = ctx.Args.Choice("dir") == "lose";
        var value = ctx.Script.AddFunction("liveval", "e,c", [$"return {(lose ? "-" : "")}({ValueSources.Lua(ctx, "c", "e:GetHandlerPlayer()", ev: false)})"]);
        var codes = ValueSources.Codes(ctx.Args.Choice("stat"), ctx.Args.Choice("dir"));
        var range = RangeOf(ctx);
        var filter = ctx.Args.Cards("cards").Clone();
        if (filter.ThisCard)
        {
            Zones.RequireMonster(ctx, Name);
            foreach (var code in codes) ctx.Effect.AddPassive(v => SelfEffect(v, range, code, value));
            return;
        }
        filter.Zones = Zone.MonsterZone;
        var f = ctx.Script.Filter(filter);
        var target = ctx.Script.AddFunction("livetg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")]);
        foreach (var code in codes)
            ctx.Effect.AddPassive(v =>
            [
                $"{v}:SetType(EFFECT_TYPE_FIELD)",
                $"{v}:SetCode({code})",
                $"{v}:SetRange({range})",
                $"{v}:SetTargetRange({f.Self},{f.Opp})",
                $"{v}:SetTarget({target})",
                $"{v}:SetValue({value})",
            ]);
    }
}

/// <summary>
/// "Target 1 monster; it gains ATK equal to half its original ATK until the end of this turn", "this card gains 500 ATK
/// for each card in your opponent's GY": worked out when the effect resolves.
/// </summary>
public sealed class StatsFromValueAction : CardGroupAction
{
    public const string BlockId = "do.stats_value";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Monsters gain ATK/DEF/Level equal to a value (LP difference, cards, its Level...)";
    public override string Sentence => "{count} {cards} {target} {dir} {stat} {how} {amount} {value} {of} {duration}";
    protected override string Category => "CATEGORY_ATKCHANGE";
    protected override string Hint => "HINTMSG_FACEUP";
    protected override string TargetedWording => "{0}";
    protected override bool AllowsThisCard => true;
    protected override bool AllowsAll => true;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ModeParam,
        ParamSpec.Cards("cards", "Which monsters", DefaultCards, Zone.MonsterZone, thisCard: true),
        ParamSpec.Flag("target", "(targeting)", false),
        ParamSpec.Choice("dir", "Gains / loses / becomes", ValueSources.Dirs),
        ParamSpec.Choice("stat", "Stat", ValueSources.Stats),
        .. ValueSources.Params(),
        ParamSpec.Choice("duration", "Until", Resets.Duration),
    ];

    private static string Tail(BlockArgs args) => args.Describe("duration") is { Length: > 0 } d ? " " + d : "";

    public override string Describe(BlockArgs args)
    {
        if (args.IsThisCard) return ValueSources.Sentence(args, "this card", false) + Tail(args);
        var all = Param("mode") is not null && args.Choice("mode") == "all";
        var n = args.Int("count");
        var subject = all ? $"all {args.Describe("cards")}" : Some(args);
        return ValueSources.Sentence(args, subject, all || n != 1) + Tail(args) + args.ExceptText();
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) =>
        ValueSources.Sentence(args, pronoun == "it" ? "it" : "they", !single) + Tail(args);

    protected override string? AbleCheck(BlockContext ctx) => "c:IsFaceup()";

    protected override string Apply(string g, BlockContext ctx)
    {
        var lose = ctx.Args.Choice("dir") == "lose";
        var codes = ValueSources.Codes(ctx.Args.Choice("stat"), ctx.Args.Choice("dir"));
        var value = ValueSources.Lua(ctx, "tc", "tp");
        // What this card gives itself is lost when its effects are negated, like "This card gains or loses ATK/DEF".
        var reset = Resets.Of(ctx) + (ctx.Args.Cards("cards").ThisCard ? "+RESET_DISABLE" : "");
        var lines = new List<string> { "local n=0", "for tc in aux.Next(g) do", "\tif tc:IsFaceup() then", $"\t\tlocal v={value}" };
        foreach (var code in codes)
        {
            lines.Add("\t\tlocal se=Effect.CreateEffect(e:GetHandler())");
            lines.Add("\t\tse:SetType(EFFECT_TYPE_SINGLE)");
            lines.Add($"\t\tse:SetCode({code})");
            lines.Add($"\t\tse:SetValue({(lose ? "-v" : "v")})");
            lines.Add($"\t\tse:SetReset({reset})");
            lines.Add("\t\ttc:RegisterEffect(se)");
        }
        lines.AddRange(["\t\tn=n+1", "\tend", "end", "return n"]);
        var fn = ctx.Script.AddFunction("valstats", "g,e,tp,ev", lines);
        return $"{fn}({g},e,tp,ev)";
    }
}

/// <summary>"Inflict damage to your opponent equal to the number of cards in their hand x 300", "gain LP equal to the difference in LP".</summary>
public sealed class LifeFromValueAction : ActionBlock
{
    public const string BlockId = "do.lp_value";
    public override string Id => BlockId;
    public override string Group => "Life Points";
    public override string Name => "Inflict damage / gain LP equal to a value (cards, LP, ATK...)";
    public override string Sentence => "{what} {who} {how} {amount} {value} {of}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "Do", new("damage", "Inflict damage to"), new("gain", "Gain LP:")),
        ParamSpec.Choice("who", "Who", new("opponent", "your opponent"), new("you", "you"), new("both", "both players")),
        .. ValueSources.Params(),
    ];

    public override string Describe(BlockArgs args)
    {
        var amount = ValueSources.Phrase(args, "");
        // "gain 500 LP for each ...", not "gain LP 500 for each ...".
        if (args.Choice("what") == "gain" && args.Choice("how") == "per")
        {
            var each = amount.Replace(" for each", " LP for each", StringComparison.Ordinal);
            return args.Choice("who") switch { "opponent" => $"your opponent gains {each}", "both" => $"both players gain {each}", _ => $"gain {each}" };
        }
        if (args.Choice("what") == "gain")
            return args.Choice("who") switch
            {
                "opponent" => $"your opponent gains LP {amount}",
                "both" => $"both players gain LP {amount}",
                _ => $"gain LP {amount}",
            };
        var who = args.Choice("who") switch { "you" => "you", "both" => "both players", _ => "your opponent" };
        return args.Choice("how") == "per" ? $"inflict {amount.Replace(" for each", " damage to " + who + " for each", StringComparison.Ordinal)}" : $"inflict damage to {who} {amount}";
    }

    public override void Compile(BlockContext ctx)
    {
        var damage = ctx.Args.Choice("what") == "damage";
        var who = ctx.Args.Choice("who");
        var players = who switch { "you" => new[] { "tp" }, "both" => ["tp", "1-tp"], _ => ["1-tp"] };
        ctx.Effect.AddCategory(damage ? "CATEGORY_DAMAGE" : "CATEGORY_RECOVER");
        var info = who == "both" ? "PLAYER_ALL" : players[0];
        ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,{(damage ? "CATEGORY_DAMAGE" : "CATEGORY_RECOVER")},nil,0,{info},0)");
        var value = ValueSources.Lua(ctx, "e:GetHandler()", "tp");
        // Nothing to do when the amount is 0 ("for each card in your hand" with an empty hand).
        if (ctx.Args.Choice("value") != "event") ctx.Effect.TargetChecks.Add($"{value}>0");
        var r = ctx.Effect.NewLocal("r");
        var body = new List<string> { $"local {r}=0", $"local lv={value}", "if lv>0 then" };
        foreach (var p in players) body.Add($"\t{r}={r}+Duel.{(damage ? "Damage" : "Recover")}({p},lv,REASON_EFFECT)");
        body.Add("end");
        ctx.Effect.AddStep(body, r, ctx.Link);
    }
}
