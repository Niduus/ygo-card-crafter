using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

public abstract class ActionBlock : BlockDefinition
{
    public override BlockKind Kind => BlockKind.Action;

    protected static string PlayerOf(string who) => who == "opponent" ? "1-tp" : "tp";

    /// <summary>"exactly 3", "up to 3" or "all": how many cards a card action takes.</summary>
    public static ParamSpec ModeParam => ParamSpec.Choice("mode", "Exactly / up to / all",
        new("exact", "", "exactly"), new("up_to", "up to"), new("all", "all"));

    /// <summary>"for each of those cards": an amount multiplied by how many cards the cost or the action before used.</summary>
    public static ParamSpec PerParam => ParamSpec.Choice("per", "For each",
        new("once", "", "(once)"),
        new("per_previous", "for each of those cards", "for each card the action before affected"),
        new("per_cost", "for each card used for the cost", "for each card used for the cost"));

    /// <summary>The multiplier for an amount: "" for once, or "*n" for each card of the cost or the action before.</summary>
    protected static string Multiplier(BlockContext ctx, bool inTarget = false)
    {
        if (ctx.Args.Definition.Param("per") is null) return "";
        switch (ctx.Args.Choice("per"))
        {
            case "per_cost":
                if (!ctx.Effect.CostCounts) { ctx.Effect.Error("\"For each card used for the cost\" needs a COST block that sends, banishes or moves cards."); return ""; }
                return "*e:GetLabel()";
            case "per_previous":
                if (inTarget) return "";
                if (ctx.Effect.LastResult is not { } r) { ctx.Effect.Error("\"For each of those cards\" needs a DO block before it that affects cards."); return ""; }
                return "*" + r;
            default:
                return "";
        }
    }
}

/// <summary>
/// The shared shape of "do something to N cards": pick them (targeting or not), check they exist
/// on activation, then move or destroy them on resolution.
/// </summary>
public abstract class CardGroupAction : ActionBlock
{
    public override string Group => "Cards";
    protected abstract string Category { get; }
    protected abstract string Hint { get; }
    /// <summary>A Lua check each card must pass, e.g. "c:IsAbleToHand()", or null.</summary>
    protected virtual string? AbleCheck(BlockContext ctx) => null;
    /// <summary>The Lua call that applies the action to a group, returning how many cards it affected.</summary>
    protected abstract string Apply(string group, BlockContext ctx);
    protected virtual IEnumerable<string> After(string group, FilterRef f) => [];
    protected virtual IEnumerable<string> ExtraCategories(CardFilter f) => [];
    /// <summary>An extra activation check, e.g. free Monster Zones. Also guards the resolution.</summary>
    protected virtual string? SpaceCheck(int count, CardFilter f, BlockContext ctx) => null;
    protected abstract Func<CardFilter> DefaultCards { get; }
    protected virtual int MaxCount => 10;
    /// <summary>Wording after the target clause, e.g. "destroy {0}" where {0} is "it" or "them".</summary>
    protected abstract string TargetedWording { get; }

    public override (string Target, string Action)? DescribeTargeting(BlockArgs args)
    {
        if (Param("target") is null || !args.Bool("target")) return null;
        var single = !args.IsPlural && !(Param("mode") is not null && args.Choice("mode") == "up_to" && args.Int("count") > 1);
        var target = $"target {args.Describe("count")} {args.Describe("cards")}{args.ExceptText()}";
        return (target, TargetedAction(args, single ? "it" : "them", single));
    }

    /// <summary>What follows the semicolon, e.g. "destroy it". Override when the wording needs more blanks.</summary>
    protected virtual string TargetedAction(BlockArgs args, string pronoun, bool single)
    {
        var action = string.Format(System.Globalization.CultureInfo.InvariantCulture, TargetedWording, pronoun);
        if (Param("position") is not null && args.Describe("position") is { Length: > 0 } pos) action += " " + pos;
        return action;
    }

    public override IReadOnlyList<ParamSpec> Params => _params ??= BuildParams();
    private IReadOnlyList<ParamSpec>? _params;

    /// <summary>Whether the card picker offers "This card itself" (move, destroy or Summon this exact card).</summary>
    protected virtual bool AllowsThisCard => false;

    /// <summary>Whether "all" makes sense (it doesn't for Special Summons, which need a free zone for each card).</summary>
    protected virtual bool AllowsAll => true;

    protected virtual IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Int("count", "How many", 1, 1, MaxCount),
        ModeParam,
        new ParamSpec { Key = "cards", Label = "Which cards", Type = ParamType.Cards, DefaultFilter = DefaultCards, AllowThisCard = AllowsThisCard },
        ParamSpec.Flag("target", "(targeting)", false),
    ];

    private string Mode(BlockContext ctx) => Param("mode") is null ? "exact" : ctx.Args.Choice("mode");

    /// <summary>The action on this card itself: wherever the effect is used from, only if it is still there.</summary>
    private void CompileThisCard(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (Param("target") is not null && ctx.Args.Bool("target"))
            e.Error($"\"{Name}\": this card itself isn't targeted. Turn off targeting.");
        if (Category.Length > 0) e.AddCategory(Category);
        var able = AbleCheck(ctx)?.Replace("c:", "e:GetHandler():", StringComparison.Ordinal);
        var space = SpaceCheck(1, new CardFilter { Zones = Zone.Hand }, ctx);
        if (able is not null) e.TargetChecks.Add(able);
        if (space is not null) e.TargetChecks.Add(space);
        if (Category.Length > 0) e.TargetLines.Add($"Duel.SetOperationInfo(0,{Category},e:GetHandler(),1,0,0)");
        var g = e.NewLocal("g");
        var r = e.NewLocal("r");
        var body = new List<string>
        {
            $"local {r}=0",
            $"if c:IsRelateToEffect(e){(space is null ? "" : " and " + space)} then",
            $"\tlocal {g}=Group.FromCards(c)",
            $"\t{r}={Apply(g, ctx)}",
        };
        body.AddRange(After(g, ctx.Script.Filter(new CardFilter { Zones = Zone.Hand })).Select(l => "\t" + l));
        body.Add("end");
        e.AddStep(body, r, ctx.Link);
    }

    /// <summary>"Destroy all monsters your opponent controls": every matching card, nothing chosen.</summary>
    private void CompileAll(BlockContext ctx, CardFilter filter, FilterRef f)
    {
        var e = ctx.Effect;
        if (Category.Length > 0) e.AddCategory(Category);
        foreach (var c in ExtraCategories(filter)) e.AddCategory(c);
        e.TargetChecks.Add($"Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},{f.Opp},1,{f.Exclude},e,tp)");
        var ig = e.NewLocal("ig");
        e.TargetLines.Add($"local {ig}=Duel.GetMatchingGroup({f.Function},tp,{f.Self},{f.Opp},{f.Exclude},e,tp)");
        if (Category.Length > 0) e.TargetLines.Add($"Duel.SetOperationInfo(0,{Category},{ig},{ig}:GetCount(),0,0)");
        var g = e.NewLocal("g");
        var r = e.NewLocal("r");
        var body = new List<string>
        {
            $"local {r}=0",
            $"local {g}=Duel.GetMatchingGroup({f.ForSelection},tp,{f.Self},{f.Opp},{f.Exclude},e,tp)",
            $"if {g}:GetCount()>0 then",
            $"\t{r}={Apply(g, ctx)}",
        };
        body.AddRange(After(g, f).Select(l => "\t" + l));
        body.Add("end");
        e.AddStep(body, r, ctx.Link);
    }

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards");
        if (filter.ThisCard && Param("cards")?.AllowThisCard == true)
        {
            CompileThisCard(ctx);
            return;
        }
        if (filter.CardCode != 0 && filter.CardCode == ctx.Card.Id)
            ctx.Effect.Warn($"\"{Name}\" picks any card named \"{ctx.Card.Name}\", not necessarily this one. To use this exact card, tick \"This card itself\" in the card picker.");
        if (filter.Zones == Zone.None)
        {
            ctx.Effect.Error($"\"{Name}\": pick at least one place to look for cards.");
            return;
        }
        var f = ctx.Script.Filter(filter, AbleCheck(ctx));
        var n = ctx.Args.Int("count");
        var mode = Mode(ctx);
        var min = mode == "up_to" ? 1 : n;
        var targeting = Param("target") is not null && ctx.Args.Bool("target");
        if (mode == "all")
        {
            if (!AllowsAll) { ctx.Effect.Error($"\"{Name}\" can't take \"all\" cards: pick exactly or up to a number."); return; }
            if (targeting) { ctx.Effect.Error($"\"{Name}\": \"all\" cards aren't targeted. Turn off targeting."); return; }
            CompileAll(ctx, filter, f);
            return;
        }
        var e = ctx.Effect;
        if (Category.Length > 0) e.AddCategory(Category);
        foreach (var c in ExtraCategories(filter)) e.AddCategory(c);
        var space = SpaceCheck(n, filter, ctx);
        if (space is not null) e.TargetChecks.Add(space);

        var g = e.NewLocal("g");
        var r = e.NewLocal("r");
        var body = new List<string>();
        if (targeting)
        {
            if (filter.Zones.HasFlag(Zone.Deck) || filter.Zones.HasFlag(Zone.Hand) || filter.Zones.HasFlag(Zone.ExtraDeck))
            {
                e.Error($"\"{Name}\": cards in the Deck, hand or Extra Deck are hidden and can't be targeted. Turn off targeting or pick the field, GY or banished cards.");
                return;
            }
            // With more than one target the engine never re-checks a single card, so chkc is false.
            e.SetChkc(n == 1 ? f.ChkcCheck : "false");
            e.TargetChecks.Add($"Duel.IsExistingTarget({f.Function},tp,{f.Self},{f.Opp},{min},{f.Exclude},e,tp)");
            var tg = e.NewLocal("tg");
            e.TargetLines.Add($"Duel.Hint(HINT_SELECTMSG,tp,{Hint})");
            e.TargetLines.Add($"local {tg}=Duel.SelectTarget(tp,{f.Function},tp,{f.Self},{f.Opp},{min},{n},{f.Exclude},e,tp)");
            if (Category.Length > 0) e.TargetLines.Add($"Duel.SetOperationInfo(0,{Category},{tg},{tg}:GetCount(),0,0)");
            body.Add($"local {g}=Duel.GetChainInfo(0,CHAININFO_TARGET_CARDS):Filter(Card.IsRelateToEffect,nil,e)");
            body.Add($"local {r}=0");
            body.Add(space is null ? $"if {g}:GetCount()>0 then" : $"if {g}:GetCount()>0 and {space} then");
        }
        else
        {
            e.TargetChecks.Add($"Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},{f.Opp},{min},{f.Exclude},e,tp)");
            if (Category.Length > 0) e.TargetLines.Add($"Duel.SetOperationInfo(0,{Category},nil,{min},{f.InfoPlayer},{f.InfoLocation})");
            body.Add($"local {r}=0");
            if (space is not null) body.Add($"if {space} then");
            var ind = space is null ? "" : "\t";
            body.Add($"{ind}Duel.Hint(HINT_SELECTMSG,tp,{Hint})");
            body.Add($"{ind}local {g}=Duel.SelectMatchingCard(tp,{f.ForSelection},tp,{f.Self},{f.Opp},{min},{n},{f.Exclude},e,tp)");
            body.Add($"{ind}if {g}:GetCount()>0 then");
            if ((filter.Zones & Zone.Field) != Zone.None) body.Add($"{ind}\tDuel.HintSelection({g})");
            body.Add($"{ind}\t{r}={Apply(g, ctx)}");
            body.AddRange(After(g, f).Select(l => $"{ind}\t{l}"));
            body.Add($"{ind}end");
            if (space is not null) body.Add("end");
            e.AddStep(body, r, ctx.Link);
            return;
        }
        body.Add($"\t{r}={Apply(g, ctx)}");
        body.AddRange(After(g, f).Select(l => "\t" + l));
        body.Add("end");
        e.AddStep(body, r, ctx.Link);
    }
}

public sealed class AddToHandAction : CardGroupAction
{
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "add {0} to your hand";
    public override string Id => "do.add_to_hand";
    public override string Name => "Add cards to your hand (search)";
    public override string Sentence => "Add {count} {cards} to your hand {target}";
    protected override string Category => "CATEGORY_TOHAND";
    protected override string Hint => "HINTMSG_ATOHAND";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster };
    protected override string? AbleCheck(BlockContext ctx) => "c:IsAbleToHand()";
    protected override string Apply(string g, BlockContext ctx) => $"Duel.SendtoHand({g},tp,REASON_EFFECT)";
    protected override IEnumerable<string> ExtraCategories(CardFilter f) =>
        f.Zones.HasFlag(Zone.Deck) ? ["CATEGORY_SEARCH"] : [];
    protected override IEnumerable<string> After(string g, FilterRef f) => [$"Duel.ConfirmCards(1-tp,{g})"];
}

public sealed class SpecialSummonAction : CardGroupAction
{
    protected override bool AllowsThisCard => true;
    protected override bool AllowsAll => false;
    protected override string TargetedWording => "Special Summon {0}";
    public override string Id => "do.special_summon";
    public override string Name => "Special Summon monsters";
    public override string Sentence => "Special Summon {count} {cards} {target} {position} {zone}";
    protected override string Category => "CATEGORY_SPECIAL_SUMMON";
    protected override string Hint => "HINTMSG_SPSUMMON";
    protected override int MaxCount => 5;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster };

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        .. base.BuildParams(),
        ParamSpec.Choice("position", "Position",
            new("faceup", "", "face-up"),
            new("attack", "in Attack Position"),
            new("defense", "in Defense Position")),
        ParamSpec.Choice("zone", "Zone", new("any", "", "to any zone"), new("pointed", "to a zone this card points to")),
    ];

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single)
    {
        var text = base.TargetedAction(args, pronoun, single);
        return args.Describe("zone") is { Length: > 0 } z ? $"{text} {z}" : text;
    }

    protected override string? AbleCheck(BlockContext ctx) => "c:IsCanBeSpecialSummoned(e,0,tp,false,false)";

    protected override string Apply(string g, BlockContext ctx)
    {
        var pos = ctx.Args.Choice("position") switch
        {
            "attack" => "POS_FACEUP_ATTACK",
            "defense" => "POS_FACEUP_DEFENSE",
            _ => "POS_FACEUP",
        };
        return PointsTo(ctx)
            ? $"Duel.SpecialSummon({g},0,tp,tp,false,false,{pos},e:GetHandler():GetLinkedZone(tp))"
            : $"Duel.SpecialSummon({g},0,tp,tp,false,false,{pos})";
    }

    protected override string? SpaceCheck(int count, CardFilter f, BlockContext ctx) =>
        PointsTo(ctx)
            ? $"Duel.GetLocationCount(tp,LOCATION_MZONE,tp,LOCATION_REASON_TOFIELD,e:GetHandler():GetLinkedZone(tp))>={count}"
            : f.Zones == Zone.ExtraDeck
                ? $"Duel.GetLocationCountFromEx(tp)>={count}"
                : $"Duel.GetLocationCount(tp,LOCATION_MZONE)>={count}";

    private static bool PointsTo(BlockContext ctx) => ctx.Args.Choice("zone") == "pointed";

    public override void Compile(BlockContext ctx)
    {
        if (PointsTo(ctx) && !ctx.Card.IsLink)
            ctx.Effect.Error("\"To a zone this card points to\" needs this card to be a Link monster.");
        base.Compile(ctx);
    }
}

public sealed class DestroyAction : CardGroupAction
{
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "destroy {0}";
    public override string Id => "do.destroy";
    public override string Name => "Destroy cards";
    public override string Sentence => "Destroy {count} {cards} {target}";
    protected override string Category => "CATEGORY_DESTROY";
    protected override string Hint => "HINTMSG_DESTROY";
    protected override Func<CardFilter> DefaultCards => () => CardFilter.OpponentField();
    protected override string Apply(string g, BlockContext ctx) => $"Duel.Destroy({g},REASON_EFFECT)";
}

public sealed class BanishAction : CardGroupAction
{
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "banish {0}";
    public override string Id => "do.banish";
    public override string Name => "Banish cards";
    public override string Sentence => "Banish {count} {cards} {target}";
    protected override string Category => "CATEGORY_REMOVE";
    protected override string Hint => "HINTMSG_REMOVE";
    protected override Func<CardFilter> DefaultCards => () => CardFilter.OpponentField();
    protected override string? AbleCheck(BlockContext ctx) => "c:IsAbleToRemove()";
    protected override string Apply(string g, BlockContext ctx) => $"Duel.Remove({g},POS_FACEUP,REASON_EFFECT)";
}

public sealed class SendToGraveAction : CardGroupAction
{
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "send {0} to the GY";
    public override string Id => "do.send_to_gy";
    public override string Name => "Send cards to the GY";
    public override string Sentence => "Send {count} {cards} to the GY {target}";
    protected override string Category => "CATEGORY_TOGRAVE";
    protected override string Hint => "HINTMSG_TOGRAVE";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck };
    protected override string? AbleCheck(BlockContext ctx) => "c:IsAbleToGrave()";
    protected override string Apply(string g, BlockContext ctx) => $"Duel.SendtoGrave({g},REASON_EFFECT)";
}

public sealed class ReturnToHandAction : CardGroupAction
{
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "return {0} to the hand";
    public override string Id => "do.return_to_hand";
    public override string Name => "Return cards to the hand (bounce)";
    public override string Sentence => "Return {count} {cards} to the hand {target}";
    protected override string Category => "CATEGORY_TOHAND";
    protected override string Hint => "HINTMSG_RTOHAND";
    protected override Func<CardFilter> DefaultCards => () => CardFilter.OpponentField();
    protected override string? AbleCheck(BlockContext ctx) => "c:IsAbleToHand()";
    protected override string Apply(string g, BlockContext ctx) => $"Duel.SendtoHand({g},nil,REASON_EFFECT)";
}

public sealed class ShuffleIntoDeckAction : CardGroupAction
{
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "shuffle {0} into the Deck";
    public override string Id => "do.shuffle_into_deck";
    public override string Name => "Shuffle cards into the Deck";
    public override string Sentence => "Shuffle {count} {cards} into the Deck {target}";
    protected override string Category => "CATEGORY_TODECK";
    protected override string Hint => "HINTMSG_TODECK";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard };
    protected override string? AbleCheck(BlockContext ctx) => "c:IsAbleToDeck()";
    protected override string Apply(string g, BlockContext ctx) => $"Duel.SendtoDeck({g},nil,SEQ_DECKSHUFFLE,REASON_EFFECT)";
}

public sealed class DrawAction : ActionBlock
{
    public override string Id => "do.draw";
    public override string Group => "Cards";
    public override string Name => "Draw cards";
    public override string Sentence => "{who} draw(s) {count} card(s)";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("you", "You"), new("opponent", "Your opponent")),
        ParamSpec.Int("count", "How many", 1, 1, 10),
        PerParam,
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var cards = n == 1 ? "1 card" : $"{n} cards";
        var text = args.Choice("who") == "you" ? $"draw {cards}" : $"your opponent draws {cards}";
        return args.Describe("per") is { Length: > 0 } per ? $"{text} {per}" : text;
    }

    public override void Compile(BlockContext ctx)
    {
        var p = PlayerOf(ctx.Args.Choice("who"));
        var n = ctx.Args.Int("count");
        var per = Multiplier(ctx);
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory("CATEGORY_DRAW");
        ctx.Effect.TargetChecks.Add($"Duel.IsPlayerCanDraw({p},{n})");
        ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,{p},{n})");
        ctx.Effect.AddStep([$"local {r}=Duel.Draw({p},{n}{per},REASON_EFFECT)"], r, ctx.Link);
    }
}

public sealed class DamageAction : ActionBlock
{
    public override string Id => "do.damage";
    public override string Group => "Life Points";
    public override string Name => "Inflict damage";
    public override string Sentence => "Inflict {amount} damage to {who}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("amount", "Amount", 500, 1, 99999),
        ParamSpec.Choice("who", "Who", new("opponent", "your opponent"), new("you", "you")),
        PerParam,
    ];

    public override string Describe(BlockArgs args)
    {
        var text = $"inflict {args.Int("amount")} damage to {args.Describe("who")}";
        return args.Describe("per") is { Length: > 0 } per ? $"{text} {per}" : text;
    }

    public override void Compile(BlockContext ctx)
    {
        var p = PlayerOf(ctx.Args.Choice("who"));
        var n = ctx.Args.Int("amount");
        var per = Multiplier(ctx);
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory("CATEGORY_DAMAGE");
        ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_DAMAGE,nil,0,{p},{n}{Multiplier(ctx, inTarget: true)})");
        ctx.Effect.AddStep([$"local {r}=Duel.Damage({p},{n}{per},REASON_EFFECT)"], r, ctx.Link);
    }
}

public sealed class GainLifePointsAction : ActionBlock
{
    public override string Id => "do.gain_lp";
    public override string Group => "Life Points";
    public override string Name => "Gain Life Points";
    public override string Sentence => "{who} gain(s) {amount} LP";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("you", "You"), new("opponent", "Your opponent")),
        ParamSpec.Int("amount", "Amount", 1000, 1, 99999),
        PerParam,
    ];

    public override string Describe(BlockArgs args)
    {
        var text = args.Choice("who") == "you" ? $"gain {args.Int("amount")} LP" : $"your opponent gains {args.Int("amount")} LP";
        return args.Describe("per") is { Length: > 0 } per ? $"{text} {per}" : text;
    }

    public override void Compile(BlockContext ctx)
    {
        var p = PlayerOf(ctx.Args.Choice("who"));
        var n = ctx.Args.Int("amount");
        var per = Multiplier(ctx);
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory("CATEGORY_RECOVER");
        ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_RECOVER,nil,0,{p},{n}{Multiplier(ctx, inTarget: true)})");
        ctx.Effect.AddStep([$"local {r}=Duel.Recover({p},{n}{per},REASON_EFFECT)"], r, ctx.Link);
    }
}

public sealed class SpecialSummonSelfAction : ActionBlock
{
    public override string Id => "do.special_summon_self";
    public override string Group => "This card";
    public override string Name => "Special Summon this card";
    public override string Sentence => "Special Summon this card {position}";
    public override string Help => "Summons this card from wherever the effect is used: the WHEN block's \"in the hand\" or \"in the GY\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("position", "Position",
            new("faceup", "", "face-up"),
            new("attack", "in Attack Position"),
            new("defense", "in Defense Position"),
            new("defense_facedown", "in face-down Defense Position")),
    ];

    /// <summary>"Special Summon this card from your hand", taking the place from the WHEN block.</summary>
    public override string Describe(BlockArgs args)
    {
        var zone = args.TriggerZone;
        var what = zone == "banished" ? "Special Summon this banished card" : "Special Summon this card";
        if (zone is not null && Zones.From(zone) is { } from) what += " " + from;
        return $"{what} {args.Describe("position")}".TrimEnd();
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        if (ctx.Effect.Range == "LOCATION_MZONE")
            ctx.Effect.Error("\"Special Summon this card\" needs the effect to be used where this card isn't on the field: set the WHEN block to \"in the hand\", \"in the GY\" or \"while banished\".");
        var pos = ctx.Args.Choice("position") switch
        {
            "attack" => "POS_FACEUP_ATTACK",
            "defense" => "POS_FACEUP_DEFENSE",
            "defense_facedown" => "POS_FACEDOWN_DEFENSE",
            _ => "POS_FACEUP",
        };
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory("CATEGORY_SPECIAL_SUMMON");
        ctx.Effect.TargetChecks.Add("Duel.GetLocationCount(tp,LOCATION_MZONE)>0");
        ctx.Effect.TargetChecks.Add($"e:GetHandler():IsCanBeSpecialSummoned(e,0,tp,false,false,{pos})");
        ctx.Effect.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,e:GetHandler(),1,0,0)");
        ctx.Effect.AddStep(
        [
            $"local {r}=0",
            "if c:IsRelateToEffect(e) and Duel.GetLocationCount(tp,LOCATION_MZONE)>0 then",
            $"\t{r}=Duel.SpecialSummon(c,0,tp,tp,false,false,{pos})",
            "end",
        ], r, ctx.Link);
    }
}

public sealed class ChangeSelfStatsAction : ActionBlock
{
    public override string Id => "do.change_self_stats";
    public override string Group => "This card";
    public override string Name => "This card gains or loses ATK/DEF";
    public override string Sentence => "This card {dir} {amount} {stat} {duration}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("dir", "Gains/loses", new("gain", "gains"), new("lose", "loses")),
        ParamSpec.Int("amount", "Amount", 500, 1, 99999),
        ParamSpec.Choice("stat", "Stat", new("atk", "ATK"), new("def", "DEF"), new("both", "ATK/DEF")),
        ParamSpec.Choice("duration", "Until", new("turn", "until the end of this turn"), new("always", "", "while face-up")),
        PerParam,
    ];

    public override string Describe(BlockArgs args)
    {
        var text = $"this card {args.Describe("dir")} {args.Int("amount")} {args.Describe("stat")}";
        if (args.Describe("per") is { Length: > 0 } per) text += " " + per;
        return args.Describe("duration") is { Length: > 0 } d ? $"{text} {d}" : text;
    }

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var per = Multiplier(ctx);
        var amount = ctx.Args.Int("amount") * (ctx.Args.Choice("dir") == "lose" ? -1 : 1);
        var reset = ctx.Args.Choice("duration") == "turn"
            ? "RESET_EVENT+RESETS_STANDARD+RESET_DISABLE+RESET_PHASE+PHASE_END"
            : "RESET_EVENT+RESETS_STANDARD+RESET_DISABLE";
        var codes = ctx.Args.Choice("stat") switch
        {
            "def" => new[] { "EFFECT_UPDATE_DEFENSE" },
            "both" => ["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"],
            _ => ["EFFECT_UPDATE_ATTACK"],
        };
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory(codes.Contains("EFFECT_UPDATE_DEFENSE") && codes.Length == 1 ? "CATEGORY_DEFCHANGE" : "CATEGORY_ATKCHANGE");
        var body = new List<string> { $"local {r}=0", "if c:IsFaceup() and c:IsRelateToEffect(e) then" };
        foreach (var code in codes)
        {
            var v = ctx.Effect.NewLocal("ce");
            body.Add($"\tlocal {v}=Effect.CreateEffect(c)");
            body.Add($"\t{v}:SetType(EFFECT_TYPE_SINGLE)");
            body.Add($"\t{v}:SetCode({code})");
            body.Add($"\t{v}:SetValue({amount}{per})");
            body.Add($"\t{v}:SetReset({reset})");
            body.Add($"\tc:RegisterEffect({v})");
        }
        body.Add($"\t{r}=1");
        body.Add("end");
        ctx.Effect.AddStep(body, r, ctx.Link);
    }
}
