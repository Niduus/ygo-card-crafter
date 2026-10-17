using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// Rare mechanics: winning the Duel, a set of named cards in your hand, banishing a monster until the card or effect
// resolving has resolved, a monster tied to the Continuous Spell that Summoned it, and an effect that becomes a Spell's.

/// <summary>"You win the Duel." (Number C88: Gimmick Puppet Disaster Leo, Vennominaga): a DO step.</summary>
public sealed class WinAction : ActionBlock
{
    public const string BlockId = "do.win";
    /// <summary>The win reason official "you win the Duel" cards without a reason of their own pass (Exodia's).</summary>
    public const string Reason = "0x10";
    public override string Id => BlockId;
    public override string Group => "Players";
    public override string Name => "You win the Duel";
    public override string Sentence => "{who} the Duel";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("you", "you win"), new("opponent", "your opponent wins")),
    ];

    public override void Compile(BlockContext ctx)
    {
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddStep([$"Duel.Win({PlayerOf(ctx.Args.Choice("who"))},{Reason})", $"local {r}=1"], r, ctx.Link);
    }
}

/// <summary>
/// "If you have "Right Leg of the Forbidden One", ... in addition to this card in your hand, you win the Duel." (Exodia
/// the Forbidden One): a lasting rule that wins as soon as its IF blocks hold, checked whenever the game state changes.
/// </summary>
public sealed class WinPassive : PassiveBlock
{
    public const string BlockId = "passive.win";
    public override string Id => BlockId;
    public override string Group => "Players";
    public override string Name => "You win the Duel (when its IF blocks hold)";
    public override string Sentence => "You win the Duel";
    public override string Help => "Add IF blocks for when, like \"You have these cards in your hand\". It isn't an effect that activates, so it can't be responded to.";

    public override string Describe(BlockArgs args) => "you win the Duel";

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Effect.Conditions.Count == 0) ctx.Effect.Warn("\"You win the Duel\" with no IF blocks wins as soon as this card is there.");
        var op = ctx.Script.AddFunction("winop", "e,tp,eg,ep,ev,re,r,rp", [$"Duel.Win(tp,{WinAction.Reason})"]);
        var range = RangeOf(ctx);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE)",
            $"{v}:SetCode(EVENT_ADJUST)", $"{v}:SetRange({range})", $"{v}:SetOperation({op})",
        ]);
    }
}

/// <summary>
/// "If you have "Right Leg of the Forbidden One", "Left Leg of the Forbidden One", "Right Arm of the Forbidden One" and
/// "Left Arm of the Forbidden One" in addition to this card in your hand": a card with each of these names is there.
/// </summary>
public sealed class HaveNamesCondition : ConditionBlock
{
    public const string BlockId = "if.have_names";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "You have a card with each of these names";
    public override string Sentence => "You have {cards} {self} in your {where}";
    public override string Help => "Pick the cards by name (a card and the other names it may have): one of each must be there, unlike \"There are cards\", where any one is enough.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        // Exodia the Forbidden One's four limbs, until other cards are picked.
        new ParamSpec
        {
            Key = "cards", Label = "Each of these cards", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { Zones = Zone.None, CardCode = 8124921, AltCodes = [44519536, 70903634, 7902349] },
        },
        ParamSpec.Flag("self", "in addition to this card", true),
        ParamSpec.Choice("where", "In your", new("hand", "hand"), new("field", "field", "field (face-up)"), new("grave", "GY")),
    ];

    private static List<long> Codes(BlockArgs args)
    {
        var f = args.Cards("cards");
        return f.CardCode == 0 ? [] : [f.CardCode, .. f.AltCodes];
    }

    public override string Describe(BlockArgs args)
    {
        var names = Codes(args).Select(c => CardNames.Find(c, args.Pack) is { } n ? $"\"{n}\"" : $"the card with passcode {c}").ToList();
        // As Exodia lists them: "A", "B", "C" and "D".
        var list = names.Count switch { 0 => "(pick the cards)", 1 => names[0], _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1] };
        var where = args.Choice("where") switch { "field" => "on your field", "grave" => "in your GY", _ => "in your hand" };
        return $"you have {list}{(args.Bool("self") ? " in addition to this card" : "")} {where}";
    }

    public override void Compile(BlockContext ctx)
    {
        var codes = Codes(ctx.Args);
        if (codes.Count == 0) { ctx.Effect.Error($"\"{Name}\": pick the cards by name."); return; }
        var (loc, test) = ctx.Args.Choice("where") switch
        {
            "field" => ("LOCATION_ONFIELD", "Card.IsFaceup"),
            "grave" => ("LOCATION_GRAVE", "aux.TRUE"),
            _ => ("LOCATION_HAND", "aux.TRUE"),
        };
        var has = ctx.Script.AddFunction("namefilter", "c,code,f", ["return c:IsCode(code) and f(c)"]);
        foreach (var code in codes) ctx.Effect.Conditions.Add($"Duel.IsExistingMatchingCard({has},tp,{loc},0,1,e:GetHandler(),{Lua.Num(code)},{test})");
        if (ctx.Args.Bool("self")) ctx.Effect.Conditions.Add($"e:GetHandler():IsLocation({loc})");
    }
}

/// <summary>
/// "Before resolving another activated card or effect, you can banish 1 Toon monster you control until immediately after
/// that card/effect resolves, also you cannot banish monsters with that same original name with this effect of "Perfect
/// Toon World" for the rest of this turn": each chain link, as it is about to resolve, may take one of your monsters out of
/// its way; it comes back as that link has resolved.
/// </summary>
public sealed class DodgePassive : PassiveBlock
{
    public const string BlockId = "passive.dodge";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Before a card or effect resolves, you can banish a monster until it has resolved";
    public override string Sentence => "Before resolving another activated card or effect, you can banish 1 {cards} you control until immediately after that card/effect resolves {lock}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new ParamSpec
        {
            Key = "cards", Label = "Which monster", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { Zones = Zone.None, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Toon },
        },
        ParamSpec.Flag("lock", "also you cannot banish monsters with that same original name with this effect for the rest of this turn", true),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards").Clone();
        f.Zones = Zone.None;
        var noun = FilterText.Describe(f, args.Pack, withLocation: false, ownName: args.CardName);
        var text = $"before resolving another activated card or effect, you can banish 1 {noun} you control until immediately after that card/effect resolves";
        if (!args.Bool("lock")) return text;
        var of = args.CardName is { Length: > 0 } n ? $" of \"{n}\"" : "";
        return text + $", also you cannot banish monsters with that same original name with this effect{of} for the rest of this turn";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (e.Index > 16) { e.Error($"\"{Name}\" works for a card's first 16 effects."); return; }
        var f = ctx.Args.Cards("cards").Clone();
        f.Owner = CardOwner.You;
        f.Zones = Zone.MonsterZone;
        f.FaceUpOnly = true;
        var lockName = ctx.Args.Bool("lock");
        // Names already banished this turn by this effect of this card's name, for each player.
        var dodged = ctx.Script.AddFunction("dodged", "tp,c", ["return s.cc_dodge~=nil and s.cc_dodge[tp..\":\"..c:GetOriginalCodeRule()]==Duel.GetTurnCount()"]);
        var filter = ctx.Script.Filter(f, "c:IsAbleToRemove()" + (lockName ? $" and not {dodged}(tp,c)" : ""), "dodgefilter");
        var flag = $"id+{8500 + ctx.Script.Slot("dodge")}";
        var retCon = ctx.Script.AddFunction("dodgeretcon", "e,tp,eg,ep,ev,re,r,rp", [$"return e:GetLabelObject():GetFlagEffect({flag})~=0"]);
        var retOp = ctx.Script.AddFunction("dodgeretop", "e,tp,eg,ep,ev,re,r,rp", ["Duel.ReturnToField(e:GetLabelObject())"]);
        var body = new List<string>
        {
            "local c=e:GetHandler()",
            "if re:GetHandler()==c then return end",
            $"if not Duel.IsExistingMatchingCard({filter.Function},tp,LOCATION_MZONE,0,1,nil,e,tp) then return end",
            $"if not Duel.SelectEffectYesNo(tp,c,aux.Stringid(id,{e.Index - 1})) then return end",
            "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_REMOVE)",
            $"local rc=Duel.SelectMatchingCard(tp,{filter.Function},tp,LOCATION_MZONE,0,1,1,nil,e,tp):GetFirst()",
            "if not rc then return end",
            "Duel.Hint(HINT_CARD,0,id)",
            "Duel.HintSelection(Group.FromCards(rc))",
            "if Duel.Remove(rc,0,REASON_EFFECT+REASON_TEMPORARY)==0 then return end",
            $"rc:RegisterFlagEffect({flag},RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,0,1)",
            // Back as soon as the chain link has resolved.
            "local e1=Effect.CreateEffect(c)",
            "e1:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "e1:SetCode(EVENT_CHAIN_SOLVED)",
            "e1:SetReset(RESET_PHASE+PHASE_END)",
            "e1:SetLabelObject(rc)",
            "e1:SetCountLimit(1)",
            $"e1:SetCondition({retCon})",
            $"e1:SetOperation({retOp})",
            "Duel.RegisterEffect(e1,tp)",
        };
        if (lockName) body.AddRange(["s.cc_dodge=s.cc_dodge or {}", "s.cc_dodge[tp..\":\"..rc:GetOriginalCodeRule()]=Duel.GetTurnCount()"]);
        var op = ctx.Script.AddFunction("dodgeop", "e,tp,eg,ep,ev,re,r,rp", body);
        var range = RangeOf(ctx);
        e.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetCode(EVENT_CHAIN_SOLVING)", $"{v}:SetRange({range})", $"{v}:SetOperation({op})"]);
    }
}

/// <summary>
/// Castle of Vampire: "When this card is activated: Special Summon 1 DARK Zombie monster from your Deck or GY. When that
/// monster leaves the field, destroy this card. That monster is unaffected by the activated effects of other non-DARK
/// monsters." The monster the step before Summoned is tied to this card (its card target, as Call of the Haunted's), and
/// keeps what this card gives it while both are on the field.
/// </summary>
public sealed class BindAction : ActionBlock
{
    public const string BlockId = "do.bind";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "Tie that monster to this card (when it leaves the field, destroy this card)";
    public override string Sentence => "{cards} is tied to this card: {leaves} {gets} {attribute}";
    public override string Help => "For Continuous Spells/Traps that Summon a monster, like Call of the Haunted. Put it after the step that Summons the monster.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monster", () => new CardFilter { That = CardRef.Affected, CardType = FilterCardType.Monster }).ThatCards(),
        ParamSpec.Flag("leaves", "when that monster leaves the field, destroy this card", true),
        ParamSpec.Choice("gets", "While tied, that monster",
            new("nothing", "", "gets nothing more"),
            new("unaffected_non_attr", "is unaffected by the activated effects of other non-", "is unaffected by the activated effects of other monsters not of an Attribute:"),
            new("unaffected_opp", "is unaffected by your opponent's card effects"),
            new("indestructible", "cannot be destroyed by card effects")),
        new ParamSpec { Key = "attribute", Label = "Attribute", Type = ParamType.Choice, Default = MonsterChoices.Attributes[0].Value, Choices = MonsterChoices.Attributes }
            .Unprinted().ShowIf(b => b.Values.GetValueOrDefault("gets") == "unaffected_non_attr"),
    ];

    private static string Attribute(BlockArgs args) => MonsterChoices.Attributes.First(a => a.Value == args.Choice("attribute")).Label;

    public override string Describe(BlockArgs args)
    {
        var parts = new List<string>();
        if (args.Bool("leaves")) parts.Add("when that monster leaves the field, destroy this card");
        switch (args.Choice("gets"))
        {
            case "unaffected_non_attr": parts.Add($"that monster is unaffected by the activated effects of other non-{Attribute(args)} monsters"); break;
            case "unaffected_opp": parts.Add("that monster is unaffected by your opponent's card effects"); break;
            case "indestructible": parts.Add("that monster cannot be destroyed by card effects"); break;
        }
        return parts.Count == 0 ? "tie that monster to this card" : string.Join(". ", parts.Select((p, i) => i == 0 ? p : SentenceCase.Upper(p)));
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var that = ctx.Args.Cards("cards").That;
        if (that == CardRef.None) { e.Error($"\"{Name}\": pick \"that card\" (the monster the step before Summoned, or this effect's target)."); return; }
        if (ctx.Card.IsMonster) e.Warn($"\"{Name}\" is for Spells and Traps that stay on the field.");
        if (CardRefs.GroupLua(ctx, that) is not { } group) return;
        var r = e.NewLocal("r");
        var tc = e.NewLocal("tc");
        var lines = new List<string>
        {
            $"local {r}=0",
            $"local {tc}={group}:GetFirst()",
            $"if {tc} and c:IsRelateToEffect(e) and {tc}:IsLocation(LOCATION_MZONE) then",
            $"\tc:SetCardTarget({tc})",
            $"\t{r}=1",
        };
        if (ctx.Args.Bool("leaves"))
        {
            var con = ctx.Script.AddFunction("bindleavecon", "e,tp,eg,ep,ev,re,r,rp", ["local tc=e:GetHandler():GetFirstCardTarget()", "return tc and eg:IsContains(tc)"]);
            var op = ctx.Script.AddFunction("bindleaveop", "e,tp,eg,ep,ev,re,r,rp", ["Duel.Destroy(e:GetHandler(),REASON_EFFECT)"]);
            lines.AddRange(
            [
                "\tlocal be=Effect.CreateEffect(c)",
                "\tbe:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
                "\tbe:SetCode(EVENT_LEAVE_FIELD)",
                "\tbe:SetRange(LOCATION_ONFIELD)",
                $"\tbe:SetCondition({con})",
                $"\tbe:SetOperation({op})",
                "\tbe:SetReset(RESET_EVENT+RESETS_STANDARD)",
                "\tc:RegisterEffect(be)",
            ]);
        }
        var gets = ctx.Args.Choice("gets");
        if (gets != "nothing")
        {
            // Only while this card is still there and still tied to it.
            var tied = ctx.Script.AddFunction("bindcon", "e", ["return e:GetOwner():IsHasCardTarget(e:GetHandler())"]);
            var (code, value) = gets switch
            {
                "unaffected_non_attr" => ("EFFECT_IMMUNE_EFFECT", ctx.Script.AddFunction("bindimmval", "e,te",
                    [$"return te:IsActivated() and te:IsActiveType(TYPE_MONSTER) and te:GetHandler()~=e:GetHandler() and not te:GetHandler():IsAttribute({EnumNames.LuaConstant(Enum.Parse<CardAttribute>(ctx.Args.Choice("attribute")))})"])),
                "unaffected_opp" => ("EFFECT_IMMUNE_EFFECT", ctx.Script.AddFunction("bindimmval", "e,te", ["return te:GetOwnerPlayer()~=e:GetHandlerPlayer()"])),
                _ => ("EFFECT_INDESTRUCTABLE_EFFECT", "1"),
            };
            lines.AddRange(
            [
                "\tlocal ge=Effect.CreateEffect(c)",
                "\tge:SetType(EFFECT_TYPE_SINGLE)",
                "\tge:SetProperty(EFFECT_FLAG_SINGLE_RANGE+EFFECT_FLAG_OWNER_RELATE)",
                "\tge:SetRange(LOCATION_MZONE)",
                $"\tge:SetCode({code})",
                $"\tge:SetValue({value})",
                $"\tge:SetCondition({tied})",
                "\tge:SetReset(RESET_EVENT+RESETS_STANDARD)",
                $"\t{tc}:RegisterEffect(ge)",
            ]);
        }
        lines.Add("end");
        e.AddStep(lines, r, ctx.Link);
    }
}

/// <summary>
/// Predaplant Verte Anaconda: "You can pay 2000 LP and send 1 "Fusion" or "Polymerization" Normal or Quick-Play Spell from
/// your Deck to the GY; this effect becomes that Spell's effect when that card is activated." The Spell sent for the cost
/// lends its activation's effect: its target is done as this effect is activated, its operation as it resolves.
/// </summary>
public sealed class CopyEffectAction : ActionBlock
{
    public const string BlockId = "do.copy_effect";
    public override string Id => BlockId;
    public override string Group => "Activation";
    public override string Name => "This effect becomes the effect of the card used for the cost";
    public override string Sentence => "This effect becomes that {what}'s effect when that card is activated";
    public override string Help => "Needs a COST block that sends or banishes a Spell/Trap, such as \"send 1 Spell from your Deck to the GY\". A Spell whose effect can't be applied now does nothing.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "That card is a", new("spell", "Spell"), new("trap", "Trap"), new("card", "card")),
    ];

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (CardRefs.GroupLua(ctx, CardRef.Cost) is not { } cost) return;
        if (e.HasSteps) e.Warn($"\"{Name}\" is usually the effect's only step.");
        e.TargetLines.AddRange(
        [
            // The Spell's activation, as if it were activated now: its target goes with this effect's activation.
            $"local cpc={cost}:GetFirst()",
            "local cte,ceg,cep,cev,cre,cr,crp",
            "if cpc then cte,ceg,cep,cev,cre,cr,crp=cpc:CheckActivateEffect(true,true,true) end",
            "if cte then",
            "\te:SetProperty(cte:GetProperty())",
            "\tlocal ctg=cte:GetTarget()",
            "\tif ctg then ctg(e,tp,ceg,cep,cev,cre,cr,crp,1) end",
            "\tcte:SetLabelObject(e:GetLabelObject())",
            "\te:SetLabelObject(cte)",
            "\tDuel.ClearOperationInfo(0)",
            "end",
        ]);
        var r = e.NewLocal("r");
        e.AddStep(
        [
            $"local {r}=0",
            "local cte=e:GetLabelObject()",
            "if cte and aux.GetValueType(cte)==\"Effect\" then",
            "\te:SetLabelObject(cte:GetLabelObject())",
            "\tlocal cop=cte:GetOperation()",
            "\tif cop then cop(e,tp,eg,ep,ev,re,r,rp) end",
            $"\t{r}=1",
            "end",
        ], r, ctx.Link);
    }
}

/// <summary>
/// "Destroy this card during your opponent's 3rd End Phase after activation" (Depth Amulet, Deck Lockdown, Time Chain,
/// Ancient Warriors Saga - Three Visits: "Send this card to the GY during your 2nd Standby Phase after activation"): part
/// of the card's activation, not a cost. The card counts that player's phases from its activation, as official cards do
/// with its turn counter.
/// </summary>
public sealed class DestroyLaterCost : CostBlock
{
    public const string BlockId = "cost.destroy_later";
    public override string Id => BlockId;
    public override string Group => "Activation";
    public override string Name => "Destroy this card during the Nth Standby / End Phase after activation";
    public override string Sentence => "{how} this card during {whose} {count} {phase} after activation, written {said}";
    public override string Help => "Not a cost: it starts counting when this card is activated. The card text says it after the effect, or first (Time Chain).";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "What", new("destroy", "Destroy"), new("grave", "Send", "Send to the GY")),
        ParamSpec.Choice("whose", "Whose", new("your", "your"), new("opponent", "your opponent's")),
        ParamSpec.Int("count", "Which one", 2, 1, 9),
        ParamSpec.Choice("phase", "Phase", new("standby", "Standby Phase"), new("end", "End Phase")),
        // Time Chain, Swords of Concealing Light: the card text starts with it. Saved before it was offered: after the effect.
        ParamSpec.Choice("said", "The card text says it", new("after", "", "after the effect"), new("first", "", "first, before the effect")),
    ];

    private static string Ordinal(int n) => n switch
    {
        1 => "1st", 2 => "2nd", 3 => "3rd",
        _ => n.ToString(CultureInfo.InvariantCulture) + "th",
    };

    public override string Describe(BlockArgs args) => "";

    public override string Afterword(BlockArgs args) => args.Choice("said") == "first" ? "" : Said(args);

    public override string Foreword(BlockArgs args) => args.Choice("said") == "first" ? Said(args) : "";

    private static string Said(BlockArgs args)
    {
        var when = $"during {args.Describe("whose")} {Ordinal(args.Int("count"))} {args.Describe("phase")} after activation";
        return args.Choice("how") == "grave" ? $"Send this card to the GY {when}." : $"Destroy this card {when}.";
    }

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Card.IsMonster) ctx.Effect.Warn($"\"{Name}\" is for Spells and Traps that stay on the field after they are activated.");
        var n = ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture);
        var phase = ctx.Args.Choice("phase") == "end" ? "PHASE_END" : "PHASE_STANDBY";
        var opponent = ctx.Args.Choice("whose") == "opponent";
        var con = ctx.Script.AddFunction("latercon", "e,tp,eg,ep,ev,re,r,rp", [$"return Duel.GetTurnPlayer(){(opponent ? "~=" : "==")}tp"]);
        var op = ctx.Script.AddFunction("laterop", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local c=e:GetHandler()",
            "local ct=c:GetTurnCounter()+1",
            "c:SetTurnCounter(ct)",
            $"if ct=={n} then {(ctx.Args.Choice("how") == "grave" ? "Duel.SendtoGrave(c,REASON_RULE)" : "Duel.Destroy(c,REASON_RULE)")} end",
        ]);
        var range = ctx.Card.IsMonster ? "LOCATION_MZONE" : ctx.Card.Kind == CardKind.Spell && ctx.Card.SpellKind == SpellKind.Field ? "LOCATION_FZONE" : "LOCATION_SZONE";
        ctx.Effect.CostLines.AddRange(
        [
            "e:GetHandler():SetTurnCounter(0)",
            "local le=Effect.CreateEffect(e:GetHandler())",
            "le:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "le:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
            $"le:SetCode(EVENT_PHASE+{phase})",
            "le:SetCountLimit(1)",
            $"le:SetRange({range})",
            $"le:SetCondition({con})",
            $"le:SetOperation({op})",
            $"le:SetReset(RESET_EVENT+RESETS_STANDARD+RESET_PHASE+{phase}+{(opponent ? "RESET_OPPO_TURN" : "RESET_SELF_TURN")},{n})",
            "e:GetHandler():RegisterEffect(le)",
        ]);
    }
}
