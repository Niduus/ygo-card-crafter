using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// Showing cards as an effect, and what a Spell or Trap does with itself after activation (Millennium Ankh).

/// <summary>
/// "Show 5 "Forbidden One" Monster Cards from your hand, Deck, and/or face-up field, then ..." (Millennium Ankh), "Reveal 1
/// "Noble Arms" Equip Spell in your hand, and if you do, ..." (The Continuing Epic of Charles): the cards are shown to the
/// opponent as the effect resolves. The effect can only be activated while there are that many; if there are fewer when it
/// resolves, nothing is shown and the steps that need it ("then", "and if you do") don't happen. A hand or Deck a card
/// was shown from is shuffled afterwards.
/// </summary>
public sealed class RevealAction : ActionBlock
{
    public const string BlockId = "do.reveal";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "Show or reveal cards (from the hand, Deck, field...)";
    public override string Sentence => "{verb} {count} {cards}";
    public override string Help => "Not a cost: the cards are shown as the effect resolves. Link the next block with \"then\" or \"and if you do\" so it only " +
                                   "happens when they were shown. The cards shown are \"the card(s) the step before affected\" for the block after it.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("verb", "Wording", new("show", "Show"), new("reveal", "Reveal")),
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand }, Zone.Hand | Zone.Deck | Zone.Field | Zone.Graveyard | Zone.ExtraDeck),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var f = args.Cards("cards");
        var verb = $"{args.Describe("verb").ToLowerInvariant()} {n.ToString(CultureInfo.InvariantCulture)}";
        // "from your hand, Deck, and/or face-up field": the field among other places, its cards face-up there.
        if (f.Owner == CardOwner.You && (f.Zones & Zone.Field) != Zone.None && (f.Zones & ~Zone.Field) != Zone.None)
        {
            var kind = f.Clone();
            kind.FaceUpOnly = false;
            var places = new List<string>();
            if (f.Zones.HasFlag(Zone.Hand)) places.Add("hand");
            if (f.Zones.HasFlag(Zone.Deck)) places.Add("Deck");
            if (f.Zones.HasFlag(Zone.ExtraDeck)) places.Add("Extra Deck");
            places.Add(f.FaceUpOnly ? "face-up field" : "field");
            if (f.Zones.HasFlag(Zone.Graveyard)) places.Add("GY");
            var or = n == 1 ? "or" : "and/or";
            var list = places.Count == 2 ? $"{places[0]} {or} {places[1]}" : string.Join(", ", places.Take(places.Count - 1)) + $", {or} " + places[^1];
            return $"{verb} {FilterText.Describe(kind, args.Pack, n != 1, withLocation: false, ownName: args.CardName)} from your {list}";
        }
        // "in your hand" for the hand alone, as "Reveal 1 ... in your hand" reads; "from your Deck" otherwise.
        var preposition = f.Zones is Zone.Hand or Zone.ExtraDeck ? "in" : "from";
        return $"{verb} {FilterText.Describe(f, args.Pack, n != 1, preposition, ownName: args.CardName)}";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var filter = ctx.Args.Cards("cards");
        if (filter.IsSpecific() || filter.Zones == Zone.None)
        {
            e.Error($"\"{Name}\": pick where the cards are shown from (the hand, Deck, field...).");
            return;
        }
        if ((filter.Zones & (Zone.Graveyard | Zone.Banished)) == filter.Zones)
            e.Warn($"\"{Name}\": cards in the GY or banished face-up are already public.");
        var f = ctx.Script.Filter(filter);
        var n = ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture);
        e.TargetChecks.Add($"Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},{f.Opp},{n},{f.Exclude},e,tp)");
        var r = e.NewLocal("r");
        var sg = e.NewLocal("sg");
        var g = e.NewLocal("g");
        var step = e.AddStep(
        [
            $"local {r}=0",
            $"local {sg}=Group.CreateGroup()",
            $"local {g}=Duel.GetMatchingGroup({f.Function},tp,{f.Self},{f.Opp},{f.Exclude},e,tp)",
            $"if {g}:GetCount()>={n} then",
            "\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_CONFIRM)",
            $"\t{sg}={g}:Select(tp,{n},{n},nil)",
            $"\tlocal fg={sg}:Filter(Card.IsOnField,nil)",
            "\tif fg:GetCount()>0 then Duel.HintSelection(fg) end",
            $"\tDuel.ConfirmCards(1-tp,{sg})",
            $"\tif {sg}:IsExists(Card.IsLocation,1,nil,LOCATION_HAND) then Duel.ShuffleHand(tp) end",
            $"\tif {sg}:IsExists(Card.IsLocation,1,nil,LOCATION_DECK) then Duel.ShuffleDeck(tp) end",
            $"\t{r}={sg}:GetCount()",
            "end",
        ], r, ctx.Link);
        // The cards shown aren't the engine's operated group: the step after reads them from here.
        step.Affected = sg;
    }
}

/// <summary>
/// "After activation, shuffle this card into the Deck instead of sending it to the GY." (Millennium Ankh), "After activation,
/// banish this card instead of sending it to the GY." (Pot of Benevolence), "... Set this card face-down instead of sending it
/// to the GY" (Subterror Final Battle): what a Normal Spell or Trap does with itself once its effect has resolved. Not a
/// cost: it happens at the end of the effect, whatever the effect did, as official scripts write it.
/// </summary>
public sealed class AfterActivationCost : CostBlock
{
    public const string BlockId = "cost.after_activation";
    public override string Id => BlockId;
    public override string Group => "Activation";
    public override string Name => "After activation, shuffle / banish / Set this card instead of sending it to the GY";
    public override string Sentence => "After activation, {how} instead of sending it to the GY";
    public override string Help => "Not a cost: it happens when the effect has resolved. For Normal Spells and Traps, which would go to the GY. The card text " +
                                   "says it after the effect.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "This card",
            new("deck", "shuffle this card into the Deck"), new("banish", "banish this card"), new("set", "Set this card face-down")),
    ];

    public override string Describe(BlockArgs args) => "";

    public override string Afterword(BlockArgs args) => $"After activation, {args.Describe("how")} instead of sending it to the GY.";

    public override void Compile(BlockContext ctx)
    {
        var card = ctx.Card;
        // Only a card's own activation sends it to the GY; anything else does nothing here.
        if (card.IsMonster || !ctx.Effect.IsActivation)
            ctx.Effect.Warn($"\"{Name}\" is for the activation of a Spell or Trap Card; other effects leave the card where it is.");
        else if (card.Kind == CardKind.Spell && card.SpellKind is SpellKind.Continuous or SpellKind.Field or SpellKind.Equip
            || card.Kind == CardKind.Trap && card.TrapKind == TrapKind.Continuous)
            ctx.Effect.Warn($"\"{Name}\": this card stays on the field after it is activated, so it is not sent to the GY anyway.");
        string[] how = ctx.Args.Choice("how") switch
        {
            "banish" => ["Duel.Remove(ac,POS_FACEUP,REASON_EFFECT)"],
            // As official cards Set themselves again (Subterror Final Battle): face-down where it is, a Set card again.
            "set" => ["Duel.ChangePosition(ac,POS_FACEDOWN)", "Duel.RaiseEvent(ac,EVENT_SSET,e,REASON_EFFECT,tp,tp,0)"],
            _ => ["Duel.SendtoDeck(ac,nil,SEQ_DECKSHUFFLE,REASON_EFFECT)"],
        };
        ctx.Effect.AddFinally(
        [
            "local ac=e:GetHandler()",
            "if ac:IsRelateToEffect(e) and e:IsHasType(EFFECT_TYPE_ACTIVATE) then",
            // No longer on its way to the GY once the chain resolves.
            "\tac:CancelToGrave()",
            .. how.Select(l => "\t" + l),
            "end",
        ]);
    }
}
