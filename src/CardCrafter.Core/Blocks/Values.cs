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
        new("sum_counters", "the number of counters on these cards:", "counters on the cards picked below"),
        new("its_level", "its Level"),
        new("its_rank", "its Rank"),
        new("its_link", "its Link Rating"),
        new("its_materials", "the number of materials attached to it"),
        new("its_counters", "the number of counters on it", "the number of counters on it (pick which below)"),
        new("its_equips", "the number of Equip Cards equipped to it"),
        new("its_linked", "the number of monsters it points to"),
        new("its_used", "the number of materials used for its Summon"),
        new("its_orig_atk", "its original ATK"),
        new("its_orig_def", "its original DEF"),
        new("its_orig_higher", "its original ATK or DEF (whichever is higher)"),
        // The monster(s) the DO step before affected: "destroy it, and if you do, ... half of that monster's original ATK".
        new("that_atk", "that monster's ATK", "that monster's ATK (as it was on the field; the card the step before affected)"),
        new("that_orig_atk", "that monster's original ATK", "that monster's original ATK (the card the step before affected)"),
        new("that_orig_def", "that monster's original DEF", "that monster's original DEF (the card the step before affected)"),
        new("that_orig_higher", "that monster's original ATK or DEF (whichever is higher)", "that monster's original ATK or DEF, whichever is higher (the card the step before affected)"),
        new("that_level", "that monster's Level", "that monster's Level (the card the step before affected)"),
        new("damage_turn", "the damage you took this turn"),
        new("opp_damage_turn", "the damage your opponent took this turn"),
        new("gained_turn", "the LP you gained this turn"),
        new("event", "that amount (the damage or LP that started this effect)"),
        new("roll", "the result", "the result of the die roll before it"),
        new("heads", "the number of heads", "the number of heads of the coin toss before it"),
        // What this card remembers, and the LP paid for the cost (Memory.cs).
        new("lp_paid", "the LP paid", "the LP paid for the cost (pay LP in multiples)"),
        // Lyrilusc - Promenade Thrush: "300 ATK for each material detached" (a cost that detaches, discards or banishes any number).
        new("cost_count", "the number of cards used for the cost", "the number of cards used for the cost (detached, discarded, banished...)"),
        new("mem", "the remembered number", "the number this card remembers (Memory below)"),
        new("mem_count", "the number of remembered cards", "the number of remembered cards (Memory below)"),
        new("mem_atk", "the remembered monster's ATK", "the remembered monster's ATK (Memory below)"),
        new("mem_orig_atk", "the remembered monster's original ATK", "the remembered monster's original ATK (Memory below)"),
        new("mem_level", "the remembered monster's Level", "the remembered monster's Level (Memory below)"),
    ];

    /// <summary>Values known only while the effect resolves: the amount that started it, a die roll's result, a toss's heads, the LP paid.</summary>
    public static bool AtResolution(string source) => source is "event" or "roll" or "heads" or "lp_paid" or "cost_count" || Affected(source);

    /// <summary>Values about the card(s) the DO step before affected ("that monster's original ATK").</summary>
    public static bool Affected(string source) => source.StartsWith("that_", StringComparison.Ordinal);

    /// <summary>Values read from what this card remembers, which name their Memory in the "mem" blank.</summary>
    public static bool Remembered(string source) => source.StartsWith("mem", StringComparison.Ordinal);

    public static readonly ChoiceOption[] Hows =
    [
        new("equal", "equal to"),
        new("half", "equal to half of"),
        new("double", "equal to double"),
        new("per", "x / for each (amount below)"),
    ];

    public static bool UsesCards(string source) => source is "count" or "different_attr" or "different_race" or "different_name" or "sum_atk" or "sum_def" or "sum_level" or "sum_counters";

    /// <summary>Values that count counters, which say which counter.</summary>
    public static bool UsesCounter(string source) => source is "its_counters" or "sum_counters";

    /// <summary>Values about the monster itself ("its Level", "materials attached to it").</summary>
    public static bool IsOwn(string source) => source.StartsWith("its_", StringComparison.Ordinal);

    /// <summary>
    /// Values that count things, which read "300 ATK for each card in your hand". The others (LP, totals, Levels, original
    /// ATK, damage) read "ATK equal to its Level x 300" instead.
    /// </summary>
    public static bool IsCountable(string source) => source is "count" or "different_attr" or "different_race" or "different_name"
        or "its_materials" or "its_counters" or "its_equips" or "its_linked" or "its_used" or "sum_counters" or "mem_count" or "cost_count";

    /// <summary>"its" for "it", "this card's" for "this card".</summary>
    private static string Poss(string owner) => owner == "it" ? "its" : owner + "'s";

    /// <summary>The value's own words for "for each": "card in your hand", "material attached to it", "counter on it".</summary>
    /// <param name="counter">The counter counted: "Spell Counter", or "counter" for any.</param>
    private static string Each(string source, CardFilter cards, CardPack? pack, string owner, string counter) => source switch
    {
        "count" => CardCounts.Group(cards, pack, plural: false),
        "different_attr" => $"different Attribute among the {CardCounts.Group(cards, pack)}",
        "different_race" => $"different Type among the {CardCounts.Group(cards, pack)}",
        "different_name" => $"different card name among the {CardCounts.Group(cards, pack)}",
        "its_materials" => $"material attached to {owner}",
        "its_counters" => $"{counter} on {owner}",
        "sum_counters" => $"{counter} on {Counters.Where(cards, pack)}",
        "its_equips" => $"Equip Card equipped to {owner}",
        "its_linked" => $"monster {owner} points to",
        "its_used" => $"material used for {Poss(owner)} Summon",
        "mem_count" => "remembered card",
        _ => Of(source, cards, pack, owner, counter),
    };

    /// <summary>
    /// The value itself: "the number of cards in your hand", "the total ATK of monsters you control", "its Level".
    /// <paramref name="owner"/> is the monster "its" means: "it", or "this card" where nothing else is affected.
    /// </summary>
    public static string Of(string source, CardFilter cards, CardPack? pack, string owner = "it", string counter = "counter") => source switch
    {
        "count" => $"the number of {CardCounts.Group(cards, pack)}",
        "different_attr" => $"the number of different Attributes among the {CardCounts.Group(cards, pack)}",
        "different_race" => $"the number of different Types among the {CardCounts.Group(cards, pack)}",
        "different_name" => $"the number of different card names among the {CardCounts.Group(cards, pack)}",
        "sum_atk" => $"the total ATK of the {CardCounts.Group(cards, pack)}",
        "sum_def" => $"the total DEF of the {CardCounts.Group(cards, pack)}",
        "sum_level" => $"the total Levels of the {CardCounts.Group(cards, pack)}",
        "its_level" => $"{Poss(owner)} Level",
        "its_rank" => $"{Poss(owner)} Rank",
        "its_link" => $"{Poss(owner)} Link Rating",
        "its_materials" => $"the number of materials attached to {owner}",
        "its_counters" => $"the number of {counter}s on {owner}",
        "sum_counters" => $"the number of {counter}s on {Counters.Where(cards, pack)}",
        "its_equips" => $"the number of Equip Cards equipped to {owner}",
        "its_linked" => $"the number of monsters {owner} points to",
        "its_used" => $"the number of materials used for {Poss(owner)} Summon",
        "its_orig_atk" => $"{Poss(owner)} original ATK",
        "its_orig_def" => $"{Poss(owner)} original DEF",
        "its_orig_higher" => $"{Poss(owner)} original ATK or DEF (whichever is higher)",
        "event" => "that amount",
        var s => Sources.First(o => o.Value == s).Label,
    };

    /// <summary>True when the amount reads "300 ... for each card in your hand": a count, used "for each".</summary>
    public static bool ForEach(BlockArgs args) => args.Choice("how") == "per" && IsCountable(args.Choice("value"));

    /// <summary>"card in your hand", for "300 damage for each card in your hand".</summary>
    public static string EachText(BlockArgs args, string owner = "it") =>
        args.Choice("value") == "cost_count" ? CostNoun(args.Effect, plural: false) : Each(args.Choice("value"), args.Cards(CardsKey), args.Pack, owner, Counters.Name(args));

    /// <summary>"material detached", "card discarded": the cards the effect's cost used, for "for each" and "the number of".</summary>
    public static string CostNoun(EffectEvent? effect, bool plural)
    {
        var (one, many) = effect?.Cost.Select(b => b.BlockId).FirstOrDefault() switch
        {
            "cost.detach" => ("material detached", "materials detached"),
            "cost.discard" => ("card discarded", "cards discarded"),
            "cost.tribute" => ("monster Tributed", "monsters Tributed"),
            _ => ("card used for the cost", "cards used for the cost"),
        };
        return plural ? many : one;
    }

    /// <summary>
    /// "ATK equal to the difference between both players' LP", "300 ATK for each card in your hand", "ATK equal to half
    /// its original ATK", "ATK equal to its Level x 300". <paramref name="unit"/> is " ATK" or "".
    /// With <paramref name="forEach"/> false, a count is "equal to the number of ... x 300" too ("its ATK becomes ...").
    /// </summary>
    public static string Phrase(BlockArgs args, string unit, string owner = "it", bool forEach = true)
    {
        var of = args.Choice("value") == "cost_count" ? "the number of " + CostNoun(args.Effect, plural: true)
            : Of(args.Choice("value"), args.Cards(CardsKey), args.Pack, owner, Counters.Name(args));
        var n = args.Int("amount").ToString(CultureInfo.InvariantCulture);
        return args.Choice("how") switch
        {
            "per" when forEach && ForEach(args) => $"{n}{unit} for each {EachText(args, owner)}",
            "per" => $"{unit} equal to {of} x {n}".Trim(),
            // "half of that monster's original ATK", as cards word it; "half its original ATK" otherwise.
            "half" => $"{unit} equal to half {(Affected(args.Choice("value")) ? "of " : "")}{of}".Trim(),
            "double" => $"{unit} equal to double {of}".Trim(),
            _ => $"{unit} equal to {of}".Trim(),
        };
    }

    /// <summary>
    /// The Lua number: <paramref name="card"/> is the monster "its" means and <paramref name="p"/> the player "you" means.
    /// <paramref name="ev"/> is false inside a lasting effect, which has no event to take an amount from.
    /// </summary>
    /// <param name="valueKey">The parameter holding the source, and <paramref name="cardsKey"/> its card picker: blocks with two values use other names.</param>
    /// <param name="counterSuffix">Tells the second value's counter blanks apart.</param>
    public static string Lua(BlockContext ctx, string card, string p, bool ev = true, string valueKey = "value", string cardsKey = CardsKey, string howKey = "how", string amountKey = "amount",
        string counterSuffix = "")
    {
        var source = ctx.Args.Choice(valueKey);
        var counter = Counters.Id(ctx.Args, counterSuffix);
        string Group()
        {
            var f = ctx.Script.Filter(ctx.Args.Cards(cardsKey));
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
            "sum_counters" => Counters.SumOf(ctx.Script, Group(), counter),
            "its_level" => $"{card}:GetLevel()",
            "its_rank" => $"{card}:GetRank()",
            "its_link" => $"{card}:GetLink()",
            "its_materials" => $"{card}:GetOverlayCount()",
            "its_counters" => Counters.On(ctx.Script, card, counter),
            "its_equips" => $"{card}:GetEquipCount()",
            "its_linked" => $"{card}:GetLinkedGroupCount()",
            "its_used" => $"{card}:GetMaterialCount()",
            "its_orig_atk" => $"math.max(0,{card}:GetBaseAttack())",
            "its_orig_def" => $"math.max(0,{card}:GetBaseDefense())",
            "its_orig_higher" => $"math.max(0,{card}:GetBaseAttack(),{card}:GetBaseDefense())",
            "that_atk" or "that_orig_atk" or "that_orig_def" or "that_orig_higher" or "that_level" when ev => AffectedLua(ctx, source),
            "damage_turn" => LifeWatch.Total(ctx, p, "damage"),
            "opp_damage_turn" => LifeWatch.Total(ctx, $"1-{p}", "damage"),
            "gained_turn" => LifeWatch.Total(ctx, p, "gained"),
            "event" when ev => "ev",
            "roll" or "heads" when ev => Chance.ValueLua(ctx, source),
            "lp_paid" when ev => LpPaid(ctx),
            "cost_count" when ev => CostCount(ctx),
            "mem" or "mem_count" or "mem_atk" or "mem_orig_atk" or "mem_level" => RememberedLua(ctx, source, "mem" + counterSuffix),
            _ => "0",
        };
        if (Affected(source) && !ev) ctx.Effect.Error("\"That monster\" means the card a DO step before affected; a lasting effect has none.");
        if (source == "lp_paid" && !ev) ctx.Effect.Error("\"The LP paid\" only exists while the effect that paid them resolves; this block can't use it.");
        if (source == "cost_count" && !ev) ctx.Effect.Error("\"The number of cards used for the cost\" only exists while the effect that paid it resolves; this block can't use it.");
        if (source is "roll" or "heads" && !ev) ctx.Effect.Error("A die roll's result or a coin toss's heads only exist while the effect resolves; this block can't use them.");
        if (source == "event" && !ev) ctx.Effect.Error("\"That amount\" only exists while an effect resolves after damage or an LP change; a lasting effect can't use it.");
        else if (source == "event" && ctx.Effect.Code is not ("EVENT_DAMAGE" or "EVENT_BATTLE_DAMAGE" or "EVENT_RECOVER" or "EVENT_PAY_LPCOST"))
            ctx.Effect.Warn("\"That amount\" needs a WHEN that is damage being taken or LP being paid or gained; otherwise it is 0.");
        if (ctx.Args.Definition.Param(howKey) is null) return raw;
        return ctx.Args.Choice(howKey) switch
        {
            "half" => $"math.floor(({raw})/2)",
            "double" => $"({raw})*2",
            "per" => $"({raw})*{ctx.Args.Int(amountKey).ToString(CultureInfo.InvariantCulture)}",
            _ => raw,
        };
    }

    /// <summary>
    /// A stat of the card(s) the DO step before affected (their total, never below 0): the destroyed monster's original ATK,
    /// read from where it is now, as official scripts read it.
    /// </summary>
    private static string AffectedLua(BlockContext ctx, string source)
    {
        if (ctx.Effect.LastResult is null)
        {
            ctx.Effect.Error("\"That monster's ...\" needs a DO block before it that affects cards (destroys, banishes, sends...), linked with \"and if you do\".");
            return "0";
        }
        var stat = source switch
        {
            "that_atk" => "tc:GetPreviousAttackOnField()",
            "that_orig_atk" => "tc:GetBaseAttack()",
            "that_orig_def" => "tc:GetBaseDefense()",
            "that_orig_higher" => "math.max(tc:GetBaseAttack(),tc:GetBaseDefense())",
            _ => "tc:GetLevel()",
        };
        var fn = ctx.Script.AddFunction("thatstat", "g", ["local n=0", "for tc in aux.Next(g) do", $"\tn=n+math.max(0,{stat})", "end", "return n"]);
        return $"{fn}(Duel.GetOperatedGroup())";
    }

    /// <summary>"The LP paid": the amount a "pay LP in multiples" cost kept in the effect's label.</summary>
    /// <summary>How many cards the cost detached, discarded or banished, which a COST block keeps in the effect's label.</summary>
    private static string CostCount(BlockContext ctx)
    {
        if (!ctx.Effect.CostCounts)
            ctx.Effect.Error("\"The number of cards used for the cost\" needs a COST block that detaches, discards, sends or banishes cards (\"any number\" or \"up to\").");
        return "e:GetLabel()";
    }

    private static string LpPaid(BlockContext ctx)
    {
        if (!PayLifePointsCost.PaysMultiple(ctx.Effect.Source))
            ctx.Effect.Error("\"The LP paid\" needs the COST block \"Pay LP in multiples of ...\" in this effect.");
        return "e:GetLabel()";
    }

    /// <summary>What this card remembers in the Memory named by the <paramref name="slotKey"/> blank: its number, or its remembered cards' count or stats.</summary>
    private static string RememberedLua(BlockContext ctx, string source, string slotKey)
    {
        var slot = ctx.Args.Definition.Param(slotKey) is not null ? ctx.Args.Choice(slotKey) : "A";
        Memory.CheckRead(ctx, slot, source == "mem" ? "number" : "cards");
        return source switch
        {
            "mem" => Memory.NumberLua(ctx.Script, slot),
            "mem_count" => $"{Memory.GroupLua(ctx.Script, slot)}:GetCount()",
            "mem_atk" => Memory.StatLua(ctx.Script, slot, "Card.GetAttack"),
            "mem_orig_atk" => Memory.StatLua(ctx.Script, slot, "Card.GetBaseAttack"),
            _ => Memory.StatLua(ctx.Script, slot, "Card.GetLevel"),
        };
    }

    /// <summary>The Memory a remembered value reads ("mem", or "mem2" for a block's second value).</summary>
    public static ParamSpec MemoryParam(string valueKey = "value", string suffix = "") =>
        Memory.SlotParam("mem" + suffix).ShowIf(b => Remembered(b.Values.GetValueOrDefault(valueKey, "lp_diff")));

    /// <summary>The value's parameters, for blocks that use one.</summary>
    /// <param name="withEvent">Offer "that amount": only effects that resolve after damage or an LP change have one.</param>
    public static IEnumerable<ParamSpec> Params(int amountDefault = 300, bool withEvent = true) =>
    [
        ParamSpec.Choice("how", "How much", Hows),
        ParamSpec.Int("amount", "Amount (x, or for each)", amountDefault, 1, 99999).ShowIf(b => b.Values.GetValueOrDefault("how") == "per"),
        ParamSpec.Choice("value", "Of", withEvent ? Sources : Sources.Where(o => !AtResolution(o.Value)).ToArray()),
        MemoryParam(),
        ParamSpec.Cards(CardsKey, "Which cards (for counts and totals)", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand })
            .ShowIf(b => UsesCards(b.Values.GetValueOrDefault("value", "lp_diff"))),
        // Saved before counters had a kind, "the number of counters on it" is every counter: "any" is the default.
        .. Counters.Params(withAny: true, b => UsesCounter(b.Values.GetValueOrDefault("value", "lp_diff"))),
    ];

    /// <summary>The engine codes for gaining, losing or becoming a stat.</summary>
    public static string[] Codes(string stat, string dir) => (stat, dir) switch
    {
        // "The original ATK of this card becomes ...": what it is printed with, so later changes still apply (Apollousa).
        // Gaining or losing original ATK is gaining or losing ATK (CheckStat warns).
        ("orig_atk", "becomes") => ["EFFECT_SET_BASE_ATTACK"],
        ("orig_def", "becomes") => ["EFFECT_SET_BASE_DEFENSE"],
        ("orig_both", "becomes") => ["EFFECT_SET_BASE_ATTACK", "EFFECT_SET_BASE_DEFENSE"],
        ("orig_atk", _) => ["EFFECT_UPDATE_ATTACK"],
        ("orig_def", _) => ["EFFECT_UPDATE_DEFENSE"],
        ("orig_both", _) => ["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"],
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
    public static readonly ChoiceOption[] Stats =
    [
        new("atk", "ATK"), new("def", "DEF"), new("both", "ATK/DEF"), new("level", "Level"),
        new("orig_atk", "original ATK"), new("orig_def", "original DEF"), new("orig_both", "original ATK/DEF"),
    ];

    /// <summary>The original stats only "become" a value: gaining or losing them is gaining or losing ATK/DEF, which the card should say.</summary>
    public static void CheckStat(BlockContext ctx)
    {
        if (ctx.Args.Choice("stat").StartsWith("orig_", StringComparison.Ordinal) && ctx.Args.Choice("dir") != "becomes")
            ctx.Effect.Warn("An original ATK or DEF only \"becomes\" a value; gaining or losing it is gaining or losing ATK/DEF: pick ATK, DEF or ATK/DEF.");
    }

    /// <summary>
    /// "this card gains ATK equal to ...", "the Levels of monsters you control become equal to ...", "its ATK becomes ...".
    /// <paramref name="subject"/> is "this card", "it", "they" or a description of the monsters. When the value is the
    /// monster's own ("its Level") and the subject is plural, <paramref name="each"/> ("each monster you control", "each
    /// of them") is used instead, so "its" has one monster to mean.
    /// </summary>
    public static string Sentence(BlockArgs args, string subject, bool plural, string? each = null)
    {
        var stat = args.Choice("stat");
        var dir = args.Choice("dir");
        if (plural && each is not null && IsOwn(args.Choice("value")))
        {
            subject = each;
            plural = false;
        }
        // "this card's ATK", "its Level", "their ATK", "the Levels of monsters you control".
        string Whose(string noun) => subject switch
        {
            "this card" => $"this card's {noun}",
            "it" => $"its {noun}",
            "they" => $"their {noun}",
            _ => $"the {noun} of {subject}",
        };
        if (stat == "level")
        {
            var whose = Whose(plural ? "Levels" : "Level");
            if (dir == "becomes") return $"{whose} {(plural ? "become" : "becomes")} {Phrase(args, "", forEach: false)}";
            // "increases by the number of ...", "increases by 1 for each ...", "decreases by half ...".
            var by = Phrase(args, "");
            if (by.StartsWith("equal to ", StringComparison.Ordinal)) by = by["equal to ".Length..];
            return $"{whose} {(dir == "lose" ? plural ? "decrease" : "decreases" : plural ? "increase" : "increases")} by {by}";
        }
        var unit = " " + args.Describe("stat");
        // "the ATK of monsters you control becomes ...", but "its ATK/DEF become ...".
        if (dir == "becomes") return $"{Whose(unit.Trim())} {(stat is "both" or "orig_both" ? "become" : "becomes")} {Phrase(args, "", forEach: false)}";
        var gain = dir == "lose" ? (plural ? "lose" : "loses") : (plural ? "gain" : "gains");
        // "this card gains that much ATK", as cards that pay LP in multiples say it (Cosmic Tree Irmistil, Psychic Blade).
        if (args.Choice("value") == "lp_paid" && args.Choice("how") == "equal") return $"{subject} {gain} that much{unit}";
        // "gains ATK equal to ...", "gains 300 ATK for each ...".
        return $"{subject} {gain} {Phrase(args, unit)}";
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
    public override string Sentence => "{cards} {dir} {stat} {how} {amount} {value} {mem} {counter} {counter_id} {of}";
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
            : ValueSources.Sentence(args, FilterText.Describe(f, args.Pack, plural: true, preposition: ""), true,
                "each " + FilterText.Describe(f, args.Pack, plural: false, preposition: ""));
    }

    public override void Compile(BlockContext ctx)
    {
        ValueSources.CheckStat(ctx);
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
    public override string Sentence => "{count} {cards} {target} {dir} {stat} {how} {amount} {value} {mem} {counter} {counter_id} {of} {duration}";
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
        KeptParam,
    ];

    private static string Tail(BlockArgs args) => args.Describe("duration") is { Length: > 0 } d ? " " + d : "";

    public override string Describe(BlockArgs args)
    {
        if (args.IsThisCard) return ValueSources.Sentence(args, "this card", false) + Tail(args);
        var all = args.CountMode == "all";
        var subject = all ? $"all {args.Describe("cards")}" : Some(args);
        // "each face-up monster you control gains ATK equal to its Level", "each of 2 face-up monsters you control ...".
        var each = all
            ? "each " + FilterText.Describe(args.Cards("cards"), args.Pack, plural: false, ownName: args.CardName, withExcept: false)
            : $"each of {args.Describe("count")} {args.Describe("cards")}";
        return ValueSources.Sentence(args, subject, args.IsPlural, each) + Tail(args) + args.ExceptText();
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) =>
        ValueSources.Sentence(args, pronoun == "it" ? "it" : "they", !single, "each of them") + Tail(args);

    // Monsters without a Level (Xyz, Link) can't have it changed.
    protected override string? AbleCheck(BlockContext ctx) => ctx.Args.Choice("stat") == "level" ? "c:IsFaceup() and c:IsLevelAbove(1)" : "c:IsFaceup()";

    public override void Compile(BlockContext ctx)
    {
        ValueSources.CheckStat(ctx);
        if (ctx.Args.Choice("stat") is "def" or "both" or "orig_def" or "orig_both") ctx.Effect.AddCategory("CATEGORY_DEFCHANGE");
        base.Compile(ctx);
    }

    protected override string Apply(string g, BlockContext ctx)
    {
        var lose = ctx.Args.Choice("dir") == "lose";
        var codes = ValueSources.Codes(ctx.Args.Choice("stat"), ctx.Args.Choice("dir"));
        var value = ValueSources.Lua(ctx, "tc", "tp");
        // What this card gives itself is lost when its effects are negated, like "This card gains or loses ATK/DEF".
        var reset = Resets.Of(ctx, ctx.Args.Cards("cards").ThisCard ? "+RESET_DISABLE" : "");
        var lines = new List<string> { "local n=0", "for tc in aux.Next(g) do", "\tif tc:IsFaceup() then", $"\t\tlocal v={value}" };
        foreach (var code in codes)
        {
            lines.Add("\t\tlocal se=Effect.CreateEffect(e:GetHandler())");
            lines.Add("\t\tse:SetType(EFFECT_TYPE_SINGLE)");
            lines.AddRange(KeptLines(ctx, "se", "\t\t"));
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

/// <summary>
/// "Inflict damage to your opponent equal to the number of cards in their hand x 300", "gain LP equal to the difference in
/// LP". The amount is worked out when the effect resolves, or when it is activated (as Dark Dimension Soldier's is); it
/// can be activated only when the amount is above 0, unless it may be 0.
/// </summary>
public sealed class LifeFromValueAction : ActionBlock
{
    public const string BlockId = "do.lp_value";
    public override string Id => BlockId;
    public override string Group => "Life Points";
    public override string Name => "Inflict damage / gain LP equal to a value (cards, LP, ATK...)";
    public override string Sentence => "{what} {who} {how} {amount} {value} {mem} {counter} {counter_id} {of} {when}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "Do", new("damage", "Inflict damage to"), new("gain", "Gain LP:")),
        ParamSpec.Choice("who", "Who", new("opponent", "your opponent"), new("you", "you"), new("both", "both players")),
        .. ValueSources.Params(),
        // Saved before these were offered: worked out when it resolves, and only activated when above 0.
        ParamSpec.Choice("when", "Amount worked out", new("resolve", "", "when it resolves"), new("activate", "", "when it is activated")),
        ParamSpec.Flag("zero_ok", "can be activated even if the amount is 0", false),
    ];

    public override string Describe(BlockArgs args)
    {
        // "Its" is this card: no other monster is involved.
        var amount = ValueSources.Phrase(args, "", "this card");
        var n = args.Int("amount").ToString(CultureInfo.InvariantCulture);
        var each = ValueSources.ForEach(args) ? " for each " + ValueSources.EachText(args, "this card") : null;
        if (args.Choice("what") == "gain")
        {
            // "gain 300 LP for each card in your hand", "your opponent gains LP equal to ...".
            var lp = each is null ? $"LP {amount}" : $"{n} LP{each}";
            return args.Choice("who") switch
            {
                "opponent" => $"your opponent gains {lp}",
                "both" => $"both players gain {lp}",
                _ => $"gain {lp}",
            };
        }
        // "inflict 300 damage to your opponent for each card in your hand", but "you take damage equal to ...", as
        // cards say it ("both players take damage equal to its ATK").
        return args.Choice("who") switch
        {
            "you" => each is null ? $"you take damage {amount}" : $"you take {n} damage{each}",
            "both" => each is null ? $"both players take damage {amount}" : $"both players take {n} damage{each}",
            _ => each is null ? $"inflict damage to your opponent {amount}" : $"inflict {n} damage to your opponent{each}",
        };
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
        if (!ValueSources.AtResolution(ctx.Args.Choice("value")) && !ctx.Args.Bool("zero_ok")) ctx.Effect.TargetChecks.Add($"{value}>0");
        var r = ctx.Effect.NewLocal("r");
        var lv = ctx.Effect.NewLocal("lv");
        if (ctx.Args.Choice("when") == "activate")
        {
            // Kept with the chain link, as official cards do (Duel.SetTargetParam), so it is the same when it resolves.
            if (ctx.Effect.TargetLines.Any(l => l.StartsWith("Duel.SetTargetParam(", StringComparison.Ordinal)))
                ctx.Effect.Error($"\"{Name}\": only one block of an effect can work out its amount when the effect is activated.");
            if (ctx.Args.Choice("value") is "roll" or "heads")
                ctx.Effect.Error($"\"{Name}\": a die roll's result or a coin toss's heads are only known when the effect resolves.");
            ctx.Effect.TargetLines.Add($"Duel.SetTargetParam({value})");
            value = "Duel.GetChainInfo(0,CHAININFO_TARGET_PARAM)";
        }
        var body = new List<string> { $"local {r}=0", $"local {lv}={value}", $"if {lv}>0 then" };
        // Both players at the same time, as official cards do it: a step for each, then both together.
        var step = players.Length > 1 ? ",true" : "";
        foreach (var p in players) body.Add($"\t{r}={r}+Duel.{(damage ? "Damage" : "Recover")}({p},{lv},REASON_EFFECT{step})");
        if (players.Length > 1) body.Add("\tDuel.RDComplete()");
        body.Add("end");
        ctx.Effect.AddStep(body, r, ctx.Link);
    }
}
