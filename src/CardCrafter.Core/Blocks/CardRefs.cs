using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "That card": a DO step acting on cards the effect already has, as card text says "destroy it", "Special Summon that
/// monster to your field", "it loses ATK equal to the sent monster's ATK": this effect's target(s), the card(s) the step
/// before affected, the card(s) used for the cost, or a battling monster. A card picker in this mode (<see
/// cref="CardFilter.That"/>) picks nothing; what it says about the cards (a Fusion Monster, a Dragon) is a check they must
/// pass. Such a step has no activation check that the cards exist, as official scripts have none: they are only known as
/// the effect resolves. Values ("the targeted monster's ATK") and picker requirements ("with a different Attribute from that
/// monster") read the same cards.
/// </summary>
public static class CardRefs
{
    /// <summary>The picker's "Which cards" choices, for the editor.</summary>
    public static readonly (CardRef Ref, string Ui)[] Choices =
    [
        (CardRef.None, "The cards picked below"),
        (CardRef.Target, "This effect's target(s)"),
        (CardRef.Affected, "The card(s) the step before affected"),
        (CardRef.Cost, "The card(s) used for the cost"),
        (CardRef.BattleTarget, "The monster this card is battling (or destroyed by battle)"),
        (CardRef.Battling, "Your monster that is battling"),
        (CardRef.Attacker, "The attacking monster (whose attack this effect answers)"),
    ];

    /// <summary>
    /// "Destroy it and all monsters ...", "return both it and this card to the hand", "Special Summon this card and 1 ...
    /// from your Deck": this card or this effect's target(s) are part of the same action. A Special Summon of this card
    /// together with other monsters Summons both or neither.
    /// </summary>
    public static ParamSpec PlusParam => ParamSpec.Choice("plus", "Together with",
            new("none", "", "(nothing else)"), new("this", "", "and this card (both)"), new("target", "", "and this effect's target(s)"))
        .ShowIf(b => b.Values.GetValueOrDefault("plus", "none") != "none" || b.BlockId == "do.special_summon"
                     || b.Values.GetValueOrDefault("mode") == "all" || b.Filters.TryGetValue("cards", out var f) && f.That != CardRef.None);

    public static string Plus(BlockArgs args) => args.Definition.Param("plus") is null ? "none" : args.Choice("plus");

    /// <summary>The block's own cards are "that card".</summary>
    public static bool Is(BlockArgs args) => args.Definition.Param("cards") is { Type: ParamType.Cards } && args.Cards("cards").That != CardRef.None;

    // ---------------------------------------------------------------- card text

    /// <summary>The block of the effect that targets: a DO block with "targeting" ticked, or "Target cards".</summary>
    private static BlockInstance? Targeting(EffectEvent? effect) =>
        effect?.Do.FirstOrDefault(b => b.Values.GetValueOrDefault("target") == "true" || b.BlockId is TargetCardsAction.BlockId or PairTargetAction.BlockId);

    /// <summary>The DO block before <paramref name="args"/>' block.</summary>
    private static BlockArgs? Previous(BlockArgs args)
    {
        var dos = args.Effect?.Do;
        var at = dos?.FindIndex(b => ReferenceEquals(b, args.Instance)) ?? -1;
        if (at <= 0) return null;
        // An "only if" or IF gate in between says nothing about the cards: the step before it is meant.
        for (var i = at - 1; i >= 0; i--)
        {
            if (BlockRegistry.Find(dos![i].BlockId) is not { } def) return null;
            if (def is OnlyIfAction or ConditionBlock) continue;
            return new BlockArgs(def, dos[i], args.Pack) { Effect = args.Effect, CardName = args.CardName };
        }
        return null;
    }

    /// <summary>The cost block whose cards the effect keeps: the first one that moves cards.</summary>
    private static BlockInstance? CostBlock(EffectEvent? effect) =>
        effect?.Cost.FirstOrDefault(b => b.BlockId is "cost.discard" or "cost.tribute" or "cost.move_cards" or "cost.this_card" or "cost.detach" or RevealCost.BlockId);

    /// <summary>Whether "that card" means several cards: several targets, cards the step before affected, or cost cards.</summary>
    public static bool Plural(BlockArgs args, CardRef that)
    {
        switch (that)
        {
            case CardRef.Target:
                if (Targeting(args.Effect) is not { } t || BlockRegistry.Find(t.BlockId) is not { } td) return false;
                if (td is PairTargetAction) return true;
                var ta = new BlockArgs(td, t, args.Pack) { Effect = args.Effect };
                return ta.IsPlural || ta.CountMode == "up_to" && ta.Definition.Param("count") is not null && ta.Int("count") > 1;
            case CardRef.Affected:
                return Previous(args) is { } prev && !prev.IsThisCard && prev.Definition.Id != "do.special_summon_self" && prev.IsPlural;
            case CardRef.Cost:
                return CostBlock(args.Effect) is { } c && int.TryParse(c.Values.GetValueOrDefault("count", "1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 1;
            default:
                return false;
        }
    }

    /// <summary>"monster" or "card", as the cost or the step before names its cards.</summary>
    private static string Noun(BlockInstance b, bool plural)
    {
        var monster = b.BlockId is "cost.tribute" or "do.special_summon" or "do.tribute"
                      || b.Filters.TryGetValue("cards", out var f) && (f.CardType == FilterCardType.Monster || f.Zones == Zone.MonsterZone);
        return (monster ? "monster" : "card") + (plural ? "s" : "");
    }

    /// <summary>"the sent monster", "the discarded card", "the Tributed monster", "the banished monster": the cost's cards.</summary>
    public static string CostNoun(EffectEvent? effect, bool plural = false)
    {
        if (CostBlock(effect) is not { } b) return plural ? "the cards used for the cost" : "the card used for the cost";
        if (b.BlockId == "cost.this_card" || b.Filters.TryGetValue("cards", out var self) && self.ThisCard) return "this card";
        var noun = Noun(b, plural);
        var verb = b.BlockId switch
        {
            "cost.discard" => "discarded",
            "cost.tribute" => "Tributed",
            "cost.detach" => "detached",
            RevealCost.BlockId => "revealed",
            _ => b.Values.GetValueOrDefault("how", "banish") switch
            {
                "grave" => "sent", "deck" or "deck_bottom" or "hand" or "extra" => "returned", _ => "banished",
            },
        };
        return b.BlockId == "cost.detach" ? $"the detached material{(plural ? "s" : "")}" : $"the {verb} {noun}";
    }

    /// <summary>Whether the effect starts when this card destroys a monster by battle, where card text says "that monster".</summary>
    private static bool DestroysByBattle(EffectEvent? effect) => effect?.When?.BlockId == "when.destroys_by_battle";

    /// <summary>"it", "them", "the sent monster", "that monster", "your battling monster": the picker's cards, as card text names them.</summary>
    public static string Words(BlockArgs args, CardFilter f)
    {
        var plural = Plural(args, f.That);
        var words = f.That switch
        {
            CardRef.Target or CardRef.Affected => plural ? "them" : "it",
            CardRef.Cost => CostNoun(args.Effect, plural),
            CardRef.BattleTarget => DestroysByBattle(args.Effect) ? "that monster" : "the monster this card is battling",
            CardRef.Battling => "your battling monster",
            CardRef.Attacker => "the attacking monster",
            _ => "",
        };
        return Plus(args) == "this" ? $"both {words} and this card" : words;
    }

    /// <summary>
    /// The count's words with the cards that go with the block's own: "this card from your hand and 1" ("Special Summon
    /// this card from your hand and 1 "Rikka" monster from your Deck"), "it and all" ("destroy it and all monsters ...").
    /// </summary>
    public static string WithPlus(BlockArgs args, string count)
    {
        if (Is(args)) return "";
        if (args.IsThisCard) return count;
        return Plus(args) switch
        {
            "this" => (args.TriggerZone is { } z && Zones.From(z) is { } from ? $"this card {from} and " : "this card and ") + count,
            "target" => (Plural(args, CardRef.Target) ? "them and " : "it and ") + count,
            _ => count,
        };
    }

    /// <summary>"the targeted monster's", "the sent monster's", "that monster's": whose stat a value reads.</summary>
    public static string Whose(CardRef that, EffectEvent? effect) => that switch
    {
        CardRef.Target => "the targeted monster's",
        CardRef.Cost => CostNoun(effect) + "'s",
        CardRef.Attacker => "the attacking monster's",
        _ => "that monster's",
    };

    // ---------------------------------------------------------------- compiling

    /// <summary>
    /// The Lua group of the cards "that card" means as the effect resolves (not yet checked against the picker), or null
    /// after an error. Battling monsters are related to the effect when it is activated, as official scripts relate them
    /// (Duel.SetTargetCard), so a monster that left in the meantime is not used.
    /// </summary>
    public static string? GroupLua(BlockContext ctx, CardRef that)
    {
        var e = ctx.Effect;
        var name = ctx.Args.Definition.Name;
        switch (that)
        {
            case CardRef.Target:
                if (!Memory.Targets(e)) { e.Error($"\"{name}\": \"this effect's target\" needs a DO block before it that targets (or \"Target cards\")."); return null; }
                return "Duel.GetChainInfo(0,CHAININFO_TARGET_CARDS):Filter(Card.IsRelateToEffect,nil,e)";
            case CardRef.Affected:
                if (e.LastResult is null) { e.Error($"\"{name}\": \"the cards the step before affected\" needs a DO block before it that moves, destroys or Summons cards."); return null; }
                return "Duel.GetOperatedGroup():Clone()";
            case CardRef.Cost:
                if (CostBlock(e.Source) is null) { e.Error($"\"{name}\": \"the cards used for the cost\" needs a COST block that discards, Tributes, banishes, sends or detaches cards."); return null; }
                return "(e:GetLabelObject() or Group.CreateGroup())";
            case CardRef.BattleTarget or CardRef.Battling or CardRef.Attacker:
                if (!e.TargetLines.Contains(RelateLine(that))) e.TargetLines.Add(RelateLine(that));
                if (!IsBattle(e)) e.Warn($"\"{name}\": a battling monster is only there during battle; use a WHEN about battle, like \"If this card destroys a monster by battle\".");
                var card = Card(that, "c");
                return $"({card} and Group.FromCards({card}) or Group.CreateGroup()):Filter(Card.IsRelateToEffect,nil,e)";
            default:
                return null;
        }
    }

    private static bool IsBattle(EffectBuilder e) =>
        e.Code is { } code && (code.Contains("BATTLE", StringComparison.Ordinal) || code.Contains("ATTACK", StringComparison.Ordinal) || code.Contains("DAMAGE", StringComparison.Ordinal))
        || e.Source.When?.BlockId is "when.destroys_by_battle" or "when.battle" or "when.attack_declared";

    /// <summary>The battling monster: this card's battle target, or your monster that is battling (one of the two Duel.GetBattleMonster gives).</summary>
    public static string Card(CardRef that, string self = "e:GetHandler()") => that switch
    {
        CardRef.BattleTarget => $"{self}:GetBattleTarget()",
        CardRef.Attacker => "Duel.GetAttacker()",
        _ => "(Duel.GetBattleMonster(tp))",
    };

    /// <summary>Relates the battling monster to the effect as it is activated.</summary>
    private static string RelateLine(CardRef that) => $"if {Card(that)} then {Card(that)}:CreateEffectRelation(e) end";

    /// <summary>
    /// The activation check of a step on "that card": only a battling monster is known when the effect is activated (as
    /// official scripts check it); targets, the cards the step before affects and cost cards are not, so there is none.
    /// </summary>
    public static string? ActivationCheck(CardRef that, string function) =>
        that is CardRef.BattleTarget or CardRef.Battling or CardRef.Attacker ? $"{Card(that)}~=nil and {function}({Card(that)},e,tp)" : null;

    /// <summary>
    /// The group a picker requirement compares with ("with a different Attribute from that monster"), or nil while the
    /// effect is being activated: the requirement holds until the cards are known, as the "declared" ones do.
    /// </summary>
    public static string GuardedGroup(CardRef that) => that switch
    {
        CardRef.Target => "(function(te,tg) if te~=e or not tg then return nil end return tg:Filter(Card.IsRelateToEffect,nil,e) end)(Duel.GetChainInfo(0,CHAININFO_TRIGGERING_EFFECT,CHAININFO_TARGET_CARDS))",
        CardRef.Affected => "(function(te) if te~=e then return nil end return Duel.GetOperatedGroup() end)(Duel.GetChainInfo(0,CHAININFO_TRIGGERING_EFFECT))",
        CardRef.Cost => "(function(te) if te~=e then return nil end return e:GetLabelObject() end)(Duel.GetChainInfo(0,CHAININFO_TRIGGERING_EFFECT))",
        _ => $"(function(bc) return bc and Group.FromCards(bc) or nil end)({Card(that)})",
    };

    /// <summary>
    /// The picker requirements that compare a card with "that card": same/different Attribute, Type, Level, name, original
    /// Attribute/Level/name, or ATK at most its ATK. Keys of <see cref="CardProperties"/>.
    /// </summary>
    public static bool IsRequirement(string key) => key.StartsWith("ref_", StringComparison.Ordinal);

    /// <summary>The Lua test of a requirement comparing card c with the referred card(s).</summary>
    public static string RequirementLua(string key, CardRef that, bool not)
    {
        var same = key switch
        {
            "ref_attribute" => "c:IsAttribute(rc:GetAttribute())",
            "ref_race" => "c:IsRace(rc:GetRace())",
            "ref_level" => "c:GetLevel()>0 and c:GetLevel()==rc:GetLevel()",
            "ref_name" => "c:IsCode(rc:GetCode())",
            "ref_orig_attribute" => "c:GetOriginalAttribute()&rc:GetOriginalAttribute()~=0",
            "ref_orig_level" => "c:GetOriginalLevel()>0 and c:GetOriginalLevel()==rc:GetOriginalLevel()",
            "ref_orig_name" => "c:IsOriginalCodeRule(rc:GetOriginalCodeRule())",
            "ref_mentioned" => "(aux.IsMaterialListCode(rc,c:GetCode()) or aux.IsCodeListed(rc,c:GetCode()))",
            _ => "c:IsAttackBelow(rc:GetAttack())",
        };
        // "A different Attribute from that monster": there must be that monster to differ from.
        var test = not ? $"rg:GetCount()>0 and not rg:IsExists(function(rc) return {same} end,1,nil)" : $"rg:IsExists(function(rc) return {same} end,1,nil)";
        return $"(function(rg) if not rg then return true end return {test} end)({GuardedGroup(that)})";
    }

    /// <summary>The choices of a requirement on "that card": which card it compares with (the card text calls it "that monster").</summary>
    public static IReadOnlyList<(long Value, string Label, string Lua)> RequirementChoices() =>
        Choices.Where(c => c.Ref != CardRef.None).Select(c => ((long)c.Ref, c.Ui[0..1].ToLowerInvariant() + c.Ui[1..], "")).ToList();

    /// <summary>
    /// Whether the effect reads the cards used for its cost (a picker, a requirement or a value), which its cost then
    /// keeps in the effect's label object. Older cards never do, so they compile as before.
    /// </summary>
    public static bool UsesCost(EffectEvent effect) => effect.Do.Concat(effect.If).Any(b =>
        b.Filters.Values.Any(f => f.That == CardRef.Cost || f.Requirements.Any(r => IsRequirement(r.Property) && r.Values.Contains((long)CardRef.Cost)))
        || b.Values.Values.Any(v => v.StartsWith("cost_", StringComparison.Ordinal)));

    /// <summary>
    /// After a COST block: keeps the cards it moved (Duel.GetOperatedGroup) in the effect's label object, for the steps
    /// that use "the cards used for the cost". <paramref name="from"/> is where its cost lines begin.
    /// </summary>
    public static void KeepCost(EffectBuilder e, BlockInstance cost, int from)
    {
        // A revealed card isn't moved: the reveal keeps it itself.
        if (e.IsSummonProcedure || cost.BlockId == RevealCost.BlockId || !UsesCost(e.Source) || !ReferenceEquals(CostBlock(e.Source), cost)) return;
        if (!e.CostLines.Skip(from).Any(l => l.Contains("Duel.", StringComparison.Ordinal) || l.Contains("RemoveOverlayCard(", StringComparison.Ordinal))) return;
        e.CostLines.Add("local cg=Duel.GetOperatedGroup():Clone()");
        e.CostLines.Add("cg:KeepAlive()");
        e.CostLines.Add("e:SetLabelObject(cg)");
    }

    /// <summary>Checks a DO or IF block's requirements on "that card" have the cards they compare with.</summary>
    public static void Check(EffectBuilder e, BlockInstance b)
    {
        foreach (var r in b.Filters.Values.SelectMany(f => f.Requirements).Where(r => IsRequirement(r.Property)))
        {
            var that = (CardRef)r.Values.FirstOrDefault();
            if (that == CardRef.Target && !Memory.Targets(e) && !TargetsLater(e.Source, b))
                e.Error("A requirement compares with \"this effect's target\", but no DO block of this effect targets.");
            if (that == CardRef.Cost && CostBlock(e.Source) is null)
                e.Error("A requirement compares with \"the card used for the cost\", but no COST block of this effect discards, Tributes, banishes or sends cards.");
            if (that == CardRef.None) e.Error("A requirement compares with \"that monster\": pick which card it is.");
        }
    }

    /// <summary>The block itself targets (its requirement then compares with its own target once it is chosen).</summary>
    private static bool TargetsLater(EffectEvent effect, BlockInstance b) => ReferenceEquals(Targeting(effect), b);

    /// <summary>The value sources about "that card", for <see cref="ValueSources"/>.</summary>
    public static string? StatLua(BlockContext ctx, string source)
    {
        var (that, stat) = source switch
        {
            "target_atk" => (CardRef.Target, "tc:GetAttack()"),
            "target_def" => (CardRef.Target, "tc:GetDefense()"),
            "target_level" => (CardRef.Target, "tc:GetLevel()"),
            "target_rank" => (CardRef.Target, "tc:GetRank()"),
            // As it was on the field when it was Tributed or detached there; otherwise as it is now.
            "cost_atk" => (CardRef.Cost, "(tc:IsPreviousLocation(LOCATION_MZONE) and tc:GetPreviousAttackOnField() or tc:GetAttack())"),
            "cost_def" => (CardRef.Cost, "(tc:IsPreviousLocation(LOCATION_MZONE) and tc:GetPreviousDefenseOnField() or tc:GetDefense())"),
            "cost_level" => (CardRef.Cost, "(tc:IsPreviousLocation(LOCATION_MZONE) and tc:GetPreviousLevelOnField() or tc:GetLevel())"),
            // Number 39: Utopia Roots: "this card gains ATK equal to that monster's Rank x 500" (the monster whose attack was negated).
            "attacker_atk" => (CardRef.Attacker, "tc:GetAttack()"),
            "attacker_rank" => (CardRef.Attacker, "tc:GetRank()"),
            _ => (CardRef.None, ""),
        };
        if (that == CardRef.None) return null;
        if (GroupLua(ctx, that) is not { } group) return "0";
        var fn = ctx.Script.AddFunction("refstat", "g", ["local n=0", "for tc in aux.Next(g) do", $"\tn=n+math.max(0,{stat})", "end", "return n"]);
        return $"{fn}({group})";
    }
}

/// <summary>
/// "Target 1 "Kashtira" monster you control; Special Summon 1 "Kashtira" monster with a different Attribute from your Deck":
/// targets cards and does nothing to them itself. The DO blocks after it act on "this effect's target" or compare with it.
/// </summary>
public sealed class TargetCardsAction : ActionBlock
{
    public const string BlockId = "do.target";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "Target cards (for the steps after)";
    public override string Sentence => "Target {count} {cards}";
    public override string Help => "Targets cards without doing anything to them: the DO blocks after it act on them (\"This effect's target(s)\" in their " +
                                   "card picker), compare with them (\"with a different Attribute from that monster\") or use their ATK or Level.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ParamSpec.Choice("mode", "Exactly / up to", new("exact", "", "exactly"), new("up_to", "up to")),
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true }),
    ];

    public override (string Target, string Action)? DescribeTargeting(BlockArgs args) => (Describe(args), "");

    /// <summary>"target 1 "Kashtira" monster you control", "target 1 other EARTH Machine monster in your GY".</summary>
    public override string Describe(BlockArgs args) =>
        $"target {args.Describe("count")} {FilterText.Describe(args.Cards("cards"), args.Pack, args.IsPlural, "in", ownName: args.CardName, withExcept: false)}{args.ExceptText()}";

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var filter = ctx.Args.Cards("cards");
        if (filter.IsSpecific() || filter.Zones == Zone.None || (filter.Zones & (Zone.Deck | Zone.Hand | Zone.ExtraDeck)) != Zone.None)
        {
            e.Error($"\"{Name}\": targets are cards on the field, in the GY or banished. Pick those places.");
            return;
        }
        var f = ctx.Script.Filter(filter);
        var n = ctx.Args.Int("count");
        var min = ctx.Args.Choice("mode") == "up_to" ? 1 : n;
        e.SetChkc(min == 1 ? f.ChkcCheck : "false");
        e.TargetChecks.Add($"Duel.IsExistingTarget({f.Function},tp,{f.Self},{f.Opp},{min},{f.Exclude},e,tp)");
        e.TargetLines.Add("Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_TARGET)");
        e.TargetLines.Add($"Duel.SelectTarget(tp,{f.Function},tp,{f.Self},{f.Opp},{min},{n},{f.Exclude},e,tp)");
    }
}
