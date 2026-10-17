using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

public abstract class ConditionBlock : BlockDefinition
{
    public override BlockKind Kind => BlockKind.Condition;

    /// <summary>For lasting effects: how long the condition holds, said after the effect ("until the end of the Damage Step"). Usually empty.</summary>
    public virtual string Until(BlockArgs args) => "";
}

public sealed class CardsExistCondition : ConditionBlock
{
    private static bool AnStart(string noun) => noun.Length > 0 && "AEIOUaeiou".Contains(noun[0]) && !noun.StartsWith("Us", StringComparison.Ordinal);

    public override string Id => "if.cards_exist";
    public override string Group => "Cards";
    public override string Name => "There are cards somewhere";
    public override string Sentence => "There are {cmp} {count} {cards}";
    public override string Help => "For example: you control 2 or more Dragon monsters, you control exactly 1 monster, or there are 3 or fewer Spells in your GY.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("cmp", "How many", CardCounts.Compare),
        ParamSpec.Int("count", "Number", 1, 1, 60),
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true }),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var cards = args.Cards("cards");
        var cmp = args.Choice("cmp");
        var plural = n != 1 || cmp == "at_most";
        var noun = FilterText.Describe(cards, args.Pack, plural: plural, preposition: "in");
        // "a Dragon monster", "exactly 1 monster", "2 or more", "3 or fewer".
        var article = cmp == "at_least" && n == 1 ? (AnStart(noun) ? "an" : "a") : CardCounts.Amount(cmp, n);
        if (FilterText.IsFieldOnly(cards) && cards.Owner != CardOwner.Either)
            return $"{(cards.Owner == CardOwner.You ? "you control" : "your opponent controls")} {article} {FilterText.Describe(cards, args.Pack, plural, "", withLocation: false)}";
        // "you have a Trap in your GY", as cards say it.
        if (cards.Owner == CardOwner.You && (cards.Zones & Zone.Field) == 0 && cards.Zones != Zone.None)
            return $"you have {article} {noun}";
        return $"there {(plural ? "are" : "is")} {article} {noun}";
    }

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Script.Filter(ctx.Args.Cards("cards"));
        var cmp = ctx.Args.Choice("cmp");
        ctx.Effect.Conditions.Add(cmp == "at_least"
            ? $"Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},{f.Opp},{ctx.Args.Int("count")},{f.Exclude},e,tp)"
            : $"{CardCounts.Count(ctx, f)}{CardCounts.Op(cmp)}{ctx.Args.Int("count")}");
    }
}

public sealed class NoCardsCondition : ConditionBlock
{
    public override string Id => "if.no_cards";
    public override string Group => "Cards";
    public override string Name => "There are no matching cards";
    public override string Sentence => "There are no {cards}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
    ];

    public override string Describe(BlockArgs args)
    {
        var cards = args.Cards("cards");
        if (FilterText.IsFieldOnly(cards) && cards.Owner != CardOwner.Either)
            return $"{(cards.Owner == CardOwner.You ? "you control no" : "your opponent controls no")} {FilterText.Describe(cards, args.Pack, true, "", withLocation: false)}";
        // "you have no Traps in your GY".
        if (cards.Owner == CardOwner.You && (cards.Zones & Zone.Field) == 0 && cards.Zones != Zone.None)
            return $"you have no {FilterText.Describe(cards, args.Pack, true, "in")}";
        return $"there are no {FilterText.Describe(cards, args.Pack, true, "in")}";
    }

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Script.Filter(ctx.Args.Cards("cards"));
        ctx.Effect.Conditions.Add($"not Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},{f.Opp},1,{f.Exclude},e,tp)");
    }
}

public sealed class SummonedThisTurnCondition : ConditionBlock
{
    public override string Id => "if.summoned_this_turn";
    public override string Group => "This card";
    public override string Name => "This card was Summoned this turn";
    public override string Sentence => "This card {was} {how} Summoned this turn";
    public override string Help => "Pair it with a Quick Effect for \"If this card was Summoned this turn (Quick Effect): ...\". " +
                                   "\"Was not\" reads \"except during the turn this card was Special Summoned\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How",
            new("any", "Normal, Flip or Special", "Normal, Flip or Special"),
            new("normal", "Normal"),
            new("special", "Special"),
            new("flip", "Flip"),
            new("normal_special", "Normal or Special")),
        ParamSpec.Choice("was", "Was it", new("was", "was"), new("not", "was not", "was not (\"except during the turn ...\")")),
    ];

    public override string Describe(BlockArgs args)
    {
        var summoned = args.Choice("how") == "any" ? "Summoned" : $"{args.Describe("how")} Summoned";
        return args.Choice("was") == "not" ? $"except during the turn this card was {summoned}" : $"this card was {summoned} this turn";
    }

    /// <summary>The check that this card was Summoned this turn, the way the choice says.</summary>
    public static string Check(string how) => how switch
    {
        "normal" => "e:GetHandler():IsStatus(STATUS_SUMMON_TURN)",
        "special" => "e:GetHandler():IsStatus(STATUS_SPSUMMON_TURN)",
        "flip" => "e:GetHandler():IsStatus(STATUS_FLIP_SUMMON_TURN)",
        "normal_special" => "e:GetHandler():IsStatus(STATUS_SUMMON_TURN+STATUS_SPSUMMON_TURN)",
        _ => "(e:GetHandler():IsStatus(STATUS_SUMMON_TURN) or e:GetHandler():IsStatus(STATUS_FLIP_SUMMON_TURN) or e:GetHandler():IsStatus(STATUS_SPSUMMON_TURN))",
    };

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var check = Check(ctx.Args.Choice("how"));
        ctx.Effect.Conditions.Add(ctx.Args.Choice("was") == "not" ? "not " + check : check);
    }
}

public sealed class OpponentMoreMonstersCondition : ConditionBlock
{
    public override string Id => "if.opponent_more_monsters";
    public override string Group => "Cards";
    public override string Name => "Your opponent controls more monsters";
    public override string Sentence => "Your opponent controls more monsters than you do";

    public override string Describe(BlockArgs args) => "your opponent controls more monsters than you do";

    public override void Compile(BlockContext ctx) =>
        ctx.Effect.Conditions.Add("Duel.GetFieldGroupCount(tp,LOCATION_MZONE,0)<Duel.GetFieldGroupCount(tp,0,LOCATION_MZONE)");
}

public sealed class LifePointsCondition : ConditionBlock
{
    public override string Id => "if.life_points";
    public override string Group => "Life Points";
    public override string Name => "Life Points compared to a number";

    public override string Sentence => "{who} LP is {cmp} {amount} {amount2}";

    public override string Describe(BlockArgs args)
    {
        var whose = args.Choice("who") == "you" ? "your" : "your opponent's";
        var (n, m) = (args.Int("amount"), args.Int("amount2"));
        return args.Choice("cmp") switch
        {
            "lt" => $"{whose} LP are less than {n}",
            "gt" => $"{whose} LP are more than {n}",
            "eq" => $"{whose} LP are exactly {n}",
            "between" => $"{whose} LP are between {Math.Min(n, m)} and {Math.Max(n, m)}",
            "ge" => $"{whose} LP are {n} or more",
            _ => $"{whose} LP are {n} or less",
        };
    }

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Whose", new("you", "Your"), new("opponent", "Your opponent's")),
        ParamSpec.Choice("cmp", "Compare", new("le", "at most"), new("ge", "at least"), new("lt", "less than"), new("gt", "more than"), new("eq", "exactly"), new("between", "between")),
        ParamSpec.Int("amount", "Amount", 2000, 0, 999999),
        ParamSpec.Int("amount2", "and", 4000, 0, 999999).ShowIf(b => b.Values.GetValueOrDefault("cmp") == "between"),
    ];

    public override void Compile(BlockContext ctx)
    {
        var lp = $"Duel.GetLP({(ctx.Args.Choice("who") == "you" ? "tp" : "1-tp")})";
        var (n, m) = (ctx.Args.Int("amount"), ctx.Args.Int("amount2"));
        ctx.Effect.Conditions.Add(ctx.Args.Choice("cmp") switch
        {
            "lt" => $"{lp}<{n}",
            "gt" => $"{lp}>{n}",
            "eq" => $"{lp}=={n}",
            "between" => $"{lp}>={Math.Min(n, m)} and {lp}<={Math.Max(n, m)}",
            "ge" => $"{lp}>={n}",
            _ => $"{lp}<={n}",
        });
    }
}

public sealed class CompareLifePointsCondition : ConditionBlock
{
    public override string Id => "if.lp_compare";
    public override string Group => "Life Points";
    public override string Name => "Your LP compared to your opponent's";
    public override string Sentence => "You have {cmp} LP than your opponent";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("cmp", "Compare",
            new("less", "less"), new("more", "more"), new("le", "less or the same", "the same or less"), new("ge", "more or the same", "the same or more"),
            new("same", "the same", "exactly the same"), new("half", "half or less of", "half or less of your opponent's"),
            new("double", "at least double", "at least double your opponent's")),
    ];

    public override string Describe(BlockArgs args) => args.Choice("cmp") switch
    {
        "le" => "your LP are less than or equal to your opponent's",
        "ge" => "your LP are more than or equal to your opponent's",
        "same" => "your LP are the same as your opponent's",
        "half" => "your LP are half your opponent's or less",
        "double" => "your LP are at least double your opponent's",
        _ => $"you have {args.Describe("cmp")} LP than your opponent",
    };

    public override void Compile(BlockContext ctx) => ctx.Effect.Conditions.Add(ctx.Args.Choice("cmp") switch
    {
        "more" => "Duel.GetLP(tp)>Duel.GetLP(1-tp)",
        "le" => "Duel.GetLP(tp)<=Duel.GetLP(1-tp)",
        "ge" => "Duel.GetLP(tp)>=Duel.GetLP(1-tp)",
        "same" => "Duel.GetLP(tp)==Duel.GetLP(1-tp)",
        "half" => "Duel.GetLP(tp)*2<=Duel.GetLP(1-tp)",
        "double" => "Duel.GetLP(tp)>=Duel.GetLP(1-tp)*2",
        _ => "Duel.GetLP(tp)<Duel.GetLP(1-tp)",
    });
}

public sealed class TurnCondition : ConditionBlock
{
    public override string Id => "if.turn";
    public override string Group => "Turn";
    public override string Name => "Whose turn it is";
    public override string Sentence => "It is {whose} turn";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("whose", "Whose", new("your", "your"), new("opponent", "your opponent's")),
    ];

    public override string Describe(BlockArgs args) => $"during {args.Describe("whose")} turn";

    public override void Compile(BlockContext ctx) =>
        ctx.Effect.Conditions.Add(ctx.Args.Choice("whose") == "your" ? "Duel.GetTurnPlayer()==tp" : "Duel.GetTurnPlayer()==1-tp");
}

public sealed class PhaseCondition : ConditionBlock
{
    public override string Id => "if.phase";
    public override string Group => "Turn";
    public override string Name => "It is a certain phase";
    public override string Sentence => "It {is} the {phase}";
    public override string Help => "\"Is not\" reads \"except during the Draw Phase\", as Droll & Lock Bird says it.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("phase", "Phase",
            new("main", "Main Phase", "Main Phase (1 or 2)"),
            new("main1", "Main Phase 1"),
            new("main2", "Main Phase 2"),
            new("battle", "Battle Phase"),
            new("standby", "Standby Phase"),
            new("end", "End Phase"),
            new("draw", "Draw Phase")),
        ParamSpec.Choice("is", "Is it", new("is", "is"), new("not", "is not", "is not (\"except during the ...\")")),
    ];

    public override string Describe(BlockArgs args) => args.Choice("is") == "not" ? $"except during the {args.Describe("phase")}" : $"during the {args.Describe("phase")}";

    public override void Compile(BlockContext ctx)
    {
        var check = ctx.Args.Choice("phase") switch
        {
            "battle" => "(Duel.GetCurrentPhase()>=PHASE_BATTLE_START and Duel.GetCurrentPhase()<=PHASE_BATTLE)",
            "standby" => "Duel.GetCurrentPhase()==PHASE_STANDBY",
            "end" => "Duel.GetCurrentPhase()==PHASE_END",
            "main1" => "Duel.GetCurrentPhase()==PHASE_MAIN1",
            "main2" => "Duel.GetCurrentPhase()==PHASE_MAIN2",
            "draw" => "Duel.GetCurrentPhase()==PHASE_DRAW",
            _ => "(Duel.GetCurrentPhase()==PHASE_MAIN1 or Duel.GetCurrentPhase()==PHASE_MAIN2)",
        };
        if (ctx.Args.Choice("is") != "not") { ctx.Effect.Conditions.Add(check); return; }
        // "Except during the Draw Phase": a "==" check turned around, the others wrapped.
        ctx.Effect.Conditions.Add(check.StartsWith('(') ? "not " + check : check.Replace("==", "~=", StringComparison.Ordinal));
    }
}

/// <summary>
/// "If "Dark Magician" is on the field", "while you control "Blue-Eyes White Dragon"", "while "Umi" is on the field":
/// a certain card (usually picked by name) is somewhere.
/// </summary>
public sealed class CardPresentCondition : ConditionBlock
{
    public override string Id => "if.card_present";
    public override string Group => "Cards";
    public override string Name => "A certain card is on the field (or somewhere)";
    public override string Sentence => "{card} {is} {where}";
    public override string Help => "Pick the card by name in the card picker. For example: if \"Dark Magician\" is on the field, while you control \"Blue-Eyes White Dragon\", or while \"Umi\" is in a Field Zone. Combine it with a WHEN like \"(Quick Effect) at any time\" to Special Summon or activate only while that card is there.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new ParamSpec
        {
            Key = "card", Label = "Which card", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field, CardType = FilterCardType.Any, FaceUpOnly = true },
        },
        ParamSpec.Choice("is", "Is or is not", new("is", "is"), new("not", "is not")),
        ParamSpec.Choice("where", "Where",
            new("field", "on the field"),
            new("you", "controlled by you", "you control it"),
            new("opponent", "controlled by your opponent", "your opponent controls it"),
            new("field_zone", "in a Field Zone", "in a Field Zone (the face-up Field Spell)"),
            new("monster", "in a Monster Zone"),
            new("gy", "in your GY"),
            new("any_gy", "in a GY", "in either GY"),
            new("banished", "banished", "banished (by either player)"),
            new("hand", "in your hand")),
    ];

    /// <summary>The card picker's card, placed where the block says.</summary>
    public static CardFilter Placed(CardFilter picked, string where)
    {
        var f = picked.Clone();
        (f.Owner, f.Zones) = where switch
        {
            "you" => (CardOwner.You, Zone.Field),
            "opponent" => (CardOwner.Opponent, Zone.Field),
            "field_zone" => (CardOwner.Either, Zone.SpellTrapZone),
            "monster" => (CardOwner.Either, Zone.MonsterZone),
            "gy" => (CardOwner.You, Zone.Graveyard),
            "any_gy" => (CardOwner.Either, Zone.Graveyard),
            "banished" => (CardOwner.Either, Zone.Banished),
            "hand" => (CardOwner.You, Zone.Hand),
            _ => (CardOwner.Either, Zone.Field),
        };
        if (where is "gy" or "any_gy" or "hand") f.FaceUpOnly = false;
        if (where == "field_zone") f.FaceUpOnly = true;
        return f;
    }

    /// <summary>Only a card's name: "while "Umi" is on the field" can use Duel.IsEnvironment.</summary>
    public static bool NameOnly(CardFilter f) =>
        f.CardCode != 0 && f.Archetype == 0 && f.Race == CardRace.None && f.Attribute == CardAttribute.None && f.MonsterKind == MonsterKind.Any
        && f.LevelMin is null && f.LevelMax is null && f.AtkMin is null && f.AtkMax is null && !f.ExcludeThisCard && !f.PointedToByThisCard && !f.CenterZone;

    /// <summary>The "where" choice that holds the filter's own zones and owner, or null.</summary>
    public static string? WhereOf(CardFilter f) => (f.Zones, f.Owner) switch
    {
        (Zone.Field, CardOwner.Either) => "field",
        (Zone.Field, CardOwner.You) => "you",
        (Zone.Field, CardOwner.Opponent) => "opponent",
        (Zone.MonsterZone, CardOwner.Either) => "monster",
        (Zone.Graveyard, CardOwner.You) => "gy",
        (Zone.Graveyard, CardOwner.Either) => "any_gy",
        (Zone.Banished, CardOwner.Either) => "banished",
        (Zone.Hand, CardOwner.You) => "hand",
        _ => null,
    };

    public override string Describe(BlockArgs args)
    {
        var picked = args.Cards("card");
        var where = args.Choice("where");
        var not = args.Choice("is") == "not";
        var noun = FilterText.Describe(picked, args.Pack, plural: false, withLocation: false);
        var named = picked.CardCode != 0 || picked.ThisCard;
        var a = named ? noun : ("AEIOUaeiou".Contains(noun[0]) ? "an " : "a ") + noun;
        return where switch
        {
            "you" => not ? $"you do not control {a}" : $"you control {a}",
            "opponent" => not ? $"your opponent does not control {a}" : $"your opponent controls {a}",
            _ when named => $"{noun} is{(not ? " not" : "")} {args.Describe("where")}",
            _ => not ? $"there is no {noun} {args.Describe("where")}" : $"{a} is {args.Describe("where")}",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var where = ctx.Args.Choice("where");
        var not = ctx.Args.Choice("is") == "not" ? "not " : "";
        var f = Placed(ctx.Args.Cards("card"), where);
        if (f.ThisCard)
        {
            var loc = where switch
            {
                "field_zone" => "LOCATION_FZONE", "monster" => "LOCATION_MZONE", "gy" or "any_gy" => "LOCATION_GRAVE",
                "banished" => "LOCATION_REMOVED", "hand" => "LOCATION_HAND", _ => "LOCATION_ONFIELD",
            };
            var who = where switch { "you" or "gy" or "hand" => " and e:GetHandler():IsControler(tp)", "opponent" => " and e:GetHandler():IsControler(1-tp)", _ => "" };
            ctx.Effect.Conditions.Add($"{not}(e:GetHandler():IsLocation({loc}){who})");
            return;
        }
        if (where == "field_zone" && NameOnly(f))
        {
            ctx.Effect.Conditions.Add($"{not}Duel.IsEnvironment({Lua.Num(f.CardCode)})");
            return;
        }
        var r = ctx.Script.Filter(f);
        var (self, opp) = where == "field_zone" ? ("LOCATION_FZONE", "LOCATION_FZONE") : (r.Self, r.Opp);
        ctx.Effect.Conditions.Add($"{not}Duel.IsExistingMatchingCard({r.Function},tp,{self},{opp},1,{r.Exclude},e,tp)");
    }
}

/// <summary>
/// "If this card battled this turn" (at the end of the Battle Phase, during the End Phase...), and "if this card destroyed
/// a monster by battle" (Number 89: Diablosis, Armed Dragon LV5): a watcher on this card remembers the destruction.
/// </summary>
public sealed class BattledThisTurnCondition : ConditionBlock
{
    public override string Id => "if.battled_this_turn";
    public override string Group => "This card";
    public override string Name => "This card battled, or destroyed a monster by battle, this turn";
    public override string Sentence => "This card {what}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "What it did",
            new("battled", "battled this turn"),
            new("destroyed", "destroyed a monster by battle this turn"),
            new("destroyed_bp", "destroyed a monster by battle", "destroyed a monster by battle this Battle Phase")),
    ];

    public override string Describe(BlockArgs args) => $"this card {args.Describe("what")}";

    public override void Compile(BlockContext ctx)
    {
        var what = ctx.Args.Choice("what");
        if (what == "battled") { ctx.Effect.Conditions.Add("e:GetHandler():GetBattledGroupCount()>0"); return; }
        Zones.RequireMonster(ctx, Name);
        // Remembered until the end of the turn or of the Battle Phase, while this card stays on the field.
        var (flag, phase) = what == "destroyed" ? ("id+8220", "PHASE_END") : ("id+8221", "PHASE_BATTLE");
        var reg = ctx.Script.AddFunction("bdreg" + (what == "destroyed" ? "" : "bp"), "e,tp,eg,ep,ev,re,r,rp",
            [$"e:GetHandler():RegisterFlagEffect({flag},RESET_EVENT+RESETS_STANDARD+RESET_PHASE+{phase},0,1)"]);
        var v = what == "destroyed" ? "bd" : "bdp";
        ctx.Script.AddInitialLine(string.Join("\n\t",
        [
            $"local {v}=Effect.CreateEffect(c)",
            $"{v}:SetType(EFFECT_TYPE_SINGLE+EFFECT_TYPE_CONTINUOUS)",
            $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
            $"{v}:SetCode(EVENT_BATTLE_DESTROYING)",
            $"{v}:SetOperation({reg})",
            $"c:RegisterEffect({v})",
        ]));
        ctx.Effect.Conditions.Add($"e:GetHandler():GetFlagEffect({flag})~=0");
    }
}
