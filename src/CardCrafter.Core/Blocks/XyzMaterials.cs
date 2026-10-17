using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "If this card is sent from the field to the GY: You can target 1 Xyz Monster you control; attach this card to it as
/// material." (Infinitrack Goliath): this card goes under an Xyz Monster, from wherever it is.
/// </summary>
public sealed class AttachSelfAction : CardGroupAction
{
    public const string BlockId = "do.attach_self";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Attach this card to an Xyz Monster as material";
    public override string Sentence => "Attach this card to {count} {cards} {target} as material";
    public override string Help => "This card goes under the Xyz Monster as material, from the field, the GY or wherever the effect is used.";
    protected override string Category => "";
    protected override string Hint => "HINTMSG_FACEUP";
    protected override string TargetedWording => "attach this card to {0} as material";
    protected override int MaxCount => 1;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz };
    protected override string? AbleCheck(BlockContext ctx) => "c:IsFaceup() and c:IsType(TYPE_XYZ)";

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Int("count", "How many", 1, 1, 1),
        ParamSpec.Cards("cards", "Which Xyz Monster", DefaultCards, Zone.MonsterZone),
        ParamSpec.Flag("target", "(targeting)", false),
    ];

    public override string Describe(BlockArgs args) =>
        $"attach this card to {args.Describe("count")} {args.Describe("cards")} as material";

    protected override string Apply(string g, BlockContext ctx)
    {
        var fn = ctx.Script.AddFunction("attachself", "g,e",
        [
            "local c=e:GetHandler()",
            "local tc=g:GetFirst()",
            "if not tc or not c:IsRelateToEffect(e) or not c:IsCanOverlay() or tc:IsFacedown() or tc:IsImmuneToEffect(e) then return 0 end",
            "Duel.Overlay(tc,Group.FromCards(c))",
            "return 1",
        ]);
        return $"{fn}({g},e)";
    }

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Args.Cards("cards").ThisCard) { ctx.Effect.Error($"\"{Name}\": pick the Xyz Monster this card goes under, not this card."); return; }
        base.Compile(ctx);
        ctx.Effect.TargetChecks.Add("e:GetHandler():IsCanOverlay()");
        // Leaving the GY (or the field) to become material.
        ctx.Effect.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_LEAVE_GRAVE,e:GetHandler(),1,0,0)");
    }
}

/// <summary>
/// "Detach 1 material from this card." as a step of an effect, not its cost; with "or destroy this card", as Gustav
/// Rocket's End Phase effect asks which (destroyed when it has too few materials).
/// </summary>
public sealed class DetachAction : ActionBlock
{
    public const string BlockId = "do.detach";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "Detach materials from this card (not as a cost)";
    public override string Sentence => "Detach {count} material(s) from this card {otherwise}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ParamSpec.Choice("otherwise", "Or", new("none", "", "(nothing else)"), new("destroy", "or destroy this card")),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var text = n == 1 ? "detach 1 material from this card" : $"detach {n} materials from this card";
        return args.Choice("otherwise") == "destroy" ? text + " or destroy this card" : text;
    }

    public override void Compile(BlockContext ctx)
    {
        if (!ctx.Card.IsXyz) ctx.Effect.Warn("Only Xyz monsters have materials to detach.");
        var n = ctx.Args.Int("count");
        var r = ctx.Effect.NewLocal("r");
        if (ctx.Args.Choice("otherwise") == "destroy")
        {
            ctx.Effect.AddCategory("CATEGORY_DESTROY");
            // The player picks while there are enough materials; otherwise this card is destroyed.
            ctx.Effect.AddStep(
            [
                $"local {r}=0",
                "if c:IsRelateToEffect(e) and c:IsFaceup() then",
                $"\tif c:CheckRemoveOverlayCard(tp,{n},REASON_EFFECT) and Duel.SelectYesNo(tp,aux.Stringid(id,{Math.Min(ctx.Effect.Index, 16) - 1})) then",
                $"\t\t{r}=c:RemoveOverlayCard(tp,{n},{n},REASON_EFFECT)",
                "\telse",
                $"\t\t{r}=Duel.Destroy(c,REASON_EFFECT)",
                "\tend",
                "end",
            ], r, ctx.Link);
            return;
        }
        ctx.Effect.AddStep(
        [
            $"local {r}=0",
            $"if c:IsRelateToEffect(e) and c:CheckRemoveOverlayCard(tp,{n},REASON_EFFECT) then {r}=c:RemoveOverlayCard(tp,{n},{n},REASON_EFFECT) end",
        ], r, ctx.Link);
    }
}

/// <summary>"If this card has a Dragon monster as material" (Voloferniges), or "does not have".</summary>
public sealed class HasMaterialCondition : ConditionBlock
{
    public const string BlockId = "if.has_material";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card has a kind of card as material";
    public override string Sentence => "This card {has} {cards} as material";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("has", "Has", new("has", "has"), new("not", "does not have")),
        new ParamSpec { Key = "cards", Label = "Which material", Type = ParamType.Cards, DefaultFilter = () => new CardFilter { CardType = FilterCardType.Monster }, WhereFromBlock = true },
    ];

    public override string Describe(BlockArgs args)
    {
        var noun = FilterText.Describe(args.Cards("cards"), args.Pack, withLocation: false);
        return $"this card {args.Describe("has")} {SummonWording.Article(noun)} {noun} as material";
    }

    public override void Compile(BlockContext ctx)
    {
        if (!ctx.Card.IsXyz) ctx.Effect.Warn("Only Xyz monsters have materials.");
        var parts = FilterCompiler.Conditions(ctx.Args.Cards("cards"), "c", SummonProcedureKind.None, hasEffect: false);
        var f = parts.Count == 0 ? "aux.TRUE" : ctx.Script.AddFunction("matfilter", "c", [$"return {string.Join(" and ", parts)}"]);
        var test = $"e:GetHandler():GetOverlayGroup():IsExists({f},1,nil)";
        ctx.Effect.Conditions.Add(ctx.Args.Choice("has") == "not" ? "not " + test : test);
    }
}
