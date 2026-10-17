using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// Draw rules and the hand: drawing more for the normal draw (Bunnillin Extract), the Mulcharmy End Phase shuffle, and
// "You can only activate 1 other "Mulcharmy" monster effect, the turn you activate this effect".

/// <summary>
/// "While you control 2 or more non-Token Normal Monsters, draw 2 cards instead of 1 for your normal draw during your Draw
/// Phase": the player's normal draw takes more cards while this card is face-up (EFFECT_DRAW_COUNT, as Pandora's Jewelry Box).
/// </summary>
public sealed class DrawCountPassive : PassiveBlock
{
    public const string BlockId = "passive.draw_count";
    public override string Id => BlockId;
    public override string Group => "Players";
    public override string Name => "Draw 2 cards instead of 1 for the normal draw";
    public override string Sentence => "{who} draw(s) {count} cards instead of 1 for the normal draw during the Draw Phase";
    public override string Help => "The effect's IF blocks say while it applies, like \"while you control 2 or more Normal Monsters\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("you", "You"), new("opponent", "Your opponent")),
        ParamSpec.Int("count", "Cards drawn", 2, 2, 5),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count").ToString(CultureInfo.InvariantCulture);
        return args.Choice("who") == "opponent"
            ? $"your opponent draws {n} cards instead of 1 for their normal draw during their Draw Phase"
            : $"draw {n} cards instead of 1 for your normal draw during your Draw Phase";
    }

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        var players = ctx.Args.Choice("who") == "opponent" ? "0,1" : "1,0";
        var n = ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD)",
            $"{v}:SetProperty(EFFECT_FLAG_PLAYER_TARGET)",
            $"{v}:SetCode(EFFECT_DRAW_COUNT)",
            $"{v}:SetRange({range})",
            $"{v}:SetTargetRange({players})",
            $"{v}:SetValue({n})",
        ]);
    }
}

/// <summary>
/// Mulcharmy Fuwalos: "Once, during this End Phase, if the number of cards in your hand is more than the number of cards
/// your opponent controls +6, you must randomly shuffle cards from your hand into the Deck so the number in your hand
/// equals the number your opponent controls +6." A watcher on the player for this turn's End Phase, as the official card has.
/// </summary>
public sealed class EndPhaseHandAction : ActionBlock
{
    public const string BlockId = "do.end_phase_hand";
    public override string Id => BlockId;
    public override string Group => "Turn";
    public override string Name => "During this End Phase, shuffle random cards from your hand into the Deck down to a number";
    public override string Sentence => "Once, during this End Phase, if you have more cards in your hand than {than} +{plus}, shuffle random cards from your hand into the Deck until you have that many";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("than", "Compared with", new("opp_field", "the number of cards your opponent controls"), new("number", "", "a number")),
        ParamSpec.Int("plus", "Plus", 6, 0, 60),
    ];

    public override string Describe(BlockArgs args)
    {
        var plus = args.Int("plus").ToString(CultureInfo.InvariantCulture);
        return args.Choice("than") == "number"
            ? $"once, during this End Phase, if the number of cards in your hand is more than {plus}, you must randomly shuffle cards from your hand into the Deck so the number in your hand equals {plus}"
            : $"once, during this End Phase, if the number of cards in your hand is more than the number of cards your opponent controls +{plus}, " +
              $"you must randomly shuffle cards from your hand into the Deck so the number in your hand equals the number your opponent controls +{plus}";
    }

    public override void Compile(BlockContext ctx)
    {
        var plus = ctx.Args.Int("plus").ToString(CultureInfo.InvariantCulture);
        // The player who used the effect: its owner, whoever's End Phase it is.
        var limit = ctx.Args.Choice("than") == "number" ? plus : $"Duel.GetFieldGroupCount(e:GetOwnerPlayer(),0,LOCATION_ONFIELD)+{plus}";
        var con = ctx.Script.AddFunction("handlimcon", "e,tp,eg,ep,ev,re,r,rp", [$"return Duel.GetFieldGroupCount(e:GetOwnerPlayer(),LOCATION_HAND,0)>{limit}"]);
        var op = ctx.Script.AddFunction("handlimop", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local p=e:GetOwnerPlayer()",
            "local g=Duel.GetFieldGroup(p,LOCATION_HAND,0)",
            $"local d=g:GetCount()-({limit})",
            "if d<=0 then return end",
            "Duel.Hint(HINT_CARD,0,id)",
            "Duel.SendtoDeck(g:RandomSelect(p,d),nil,SEQ_DECKSHUFFLE,REASON_EFFECT)",
        ]);
        ctx.Effect.AddStep(
        [
            "local th=Effect.CreateEffect(e:GetHandler())",
            "th:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
            "th:SetCode(EVENT_PHASE+PHASE_END)",
            "th:SetCountLimit(1)",
            $"th:SetCondition({con})",
            $"th:SetOperation({op})",
            "th:SetReset(RESET_PHASE+PHASE_END)",
            "Duel.RegisterEffect(th,tp)",
        ], null, ctx.Link);
    }
}

/// <summary>
/// "You can only activate 1 other "Mulcharmy" monster effect, the turn you activate this effect.": the effect can't be
/// activated after more monster effects of the archetype this turn, and the card says so after the effect. The
/// activations are counted as official cards count them: a flag on the player for each monster effect of the archetype,
/// the official "Mulcharmy" flag (aux.EnableMulcharmyGlobalCheck) for "Mulcharmy", so they count each other.
/// </summary>
public sealed class ArchetypeActivationsCondition : ConditionBlock
{
    public const string BlockId = "if.archetype_activations";
    public override string Id => BlockId;
    public override string Group => "Limits";
    public override string Name => "You can only activate 1 other \"X\" monster effect, the turn you activate this effect";
    public override string Sentence => "You can only activate {count} other {cards} monster effect(s), the turn you activate this effect";
    public override string Help => "Pick the archetype in the card picker. Its monsters' effects activated this turn are counted, this card's own included.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many others", 1, 0, 5),
        new ParamSpec { Key = "cards", Label = "Which archetype", Type = ParamType.Cards, WhereFromBlock = true, DefaultFilter = () => new CardFilter { CardType = FilterCardType.Monster } },
    ];

    /// <summary>The setcode of "Mulcharmy", whose official flag the official cards share.</summary>
    public const int Mulcharmy = 0x1b2;
    public const string MulcharmyFlag = "84192580";

    /// <summary>Said after the effect, not as a condition.</summary>
    public override string Describe(BlockArgs args) => "";

    private static string Archetype(BlockArgs args) => args.Cards("cards").Archetype is var code and > 0
        ? $"\"{(args.Pack is not null ? args.Pack.ArchetypeName(code) : ArchetypeNames.Find(code) ?? $"0x{code:x}")}\"" : "\"?\"";

    public override string Afterword(BlockArgs args)
    {
        var n = args.Int("count");
        return n == 0
            ? $"You cannot activate other {Archetype(args)} monster effects the turn you activate this effect."
            : $"You can only activate {n} other {Archetype(args)} monster effect{(n == 1 ? "" : "s")}, the turn you activate this effect.";
    }

    /// <summary>The flag the activations of the archetype's monster effects leave on their player.</summary>
    public static string Flag(int setcode) => setcode == Mulcharmy ? MulcharmyFlag : (0x7c000000 + setcode).ToString(CultureInfo.InvariantCulture);

    public override void Compile(BlockContext ctx)
    {
        var code = ctx.Args.Cards("cards").Archetype;
        if (code <= 0)
        {
            ctx.Effect.Error($"\"{Name}\": pick the archetype in the card picker.");
            return;
        }
        var hex = "0x" + code.ToString("x", CultureInfo.InvariantCulture);
        if (code == Mulcharmy) ctx.Script.AddInitialLine("aux.EnableMulcharmyGlobalCheck()");
        else
        {
            // One watcher per archetype in the duel, shared by every card of it that counts.
            ctx.Script.AddInitialLine(string.Join("\n\t",
            [
                "if not CardCrafterActivationChecks then CardCrafterActivationChecks={} end",
                $"if not CardCrafterActivationChecks[{hex}] then",
                $"\tCardCrafterActivationChecks[{hex}]=true",
                "\tlocal ge=Effect.GlobalEffect()",
                "\tge:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)",
                "\tge:SetCode(EVENT_CHAINING)",
                $"\tge:SetOperation(function(e,tp,eg,ep,ev,re,r,rp) if re:IsActiveType(TYPE_MONSTER) and re:GetHandler():IsSetCard({hex}) then Duel.RegisterFlagEffect(ep,{Flag(code)},RESET_PHASE+PHASE_END,0,1) end end)",
                "\tDuel.RegisterEffect(ge,0)",
                "end",
            ]));
        }
        ctx.Effect.Conditions.Add($"Duel.GetFlagEffect(tp,{Flag(code)})<={ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture)}");
    }
}
