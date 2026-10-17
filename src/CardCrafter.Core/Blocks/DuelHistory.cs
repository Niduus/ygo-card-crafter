using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// What happened earlier in the turn or the Duel that the game doesn't remember by itself: a card sent to the GY or
// activated (the Sun God cards, Heart of the Blue-Eyes), this card's own activation (Wicked Canon), and whether this card
// was already face-up (Kashtira Preparations). Watchers and flags remember them, as official scripts do.

/// <summary>
/// "If "The Immortal Sun God" was sent to your GY this turn", "and you have successfully activated "Millennium Ankh" this
/// Duel": a certain card, picked by the card picker, that went to the GY or was activated. A watcher for the whole Duel
/// marks the player whose GY it went to, or who activated it.
/// </summary>
public sealed class CardHistoryCondition : ConditionBlock
{
    public const string BlockId = "if.card_history";
    public override string Id => BlockId;
    public override string Group => "Turn";
    public override string Name => "A certain card was sent to your GY, or activated, this turn or this Duel";
    public override string Sentence => "{card} {event} {when}";
    public override string Help => "Pick the card by name or archetype. Counted from when the first copy of this card is in the Duel. " +
                                   "\"Successfully activated\" means its activation resolved without being negated.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        new ParamSpec
        {
            Key = "card", Label = "Which card", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Any },
        },
        ParamSpec.Choice("event", "What happened",
            new("to_gy", "was sent to your GY"),
            new("activated", "was activated by you", "you have successfully activated it (the card, not an effect)")),
        ParamSpec.Choice("when", "When", new("turn", "this turn"), new("duel", "this Duel")),
    ];

    /// <summary>""The Immortal Sun God"", "a "Sun God" card".</summary>
    private static string Noun(BlockArgs args)
    {
        var f = args.Cards("card");
        var noun = FilterText.Describe(f, args.Pack, plural: false, withLocation: false, withExcept: false);
        return f.CardCode != 0 ? noun : $"{SummonWording.Article(noun)} {noun}";
    }

    public override string Describe(BlockArgs args)
    {
        var when = args.Describe("when");
        return args.Choice("event") == "activated"
            ? $"you have successfully activated {Noun(args)} {when}"
            : args.Choice("when") == "duel" ? $"{Noun(args)} has been sent to your GY {when}" : $"{Noun(args)} was sent to your GY {when}";
    }

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Args.Cards("card").Clone();
        f.Zones = Zone.None;
        f.Owner = CardOwner.Either;
        f.ExcludeThisCard = false;
        var filter = ctx.Script.Filter(f, null, "histfilter").Function;
        var ev = ctx.Args.Choice("event");
        var duel = ctx.Args.Choice("when") == "duel";
        var slot = ctx.Script.Slot($"history:{ev}:{filter}:{duel}");
        if (slot >= 20) ctx.Effect.Error($"\"{Name}\": a card can remember at most 20 of these.");
        var flag = $"id+{8230 + slot}";
        var reset = duel ? "0" : "RESET_PHASE+PHASE_END";
        var op = ev == "activated"
            ? ctx.Script.AddFunction("histop", "e,tp,eg,ep,ev,re,r,rp",
            [
                $"if re:IsHasType(EFFECT_TYPE_ACTIVATE) and {filter}(re:GetHandler(),e,tp) then Duel.RegisterFlagEffect(rp,{flag},{reset},0,1) end",
            ])
            : ctx.Script.AddFunction("histop", "e,tp,eg,ep,ev,re,r,rp",
            [
                "local tc=eg:GetFirst()",
                "while tc do",
                $"\tif {filter}(tc,e,tp) then Duel.RegisterFlagEffect(tc:GetControler(),{flag},{reset},0,1) end",
                "\ttc=eg:GetNext()",
                "end",
            ]);
        var guard = $"s.cc_hist{slot}";
        ctx.Script.AddInitialLine(string.Join("\n\t",
        [
            $"if not {guard} then",
            $"\t{guard}=true",
            "\tlocal hw=Effect.CreateEffect(c)",
            "\thw:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            // Activations count once they resolve: a negated activation never does.
            $"\thw:SetCode({(ev == "activated" ? "EVENT_CHAIN_SOLVED" : "EVENT_TO_GRAVE")})",
            $"\thw:SetOperation({op})",
            "\tDuel.RegisterEffect(hw,0)",
            "end",
        ]));
        ctx.Effect.Conditions.Add($"Duel.GetFlagEffect(tp,{flag})>0");
    }
}

/// <summary>
/// "During your Main Phase, if this card was activated this turn": this Spell/Trap Card was activated (not Set and
/// flipped by an effect) this turn and is still there. Its activation sets a flag on it, as Wicked Canon's does.
/// </summary>
public sealed class ActivatedThisTurnCondition : ConditionBlock
{
    public const string BlockId = "if.activated_this_turn";
    public const string Flag = "id+8210";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card was activated this turn";
    public override string Sentence => "This card was activated this turn";
    public override string Help => "For Continuous and Field Spells and Continuous Traps: an effect they can use the turn they are activated, " +
                                   "like \"During your Main Phase, if this card was activated this turn: You can add 1 ...\".";

    public override string Describe(BlockArgs args) => "this card was activated this turn";

    /// <summary>True when the card has this block, so its activation must leave the flag.</summary>
    public static bool Watches(CardProject project) =>
        project.Effects.Any(e => e.If.Concat(e.Do).Any(b => b.BlockId == BlockId));

    /// <summary>The line the card's activation adds to its cost.</summary>
    public const string FlagLine = $"e:GetHandler():RegisterFlagEffect({Flag},RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,EFFECT_FLAG_OATH,1)";

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Card.Kind == CardKind.Monster) ctx.Effect.Error($"\"{Name}\" is for Spell and Trap Cards: only they are activated as cards.");
        ctx.Effect.Conditions.Add($"e:GetHandler():GetFlagEffect({Flag})~=0");
    }

    /// <summary>The card's activations leave the flag; a card without one can't be activated, so the block does nothing.</summary>
    public static void Watch(ScriptBuilder script, IReadOnlyList<EffectBuilder> built, bool plainActivation)
    {
        var activations = built.Where(b => b.IsActivation).ToList();
        foreach (var a in activations) a.CostLines.Insert(0, FlagLine);
        if (activations.Count == 0 && !plainActivation)
            script.Diagnostics.Add(new(Severity.Error, "\"This card was activated this turn\" needs the card to be activated: give it an effect with \"Activate this Spell/Trap\", or make it a Continuous or Field card."));
    }
}

/// <summary>
/// "If your opponent activates a Trap Card or effect, ... and this card is already face-up in your Spell & Trap Zone":
/// this card was face-up before the event, so it wasn't just activated in response.
/// </summary>
public sealed class AlreadyFaceUpCondition : ConditionBlock
{
    public const string BlockId = "if.already_face_up";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "This card is already face-up (not just activated)";
    public override string Sentence => "This card is already face-up {where}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("where", "Where", new("szone", "in your Spell & Trap Zone"), new("field", "on the field")),
    ];

    public override string Describe(BlockArgs args) => $"this card is already face-up {args.Describe("where")}";

    public override void Compile(BlockContext ctx) => ctx.Effect.Conditions.Add("e:GetHandler():IsStatus(STATUS_EFFECT_ENABLED)");
}
