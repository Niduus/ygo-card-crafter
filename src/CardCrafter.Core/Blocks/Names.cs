using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "Special Summon this card, and if you do, its name becomes "Buster Blader"", "choose 1 non-Fusion Monster in your GY
/// whose name is mentioned on the revealed monster, and this card's name becomes that monster's (until the End Phase)":
/// this card takes another card's name as the effect resolves.
/// </summary>
public sealed class NameBecomesAction : ActionBlock
{
    public const string BlockId = "do.name_becomes";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card's name becomes another card's name (for a while)";
    public override string Sentence => "This card's name becomes {how} {cards} {duration}";
    public override string Help => "\"A card name\" takes the name picked in the card picker (Buster Blader, the One Who Surpasses the Limits). " +
                                   "\"The name of 1 card\" chooses the card as the effect resolves, or takes \"that card\" (its target, the card the step before " +
                                   "affected...): \"choose 1 ... in your GY, and this card's name becomes that monster's\" (Necromancer of Dragon Magic).";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "Becomes", new("named", "", "a card name (picked below)"), new("chosen", "", "the name of 1 card (picked below, or that card)")),
        // "Buster Blader" by default.
        ParamSpec.Cards("cards", "Which card", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, CardCode = 78193831 }).ThatCards(),
        ParamSpec.Choice("duration", "Until", new("end_phase", "until the End Phase"), new("always", "", "while face-up")),
    ];

    /// <summary>"its" right after the step that Summons this card ("Special Summon this card, and if you do, its name becomes ...").</summary>
    private static string Whose(BlockArgs args)
    {
        var dos = args.Effect?.Do;
        var at = dos?.FindIndex(b => ReferenceEquals(b, args.Instance)) ?? -1;
        return at > 0 && dos![at - 1].BlockId == "do.special_summon_self" ? "its" : "this card's";
    }

    private static string Named(BlockArgs args) =>
        args.Cards("cards").CardCode is var code and not 0 ? CardNames.Find(code, args.Pack) is { } n ? $"\"{n}\"" : $"the name of passcode {code}" : "another card's name";

    public override string Describe(BlockArgs args)
    {
        var whose = Whose(args);
        var f = args.Cards("cards");
        var end = args.Choice("duration") == "end_phase";
        if (args.Choice("how") == "named" && f.That == CardRef.None) return $"{whose} name becomes {Named(args)}{(end ? " until the End Phase" : "")}";
        // "this card's name becomes the targeted monster's name".
        if (f.That != CardRef.None) return $"{whose} name becomes {CardRefs.Whose(f.That, args.Effect)} name{(end ? " until the End Phase" : "")}";
        // Necromancer of Dragon Magic: "choose 1 ..., and this card's name becomes that monster's (until the End Phase)".
        var cards = FilterText.Describe(f, args.Pack, plural: false, preposition: "in", ownName: args.CardName);
        return $"choose 1 {cards}, and {whose} name becomes that {(f.CardType == FilterCardType.Monster ? "monster" : "card")}'s{(end ? " (until the End Phase)" : "")}";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var a = ctx.Args;
        var f = a.Cards("cards");
        var reset = a.Choice("duration") == "end_phase" ? Resets.EndOfTurn : Resets.WhileFaceUp;
        var (r, tc, code) = (e.NewLocal("r"), e.NewLocal("tc"), e.NewLocal("code"));
        var lines = new List<string> { $"local {r}=0" };
        if (a.Choice("how") == "named" && f.That == CardRef.None)
        {
            if (f.CardCode == 0) { e.Error($"\"{Name}\": pick the card whose name it becomes (its name or passcode in the card picker)."); return; }
            lines.Add($"local {code}={Lua.Num(f.CardCode)}");
        }
        else if (f.That != CardRef.None)
        {
            var cards = f.Clone();
            cards.That = CardRef.None;
            cards.Zones = Zone.None;
            cards.Owner = CardOwner.Either;
            var fn = ctx.Script.Filter(cards).Function;
            if (CardRefs.GroupLua(ctx, f.That) is not { } group) return;
            lines.Add($"local {tc}={group}:Filter({fn},nil,e,tp):GetFirst()");
            lines.Add($"local {code}={tc} and {tc}:GetCode()");
        }
        else
        {
            if (f.Zones == Zone.None) { e.Error($"\"{Name}\": pick where the card whose name it takes is."); return; }
            var fn = ctx.Script.Filter(f);
            e.TargetChecks.Add($"Duel.IsExistingMatchingCard({fn.Function},tp,{fn.Self},{fn.Opp},1,{fn.Exclude},e,tp)");
            lines.Add("Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_CONFIRM)");
            lines.Add($"local {tc}=Duel.SelectMatchingCard(tp,{fn.ForSelection},tp,{fn.Self},{fn.Opp},1,1,{fn.Exclude},e,tp):GetFirst()");
            lines.Add($"if {tc} then Duel.HintSelection(Group.FromCards({tc})) end");
            lines.Add($"local {code}={tc} and {tc}:GetCode()");
        }
        // This card takes the name while it stays face-up on the field (as Buster Blader, the One Who Surpasses the Limits does it).
        lines.AddRange(
        [
            $"if {code} and c:IsFaceup() and c:IsOnField() then",
            "\tlocal ne=Effect.CreateEffect(c)",
            "\tne:SetType(EFFECT_TYPE_SINGLE)",
            "\tne:SetCode(EFFECT_CHANGE_CODE)",
            $"\tne:SetValue({code})",
            $"\tne:SetReset({reset})",
            "\tc:RegisterEffect(ne)",
            $"\t{r}=1",
            "end",
        ]);
        e.AddStep(lines, r, ctx.Link);
    }
}

/// <summary>
/// Called by the Grave: "banish it, and if you do, until the end of the next turn, its effects are negated, as well as the
/// activated effects and effects on the field of monsters with the same original name".
/// </summary>
public sealed class NegateSameNameAction : ActionBlock
{
    public const string BlockId = "do.negate_same_name";
    public override string Id => BlockId;
    public override string Group => "Negate";
    public override string Name => "Negate that card's effects, and those of monsters with its original name";
    public override string Sentence => "{duration}, the effects of {cards} are negated, as well as the activated effects and effects on the field of monsters with the same original name";
    public override string Help => "Like Called by the Grave: put it after the step that banishes the monster, linked with \"and if you do\". Monsters on the field " +
                                   "with that original name have their effects negated, and their activated effects are negated as they resolve.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("duration", "Until", new("next", "until the end of the next turn"), new("turn", "until the end of this turn")),
        ParamSpec.Cards("cards", "Which monster", () => new CardFilter { That = CardRef.Affected, CardType = FilterCardType.Monster }).ThatCards(),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        var whose = f.That != CardRef.None ? CardRefs.Plural(args, f.That) ? "their" : "its" : $"the effects of {FilterText.Describe(f, args.Pack)}";
        var negated = f.That != CardRef.None ? $"{whose} effects are negated" : $"{whose} are negated";
        return $"{args.Describe("duration")}, {negated}, as well as the activated effects and effects on the field of monsters with the same original name";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var f = ctx.Args.Cards("cards");
        if (f.That == CardRef.None)
        {
            e.Error($"\"{Name}\" is about \"that card\": pick the card the step before affected, or this effect's target, in its card picker.");
            return;
        }
        var cards = f.Clone();
        cards.That = CardRef.None;
        cards.Zones = Zone.None;
        cards.Owner = CardOwner.Either;
        var fn = ctx.Script.Filter(cards).Function;
        if (CardRefs.GroupLua(ctx, f.That) is not { } group) return;
        var reset = ctx.Args.Choice("duration") == "next" ? "RESET_PHASE+PHASE_END,2" : "RESET_PHASE+PHASE_END";
        // As Called by the Grave's script: monsters on the field with its original name, and activated monster effects with it.
        var distg = ctx.Script.AddFunction("samenametg", "e,c",
            ["local tc=e:GetLabelObject()", "return c:IsOriginalCodeRule(tc:GetOriginalCodeRule()) and (c:IsType(TYPE_EFFECT) or c:GetOriginalType()&TYPE_EFFECT~=0)"]);
        var discon = ctx.Script.AddFunction("samenamecon", "e,tp,eg,ep,ev,re,r,rp",
            ["local tc=e:GetLabelObject()", "return re:IsActiveType(TYPE_MONSTER) and re:GetHandler():IsOriginalCodeRule(tc:GetOriginalCodeRule())"]);
        var disop = ctx.Script.AddFunction("samenameop", "e,tp,eg,ep,ev,re,r,rp", ["Duel.NegateEffect(ev)"]);
        var (g, r) = (e.NewLocal("g"), e.NewLocal("r"));
        e.AddCategory("CATEGORY_DISABLE");
        e.AddStep(
        [
            $"local {g}={group}:Filter({fn},nil,e,tp)",
            $"local {r}=0",
            $"for tc in aux.Next({g}) do",
            "\tlocal e1=Effect.CreateEffect(c)",
            "\te1:SetType(EFFECT_TYPE_FIELD)",
            "\te1:SetCode(EFFECT_DISABLE)",
            "\te1:SetTargetRange(LOCATION_MZONE,LOCATION_MZONE)",
            $"\te1:SetTarget({distg})",
            "\te1:SetLabelObject(tc)",
            $"\te1:SetReset({reset})",
            "\tDuel.RegisterEffect(e1,tp)",
            "\tlocal e2=Effect.CreateEffect(c)",
            "\te2:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "\te2:SetCode(EVENT_CHAIN_SOLVING)",
            $"\te2:SetCondition({discon})",
            $"\te2:SetOperation({disop})",
            "\te2:SetLabelObject(tc)",
            $"\te2:SetReset({reset})",
            "\tDuel.RegisterEffect(e2,tp)",
            $"\t{r}={r}+1",
            "end",
        ], r, ctx.Link);
    }
}

/// <summary>
/// Salamangreat Sanctuary and Salamangreat Shrine: "If you Link Summon a "Salamangreat" Link Monster, you can use 1
/// "Salamangreat" Link Monster you control with its same name as the entire material": a Link Summon procedure this card
/// gives the Link Monsters in your Extra Deck.
/// </summary>
public sealed class SameNameLinkPassive : PassiveBlock
{
    public const string BlockId = "passive.same_name_link";
    public override string Id => BlockId;
    public override string Group => "Summon";
    public override string Name => "Link Summon using 1 Link Monster with its same name as the entire material";
    public override string Sentence => "If you Link Summon {cards}, you can use 1 {mats} you control with its same name as the entire material {once}";
    public override string Help => "Like Salamangreat Sanctuary: the Link Monsters picked first can be Link Summoned using 1 face-up Link Monster you control with " +
                                   "the same name (\"reincarnation\"). Put it under \"While this card is face-up\" in the zone this card works from.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new ParamSpec
        {
            Key = "cards", Label = "Which Link Monster is Summoned", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Link },
        },
        new ParamSpec
        {
            Key = "mats", Label = "The material", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Link },
        },
        ParamSpec.Flag("once", "(once per turn)", true).Unprinted(),
    ];

    private static CardFilter LinkOf(BlockArgs args, string key, Zone zone)
    {
        var f = args.Cards(key).Clone();
        f.Owner = CardOwner.You;
        f.Zones = zone;
        f.CardType = FilterCardType.Monster;
        f.MonsterKind = MonsterKind.Link;
        return f;
    }

    public override string Describe(BlockArgs args)
    {
        var summoned = FilterText.Describe(LinkOf(args, "cards", Zone.ExtraDeck), args.Pack, withLocation: false);
        var mat = FilterText.Describe(LinkOf(args, "mats", Zone.MonsterZone), args.Pack, withLocation: false);
        return $"if you Link Summon {SummonWording.Article(summoned)} {summoned}, you can use 1 {mat} you control with its same name as the entire material";
    }

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        var target = ctx.Script.Filter(LinkOf(ctx.Args, "cards", Zone.ExtraDeck), null, "relinktg").Function;
        var mat = FilterCompiler.Conditions(LinkOf(ctx.Args, "mats", Zone.MonsterZone), "c", SummonProcedureKind.Link, hasEffect: false);
        var flag = $"id+{9100 + ctx.Effect.Index}";
        var once = ctx.Args.Bool("once");
        // As Salamangreat Sanctuary's script: a face-up Link Monster with the Summoned monster's name, sent to the GY as its only material.
        var mf = ctx.Script.AddFunction("relinkmat", "c,lc,tp,og,lmat",
        [
            $"return {string.Join(" and ", ["c:IsFaceup()", "c:IsCanBeLinkMaterial(lc)", "c:IsLinkCode(lc:GetCode())", .. mat])}",
            "\tand Duel.GetLocationCountFromEx(tp,tp,c,lc)>0 and aux.MustMaterialCheck(c,tp,EFFECT_MUST_BE_LMATERIAL)",
            "\tand (not og or og:IsContains(c)) and (not lmat or lmat==c)",
        ]);
        var con = ctx.Script.AddFunction("relinkcon", "e,c,og,lmat,min,max",
        [
            "if c==nil then return true end",
            "local tp=c:GetControler()",
            $"return {(once ? $"Duel.GetFlagEffect(tp,{flag})==0 and " : "")}Duel.IsExistingMatchingCard({mf},tp,LOCATION_MZONE,0,1,nil,c,tp,og,lmat)",
        ]);
        var op = ctx.Script.AddFunction("relinkop", "e,tp,eg,ep,ev,re,r,rp,c,og,lmat,min,max",
        [
            "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_LMATERIAL)",
            $"local mg=Duel.SelectMatchingCard(tp,{mf},tp,LOCATION_MZONE,0,1,1,nil,c,tp,og,lmat)",
            "c:SetMaterial(mg)",
            "Duel.SendtoGrave(mg,REASON_MATERIAL+REASON_LINK)",
            .. once ? new[] { $"Duel.RegisterFlagEffect(tp,{flag},RESET_PHASE+PHASE_END,0,1)" } : [],
        ]);
        var grant = ctx.Script.AddFunction("relinkgrant", "e,c", [$"return {target}(c,e,e:GetHandlerPlayer())"]);
        ctx.Effect.AddPassive(v =>
        [
            $"local {v}p=Effect.CreateEffect(c)",
            $"{v}p:SetDescription(aux.Stringid(id,{Math.Min(ctx.Effect.Index, 16) - 1}))",
            $"{v}p:SetType(EFFECT_TYPE_FIELD)",
            $"{v}p:SetProperty(EFFECT_FLAG_UNCOPYABLE+EFFECT_FLAG_IGNORE_IMMUNE)",
            $"{v}p:SetCode(EFFECT_SPSUMMON_PROC)",
            $"{v}p:SetRange(LOCATION_EXTRA)",
            $"{v}p:SetCondition({con})",
            $"{v}p:SetOperation({op})",
            $"{v}p:SetValue(SUMMON_TYPE_LINK)",
            $"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_GRANT)",
            $"{v}:SetRange({range})",
            $"{v}:SetTargetRange(LOCATION_EXTRA,0)",
            $"{v}:SetTarget({grant})",
            $"{v}:SetLabelObject({v}p)",
        ]);
    }
}

/// <summary>The Secret Crane Princess Who Returns A Favor: "You can banish this revealed card from your hand": this card is revealed in the hand.</summary>
public sealed class RevealedCondition : ConditionBlock
{
    public const string BlockId = "if.revealed";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card is revealed in your hand";
    public override string Sentence => "This card is revealed in your hand";
    public override string Help => "While your hand is kept revealed, or this card was revealed and stays so. Put it with a WHEN \"in the hand\".";

    public override string Describe(BlockArgs args) => "this card is revealed in your hand";

    public override void Compile(BlockContext ctx) => ctx.Effect.Conditions.Add("e:GetHandler():IsLocation(LOCATION_HAND) and e:GetHandler():IsPublic()");
}

/// <summary>
/// Sacred Spirit Sword Aiwass: "Look at 3 random face-down cards in your opponent's Extra Deck, and if you do, banish 1 of
/// them": the cards are picked at random, shown to you, and you take some of them.
/// </summary>
public sealed class LookRandomAction : ActionBlock
{
    public const string BlockId = "do.look_random";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "Look at random face-down cards in your opponent's Extra Deck, and banish some";
    public override string Sentence => "Look at {count} random face-down cards in your opponent's Extra Deck, and if you do, {then} {take} of them";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many to look at", 3, 1, 10),
        ParamSpec.Choice("then", "Then", new("banish", "banish"), new("grave", "send to the GY:")),
        ParamSpec.Int("take", "How many of them", 1, 1, 10),
    ];

    public override string Describe(BlockArgs args)
    {
        var then = args.Choice("then") == "grave" ? $"send {args.Int("take")} of them to the GY" : $"banish {args.Int("take")} of them";
        return $"look at {args.Int("count")} random face-down cards in your opponent's Extra Deck, and if you do, {then}";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var (n, take) = (Lua.Num(ctx.Args.Int("count")), Lua.Num(Math.Min(ctx.Args.Int("take"), ctx.Args.Int("count"))));
        var grave = ctx.Args.Choice("then") == "grave";
        var able = grave ? "Card.IsAbleToGrave" : "Card.IsAbleToRemove";
        e.AddCategory(grave ? "CATEGORY_TOGRAVE" : "CATEGORY_REMOVE");
        e.TargetChecks.Add($"Duel.GetMatchingGroupCount(Card.IsFacedown,tp,0,LOCATION_EXTRA,nil)>={n}");
        e.TargetLines.Add($"Duel.SetOperationInfo(0,{(grave ? "CATEGORY_TOGRAVE" : "CATEGORY_REMOVE")},nil,{take},1-tp,LOCATION_EXTRA)");
        var (g, cg, sg, r) = (e.NewLocal("g"), e.NewLocal("cg"), e.NewLocal("sg"), e.NewLocal("r"));
        // As Sacred Spirit Sword Aiwass's script: the opponent's random cards, shown to you, then yours to pick from.
        e.AddStep(
        [
            $"local {r}=0",
            $"local {g}=Duel.GetMatchingGroup(Card.IsFacedown,tp,0,LOCATION_EXTRA,nil)",
            $"if {g}:GetCount()>={n} then",
            $"\tlocal {cg}={g}:RandomSelect(1-tp,{n})",
            $"\tDuel.ConfirmCards(tp,{cg},{cg}:IsExists({able},1,nil))",
            $"\tDuel.Hint(HINT_SELECTMSG,tp,{(grave ? "HINTMSG_TOGRAVE" : "HINTMSG_REMOVE")})",
            $"\tlocal {sg}={cg}:FilterSelect(tp,{able},{take},{take},nil)",
            $"\tif {sg}:GetCount()>0 then {r}={(grave ? $"Duel.SendtoGrave({sg},REASON_EFFECT)" : $"Duel.Remove({sg},POS_FACEUP,REASON_EFFECT)")} end",
            "\tDuel.ShuffleExtra(1-tp)",
            "end",
        ], r, ctx.Link);
    }
}
