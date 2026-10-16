using CardCrafter.Core.Compiler;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>Type and Attribute choices for blocks that name one, the first being the default.</summary>
internal static class MonsterChoices
{
    public static ChoiceOption[] Races(Func<CardRace, string> label) => Enum.GetValues<CardRace>().Where(r => r != CardRace.None)
        .Select(r => new ChoiceOption(r.ToString(), label(r), EnumNames.Display(r))).ToArray();

    /// <summary>In the order card text lists them: LIGHT, DARK, EARTH, WATER, FIRE, WIND, DIVINE.</summary>
    public static readonly ChoiceOption[] Attributes =
        new[] { CardAttribute.Light, CardAttribute.Dark, CardAttribute.Earth, CardAttribute.Water, CardAttribute.Fire, CardAttribute.Wind, CardAttribute.Divine }
            .Select(a => new ChoiceOption(a.ToString(), EnumNames.Display(a))).ToArray();
}

// ---------------------------------------------------------------- Gemini

/// <summary>
/// A Gemini monster's effects: they only work while it is an Effect Monster, after it was Normal Summoned a second
/// time on the field (aux.IsDualState). The card text lists such effects under the Gemini rule, each after a "●".
/// </summary>
public sealed class GeminiCondition : ConditionBlock
{
    public const string BlockId = "if.gemini";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This Gemini monster was Summoned again (Gemini effect)";
    public override string Sentence => "This card is an Effect Monster (Gemini Summoned)";
    public override string Help => "For Gemini monsters (Card tab: Gemini). The effect only works after this card was Normal Summoned a second time " +
                                   "while face-up on the field. The card text puts it under the Gemini rule: \"... become an Effect Monster with this effect. ● ...\"";

    /// <summary>The Gemini rule says it, so the effect itself reads without a condition.</summary>
    public override string Describe(BlockArgs args) => "";

    public override void Compile(BlockContext ctx)
    {
        if (!ctx.Card.IsMonster || !ctx.Card.Abilities.HasFlag(MonsterAbility.Gemini))
            ctx.Effect.Error($"\"{Name}\" needs a Gemini monster: tick Gemini on the Card tab.");
        ctx.Effect.Conditions.Add("aux.IsDualState(e)");
    }

    public static bool Has(EffectEvent e) => e.If.Any(b => b.BlockId == BlockId);

    /// <summary>The Gemini rule, as printed cards say it, before the effects it gives.</summary>
    public static string Rule(int effects) =>
        "This card is treated as a Normal Monster while face-up on the field or in the GY. While this card is a Normal Monster on the field, " +
        $"you can Normal Summon it to have it become an Effect Monster with {(effects == 1 ? "this effect" : "these effects")}.";
}

// ---------------------------------------------------------------- Union

/// <summary>
/// A Union monster's built-in effects (aux.EnableUnionAttribute): once per turn, equip it to a monster you control or
/// unequip it to Special Summon it, and destroy it instead of the equipped monster.
/// </summary>
public sealed class UnionPassive : PassiveBlock
{
    public const string BlockId = "passive.union";
    public override string Id => BlockId;
    public override string Group => "Equip";
    public override string Name => "Union: equip this card to a monster, or unequip it to Special Summon it";
    public override string Sentence => "Once per turn, you can either: Target 1 {cards} you control; equip this card to that target, OR: Unequip this card and Special Summon it";
    public override string Help => "For Union monsters (Card tab: Union). Add \"The equipped monster gains ATK\" blocks for what it gives the monster it is equipped to.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new ParamSpec { Key = "cards", Label = "Which monsters it can equip to", Type = ParamType.Cards, WhereFromBlock = true, DefaultFilter = () => new CardFilter { CardType = FilterCardType.Monster } },
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards").Clone();
        f.CardType = FilterCardType.Monster;
        var noun = FilterText.Describe(f, args.Pack, withLocation: false, ownName: args.CardName);
        return $"once per turn, you can either: Target 1 {noun} you control; equip this card to that target, OR: Unequip this card and Special Summon it. " +
               "If the equipped monster would be destroyed by battle or card effect, destroy this card instead";
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        if (ctx.Card.IsMonster && !ctx.Card.Abilities.HasFlag(MonsterAbility.Union))
            ctx.Effect.Warn("A Union monster's equip effects work on any monster card, but only cards marked Union on the Card tab count as Union monsters.");
        if (ctx.Effect.Conditions.Count > 0 || ctx.Effect.Source.Limit != OncePerTurn.None)
            ctx.Effect.Error($"\"{Name}\" is always on and once per turn by itself: remove its IF blocks and its once-per-turn setting.");
        var f = ctx.Script.MaterialFilter(ctx.Args.Cards("cards"), "unionfilter");
        ctx.Script.AddInitialLine($"aux.EnableUnionAttribute(c,{(f == "nil" ? "aux.TRUE" : f)})");
    }
}

// ---------------------------------------------------------------- Trap Monsters

/// <summary>
/// "Special Summon this card as an Effect Monster (Rock/EARTH/Level 4/ATK 1800/DEF 1800). (This card is also still a
/// Trap.)": a Trap Card that becomes a monster when it resolves. The database gets the monster's stats, as official
/// Trap Monsters have them.
/// </summary>
public sealed class TrapMonsterAction : ActionBlock
{
    public const string BlockId = "do.trap_monster";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "Special Summon this Trap as a monster (Trap Monster)";
    public override string Sentence => "Special Summon this card {position} as {kind} ({race}/{attribute}/Level {level}/ATK {atk}/DEF {def})";
    public override string Help => "For Trap Cards: when the Trap resolves it becomes a monster with these stats and is Special Summoned. It is still a Trap, " +
                                   "so it doesn't take a Spell & Trap Zone while it is a monster.";

    private static readonly ChoiceOption[] RaceChoices = MonsterChoices.Races(r => EnumNames.Display(r));

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("position", "Position", new("faceup", "", "face-up"), new("defense", "in Defense Position"), new("attack", "in Attack Position")),
        ParamSpec.Choice("kind", "As", new("effect", "an Effect Monster"), new("normal", "a Normal Monster")),
        new ParamSpec { Key = "race", Label = "Type", Type = ParamType.Choice, Default = RaceChoices[0].Value, Choices = RaceChoices },
        new ParamSpec { Key = "attribute", Label = "Attribute", Type = ParamType.Choice, Default = MonsterChoices.Attributes[0].Value, Choices = MonsterChoices.Attributes },
        ParamSpec.Int("level", "Level", 4, 1, 12),
        ParamSpec.Int("atk", "ATK", 1000, 0, 99999),
        ParamSpec.Int("def", "DEF", 1000, 0, 99999),
    ];

    public override string Afterword(BlockArgs args) => "(This card is also still a Trap.)";

    public static CardRace Race(BlockArgs args) => Enum.Parse<CardRace>(args.Choice("race"));
    public static CardAttribute Attribute(BlockArgs args) => Enum.Parse<CardAttribute>(args.Choice("attribute"));

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (ctx.Card.Kind != CardKind.Trap) e.Error($"\"{Name}\" only works on Trap Cards.");
        else if (!e.IsActivation) e.Error($"\"{Name}\" happens when this Trap Card is activated: use the WHEN block \"Activate this Spell/Trap\".");
        var a = ctx.Args;
        var normal = a.Choice("kind") == "normal";
        var types = normal ? "TYPES_NORMAL_TRAP_MONSTER" : "TYPES_EFFECT_TRAP_MONSTER";
        var setcode = Lua.Hex(CdbEncoder.PackSetcodes(TreatedAsPassive.Setcodes(ctx.Script.Project)));
        var can = $"Duel.IsPlayerCanSpecialSummonMonster(tp,id,{setcode},{types},{a.Int("atk")},{a.Int("def")},{a.Int("level")},{EnumNames.LuaConstant(Race(a))},{EnumNames.LuaConstant(Attribute(a))})";
        var pos = a.Choice("position") switch { "defense" => "POS_FACEUP_DEFENSE", "attack" => "POS_FACEUP_ATTACK", _ => "POS_FACEUP" };
        e.AddCategory("CATEGORY_SPECIAL_SUMMON");
        // As official Trap Monsters check: only when the Trap is really activated (its cost checked), with a free Monster Zone.
        e.TargetChecks.Add("e:IsCostChecked()");
        e.TargetChecks.Add("Duel.GetLocationCount(tp,LOCATION_MZONE)>0");
        e.TargetChecks.Add(can);
        e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,e:GetHandler(),1,0,0)");
        var r = e.NewLocal("r");
        e.AddStep(
        [
            $"local {r}=0",
            $"if {can} then",
            $"\tc:AddMonsterAttribute({(normal ? "TYPE_NORMAL" : "TYPE_EFFECT")}+TYPE_TRAP)",
            $"\t{r}=Duel.SpecialSummon(c,SUMMON_VALUE_SELF,tp,tp,true,false,{pos})",
            "end",
        ], r, ctx.Link);
    }

    /// <summary>A Trap Card that becomes a monster: its other effects may be a monster's, like "If Summoned this way, it cannot attack".</summary>
    public static bool IsTrapMonster(CardProject project) =>
        project.Card.Kind == CardKind.Trap && project.Effects.Any(e => e.Do.Any(b => b.BlockId == BlockId));

    /// <summary>Writes the monster's stats into the Trap's database row, where official Trap Monsters keep them.</summary>
    public static void WriteStats(CardProject project, CdbRow row)
    {
        if (project.UseCustomLua || project.Card.Kind != CardKind.Trap) return;
        var block = project.Effects.SelectMany(e => e.Do).FirstOrDefault(b => b.BlockId == BlockId);
        if (block is null || BlockRegistry.Find(BlockId) is not { } def) return;
        var a = new BlockArgs(def, block);
        row.Atk = a.Int("atk");
        row.Def = a.Int("def");
        row.Level = a.Int("level");
        row.Race = (long)Race(a);
        row.Attribute = (long)Attribute(a);
    }
}

// ---------------------------------------------------------------- the Deck

/// <summary>
/// "Place it on top of the Deck", "place 1 card from your hand on the bottom of the Deck", and "take 1 monster from your
/// Deck and place it on top of your Deck".
/// </summary>
public sealed class PlaceOnDeckAction : CardGroupAction
{
    public const string BlockId = "do.place_on_deck";
    public override string Id => BlockId;
    public override string Name => "Place a card on the top or bottom of the Deck";
    public override string Sentence => "Place {count} {cards} {target} {where} of the Deck";
    public override string Help => "Cards from the field, hand, GY or banishment go back to their owner's Deck (Extra Deck monsters to the Extra Deck). " +
                                   "A card from your own Deck is moved to the top after the Deck is shuffled.";
    protected override string Category => "CATEGORY_TODECK";
    protected override string Hint => "HINTMSG_TODECK";
    protected override string TargetedWording => "place {0}";
    protected override bool AllowsThisCard => true;
    protected override bool AllowsAll => false;
    protected override int MaxCount => 1;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        // One card at a time: "place it on top of the Deck".
        ParamSpec.Int("count", "How many", 1, 1, 1).ShowIf(_ => false),
        new ParamSpec { Key = "cards", Label = "Which card", Type = ParamType.Cards, DefaultFilter = DefaultCards, AllowThisCard = true },
        ParamSpec.Flag("target", "(targeting)", false),
        ParamSpec.Choice("where", "Where", new("top", "on top", "on top of the Deck"), new("bottom", "on the bottom", "on the bottom of the Deck")),
    ];

    private static bool FromDeck(CardFilter f) => !f.ThisCard && f.Zones == Zone.Deck;

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        if (FromDeck(f)) return $"take {Some(args)} and place it on top of your Deck{args.ExceptText()}";
        return $"place {Some(args)} {args.Describe("where")} of the Deck{args.ExceptText()}";
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) => $"place {pronoun} {args.Describe("where")} of the Deck";

    protected override string? AbleCheck(BlockContext ctx) => FromDeck(ctx.Args.Cards("cards")) ? null : "c:IsAbleToDeck()";

    protected override string Apply(string g, BlockContext ctx) =>
        $"Duel.SendtoDeck({g},nil,{(ctx.Args.Choice("where") == "bottom" ? "SEQ_DECKBOTTOM" : "SEQ_DECKTOP")},REASON_EFFECT)";

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Args.Cards("cards");
        if (!f.ThisCard && f.Zones.HasFlag(Zone.Deck))
        {
            if (!FromDeck(f) || f.Owner != CardOwner.You || ctx.Args.Choice("where") != "top" || ctx.Args.Bool("target"))
            {
                ctx.Effect.Error($"\"{Name}\": a card from the Deck can only be taken from your own Deck, without targeting, and placed on top of it.");
                return;
            }
            CompileFromDeck(ctx, ctx.Script.Filter(f));
            return;
        }
        base.Compile(ctx);
    }

    /// <summary>"Take 1 monster from your Deck and place it on top of your Deck": shuffle, then move it to the top.</summary>
    private static void CompileFromDeck(BlockContext ctx, FilterRef f)
    {
        var e = ctx.Effect;
        e.TargetChecks.Add($"Duel.IsExistingMatchingCard({f.Function},tp,LOCATION_DECK,0,1,nil,e,tp)");
        var g = e.NewLocal("g");
        var tc = e.NewLocal("tc");
        var r = e.NewLocal("r");
        e.AddStep(
        [
            $"local {r}=0",
            "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_TODECK)",
            $"local {g}=Duel.SelectMatchingCard(tp,{f.Function},tp,LOCATION_DECK,0,1,1,nil,e,tp)",
            $"local {tc}={g}:GetFirst()",
            $"if {tc} then",
            "\tDuel.ShuffleDeck(tp)",
            $"\tDuel.MoveSequence({tc},SEQ_DECKTOP)",
            "\tDuel.ConfirmDecktop(tp,1)",
            $"\t{r}=1",
            "end",
        ], r, ctx.Link);
    }
}

/// <summary>
/// "Excavate the top 5 cards of your Deck, and if you do, you can add 1 excavated Dinosaur monster to your hand, also send
/// the rest to the GY": the cards are revealed from the top of the Deck, one of them may be taken, and the rest go away.
/// </summary>
public sealed class ExcavateAction : ActionBlock
{
    public const string BlockId = "do.excavate";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "Excavate the top cards of your Deck";
    public override string Sentence => "Excavate the top {count} cards of your Deck, and if you do, you can {take} 1 excavated {cards}, also {rest}";
    public override string Help => "The excavated cards are shown to both players. You may take 1 card that matches; the rest go where you choose.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 3, 1, 10),
        ParamSpec.Choice("take", "Take", new("add", "add", "add it to your hand"), new("special", "Special Summon", "Special Summon it")),
        new ParamSpec { Key = "cards", Label = "Which card", Type = ParamType.Cards, WhereFromBlock = true, DefaultFilter = () => new CardFilter { CardType = FilterCardType.Monster } },
        ParamSpec.Choice("rest", "The rest",
            new("gy", "send the rest to the GY"),
            new("shuffle", "shuffle the rest into the Deck"),
            new("bottom", "place the rest on the bottom of the Deck in any order")),
    ];

    public override string Describe(BlockArgs args)
    {
        var noun = FilterText.Describe(args.Cards("cards"), args.Pack, withLocation: false, ownName: args.CardName);
        var take = args.Choice("take") == "special" ? $"Special Summon 1 excavated {noun}" : $"add 1 excavated {noun} to your hand";
        return $"excavate the top {args.Int("count")} cards of your Deck, and if you do, you can {take}, also {args.Describe("rest")}{args.ExceptText()}";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var n = ctx.Args.Int("count");
        var special = ctx.Args.Choice("take") == "special";
        var rest = ctx.Args.Choice("rest");
        var picked = ctx.Args.Cards("cards").Clone();
        picked.Owner = CardOwner.You;
        picked.Zones = Zone.Deck;
        var f = ctx.Script.Filter(picked, special ? "c:IsCanBeSpecialSummoned(e,0,tp,false,false)" : "c:IsAbleToHand()");
        e.AddCategory(special ? "CATEGORY_SPECIAL_SUMMON" : "CATEGORY_TOHAND");
        if (!special) e.AddCategory("CATEGORY_SEARCH");
        if (rest == "gy") e.AddCategory("CATEGORY_DECKDES");
        e.TargetChecks.Add($"Duel.GetFieldGroupCount(tp,LOCATION_DECK,0)>={n}");
        var g = e.NewLocal("g");
        var sg = e.NewLocal("sg");
        var r = e.NewLocal("r");
        var ask = $"Duel.SelectYesNo(tp,aux.Stringid(id,{Math.Min(e.Index, 16) - 1}))";
        var lines = new List<string>
        {
            $"local {r}=0",
            $"if Duel.GetFieldGroupCount(tp,LOCATION_DECK,0)>={n} then",
            $"\tDuel.ConfirmDecktop(tp,{n})",
            $"\tlocal {g}=Duel.GetDecktopGroup(tp,{n})",
            $"\t{r}={g}:GetCount()",
            $"\tif {(special ? "Duel.GetLocationCount(tp,LOCATION_MZONE)>0 and " : "")}{g}:IsExists({f.Function},1,nil,e,tp) and {ask} then",
            "\t\tDuel.DisableShuffleCheck()",
            $"\t\tDuel.Hint(HINT_SELECTMSG,tp,{(special ? "HINTMSG_SPSUMMON" : "HINTMSG_ATOHAND")})",
            $"\t\tlocal {sg}={g}:FilterSelect(tp,{f.Function},1,1,nil,e,tp)",
        };
        if (special) lines.Add($"\t\tDuel.SpecialSummon({sg},0,tp,tp,false,false,POS_FACEUP)");
        else lines.AddRange([$"\t\tDuel.SendtoHand({sg},nil,REASON_EFFECT)", $"\t\tDuel.ConfirmCards(1-tp,{sg})", "\t\tDuel.ShuffleHand(tp)"]);
        lines.Add($"\t\t{g}:Sub({sg})");
        lines.Add("\tend");
        switch (rest)
        {
            case "gy":
                lines.AddRange(["\tDuel.DisableShuffleCheck()", $"\tDuel.SendtoGrave({g},REASON_EFFECT+REASON_REVEAL)"]);
                break;
            case "shuffle":
                lines.Add("\tDuel.ShuffleDeck(tp)");
                break;
            default:
                // What is left of the excavated cards is still on top: sort them, then move each to the bottom.
                lines.AddRange(
                [
                    $"\tif {g}:GetCount()>0 then",
                    $"\t\tDuel.SortDecktop(tp,tp,{g}:GetCount())",
                    $"\t\tfor i=1,{g}:GetCount() do",
                    "\t\t\tlocal mg=Duel.GetDecktopGroup(tp,1)",
                    "\t\t\tDuel.MoveSequence(mg:GetFirst(),SEQ_DECKBOTTOM)",
                    "\t\tend",
                    "\tend",
                ]);
                break;
        }
        lines.Add("end");
        e.AddStep(lines, r, ctx.Link);
    }
}

/// <summary>"Look at the top 3 cards of your Deck, then place them on top of the Deck in any order."</summary>
public sealed class SortDeckTopAction : ActionBlock
{
    public const string BlockId = "do.sort_deck_top";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "Look at the top cards of a Deck and put them back in any order";
    public override string Sentence => "Look at the top {count} cards of {whose} Deck, then place them on top of the Deck in any order";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 3, 1, 10),
        ParamSpec.Choice("whose", "Whose Deck", new("you", "your"), new("opponent", "your opponent's")),
    ];

    public override void Compile(BlockContext ctx)
    {
        var p = PlayerOf(ctx.Args.Choice("whose"));
        var n = ctx.Args.Int("count");
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.TargetChecks.Add($"Duel.GetFieldGroupCount({p},LOCATION_DECK,0)>={n}");
        ctx.Effect.AddStep([$"local {r}=math.min({n},Duel.GetFieldGroupCount({p},LOCATION_DECK,0))", $"Duel.SortDecktop(tp,{p},{n})"], r, ctx.Link);
    }
}

/// <summary>"Look at your opponent's hand.": the cards are shown to you, then the hand is shuffled.</summary>
public sealed class LookAtHandAction : ActionBlock
{
    public const string BlockId = "do.look_at_hand";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "Look at your opponent's hand";
    public override string Sentence => "Look at your opponent's hand";

    public override void Compile(BlockContext ctx)
    {
        var g = ctx.Effect.NewLocal("g");
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.TargetChecks.Add("Duel.GetFieldGroupCount(tp,0,LOCATION_HAND)>0");
        ctx.Effect.AddStep(
        [
            $"local {g}=Duel.GetFieldGroup(tp,0,LOCATION_HAND)",
            $"local {r}={g}:GetCount()",
            $"if {r}>0 then",
            $"\tDuel.ConfirmCards(tp,{g})",
            "\tDuel.ShuffleHand(1-tp)",
            "end",
        ], r, ctx.Link);
    }
}

// ---------------------------------------------------------------- Equip

/// <summary>
/// "Equip 1 monster from your GY to this card": the monster becomes an Equip Card that only this card can hold, as
/// official monsters that take others as equipment do (EFFECT_EQUIP_LIMIT on the equipped card, owner-related).
/// </summary>
public sealed class EquipToThisAction : ActionBlock
{
    public const string BlockId = "do.equip_to_this";
    public override string Id => BlockId;
    public override string Group => "Equip";
    public override string Name => "Equip a monster to this card";
    public override string Sentence => "Equip 1 {cards} {target} to this card";
    public override string Help => "This card must be a face-up monster on the field when the effect resolves. The monster becomes an Equip Card " +
                                   "in your Spell & Trap Zone that can only be equipped to this card.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monster", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster }),
        ParamSpec.Flag("target", "(targeting)", false),
    ];

    public override string Describe(BlockArgs args) => $"equip 1 {args.Describe("cards")} to this card{args.ExceptText()}";

    public override (string Target, string Action)? DescribeTargeting(BlockArgs args) =>
        args.Bool("target") ? ($"target 1 {args.Describe("cards")}{args.ExceptText()}", "equip it to this card") : null;

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        Zones.RequireMonster(ctx, Name);
        var picked = ctx.Args.Cards("cards").Clone();
        if (picked.ThisCard || picked.Zones == Zone.None) { e.Error($"\"{Name}\": pick where the monster comes from."); return; }
        picked.CardType = FilterCardType.Monster;
        picked.ExcludeThisCard = true;
        var targeting = ctx.Args.Bool("target");
        if (targeting && (picked.Zones & (Zone.Deck | Zone.Hand | Zone.ExtraDeck)) != Zone.None)
        {
            e.Error($"\"{Name}\": cards in the Deck, hand or Extra Deck are hidden and can't be targeted. Turn off targeting or pick the field, GY or banished cards.");
            return;
        }
        var f = ctx.Script.Filter(picked);
        var limit = ctx.Script.AddFunction("eqlimit", "e,c", ["return e:GetOwner()==c"]);
        var category = picked.Zones.HasFlag(Zone.Graveyard) ? "CATEGORY_LEAVE_GRAVE" : "CATEGORY_EQUIP";
        e.AddCategory("CATEGORY_EQUIP");
        if (category != "CATEGORY_EQUIP") e.AddCategory(category);
        e.TargetChecks.Add("Duel.GetLocationCount(tp,LOCATION_SZONE)>0");
        var tc = e.NewLocal("tc");
        var r = e.NewLocal("r");
        var lines = new List<string> { $"local {r}=0" };
        if (targeting)
        {
            e.SetChkc(f.ChkcCheck);
            e.TargetChecks.Add($"Duel.IsExistingTarget({f.Function},tp,{f.Self},{f.Opp},1,{f.Exclude},e,tp)");
            var tg = e.NewLocal("tg");
            e.TargetLines.Add("Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_EQUIP)");
            e.TargetLines.Add($"local {tg}=Duel.SelectTarget(tp,{f.Function},tp,{f.Self},{f.Opp},1,1,{f.Exclude},e,tp)");
            e.TargetLines.Add($"Duel.SetOperationInfo(0,{category},{tg},1,0,0)");
            lines.Add($"local {tc}=Duel.GetFirstTarget()");
            lines.Add($"if Duel.GetLocationCount(tp,LOCATION_SZONE)>0 and c:IsRelateToEffect(e) and c:IsFaceup() and {tc}:IsRelateToEffect(e) and Duel.Equip(tp,{tc},c) then");
        }
        else
        {
            e.TargetChecks.Add($"Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},{f.Opp},1,{f.Exclude},e,tp)");
            if (category == "CATEGORY_LEAVE_GRAVE") e.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_LEAVE_GRAVE,nil,1,{f.InfoPlayer},LOCATION_GRAVE)");
            var g = e.NewLocal("g");
            lines.AddRange(
            [
                "if Duel.GetLocationCount(tp,LOCATION_SZONE)>0 and c:IsRelateToEffect(e) and c:IsFaceup() then",
                "\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_EQUIP)",
                $"\tlocal {g}=Duel.SelectMatchingCard(tp,{f.ForSelection},tp,{f.Self},{f.Opp},1,1,{f.Exclude},e,tp)",
                $"\tlocal {tc}={g}:GetFirst()",
                $"\tif {tc} and Duel.Equip(tp,{tc},c) then",
            ]);
        }
        var ind = targeting ? "\t" : "\t\t";
        lines.AddRange(
        [
            $"{ind}local eq=Effect.CreateEffect(c)",
            $"{ind}eq:SetType(EFFECT_TYPE_SINGLE)",
            $"{ind}eq:SetProperty(EFFECT_FLAG_OWNER_RELATE)",
            $"{ind}eq:SetCode(EFFECT_EQUIP_LIMIT)",
            $"{ind}eq:SetReset(RESET_EVENT+RESETS_STANDARD)",
            $"{ind}eq:SetValue({limit})",
            $"{ind}{tc}:RegisterEffect(eq)",
            $"{ind}{r}=1",
        ]);
        if (!targeting) lines.Add("\tend");
        lines.Add("end");
        e.AddStep(lines, r, ctx.Link);
    }
}

// ---------------------------------------------------------------- the End Phase

/// <summary>
/// "Special Summon 1 monster from your GY, but destroy it during the End Phase": the cards the DO block before this one put
/// on the field are destroyed (or returned, banished...) in the next End Phase, if they are still there.
/// </summary>
public sealed class EndPhaseAction : ActionBlock
{
    public const string BlockId = "do.end_phase";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "...but destroy it during the End Phase";
    public override string Sentence => "but {how} during the End Phase";
    public override string Help => "Put it right after the block whose cards it is about, like \"Special Summon 1 monster from your GY\". " +
                                   "Only cards still on the field, face-up or face-down, that are the same cards then are affected.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "What happens",
            new("destroy", "destroy them"),
            new("hand", "return them to the hand"),
            new("banish", "banish them"),
            new("deck", "shuffle them into the Deck"),
            new("grave", "send them to the GY")),
    ];

    /// <summary>"it" or "them": whether the block before this one is about one card.</summary>
    private static string Pronoun(BlockArgs args)
    {
        var dos = args.Effect?.Do;
        var at = dos?.FindIndex(b => ReferenceEquals(b, args.Instance)) ?? -1;
        if (at <= 0 || BlockRegistry.Find(dos![at - 1].BlockId) is not { } def) return "it";
        return new BlockArgs(def, dos[at - 1], args.Pack).IsPlural ? "them" : "it";
    }

    public override string Describe(BlockArgs args)
    {
        var it = Pronoun(args);
        return args.Choice("how") switch
        {
            "hand" => $"return {it} to the hand during the End Phase",
            "banish" => $"banish {it} during the End Phase",
            "deck" => $"shuffle {it} into the Deck during the End Phase",
            "grave" => $"send {it} to the GY during the End Phase",
            _ => $"destroy {it} during the End Phase",
        };
    }

    public static string Call(string how, string g) => how switch
    {
        "hand" => $"Duel.SendtoHand({g},nil,REASON_EFFECT)",
        "banish" => $"Duel.Remove({g},POS_FACEUP,REASON_EFFECT)",
        "deck" => $"Duel.SendtoDeck({g},nil,SEQ_DECKSHUFFLE,REASON_EFFECT)",
        "grave" => $"Duel.SendtoGrave({g},REASON_EFFECT)",
        _ => $"Duel.Destroy({g},REASON_EFFECT)",
    };

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (e.LastResult is null) { e.Error($"\"{Name}\" needs a DO block before it that puts cards on the field, like \"Special Summon monsters\"."); return; }
        var filter = ctx.Script.AddFunction("epfilter", "c,fid", ["return c:GetFlagEffectLabel(id)==fid"]);
        var con = ctx.Script.AddFunction("epcon", "e,tp,eg,ep,ev,re,r,rp",
        [
            $"if not e:GetLabelObject():IsExists({filter},1,nil,e:GetLabel()) then",
            "\te:GetLabelObject():DeleteGroup()",
            "\te:Reset()",
            "\treturn false",
            "end",
            "return true",
        ]);
        var op = ctx.Script.AddFunction("epop", "e,tp,eg,ep,ev,re,r,rp",
        [
            $"local g=e:GetLabelObject():Filter({filter},nil,e:GetLabel())",
            "e:GetLabelObject():DeleteGroup()",
            Call(ctx.Args.Choice("how"), "g"),
        ]);
        var og = e.NewLocal("og");
        var r = e.NewLocal("r");
        e.AddStep(
        [
            $"local {og}=Duel.GetOperatedGroup():Filter(Card.IsOnField,nil)",
            $"local {r}={og}:GetCount()",
            $"if {r}>0 then",
            "\tlocal fid=e:GetHandler():GetFieldID()",
            $"\tfor tc in aux.Next({og}) do tc:RegisterFlagEffect(id,RESET_EVENT+RESETS_STANDARD,0,1,fid) end",
            $"\t{og}:KeepAlive()",
            "\tlocal ep=Effect.CreateEffect(e:GetHandler())",
            "\tep:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "\tep:SetCode(EVENT_PHASE+PHASE_END)",
            "\tep:SetCountLimit(1)",
            "\tep:SetProperty(EFFECT_FLAG_IGNORE_IMMUNE)",
            "\tep:SetLabel(fid)",
            $"\tep:SetLabelObject({og})",
            $"\tep:SetCondition({con})",
            $"\tep:SetOperation({op})",
            "\tDuel.RegisterEffect(ep,tp)",
            "end",
        ], r, ctx.Link);
    }
}

// ---------------------------------------------------------------- the Chain

/// <summary>
/// "This card's activation and effect cannot be negated.": the activation can't be negated, its effect can't be negated,
/// and (for a Spell/Trap Card's activation) the card's effects can't be negated either, as official cards write it.
/// </summary>
public sealed class CannotBeNegatedCost : CostBlock
{
    public const string BlockId = "cost.cannot_be_negated";
    public override string Id => BlockId;
    public override string Group => "Activation";
    public override string Name => "This activation and effect cannot be negated";
    public override string Sentence => "This activation and effect cannot be negated";
    public override string Help => "Not a cost: it is part of the effect. The card text puts it after the effect.";

    public override string Describe(BlockArgs args) => "";

    public override string Afterword(BlockArgs args) =>
        DetailedTriggers.ActivatesThisCard(args.Effect) ? "This card's activation and effect cannot be negated." : "This effect's activation and effect cannot be negated.";

    /// <summary>The effect's flags: its activation can't be negated, its effect can't be negated, "Prohibition" still works on it.</summary>
    public static readonly string[] Flags = ["EFFECT_FLAG_CANNOT_INACTIVATE", "EFFECT_FLAG_CANNOT_DISABLE", "EFFECT_FLAG_CAN_FORBIDDEN"];

    /// <summary>The Spell/Trap Card's own "its effects cannot be negated" (EFFECT_CANNOT_DISABLE on itself).</summary>
    public const string CardLine = "do local cd=Effect.CreateEffect(c) cd:SetType(EFFECT_TYPE_SINGLE) cd:SetCode(EFFECT_CANNOT_DISABLE) " +
                                   "cd:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE) c:RegisterEffect(cd) end";

    public override void Compile(BlockContext ctx)
    {
        foreach (var flag in Flags) ctx.Effect.AddProperty(flag);
        if (DetailedTriggers.ActivatesThisCard(ctx.Effect.Source)) ctx.Script.AddInitialLine(CardLine);
    }
}

// ---------------------------------------------------------------- control

/// <summary>
/// "Target 1 monster you control and 1 monster your opponent controls; switch control of both monsters.": both must be
/// able to change control, with a zone free for each, as official cards check it.
/// </summary>
public sealed class SwapControlAction : ActionBlock
{
    public const string BlockId = "do.swap_control";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Switch control of 1 monster you control and 1 your opponent controls";
    public override string Sentence => "Target 1 {mine} you control and 1 {theirs} your opponent controls; switch control of both monsters";
    public override string Help => "Always targets both. It is the effect's targeting, so no other DO block of the effect can target.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new ParamSpec { Key = "mine", Label = "Your monster", Type = ParamType.Cards, WhereFromBlock = true, DefaultFilter = () => new CardFilter { CardType = FilterCardType.Monster } },
        new ParamSpec { Key = "theirs", Label = "Your opponent's monster", Type = ParamType.Cards, WhereFromBlock = true, DefaultFilter = () => new CardFilter { CardType = FilterCardType.Monster } },
    ];

    private static CardFilter Side(BlockArgs args, string key, CardOwner owner)
    {
        var f = args.Cards(key).Clone();
        f.Owner = owner;
        f.Zones = Zone.MonsterZone;
        f.CardType = FilterCardType.Monster;
        return f;
    }

    private static string Noun(BlockArgs args, string key) =>
        FilterText.Describe(Side(args, key, CardOwner.You), args.Pack, withLocation: false, ownName: args.CardName, withExcept: false);

    public override string Describe(BlockArgs args) => "";

    public override (string Target, string Action)? DescribeTargeting(BlockArgs args) =>
        ($"target 1 {Noun(args, "mine")} you control and 1 {Noun(args, "theirs")} your opponent controls{args.ExceptText()}", "switch control of both monsters");

    /// <summary>What both monsters must be able to do: change control, with a free Monster Zone on the side they go to.</summary>
    public const string Able = "c:IsAbleToChangeControler() and Duel.GetMZoneCount(c:GetControler(),c,c:GetControler(),LOCATION_REASON_CONTROL)>0";

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var mine = ctx.Script.Filter(Side(ctx.Args, "mine", CardOwner.You), Able);
        var theirs = ctx.Script.Filter(Side(ctx.Args, "theirs", CardOwner.Opponent), Able);
        e.AddCategory("CATEGORY_CONTROL");
        // Two targets: the engine never re-checks a single one.
        e.SetChkc("false");
        e.TargetChecks.Add($"Duel.IsExistingTarget({mine.Function},tp,LOCATION_MZONE,0,1,{mine.Exclude},e,tp)");
        e.TargetChecks.Add($"Duel.IsExistingTarget({theirs.Function},tp,0,LOCATION_MZONE,1,{theirs.Exclude},e,tp)");
        var g1 = e.NewLocal("g");
        var g2 = e.NewLocal("g");
        e.TargetLines.AddRange(
        [
            "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_CONTROL)",
            $"local {g1}=Duel.SelectTarget(tp,{mine.Function},tp,LOCATION_MZONE,0,1,1,{mine.Exclude},e,tp)",
            "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_CONTROL)",
            $"local {g2}=Duel.SelectTarget(tp,{theirs.Function},tp,0,LOCATION_MZONE,1,1,{theirs.Exclude},e,tp)",
            $"{g1}:Merge({g2})",
            $"Duel.SetOperationInfo(0,CATEGORY_CONTROL,{g1},2,0,0)",
        ]);
        var g = e.NewLocal("g");
        var r = e.NewLocal("r");
        e.AddStep(
        [
            $"local {r}=0",
            $"local {g}=Duel.GetChainInfo(0,CHAININFO_TARGET_CARDS)",
            $"local {g}a,{g}b={g}:GetFirst(),{g}:GetNext()",
            $"if {g}a:IsRelateToEffect(e) and {g}b:IsRelateToEffect(e) and Duel.SwapControl({g}a,{g}b) then {r}=2 end",
        ], r, ctx.Link);
    }
}

// ---------------------------------------------------------------- Types and Attributes

/// <summary>
/// "All face-up monsters on the field become DARK.", "All monsters your opponent controls become Plant monsters.": a
/// lasting change of Type or Attribute for the monsters a picker allows.
/// </summary>
public sealed class BecomePassive : PassiveBlock
{
    public const string BlockId = "passive.become";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Monsters become a Type or Attribute";
    public override string Sentence => "{cards} become {what} {race} {attribute}";
    public override string Help => "Like Zombie World or Kitsuni Kitsunebi. As on official cards, monsters picked only by where they are " +
                                   "(\"all face-up monsters on the field\") change even while face-down; other conditions are checked.";

    private static readonly ChoiceOption[] RaceChoices = MonsterChoices.Races(r => EnumNames.Display(r) + " monsters");

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true }, Zone.MonsterZone),
        ParamSpec.Choice("what", "Become", new("race", "", "a Type"), new("attribute", "", "an Attribute")),
        new ParamSpec { Key = "race", Label = "Type", Type = ParamType.Choice, Default = RaceChoices[0].Value, Choices = RaceChoices }.ShowIf(b => b.Values.GetValueOrDefault("what", "race") == "race"),
        new ParamSpec { Key = "attribute", Label = "Attribute", Type = ParamType.Choice, Default = MonsterChoices.Attributes[0].Value, Choices = MonsterChoices.Attributes }.ShowIf(b => b.Values.GetValueOrDefault("what", "race") == "attribute"),
    ];

    public override string Describe(BlockArgs args)
    {
        var noun = FilterText.Describe(args.Cards("cards"), args.Pack, plural: true, preposition: "", ownName: args.CardName, withExcept: false);
        var value = args.Choice("what") == "attribute" ? args.Describe("attribute") : args.Describe("race");
        return $"all {noun} become {value}{args.ExceptText()}";
    }

    /// <summary>Whether the picker asks more than where the monsters are (and that they are face-up), so the effect needs a target check.</summary>
    public static bool NeedsTarget(CardFilter f)
    {
        var plain = f.Clone();
        plain.FaceUpOnly = false;
        return f.ExcludeThisCard || FilterCompiler.Conditions(plain).Any(c => c != "c:IsType(TYPE_MONSTER)");
    }

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards").Clone();
        filter.Zones = Zone.MonsterZone;
        filter.CardType = FilterCardType.Monster;
        var f = ctx.Script.Filter(filter);
        var range = RangeOf(ctx);
        var attribute = ctx.Args.Choice("what") == "attribute";
        var value = attribute
            ? EnumNames.LuaConstant(Enum.Parse<CardAttribute>(ctx.Args.Choice("attribute")))
            : EnumNames.LuaConstant(Enum.Parse<CardRace>(ctx.Args.Choice("race")));
        var target = NeedsTarget(filter)
            ? ctx.Script.AddFunction("becometg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")])
            : null;
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD)",
            $"{v}:SetCode({(attribute ? "EFFECT_CHANGE_ATTRIBUTE" : "EFFECT_CHANGE_RACE")})",
            $"{v}:SetRange({range})",
            $"{v}:SetTargetRange({f.Self},{f.Opp})",
            .. target is null ? Array.Empty<string>() : new[] { $"{v}:SetTarget({target})" },
            $"{v}:SetValue({value})",
        ]);
    }
}

// ---------------------------------------------------------------- both players

/// <summary>"Inflict 500 damage to both players.": both at the same time, as official cards do it (Duel.RDComplete).</summary>
public sealed class DamageBothAction : ActionBlock
{
    public const string BlockId = "do.damage_both";
    public override string Id => BlockId;
    public override string Group => "Life Points";
    public override string Name => "Inflict damage to both players";
    public override string Sentence => "Inflict {amount} damage to both players";
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Int("amount", "Amount", 500, 1, 99999)];

    public override void Compile(BlockContext ctx)
    {
        var n = ctx.Args.Int("amount");
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory("CATEGORY_DAMAGE");
        ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_DAMAGE,nil,0,PLAYER_ALL,{n})");
        ctx.Effect.AddStep(
        [
            $"local {r}=Duel.Damage(tp,{n},REASON_EFFECT,true)",
            $"{r}={r}+Duel.Damage(1-tp,{n},REASON_EFFECT,true)",
            "Duel.RDComplete()",
        ], r, ctx.Link);
    }
}
