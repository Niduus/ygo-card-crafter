using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// ---------------------------------------------------------------- Link Arrows

public sealed class LinkedCondition : ConditionBlock
{
    public override string Id => "if.linked";
    public override string Group => "Link";
    public override string Name => "This card is linked";
    public override string Sentence => "This card {state}";
    public override string Help => "Linked: a Link Arrow points to it or it points to a monster. Co-linked: arrows point both ways.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("state", "State",
            new("linked", "is linked"),
            new("colinked", "is co-linked"),
            new("unlinked", "is not linked")),
    ];

    public override string Describe(BlockArgs args) => $"this card {args.Describe("state")}";

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        ctx.Effect.Conditions.Add(ctx.Args.Choice("state") switch
        {
            "colinked" => "e:GetHandler():GetMutualLinkedGroupCount()>0",
            "unlinked" => "not e:GetHandler():IsLinkState()",
            _ => "e:GetHandler():IsLinkState()",
        });
    }
}

public sealed class PointsToCondition : ConditionBlock
{
    public override string Id => "if.points_to";
    public override string Group => "Link";
    public override string Name => "This card points to monsters";
    public override string Sentence => "This card points to {count} or more {cards}";
    public override string Help => "Counts the monsters in the zones this Link monster's arrows point to.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 1, 1, 8),
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var noun = FilterText.Describe(args.Cards("cards"), args.Pack, n != 1, "", withLocation: false);
        return n == 1 ? $"this card points to a {noun}" : $"this card points to {n} or more {noun}";
    }

    public override void Compile(BlockContext ctx)
    {
        if (!ctx.Card.IsLink) ctx.Effect.Error("\"This card points to\" needs this card to be a Link monster.");
        var f = ctx.Script.Filter(ctx.Args.Cards("cards"));
        ctx.Effect.Conditions.Add($"e:GetHandler():GetLinkedGroup():FilterCount({f.Function},nil,e,tp)>={ctx.Args.Int("count")}");
    }
}

// ---------------------------------------------------------------- Xyz

public sealed class MaterialsCondition : ConditionBlock
{
    public override string Id => "if.xyz_materials";
    public override string Group => "This card";
    public override string Name => "This card has Xyz Materials";
    public override string Sentence => "This card has {count} or more materials";
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Int("count", "How many", 1, 1, 10)];

    public override void Compile(BlockContext ctx)
    {
        if (!ctx.Card.IsXyz) ctx.Effect.Warn("Only Xyz monsters have materials.");
        ctx.Effect.Conditions.Add($"e:GetHandler():GetOverlayCount()>={ctx.Args.Int("count")}");
    }
}

public sealed class DetachCost : CostBlock
{
    public override string Id => "cost.detach";
    public override string Group => "This card";
    public override string Name => "Detach Xyz Materials";
    public override string Sentence => "Detach {count} material(s) from this card";
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Int("count", "How many", 1, 1, 10)];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        return n == 1 ? "detach 1 material from this card" : $"detach {n} materials from this card";
    }

    public override void Compile(BlockContext ctx)
    {
        if (!ctx.Card.IsXyz) ctx.Effect.Warn("Only Xyz monsters have materials to detach.");
        var n = ctx.Args.Int("count");
        ctx.Effect.CostChecks.Add($"e:GetHandler():CheckRemoveOverlayCard(tp,{n},REASON_COST)");
        ctx.Effect.CostLines.Add($"e:GetHandler():RemoveOverlayCard(tp,{n},{n},REASON_COST)");
    }
}

public sealed class UsedAsMaterialTrigger : SelfTriggerBlock
{
    public override string Id => "when.used_as_material";
    public override string Name => "This card is used as material";
    public override string Sentence => "If this card is used as material for a {kind} Summon";
    public override string Help => "Fusion, Synchro and Link Materials go to the GY, so they can activate. Xyz Materials go under the Xyz monster and can't.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("kind", "Summon",
            new("any", "Fusion, Synchro or Link"),
            new("fusion", "Fusion"),
            new("synchro", "Synchro"),
            new("link", "Link")),
        OptionalParam,
    ];

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        Setup(ctx, "EVENT_BE_MATERIAL");
        var reason = ctx.Args.Choice("kind") switch
        {
            "fusion" => "REASON_FUSION",
            "synchro" => "REASON_SYNCHRO",
            "link" => "REASON_LINK",
            _ => "REASON_FUSION+REASON_SYNCHRO+REASON_LINK",
        };
        ctx.Effect.Conditions.Add($"e:GetHandler():IsReason({reason})");
    }
}

// ---------------------------------------------------------------- Negation

public sealed class NegateAction : ActionBlock
{
    public override string Id => "do.negate";
    public override string Group => "Negate";
    public override string Name => "Negate the activation or effect";
    public override string Sentence => "Negate {what} {and}";
    public override string Help => "Use with a WHEN timing like \"when your opponent activates a card or effect\", like Ash Blossom or Solemn Judgment.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "What", new("activation", "the activation"), new("effect", "the effect")),
        ParamSpec.Choice("and", "And",
            new("nothing", "", "and nothing else"),
            new("destroy", "and if you do, destroy that card"),
            new("banish", "and if you do, banish that card")),
    ];

    public override string Describe(BlockArgs args) =>
        args.Choice("and") == "nothing" ? $"negate {args.Describe("what")}" : $"negate {args.Describe("what")}, {args.Describe("and")}";

    public override void Compile(BlockContext ctx)
    {
        if (!Timing.IsChainTiming(ctx.Effect.Code))
        {
            ctx.Effect.Error("\"Negate\" needs a WHEN timing that responds to an activation, such as \"when your opponent activates a card or effect\".");
            return;
        }
        var activation = ctx.Args.Choice("what") == "activation";
        var follow = ctx.Args.Choice("and");
        var r = ctx.Effect.NewLocal("r");
        var e = ctx.Effect;
        e.AddCategory(activation ? "CATEGORY_NEGATE" : "CATEGORY_DISABLE");
        e.Conditions.Add(activation ? "Duel.IsChainNegatable(ev)" : "Duel.IsChainDisablable(ev)");
        e.TargetLines.Add($"Duel.SetOperationInfo(0,{(activation ? "CATEGORY_NEGATE" : "CATEGORY_DISABLE")},eg,1,0,0)");
        if (follow == "destroy")
        {
            e.AddCategory("CATEGORY_DESTROY");
            e.TargetLines.Add("if re:GetHandler():IsDestructable() and re:GetHandler():IsRelateToEffect(re) then");
            e.TargetLines.Add("\tDuel.SetOperationInfo(0,CATEGORY_DESTROY,eg,1,0,0)");
            e.TargetLines.Add("end");
        }
        if (follow == "banish")
        {
            e.AddCategory("CATEGORY_REMOVE");
            e.TargetLines.Add("if re:GetHandler():IsAbleToRemove() and re:GetHandler():IsRelateToEffect(re) then");
            e.TargetLines.Add("\tDuel.SetOperationInfo(0,CATEGORY_REMOVE,eg,1,0,0)");
            e.TargetLines.Add("end");
        }
        var lines = new List<string>
        {
            $"local {r}=0",
            $"if Duel.{(activation ? "NegateActivation" : "NegateEffect")}(ev) then",
            $"\t{r}=1",
        };
        if (follow == "destroy") lines.AddRange(["\tif re:GetHandler():IsRelateToEffect(re) then", "\t\tDuel.Destroy(eg,REASON_EFFECT)", "\tend"]);
        if (follow == "banish") lines.AddRange(["\tif re:GetHandler():IsRelateToEffect(re) then", "\t\tDuel.Remove(eg,POS_FACEUP,REASON_EFFECT)", "\tend"]);
        lines.Add("end");
        e.AddStep(lines, r, ctx.Link);
    }
}

public sealed class NegateCardEffectsAction : CardGroupAction
{
    public override string Id => "do.negate_effects";
    public override string Group => "Negate";
    public override string Name => "Negate the effects of face-up cards";
    public override string Sentence => "Negate the effects of {count} {cards} {target} {duration}";
    public override string Help => "Like Effect Veiler or Infinite Impermanence: the chosen face-up cards lose their effects.";
    protected override string Category => "CATEGORY_DISABLE";
    protected override string Hint => "HINTMSG_FACEUP";
    protected override string TargetedWording => "negate {0} effects";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        .. base.BuildParams(),
        ParamSpec.Choice("duration", "Until", new("turn", "until the end of this turn"), new("always", "", "while face-up")),
    ];

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single)
    {
        var text = single ? "negate its effects" : "negate their effects";
        return args.Describe("duration") is { Length: > 0 } d ? $"{text} {d}" : text;
    }

    protected override string? AbleCheck(BlockContext ctx) =>
        "c:IsFaceup() and not c:IsDisabled() and (c:IsType(TYPE_EFFECT) or c:IsType(TYPE_SPELL+TYPE_TRAP))";

    protected override string Apply(string group, BlockContext ctx)
    {
        var reset = ctx.Args.Choice("duration") == "turn"
            ? "RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END"
            : "RESET_EVENT+RESETS_STANDARD";
        var fn = ctx.Script.AddFunction("negatecards", "g,e",
        [
            "local c=e:GetHandler()",
            "local n=0",
            "local tc=g:GetFirst()",
            "while tc do",
            "\tif tc:IsFaceup() and not tc:IsDisabled() then",
            "\t\tDuel.NegateRelatedChain(tc,RESET_TURN_SET)",
            "\t\tlocal e1=Effect.CreateEffect(c)",
            "\t\te1:SetType(EFFECT_TYPE_SINGLE)",
            "\t\te1:SetCode(EFFECT_DISABLE)",
            $"\t\te1:SetReset({reset})",
            "\t\ttc:RegisterEffect(e1)",
            "\t\tlocal e2=e1:Clone()",
            "\t\te2:SetCode(EFFECT_DISABLE_EFFECT)",
            "\t\ttc:RegisterEffect(e2)",
            "\t\tn=n+1",
            "\tend",
            "\ttc=g:GetNext()",
            "end",
            "return n",
        ]);
        return $"{fn}({group},e)";
    }
}

// ---------------------------------------------------------------- more card actions

public sealed class ChangeStatsAction : CardGroupAction
{
    public override string Id => "do.change_stats";
    public override string Group => "Monsters";
    public override string Name => "Monsters gain or lose ATK/DEF";
    public override string Sentence => "{count} {cards} {target} {dir} {amount} {stat} {duration}";
    protected override string Category => "CATEGORY_ATKCHANGE";
    protected override string Hint => "HINTMSG_FACEUP";
    protected override string TargetedWording => "{0} gains";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ParamSpec.Cards("cards", "Which monsters", DefaultCards, Zone.MonsterZone),
        ParamSpec.Flag("target", "(targeting)", false),
        ParamSpec.Choice("dir", "Gain/lose", new("gain", "gain(s)"), new("lose", "lose(s)")),
        ParamSpec.Int("amount", "Amount", 500, 1, 99999),
        ParamSpec.Choice("stat", "Stat", new("atk", "ATK"), new("def", "DEF"), new("both", "ATK/DEF")),
        ParamSpec.Choice("duration", "Until", new("turn", "until the end of this turn"), new("always", "", "while face-up")),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var verb = args.Choice("dir") == "gain" ? (n == 1 ? "gains" : "gain") : (n == 1 ? "loses" : "lose");
        var tail = $"{verb} {args.Int("amount")} {args.Describe("stat")} {args.Describe("duration")}".TrimEnd();
        return $"{n} {args.Describe("cards")} {tail}";
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single)
    {
        var verb = args.Choice("dir") == "gain" ? (single ? "gains" : "gain") : (single ? "loses" : "lose");
        return $"{pronoun} {verb} {args.Int("amount")} {args.Describe("stat")} {args.Describe("duration")}".TrimEnd();
    }

    protected override string? AbleCheck(BlockContext ctx) => "c:IsFaceup()";

    protected override string Apply(string group, BlockContext ctx)
    {
        var amount = ctx.Args.Int("amount") * (ctx.Args.Choice("dir") == "lose" ? -1 : 1);
        var reset = ctx.Args.Choice("duration") == "turn"
            ? "RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END"
            : "RESET_EVENT+RESETS_STANDARD";
        var codes = ctx.Args.Choice("stat") switch
        {
            "def" => new[] { "EFFECT_UPDATE_DEFENSE" },
            "both" => ["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"],
            _ => ["EFFECT_UPDATE_ATTACK"],
        };
        var body = new List<string> { "local c=e:GetHandler()", "local n=0", "local tc=g:GetFirst()", "while tc do", "\tif tc:IsFaceup() then" };
        foreach (var (code, i) in codes.Select((c, i) => (c, i)))
        {
            body.Add($"\t\tlocal e{i + 1}=Effect.CreateEffect(c)");
            body.Add($"\t\te{i + 1}:SetType(EFFECT_TYPE_SINGLE)");
            body.Add($"\t\te{i + 1}:SetCode({code})");
            body.Add($"\t\te{i + 1}:SetValue({amount.ToString(CultureInfo.InvariantCulture)})");
            body.Add($"\t\te{i + 1}:SetReset({reset})");
            body.Add($"\t\ttc:RegisterEffect(e{i + 1})");
        }
        body.AddRange(["\t\tn=n+1", "\tend", "\ttc=g:GetNext()", "end", "return n"]);
        var fn = ctx.Script.AddFunction("changestats", "g,e", body);
        return $"{fn}({group},e)";
    }
}

public sealed class SetSpellTrapAction : CardGroupAction
{
    public override string Id => "do.set";
    public override string Name => "Set Spells/Traps";
    public override string Sentence => "Set {count} {cards} {target}";
    protected override string Category => "";
    protected override string Hint => "HINTMSG_SET";
    protected override string TargetedWording => "Set {0}";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Trap };
    protected override string? AbleCheck(BlockContext ctx) => "c:IsSSetable()";
    protected override string Apply(string group, BlockContext ctx) => $"Duel.SSet(tp,{group})";
    protected override string? SpaceCheck(int count, CardFilter f, BlockContext ctx) => $"Duel.GetLocationCount(tp,LOCATION_SZONE)>={count}";
}

public sealed class MillAction : ActionBlock
{
    public override string Id => "do.mill";
    public override string Group => "Cards";
    public override string Name => "Send the top cards of a Deck to the GY";
    public override string Sentence => "Send the top {count} card(s) of {whose} Deck to the GY";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 1, 1, 20),
        ParamSpec.Choice("whose", "Whose", new("you", "your"), new("opponent", "your opponent's")),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        return $"send the top {(n == 1 ? "card" : n + " cards")} of {args.Describe("whose")} Deck to the GY";
    }

    public override void Compile(BlockContext ctx)
    {
        var p = PlayerOf(ctx.Args.Choice("whose"));
        var n = ctx.Args.Int("count");
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory("CATEGORY_DECKDES");
        ctx.Effect.TargetChecks.Add($"Duel.IsPlayerCanDiscardDeck({p},{n})");
        ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_DECKDES,nil,0,{p},{n})");
        ctx.Effect.AddStep([$"local {r}=Duel.DiscardDeck({p},{n},REASON_EFFECT)"], r, ctx.Link);
    }
}

public sealed class ThisCardAction : ActionBlock
{
    public override string Id => "do.this_card";
    public override string Group => "This card";
    public override string Name => "Destroy, banish or return this card";
    public override string Sentence => "{how} this card";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How",
            new("hand", "Return to the hand"),
            new("destroy", "Destroy"),
            new("banish", "Banish"),
            new("grave", "Send to the GY"),
            new("deck", "Shuffle into the Deck")),
    ];

    public override string Describe(BlockArgs args) => args.Choice("how") switch
    {
        "hand" => "return this card to the hand",
        "destroy" => "destroy this card",
        "banish" => "banish this card",
        "grave" => "send this card to the GY",
        _ => "shuffle this card into the Deck",
    };

    public override void Compile(BlockContext ctx)
    {
        var (category, check, call) = ctx.Args.Choice("how") switch
        {
            "destroy" => ("CATEGORY_DESTROY", (string?)null, "Duel.Destroy(c,REASON_EFFECT)"),
            "banish" => ("CATEGORY_REMOVE", "e:GetHandler():IsAbleToRemove()", "Duel.Remove(c,POS_FACEUP,REASON_EFFECT)"),
            "grave" => ("CATEGORY_TOGRAVE", "e:GetHandler():IsAbleToGrave()", "Duel.SendtoGrave(c,REASON_EFFECT)"),
            "deck" => ("CATEGORY_TODECK", "e:GetHandler():IsAbleToDeck()", "Duel.SendtoDeck(c,nil,SEQ_DECKSHUFFLE,REASON_EFFECT)"),
            _ => ("CATEGORY_TOHAND", "e:GetHandler():IsAbleToHand()", "Duel.SendtoHand(c,nil,REASON_EFFECT)"),
        };
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddCategory(category);
        if (check is not null) ctx.Effect.TargetChecks.Add(check);
        ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,{category},e:GetHandler(),1,0,0)");
        ctx.Effect.AddStep([$"local {r}=0", "if c:IsRelateToEffect(e) then", $"\t{r}={call}", "end"], r, ctx.Link);
    }
}

// ---------------------------------------------------------------- lasting

public sealed class ProtectCardsPassive : PassiveBlock
{
    public override string Id => "passive.protect_cards";
    public override string Group => "Monsters";
    public override string Name => "Monsters can't be destroyed";
    public override string Sentence => "{cards} cannot be destroyed {by}";
    public override string Help => "For example: monsters this card points to cannot be destroyed by battle.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone),
        ParamSpec.Choice("by", "By",
            new("battle", "by battle"),
            new("effect", "by card effects"),
            new("both", "by battle or card effects")),
    ];

    public override string Describe(BlockArgs args) =>
        $"{FilterText.Describe(args.Cards("cards"), args.Pack, plural: true, preposition: "")} cannot be destroyed {args.Describe("by")}";

    public override void Compile(BlockContext ctx)
    {
        var filter = ctx.Args.Cards("cards").Clone();
        filter.Zones = Zone.MonsterZone;
        var f = ctx.Script.Filter(filter);
        var target = ctx.Script.AddFunction("protecttg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")]);
        var range = RangeOf(ctx);
        var by = ctx.Args.Choice("by");
        var codes = by switch
        {
            "effect" => new[] { "EFFECT_INDESTRUCTABLE_EFFECT" },
            "both" => ["EFFECT_INDESTRUCTABLE_BATTLE", "EFFECT_INDESTRUCTABLE_EFFECT"],
            _ => ["EFFECT_INDESTRUCTABLE_BATTLE"],
        };
        foreach (var code in codes)
            ctx.Effect.AddPassive(v =>
            [
                $"{v}:SetType(EFFECT_TYPE_FIELD)",
                $"{v}:SetCode({code})",
                $"{v}:SetRange({range})",
                $"{v}:SetTargetRange({f.Self},{f.Opp})",
                $"{v}:SetTarget({target})",
                $"{v}:SetValue(1)",
            ]);
    }
}
