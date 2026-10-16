using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

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

/// <summary>
/// "If this card is Normal Summoned": one way of Summoning per block. More ways start the same effect with "Or when",
/// and read as "If this card is Normal or Special Summoned".
/// </summary>
public sealed class SummonedTrigger : SelfTriggerBlock
{
    public const string BlockId = "when.summoned";
    public override string Id => BlockId;
    public override string Name => "This card is Summoned";
    public override string Sentence => "If this card is {how} Summoned{from}";
    public override string Help => "One way of Summoning per block. For \"Normal or Special Summoned\", add the other with \"+ Or when...\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "How",
            new("normal", "Normal"),
            new("special", "Special"),
            new("flip", "Flip"),
            new("tribute", "Tribute"),
            new("ritual", "Ritual"), new("fusion", "Fusion"), new("synchro", "Synchro"), new("xyz", "Xyz"),
            new("pendulum", "Pendulum"), new("link", "Link"),
            new("self", "Special", "Special Summoned by its own effect (\"Summoned this way\")")),
        ParamSpec.Choice("from", "From",
            new("any", "", "from anywhere"),
            new("hand", " from the hand"),
            new("deck", " from the Deck"),
            new("gy", " from the GY"),
            new("banished", " from banishment"),
            new("extra", " from the Extra Deck")).ShowIf(b => b.Values.GetValueOrDefault("how") is not ("normal" or "flip" or "tribute")),
        OptionalParam,
    ];

    private static string From(BlockArgs args) => args.Choice("how") is "normal" or "flip" or "tribute" ? "" : args.Describe("from");

    public override string Describe(BlockArgs args) => args.Choice("how") == "self"
        ? "If this card is Special Summoned by its own effect" + From(args)
        : $"If this card is {args.Describe("how")} Summoned{From(args)}";

    /// <summary>"Normal or Special Summoned", and "Special Summoned from the hand or GY".</summary>
    public override EventPhrase? Phrase(BlockArgs args)
    {
        var from = From(args);
        if (args.Choice("how") == "self") return new("If", "this card is Special Summoned by its own effect" + from);
        return from.Length == 0
            ? new("If", "this card is {0} Summoned", args.Describe("how"))
            : new("If", $"this card is {args.Describe("how")} Summoned from {{0}}", from[" from ".Length..]);
    }

    /// <summary>The Summon type check for each way of Summoning, or null when the event says it all.</summary>
    public static string? TypeCheck(string how) => how switch
    {
        "tribute" => "e:GetHandler():IsSummonType(SUMMON_TYPE_ADVANCE)",
        "ritual" or "fusion" or "synchro" or "xyz" or "pendulum" or "link" or "self" => SummonedByCondition.Check(how),
        _ => null,
    };

    public static string? FromCheck(string from) => from switch
    {
        "hand" => "e:GetHandler():IsSummonLocation(LOCATION_HAND)",
        "deck" => "e:GetHandler():IsSummonLocation(LOCATION_DECK)",
        "gy" => "e:GetHandler():IsSummonLocation(LOCATION_GRAVE)",
        "banished" => "e:GetHandler():IsSummonLocation(LOCATION_REMOVED)",
        "extra" => "e:GetHandler():IsSummonLocation(LOCATION_EXTRA)",
        _ => null,
    };

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var how = ctx.Args.Choice("how");
        Setup(ctx, how switch { "normal" or "tribute" => "EVENT_SUMMON_SUCCESS", "flip" => "EVENT_FLIP_SUMMON_SUCCESS", _ => "EVENT_SPSUMMON_SUCCESS" });
        if (TypeCheck(how) is { } type) ctx.Effect.Conditions.Add(type);
        if (how is not ("normal" or "flip" or "tribute") && FromCheck(ctx.Args.Choice("from")) is { } from) ctx.Effect.Conditions.Add(from);
        if (how == "pendulum" && ctx.Args.Choice("from") is "gy" or "deck" or "banished")
            ctx.Effect.Warn("Pendulum Summons are from the hand or face-up Extra Deck only.");
    }
}

public sealed class SentToGraveTrigger : SelfTriggerBlock
{
    public override string Id => "when.sent_to_gy";
    public override string Name => "This card is sent to the GY";
    public override string Sentence => "If this card is sent to the GY{from}{by}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("from", "From",
            new("any", "", "from anywhere"),
            new("field", " from the field"),
            new("hand", " from the hand"),
            new("deck", " from the Deck"),
            new("extra", " from the Extra Deck"),
            new("overlay", " after being detached from an Xyz Monster", "after being detached from an Xyz Monster")),
        ParamSpec.Choice("by", "By",
            new("any", "", "by anything"),
            new("effect", " by a card effect"),
            new("opponent", " by an opponent's card"),
            new("opponent_effect", " by an opponent's card effect"),
            new("battle", " by battle"),
            new("cost", " as a cost", "as a cost (to activate an effect)")),
        OptionalParam,
    ];

    public override EventPhrase? Phrase(BlockArgs args) => new("If", "this card is {0}", Describe(args)["If this card is ".Length..]);

    public override void Compile(BlockContext ctx)
    {
        Setup(ctx, "EVENT_TO_GRAVE");
        var loc = ctx.Args.Choice("from") switch
        {
            "field" => "LOCATION_ONFIELD",
            "hand" => "LOCATION_HAND",
            "deck" => "LOCATION_DECK",
            "extra" => "LOCATION_EXTRA",
            "overlay" => "LOCATION_OVERLAY",
            _ => null,
        };
        if (loc is not null) ctx.Effect.Conditions.Add($"e:GetHandler():IsPreviousLocation({loc})");
        switch (ctx.Args.Choice("by"))
        {
            case "effect": ctx.Effect.Conditions.Add("e:GetHandler():IsReason(REASON_EFFECT)"); break;
            case "opponent": ctx.Effect.Conditions.Add("rp==1-tp and e:GetHandler():IsPreviousControler(tp)"); break;
            case "opponent_effect": ctx.Effect.Conditions.Add("rp==1-tp and e:GetHandler():IsReason(REASON_EFFECT) and e:GetHandler():IsPreviousControler(tp)"); break;
            case "battle": ctx.Effect.Conditions.Add("e:GetHandler():IsReason(REASON_BATTLE)"); break;
            case "cost": ctx.Effect.Conditions.Add("e:GetHandler():IsReason(REASON_COST)"); break;
        }
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
            new("battle_gy", " by battle and sent to the GY"),
            new("effect", " by a card effect"),
            new("opponent", " by an opponent's card"),
            new("opponent_effect", " by an opponent's card effect"),
            new("facedown", " while face-down", "while face-down"),
            new("in_pzone", " in the Pendulum Zone", "in the Pendulum Zone")),
        OptionalParam,
    ];

    public override EventPhrase? Phrase(BlockArgs args) => new("If", "this card is {0}", Describe(args)["If this card is ".Length..]);

    public override void Compile(BlockContext ctx)
    {
        switch (ctx.Args.Choice("by"))
        {
            case "battle":
                Zones.RequireMonster(ctx, "Destroyed by battle");
                Setup(ctx, "EVENT_BATTLE_DESTROYED");
                break;
            case "battle_gy":
                Zones.RequireMonster(ctx, "Destroyed by battle");
                Setup(ctx, "EVENT_BATTLE_DESTROYED");
                ctx.Effect.Conditions.Add("e:GetHandler():IsLocation(LOCATION_GRAVE)");
                break;
            case "effect":
                Setup(ctx, "EVENT_DESTROYED");
                ctx.Effect.Conditions.Add("e:GetHandler():IsReason(REASON_EFFECT)");
                break;
            case "opponent":
                Setup(ctx, "EVENT_DESTROYED");
                ctx.Effect.Conditions.Add("rp==1-tp and e:GetHandler():IsPreviousControler(tp)");
                break;
            case "opponent_effect":
                Setup(ctx, "EVENT_DESTROYED");
                ctx.Effect.Conditions.Add("rp==1-tp and e:GetHandler():IsReason(REASON_EFFECT) and e:GetHandler():IsPreviousControler(tp)");
                break;
            case "facedown":
                Setup(ctx, "EVENT_DESTROYED");
                ctx.Effect.Conditions.Add("e:GetHandler():IsPreviousPosition(POS_FACEDOWN) and e:GetHandler():IsPreviousLocation(LOCATION_ONFIELD)");
                break;
            case "in_pzone":
                Setup(ctx, "EVENT_DESTROYED");
                ctx.Effect.Conditions.Add("e:GetHandler():IsPreviousLocation(LOCATION_PZONE)");
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
    public const string BlockId = "when.this_card";

    /// <summary>One thing that can happen to this card: its card text, event, check, and whether only monsters do it.</summary>
    /// <param name="Kind">
    /// "is" for "this card is banished", "does" for "this card leaves the field", "when" for "When this card declares
    /// an attack", "if" for "If control of this card changes", "fixed" for a whole phrase that doesn't combine.
    /// </param>
    public sealed record ThisCardEvent(string Key, string Text, string Code, string? Check = null, bool Monster = false, string Kind = "is", bool Delay = true);

    public static readonly ThisCardEvent[] Events =
    [
        new("banished", "banished", "EVENT_REMOVE"),
        new("banished_facedown", "banished face-down", "EVENT_REMOVE", "e:GetHandler():IsFacedown()"),
        new("leaves_field", "leaves the field", "EVENT_LEAVE_FIELD", Kind: "does"),
        new("leaves_by_opponent", "leaves the field because of an opponent's card", "EVENT_LEAVE_FIELD", "rp==1-tp and e:GetHandler():IsPreviousControler(tp)", Kind: "does"),
        new("leaves_by_opponent_effect", "leaves the field because of an opponent's card effect", "EVENT_LEAVE_FIELD",
            "rp==1-tp and e:GetHandler():IsReason(REASON_EFFECT) and e:GetHandler():IsPreviousControler(tp)", Kind: "does"),
        new("tributed", "Tributed", "EVENT_RELEASE"),
        new("to_hand", "returned from the field to the hand", "EVENT_TO_HAND", "e:GetHandler():IsPreviousLocation(LOCATION_ONFIELD)"),
        new("added_to_hand", "added to your hand, except by drawing it", "EVENT_TO_HAND", "not e:GetHandler():IsReason(REASON_DRAW)"),
        new("drawn", "drawn", "EVENT_DRAW"),
        new("to_deck", "returned to the Deck", "EVENT_TO_DECK", "e:GetHandler():IsLocation(LOCATION_DECK)"),
        new("to_extra", "returned to the Extra Deck", "EVENT_TO_DECK", "e:GetHandler():IsLocation(LOCATION_EXTRA)"),
        new("face_up_extra", "placed face-up in the Extra Deck", "EVENT_TO_DECK", "e:GetHandler():IsLocation(LOCATION_EXTRA) and e:GetHandler():IsFaceup()", true),
        new("discarded", "discarded", "EVENT_DISCARD"),
        new("detached", "detached from an Xyz Monster", "EVENT_TO_GRAVE", "e:GetHandler():IsPreviousLocation(LOCATION_OVERLAY)"),
        new("flipped", "flipped face-up", "EVENT_FLIP", Monster: true),
        new("position_changed", "this card's battle position is changed", "EVENT_CHANGE_POS", Monster: true, Kind: "if"),
        new("control_changed", "control of this card changes", "EVENT_CONTROL_CHANGED", Monster: true, Kind: "if"),
        new("equipped", "equipped with an Equip Card", "EVENT_EQUIP", Monster: true),
        new("targeted", "targeted by a card effect", "EVENT_BECOME_TARGET"),
        new("targeted_opponent", "targeted by an opponent's card effect", "EVENT_BECOME_TARGET", "rp==1-tp"),
        new("battle_damage", "inflicts battle damage to your opponent", "EVENT_BATTLE_DAMAGE", "ep~=tp", true, "does"),
        new("direct_damage", "inflicts battle damage to your opponent by a direct attack", "EVENT_BATTLE_DAMAGE", "ep~=tp and Duel.GetAttackTarget()==nil", true, "does"),
        new("you_battle_damage", "you take battle damage from a battle involving this card", "EVENT_BATTLE_DAMAGE", "ep==tp", true, "if"),
        new("declares_attack", "declares an attack", "EVENT_ATTACK_ANNOUNCE", Monster: true, Kind: "when", Delay: false),
        new("declares_direct_attack", "declares a direct attack", "EVENT_ATTACK_ANNOUNCE", "Duel.GetAttackTarget()==nil", true, "when", false),
        new("attacked", "is targeted for an attack", "EVENT_BE_BATTLE_TARGET", Monster: true, Kind: "when", Delay: false),
        new("battle_start", "At the start of the Damage Step, if this card battles", "EVENT_BATTLE_START", Monster: true, Kind: "fixed", Delay: false),
        new("battled", "After damage calculation, if this card battled", "EVENT_BATTLED", "e:GetHandler():IsRelateToBattle()", true, "fixed", false),
        new("damage_step_end", "At the end of the Damage Step, if this card battled", "EVENT_DAMAGE_STEP_END", "e:GetHandler():IsRelateToBattle()", true, "fixed", false),
    ];

    private static string Wording(ThisCardEvent e) => e.Kind switch
    {
        "is" => $"If this card is {e.Text}",
        "does" => $"If this card {e.Text}",
        "when" => $"When this card {e.Text}",
        "if" => $"If {e.Text}",
        _ => e.Text,
    };

    public override string Id => BlockId;
    public override string Name => "Something happens to this card";
    public override string Sentence => "{event}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("event", "What happens", Events.Select(e => new ChoiceOption(e.Key, Wording(e))).ToArray()),
        OptionalParam,
    ];

    public static ThisCardEvent Find(string key) => Events.FirstOrDefault(e => e.Key == key) ?? Events[0];

    public override EventPhrase? Phrase(BlockArgs args)
    {
        var e = Find(args.Choice("event"));
        return e.Kind switch
        {
            "is" => new("If", "this card is {0}", e.Text),
            "does" => new("If", "this card " + e.Text),
            "when" => new("When", "this card " + e.Text),
            "if" => new("If", e.Text),
            _ => null,
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var e = Find(ctx.Args.Choice("event"));
        if (e.Monster) Zones.RequireMonster(ctx, Name);
        Setup(ctx, e.Code);
        // "When ..." effects activate right away; only "If ..." ones can't miss their timing.
        if (!e.Delay) ctx.Effect.Properties.Remove("EFFECT_FLAG_DELAY");
        if (e.Check is not null) ctx.Effect.Conditions.Add(e.Check);
    }
}

public sealed class DestroysByBattleTrigger : SelfTriggerBlock
{
    public override string Id => "when.destroys_by_battle";
    public override string Name => "This card destroys a monster by battle";
    public override string Sentence => "If this card destroys {versus} by battle{sent}";
    public override EventPhrase? Phrase(BlockArgs args) => new("If", Describe(args)["If ".Length..]);
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("sent", "And", new("any", "", "(that's all)"), new("gy", " and sends it to the GY")),
        // What the scripts check: an opponent's monster and this card still on the field (most cards), any monster, or nothing.
        ParamSpec.Choice("versus", "Which monster",
            new("opponent", "an opponent's monster"),
            new("any", "a monster", "a monster (this card still on the field)"),
            new("monster", "a monster", "a monster that is still a monster (this card still on the field)"),
            new("unchecked", "a monster", "a monster (even if this card left the field)")),
        OptionalParam,
    ];

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        Setup(ctx, "EVENT_BATTLE_DESTROYING");
        var gy = ctx.Args.Choice("sent") == "gy";
        var check = (ctx.Args.Choice("versus"), gy) switch
        {
            ("opponent", true) => "aux.bdogcon(e,tp,eg,ep,ev,re,r,rp)",
            ("opponent", false) => "aux.bdocon(e,tp,eg,ep,ev,re,r,rp)",
            // The monster it destroyed: a Trap Card that was a monster is a Trap again in the GY.
            ("any" or "monster", true) => "aux.bdgcon(e,tp,eg,ep,ev,re,r,rp)",
            ("any", false) => "aux.bdcon(e,tp,eg,ep,ev,re,r,rp)",
            ("monster", false) => "aux.bdcon(e,tp,eg,ep,ev,re,r,rp) and e:GetHandler():GetBattleTarget():IsType(TYPE_MONSTER)",
            (_, true) => "e:GetHandler():GetBattleTarget():IsLocation(LOCATION_GRAVE) and e:GetHandler():GetBattleTarget():IsType(TYPE_MONSTER)",
            _ => null,
        };
        if (check is not null) ctx.Effect.Conditions.Add(check);
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
    public override IReadOnlyList<ParamSpec> Params { get; } = [ParamSpec.Flag("optional", "optional (\"You can\")", false)];

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        if (!ctx.Card.Abilities.HasFlag(MonsterAbility.Flip))
            ctx.Effect.Warn("FLIP effects need the Flip ability ticked on the Card tab.");
        ctx.Effect.Types.Add("EFFECT_TYPE_SINGLE");
        ctx.Effect.Types.Add("EFFECT_TYPE_FLIP");
        if (ctx.Args.Bool("optional"))
        {
            // "FLIP: You can ...": an optional trigger, which can't miss its timing.
            ctx.Effect.Types.Add("EFFECT_TYPE_TRIGGER_O");
            ctx.Effect.AddProperty("EFFECT_FLAG_DELAY");
        }
        else ctx.Effect.IsMandatory = true;
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
    public override string Sentence => "You can Special Summon this card {zone} {to} (Summon procedure)";
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
        ParamSpec.Choice("to", "To", new("any", "", "to any zone"), new("center", "to your center Main Monster Zone")),
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
        // Elfnote: "You can Special Summon this card (from your hand) to your center Main Monster Zone."
        if (ctx.Args.Choice("to") == "center") ctx.Effect.ProcedureZone = "0x4";
    }
}

internal static class Timing
{
    public static EventPhrase? PhraseOf(string timing) => TimingPhrases.Of(timing);

    public static readonly ChoiceOption[] Choices =
    [
        new("any", "at any time"),
        new("attack", "when an opponent's monster declares an attack"),
        new("attack_direct", "when an opponent's monster declares a direct attack"),
        new("attack_any", "when a monster declares an attack"),
        new("main_phase", "during the Main Phase"),
        new("standby_phase", "during the Standby Phase"),
        new("summon_normal", "when your opponent Normal Summons a monster"),
        new("summon_special", "when your opponent Special Summons a monster(s)"),
        new("summon_flip", "when your opponent Flip Summons a monster"),
        new("summon_you_normal", "when you Normal Summon a monster"),
        new("summon_you_special", "when you Special Summon a monster(s)"),
        new("any_normal", "when a monster is Normal Summoned"),
        new("any_flip", "when a monster is Flip Summoned"),
        new("any_special", "when a monster(s) is Special Summoned"),
        new("chain_any", "when your opponent activates a card or effect"),
        new("chain_monster", "when your opponent activates a monster effect"),
        new("chain_monster_field", "when your opponent activates a monster effect on the field"),
        new("chain_spelltrap", "when your opponent activates a Spell/Trap Card"),
        new("chain_spell", "when your opponent activates a Spell Card"),
        new("chain_trap", "when your opponent activates a Trap Card"),
        new("chain_either", "when a card or effect is activated"),
        new("chain_monster_any", "when a monster effect is activated"),
        new("chain_spelltrap_any", "when a Spell/Trap Card is activated"),
        new("chain_spell_any", "when a Spell Card is activated"),
        new("chain_trap_any", "when a Trap Card is activated"),
        new("chain_summons", "when a card or effect is activated that includes an effect that Special Summons a monster(s)"),
        new("chain_searches", "when a card or effect is activated that includes an effect that adds a card(s) from the Deck to the hand"),
        new("chain_destroys", "when a card or effect is activated that includes an effect that destroys a card(s)"),
        new("would_normal", "when your opponent would Normal Summon a monster"),
        new("would_special", "when your opponent would Special Summon a monster(s)"),
        new("would_flip", "when your opponent would Flip Summon a monster"),
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
        or "summon_flip" or "would_summon" or "would_special" or "would_normal" or "would_flip" or "chain_any" or "chain_monster" or "chain_monster_field" or "chain_spelltrap" or "chain_spell" or "chain_trap";

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
            case "summon_you_normal":
                e.Code = "EVENT_SUMMON_SUCCESS";
                e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,tp)");
                break;
            case "summon_you_special":
                e.Code = "EVENT_SPSUMMON_SUCCESS";
                e.Conditions.Add("eg:IsExists(Card.IsSummonPlayer,1,nil,tp)");
                break;
            case "any_normal": e.Code = "EVENT_SUMMON_SUCCESS"; break;
            case "any_flip": e.Code = "EVENT_FLIP_SUMMON_SUCCESS"; break;
            case "any_special": e.Code = "EVENT_SPSUMMON_SUCCESS"; break;
            case "would_normal":
                e.Code = "EVENT_SUMMON";
                e.Conditions.Add("Duel.GetCurrentChain()==0 and eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)");
                break;
            case "would_flip":
                e.Code = "EVENT_FLIP_SUMMON";
                e.Conditions.Add("Duel.GetCurrentChain()==0 and eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)");
                break;
            case "main_phase":
                // "During the Main Phase (Quick Effect)": either player's Main Phase.
                e.Code = "EVENT_FREE_CHAIN";
                e.HintTiming = "TIMINGS_CHECK_MONSTER+TIMING_MAIN_END";
                e.Conditions.Add("(Duel.GetCurrentPhase()==PHASE_MAIN1 or Duel.GetCurrentPhase()==PHASE_MAIN2)");
                break;
            case "standby_phase":
                e.Code = "EVENT_FREE_CHAIN";
                e.HintTiming = "TIMING_STANDBY_PHASE";
                e.Conditions.Add("Duel.GetCurrentPhase()==PHASE_STANDBY");
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
            case "chain_monster_any":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("re:IsActiveType(TYPE_MONSTER)");
                break;
            case "chain_spelltrap_any":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("re:IsHasType(EFFECT_TYPE_ACTIVATE)");
                break;
            case "chain_spell_any":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:IsActiveType(TYPE_SPELL)");
                break;
            case "chain_trap_any":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:IsActiveType(TYPE_TRAP)");
                break;
            case "chain_summons":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("re:IsHasCategory(CATEGORY_SPECIAL_SUMMON)");
                break;
            case "chain_searches":
                e.Code = "EVENT_CHAINING";
                // Drawing adds cards from the Deck to the hand too (Dominus Purge).
                e.Conditions.Add("(re:IsHasCategory(CATEGORY_SEARCH) or re:IsHasCategory(CATEGORY_DRAW))");
                break;
            case "chain_monster_field":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("rp==1-tp and re:IsActiveType(TYPE_MONSTER) and re:GetActivateLocation()&LOCATION_ONFIELD~=0");
                break;
            case "chain_destroys":
                e.Code = "EVENT_CHAINING";
                e.Conditions.Add("re:IsHasCategory(CATEGORY_DESTROY)");
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
                // Like official cards: tells the game when to offer the effect, including during the Main Phase and End Phase.
                e.HintTiming ??= "TIMINGS_CHECK_MONSTER+TIMING_MAIN_END+TIMING_END_PHASE";
                break;
        }
    }

    /// <summary>"When your opponent activates a monster effect": the timing's wording at the start of the card text.</summary>
    public static string Phrase(string timing) =>
        timing == "any" || Choices.FirstOrDefault(c => c.Value == timing) is not { } choice ? "" : char.ToUpperInvariant(choice.Label[0]) + choice.Label[1..];
}

public sealed class ActivateTrigger : BlockDefinition
{
    public override string Id => "when.activate";
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "You choose";
    public override string Name => "Activate this Spell/Trap";
    public override string Sentence => "Activate this card {timing}";
    public override string Help => "The effect a Spell or Trap card has when it is activated.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("timing", "Timing", Timing.Choices),
        ParamSpec.Flag("damage_step", "can be activated in the Damage Step", false),
    ];

    public override string Describe(BlockArgs args) => Timing.Phrase(args.Choice("timing"));

    public override EventPhrase? Phrase(BlockArgs args) => Timing.PhraseOf(args.Choice("timing"));

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Card.IsMonster) ctx.Effect.Error("\"Activate this card\" is for Spell and Trap cards. Monsters use \"You activate it\" or a Quick Effect.");
        ctx.Effect.Types.Add("EFFECT_TYPE_ACTIVATE");
        ctx.Effect.IsActivation = true;
        Timing.Apply(ctx, ctx.Args.Choice("timing"));
        if (ctx.Args.Bool("damage_step"))
        {
            ctx.Effect.AddProperty("EFFECT_FLAG_DAMAGE_STEP");
            ctx.Effect.Conditions.Add("aux.dscon(e,tp,eg,ep,ev,re,r,rp)");
        }
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
        ParamSpec.Flag("damage_step", "can be used in the Damage Step", false),
    ];

    /// <summary>Only the timing; the text generator adds "(Quick Effect)" after any conditions.</summary>
    public override string Describe(BlockArgs args) => Timing.Phrase(args.Choice("timing"));

    public override EventPhrase? Phrase(BlockArgs args) => Timing.PhraseOf(args.Choice("timing"));

    public override void Compile(BlockContext ctx)
    {
        ctx.Effect.Types.Add("EFFECT_TYPE_QUICK_O");
        ctx.Effect.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
        Timing.Apply(ctx, ctx.Args.Choice("timing"));
        if (ctx.Args.Bool("damage_step"))
        {
            // Up to damage calculation, like ATK-changing Quick Effects.
            ctx.Effect.AddProperty("EFFECT_FLAG_DAMAGE_STEP");
            ctx.Effect.Conditions.Add("aux.dscon(e,tp,eg,ep,ev,re,r,rp)");
        }
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
    /// <summary>One thing that can happen in the duel: its key, card text, event and check.</summary>
    /// <param name="Summon">For Summons: who Summons ("opp", "you", "any") and how ("Normal"), so they combine as "Normal or Special".</param>
    public sealed record DuelEvent(string Key, string Text, string Code, string? Check = null, (string Who, string How)? Summon = null);

    public static readonly DuelEvent[] Events =
    [
        new("opp_normal", "your opponent Normal Summons a monster", "EVENT_SUMMON_SUCCESS", "eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)", ("opp", "Normal")),
        new("opp_special", "your opponent Special Summons a monster(s)", "EVENT_SPSUMMON_SUCCESS", "eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)", ("opp", "Special")),
        new("opp_flip", "your opponent Flip Summons a monster", "EVENT_FLIP_SUMMON_SUCCESS", "eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)", ("opp", "Flip")),
        new("you_normal", "you Normal Summon a monster", "EVENT_SUMMON_SUCCESS", "eg:IsExists(Card.IsSummonPlayer,1,nil,tp)", ("you", "Normal")),
        new("you_special", "you Special Summon a monster(s)", "EVENT_SPSUMMON_SUCCESS", "eg:IsExists(Card.IsSummonPlayer,1,nil,tp)", ("you", "Special")),
        new("you_flip", "you Flip Summon a monster", "EVENT_FLIP_SUMMON_SUCCESS", "eg:IsExists(Card.IsSummonPlayer,1,nil,tp)", ("you", "Flip")),
        new("any_normal", "a monster is Normal Summoned", "EVENT_SUMMON_SUCCESS", null, ("any", "Normal")),
        new("any_special", "a monster(s) is Special Summoned", "EVENT_SPSUMMON_SUCCESS", null, ("any", "Special")),
        new("any_flip", "a monster is Flip Summoned", "EVENT_FLIP_SUMMON_SUCCESS", null, ("any", "Flip")),
        new("opp_sets_monster", "your opponent Sets a monster", "EVENT_MSET", "eg:IsExists(Card.IsControler,1,nil,1-tp)"),
        new("opp_sets_spell_trap", "your opponent Sets a Spell/Trap Card(s)", "EVENT_SSET", "eg:IsExists(Card.IsControler,1,nil,1-tp)"),
        new("your_destroyed", "a monster(s) you control is destroyed", "EVENT_DESTROYED", "@desfilter:c:IsPreviousControler(tp) and c:IsPreviousLocation(LOCATION_MZONE)"),
        new("opp_destroyed", "a monster(s) your opponent controls is destroyed", "EVENT_DESTROYED", "@desfilter:c:IsPreviousControler(1-tp) and c:IsPreviousLocation(LOCATION_MZONE)"),
        new("your_battle_destroyed", "a monster(s) you control is destroyed by battle", "EVENT_BATTLE_DESTROYED", "@desfilter:c:IsPreviousControler(tp)"),
        new("opp_battle_destroyed", "a monster(s) your opponent controls is destroyed by battle", "EVENT_BATTLE_DESTROYED", "@desfilter:c:IsPreviousControler(1-tp)"),
        new("your_spell_trap_destroyed", "a Spell/Trap Card(s) you control is destroyed", "EVENT_DESTROYED", "@desfilter:c:IsPreviousControler(tp) and c:IsPreviousLocation(LOCATION_SZONE)"),
        new("opp_to_gy", "a card(s) is sent to your opponent's GY", "EVENT_TO_GRAVE", "eg:IsExists(Card.IsControler,1,nil,1-tp)"),
        new("your_to_gy", "a card(s) is sent to your GY", "EVENT_TO_GRAVE", "eg:IsExists(Card.IsControler,1,nil,tp)"),
        new("opp_discard", "a card(s) is discarded from your opponent's hand", "EVENT_DISCARD", "@dcfilter:c:IsPreviousControler(1-tp)"),
        new("your_discard", "a card(s) is discarded from your hand", "EVENT_DISCARD", "@dcfilter:c:IsPreviousControler(tp)"),
        new("banished_any", "a card(s) is banished", "EVENT_REMOVE"),
        new("opp_banished", "a card(s) your opponent controls or owns is banished", "EVENT_REMOVE", "@rmfilter:c:IsPreviousControler(1-tp)"),
        new("returned_hand", "a card(s) on the field is returned to the hand", "EVENT_TO_HAND", "@thfilter:c:IsPreviousLocation(LOCATION_ONFIELD)"),
        new("opp_search", "your opponent adds a card(s) from their Deck to their hand", "EVENT_TO_HAND",
            "@thfilter:c:IsControler(1-tp) and c:IsPreviousLocation(LOCATION_DECK) and not c:IsReason(REASON_DRAW)"),
        new("you_search", "you add a card(s) from your Deck to your hand", "EVENT_TO_HAND",
            "@thfilter:c:IsControler(tp) and c:IsPreviousLocation(LOCATION_DECK) and not c:IsReason(REASON_DRAW)"),
        new("opp_draw", "your opponent draws a card(s)", "EVENT_DRAW", "ep==1-tp"),
        new("you_draw", "you draw a card(s)", "EVENT_DRAW", "ep==tp"),
        new("you_damage", "you take damage", "EVENT_DAMAGE", "ep==tp"),
        new("opp_damage", "your opponent takes damage", "EVENT_DAMAGE", "ep==1-tp"),
        new("you_effect_damage", "you take effect damage", "EVENT_DAMAGE", "ep==tp and bit.band(r,REASON_EFFECT)~=0"),
        new("opp_effect_damage", "your opponent takes effect damage", "EVENT_DAMAGE", "ep==1-tp and bit.band(r,REASON_EFFECT)~=0"),
        new("you_damage_by_opp", "you take damage from your opponent's card effect", "EVENT_DAMAGE", "ep==tp and bit.band(r,REASON_EFFECT)~=0 and rp==1-tp"),
        new("any_damage", "a player takes damage", "EVENT_DAMAGE"),
        new("you_pay_lp", "you pay LP", "EVENT_PAY_LPCOST", "ep==tp"),
        new("opp_pay_lp", "your opponent pays LP", "EVENT_PAY_LPCOST", "ep==1-tp"),
        new("you_battle_damage", "you take battle damage", "EVENT_BATTLE_DAMAGE", "ep==tp"),
        new("opp_battle_damage", "your opponent takes battle damage", "EVENT_BATTLE_DAMAGE", "ep==1-tp"),
        new("you_gain_lp", "you gain LP", "EVENT_RECOVER", "ep==tp"),
        new("opp_gain_lp", "your opponent gains LP", "EVENT_RECOVER", "ep==1-tp"),
        new("any_gain_lp", "a player gains LP", "EVENT_RECOVER"),
        new("opp_attack", "an opponent's monster declares an attack", "EVENT_ATTACK_ANNOUNCE", "Duel.GetAttacker():IsControler(1-tp)"),
        new("opp_direct_attack", "an opponent's monster declares a direct attack", "EVENT_ATTACK_ANNOUNCE", "Duel.GetAttacker():IsControler(1-tp) and Duel.GetAttackTarget()==nil"),
        new("your_attack", "a monster you control declares an attack", "EVENT_ATTACK_ANNOUNCE", "Duel.GetAttacker():IsControler(tp)"),
        new("your_attacked", "a monster you control is targeted for an attack", "EVENT_BE_BATTLE_TARGET", "Duel.GetAttackTarget()~=nil and Duel.GetAttackTarget():IsControler(tp)"),
        new("your_leaves", "a monster(s) you control leaves the field", "EVENT_LEAVE_FIELD", "@lvfilter:c:IsPreviousControler(tp) and c:IsPreviousLocation(LOCATION_MZONE)"),
        new("opp_leaves", "a monster(s) your opponent controls leaves the field", "EVENT_LEAVE_FIELD", "@lvfilter:c:IsPreviousControler(1-tp) and c:IsPreviousLocation(LOCATION_MZONE)"),
        new("control_changes", "control of a monster changes", "EVENT_CONTROL_CHANGED"),
        new("position_changes", "a monster's battle position is changed", "EVENT_CHANGE_POS"),
        new("your_targeted", "a card(s) you control is targeted by an opponent's card effect", "EVENT_BECOME_TARGET",
            "rp==1-tp and @tgfilter:c:IsControler(tp) and c:IsOnField()"),
        new("opp_activates", "your opponent activates a card or effect", "EVENT_CHAINING", "rp==1-tp"),
        new("opp_monster_effect", "your opponent activates a monster effect", "EVENT_CHAINING", "rp==1-tp and re:IsActiveType(TYPE_MONSTER)"),
        new("opp_spell_trap_effect", "your opponent activates a Spell/Trap Card or effect", "EVENT_CHAINING", "rp==1-tp and re:IsActiveType(TYPE_SPELL+TYPE_TRAP)"),
        new("opp_spell_card", "your opponent activates a Spell Card", "EVENT_CHAINING", "rp==1-tp and re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:IsActiveType(TYPE_SPELL)"),
        new("opp_trap_card", "your opponent activates a Trap Card", "EVENT_CHAINING", "rp==1-tp and re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:IsActiveType(TYPE_TRAP)"),
        new("you_activate", "you activate a card or effect", "EVENT_CHAINING", "rp==tp"),
    ];

    public static DuelEvent Find(string key) => Events.FirstOrDefault(e => e.Key == key) ?? Events[0];

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("event", "What happens", Events.Select(e => new ChoiceOption(e.Key, e.Text)).ToArray()),
        ParamSpec.Choice("zone", "This card is", Zones.RangeChoices),
        ParamSpec.Flag("optional", "optional (\"You can\")", true),
    ];

    public override string Describe(BlockArgs args) => "If " + args.Describe("event");

    /// <summary>"your opponent Normal or Special Summons a monster(s)".</summary>
    public override EventPhrase? Phrase(BlockArgs args)
    {
        var e = Find(args.Choice("event"));
        return e.Summon switch
        {
            ("opp", var how) => new("If", "your opponent {0} Summons a monster(s)", how, e.Text),
            ("you", var how) => new("If", "you {0} Summon a monster(s)", how, e.Text),
            ({ } _, var how) => new("If", "a monster(s) is {0} Summoned", how, e.Text),
            _ => new("If", e.Text),
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var optional = ctx.Args.Bool("optional");
        e.Types.Add("EFFECT_TYPE_FIELD");
        e.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
        e.AddProperty("EFFECT_FLAG_DELAY"); // "If" wording: it can't miss its timing.
        e.IsMandatory = !optional;
        e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);
        var ev = Find(ctx.Args.Choice("event"));
        e.Code = ev.Code;
        if (ev.Check is not { } check) return;
        // "@name:test" checks the cards of the event with a filter function "name".
        var at = check.IndexOf('@');
        if (at >= 0)
        {
            var colon = check.IndexOf(':', at);
            var fn = ctx.Script.AddFunction(check[(at + 1)..colon], "c,tp", [$"return {check[(colon + 1)..]}"]);
            check = check[..at] + $"eg:IsExists({fn},1,nil,tp)";
        }
        e.Conditions.Add(check);
    }
}

/// <summary>
/// "If a Dragon monster(s) is Special Summoned to your field": a trigger on other cards, picked by a card filter.
/// Who controls them comes from the filter's owner; "other" in the filter leaves this card out.
/// </summary>
public sealed class CardsEventTrigger : BlockDefinition
{
    public const string BlockId = "when.cards_event";
    public override string Id => BlockId;
    public override BlockKind Kind => BlockKind.Trigger;
    public override string Group => "Something happens";
    public override string Name => "Matching cards are Summoned, destroyed or sent to the GY";
    public override string Sentence => "If {cards} is {event}, {zone}";
    public override string Help => "For example: \"If a Dragon monster(s) is Special Summoned to your field\" or \"If a \"Blue-Eyes\" monster(s) you control is destroyed\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }, Zone.MonsterZone),
        ParamSpec.Choice("event", "What happens to them",
            new("normal", "Normal Summoned"),
            new("special", "Special Summoned"),
            new("flip", "Flip Summoned"),
            new("tribute", "Tribute Summoned"),
            new("ritual", "Ritual Summoned"),
            new("fusion", "Fusion Summoned"),
            new("synchro", "Synchro Summoned"),
            new("xyz", "Xyz Summoned"),
            new("pendulum", "Pendulum Summoned"),
            new("link", "Link Summoned"),
            new("destroyed", "destroyed"),
            new("destroyed_battle", "destroyed by battle"),
            new("destroyed_effect", "destroyed by a card effect"),
            new("destroyed_opp", "destroyed by an opponent's card effect"),
            new("destroyed_battle_opp", "destroyed by battle or an opponent's card effect"),
            new("to_gy", "sent to the GY"),
            new("banished", "banished"),
            new("tributed", "Tributed"),
            new("to_hand", "returned to the hand"),
            new("leaves", "leaves the field", "leaves the field")),
        ParamSpec.Choice("zone", "This card is", Zones.RangeChoices),
        ParamSpec.Flag("optional", "optional (\"You can\")", true),
    ];

    private static bool IsSummon(string ev) => ev is "normal" or "special" or "flip" or "tribute" or "ritual" or "fusion" or "synchro" or "xyz" or "pendulum" or "link";

    private static string Subject(CardFilter f, CardPack? pack)
    {
        var noun = FilterText.Describe(f, pack, false, "", withLocation: false, withExcept: false) + "(s)";
        if (f.ExcludeThisCard && noun.StartsWith("other ", StringComparison.Ordinal)) noun = noun[6..];
        var article = f.ExcludeThisCard ? "another" : f.CardCode != 0 ? "" : "AEIOUaeiou".Contains(noun[0]) ? "an" : "a";
        return (article.Length > 0 ? article + " " : "") + noun;
    }

    /// <summary>The wording with "{0}" where the event goes, and the event's own words.</summary>
    private static (string Template, string Label) Words(BlockArgs args)
    {
        var f = args.Cards("cards");
        var ev = args.Choice("event");
        var subject = Subject(f, args.Pack);
        if (IsSummon(ev))
        {
            var where = f.Owner switch { CardOwner.You => " to your field", CardOwner.Opponent => " to your opponent's field", _ => "" };
            return ($"{subject} is {{0}} Summoned{where}", args.Describe("event").Replace(" Summoned", "", StringComparison.Ordinal));
        }
        if (ev == "to_gy")
        {
            var gy = f.Owner switch { CardOwner.You => "your GY", CardOwner.Opponent => "your opponent's GY", _ => "the GY" };
            return ($"{subject} is sent to {gy}", "");
        }
        var controls = f.Owner switch { CardOwner.You => " you control", CardOwner.Opponent => " your opponent controls", _ => " on the field" };
        if (ev == "leaves") return ($"{subject}{controls} leaves the field", "");
        return ($"{subject}{controls} is {{0}}", args.Describe("event"));
    }

    public override string Describe(BlockArgs args)
    {
        var (template, label) = Words(args);
        return "If " + template.Replace("{0}", label, StringComparison.Ordinal);
    }

    public override EventPhrase? Phrase(BlockArgs args)
    {
        var (template, label) = Words(args);
        return new("If", template, label);
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var optional = ctx.Args.Bool("optional");
        e.Types.Add("EFFECT_TYPE_FIELD");
        e.Types.Add(optional ? "EFFECT_TYPE_TRIGGER_O" : "EFFECT_TYPE_TRIGGER_F");
        e.AddProperty("EFFECT_FLAG_DELAY");
        e.IsMandatory = !optional;
        e.Range = Zones.Resolve(ctx.Args.Choice("zone"), ctx.Card);

        var f = ctx.Args.Cards("cards");
        var ev = ctx.Args.Choice("event");
        var parts = FilterCompiler.Conditions(new CardFilter
        {
            CardType = f.CardType, MonsterKind = f.MonsterKind, Race = f.Race, Attribute = f.Attribute, Archetype = f.Archetype, Archetype2 = f.Archetype2,
            CardCode = f.CardCode, AltCodes = f.AltCodes, ExceptOwnName = f.ExceptOwnName, ExceptCode = f.ExceptCode, LevelMin = f.LevelMin, LevelMax = f.LevelMax,
            AtkMin = f.AtkMin, AtkMax = f.AtkMax, DefMin = f.DefMin, DefMax = f.DefMax, MentionsCode = f.MentionsCode, MentionsOrIs = f.MentionsOrIs,
            Requirements = f.Requirements,
        });
        if (IsSummon(ev))
        {
            if (f.FaceUpOnly) parts.Add("c:IsFaceup()");
            if (f.Owner != CardOwner.Either) parts.Add(f.Owner == CardOwner.You ? "c:IsControler(tp)" : "c:IsControler(1-tp)");
        }
        else if (ev == "to_gy")
        {
            if (f.Owner != CardOwner.Either) parts.Add(f.Owner == CardOwner.You ? "c:IsControler(tp)" : "c:IsControler(1-tp)");
        }
        else
        {
            if (f.Owner != CardOwner.Either) parts.Add(f.Owner == CardOwner.You ? "c:IsPreviousControler(tp)" : "c:IsPreviousControler(1-tp)");
            // Spells and Traps leave the field too: "if another card(s) you control is destroyed".
            parts.Add(f.CardType == FilterCardType.Monster ? "c:IsPreviousLocation(LOCATION_MZONE)" : "c:IsPreviousLocation(LOCATION_ONFIELD)");
            if (f.FaceUpOnly) parts.Add("c:IsPreviousPosition(POS_FACEUP)");
            if (ev == "destroyed_effect") parts.Add("c:IsReason(REASON_EFFECT)");
            if (ev == "destroyed_opp") parts.Add("c:IsReason(REASON_EFFECT) and c:GetReasonPlayer()==1-tp");
            if (ev == "destroyed_battle_opp") parts.Add("(c:IsReason(REASON_BATTLE) or c:IsReason(REASON_EFFECT) and c:GetReasonPlayer()==1-tp)");
        }
        switch (ev)
        {
            case "normal": e.Code = "EVENT_SUMMON_SUCCESS"; break;
            case "flip": e.Code = "EVENT_FLIP_SUMMON_SUCCESS"; break;
            case "tribute": e.Code = "EVENT_SUMMON_SUCCESS"; parts.Add("c:IsSummonType(SUMMON_TYPE_ADVANCE)"); break;
            case "special": e.Code = "EVENT_SPSUMMON_SUCCESS"; break;
            case "destroyed" or "destroyed_effect" or "destroyed_opp": e.Code = "EVENT_DESTROYED"; break;
            // Battle destruction happens in the Damage Step.
            case "destroyed_battle_opp": e.Code = "EVENT_DESTROYED"; e.AddProperty("EFFECT_FLAG_DAMAGE_STEP"); break;
            case "destroyed_battle": e.Code = "EVENT_BATTLE_DESTROYED"; break;
            case "to_gy": e.Code = "EVENT_TO_GRAVE"; break;
            case "banished": e.Code = "EVENT_REMOVE"; break;
            case "tributed": e.Code = "EVENT_RELEASE"; break;
            case "to_hand": e.Code = "EVENT_TO_HAND"; break;
            case "leaves": e.Code = "EVENT_LEAVE_FIELD"; break;
            default:
                e.Code = "EVENT_SPSUMMON_SUCCESS";
                parts.Add($"c:IsSummonType(SUMMON_TYPE_{ev.ToUpperInvariant()})");
                break;
        }
        if (f.ExcludeThisCard) parts.Add("c~=e:GetHandler()");
        var fn = ctx.Script.AddFunction("evfilter", "c,e,tp", [$"return {(parts.Count == 0 ? "true" : string.Join(" and ", parts))}"]);
        e.Conditions.Add($"eg:IsExists({fn},1,nil,e,tp)");
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

/// <summary>
/// "If this card is Normal or Special Summoned, or if this card is Tributed or banished": any mix of the things that
/// can happen to this card, each ticked on its own. One effect starts on each of them and shares its once per turn.
/// </summary>
public sealed class ThisCardEventsTrigger : SelfTriggerBlock
{
    public const string BlockId = "when.this_card_events";

    /// <summary>One thing that can happen to this card: its tick box, how card text says it, its event and its check.</summary>
    public sealed record Event(string Key, string Label, string Code, string? Check = null, bool Monster = false);

    /// <summary>Summons read together: "Normal, Flip, or Special Summoned".</summary>
    public static readonly Event[] Summons =
    [
        new("normal", "Normal", "EVENT_SUMMON_SUCCESS", Monster: true),
        new("tribute_summon", "Tribute", "EVENT_SUMMON_SUCCESS", "e:GetHandler():IsSummonType(SUMMON_TYPE_ADVANCE)", true),
        new("flip_summon", "Flip", "EVENT_FLIP_SUMMON_SUCCESS", Monster: true),
        new("special", "Special", "EVENT_SPSUMMON_SUCCESS", Monster: true),
        new("ritual", "Ritual", "EVENT_SPSUMMON_SUCCESS", "e:GetHandler():IsSummonType(SUMMON_TYPE_RITUAL)", true),
        new("fusion", "Fusion", "EVENT_SPSUMMON_SUCCESS", "e:GetHandler():IsSummonType(SUMMON_TYPE_FUSION)", true),
        new("synchro", "Synchro", "EVENT_SPSUMMON_SUCCESS", "e:GetHandler():IsSummonType(SUMMON_TYPE_SYNCHRO)", true),
        new("xyz", "Xyz", "EVENT_SPSUMMON_SUCCESS", "e:GetHandler():IsSummonType(SUMMON_TYPE_XYZ)", true),
        new("link", "Link", "EVENT_SPSUMMON_SUCCESS", "e:GetHandler():IsSummonType(SUMMON_TYPE_LINK)", true),
        new("pendulum", "Pendulum", "EVENT_SPSUMMON_SUCCESS", "e:GetHandler():IsSummonType(SUMMON_TYPE_PENDULUM)", true),
    ];

    /// <summary>What happens to this card: "If this card is Tributed, sent to the GY, or banished".</summary>
    public static readonly Event[] Happenings =
    [
        new("special_gy", "Special Summoned from the GY", "EVENT_SPSUMMON_SUCCESS", "e:GetHandler():IsSummonLocation(LOCATION_GRAVE)", true),
        new("special_hand", "Special Summoned from the hand", "EVENT_SPSUMMON_SUCCESS", "e:GetHandler():IsSummonLocation(LOCATION_HAND)", true),
        new("tributed", "Tributed", "EVENT_RELEASE"),
        new("sent_gy", "sent to the GY", "EVENT_TO_GRAVE"),
        new("sent_gy_field", "sent from the field to the GY", "EVENT_TO_GRAVE", "e:GetHandler():IsPreviousLocation(LOCATION_ONFIELD)"),
        new("sent_gy_hand", "sent from the hand to the GY", "EVENT_TO_GRAVE", "e:GetHandler():IsPreviousLocation(LOCATION_HAND)"),
        new("sent_gy_deck", "sent from the Deck to the GY", "EVENT_TO_GRAVE", "e:GetHandler():IsPreviousLocation(LOCATION_DECK)"),
        new("destroyed", "destroyed", "EVENT_DESTROYED"),
        new("destroyed_battle", "destroyed by battle", "EVENT_BATTLE_DESTROYED", Monster: true),
        new("destroyed_effect", "destroyed by a card effect", "EVENT_DESTROYED", "e:GetHandler():IsReason(REASON_EFFECT)"),
        new("destroyed_opponent", "destroyed by an opponent's card", "EVENT_DESTROYED", "rp==1-tp and e:GetHandler():IsPreviousControler(tp)"),
        new("banished", "banished", "EVENT_REMOVE"),
        new("returned_hand", "returned from the field to the hand", "EVENT_TO_HAND", "e:GetHandler():IsPreviousLocation(LOCATION_ONFIELD)"),
        new("added_hand", "added to your hand, except by drawing it", "EVENT_TO_HAND", "not e:GetHandler():IsReason(REASON_DRAW)"),
        new("returned_deck", "shuffled into the Deck", "EVENT_TO_DECK"),
        new("discarded", "discarded", "EVENT_DISCARD"),
        new("flipped", "flipped face-up", "EVENT_FLIP", Monster: true),
        new("targeted", "targeted by a card effect", "EVENT_BECOME_TARGET"),
        new("material", "used as material", "EVENT_BE_MATERIAL", Monster: true),
    ];

    /// <summary>What this card does: "or if this card leaves the field".</summary>
    public static readonly Event[] Doings =
    [
        new("leaves_field", "leaves the field", "EVENT_LEAVE_FIELD"),
        new("battle_damage", "inflicts battle damage to your opponent", "EVENT_BATTLE_DAMAGE", "ep~=tp", true),
        new("destroys_battle", "destroys an opponent's monster by battle", "EVENT_BATTLE_DESTROYING", "aux.bdocon(e,tp,eg,ep,ev,re,r,rp)", true),
    ];

    public static IEnumerable<Event> All => Summons.Concat(Happenings).Concat(Doings);

    public override string Id => BlockId;
    /// <summary>Replaced by one WHEN per event with "Or when"; older packs are split into those when they load.</summary>
    public override bool Hidden => true;
    public override string Name => "This card: any of these happen (tick several)";
    public override string Help => "Tick everything that starts this effect, like Mitsurugi's \"If this card is Normal or Special Summoned, or if this card is " +
                                   "Tributed\" or \"If this card is sent to the GY or banished\". It is one effect: its once per turn covers all of them.";
    public override string Sentence => "If this card is Summoned: " + string.Join(" ", Summons.Select(e => "{" + e.Key + "}"))
        + " · or if it is: " + string.Join(" ", Happenings.Select(e => "{" + e.Key + "}"))
        + " · or if it: " + string.Join(" ", Doings.Select(e => "{" + e.Key + "}"));

    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        .. Summons.Select(e => ParamSpec.Flag(e.Key, e.Label, e.Key == "normal" || e.Key == "special")),
        .. Happenings.Select(e => ParamSpec.Flag(e.Key, e.Label, false)),
        .. Doings.Select(e => ParamSpec.Flag(e.Key, e.Label, false)),
        OptionalParam,
    ];

    public static List<Event> Ticked(BlockArgs args) => All.Where(e => args.Bool(e.Key)).ToList();

    /// <summary>"A or B", "A, B, or C".</summary>
    private static string Or(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} or {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + ", or " + items[^1],
    };

    public override string Describe(BlockArgs args)
    {
        var ticked = Ticked(args);
        var clauses = new List<string>();
        var summons = Summons.Where(e => ticked.Contains(e)).Select(e => e.Label).ToList();
        var happenings = Happenings.Where(e => ticked.Contains(e)).Select(e => e.Label).ToList();
        // "destroyed by battle or card effect".
        if (happenings.Contains("destroyed by battle") && happenings.Contains("destroyed by a card effect"))
        {
            var at = happenings.IndexOf("destroyed by battle");
            happenings.Remove("destroyed by a card effect");
            happenings[at] = "destroyed by battle or card effect";
        }
        if (summons.Count > 0) clauses.Add($"this card is {Or(summons)} Summoned");
        if (happenings.Count > 0) clauses.Add($"this card is {Or(happenings)}");
        clauses.AddRange(Doings.Where(e => ticked.Contains(e)).Select(e => $"this card {e.Label}"));
        return clauses.Count == 0 ? "If (tick what happens to this card)" : "If " + string.Join(", or if ", clauses);
    }

    public override void Compile(BlockContext ctx)
    {
        var ticked = Ticked(ctx.Args);
        if (ticked.Count == 0)
        {
            ctx.Effect.Error($"\"{Name}\": tick at least one thing that starts the effect.");
            return;
        }
        if (ticked.Any(e => e.Monster)) Zones.RequireMonster(ctx, Name);
        // One event per code; a code whose ticks all have checks only starts the effect when one of them holds.
        var codes = ticked.GroupBy(e => e.Code).ToList();
        Setup(ctx, codes[0].Key, codes.Skip(1).Select(g => g.Key).ToArray());
        string? Check(IGrouping<string, Event> g) => g.Any(e => e.Check is null) ? null : string.Join(" or ", g.Select(e => $"({e.Check})"));
        if (codes.Count == 1)
        {
            if (Check(codes[0]) is { } only) ctx.Effect.Conditions.Add(codes[0].Count() == 1 ? codes[0].First().Check! : $"({only})");
        }
        else if (codes.Any(g => Check(g) is not null))
        {
            // Each copy of the effect has its own code: e:GetCode() says which event started it.
            ctx.Effect.Conditions.Add("(" + string.Join(" or ", codes.Select(g => Check(g) is { } c ? $"(e:GetCode()=={g.Key} and ({c}))" : $"e:GetCode()=={g.Key}")) + ")");
        }
    }
}

internal static partial class TimingPhrases
{
    /// <summary>"When your opponent Normal or Special Summons a monster(s)": Summon timings combine; the others follow with ", or when".</summary>
    public static EventPhrase? Of(string timing)
    {
        if (timing == "any") return null;
        var label = Timing.Choices.First(c => c.Value == timing).Label;
        var m = System.Text.RegularExpressions.Regex.Match(label, @"^when (your opponent|you|a monster\(s\)|a monster) (would |is )?(Normal|Special|Flip) (Summons?|Summoned)(?: a monster(?:\(s\))?)?$");
        if (m.Success)
        {
            var subject = m.Groups[1].Value.StartsWith("a monster", StringComparison.Ordinal) ? "a monster(s) is {0} Summoned"
                : $"{m.Groups[1].Value} {m.Groups[2].Value}{{0}} {(m.Groups[1].Value == "your opponent" && m.Groups[2].Value != "would " ? "Summons" : "Summon")} a monster(s)";
            return new("When", subject, m.Groups[3].Value, label["when ".Length..]);
        }
        var space = label.IndexOf(' ');
        return new(char.ToUpperInvariant(label[0]) + label[1..space], label[(space + 1)..]);
    }
}
