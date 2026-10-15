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
        new("hand_or_graveyard", "in the hand or GY"),
    ];

    /// <summary>Where this card is while the effect can be used, as card text, or null for the field.</summary>
    public static string? Where(string choice) => choice switch
    {
        "hand" => "in your hand",
        "graveyard" => "in your GY",
        "banished" => "banished",
        "hand_or_graveyard" => "in your hand or GY",
        _ => null,
    };

    /// <summary>"from your hand" and the like, for actions on this card where the effect is used.</summary>
    public static string? From(string choice) => choice switch
    {
        "hand" => "from your hand",
        "graveyard" => "from your GY",
        "hand_or_graveyard" => "from your hand or GY",
        _ => null,
    };

    public static string Resolve(string choice, CardData card) => choice switch
    {
        "monster_zone" => "LOCATION_MZONE",
        "hand" => "LOCATION_HAND",
        "graveyard" => "LOCATION_GRAVE",
        "spell_trap_zone" => "LOCATION_SZONE",
        "field_zone" => "LOCATION_FZONE",
        "pendulum_zone" => "LOCATION_PZONE",
        "banished" => "LOCATION_REMOVED",
        "hand_or_graveyard" => "LOCATION_HAND+LOCATION_GRAVE",
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
            new("any", "Normal, Flip or Special"),
            new("link", "Link"), new("synchro", "Synchro"), new("xyz", "Xyz"),
            new("fusion", "Fusion"), new("ritual", "Ritual"), new("pendulum", "Pendulum")),
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
            case "link" or "synchro" or "xyz" or "fusion" or "ritual" or "pendulum":
                Setup(ctx, "EVENT_SPSUMMON_SUCCESS");
                ctx.Effect.Conditions.Add(SummonedByCondition.Check(ctx.Args.Choice("how")));
                break;
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

/// <summary>The other things that happen to this card: banished, leaves the field, Tributed, battles, is attacked...</summary>
public sealed class ThisCardEventTrigger : SelfTriggerBlock
{
    public override string Id => "when.this_card";
    public override string Name => "Something happens to this card";
    public override string Sentence => "{event}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("event", "What happens",
            new("banished", "If this card is banished"),
            new("leaves_field", "If this card leaves the field"),
            new("tributed", "If this card is Tributed"),
            new("to_hand", "If this card is returned from the field to the hand"),
            new("targeted", "If this card is targeted by a card effect"),
            new("battle_damage", "If this card inflicts battle damage to your opponent"),
            new("declares_attack", "When this card declares an attack"),
            new("attacked", "When this card is targeted for an attack"),
            new("battle_start", "At the start of the Damage Step, if this card battles"),
            new("battled", "After damage calculation, if this card battled"),
            new("damage_step_end", "At the end of the Damage Step, if this card battled")),
        OptionalParam,
    ];

    public override void Compile(BlockContext ctx)
    {
        var ev = ctx.Args.Choice("event");
        if (ev is "battle_damage" or "declares_attack" or "attacked" or "battle_start" or "battled" or "damage_step_end")
            Zones.RequireMonster(ctx, Name);
        var code = ev switch
        {
            "banished" => "EVENT_REMOVE",
            "leaves_field" => "EVENT_LEAVE_FIELD",
            "tributed" => "EVENT_RELEASE",
            "to_hand" => "EVENT_TO_HAND",
            "targeted" => "EVENT_BECOME_TARGET",
            "battle_damage" => "EVENT_BATTLE_DAMAGE",
            "declares_attack" => "EVENT_ATTACK_ANNOUNCE",
            "attacked" => "EVENT_BE_BATTLE_TARGET",
            "battle_start" => "EVENT_BATTLE_START",
            "battled" => "EVENT_BATTLED",
            _ => "EVENT_DAMAGE_STEP_END",
        };
        Setup(ctx, code);
        // "When ..." effects activate right away; only "If ..." ones can't miss their timing.
        if (ev is "declares_attack" or "attacked" or "battle_start" or "battled" or "damage_step_end") ctx.Effect.Properties.Remove("EFFECT_FLAG_DELAY");
        switch (ev)
        {
            case "battle_damage": ctx.Effect.Conditions.Add("ep~=tp"); break;
            case "to_hand": ctx.Effect.Conditions.Add("e:GetHandler():IsPreviousLocation(LOCATION_ONFIELD)"); break;
            case "battled" or "damage_step_end": ctx.Effect.Conditions.Add("e:GetHandler():IsRelateToBattle()"); break;
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

/// <summary>
/// A built-in way to Special Summon this card, like Cyber Dragon or Chaos Sorcerer. It is not an effect:
/// it doesn't start a chain, so it has IF and COST blocks but no DO blocks.
/// </summary>
public sealed class SummonProcedureTrigger : BlockDefinition
{
    public const string BlockId = "when.summon_procedure";

    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "This card";
    public override string Name => "Special Summon procedure";
    public override string Sentence => "You can Special Summon this card {zone} (Summon procedure)";
    public override string Help =>
        "A built-in way to Special Summon this card, like Cyber Dragon or Chaos Sorcerer. It doesn't start a chain, " +
        "so it can't be responded to or negated. Add IF blocks for when it's allowed and COST blocks for what you pay. " +
        "It has no DO blocks: the Summon is what it does.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("zone", "From",
            new("hand", "from your hand"),
            new("graveyard", "from your GY"),
            new("banished", "from your banishment")),
    ];

    public override string Describe(BlockArgs args) => "";

    /// <summary>The "(from your hand)" part of the card text.</summary>
    public static string FromText(string zone) => zone switch
    {
        "graveyard" => "(from your GY)",
        "banished" => "(from your banishment)",
        _ => "(from your hand)",
    };

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        if (ctx.Card.IsExtraDeck)
            ctx.Effect.Error($"{ctx.Card.Frame} monsters are Summoned by the procedure on the Card tab, not by a \"{Name}\" effect.");
        ctx.Effect.IsSummonProcedure = true;
        ctx.Effect.Types.Add("EFFECT_TYPE_FIELD");
        ctx.Effect.Code = "EFFECT_SPSUMMON_PROC";
        ctx.Effect.AddProperty("EFFECT_FLAG_UNCOPYABLE");
        if (ctx.Card.SummonRule != SummonRule.Normal) ctx.Effect.AddProperty("EFFECT_FLAG_CANNOT_DISABLE");
        ctx.Effect.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
    }
}

internal static class Timing
{
    public static readonly ChoiceOption[] Choices =
    [
        new("any", "at any time"),
        new("attack", "when an opponent's monster declares an attack"),
        new("attack_direct", "when an opponent's monster declares a direct attack"),
        new("attack_any", "when a monster declares an attack"),
        new("summon", "when your opponent Normal or Special Summons a monster(s)"),
        new("summon_normal", "when your opponent Normal Summons a monster"),
        new("summon_special", "when your opponent Special Summons a monster(s)"),
        new("summon_flip", "when your opponent Flip Summons a monster"),
        new("summon_you", "when you Normal or Special Summon a monster(s)"),
        new("any_summon", "when any monster is Summoned"),
        new("chain_any", "when your opponent activates a card or effect"),
        new("chain_monster", "when your opponent activates a monster effect"),
        new("chain_spelltrap", "when your opponent activates a Spell/Trap Card"),
        new("chain_spell", "when your opponent activates a Spell Card"),
        new("chain_trap", "when your opponent activates a Trap Card"),
        new("chain_either", "when a card or effect is activated"),
        new("would_summon", "when your opponent would Normal or Special Summon a monster(s)"),
        new("would_special", "when your opponent would Special Summon a monster(s)"),
        new("damage_calc", "during damage calculation, if your monster battles"),
        new("battle_phase", "during the Battle Phase"),
        new("end_phase", "during the End Phase"),
    ];

    /// <summary>True when the effect responds to a Summon about to happen, so "negate the Summon" can be used.</summary>
    public static bool IsWouldSummonTiming(string? code) => code is "EVENT_SUMMON" or "EVENT_SPSUMMON" or "EVENT_FLIP_SUMMON";

    /// <summary>True when the effect responds to an activation, so "negate" blocks can be used.</summary>
    public static bool IsChainTiming(string? code) => code == "EVENT_CHAINING";

    /// <summary>True when the timing is something the opponent does, so "during their turn" reads naturally after it.</summary>
    public static bool IsOpponents(string timing) => timing is "attack" or "attack_direct" or "summon" or "summon_normal" or "summon_special"
        or "summon_flip" or "would_summon" or "would_special" or "chain_any" or "chain_monster" or "chain_spelltrap" or "chain_spell" or "chain_trap";

    public static void Apply(BlockContext ctx, string timing)
    {
        var e = ctx.Effect;
        switch (timing)
        {
            case "attack":
                e.Code = "EVENT_ATTACK_ANNOUNCE";
                e.Conditions.Add("Duel.GetAttacker():IsControler(1-tp)");
                break;
            case "attack_direct":
                e.Code = "EVENT_ATTACK_ANNOUNCE";
                e.Conditions.Add("Duel.GetAttacker():IsControler(1-tp) and Duel.GetAttackTarget()==nil");
                break;
            case "attack_any":
                e.Code = "EVENT_ATTACK_ANNOUNCE";
                break;
            case "summon":
                e.Code = "EVENT_SUMMON_SUCCESS";
                e.ExtraCodes.Add("EVENT_SPSUMMON_SUCCESS");
                e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)");
                break;
            case "summon_normal":
                e.Code = "EVENT_SUMMON_SUCCESS";
                e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)");
                break;
            case "summon_special":
                e.Code = "EVENT_SPSUMMON_SUCCESS";
                e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)");
                break;
            case "summon_flip":
                e.Code = "EVENT_FLIP_SUMMON_SUCCESS";
                e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)");
                break;
            case "summon_you":
                e.Code = "EVENT_SUMMON_SUCCESS";
                e.ExtraCodes.Add("EVENT_SPSUMMON_SUCCESS");
                e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,tp)");
                break;
            case "chain_any":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("rp==1-tp");
                break;
            case "chain_monster":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("rp==1-tp and re:IsActiveType(TYPE_MONSTER)");
                break;
            case "chain_spelltrap":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("rp==1-tp and re:IsHasType(EFFECT_TYPE_ACTIVATE)");
                break;
            case "chain_spell":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("rp==1-tp and re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:IsActiveType(TYPE_SPELL)");
                break;
            case "chain_trap":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("rp==1-tp and re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:IsActiveType(TYPE_TRAP)");
                break;
            case "chain_either":
                e.Code = "EVENT_CHAINING";
                break;
            case "would_summon":
                e.Code = "EVENT_SUMMON";
                e.ExtraCodes.Add("EVENT_FLIP_SUMMON");
                e.ExtraCodes.Add("EVENT_SPSUMMON");
                e.Conditions.Add("Duel.GetCurrentChain()==0 and eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)");
                break;
            case "would_special":
                e.Code = "EVENT_SPSUMMON";
                e.Conditions.Add("Duel.GetCurrentChain()==0 and eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)");
                break;
            case "damage_calc":
                e.Code = "EVENT_PRE_DAMAGE_CALCULATE";
                e.Conditions.Add("Duel.GetBattleMonster(tp)~=nil");
                break;
            case "battle_phase":
                e.Code = "EVENT_FREE_CHAIN";
                e.HintTiming = "TIMING_BATTLE_START+TIMING_BATTLE_END";
                e.Conditions.Add("Duel.GetCurrentPhase()>=PHASE_BATTLE_START and Duel.GetCurrentPhase()<=PHASE_BATTLE");
                break;
            case "end_phase":
                e.Code = "EVENT_FREE_CHAIN";
                e.HintTiming = "TIMING_END_PHASE";
                e.Conditions.Add("Duel.GetCurrentPhase()==PHASE_END");
                break;
            case "any_summon":
                // Normal, Flip or Special Summons by either player, this card included.
                e.Code = "EVENT_SUMMON_SUCCESS";
                e.ExtraCodes.Add("EVENT_FLIP_SUMMON_SUCCESS");
                e.ExtraCodes.Add("EVENT_SPSUMMON_SUCCESS");
                break;
            default:
                e.Code = "EVENT_FREE_CHAIN";
                break;
        }
    }

    public static string Phrase(string timing) => timing switch
    {
        "attack" => "When an opponent's monster declares an attack",
        "attack_direct" => "When an opponent's monster declares a direct attack",
        "attack_any" => "When a monster declares an attack",
        "summon" => "When your opponent Normal or Special Summons a monster(s)",
        "summon_normal" => "When your opponent Normal Summons a monster",
        "summon_special" => "When your opponent Special Summons a monster(s)",
        "summon_flip" => "When your opponent Flip Summons a monster",
        "summon_you" => "When you Normal or Special Summon a monster(s)",
        "any_summon" => "When a monster(s) is Normal, Flip or Special Summoned",
        "chain_any" => "When your opponent activates a card or effect",
        "chain_monster" => "When your opponent activates a monster effect",
        "chain_spelltrap" => "When your opponent activates a Spell/Trap Card",
        "chain_spell" => "When your opponent activates a Spell Card",
        "chain_trap" => "When your opponent activates a Trap Card",
        "chain_either" => "When a card or effect is activated",
        "would_summon" => "When your opponent would Normal or Special Summon a monster(s)",
        "would_special" => "When your opponent would Special Summon a monster(s)",
        "damage_calc" => "During damage calculation, if your monster battles",
        "battle_phase" => "During the Battle Phase",
        "end_phase" => "During the End Phase",
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

/// <summary>
/// "If [something happens]: You can ...": a trigger effect that watches the duel rather than this card, used from
/// the field, hand, GY or banishment. For example, "If your opponent Special Summons a monster(s): You can Special
/// Summon this card from your hand".
/// </summary>
public sealed class EventTrigger : BlockDefinition
{
    public const string BlockId = "when.event";
    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "Something happens";
    public override string Name => "Something happens in the duel (trigger)";
    public override string Sentence => "If {event}, {zone}";
    public override string Help => "A trigger effect: it activates after the event, like \"If your opponent Special Summons a monster(s)\". " +
                                   "For an effect used in response, during either player's turn, use \"Quick Effect\" instead.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("event", "What happens",
            new("opp_summon", "your opponent Normal or Special Summons a monster(s)"),
            new("opp_normal", "your opponent Normal Summons a monster"),
            new("opp_special", "your opponent Special Summons a monster(s)"),
            new("you_summon", "you Normal or Special Summon a monster(s)"),
            new("you_special", "you Special Summon a monster(s)"),
            new("any_summon", "a monster(s) is Normal or Special Summoned"),
            new("your_destroyed", "a monster(s) you control is destroyed"),
            new("opp_destroyed", "a monster(s) your opponent controls is destroyed"),
            new("your_battle_destroyed", "a monster(s) you control is destroyed by battle"),
            new("opp_to_gy", "a card(s) is sent to your opponent's GY"),
            new("your_to_gy", "a card(s) is sent to your GY"),
            new("opp_search", "your opponent adds a card(s) from their Deck to their hand"),
            new("opp_draw", "your opponent draws a card(s)"),
            new("you_damage", "you take damage"),
            new("you_battle_damage", "you take battle damage"),
            new("opp_battle_damage", "your opponent takes battle damage"),
            new("opp_attack", "an opponent's monster declares an attack"),
            new("your_attacked", "a monster you control is targeted for an attack"),
            new("your_leaves", "a monster(s) you control leaves the field"),
            new("banished_any", "a card(s) is banished"),
            new("opp_activates", "your opponent activates a card or effect")),
        ParamSpec.Choice("zone", "This card is", Zones.RangeChoices),
        ParamSpec.Flag("optional", "optional (\"You can\")", true),
    ];

    public override string Describe(BlockArgs args) => "If " + args.Describe("event");

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var optional = ctx.Args.Bool("optional");
        e.Types.Add("EFFECT_TYPE_FIELD");
        e.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
        e.AddProperty("EFFECT_FLAG_DELAY"); // "If" wording: it can't miss its timing.
        e.IsMandatory = !optional;
        e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);

        string Filter(string name, string test) => ctx.Script.AddFunction(name, "c,tp", [$"return {test}"]);
        switch (ctx.Args.Choice("event"))
        {
            case "opp_normal": e.Code = "EVENT_SUMMON_SUCCESS"; e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)"); break;
            case "opp_special": e.Code = "EVENT_SPSUMMON_SUCCESS"; e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)"); break;
            case "you_summon":
                e.Code = "EVENT_SUMMON_SUCCESS"; e.ExtraCodes.Add("EVENT_SPSUMMON_SUCCESS");
                e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,tp)");
                break;
            case "you_special": e.Code = "EVENT_SPSUMMON_SUCCESS"; e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,tp)"); break;
            case "any_summon": e.Code = "EVENT_SUMMON_SUCCESS"; e.ExtraCodes.Add("EVENT_SPSUMMON_SUCCESS"); break;
            case "your_destroyed":
                e.Code = "EVENT_DESTROYED";
                e.Conditions.Add($"eg:IsExists({Filter("desfilter", "c:IsPreviousControler(tp) and c:IsPreviousLocation(LOCATION_MZONE)")},1,nil,tp)");
                break;
            case "opp_destroyed":
                e.Code = "EVENT_DESTROYED";
                e.Conditions.Add($"eg:IsExists({Filter("desfilter", "c:IsPreviousControler(1-tp) and c:IsPreviousLocation(LOCATION_MZONE)")},1,nil,tp)");
                break;
            case "your_battle_destroyed":
                e.Code = "EVENT_BATTLE_DESTROYED";
                e.Conditions.Add($"eg:IsExists({Filter("desfilter", "c:IsPreviousControler(tp)")},1,nil,tp)");
                break;
            case "opp_to_gy": e.Code = "EVENT_TO_GRAVE"; e.Conditions.Add("eg:IsExists(Card.IsControler,1,nil,1-tp)"); break;
            case "your_to_gy": e.Code = "EVENT_TO_GRAVE"; e.Conditions.Add("eg:IsExists(Card.IsControler,1,nil,tp)"); break;
            case "opp_search":
                e.Code = "EVENT_TO_HAND";
                e.Conditions.Add($"eg:IsExists({Filter("thfilter", "c:IsControler(1-tp) and c:IsPreviousLocation(LOCATION_DECK) and not c:IsReason(REASON_DRAW)")},1,nil,tp)");
                break;
            case "opp_draw": e.Code = "EVENT_DRAW"; e.Conditions.Add("ep==1-tp"); break;
            case "you_damage": e.Code = "EVENT_DAMAGE"; e.Conditions.Add("ep==tp"); break;
            case "you_battle_damage": e.Code = "EVENT_BATTLE_DAMAGE"; e.Conditions.Add("ep==tp"); break;
            case "opp_battle_damage": e.Code = "EVENT_BATTLE_DAMAGE"; e.Conditions.Add("ep==1-tp"); break;
            case "opp_attack": e.Code = "EVENT_ATTACK_ANNOUNCE"; e.Conditions.Add("Duel.GetAttacker():IsControler(1-tp)"); break;
            case "your_attacked":
                e.Code = "EVENT_BE_BATTLE_TARGET";
                e.Conditions.Add("Duel.GetAttackTarget()~=nil and Duel.GetAttackTarget():IsControler(tp)");
                break;
            case "your_leaves":
                e.Code = "EVENT_LEAVE_FIELD";
                e.Conditions.Add($"eg:IsExists({Filter("lvfilter", "c:IsPreviousControler(tp) and c:IsPreviousLocation(LOCATION_MZONE)")},1,nil,tp)");
                break;
            case "banished_any": e.Code = "EVENT_REMOVE"; break;
            case "opp_activates": e.Code = "EVENT_CHAINING"; e.Conditions.Add("rp==1-tp"); break;
            default:
                e.Code = "EVENT_SUMMON_SUCCESS"; e.ExtraCodes.Add("EVENT_SPSUMMON_SUCCESS");
                e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)");
                break;
        }
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
        ParamSpec.Choice("phase", "Phase", new("standby", "Standby Phase"), new("end", "End Phase"),
            new("battle_start", "Battle Phase", "start of the Battle Phase"), new("battle_end", "Battle Phase", "end of the Battle Phase"),
            new("draw", "Draw Phase")),
        ParamSpec.Choice("zone", "From", Zones.RangeChoices),
        ParamSpec.Flag("optional", "optional (\"You can\")", true),
    ];

    public override string Describe(BlockArgs args) => args.Choice("phase") switch
    {
        "battle_start" => $"At the start of {args.Describe("whose")} Battle Phase",
        "battle_end" => $"At the end of {args.Describe("whose")} Battle Phase",
        _ => $"During {args.Describe("whose")} {args.Describe("phase")}",
    };

    public override void Compile(BlockContext ctx)
    {
        var optional = ctx.Args.Bool("optional");
        ctx.Effect.Types.Add("EFFECT_TYPE_FIELD");
        ctx.Effect.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
        ctx.Effect.IsMandatory = !optional;
        ctx.Effect.Code = ctx.Args.Choice("phase") switch
        {
            "end" => "EVENT_PHASE+PHASE_END",
            "battle_start" => "EVENT_PHASE+PHASE_BATTLE_START",
            "battle_end" => "EVENT_PHASE+PHASE_BATTLE",
            "draw" => "EVENT_PHASE+PHASE_DRAW",
            _ => "EVENT_PHASE+PHASE_STANDBY",
        };
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
