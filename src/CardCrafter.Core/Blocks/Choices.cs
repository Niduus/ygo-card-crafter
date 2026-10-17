using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// "Add to your hand, or Special Summon, 1 "Ars Magna" monster from your Deck", "take 1 "Aleister" monster from your Deck,
/// and either send it to the GY or banish it": the cards are picked first, then the player chooses which of two things
/// happens to them (as official scripts ask with Duel.SelectOption). The steps after it chain on whichever was done.
/// </summary>
public sealed class EitherAction : CardGroupAction
{
    public const string BlockId = "do.either";
    public override string Id => BlockId;
    public override string Name => "Do one thing or another to cards (add to your hand, or Special Summon, ...)";
    public override string Sentence => "{first}, or {second}, {count} {cards} {target} {wording}";
    public override string Help => "The cards are picked first; then the player chooses what happens to them, among what can be done. " +
                                   "\"Take ... and either\" reads as cards that take a card from the Deck: \"take 1 monster from your Deck, and either send it to the GY or banish it\".";
    protected override bool AllowsThisCard => true;
    protected override string Category => "";
    protected override string Hint => "HINTMSG_OPERATECARD";
    protected override string TargetedWording => "{0}";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster };

    /// <summary>What can be done to the cards: the verb before them, the verb with "it", its system string, its category.</summary>
    private static readonly (string Value, string Verb, string Ui)[] Ways =
    [
        ("hand", "add to your hand", "add them to the hand"),
        ("summon", "Special Summon", "Special Summon them"),
        ("grave", "send to the GY", "send them to the GY"),
        ("banish", "banish", "banish them"),
        ("destroy", "destroy", "destroy them"),
        ("deck", "shuffle into the Deck", "shuffle them into the Deck"),
        ("set", "Set", "Set them (Spells/Traps)"),
    ];

    private static ParamSpec Way(string key, string label, int first) =>
        ParamSpec.Choice(key, label, [.. Ways.Skip(first).Concat(Ways.Take(first)).Select(w => new ChoiceOption(w.Value, w.Verb, w.Ui))]);

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        Way("first", "Either", 0),
        Way("second", "Or", 1),
        .. base.BuildParams(),
        ParamSpec.Choice("wording", "Wording",
            new("verbs", "", "\"add to your hand, or Special Summon, 1 ...\""),
            new("take", "", "\"take 1 ..., and either add it to your hand or send it to the GY\"")),
    ];

    /// <summary>"add it to your hand", "Special Summon them", "send it to the GY".</summary>
    private static string With(string way, string pronoun) => way switch
    {
        "hand" => $"add {pronoun} to your hand",
        "summon" => $"Special Summon {pronoun}",
        "grave" => $"send {pronoun} to the GY",
        "banish" => $"banish {pronoun}",
        "destroy" => $"destroy {pronoun}",
        "deck" => $"shuffle {pronoun} into the Deck",
        _ => $"Set {pronoun}",
    };

    private static string Verb(string way) => Ways.First(w => w.Value == way).Verb;

    /// <summary>"either send it to the GY or banish it".</summary>
    private static string Either(BlockArgs args, string pronoun) =>
        $"either {With(args.Choice("first"), pronoun)} or {With(args.Choice("second"), pronoun)}";

    public override string Describe(BlockArgs args)
    {
        // "Either add it to your hand or send it to the GY": cards the effect already has.
        if (args.IsThatCard) return Either(args, CardRefs.Words(args, args.Cards("cards")));
        if (args.IsThisCard) return Either(args, "this card");
        var cards = $"{args.Describe("count")} {args.Describe("cards")}".Trim() + args.ExceptText();
        var pronoun = args.IsPlural ? "them" : "it";
        return args.Choice("wording") == "take"
            ? $"take {cards}, and {Either(args, pronoun)}"
            : $"{Verb(args.Choice("first"))}, or {Verb(args.Choice("second"))}, {cards}";
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) => Either(args, pronoun);

    /// <summary>What each card must pass for one way: "c:IsAbleToHand()", with a free zone for a Special Summon.</summary>
    private static string Able(string way, Zone zones, bool thisCard) => way switch
    {
        "hand" => "c:IsAbleToHand()",
        "summon" => (zones == Zone.ExtraDeck && !thisCard ? "Duel.GetLocationCountFromEx(tp,tp,nil,c)>0" : "Duel.GetLocationCount(tp,LOCATION_MZONE)>0")
                    + $" and c:IsCanBeSpecialSummoned(e,{(thisCard ? "SUMMON_VALUE_SELF" : "0")},tp,false,false)",
        "grave" => "c:IsAbleToGrave()",
        "banish" => "c:IsAbleToRemove()",
        "destroy" => "c:IsDestructable(e)",
        "deck" => "c:IsAbleToDeck()",
        _ => "c:IsSSetable()",
    };

    /// <summary>The system string the game shows for each way, as official scripts offer them (1190 "Add to hand", 1152 "Special Summon"...).</summary>
    private static string String(string way) => way switch
    {
        "hand" => "1190", "summon" => "1152", "grave" => "1191", "banish" => "1192", "destroy" => "1124", "deck" => "1105", _ => "1153",
    };

    private static string Do(string way, bool thisCard) => way switch
    {
        "hand" => "local n=Duel.SendtoHand(g,nil,REASON_EFFECT) if n>0 then Duel.ConfirmCards(1-tp,g) end return n",
        "summon" => $"return Duel.SpecialSummon(g,{(thisCard ? "SUMMON_VALUE_SELF" : "0")},tp,tp,false,false,POS_FACEUP)",
        "grave" => "return Duel.SendtoGrave(g,REASON_EFFECT)",
        "banish" => "return Duel.Remove(g,POS_FACEUP,REASON_EFFECT)",
        "destroy" => "return Duel.Destroy(g,REASON_EFFECT)",
        "deck" => "return Duel.SendtoDeck(g,nil,SEQ_DECKSHUFFLE,REASON_EFFECT)",
        _ => "return Duel.SSet(tp,g)",
    };

    private static IEnumerable<string> Categories(string way, Zone zones) => way switch
    {
        "hand" => zones.HasFlag(Zone.Deck) ? ["CATEGORY_TOHAND", "CATEGORY_SEARCH"] : ["CATEGORY_TOHAND"],
        "summon" => ["CATEGORY_SPECIAL_SUMMON"],
        "grave" => zones.HasFlag(Zone.Deck) ? ["CATEGORY_TOGRAVE", "CATEGORY_DECKDES"] : ["CATEGORY_TOGRAVE"],
        "banish" => ["CATEGORY_REMOVE"],
        "destroy" => ["CATEGORY_DESTROY"],
        "deck" => ["CATEGORY_TODECK"],
        _ => [],
    };

    private static (string First, string Second) Pair(BlockContext ctx) => (ctx.Args.Choice("first"), ctx.Args.Choice("second"));

    protected override string? AbleCheck(BlockContext ctx)
    {
        var (first, second) = Pair(ctx);
        var f = ctx.Args.Cards("cards");
        return $"({Able(first, f.Zones, f.ThisCard)} or {Able(second, f.Zones, f.ThisCard)})";
    }

    /// <summary>Offers the ways every card can go, and does the one the player chooses; how many cards it moved.</summary>
    protected override string Apply(string g, BlockContext ctx)
    {
        var (first, second) = Pair(ctx);
        var f = ctx.Args.Cards("cards");
        var body = new List<string>();
        var n = 0;
        foreach (var way in new[] { first, second })
        {
            n++;
            var room = way == "summon" ? (f.Zones == Zone.ExtraDeck && !f.ThisCard ?"Duel.GetLocationCountFromEx(tp)>=#g and " : "Duel.GetLocationCount(tp,LOCATION_MZONE)>=#g and ") : "";
            var able = Able(way, f.Zones, f.ThisCard).Replace("Duel.GetLocationCount(tp,LOCATION_MZONE)>0 and ", "", StringComparison.Ordinal)
                .Replace("Duel.GetLocationCountFromEx(tp,tp,nil,c)>0 and ", "", StringComparison.Ordinal);
            body.Add($"local b{n}={room}not g:IsExists(function(c) return not ({able}) end,1,nil)");
        }
        body.Add($"local op=aux.SelectFromOptions(tp,{{b1,{String(first)},1}},{{b2,{String(second)},2}})");
        body.Add($"if op==1 then {Do(first, f.ThisCard)} end");
        body.Add($"if op==2 then {Do(second, f.ThisCard)} end");
        body.Add("return 0");
        return ctx.Script.AddFunction("either", "g,e,tp", body) + $"({g},e,tp)";
    }

    public override void Compile(BlockContext ctx)
    {
        var (first, second) = Pair(ctx);
        if (first == second) ctx.Effect.Warn($"\"{Name}\": both choices do the same; pick two different things to do.");
        var f = ctx.Args.Cards("cards");
        if ((first == "set" || second == "set") && !f.IsSpecific() && f.CardType is not (FilterCardType.Spell or FilterCardType.Trap or FilterCardType.SpellTrap))
            ctx.Effect.Warn($"\"{Name}\": only Spells and Traps are Set this way.");
        foreach (var c in Categories(first, f.Zones).Concat(Categories(second, f.Zones))) ctx.Effect.AddCategory(c);
        base.Compile(ctx);
    }
}

/// <summary>
/// "If this card is sent to the GY: You can activate this effect based on where it was sent from; ● Field: ... ● Extra
/// Deck: ...": where this card was before it moved. Under a "Choose 1 of these effects" block set to "based on where it
/// was sent from", each option starts with one of these and reads "● Field:".
/// </summary>
public sealed class SentFromCondition : ConditionBlock
{
    public const string BlockId = "if.sent_from";
    public override string Id => BlockId;
    public override string Group => "This card";
    public override string Name => "Where this card was sent from";
    public override string Sentence => "This card was sent from {from}";
    public override string Help => "For effects that start when this card moves (sent to the GY, banished...). Each option of \"activate this effect " +
                                   "based on where it was sent from\" starts with one.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("from", "From",
            new("field", "the field", "the field (Field)"), new("extra", "the Extra Deck", "the Extra Deck (Extra Deck)"),
            new("hand", "the hand", "the hand (Hand)"), new("deck", "the Deck", "the Deck (Deck)"),
            new("mzone", "the Monster Zone", "the Monster Zone (Monster Zone)"), new("hand_deck", "the hand or Deck", "the hand or Deck (Hand or Deck)")),
    ];

    public override string Describe(BlockArgs args) => $"this card was sent from {args.Describe("from")}";

    /// <summary>"Field", "Extra Deck": the option's label when the options are "based on where it was sent from".</summary>
    public static string Label(BlockInstance b) => b.Values.GetValueOrDefault("from", "field") switch
    {
        "extra" => "Extra Deck", "hand" => "Hand", "deck" => "Deck", "mzone" => "Monster Zone", "hand_deck" => "Hand or Deck", _ => "Field",
    };

    public override void Compile(BlockContext ctx) =>
        ctx.Effect.Conditions.Add("e:GetHandler():IsPreviousLocation(" + ctx.Args.Choice("from") switch
        {
            "extra" => "LOCATION_EXTRA", "hand" => "LOCATION_HAND", "deck" => "LOCATION_DECK", "mzone" => "LOCATION_MZONE",
            "hand_deck" => "LOCATION_HAND+LOCATION_DECK", _ => "LOCATION_ONFIELD",
        } + ")");
}
