using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Blocks;

internal static class Zones
{
    public static readonly ChoiceOption[] RangeChoices =
    [
        new("auto", "on the field"),
        new("monster_zone", "in the Monster Zone"),
        new("hand", "in the hand"),
        new("graveyard", "in the GY"),
        new("spell_trap_zone", "in the Spell & Trap Zone"),
        new("field_zone", "in the Field Zone"),
        new("pendulum_zone", "in the Pendulum Zone"),
        new("banished", "while banished"),
    ];

    public static string Resolve(string choice, CardData card) => choice switch
    {
        "monster_zone" => "LOCATION_MZONE",
        "hand" => "LOCATION_HAND",
        "graveyard" => "LOCATION_GRAVE",
        "spell_trap_zone" => "LOCATION_SZONE",
        "field_zone" => "LOCATION_FZONE",
        "pendulum_zone" => "LOCATION_PZONE",
        "banished" => "LOCATION_REMOVED",
        _ => card.Kind switch
        {
            CardKind.Monster => "LOCATION_MZONE",
            CardKind.Spell when card.SpellKind == SpellKind.Field => "LOCATION_FZONE",
            _ => "LOCATION_SZONE",
        },
    };

    public static void RequireMonster(BlockContext ctx, string what)
    {
        if (!ctx.Card.IsMonster) ctx.Effect.Error($"\"{what}\" only works on monster cards.");
    }
}

/// <summary>Shared setup for "If this card is ...: You can ..." triggers.</summary>
public abstract class SelfTriggerBlock : BlockDefinition
{
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "This card";

    protected static ParamSpec OptionalParam => ParamSpec.Flag("optional", "optional (\"You can\")", true);

    protected static void Setup(BlockContext ctx, string code, params string[] extraCodes)
    {
        var optional = ctx.Args.Bool("optional");
        ctx.Effect.Types.Add("EFFECT_TYPE_SINGLE");
        ctx.Effect.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
        // "If" wording: the effect can't miss its timing.
        ctx.Effect.AddProperty("EFFECT_FLAG_DELAY");
        ctx.Effect.Code = code;
        ctx.Effect.ExtraCodes.AddRange(extraCodes);
        ctx.Effect.IsMandatory = !optional;
    }
}

public sealed class SummonedTrigger : SelfTriggerBlock
{
    public override string Id => "when.summoned";
    public override string Name => "This card is Summoned";
    public override string Sentence => "If this card is {how} Summoned";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How",
            new("normal", "Normal"),
            new("special", "Special"),
            new("normal_or_special", "Normal or Special"),
            new("flip", "Flip"),
            new("any", "Normal, Flip or Special")),
        OptionalParam,
    ];

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        switch (ctx.Args.Choice("how"))
        {
            case "normal": Setup(ctx, "EVENT_SUMMON_SUCCESS"); break;
            case "special": Setup(ctx, "EVENT_SPSUMMON_SUCCESS"); break;
            case "flip": Setup(ctx, "EVENT_FLIP_SUMMON_SUCCESS"); break;
            case "normal_or_special": Setup(ctx, "EVENT_SUMMON_SUCCESS", "EVENT_SPSUMMON_SUCCESS"); break;
            default: Setup(ctx, "EVENT_SUMMON_SUCCESS", "EVENT_FLIP_SUMMON_SUCCESS", "EVENT_SPSUMMON_SUCCESS"); break;
        }
    }
}

public sealed class SentToGraveTrigger : SelfTriggerBlock
{
    public override string Id => "when.sent_to_gy";
    public override string Name => "This card is sent to the GY";
    public override string Sentence => "If this card is sent to the GY{from}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("from", "From",
            new("any", "", "from anywhere"),
            new("field", " from the field"),
            new("hand", " from the hand"),
            new("deck", " from the Deck"),
            new("hand_or_field", " from the hand or field")),
        OptionalParam,
    ];

    public override void Compile(BlockContext ctx)
    {
        Setup(ctx, "EVENT_TO_GRAVE");
        var loc = ctx.Args.Choice("from") switch
        {
            "field" => "LOCATION_ONFIELD",
            "hand" => "LOCATION_HAND",
            "deck" => "LOCATION_DECK",
            "hand_or_field" => "LOCATION_HAND+LOCATION_ONFIELD",
            _ => null,
        };
        if (loc is not null) ctx.Effect.Conditions.Add($"e:GetHandler():IsPreviousLocation({loc})");
    }
}

public sealed class DestroyedTrigger : SelfTriggerBlock
{
    public override string Id => "when.destroyed";
    public override string Name => "This card is destroyed";
    public override string Sentence => "If this card is destroyed{by}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("by", "By",
            new("any", "", "by anything"),
            new("battle", " by battle"),
            new("effect", " by a card effect"),
            new("opponent", " by an opponent's card")),
        OptionalParam,
    ];

    public override void Compile(BlockContext ctx)
    {
        switch (ctx.Args.Choice("by"))
        {
            case "battle":
                Zones.RequireMonster(ctx, "Destroyed by battle");
                Setup(ctx, "EVENT_BATTLE_DESTROYED");
                break;
            case "effect":
                Setup(ctx, "EVENT_DESTROYED");
                ctx.Effect.Conditions.Add("e:GetHandler():IsReason(REASON_EFFECT)");
                break;
            case "opponent":
                Setup(ctx, "EVENT_DESTROYED");
                ctx.Effect.Conditions.Add("rp==1-tp and e:GetHandler():IsPreviousControler(tp)");
                break;
            default:
                Setup(ctx, "EVENT_DESTROYED");
                break;
        }
    }
}

public sealed class DestroysByBattleTrigger : SelfTriggerBlock
{
    public override string Id => "when.destroys_by_battle";
    public override string Name => "This card destroys a monster by battle";
    public override string Sentence => "If this card destroys an opponent's monster by battle";
    public override IReadOnlyList<ParamSpec> Params { get; } = [OptionalParam];

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        Setup(ctx, "EVENT_BATTLE_DESTROYING");
        ctx.Effect.Conditions.Add("aux.bdocon(e,tp,eg,ep,ev,re,r,rp)");
    }
}

public sealed class FlipTrigger : BlockDefinition
{
    public override string Id => "when.flip";
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "This card";
    public override string Name => "FLIP (this card is flipped face-up)";
    public override string Sentence => "FLIP";
    public override string Help => "A FLIP effect. Tick the Flip ability on the Card tab too.";

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        if (!ctx.Card.Abilities.HasFlag(MonsterAbility.Flip))
            ctx.Effect.Warn("FLIP effects need the Flip ability ticked on the Card tab.");
        ctx.Effect.Types.Add("EFFECT_TYPE_SINGLE");
        ctx.Effect.Types.Add("EFFECT_TYPE_FLIP");
        ctx.Effect.IsMandatory = true;
    }
}

public sealed class IgnitionTrigger : BlockDefinition
{
    public override string Id => "when.ignition";
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "You choose";
    public override string Name => "You activate it (Main Phase)";
    public override string Sentence => "You activate it during your Main Phase, {zone}";
    public override string Help => "An Ignition effect: the player activates it during their own Main Phase.";
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Choice("zone", "From", Zones.RangeChoices)];

    public override string Describe(BlockArgs args) => "";

    public override void Compile(BlockContext ctx)
    {
        ctx.Effect.Types.Add("EFFECT_TYPE_IGNITION");
        ctx.Effect.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
    }
}

internal static class Timing
{
    public static readonly ChoiceOption[] Choices =
    [
        new("any", "at any time"),
        new("attack", "when an opponent's monster declares an attack"),
        new("summon", "when your opponent Normal or Special Summons a monster"),
        new("any_summon", "when any monster is Summoned"),
        new("chain_any", "when your opponent activates a card or effect"),
        new("chain_monster", "when your opponent activates a monster effect"),
        new("chain_spelltrap", "when your opponent activates a Spell/Trap Card"),
    ];

    /// <summary>True when the effect responds to an activation, so "negate" blocks can be used.</summary>
    public static bool IsChainTiming(string? code) => code == "EVENT_CHAINING";

    public static void Apply(BlockContext ctx, string timing)
    {
        switch (timing)
        {
            case "attack":
                ctx.Effect.Code = "EVENT_ATTACK_ANNOUNCE";
                ctx.Effect.Conditions.Add("Duel.GetAttacker():IsControler(1-tp)");
                break;
            case "summon":
                ctx.Effect.Code = "EVENT_SUMMON_SUCCESS";
                ctx.Effect.ExtraCodes.Add("EVENT_SPSUMMON_SUCCESS");
                ctx.Effect.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)");
                break;
            case "chain_any":
                ctx.Effect.Code = "EVENT_CHAINING";
                ctx.Effect.Conditions.Add("rp==1-tp");
                break;
            case "chain_monster":
                ctx.Effect.Code = "EVENT_CHAINING";
                ctx.Effect.Conditions.Add("rp==1-tp and re:IsActiveType(TYPE_MONSTER)");
                break;
            case "chain_spelltrap":
                ctx.Effect.Code = "EVENT_CHAINING";
                ctx.Effect.Conditions.Add("rp==1-tp and re:IsHasType(EFFECT_TYPE_ACTIVATE)");
                break;
            case "any_summon":
                // Normal, Flip or Special Summons by either player, this card included.
                ctx.Effect.Code = "EVENT_SUMMON_SUCCESS";
                ctx.Effect.ExtraCodes.Add("EVENT_FLIP_SUMMON_SUCCESS");
                ctx.Effect.ExtraCodes.Add("EVENT_SPSUMMON_SUCCESS");
                break;
            default:
                ctx.Effect.Code = "EVENT_FREE_CHAIN";
                break;
        }
    }

    public static string Phrase(string timing) => timing switch
    {
        "attack" => "When an opponent's monster declares an attack",
        "summon" => "When your opponent Normal or Special Summons a monster",
        "any_summon" => "When a monster is Normal, Flip or Special Summoned",
        "chain_any" => "When your opponent activates a card or effect",
        "chain_monster" => "When your opponent activates a monster effect",
        "chain_spelltrap" => "When your opponent activates a Spell/Trap Card",
        _ => "",
    };
}

public sealed class ActivateTrigger : BlockDefinition
{
    public override string Id => "when.activate";
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "You choose";
    public override string Name => "Activate this Spell/Trap";
    public override string Sentence => "Activate this card {timing}";
    public override string Help => "The effect a Spell or Trap card has when it is activated.";
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Choice("timing", "Timing", Timing.Choices)];

    public override string Describe(BlockArgs args) => Timing.Phrase(args.Choice("timing"));

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Card.IsMonster) ctx.Effect.Error("\"Activate this card\" is for Spell and Trap cards. Monsters use \"You activate it\" or a Quick Effect.");
        ctx.Effect.Types.Add("EFFECT_TYPE_ACTIVATE");
        ctx.Effect.IsActivation = true;
        Timing.Apply(ctx, ctx.Args.Choice("timing"));
    }
}

public sealed class QuickTrigger : BlockDefinition
{
    public override string Id => "when.quick";
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "You choose";
    public override string Name => "Quick Effect";
    public override string Sentence => "(Quick Effect) You activate it {timing}, {zone}";
    public override string Help => "Can be activated during either player's turn, like a Quick-Play Spell.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("timing", "Timing", Timing.Choices),
        ParamSpec.Choice("zone", "From", Zones.RangeChoices),
    ];

    /// <summary>Only the timing; the text generator adds "(Quick Effect)" after any conditions.</summary>
    public override string Describe(BlockArgs args) => Timing.Phrase(args.Choice("timing"));

    public override void Compile(BlockContext ctx)
    {
        ctx.Effect.Types.Add("EFFECT_TYPE_QUICK_O");
        ctx.Effect.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
        Timing.Apply(ctx, ctx.Args.Choice("timing"));
    }
}

public sealed class PhaseTrigger : BlockDefinition
{
    public override string Id => "when.phase";
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "Turn";
    public override string Name => "During a phase";
    public override string Sentence => "During {whose} {phase}, {zone}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("whose", "Whose", new("your", "your"), new("opponent", "your opponent's"), new("each", "each")),
        ParamSpec.Choice("phase", "Phase", new("standby", "Standby Phase"), new("end", "End Phase")),
        ParamSpec.Choice("zone", "From", Zones.RangeChoices),
        ParamSpec.Flag("optional", "optional (\"You can\")", true),
    ];

    public override string Describe(BlockArgs args) => $"During {args.Describe("whose")} {args.Describe("phase")}";

    public override void Compile(BlockContext ctx)
    {
        var optional = ctx.Args.Bool("optional");
        ctx.Effect.Types.Add("EFFECT_TYPE_FIELD");
        ctx.Effect.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
        ctx.Effect.IsMandatory = !optional;
        ctx.Effect.Code = ctx.Args.Choice("phase") == "end" ? "EVENT_PHASE+PHASE_END" : "EVENT_PHASE+PHASE_STANDBY";
        ctx.Effect.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
        switch (ctx.Args.Choice("whose"))
        {
            case "your": ctx.Effect.Conditions.Add("Duel.GetTurnPlayer()==tp"); break;
            case "opponent": ctx.Effect.Conditions.Add("Duel.GetTurnPlayer()==1-tp"); break;
        }
    }
}

public sealed class WhileFaceUpTrigger : BlockDefinition
{
    public override string Id => "when.while_face_up";
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "Lasting";
    public override string Name => "While this card is face-up (lasting effect)";
    public override string Sentence => "While this card is face-up {zone}";
    public override string Help => "A lasting effect that is always on and never activates, like an ATK boost or protection.";
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Choice("zone", "Where", Zones.RangeChoices)];

    public override string Describe(BlockArgs args) => "";

    public override void Compile(BlockContext ctx)
    {
        ctx.Effect.IsPassive = true;
        ctx.Effect.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
    }
}
