using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

public abstract class ActionBlock : BlockDefinition
{
    public override BlockKind Kind => BlockKind.Action;

    protected static string PlayerOf(string who) => who == "opponent" ? "1-tp" : "tp";

    /// <summary>"exactly 3", "up to 3" or "all": how many cards a card action takes.</summary>
    public static ParamSpec ModeParam => ParamSpec.Choice("mode", "Exactly / up to / all",
        new("exact", "", "exactly"), new("up_to", "up to"), new("all", "all"));

    /// <summary>
    /// For effects given to monsters that may include this card: whether this card keeps what it gave itself while its
    /// effects are negated (EFFECT_FLAG_CANNOT_DISABLE), as many official cards' "all monsters you control gain ..." do.
    /// </summary>
    public static ParamSpec KeptParam => ParamSpec.Flag("kept", "this card keeps it even if its effects are negated", false)
        .ShowIf(b => !(b.Filters.TryGetValue("cards", out var f) && f.ThisCard));

    /// <summary>"e1:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)" when the block's "kept" is ticked, else nothing.</summary>
    protected static IEnumerable<string> KeptLines(BlockContext ctx, string v, string indent) =>
        ctx.Args.Definition.Param("kept") is not null && ctx.Args.Bool("kept") && !ctx.Args.Cards("cards").ThisCard
            ? [$"{indent}{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)"] : [];

    /// <summary>The same, for blocks whose "mode" says something else ("Level becomes / increases by").</summary>
    public static ParamSpec CountModeParam => ParamSpec.Choice("count_mode", "Exactly / up to / all",
        new("exact", "", "exactly"), new("up_to", "up to"), new("all", "all"));

    /// <summary>"for each of those cards": an amount multiplied by how many cards the cost or the action before used.</summary>
    public static ParamSpec PerParam => ParamSpec.Choice("per", "For each",
        new("once", "", "(once)"),
        new("per_previous", "for each of those cards", "for each card the action before affected"),
        new("per_cost", "for each card used for the cost", "for each card used for the cost"));

    /// <summary>"equal to that monster's original ATK": an amount taken from the cards the DO block before affected.</summary>
    public static ParamSpec EqualParam => ParamSpec.Choice("equal", "Amount",
        new("amount", "", "a number"),
        new("orig_atk", "equal to that monster's original ATK", "equal to the original ATK of the cards the action before affected"),
        new("atk", "equal to that monster's ATK", "equal to the ATK those cards had on the field"),
        new("orig_def", "equal to that monster's original DEF", "equal to the original DEF of the cards the action before affected"),
        new("event", "equal to that amount", "equal to the damage taken or LP paid/gained that started this effect"));

    protected static bool UsesAmount(BlockInstance b) => b.Values.GetValueOrDefault("equal", "amount") == "amount";

    /// <summary>Lines that total the chosen stat of the cards the step before affected into <paramref name="v"/>.</summary>
    protected static List<string>? EqualLines(BlockContext ctx, string v)
    {
        if (ctx.Args.Definition.Param("equal") is null || ctx.Args.Choice("equal") == "amount") return null;
        // "gain LP equal to the damage you took": the amount of the damage, payment or LP gain that triggered the effect.
        if (ctx.Args.Choice("equal") == "event")
        {
            if (ctx.Effect.Code is not ("EVENT_DAMAGE" or "EVENT_BATTLE_DAMAGE" or "EVENT_RECOVER" or "EVENT_PAY_LPCOST"))
                ctx.Effect.Warn("\"Equal to that amount\" needs a WHEN that is damage being taken or LP being paid or gained.");
            return [$"local {v}=ev"];
        }
        if (ctx.Effect.LastResult is null) { ctx.Effect.Error("\"Equal to that monster's ATK\" needs a DO block before it that destroys, banishes or sends cards."); return null; }
        var stat = ctx.Args.Choice("equal") switch { "atk" => "GetPreviousAttackOnField", "orig_def" => "GetTextDefense", _ => "GetTextAttack" };
        var og = ctx.Effect.NewLocal("og");
        var tc = ctx.Effect.NewLocal("tc");
        return
        [
            $"local {v}=0",
            $"local {og}=Duel.GetOperatedGroup()",
            $"local {tc}={og}:GetFirst()",
            $"while {tc} do if {tc}:{stat}()>0 then {v}={v}+{tc}:{stat}() end {tc}={og}:GetNext() end",
        ];
    }

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
public abstract partial class CardGroupAction : ActionBlock
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
        var single = !args.IsPlural && !(args.CountMode == "up_to" && args.Int("count") > 1);
        var target = $"target {args.Describe("count")} {args.Describe("cards")}{args.ExceptText()}{Selection.EachText(args)}";
        return (target, TargetedAction(args, single ? "it" : "them", single));
    }

    /// <summary>What follows the semicolon, e.g. "destroy it". Override when the wording needs more blanks.</summary>
    protected virtual string TargetedAction(BlockArgs args, string pronoun, bool single)
    {
        var action = string.Format(System.Globalization.CultureInfo.InvariantCulture, TargetedWording, pronoun);
        if (Param("position") is not null && args.Describe("position") is { Length: > 0 } pos) action += " " + pos;
        return action;
    }

    /// <summary>The block's blanks: its picker also offers "that card", and "together with this card / the target" follows.</summary>
    public override IReadOnlyList<ParamSpec> Params => _params ??=
        [.. BuildParams().Select(p => p is { Key: "cards", Type: ParamType.Cards } ? p.ThatCards() : p), CardRefs.PlusParam];
    private IReadOnlyList<ParamSpec>? _params;

    /// <summary>Whether the card picker offers "This card itself" (move, destroy or Summon this exact card).</summary>
    protected virtual bool AllowsThisCard => false;

    /// <summary>"this card", "2 Dragon monsters", "up to 2 ...", "all ...": the cards the action is about, for its sentence.</summary>
    protected static string Some(BlockArgs args) =>
        args.IsThisCard ? "this card" : $"{args.Describe("count")} {args.Describe("cards")}".Trim();

    /// <summary>The same as the subject of a sentence: "they" for "them" ("they gain 500 ATK").</summary>
    protected static string Subject(BlockArgs args) => Some(args) is var some && some.StartsWith("them", StringComparison.Ordinal) ? "they" + some[4..] : Some(args);

    /// <summary>"this card's ATK", "its ATK", "the ATK of 1 monster you control": a stat of the block's cards.</summary>
    protected static string StatOf(BlockArgs args, string noun) => Some(args) switch
    {
        "this card" => $"this card's {noun}",
        "it" => $"its {noun}",
        "them" => $"their {noun}",
        var some => $"the {noun} of {some}",
    };

    /// <summary>One card: this card, or a count of 1 (not "all").</summary>
    protected static bool One(BlockArgs args) => args.IsThisCard || args.Int("count") == 1 && !args.IsPlural;

    /// <summary>Whether "all" makes sense (it doesn't for Special Summons, which need a free zone for each card).</summary>
    protected virtual bool AllowsAll => true;

    protected virtual IReadOnlyList<ParamSpec> BuildParams() =>
    [
        // "any number of" says how many itself (Selections.cs).
        ParamSpec.Int("count", "How many", 1, 1, MaxCount).ShowIf(b => b.Values.GetValueOrDefault("amount") is not ("any" or "number_of" or "materials")),
        ModeParam.ShowIf(b => b.Values.GetValueOrDefault("amount") is not ("any" or "number_of" or "materials")),
        new ParamSpec { Key = "cards", Label = "Which cards", Type = ParamType.Cards, DefaultFilter = DefaultCards, AllowThisCard = AllowsThisCard },
        ParamSpec.Flag("target", "(targeting)", false),
        .. SelectionParams,
    ];

    private static string Mode(BlockContext ctx) => ctx.Args.CountMode;

    /// <summary>The action on this card itself: wherever the effect is used from, only if it is still there.</summary>
    private void CompileThisCard(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (Param("target") is not null && ctx.Args.Bool("target"))
            e.Error($"\"{Name}\": this card itself isn't targeted. Turn off targeting.");
        if (Category.Length > 0) e.AddCategory(Category);
        var able = AbleCheck(ctx)?.Replace("c:", "e:GetHandler():", StringComparison.Ordinal);
        var space = Space(1, new CardFilter { Zones = Zone.Hand }, ctx);
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
        PlusChecks(ctx);
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
        };
        body.AddRange(ApplyLines(g, r, ctx).Select(l => "\t" + l));
        body.AddRange(After(g, f).Select(l => "\t" + l));
        body.Add("end");
        e.AddStep(body, r, ctx.Link);
    }

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards");
        if (filter.ThisCard && Param("cards")?.AllowThisCard == true)
        {
            if (CardRefs.Plus(ctx.Args) == "this") ctx.Effect.Error($"\"{Name}\": the cards are this card already, so \"and this card\" adds nothing.");
            CompileThisCard(ctx);
            return;
        }
        if (filter.That != CardRef.None)
        {
            CompileThat(ctx, filter);
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
        // "with different names", "any number of", "1 random card", "your opponent chooses" (Selections.cs).
        if (Selection.Applies(ctx.Args))
        {
            CompileSelection(ctx, filter, f, targeting);
            return;
        }
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
        var space = Space(n, filter, ctx);
        if (space is not null) e.TargetChecks.Add(space);
        PlusChecks(ctx);

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
            // A single target can be checked again (an effect that changes the target to another card), as can "up to 3",
            // which may be 1 card; an exact number of several targets never is, so chkc is false.
            e.SetChkc(min == 1 ? f.ChkcCheck : "false");
            e.TargetChecks.Add($"Duel.IsExistingTarget({f.Function},tp,{f.Self},{f.Opp},{min},{f.Exclude},e,tp)");
            var tg = e.NewLocal("tg");
            e.TargetLines.Add($"Duel.Hint(HINT_SELECTMSG,tp,{Hint})");
            e.TargetLines.Add($"local {tg}=Duel.SelectTarget(tp,{f.Function},tp,{f.Self},{f.Opp},{min},{PaidMax(e, n.ToString(System.Globalization.CultureInfo.InvariantCulture))},{f.Exclude},e,tp)");
            PayPerTarget(e, tg);
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
            body.AddRange(ApplyLines(g, r, ctx).Select(l => $"{ind}\t{l}"));
            body.AddRange(After(g, f).Select(l => $"{ind}\t{l}"));
            body.Add($"{ind}end");
            if (space is not null) body.Add("end");
            e.AddStep(body, r, ctx.Link);
            return;
        }
        body.AddRange(ApplyLines(g, r, ctx).Select(l => "\t" + l));
        body.AddRange(After(g, f).Select(l => "\t" + l));
        body.Add("end");
        e.AddStep(body, r, ctx.Link);
    }

    /// <summary>
    /// "Target ... and pay 800 LP for each target": no more targets than the LP pays for (as Ultimate Sky caps them), or
    /// <paramref name="max"/> when the effect has no such cost.
    /// </summary>
    protected static string PaidMax(EffectBuilder e, string max) =>
        e.LpPerTarget is { } each ? $"math.min({max},math.floor(Duel.GetLP(tp)/{each}))" : max;

    /// <summary>Pays the LP for each target as the targets are chosen, when the effect is activated (not copied); the cost is then used.</summary>
    protected static void PayPerTarget(EffectBuilder e, string targets)
    {
        if (e.LpPerTarget is not { } each) return;
        e.TargetLines.Add($"if e:IsCostChecked() then Duel.PayLPCost(tp,{targets}:GetCount()*{each}) end");
        e.LpPerTarget = null;
    }

    /// <summary>
    /// A Special Summon of the block's cards together with this card: both or neither, with a zone for each, and never
    /// while only 1 monster can be Special Summoned at a time (Blue-Eyes Spirit Dragon), as official scripts check it.
    /// </summary>
    protected virtual bool Together => false;

    /// <summary>The space check for <paramref name="count"/> cards, and this card when it is Summoned with them.</summary>
    protected string? Space(int count, CardFilter f, BlockContext ctx) =>
        SpaceCheck(count + (Together && CardRefs.Plus(ctx.Args) == "this" ? 1 : 0), f, ctx);

    /// <summary>The activation checks of "and this card" / "and the target": this card can be used, or the effect targets.</summary>
    protected void PlusChecks(BlockContext ctx)
    {
        switch (CardRefs.Plus(ctx.Args))
        {
            case "this":
                if (AbleCheck(ctx) is { } able) ctx.Effect.TargetChecks.Add(able.Replace("c:", "e:GetHandler():", StringComparison.Ordinal));
                if (Together) ctx.Effect.TargetChecks.Add("not Duel.IsPlayerAffectedByEffect(tp,59822133)");
                break;
            case "target":
                if (!Memory.Targets(ctx.Effect)) ctx.Effect.Error($"\"{Name}\": \"and this effect's target\" needs a DO block before it that targets (or \"Target cards\").");
                break;
        }
    }

    /// <summary>
    /// The lines that apply the action to group <paramref name="g"/> and keep how many it affected in <paramref name="r"/>:
    /// with this card ("both it and this card") or this effect's target(s) ("destroy it and all ...") added to it.
    /// </summary>
    protected List<string> ApplyLines(string g, string r, BlockContext ctx)
    {
        var able = AbleCheck(ctx) is { } a ? " and " + a : "";
        switch (CardRefs.Plus(ctx.Args))
        {
            case "this" when Together:
                return
                [
                    $"if c:IsRelateToEffect(e){able} and not Duel.IsPlayerAffectedByEffect(tp,59822133) then",
                    $"\t{g}:AddCard(c)",
                    $"\t{r}={Apply(g, ctx)}",
                    "end",
                ];
            case "this":
                return [$"if c:IsRelateToEffect(e){able} then {g}:AddCard(c) end", $"{r}={Apply(g, ctx)}"];
            case "target":
                var targets = "Duel.GetChainInfo(0,CHAININFO_TARGET_CARDS):Filter(Card.IsRelateToEffect,nil,e)";
                if (AbleCheck(ctx) is { } check) targets += $":Filter({ctx.Script.Filter(new CardFilter { Owner = CardOwner.Either, Zones = Zone.None }, check).Function},nil,e,tp)";
                return [$"{g}:Merge({targets})", $"{r}={Apply(g, ctx)}"];
            default:
                return [$"{r}={Apply(g, ctx)}"];
        }
    }

    /// <summary>
    /// "Destroy it", "Special Summon that monster to your field", "it loses ATK equal to the sent monster's ATK": the action
    /// on cards the effect already has (<see cref="CardRefs"/>). What the picker says about them is a check they must pass.
    /// They aren't known as the effect is activated (except a battling monster), so nothing is checked then.
    /// </summary>
    private void CompileThat(BlockContext ctx, CardFilter filter)
    {
        var e = ctx.Effect;
        if (Param("target") is not null && ctx.Args.Bool("target"))
        {
            e.Error($"\"{Name}\": the cards are \"that card\" already, so they aren't targeted by this block. Turn off targeting.");
            return;
        }
        if (filter.That == CardRef.Target && CardRefs.Plus(ctx.Args) == "target")
        {
            e.Error($"\"{Name}\": the cards are this effect's target already, so \"and this effect's target\" adds nothing.");
            return;
        }
        // Where the cards are doesn't matter: only what they are.
        var cards = filter.Clone();
        cards.Zones = Zone.None;
        cards.Owner = CardOwner.Either;
        if (Category.Length > 0) e.AddCategory(Category);
        foreach (var c in ExtraCategories(cards)) e.AddCategory(c);
        var f = ctx.Script.Filter(cards, AbleCheck(ctx));
        if (CardRefs.GroupLua(ctx, filter.That) is not { } group) return;
        var space = Space(1, cards, ctx);
        if (space is not null) e.TargetChecks.Add(space);
        if (CardRefs.ActivationCheck(filter.That, f.Function) is { } known) e.TargetChecks.Add(known);
        PlusChecks(ctx);
        if (Category.Length > 0)
            e.TargetLines.Add(filter.That is CardRef.BattleTarget or CardRef.Battling or CardRef.Equipped or CardRef.Attacker
                ? $"Duel.SetOperationInfo(0,{Category},{CardRefs.Card(filter.That)},1,0,0)"
                : $"Duel.SetOperationInfo(0,{Category},nil,1,0,0)");
        var g = e.NewLocal("g");
        var r = e.NewLocal("r");
        var body = new List<string>
        {
            $"local {r}=0",
            $"local {g}={group}:Filter({f.Function},nil,e,tp)",
            space is null ? $"if {g}:GetCount()>0 then" : $"if {g}:GetCount()>0 and {space} then",
        };
        body.AddRange(ApplyLines(g, r, ctx).Select(l => "\t" + l));
        body.AddRange(After(g, f).Select(l => "\t" + l));
        body.Add("end");
        e.AddStep(body, r, ctx.Link);
    }
}

public sealed class AddToHandAction : CardGroupAction
{
    protected override bool OffersSelections => true;
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "add {0} to your hand";
    public override string Id => "do.add_to_hand";
    public override string Name => "Add cards to your hand (search)";
    public override string Sentence => "Add {amount} {count_cards} {count} {cards} {pick} {pick2} {pick3} {sum} {inc_cards} to your hand {target} {chooser}";
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
    protected override bool OffersSelections => true;
    protected override bool AllowsThisCard => true;
    protected override bool AllowsAll => false;
    protected override string TargetedWording => "Special Summon {0}";
    public override string Id => "do.special_summon";
    public override string Name => "Special Summon monsters";
    public override string Sentence => "Special Summon {count} {cards} {pick} {pick2} {pick3} {sum} {inc_cards} {target} {position} {zone} {as} {chooser}";

    public override string Describe(BlockArgs args)
    {
        var text = base.Describe(args);
        // Buster Blader: "Special Summon that monster to your field": the opponent's monster it destroyed by battle.
        if (args.Cards("cards").That == CardRef.BattleTarget) text = text.Replace("that monster", "that monster to your field", StringComparison.Ordinal);
        return text + (args.Bool("ignore") && !Selection.OpponentPicks(args) ? ", ignoring its Summoning conditions" : "") + Negated(args);
    }

    /// <summary>", but negate its effects", ", but negate their effects", ", but it cannot be used as Link Material".</summary>
    private static string Negated(BlockArgs args)
    {
        var plural = args.IsPlural || CardRefs.Plus(args) == "this";
        var text = !args.Bool("negate") ? "" : plural ? ", but negate their effects" : ", but negate its effects";
        // Ars Magna Academic Arcane Apparition Acropolis: "Special Summon that monster ..., but it cannot be used as Link Material".
        if (args.Bool("no_link")) text += text.Length > 0 ? (plural ? ", also they cannot be used as Link Material" : ", also it cannot be used as Link Material")
            : plural ? ", but they cannot be used as Link Material" : ", but it cannot be used as Link Material";
        return text;
    }

    protected override bool Together => true;

    /// <summary>"(This is treated as a Fusion Summon.)"</summary>
    public override string Afterword(BlockArgs args) => TreatedAsSummon.Afterword(args.Choice("as"));
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
            new("defense", "in Defense Position"),
            new("facedown", "in face-down Defense Position")),
        ParamSpec.Choice("zone", "Zone", new("any", "", "to any zone"), new("pointed", "to a zone this card points to")),
        ParamSpec.Flag("ignore", "ignoring its Summoning conditions", false),
        TreatedAsSummon.Param,
        // Saved before this was offered: the Summoned monsters keep their effects.
        ParamSpec.Flag("negate", "but negate its effects", false),
        ParamSpec.Flag("no_link", "but it cannot be used as Link Material", false),
    ];

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single)
    {
        // "Special Summon both it and this card".
        if (CardRefs.Plus(args) == "this") pronoun = $"both {pronoun} and this card";
        var text = base.TargetedAction(args, pronoun, single);
        if (args.Describe("zone") is { Length: > 0 } z) text = $"{text} {z}";
        return (args.Bool("ignore") ? $"{text}, ignoring its Summoning conditions" : text) + Negated(args);
    }

    /// <summary>
    /// "Special Summon it, but negate its effects": each monster is Special Summoned as a step and its effects are negated
    /// (EFFECT_DISABLE and EFFECT_DISABLE_EFFECT), then the Summon is completed, as official scripts do. The call on
    /// <paramref name="g"/>, returning how many were Special Summoned.
    /// </summary>
    public static string NegatedSummon(ScriptBuilder script, string g, string value, string ignore, string limit, string pos, string? zone = null, bool negate = true, bool noLink = false) =>
        script.AddFunction(negate ? "spnegate" : "spnolink", "g,e,tp,v,nocheck,nolimit,pos" + (zone is null ? "" : ",zone"),
        [
            "local c=e:GetHandler()",
            "local n=0",
            "for tc in aux.Next(g) do",
            $"\tif Duel.SpecialSummonStep(tc,v,tp,tp,nocheck,nolimit,pos{(zone is null ? "" : ",zone")}) then",
            .. negate ? new[]
            {
                "\t\tlocal e1=Effect.CreateEffect(c)",
                "\t\te1:SetType(EFFECT_TYPE_SINGLE)",
                "\t\te1:SetCode(EFFECT_DISABLE)",
                "\t\te1:SetReset(RESET_EVENT+RESETS_STANDARD)",
                "\t\ttc:RegisterEffect(e1)",
                "\t\tlocal e2=Effect.CreateEffect(c)",
                "\t\te2:SetType(EFFECT_TYPE_SINGLE)",
                "\t\te2:SetCode(EFFECT_DISABLE_EFFECT)",
                "\t\te2:SetValue(RESET_TURN_SET)",
                "\t\te2:SetReset(RESET_EVENT+RESETS_STANDARD)",
                "\t\ttc:RegisterEffect(e2)",
            } : [],
            // "But it cannot be used as Link Material", as Ars Magna Academic Arcane Apparition Acropolis's script does it.
            .. noLink ? new[]
            {
                "\t\tlocal e3=Effect.CreateEffect(c)",
                "\t\te3:SetDescription(3312)",
                "\t\te3:SetType(EFFECT_TYPE_SINGLE)",
                "\t\te3:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_CLIENT_HINT)",
                "\t\te3:SetCode(EFFECT_CANNOT_BE_LINK_MATERIAL)",
                "\t\te3:SetValue(1)",
                "\t\te3:SetReset(RESET_EVENT+RESETS_STANDARD)",
                "\t\ttc:RegisterEffect(e3)",
            } : [],
            "\t\tn=n+1",
            "\tend",
            "end",
            "Duel.SpecialSummonComplete()",
            "return n",
        ]) + $"({g},e,tp,{value},{ignore},{limit},{pos}{(zone is null ? "" : "," + zone)})";

    private static string Ignore(BlockContext ctx) => ctx.Args.Bool("ignore") ? "true" : "false";

    private static bool FaceDown(BlockContext ctx) => ctx.Args.Choice("position") == "facedown";

    /// <summary>"ritual", "fusion"...: the Summon is treated as that Summon; "none" for a plain Special Summon.</summary>
    private static string As(BlockContext ctx) => ctx.Args.Choice("as");

    protected override string? AbleCheck(BlockContext ctx)
    {
        if (As(ctx) != "none") return TreatedAsSummon.AbleCheck(ctx, As(ctx), Ignore(ctx), ctx.Args.Cards("cards").Zones);
        return FaceDown(ctx) ? $"c:IsCanBeSpecialSummoned(e,0,tp,{Ignore(ctx)},false,POS_FACEDOWN_DEFENSE)" : $"c:IsCanBeSpecialSummoned(e,0,tp,{Ignore(ctx)},false)";
    }

    protected override string Apply(string g, BlockContext ctx)
    {
        if (As(ctx) != "none")
        {
            var position = ctx.Args.Choice("position") switch { "attack" => "POS_FACEUP_ATTACK", "defense" => "POS_FACEUP_DEFENSE", _ => "POS_FACEUP" };
            return TreatedAsSummon.Apply(ctx, g, As(ctx), Ignore(ctx), position, PointsTo(ctx) ? "e:GetHandler():GetLinkedZone(tp)" : null);
        }
        // Face-down: the opponent is shown the Summoned cards, as official cards do.
        if (FaceDown(ctx))
            return ctx.Script.AddFunction("spfacedown", "g,e,tp",
            [
                $"local n=Duel.SpecialSummon(g,{(ctx.Args.Cards("cards").ThisCard ? "SUMMON_VALUE_SELF" : "0")},tp,tp,{Ignore(ctx)},false,POS_FACEDOWN_DEFENSE{(PointsTo(ctx) ? ",e:GetHandler():GetLinkedZone(tp)" : "")})",
                "if n>0 then Duel.ConfirmCards(1-tp,g) end",
                "return n",
            ]) + $"({g},e,tp)";
        var pos = ctx.Args.Choice("position") switch
        {
            "attack" => "POS_FACEUP_ATTACK",
            "defense" => "POS_FACEUP_DEFENSE",
            "facedown" => "POS_FACEDOWN_DEFENSE",
            _ => "POS_FACEUP",
        };
        // "This card itself" is Special Summoned by its own effect: SUMMON_VALUE_SELF, as official cards do.
        var value = ctx.Args.Cards("cards").ThisCard ? "SUMMON_VALUE_SELF" : "0";
        if (ctx.Args.Bool("negate") || ctx.Args.Bool("no_link"))
            return NegatedSummon(ctx.Script, g, value, Ignore(ctx), "false", pos, PointsTo(ctx) ? "e:GetHandler():GetLinkedZone(tp)" : null, ctx.Args.Bool("negate"), ctx.Args.Bool("no_link"));
        return PointsTo(ctx)
            ? $"Duel.SpecialSummon({g},{value},tp,tp,{Ignore(ctx)},false,{pos},e:GetHandler():GetLinkedZone(tp))"
            : $"Duel.SpecialSummon({g},{value},tp,tp,{Ignore(ctx)},false,{pos})";
    }

    protected override string? SpaceCheck(int count, CardFilter f, BlockContext ctx)
    {
        if (PointsTo(ctx)) return $"Duel.GetLocationCount(tp,LOCATION_MZONE,tp,LOCATION_REASON_TOFIELD,e:GetHandler():GetLinkedZone(tp))>={count}";
        if (As(ctx) != "none") return TreatedAsSummon.SpaceCheck(ctx, As(ctx), f.Zones);
        // "Tribute this card; Special Summon ...": the zone this card leaves for the cost counts, as official scripts check.
        var leaving = TreatedAsSummon.CostFreesThisCard(ctx) ? "e:GetHandler()" : null;
        return f.Zones == Zone.ExtraDeck
            ? leaving is null ? $"Duel.GetLocationCountFromEx(tp)>={count}" : $"Duel.GetLocationCountFromEx(tp,tp,{leaving})>={count}"
            : leaving is null ? $"Duel.GetLocationCount(tp,LOCATION_MZONE)>={count}" : $"Duel.GetMZoneCount(tp,{leaving})>={count}";
    }

    private static bool PointsTo(BlockContext ctx) => ctx.Args.Choice("zone") == "pointed";

    public override void Compile(BlockContext ctx)
    {
        if (PointsTo(ctx) && !ctx.Card.IsLink)
            ctx.Effect.Error("\"To a zone this card points to\" needs this card to be a Link monster.");
        if (As(ctx) != "none" && !TreatedAsSummon.Check(ctx, Name, ctx.Args.Cards("cards").ThisCard ? 1 : ctx.Args.Int("count"), FaceDown(ctx))) return;
        if ((ctx.Args.Bool("negate") || ctx.Args.Bool("no_link")) && (As(ctx) != "none" || FaceDown(ctx)))
        {
            ctx.Effect.Error($"\"{Name}\": \"but negate its effects\" and \"cannot be used as Link Material\" are for monsters Special Summoned face-up, not {(FaceDown(ctx) ? "face-down" : "treated as another Summon")}.");
            return;
        }
        base.Compile(ctx);
    }
}

public sealed class DestroyAction : CardGroupAction
{
    protected override bool OffersSelections => true;
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "destroy {0}";
    public override string Id => "do.destroy";
    public override string Name => "Destroy cards";
    public override string Sentence => "Destroy {amount} {count_cards} {count} {cards} {pick} {pick2} {pick3} {sum} {inc_cards} {target} {chooser}";
    protected override string Category => "CATEGORY_DESTROY";
    protected override string Hint => "HINTMSG_DESTROY";
    protected override Func<CardFilter> DefaultCards => () => CardFilter.OpponentField();
    protected override string Apply(string g, BlockContext ctx) => $"Duel.Destroy({g},REASON_EFFECT)";
}

public sealed class BanishAction : CardGroupAction
{
    protected override bool OffersSelections => true;
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "banish {0}";
    public override string Id => "do.banish";
    public override string Name => "Banish cards";
    public override string Sentence => "Banish {amount} {count_cards} {count} {cards} {pick} {pick2} {pick3} {sum} {inc_cards} {target} {facedown} {until} {chooser}";
    protected override string Category => "CATEGORY_REMOVE";
    protected override string Hint => "HINTMSG_REMOVE";
    protected override Func<CardFilter> DefaultCards => () => CardFilter.OpponentField();
    protected override string? AbleCheck(BlockContext ctx) => ctx.Args.Bool("facedown") ? "c:IsAbleToRemove(tp,POS_FACEDOWN)" : "c:IsAbleToRemove()";

    /// <summary>S:P Little Knight's "banish them until the End Phase": cards banished from the field come back then.</summary>
    protected override IReadOnlyList<ParamSpec> BuildParams() =>
        [.. base.BuildParams(), ParamSpec.Choice("until", "For how long", new("always", "", "for good"), new("end_phase", "until the End Phase"),
             // Memory.cs: banished for now, back during a Standby Phase, as official scripts write it.
             new("standby_next", "until the next Standby Phase"), new("standby_your", "until your next Standby Phase"),
             new("standby_opp", "until your opponent's next Standby Phase"),
             // Interdimensional Matter Forwarder: "banish it, then return it to the field" (SelfFlicker.cs).
             new("return", ", then return it to the field")),
         ParamSpec.Flag("facedown", "face-down", false)];

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) =>
        base.TargetedAction(args, pronoun, single) + (args.Bool("facedown") ? " face-down" : "") + (args.Choice("until") != "always" ? " " + args.Describe("until") : "");

    protected override string Apply(string g, BlockContext ctx)
    {
        if (ctx.Args.Choice("until") == "always") return $"Duel.Remove({g},{(ctx.Args.Bool("facedown") ? "POS_FACEDOWN" : "POS_FACEUP")},REASON_EFFECT)";
        var back = ctx.Args.Choice("until") switch
        {
            "standby_next" => "during the next Standby Phase", "standby_your" => "during your next Standby Phase",
            "standby_opp" => "during your opponent's next Standby Phase", "return" => "to the field", _ => "in the End Phase",
        };
        if (ctx.Args.Bool("facedown")) ctx.Effect.Error($"\"Banish cards\": cards banished face-down don't come back {back}. Untick \"face-down\" or banish them for good.");
        if (!ctx.Args.Cards("cards").ThisCard && (ctx.Args.Cards("cards").Zones & ~Zone.Field) != Zone.None)
            ctx.Effect.Warn($"Only cards banished from the field come back {back}.");
        if (ctx.Args.Choice("until") == "return") return BanishAndReturn.Call(ctx, g);
        if (ctx.Args.Choice("until") != "end_phase") return LaterAction.BanishUntil(ctx.Script, g, ctx.Args.Choice("until"));
        var retFilter = ctx.Script.AddFunction("tmpretfilter", "c,fid", ["return c:GetFlagEffectLabel(id)==fid"]);
        var retCon = ctx.Script.AddFunction("tmpretcon", "e,tp,eg,ep,ev,re,r,rp",
        [
            $"if not e:GetLabelObject():IsExists({retFilter},1,nil,e:GetLabel()) then",
            "	e:GetLabelObject():DeleteGroup()",
            "	e:Reset()",
            "	return false",
            "end",
            "return true",
        ]);
        var retOp = ctx.Script.AddFunction("tmpretop", "e,tp,eg,ep,ev,re,r,rp",
        [
            $"local g=e:GetLabelObject():Filter({retFilter},nil,e:GetLabel())",
            "for p=0,1 do",
            "	local tg=g:Filter(Card.IsPreviousControler,nil,p)",
            "	local ft=Duel.GetLocationCount(p,LOCATION_MZONE)",
            "	if tg:GetCount()>1 and ft==1 then",
            "		Duel.Hint(HINT_SELECTMSG,p,HINTMSG_TOFIELD)",
            "		local sg=tg:Select(p,1,1,nil)",
            "		Duel.ReturnToField(sg:GetFirst())",
            "		tg:Sub(sg)",
            "	end",
            "	local tc=tg:GetFirst()",
            "	while tc do Duel.ReturnToField(tc) tc=tg:GetNext() end",
            "end",
            "e:GetLabelObject():DeleteGroup()",
        ]);
        var banish = ctx.Script.AddFunction("tmpbanish", "g,e,tp",
        [
            // Banished in the position it is in (0), as S:P Little Knight and the official temporary banishes do: a face-down card stays face-down.
            "local n=Duel.Remove(g,0,REASON_EFFECT+REASON_TEMPORARY)",
            "local og=Duel.GetOperatedGroup():Filter(Card.IsLocation,nil,LOCATION_REMOVED)",
            "if og:GetCount()==0 then return n end",
            "local fid=e:GetHandler():GetFieldID()",
            "local tc=og:GetFirst()",
            "while tc do tc:RegisterFlagEffect(id,RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,0,1,fid) tc=og:GetNext() end",
            "og:KeepAlive()",
            "local e1=Effect.CreateEffect(e:GetHandler())",
            "e1:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "e1:SetCode(EVENT_PHASE+PHASE_END)",
            "e1:SetReset(RESET_PHASE+PHASE_END)",
            "e1:SetLabel(fid)",
            "e1:SetLabelObject(og)",
            "e1:SetCountLimit(1)",
            $"e1:SetCondition({retCon})",
            $"e1:SetOperation({retOp})",
            "Duel.RegisterEffect(e1,tp)",
            "return n",
        ]);
        return $"{banish}({g},e,tp)";
    }
}

public sealed class SendToGraveAction : CardGroupAction
{
    protected override bool OffersSelections => true;
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "send {0} to the GY";
    public override string Id => "do.send_to_gy";
    public override string Name => "Send cards to the GY";
    public override string Sentence => "Send {amount} {count_cards} {count} {cards} {pick} {pick2} {pick3} {sum} {inc_cards} to the GY {target} {chooser}";
    protected override string Category => "CATEGORY_TOGRAVE";
    protected override string Hint => "HINTMSG_TOGRAVE";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck };
    protected override string? AbleCheck(BlockContext ctx) => "c:IsAbleToGrave()";
    protected override string Apply(string g, BlockContext ctx) => $"Duel.SendtoGrave({g},REASON_EFFECT)";
}

public sealed class ReturnToHandAction : CardGroupAction
{
    protected override bool OffersSelections => true;
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "return {0} to the hand";
    public override string Id => "do.return_to_hand";
    public override string Name => "Return cards to the hand (bounce)";
    public override string Sentence => "Return {amount} {count_cards} {count} {cards} {pick} {pick2} {pick3} {sum} {inc_cards} to the hand {target} {chooser}";
    protected override string Category => "CATEGORY_TOHAND";
    protected override string Hint => "HINTMSG_RTOHAND";
    protected override Func<CardFilter> DefaultCards => () => CardFilter.OpponentField();
    protected override string? AbleCheck(BlockContext ctx) => "c:IsAbleToHand()";
    protected override string Apply(string g, BlockContext ctx) => $"Duel.SendtoHand({g},nil,REASON_EFFECT)";
}

public sealed class ShuffleIntoDeckAction : CardGroupAction
{
    protected override bool OffersSelections => true;
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "shuffle {0} into the Deck";
    public override string Id => "do.shuffle_into_deck";
    public override string Name => "Shuffle cards into the Deck";
    public override string Sentence => "Shuffle {amount} {count_cards} {count} {cards} {pick} {pick2} {pick3} {sum} {inc_cards} into the Deck {target} {chooser}";
    protected override string Category => "CATEGORY_TODECK";
    protected override string Hint => "HINTMSG_TODECK";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard };
    protected override string? AbleCheck(BlockContext ctx) => "c:IsAbleToDeck()";
    protected override string Apply(string g, BlockContext ctx) => $"Duel.SendtoDeck({g},nil,SEQ_DECKSHUFFLE,REASON_EFFECT)";

    /// <summary>
    /// Fusion, Synchro, Xyz and Link Monsters go back to the Extra Deck, and cards say so: "return all Fusion, Synchro, Xyz,
    /// and Link Monsters your opponent controls to the Extra Deck", "return this card to the Extra Deck".
    /// </summary>
    public static bool ToExtraDeck(BlockArgs args)
    {
        var f = args.Cards("cards");
        if (f.ThisCard) return args.Card?.IsExtraDeck == true;
        if (f.MonsterKind is MonsterKind.Fusion or MonsterKind.Synchro or MonsterKind.Xyz or MonsterKind.Link) return true;
        const long extra = 0x40 | 0x2000 | 0x800000 | 0x4000000;
        return f.MonsterKind == MonsterKind.Any && !f.AnyRequirement && f.Requirements.Any(r =>
            r.Property == "monster_type" && r.Op == RequirementOp.Is && r.Values.Count > 0 && r.Values.All(v => v != 0 && (v & ~extra) == 0));
    }

    private static string ExtraWording(string text) => text
        .Replace("Shuffle ", "Return ", StringComparison.Ordinal).Replace("shuffle ", "return ", StringComparison.Ordinal)
        .Replace(" into the Deck", " to the Extra Deck", StringComparison.Ordinal);

    public override string Describe(BlockArgs args) => ToExtraDeck(args) ? ExtraWording(base.Describe(args)) : base.Describe(args);

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) =>
        ToExtraDeck(args) ? ExtraWording(base.TargetedAction(args, pronoun, single)) : base.TargetedAction(args, pronoun, single);
}

public sealed class DrawAction : ActionBlock
{
    public override string Id => "do.draw";
    public override string Group => "Cards";
    public override string Name => "Draw cards";
    public override string Sentence => "{who} draw(s) {count} card(s)";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("you", "You"), new("opponent", "Your opponent"), new("each", "Each player"),
            // The Secret Crane Princess Who Returns A Favor: "then your opponent can draw 1 card, and if they do, ...".
            new("opponent_may", "Your opponent can", "Your opponent can (if they choose to)"),
            // Mimighoul Fork: "Send it to the GY, then its owner draws 2 cards": the owner of the card the step before affected.
            new("owner", "Its owner", "Its owner (of the card the step before affected)")),
        ParamSpec.Int("count", "How many", 1, 1, 10),
        PerParam,
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var cards = n == 1 ? "1 card" : $"{n} cards";
        var text = args.Choice("who") switch
        {
            "you" => $"draw {cards}",
            "each" => $"each player draws {cards}",
            "opponent_may" => $"your opponent can draw {cards}",
            "owner" => $"its owner draws {cards}",
            _ => $"your opponent draws {cards}",
        };
        return args.Describe("per") is { Length: > 0 } per ? $"{text} {per}" : text;
    }

    public override void Compile(BlockContext ctx)
    {
        var who = ctx.Args.Choice("who");
        var p = PlayerOf(who is "opponent_may" ? "opponent" : who);
        var n = ctx.Args.Int("count");
        var per = Multiplier(ctx);
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory("CATEGORY_DRAW");
        if (ctx.Args.Choice("who") == "owner")
        {
            // Known once the step before has moved the card, as official scripts read it (GetOwner).
            if (ctx.Effect.LastResult is null) { ctx.Effect.Error("\"Its owner draws\" needs a DO block before it that moves a card, like \"Send it to the GY\"."); return; }
            var oc = ctx.Effect.NewLocal("oc");
            ctx.Effect.AddStep(
            [
                $"local {r}=0",
                $"local {oc}=Duel.GetOperatedGroup():GetFirst()",
                $"if {oc} then {r}=Duel.Draw({oc}:GetOwner(),{n}{per},REASON_EFFECT) end",
            ], r, ctx.Link);
            return;
        }
        switch (who)
        {
            case "each":
                ctx.Effect.TargetChecks.Add($"Duel.IsPlayerCanDraw(tp,{n}) and Duel.IsPlayerCanDraw(1-tp,{n})");
                ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,PLAYER_ALL,{n})");
                ctx.Effect.AddStep([$"local {r}=Duel.Draw(tp,{n}{per},REASON_EFFECT)+Duel.Draw(1-tp,{n}{per},REASON_EFFECT)"], r, ctx.Link);
                return;
            case "opponent_may":
                // Their choice as it resolves: nothing is checked as it is activated.
                ctx.Effect.AddStep(
                [
                    $"local {r}=0",
                    $"if Duel.IsPlayerCanDraw(1-tp,{n}) and Duel.SelectYesNo(1-tp,aux.Stringid(id,{Math.Min(ctx.Effect.Index, 16) - 1})) then {r}=Duel.Draw(1-tp,{n}{per},REASON_EFFECT) end",
                ], r, ctx.Link);
                return;
        }
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
    public override string Sentence => "Inflict {amount} damage to {who} {equal}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("amount", "Amount", 500, 1, 99999).ShowIf(UsesAmount),
        ParamSpec.Choice("who", "Who", new("opponent", "your opponent"), new("you", "you")),
        EqualParam,
        PerParam.ShowIf(UsesAmount),
    ];

    public override string Describe(BlockArgs args)
    {
        // Cards say "you take 1000 damage", not "inflict 1000 damage to you".
        var you = args.Choice("who") == "you";
        if (args.Choice("equal") != "amount") return you ? $"you take damage {args.Describe("equal")}" : $"inflict damage to {args.Describe("who")} {args.Describe("equal")}";
        var text = you ? $"you take {args.Int("amount")} damage" : $"inflict {args.Int("amount")} damage to {args.Describe("who")}";
        return args.Describe("per") is { Length: > 0 } per ? $"{text} {per}" : text;
    }

    public override void Compile(BlockContext ctx)
    {
        var p = PlayerOf(ctx.Args.Choice("who"));
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory("CATEGORY_DAMAGE");
        var v = ctx.Effect.NewLocal("v");
        if (EqualLines(ctx, v) is { } lines)
        {
            ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_DAMAGE,nil,0,{p},0)");
            ctx.Effect.AddStep([.. lines, $"local {r}=Duel.Damage({p},{v},REASON_EFFECT)"], r, ctx.Link);
            return;
        }
        var n = ctx.Args.Int("amount");
        var per = Multiplier(ctx);
        ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_DAMAGE,nil,0,{p},{n}{Multiplier(ctx, inTarget: true)})");
        ctx.Effect.AddStep([$"local {r}=Duel.Damage({p},{n}{per},REASON_EFFECT)"], r, ctx.Link);
    }
}

public sealed class GainLifePointsAction : ActionBlock
{
    public override string Id => "do.gain_lp";
    public override string Group => "Life Points";
    public override string Name => "Gain Life Points";
    public override string Sentence => "{who} gain(s) {amount} LP {equal}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("you", "You"), new("opponent", "Your opponent")),
        ParamSpec.Int("amount", "Amount", 1000, 1, 99999).ShowIf(UsesAmount),
        EqualParam,
        PerParam.ShowIf(UsesAmount),
    ];

    public override string Describe(BlockArgs args)
    {
        if (args.Choice("equal") != "amount")
            return (args.Choice("who") == "you" ? "gain LP " : "your opponent gains LP ") + args.Describe("equal");
        var text = args.Choice("who") == "you" ? $"gain {args.Int("amount")} LP" : $"your opponent gains {args.Int("amount")} LP";
        return args.Describe("per") is { Length: > 0 } per ? $"{text} {per}" : text;
    }

    public override void Compile(BlockContext ctx)
    {
        var p = PlayerOf(ctx.Args.Choice("who"));
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory("CATEGORY_RECOVER");
        var v = ctx.Effect.NewLocal("v");
        if (EqualLines(ctx, v) is { } lines)
        {
            ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_RECOVER,nil,0,{p},0)");
            ctx.Effect.AddStep([.. lines, $"local {r}=Duel.Recover({p},{v},REASON_EFFECT)"], r, ctx.Link);
            return;
        }
        var n = ctx.Args.Int("amount");
        var per = Multiplier(ctx);
        ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_RECOVER,nil,0,{p},{n}{Multiplier(ctx, inTarget: true)})");
        ctx.Effect.AddStep([$"local {r}=Duel.Recover({p},{n}{per},REASON_EFFECT)"], r, ctx.Link);
    }
}

public sealed class SpecialSummonSelfAction : ActionBlock
{
    public override string Id => "do.special_summon_self";
    public override string Group => "This card";
    public override string Name => "Special Summon this card";
    public override string Sentence => "Special Summon this card {with} {with_cards} {from} {side} {position} {as} {halved}";
    public override string Help => "Summons this card from wherever the effect is used: the WHEN block's \"in the hand\" or \"in the GY\". " +
                                   "\"From where its cost put it\" Summons it after the effect's own cost banished it or sent it to the GY or hand.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("from", "From", new("zone", "", "from where this effect is used"), new("cost", "", "from where its cost put it")),
        // Mimighoul: "Special Summon this card from your hand to your opponent's field in face-down Defense Position".
        ParamSpec.Choice("side", "Whose field", new("you", "", "to your field"), new("opponent", "to your opponent's field")),
        ParamSpec.Choice("position", "Position",
            new("faceup", "", "face-up"),
            new("attack", "in Attack Position"),
            new("defense", "in Defense Position"),
            new("defense_facedown", "in face-down Defense Position")),
        ParamSpec.Flag("ignore", "ignoring its Summoning conditions", false),
        ParamSpec.Flag("banish", "but banish it when it leaves the field", false),
        TreatedAsSummon.Param,
        // Heavy Freight Train Derricrane: "Special Summon this card from your hand, but its original ATK/DEF become halved".
        ParamSpec.Flag("halved", "but its original ATK/DEF become halved", false),
        // Saved before these were offered: this card alone, keeping its effects.
        ParamSpec.Flag("negate", "but negate its effects", false),
        ParamSpec.Choice("with", "Together with",
            new("none", "", "(this card alone)"), new("target", "", "and this effect's target (\"both it and this card\")"), new("pick", "", "and 1 monster picked below (from your Deck...)")),
        ParamSpec.Cards("with_cards", "The other monster", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster })
            .ShowIf(b => b.Values.GetValueOrDefault("with") == "pick"),
    ];

    /// <summary>"both it and this card", "this card from your hand and 1 "Rikka" monster from your Deck": the monsters Summoned together.</summary>
    private static string With(BlockArgs args)
    {
        var from = args.TriggerZone is { } z && Zones.From(z) is { } f ? " " + f : "";
        if (args.Choice("with") == "target") return CardRefs.Plural(args, CardRef.Target) ? "both them and this card" : "both it and this card";
        var cards = args.Cards("with_cards");
        // ", except "Primrose the Twin Rikka Fairy", in Defense Position".
        var except = FilterText.ExceptText(cards, args.Pack, args.CardName);
        var comma = except.Length > 0 && args.Describe("position").Length > 0 ? "," : "";
        return $"this card{from} and 1 {FilterText.Describe(cards, args.Pack, ownName: args.CardName, withExcept: false)}{except}{comma}";
    }

    /// <summary>"(This is treated as a Synchro Summon.)"</summary>
    public override string Afterword(BlockArgs args) => TreatedAsSummon.Afterword(args.Choice("as"));

    /// <summary>"Special Summon this card from your hand", taking the place from the WHEN block.</summary>
    public override string Describe(BlockArgs args)
    {
        var zone = args.TriggerZone;
        // Night Train Blue Traveler: "Special Summon both it and this card".
        if (args.Choice("with") != "none")
        {
            var both = $"Special Summon {With(args)} {args.Describe("position")}".TrimEnd();
            if (args.Bool("ignore")) both += ", ignoring their Summoning conditions";
            return args.Bool("negate") ? both + ", but negate their effects" : both;
        }
        var what = zone == "banished" ? "Special Summon this banished card" : "Special Summon this card";
        if (args.Choice("from") == "cost") what = SummonFromCost.Text(args);
        else if (zone is not null && Zones.From(zone) is { } from) what += " " + from;
        // A Pendulum Effect's "Special Summon this card from your Pendulum Zone".
        else if (zone == "pendulum_zone") what += " from your Pendulum Zone";
        if (args.Describe("side") is { Length: > 0 } side) what += " " + side;
        // Mimighoul: "..., or, if ..., you can Special Summon it face-up on your field instead", beside its Summon to the opponent's field.
        else if (args.Effect?.Do.Any(b => !ReferenceEquals(b, args.Instance) && b.BlockId == Id && b.Values.GetValueOrDefault("side") == "opponent") == true)
            what += args.Choice("position") == "faceup" ? " face-up on your field" : " to your field";
        what = $"{what} {args.Describe("position")}".TrimEnd();
        if (args.Bool("ignore")) what += ", ignoring its Summoning conditions";
        if (args.Bool("halved")) what += ", but its original ATK/DEF become halved";
        if (args.Bool("negate")) what += ", but negate its effects";
        return args.Bool("banish") ? what + ", but banish it when it leaves the field" : what;
    }

    /// <summary>"..., but banish it when it leaves the field": the Summoned card goes to banishment instead of anywhere else.</summary>
    public static IEnumerable<string> BanishWhenLeaves(string card, string v) =>
    [
        $"local {v}=Effect.CreateEffect(e:GetHandler())",
        $"{v}:SetType(EFFECT_TYPE_SINGLE)",
        $"{v}:SetCode(EFFECT_LEAVE_FIELD_REDIRECT)",
        $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
        $"{v}:SetReset(RESET_EVENT+RESETS_REDIRECT)",
        $"{v}:SetValue(LOCATION_REMOVED)",
        $"{card}:RegisterEffect({v},true)",
    ];

    private static string Position(BlockContext ctx) => ctx.Args.Choice("position") switch
    {
        "attack" => "POS_FACEUP_ATTACK",
        "defense" => "POS_FACEUP_DEFENSE",
        "defense_facedown" => "POS_FACEDOWN_DEFENSE",
        _ => "POS_FACEUP",
    };

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        // "You can banish this card; Special Summon this banished card" (SelfFlicker.cs).
        if (ctx.Args.Choice("from") == "cost")
        {
            SummonFromCost.Compile(ctx, Position(ctx), ctx.Args.Bool("ignore") ? "true" : "false");
            return;
        }
        if (ctx.Effect.Range == "LOCATION_MZONE")
            ctx.Effect.Error("\"Special Summon this card\" needs the effect to be used where this card isn't on the field: set the WHEN block to \"in the hand\", \"in the GY\" or \"while banished\".");
        var pos = Position(ctx);
        var ignore = ctx.Args.Bool("ignore") ? "true" : "false";
        var treated = ctx.Args.Choice("as");
        // To the opponent's field: in their Monster Zones, under their control.
        var opp = ctx.Args.Choice("side") == "opponent";
        var p = opp ? "1-tp" : "tp";
        if (opp && treated != "none") { ctx.Effect.Error($"\"{Name}\": a Summon treated as a Fusion, Synchro... Summon goes to your own field."); return; }
        if (ctx.Args.Bool("negate") && (treated != "none" || pos == "POS_FACEDOWN_DEFENSE" || opp || ctx.Args.Bool("halved")))
        {
            ctx.Effect.Error($"\"{Name}\": \"but negate its effects\" is for a plain face-up Special Summon to your field.");
            return;
        }
        if (ctx.Args.Choice("with") != "none")
        {
            if (opp || ctx.Args.Bool("halved")) { ctx.Effect.Error($"\"{Name}\": Summoning another monster together with this card is for a plain Summon to your field."); return; }
            CompileTogether(ctx);
            return;
        }
        if (treated != "none" && !TreatedAsSummon.Check(ctx, Name, 1, pos == "POS_FACEDOWN_DEFENSE")) return;
        // Ritual and Extra Deck Monsters (and "must be Special Summoned" ones) must be properly Summoned before they can
        // come back from the GY or banishment, unless the effect ignores their Summoning conditions (or, treated as a
        // Ritual Summon, lifts that limit as official cards do).
        var c = ctx.Card;
        if (ignore == "false" && treated != "ritual" && ctx.Effect.Range is "LOCATION_GRAVE" or "LOCATION_REMOVED" or "LOCATION_GRAVE+LOCATION_REMOVED"
            && (c.IsExtraDeck || c.Frame == MonsterFrame.Ritual || c.Abilities.HasFlag(MonsterAbility.SpecialSummonOnly)))
            ctx.Effect.Warn($"A {(c.Frame == MonsterFrame.Ritual ? "Ritual" : c.IsExtraDeck ? c.Frame.ToString() : "\"must be Special Summoned\"")} Monster can only be Special Summoned from the GY or banishment " +
                            "after it was properly Summoned first. Tick \"ignoring its Summoning conditions\" if this effect should work anyway.");
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory("CATEGORY_SPECIAL_SUMMON");
        var room = treated == "none" ? (opp ? "Duel.GetLocationCount(1-tp,LOCATION_MZONE,tp)>0" : "Duel.GetLocationCount(tp,LOCATION_MZONE)>0") : TreatedAsSummon.SpaceCheck(ctx, treated, Zone.Hand)!;
        ctx.Effect.TargetChecks.Add(room);
        // The second switch also lifts "must first be properly Summoned" (the engine's revive limit).
        ctx.Effect.TargetChecks.Add(treated == "none"
            ? $"e:GetHandler():IsCanBeSpecialSummoned(e,0,tp,{ignore},{ignore},{pos}{(opp ? ",1-tp" : "")})"
            : $"e:GetHandler():IsCanBeSpecialSummoned(e,{TreatedAsSummon.SummonType(treated)},tp,{ignore},{TreatedAsSummon.NoLimit(treated, ignore)},{pos})");
        ctx.Effect.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,e:GetHandler(),1,0,0)");
        var lines = new List<string> { $"local {r}=0", $"if c:IsRelateToEffect(e) and {room} then" };
        if (treated == "none" && ctx.Args.Bool("halved"))
        {
            // Its original ATK/DEF are halved as it is Summoned, as Derricrane does it (one Summon step, then the change).
            lines.AddRange(
            [
                $"\tif Duel.SpecialSummonStep(c,SUMMON_VALUE_SELF,tp,{p},{ignore},{ignore},{pos}) then",
                "\t\tlocal hb=Effect.CreateEffect(c)", "\t\thb:SetType(EFFECT_TYPE_SINGLE)", "\t\thb:SetCode(EFFECT_SET_BASE_ATTACK)",
                "\t\thb:SetValue(math.ceil(c:GetBaseAttack()/2))", "\t\thb:SetReset(RESET_EVENT+RESETS_STANDARD+RESET_DISABLE)", "\t\tc:RegisterEffect(hb)",
                "\t\tlocal hd=hb:Clone()", "\t\thd:SetCode(EFFECT_SET_BASE_DEFENSE)", "\t\thd:SetValue(math.ceil(c:GetBaseDefense()/2))", "\t\tc:RegisterEffect(hd)",
                $"\t\t{r}=1",
                "\tend",
                "\tDuel.SpecialSummonComplete()",
            ]);
        }
        else if (treated == "none" && ctx.Args.Bool("negate"))
            lines.Add($"\t{r}={SpecialSummonAction.NegatedSummon(ctx.Script, "Group.FromCards(c)", "SUMMON_VALUE_SELF", ignore, ignore, pos)}");
        else if (treated == "none") lines.Add($"\t{r}=Duel.SpecialSummon(c,SUMMON_VALUE_SELF,tp,{p},{ignore},{ignore},{pos})");
        else
        {
            // Treated as a Fusion Summon...: it counts as properly Summoned afterwards, though it had no materials.
            lines.Add("\tc:SetMaterial(nil)");
            lines.Add($"\t{r}=Duel.SpecialSummon(c,{TreatedAsSummon.SummonType(treated)},tp,tp,{ignore},{TreatedAsSummon.NoLimit(treated, ignore)},{pos})");
            lines.Add($"\tif {r}>0 then c:CompleteProcedure() end");
        }
        if (ctx.Args.Bool("banish"))
        {
            lines.Add($"\tif {r}>0 then");
            lines.AddRange(BanishWhenLeaves("c", "rd").Select(l => "\t\t" + l));
            lines.Add("\tend");
        }
        // A face-down monster on the opponent's field: its owner may still look at it.
        if (opp && pos == "POS_FACEDOWN_DEFENSE") lines.Add($"\tif {r}>0 then Duel.ConfirmCards(tp,c) end");
        lines.Add("end");
        ctx.Effect.AddStep(lines, r, ctx.Link);
    }

    /// <summary>
    /// "Special Summon both it and this card", "Special Summon this card from your hand and 1 "Rikka" monster from your
    /// Deck": one Summon of both, or neither, as "Special Summon monsters" with "and this card" does it.
    /// </summary>
    private void CompileTogether(BlockContext ctx)
    {
        if (ctx.Args.Choice("as") != "none" || ctx.Args.Bool("banish"))
        {
            ctx.Effect.Error($"\"{Name}\": this card Summoned together with another monster can't be treated as another Summon or banished when it leaves the field.");
            return;
        }
        var def = BlockRegistry.Find("do.special_summon")!;
        var inst = def.CreateInstance()
            .With("plus", "this").With("ignore", ctx.Args.Bool("ignore")).With("negate", ctx.Args.Bool("negate"))
            .With("position", ctx.Args.Choice("position") switch { "attack" => "attack", "defense" => "defense", "defense_facedown" => "facedown", _ => "faceup" })
            .With("cards", ctx.Args.Choice("with") == "target" ? new CardFilter { That = CardRef.Target, CardType = FilterCardType.Monster } : ctx.Args.Cards("with_cards").Clone())
            .Linked(ctx.Link);
        def.Compile(new BlockContext(new BlockArgs(def, inst, ctx.Args.Pack) { Effect = ctx.Args.Effect, CardName = ctx.Args.CardName }, ctx.Effect));
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
        ParamSpec.Choice("duration", "Until", Resets.Duration),
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
        var reset = ctx.Args.Choice("duration") switch
        {
            "turn" => "RESET_EVENT+RESETS_STANDARD+RESET_DISABLE+RESET_PHASE+PHASE_END",
            "always" => "RESET_EVENT+RESETS_STANDARD+RESET_DISABLE",
            _ => Resets.Of(ctx, "+RESET_DISABLE"),
        };
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
