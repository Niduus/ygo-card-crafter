using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// Lasting effects, and the turn-long rules costs put on this card, added from the scan of the official scripts.

/// <summary>
/// "This card cannot attack the turn you activate this effect" (Fairy Archer, Six Samurai): put on this card when the
/// effect is activated, so it holds even if the effect is negated, and the effect can't be used once this card attacked.
/// </summary>
public sealed class ThisCardTurnCost : CostBlock
{
    public const string BlockId = "cost.this_card_turn";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card cannot attack the turn you activate this";
    public override string Sentence => "This card {rule} the turn you activate this effect";
    public override string Help => "The effect can only be activated if this card hasn't done it yet this turn. The card text puts it after the effect.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("rule", "Cannot",
            new("attack", "cannot attack"),
            new("declare", "cannot declare an attack"),
            new("direct", "cannot attack directly")),
    ];

    public override string Describe(BlockArgs args) => "";

    public override string Afterword(BlockArgs args) => $"This card {args.Describe("rule")} the turn you activate this effect.";

    /// <summary>The effect on this card, and what this card must not have done yet this turn.</summary>
    public static (string Code, string Check) Rule(string rule) => rule switch
    {
        "declare" => ("EFFECT_CANNOT_ATTACK_ANNOUNCE", "e:GetHandler():GetAttackAnnouncedCount()==0"),
        "direct" => ("EFFECT_CANNOT_DIRECT_ATTACK", "not e:GetHandler():IsDirectAttacked()"),
        _ => ("EFFECT_CANNOT_ATTACK", "e:GetHandler():GetAttackAnnouncedCount()==0"),
    };

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var (code, check) = Rule(ctx.Args.Choice("rule"));
        ctx.Effect.CostChecks.Add(check);
        ctx.Effect.CostLines.AddRange(
        [
            "local na=Effect.CreateEffect(e:GetHandler())",
            "na:SetType(EFFECT_TYPE_SINGLE)",
            $"na:SetCode({code})",
            "na:SetProperty(EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_OATH)",
            "na:SetReset(RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END)",
            "e:GetHandler():RegisterEffect(na)",
        ]);
    }
}

/// <summary>"This card can be treated as 2 Tributes for the Tribute Summon of a Dragon monster" (Kaiser Sea Snake's kin).</summary>
public sealed class DoubleTributePassive : PassiveBlock
{
    public const string BlockId = "passive.double_tribute";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card counts as 2 Tributes";
    public override string Sentence => "This card can be treated as 2 Tributes for the Tribute Summon of {cards}";
    public override string Help => "Pick the monsters it can be 2 Tributes for; plain monsters for any.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "For the Tribute Summon of", () => new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Dragon }, Zone.Hand),
    ];

    private static bool Any(CardFilter f) => FilterCompiler.Conditions(f).All(c => c == "c:IsType(TYPE_MONSTER)");

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        if (Any(f)) return "this card can be treated as 2 Tributes for a Tribute Summon";
        return $"this card can be treated as 2 Tributes for the Tribute Summon of {Article(FilterText.Describe(f, args.Pack, withLocation: false, ownName: args.CardName))}";
    }

    /// <summary>"a Dragon monster", "an EARTH monster", "a "Performapal" monster", but just ""Dark Magician"" for a card's name.</summary>
    public static string Article(string noun) =>
        noun.Length == 0 || noun.StartsWith('"') && noun.EndsWith('"') ? noun : ("AEIOUaeiou".Contains(noun[0]) ? "an " : "a ") + noun;

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var f = ctx.Args.Cards("cards").Clone();
        f.Zones = Zone.None;
        f.Owner = CardOwner.Either;
        var tests = FilterCompiler.Conditions(f).Where(c => c != "c:IsType(TYPE_MONSTER)").ToList();
        // A face-down Tribute counts only for its controller's own Summons, as in the official scripts.
        tests.Add("(ec:IsFaceup() or c:GetControler()==ec:GetControler())");
        var value = ctx.Script.AddFunction("dtcon", "e,c", ["local ec=e:GetHandler()", $"return {string.Join(" and ", tests)}"]);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_SINGLE)",
            $"{v}:SetCode(EFFECT_DOUBLE_TRIBUTE)",
            $"{v}:SetValue({value})",
        ]);
    }
}

/// <summary>"If you control an "Artmage" monster, you can activate this card the turn it was Set": a Trap's rule, with IF blocks for when.</summary>
public sealed class ActivateSetTurnPassive : PassiveBlock
{
    public const string BlockId = "passive.activate_set_turn";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "You can activate this Trap the turn it was Set";
    public override string Sentence => "You can activate this card the turn it was Set";
    public override string Help => "For Trap Cards. Add IF blocks for when it's allowed, like \"you control an \"Artmage\" monster\".";

    public override string Describe(BlockArgs args) => "you can activate this card the turn it was Set";

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Card.Kind != CardKind.Trap) ctx.Effect.Error("Only Trap Cards can be activated the turn they were Set this way.");
        // It works while the card is Set, where its other effects don't apply yet.
        ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_SINGLE)", $"{v}:SetProperty(EFFECT_FLAG_SET_AVAILABLE)", $"{v}:SetCode(EFFECT_TRAP_ACT_IN_SET_TURN)"]);
    }
}

/// <summary>
/// "Cannot be Normal Summoned, unless you control a "Ghostrick" monster": a rule on this card wherever it is, with IF
/// blocks for when it applies ("you control no "Ghostrick" monsters").
/// </summary>
public sealed class CannotBeSummonedPassive : PassiveBlock
{
    public const string BlockId = "passive.cannot_be_summoned";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card cannot be Normal Summoned / Flip Summoned / Set (when its IF blocks hold)";
    public override string Sentence => "This card cannot be {how}";
    public override string Help => "Add IF blocks for when, like \"you control no \"Ghostrick\" monsters\". For \"cannot be Special Summoned\", use the Card tab.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "Cannot be", new("normal", "Normal Summoned"), new("flip", "Flip Summoned"), new("set", "Set")),
    ];

    public override string Describe(BlockArgs args) => $"this card cannot be {args.Describe("how")}";

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var code = ctx.Args.Choice("how") switch { "flip" => "EFFECT_CANNOT_FLIP_SUMMON", "set" => "EFFECT_CANNOT_MSET", _ => "EFFECT_CANNOT_SUMMON" };
        // Checked in the hand (and for a Flip Summon, face-down on the field): an effect of the card wherever it is.
        ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_SINGLE)", $"{v}:SetCode({code})"]);
    }
}

/// <summary>
/// "If this card attacks, your opponent cannot activate Spell/Trap Cards until the end of the Damage Step" (Ancient Gear
/// monsters): true from the attack declaration to the end of the Damage Step.
/// </summary>
public sealed class BattlingCondition : ConditionBlock
{
    public const string BlockId = "if.battling";
    public override string Id => BlockId;
    public override string Group => "Battle";
    public override string Name => "This card attacks / battles";
    public override string Sentence => "If this card {how}";
    public override string Help => "From the attack declaration until the end of the Damage Step. For lasting effects like the Ancient Gear monsters'.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "When",
            new("attacks", "attacks"),
            new("battles", "battles"),
            new("attacked", "is attacked")),
    ];

    public override string Describe(BlockArgs args) => $"if this card {args.Describe("how")}";

    public override string Until(BlockArgs args) => "until the end of the Damage Step";

    public static string Check(string how) => how switch
    {
        "battles" => "(Duel.GetAttacker()==e:GetHandler() or Duel.GetAttackTarget()==e:GetHandler())",
        "attacked" => "Duel.GetAttackTarget()==e:GetHandler()",
        _ => "Duel.GetAttacker()==e:GetHandler()",
    };

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        ctx.Effect.Conditions.Add(Check(ctx.Args.Choice("how")));
    }
}
