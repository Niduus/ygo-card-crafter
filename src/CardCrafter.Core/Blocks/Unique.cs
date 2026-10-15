using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// Blocks for the mechanics that make modern archetypes unusual: Mitsurugi (Tributing, Rituals, "Tribute this card
// instead"), Dominus (Traps from the hand, locks for the rest of the Duel), Elfnote and Infinite Impermanence (zones and
// columns), Forbidden Crown (a "freeze" with independent effects), Mind Scan (revealed hands, declaring a card name).

// ---------------------------------------------------------------- Mitsurugi

/// <summary>"Reveal this card in your hand" as a cost.</summary>
public sealed class RevealSelfCost : CostBlock
{
    public override string Id => "cost.reveal_self";
    public override string Group => "This card";
    public override string Name => "Reveal this card in your hand";
    public override string Sentence => "Reveal this card in your hand";

    public override string Describe(BlockArgs args) => "reveal this card in your hand";

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Effect.Range is not null && ctx.Effect.Range != "LOCATION_HAND")
            ctx.Effect.Error("\"Reveal this card in your hand\" needs the effect to be used from the hand.");
        ctx.Effect.CostChecks.Add("not e:GetHandler():IsPublic()");
        ctx.Effect.CostLines.Add("Duel.ConfirmCards(1-tp,e:GetHandler())");
    }
}

/// <summary>"Tribute 1 monster you control" or, from Mitsurugi, "Tribute 1 Reptile monster from your hand or field", as an effect.</summary>
public sealed class TributeAction : CardGroupAction
{
    protected override bool AllowsThisCard => true;
    protected override string TargetedWording => "Tribute {0}";
    public override string Id => "do.tribute";
    public override string Name => "Tribute monsters (as an effect)";
    public override string Sentence => "Tribute {count} {cards} {target}";
    public override string Help => "Tributing by an effect, like Mitsurugi: \"Special Summon 1 monster, then Tribute 1 monster you control\". " +
                                   "Pick the hand as well as the Monster Zone to Tribute from the hand or field.";
    protected override string Category => "CATEGORY_RELEASE";
    protected override string Hint => "HINTMSG_RELEASE";
    protected override int MaxCount => 5;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
    protected override string? AbleCheck(BlockContext ctx) => "c:IsReleasableByEffect()";
    protected override string Apply(string g, BlockContext ctx) => $"Duel.Release({g},REASON_EFFECT)";
}

/// <summary>"Make your opponent Tribute 1 monster they control (their choice)": Mitsurugi Sacred Boundary.</summary>
public sealed class OpponentTributesAction : ActionBlock
{
    public override string Id => "do.opponent_tributes";
    public override string Group => "Cards";
    public override string Name => "Your opponent Tributes a monster (their choice)";
    public override string Sentence => "Your opponent Tributes {count} monster(s) they control (their choice)";
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Int("count", "How many", 1, 1, 5)];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        return $"make your opponent Tribute {n} monster{(n == 1 ? "" : "s")} they control (their choice)";
    }

    public override void Compile(BlockContext ctx)
    {
        var n = ctx.Args.Int("count");
        var r = ctx.Effect.NewLocal("r");
        var g = ctx.Effect.NewLocal("g");
        ctx.Effect.AddCategory("CATEGORY_RELEASE");
        ctx.Effect.TargetChecks.Add($"Duel.CheckReleaseGroupEx(1-tp,nil,{n},REASON_RULE,false,nil)");
        ctx.Effect.AddStep(
        [
            $"local {r}=0",
            $"if Duel.CheckReleaseGroupEx(1-tp,nil,{n},REASON_RULE,false,nil) then",
            $"\tlocal {g}=Duel.SelectReleaseGroupEx(1-tp,nil,{n},{n},REASON_RULE,false,nil)",
            $"\tDuel.HintSelection({g})",
            $"\t{r}=Duel.Release({g},REASON_RULE,1-tp)",
            "end",
        ], r, ctx.Link);
    }
}

/// <summary>
/// "If another Reptile monster(s) you control would be destroyed by battle or card effect, you can Tribute this card
/// instead." Mitsurugi, Ryzeal Detonator (detach a material), Toon Bookmark (banish it from the GY).
/// </summary>
public sealed class ReplaceDestroyPassive : PassiveBlock
{
    public const string BlockId = "passive.replace_destroy";
    public override string Id => BlockId;
    public override string Group => "Protection";
    public override string Name => "If cards would be destroyed, do something with this card instead";
    public override string Sentence => "If {cards} would be destroyed {by}, you can {how} instead";
    public override string Help => "A replacement: it isn't activated and doesn't start a chain. The effect's \"once per turn\" applies to it. " +
                                   "Tick \"other\" in the picker for \"another monster\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, ExcludeThisCard = true }, Zone.Field),
        ParamSpec.Choice("by", "By", new("any", "by battle or card effect"), new("effect", "by card effect"), new("battle", "by battle")),
        ParamSpec.Choice("how", "Instead",
            new("tribute", "Tribute this card"),
            new("destroy", "destroy this card"),
            new("banish", "banish this card"),
            new("grave", "send this card to the GY"),
            new("detach", "detach 1 material from this card")),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        var noun = FilterText.Describe(f, args.Pack, false, "", withLocation: false, withExcept: false);
        if (noun.StartsWith("other ", StringComparison.Ordinal)) noun = noun[6..];
        noun += "(s)";
        var article = f.ExcludeThisCard ? "another" : "AEIOUaeiou".Contains(noun[0]) ? "an" : "a";
        var where = FilterText.IsFieldOnly(f)
            ? f.Owner switch { CardOwner.You => " you control", CardOwner.Opponent => " your opponent controls", _ => " on the field" }
            : "";
        var how = args.Choice("how") == "banish" && args.TriggerZone == "graveyard" ? "banish this card from your GY" : args.Describe("how");
        return $"if {article} {noun}{where} would be destroyed {args.Describe("by")}, you can {how} instead";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var f = ctx.Args.Cards("cards");
        var range = RangeOf(ctx);
        var how = ctx.Args.Choice("how");
        if (how == "detach" && !ctx.Card.IsXyz) e.Error("\"Detach 1 material from this card\" needs an Xyz Monster.");
        var reason = ctx.Args.Choice("by") switch { "effect" => "REASON_EFFECT", "battle" => "REASON_BATTLE", _ => "REASON_BATTLE+REASON_EFFECT" };
        var loc = f.Zones == Zone.MonsterZone ? "LOCATION_MZONE" : "LOCATION_ONFIELD";
        var owner = f.Owner switch { CardOwner.You => " and c:IsControler(tp)", CardOwner.Opponent => " and c:IsControler(1-tp)", _ => "" };
        var rf = ctx.Script.Filter(f, $"c:IsLocation({loc}){owner} and c:IsReason({reason}) and not c:IsReason(REASON_REPLACE)", "repfilter");
        var (can, pay) = how switch
        {
            "tribute" => ("c:IsReleasableByEffect()", "Duel.Release(e:GetHandler(),REASON_EFFECT+REASON_REPLACE)"),
            "destroy" => ("c:IsDestructable(e)", "Duel.Destroy(e:GetHandler(),REASON_EFFECT+REASON_REPLACE)"),
            "banish" => ("c:IsAbleToRemove()", "Duel.Remove(e:GetHandler(),POS_FACEUP,REASON_EFFECT+REASON_REPLACE)"),
            "grave" => ("c:IsAbleToGrave()", "Duel.SendtoGrave(e:GetHandler(),REASON_EFFECT+REASON_REPLACE)"),
            _ => ("c:CheckRemoveOverlayCard(tp,1,REASON_EFFECT)", "e:GetHandler():RemoveOverlayCard(tp,1,1,REASON_EFFECT)"),
        };
        var exclude = f.ExcludeThisCard ? "c" : "nil";
        var tg = ctx.Script.AddFunction("reptg", "e,tp,eg,ep,ev,re,r,rp,chk",
        [
            "local c=e:GetHandler()",
            $"if chk==0 then return eg:IsExists({rf.Function},1,{exclude},e,tp) and {can} and not c:IsStatus(STATUS_DESTROY_CONFIRMED) end",
            "return Duel.SelectEffectYesNo(tp,c,96)",
        ]);
        var val = ctx.Script.AddFunction("repval", "e,c", [$"return {rf.Function}(c,e,e:GetHandlerPlayer())"]);
        var op = ctx.Script.AddFunction("repop", "e,tp,eg,ep,ev,re,r,rp", [$"Duel.Hint(HINT_CARD,0,id)", pay]);
        e.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetCode(EFFECT_DESTROY_REPLACE)", $"{v}:SetRange({range})",
            $"{v}:SetTarget({tg})", $"{v}:SetValue({val})", $"{v}:SetOperation({op})",
        ], limited: true);
    }
}

/// <summary>
/// "Ritual Summon 1 Reptile Ritual Monster from your hand, by Tributing monsters from your hand or field whose total
/// Levels equal its Level." Built on the engine's own Ritual helpers.
/// </summary>
public sealed class RitualSummonAction : ActionBlock
{
    public override string Id => "do.ritual_summon";
    public override string Group => "Summon";
    public override string Name => "Ritual Summon a monster";
    public override string Sentence => "Ritual Summon 1 {cards}, by Tributing {mats} from your hand or field whose total Levels {how} its Level";
    public override string Help => "A Ritual Spell's effect (or any card's, like Mitsurugi Magatama). Pick where the Ritual Monster can come from: hand, Deck or GY.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Ritual Monster", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Ritual }, Zone.Hand | Zone.Deck | Zone.Graveyard),
        ParamSpec.Cards("mats", "Tributed monsters", () => new CardFilter { CardType = FilterCardType.Monster }, Zone.MonsterZone),
        ParamSpec.Choice("how", "Levels", new("equal", "equal"), new("greater", "equal or exceed")),
    ];

    public override string Describe(BlockArgs args)
    {
        var ritual = args.Cards("cards").Clone();
        ritual.MonsterKind = MonsterKind.Ritual;
        var mats = FilterText.Describe(args.Cards("mats"), args.Pack, plural: true, withLocation: false);
        return $"Ritual Summon 1 {FilterText.Describe(ritual, args.Pack)}, by Tributing {mats} from your hand or field whose total Levels {args.Describe("how")} its Level";
    }

    public override void Compile(BlockContext ctx)
    {
        var ritual = ctx.Args.Cards("cards").Clone();
        ritual.MonsterKind = MonsterKind.Ritual;
        var where = new List<string>();
        if (ritual.Zones.HasFlag(Zone.Hand)) where.Add("LOCATION_HAND");
        if (ritual.Zones.HasFlag(Zone.Deck)) where.Add("LOCATION_DECK");
        if (ritual.Zones.HasFlag(Zone.Graveyard)) where.Add("LOCATION_GRAVE");
        if (where.Count == 0) { ctx.Effect.Error("\"Ritual Summon\": pick the hand, Deck or GY for the Ritual Monster."); return; }
        var loc = string.Join("+", where);
        var rf = ctx.Script.MaterialFilter(ritual, "ritfilter");
        var mf = ctx.Script.MaterialFilter(ctx.Args.Cards("mats"), "ritmatfilter");
        var how = ctx.Args.Choice("how") == "greater" ? "\"Greater\"" : "\"Equal\"";
        var args = $"{(rf == "nil" ? "aux.TRUE" : rf)},Card.GetLevel,{how},{loc},nil,{mf}";
        ctx.Effect.AddCategory("CATEGORY_SPECIAL_SUMMON");
        ctx.Effect.AddCategory("CATEGORY_RELEASE");
        ctx.Effect.TargetChecks.Add($"aux.RitualUltimateTarget({args})(e,tp,eg,ep,ev,re,r,rp,0)");
        ctx.Effect.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,{loc})");
        ctx.Effect.AddStep([$"aux.RitualUltimateOperation({args})(e,tp,eg,ep,ev,re,r,rp)"], null, ctx.Link);
    }
}

/// <summary>"Your opponent can discard 1 card, or that effect is negated": Ame no Murakumo no Mitsurugi.</summary>
public sealed class NegateUnlessAction : ActionBlock
{
    public override string Id => "do.negate_unless";
    public override string Group => "Negate";
    public override string Name => "Your opponent pays something, or that effect is negated";
    public override string Sentence => "Your opponent can {pay}, or that effect is negated";
    public override string Help => "Use with a timing that responds to an activation. Your opponent chooses: pay, or have the effect negated.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("pay", "They can", new("discard", "discard 1 card"), new("lp", "pay 1000 LP")),
    ];

    public override string Describe(BlockArgs args) => $"your opponent can {args.Describe("pay")}, or that effect is negated";

    public override void Compile(BlockContext ctx)
    {
        if (!Timing.IsChainTiming(ctx.Effect.Code))
        {
            ctx.Effect.Error("\"Or that effect is negated\" needs a WHEN timing that responds to an activation.");
            return;
        }
        var e = ctx.Effect;
        e.AddCategory("CATEGORY_DISABLE");
        e.Conditions.Add("Duel.IsChainDisablable(ev)");
        var discard = ctx.Args.Choice("pay") == "discard";
        if (discard) e.AddCategory("CATEGORY_HANDES");
        var can = discard ? "Duel.IsExistingMatchingCard(Card.IsDiscardable,1-tp,LOCATION_HAND,0,1,nil,REASON_EFFECT)" : "Duel.CheckLPCost(1-tp,1000)";
        var pay = discard ? "Duel.DiscardHand(1-tp,aux.TRUE,1,1,REASON_EFFECT+REASON_DISCARD,nil)" : "Duel.PayLPCost(1-tp,1000)";
        e.AddStep(
        [
            $"if {can} and Duel.SelectYesNo(1-tp,aux.Stringid(id,{Math.Min(e.Index, 16) - 1})) then",
            $"\t{pay}",
            "else",
            "\tDuel.NegateEffect(ev)",
            "end",
        ], null, ctx.Link);
    }
}

// ---------------------------------------------------------------- Dominus

/// <summary>"If your opponent controls a card, you can activate this card from your hand" (Dominus, Infinite Impermanence).</summary>
public sealed class ActivateFromHandPassive : PassiveBlock
{
    public const string BlockId = "passive.activate_from_hand";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "You can activate this Trap from your hand";
    public override string Sentence => "You can activate this card from your hand";
    public override string Help => "For Trap Cards. Add IF blocks for when it's allowed, like \"your opponent controls a card\" or \"you control no cards\".";

    public override string Describe(BlockArgs args) => "you can activate this card from your hand";

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Card.Kind != CardKind.Trap) ctx.Effect.Error("Only Trap Cards can be activated from the hand this way.");
        ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_SINGLE)", $"{v}:SetCode(EFFECT_TRAP_ACT_IN_HAND)"]);
    }
}

/// <summary>"During the turn in which your opponent has activated a monster effect in the hand or GY": Dominus Spark.</summary>
public sealed class OpponentActivatedCondition : ConditionBlock
{
    public override string Id => "if.opponent_activated";
    public override string Group => "Turn";
    public override string Name => "Your opponent activated something this turn";
    public override string Sentence => "Your opponent has activated {what} this turn";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "What",
            new("monster_hand_gy", "a monster effect in the hand or GY"),
            new("monster", "a monster effect"),
            new("spelltrap", "a Spell/Trap Card"),
            new("any", "a card or effect")),
    ];

    public override string Describe(BlockArgs args) => $"your opponent has activated {args.Describe("what")} this turn";

    public override void Compile(BlockContext ctx)
    {
        var test = ctx.Args.Choice("what") switch
        {
            "monster_hand_gy" => "re:IsActiveType(TYPE_MONSTER) and re:GetActivateLocation()&(LOCATION_HAND+LOCATION_GRAVE)~=0",
            "monster" => "re:IsActiveType(TYPE_MONSTER)",
            "spelltrap" => "re:IsHasType(EFFECT_TYPE_ACTIVATE)",
            _ => "true",
        };
        var filter = ctx.Script.AddFunction("chainfilter", "re,tp,cid", [$"return not ({test})"]);
        var counter = ctx.Script.ActivityCounter("ACTIVITY_CHAIN", filter);
        ctx.Effect.Conditions.Add($"Duel.GetCustomActivityCount({counter},1-tp,ACTIVITY_CHAIN)>0");
    }
}

/// <summary>"..., then if you have a Trap in your GY, destroy that card": the DO blocks after this one happen only if it holds.</summary>
public sealed class OnlyIfAction : ActionBlock
{
    public const string BlockId = "do.only_if";
    public override string Id => BlockId;
    public override string Group => "Flow";
    public override string Name => "Only continue if there are (no) cards...";
    public override string Sentence => "if there {is} {count} {cards}";
    public override string Help => "Stops the effect here unless the cards are there, so the DO blocks after it only happen if they are: " +
                                   "\"then if you have a Trap in your GY, destroy that card\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("is", "There are", new("some", "are"), new("none", "are no")),
        ParamSpec.Int("count", "How many (or more)", 1, 1, 60),
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Trap }),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        var n = args.Int("count");
        if (args.Choice("is") == "none") return $"if there are no {FilterText.Describe(f, args.Pack, true, "in")}";
        return FilterText.IsFieldOnly(f) && f.Owner != CardOwner.Either
            ? $"if {(f.Owner == CardOwner.You ? "you control" : "your opponent controls")} {(n == 1 ? "a" : n + " or more")} {FilterText.Describe(f, args.Pack, n != 1, "", withLocation: false)}"
            : f.Owner == CardOwner.You && f.Zones == Zone.Graveyard
                ? $"if you have {(n == 1 ? "a" : n + " or more")} {FilterText.Describe(f, args.Pack, n != 1, withLocation: false)} in your GY"
                : $"if there {(n == 1 ? "is a" : $"are {n} or more")} {FilterText.Describe(f, args.Pack, n != 1, "in")}";
    }

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Script.Filter(ctx.Args.Cards("cards"));
        var exists = $"Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},{f.Opp},{ctx.Args.Int("count")},{f.Exclude},e,tp)";
        ctx.Effect.AddStep([ctx.Args.Choice("is") == "none" ? $"if {exists} then return end" : $"if not {exists} then return end"], null, ctx.Link);
    }
}

/// <summary>"Destroy that card": the card whose activation this effect responds to.</summary>
public sealed class ThatCardAction : ActionBlock
{
    public override string Id => "do.that_card";
    public override string Group => "Negate";
    public override string Name => "Destroy / banish / return that card (the one activated)";
    public override string Sentence => "{how} that card";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "What", new("destroy", "Destroy"), new("banish", "Banish"), new("hand", "Return to the hand:"), new("deck", "Shuffle into the Deck:")),
    ];

    public override string Describe(BlockArgs args) => args.Choice("how") switch
    {
        "banish" => "banish that card",
        "hand" => "return that card to the hand",
        "deck" => "shuffle that card into the Deck",
        _ => "destroy that card",
    };

    public override void Compile(BlockContext ctx)
    {
        if (!Timing.IsChainTiming(ctx.Effect.Code))
        {
            ctx.Effect.Error("\"That card\" needs a WHEN timing that responds to an activation.");
            return;
        }
        var (cat, act) = ctx.Args.Choice("how") switch
        {
            "banish" => ("CATEGORY_REMOVE", "Duel.Remove(rc,POS_FACEUP,REASON_EFFECT)"),
            "hand" => ("CATEGORY_TOHAND", "Duel.SendtoHand(rc,nil,REASON_EFFECT)"),
            "deck" => ("CATEGORY_TODECK", "Duel.SendtoDeck(rc,nil,SEQ_DECKSHUFFLE,REASON_EFFECT)"),
            _ => ("CATEGORY_DESTROY", "Duel.Destroy(rc,REASON_EFFECT)"),
        };
        ctx.Effect.AddCategory(cat);
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddStep(
        [
            $"local {r}=0",
            "local rc=re:GetHandler()",
            $"if rc:IsRelateToEffect(re) then {r}={act} end",
        ], r, ctx.Link);
    }
}

// ---------------------------------------------------------------- Zones (Elfnote, Infinite Impermanence)

/// <summary>"If this card is in the center Main Monster Zone".</summary>
public sealed class ZoneCondition : ConditionBlock
{
    public override string Id => "if.zone";
    public override string Group => "This card";
    public override string Name => "This card is in a certain zone";
    public override string Sentence => "This card is in {zone}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("zone", "Zone",
            new("center", "the center Main Monster Zone"),
            new("main", "a Main Monster Zone"),
            new("extra", "an Extra Monster Zone"),
            new("not_center", "a Main Monster Zone other than the center one")),
    ];

    public override string Describe(BlockArgs args) => $"this card is in {args.Describe("zone")}";

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        ctx.Effect.Conditions.Add(ctx.Args.Choice("zone") switch
        {
            "main" => "e:GetHandler():IsLocation(LOCATION_MZONE) and e:GetHandler():GetSequence()<5",
            "extra" => "e:GetHandler():IsLocation(LOCATION_MZONE) and e:GetHandler():GetSequence()>=5",
            "not_center" => "e:GetHandler():IsLocation(LOCATION_MZONE) and e:GetHandler():GetSequence()<5 and e:GetHandler():GetSequence()~=2",
            _ => "e:GetHandler():IsLocation(LOCATION_MZONE) and e:GetHandler():GetSequence()==2",
        });
    }
}

/// <summary>"Move this card to another of your Main Monster Zones" or "switch the locations of this card and the monster in your center Main Monster Zone".</summary>
public sealed class MoveZoneAction : ActionBlock
{
    public override string Id => "do.move_zone";
    public override string Group => "Cards";
    public override string Name => "Move a monster to another zone";
    public override string Sentence => "{what}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "What",
            new("self", "Move this card to another of your Main Monster Zones"),
            new("monster", "Move 1 face-up monster you control to another of your Main Monster Zones"),
            new("to_center", "Move this card to your center Main Monster Zone"),
            new("swap_center", "Switch the locations of this card and the monster in your center Main Monster Zone")),
    ];

    public override string Describe(BlockArgs args) => args.Choice("what") switch
    {
        "monster" => "move 1 face-up monster you control to another of your Main Monster Zones",
        "to_center" => "move this card to your center Main Monster Zone",
        "swap_center" => "switch the locations of this card in your Main Monster Zone and the monster in your center Main Monster Zone",
        _ => "move this card to another of your Main Monster Zones",
    };

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var r = e.NewLocal("r");
        var lines = new List<string> { $"local {r}=0" };
        const string mine = "c:IsRelateToEffect(e) and c:IsControler(tp) and c:IsLocation(LOCATION_MZONE)";
        switch (ctx.Args.Choice("what"))
        {
            case "swap_center":
                Zones.RequireMonster(ctx, Name);
                e.TargetChecks.Add("Duel.GetFieldCard(tp,LOCATION_MZONE,2)~=nil and e:GetHandler():GetSequence()<5 and e:GetHandler():GetSequence()~=2");
                lines.Add("local center=Duel.GetFieldCard(tp,LOCATION_MZONE,2)");
                lines.Add($"if center and {mine} and c:GetSequence()<5 and c:GetSequence()~=2 then");
                lines.Add("\tlocal before=c:GetSequence()");
                lines.Add("\tDuel.SwapSequence(c,center)");
                lines.Add($"\tif c:GetSequence()~=before then {r}=1 end");
                lines.Add("end");
                break;
            case "to_center":
                Zones.RequireMonster(ctx, Name);
                e.TargetChecks.Add("Duel.CheckLocation(tp,LOCATION_MZONE,2) and e:GetHandler():GetSequence()<5");
                lines.Add($"if {mine} and Duel.CheckLocation(tp,LOCATION_MZONE,2) then");
                lines.Add("\tDuel.MoveSequence(c,2)");
                lines.Add($"\tif c:GetSequence()==2 then {r}=1 end");
                lines.Add("end");
                break;
            case "monster":
                e.TargetChecks.Add("Duel.IsExistingMatchingCard(Card.IsFaceup,tp,LOCATION_MZONE,0,1,nil) and Duel.GetLocationCount(tp,LOCATION_MZONE,PLAYER_NONE,0)>0");
                lines.Add("if Duel.GetLocationCount(tp,LOCATION_MZONE,PLAYER_NONE,0)>0 then");
                lines.Add("\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_FACEUP)");
                lines.Add("\tlocal mc=Duel.SelectMatchingCard(tp,Card.IsFaceup,tp,LOCATION_MZONE,0,1,1,nil):GetFirst()");
                lines.Add("\tif mc then");
                lines.Add("\t\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_TOZONE)");
                lines.Add("\t\tlocal seq=Duel.SelectDisableField(tp,1,LOCATION_MZONE,0,0)");
                lines.Add("\t\tDuel.MoveSequence(mc,math.log(seq,2))");
                lines.Add($"\t\t{r}=1");
                lines.Add("\tend");
                lines.Add("end");
                break;
            default:
                Zones.RequireMonster(ctx, Name);
                e.TargetChecks.Add("Duel.GetLocationCount(tp,LOCATION_MZONE,PLAYER_NONE,0)>0");
                lines.Add($"if {mine} and Duel.GetLocationCount(tp,LOCATION_MZONE,PLAYER_NONE,0)>0 then");
                lines.Add("\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_TOZONE)");
                lines.Add("\tlocal seq=Duel.SelectDisableField(tp,1,LOCATION_MZONE,0,0)");
                lines.Add("\tDuel.MoveSequence(c,math.log(seq,2))");
                lines.Add($"\t{r}=1");
                lines.Add("end");
                break;
        }
        e.AddStep(lines, r, ctx.Link);
    }
}

/// <summary>
/// Infinite Impermanence: "if this card was Set before activation and is on the field at resolution, for the rest of this
/// turn all other Spell/Trap effects in this column are negated".
/// </summary>
public sealed class NegateColumnAction : ActionBlock
{
    public override string Id => "do.negate_column";
    public override string Group => "Negate";
    public override string Name => "Negate Spell/Trap effects in this card's column";
    public override string Sentence => "If this card was Set before activation, negate all other Spell/Trap effects in this column for the rest of this turn";

    public override string Describe(BlockArgs args) =>
        "if this card was Set before activation and is on the field at resolution, for the rest of this turn all other Spell/Trap effects in this column are negated";

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Card.IsMonster) ctx.Effect.Error("\"In this column\" is for Spells and Traps that are Set in a column.");
        var distg = ctx.Script.AddFunction("coltg", "e,c",
        [
            "local seq,fid=e:GetLabel()",
            "return c:IsType(TYPE_SPELL+TYPE_TRAP) and aux.GetColumn(c,e:GetHandlerPlayer())==seq and c:GetFieldID()~=fid",
        ]);
        var disop = ctx.Script.AddFunction("colop", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local tseq=e:GetLabel()",
            "local controller,loc,seq=Duel.GetChainInfo(ev,CHAININFO_TRIGGERING_CONTROLER,CHAININFO_TRIGGERING_LOCATION,CHAININFO_TRIGGERING_SEQUENCE)",
            "if loc&LOCATION_SZONE~=0 and seq<=4 and re:IsActiveType(TYPE_SPELL+TYPE_TRAP)",
            "\t\tand ((controller==tp and seq==tseq) or (controller==1-tp and seq==4-tseq)) then",
            "\tDuel.NegateEffect(ev)",
            "end",
        ]);
        ctx.Effect.AddStep(
        [
            "if not c:IsStatus(STATUS_ACT_FROM_HAND) and c:IsRelateToEffect(e) and c:IsLocation(LOCATION_SZONE) then",
            "\tlocal cd=Effect.CreateEffect(c)",
            "\tcd:SetType(EFFECT_TYPE_FIELD)",
            "\tcd:SetCode(EFFECT_DISABLE)",
            "\tcd:SetTargetRange(LOCATION_SZONE,LOCATION_SZONE)",
            $"\tcd:SetTarget({distg})",
            "\tcd:SetReset(RESET_PHASE+PHASE_END)",
            "\tcd:SetLabel(c:GetSequence(),c:GetFieldID())",
            "\tDuel.RegisterEffect(cd,tp)",
            "\tlocal cs=Effect.CreateEffect(c)",
            "\tcs:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "\tcs:SetCode(EVENT_CHAIN_SOLVING)",
            $"\tcs:SetOperation({disop})",
            "\tcs:SetReset(RESET_PHASE+PHASE_END)",
            "\tcs:SetLabel(c:GetSequence())",
            "\tDuel.RegisterEffect(cs,tp)",
            "\tlocal ct=cd:Clone()",
            "\tct:SetCode(EFFECT_DISABLE_TRAPMONSTER)",
            "\tct:SetTargetRange(LOCATION_MZONE,LOCATION_MZONE)",
            "\tDuel.RegisterEffect(ct,tp)",
            "\tDuel.Hint(HINT_ZONE,tp,0x1<<(c:GetSequence()+8))",
            "end",
        ], null, ctx.Link);
    }
}

// ---------------------------------------------------------------- Forbidden Crown

/// <summary>
/// "Apply these effects to 1 face-up monster on the field, until the end of this turn" with each effect a separate tick:
/// Forbidden Crown's freeze, or any subset of it.
/// </summary>
public sealed class ApplyEffectsAction : CardGroupAction
{
    public override string Id => "do.apply_effects";
    public override string Name => "Apply effects to a monster (freeze)";
    public override string Sentence => "Apply these effects to {count} {cards} {target} {duration}";
    public override string Help => "Like Forbidden Crown. Tick each effect it gets; each works on its own.";
    protected override string TargetedWording => "apply these effects to {0}";
    protected override string Category => "CATEGORY_DISABLE";
    protected override string Hint => "HINTMSG_FACEUP";
    protected override int MaxCount => 5;
    protected override bool AllowsAll => true;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    private static readonly (string Key, string Label, string Text)[] Effects =
    [
        ("negate", "negate its effects", "Negate its effects."),
        ("no_attack", "cannot attack", "Cannot attack."),
        ("indestructible", "cannot be destroyed by battle or card effects", "Cannot be destroyed by battle or card effects."),
        ("unaffected", "unaffected by activated card effects, except its own", "Unaffected by activated card effects, except its own."),
        ("no_tribute", "cannot be Tributed", "Cannot be Tributed."),
        ("no_material", "cannot be used as material for a Fusion, Synchro, Xyz, or Link Summon", "Cannot be used as material for a Fusion, Synchro, Xyz, or Link Summon."),
    ];

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        .. base.BuildParams(),
        ParamSpec.Choice("duration", "For", new("turn", "until the end of this turn"), new("always", "", "while it is face-up")),
        .. Effects.Select(x => ParamSpec.Flag(x.Key, x.Label, true)),
    ];

    private static string List(BlockArgs args) =>
        string.Join(" ", Effects.Where(x => args.Bool(x.Key)).Select(x => "● " + x.Text));

    public override string Describe(BlockArgs args)
    {
        var duration = args.Choice("duration") == "turn" ? ", until the end of this turn" : "";
        return $"apply these effects to {args.Describe("count")} {args.Describe("cards")}{duration}. {List(args)}".TrimEnd('.');
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) =>
        $"apply these effects to {pronoun}{(args.Choice("duration") == "turn" ? ", until the end of this turn" : "")}. {List(args)}".TrimEnd('.');

    protected override string Apply(string g, BlockContext ctx)
    {
        var reset = ctx.Args.Choice("duration") == "turn" ? "RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END" : "RESET_EVENT+RESETS_STANDARD";
        var lines = new List<string> { "local n=0", "for tc in aux.Next(g) do", "\tif not tc:IsImmuneToEffect(e) then", "\t\tn=n+1" };
        void Add(string code, string? value = null, string? props = null, bool range = false)
        {
            lines.Add("\t\tlocal fx=Effect.CreateEffect(e:GetHandler())");
            lines.Add("\t\tfx:SetType(EFFECT_TYPE_SINGLE)");
            if (props is not null || range) lines.Add($"\t\tfx:SetProperty({string.Join("+", new[] { range ? "EFFECT_FLAG_SINGLE_RANGE" : null, props }.Where(p => p is not null))})");
            if (range) lines.Add("\t\tfx:SetRange(LOCATION_MZONE)");
            lines.Add($"\t\tfx:SetCode({code})");
            if (value is not null) lines.Add($"\t\tfx:SetValue({value})");
            lines.Add($"\t\tfx:SetReset({reset})");
            lines.Add("\t\ttc:RegisterEffect(fx)");
        }
        if (ctx.Args.Bool("negate"))
        {
            lines.Add("\t\tDuel.NegateRelatedChain(tc,RESET_TURN_SET)");
            Add("EFFECT_DISABLE");
            Add("EFFECT_DISABLE_EFFECT", "RESET_TURN_SET");
        }
        if (ctx.Args.Bool("no_attack")) Add("EFFECT_CANNOT_ATTACK");
        if (ctx.Args.Bool("indestructible"))
        {
            Add("EFFECT_INDESTRUCTABLE_BATTLE", "1", "EFFECT_FLAG_CANNOT_DISABLE");
            Add("EFFECT_INDESTRUCTABLE_EFFECT", "1", "EFFECT_FLAG_CANNOT_DISABLE");
        }
        if (ctx.Args.Bool("unaffected"))
            Add("EFFECT_IMMUNE_EFFECT", ctx.Script.AddFunction("crownimmune", "e,te", ["return te:GetOwner()~=e:GetHandler() and te:IsActivated()"]), "EFFECT_FLAG_CANNOT_DISABLE", range: true);
        if (ctx.Args.Bool("no_tribute"))
        {
            Add("EFFECT_UNRELEASABLE_SUM", "1", range: true);
            Add("EFFECT_UNRELEASABLE_NONSUM", "1", range: true);
        }
        if (ctx.Args.Bool("no_material"))
        {
            Add("EFFECT_CANNOT_BE_FUSION_MATERIAL", ctx.Script.AddFunction("fuslimit", "e,c,sumtype", ["return sumtype==SUMMON_TYPE_FUSION"]), range: true);
            Add("EFFECT_CANNOT_BE_SYNCHRO_MATERIAL", "1", range: true);
            Add("EFFECT_CANNOT_BE_XYZ_MATERIAL", "1", range: true);
            Add("EFFECT_CANNOT_BE_LINK_MATERIAL", "1", range: true);
        }
        lines.AddRange(["\tend", "end", "return n"]);
        var fn = ctx.Script.AddFunction("applyfx", "g,e", lines);
        return $"{fn}({g},e)";
    }

    public override void Compile(BlockContext ctx)
    {
        if (!Effects.Any(x => ctx.Args.Bool(x.Key))) ctx.Effect.Warn("\"Apply these effects\" has no effect ticked.");
        base.Compile(ctx);
    }
}

/// <summary>"Neither player can activate monster effects in response to this card's activation": Forbidden Crown, Beargram.</summary>
public sealed class ChainLimitCost : CostBlock
{
    public override string Id => "cost.chain_limit";
    public override string Group => "Activation";
    public override string Name => "Players can't respond to this activation";
    public override string Sentence => "{who} can activate {what} in response to this activation";
    public override string Help => "Not a cost: it is set when the effect is activated. The card text puts it after the effect.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("both", "Neither player"), new("opponent", "Your opponent")),
        ParamSpec.Choice("what", "What", new("monster", "monster effects"), new("spelltrap", "Spell/Trap Cards"), new("any", "cards or effects")),
    ];

    public override string Describe(BlockArgs args) => "";

    public override string Afterword(BlockArgs args)
    {
        var who = args.Choice("who") == "both" ? "Neither player can" : "Your opponent cannot";
        var thing = args.Effect?.When?.BlockId == "when.activate" ? "card" : "effect";
        return $"{who} activate {args.Describe("what")} in response to this {thing}'s activation.";
    }

    public override void Compile(BlockContext ctx)
    {
        var test = ctx.Args.Choice("what") switch
        {
            "monster" => "not e:IsActiveType(TYPE_MONSTER)",
            "spelltrap" => "not e:IsHasType(EFFECT_TYPE_ACTIVATE)",
            _ => "false",
        };
        if (ctx.Args.Choice("who") == "opponent") test = $"tp==rp or {test}";
        var fn = ctx.Script.AddFunction("chainlm", "e,rp,tp", [$"return {test}"]);
        ctx.Effect.TargetLines.Add($"Duel.SetChainLimit({fn})");
    }
}

// ---------------------------------------------------------------- Mind Scan

/// <summary>"Your opponent must keep their hand revealed": Mind Scan, Mind on Air.</summary>
public sealed class RevealHandPassive : PassiveBlock
{
    public override string Id => "passive.reveal_hand";
    public override string Group => "Players";
    public override string Name => "A player keeps their hand revealed";
    public override string Sentence => "{who} must keep their hand revealed";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("opponent", "Your opponent"), new("both", "Both players")),
    ];

    public override string Describe(BlockArgs args) =>
        args.Choice("who") == "both" ? "both players must keep their hands revealed" : "your opponent must keep their hand revealed";

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        var self = ctx.Args.Choice("who") == "both" ? "LOCATION_HAND" : "0";
        ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetCode(EFFECT_PUBLIC)", $"{v}:SetRange({range})", $"{v}:SetTargetRange({self},LOCATION_HAND)"]);
    }
}

/// <summary>"Declare 1 card name; negate the activated effects of cards with that original name until the end of this turn": Mind Scan.</summary>
public sealed class DeclareNegateAction : ActionBlock
{
    public override string Id => "do.declare_negate";
    public override string Group => "Negate";
    public override string Name => "Declare a card name; negate its activated effects this turn";
    public override string Sentence => "Declare 1 card name; negate the activated effects of cards with that original name until the end of this turn";

    public override string Describe(BlockArgs args) => "negate the activated effects of cards with the declared original name until the end of this turn";

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        e.AddCategory("CATEGORY_DISABLE");
        e.TargetLines.Add("Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_CODE)");
        e.TargetLines.Add("local code=Duel.AnnounceCard(tp)");
        e.TargetLines.Add("Duel.SetTargetParam(code)");
        e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_ANNOUNCE,nil,0,tp,0)");
        var op = ctx.Script.AddFunction("declop", "e,tp,eg,ep,ev,re,r,rp", ["if re:GetHandler():IsOriginalCodeRule(e:GetLabel()) then Duel.NegateEffect(ev) end"]);
        e.AddStep(
        [
            "local code=Duel.GetChainInfo(0,CHAININFO_TARGET_PARAM)",
            "local dn=Effect.CreateEffect(e:GetHandler())",
            "dn:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "dn:SetCode(EVENT_CHAIN_SOLVING)",
            "dn:SetLabel(code)",
            $"dn:SetOperation({op})",
            "dn:SetReset(RESET_PHASE+PHASE_END)",
            "Duel.RegisterEffect(dn,tp)",
        ], null, ctx.Link);
    }

    public override (string Target, string Action)? DescribeTargeting(BlockArgs args) =>
        ("declare 1 card name", "negate the activated effects of cards with that original name until the end of this turn");
}
