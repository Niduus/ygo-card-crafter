using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// How a card action picks its cards beyond what each card must be, as official cards word it: as a group ("2 "Rokket"
/// monsters with different names", "whose total Levels equal 8", "1 each from your hand and GY"), how many ("any number
/// of") and who picks them ("1 random card from your opponent's hand", "your opponent chooses 1 monster in your GY").
/// The card actions that move cards offer these blanks (<see cref="CardGroupAction.OffersSelections"/>); left at their
/// defaults they change nothing, so older packs compile as before.
/// </summary>
public static class Selection
{
    /// <summary>The blanks, in the order the sentences place them; "any number" only on blocks that can take every card.</summary>
    public static IReadOnlyList<ParamSpec> Params(bool anyNumber) =>
    [
        .. anyNumber ? [AmountParam, NumberOfParam] : Array.Empty<ParamSpec>(),
        PickParam, SumParam, EachParam, ChooserParam,
    ];

    /// <summary>
    /// "any number of": as many of the cards as the player likes (at least 1); "up to the number of "Ritual Beast"
    /// monsters you control": at least 1, and at most that many.
    /// </summary>
    public static ParamSpec AmountParam => ParamSpec.Choice("amount", "Number",
        new("count", "", "a number:"), new("any", "", "any number"), new("number_of", "", "up to the number of:"));

    /// <summary>The cards counted for "up to the number of ...", said at the end of the sentence (<see cref="EachText"/>).</summary>
    public static ParamSpec NumberOfParam => ParamSpec.Cards("count_cards", "The number of",
            () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true })
        .Unprinted().WhenUsed().ShowIf(b => b.Values.GetValueOrDefault("amount") == "number_of");

    public static ParamSpec PickParam => ParamSpec.Choice("pick", "The cards as a group",
        new("any", "", "(any of them)"),
        new("names", "", "with different names"),
        new("attributes", "", "with different Attributes"),
        new("types", "", "with different Types"),
        new("levels", "", "with different Levels"),
        new("same_name", "", "with the same name"),
        new("same_attribute", "", "with the same Attribute"),
        new("same_type", "", "with the same Type"),
        new("same_level", "", "with the same Level"),
        new("level_sum", "", "whose total Levels equal ..."),
        new("level_max", "", "whose total Levels equal ... or less"),
        new("atk_max", "", "whose combined ATK is ... or less")).ShowIf(Several);

    public static ParamSpec SumParam => ParamSpec.Int("sum", "Total", 8, 1, 99999).Unprinted()
        .ShowIf(b => Several(b) && b.Values.GetValueOrDefault("pick") is "level_sum" or "level_max" or "atk_max");

    /// <summary>
    /// "1 each from your hand, Deck, and GY": no two cards from the same place; with one place on both sides, "1 each
    /// from your GY and your opponent's GY": no two cards of the same player.
    /// </summary>
    public static ParamSpec EachParam => ParamSpec.Flag("each_zone", "1 each (from each place, or from each player)", false)
        .ShowIf(b => Several(b) && b.Filters.TryGetValue("cards", out var f) && Parts(f).Count > 1);

    public static ParamSpec ChooserParam => ParamSpec.Choice("chooser", "Who picks",
        new("you", "", "you pick them"), new("random", "", "picked at random"), new("opponent", "", "your opponent picks them"))
        .ShowIf(b => !(b.Filters.TryGetValue("cards", out var f) && f.IsSpecific()));

    /// <summary>The block may take more than 1 card, so the group blanks mean something.</summary>
    private static bool Several(BlockInstance b) =>
        b.Values.GetValueOrDefault("amount") is "any" or "number_of"
        || b.Values.GetValueOrDefault("mode") != "all" && b.Values.GetValueOrDefault("count_mode") != "all"
        && int.TryParse(b.Values.GetValueOrDefault("count", "1"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 1;

    public static bool Offers(BlockDefinition def) => def.Param("pick") is not null;

    public static bool AnyNumber(BlockArgs a) => a.Definition.Param("amount") is not null && a.Choice("amount") == "any";

    /// <summary>"up to the number of ...": how many is counted as the effect is used.</summary>
    public static bool NumberOf(BlockArgs a) => a.Definition.Param("amount") is not null && a.Choice("amount") == "number_of";

    /// <summary>"any number of" or "up to the number of": the count blank says nothing, and the cards are plural.</summary>
    public static bool Uncounted(BlockArgs a) => AnyNumber(a) || NumberOf(a);

    private static bool Several(BlockArgs a) => Uncounted(a) || a.CountMode != "all" && a.Int("count") > 1;

    /// <summary>The group rule that applies: "any" when the block takes 1 card or the cards are this card.</summary>
    private static string Pick(BlockArgs a) => Offers(a.Definition) && Several(a) && !a.Cards("cards").IsSpecific() ? a.Choice("pick") : "any";

    private static bool Each(BlockArgs a) => Offers(a.Definition) && Several(a) && a.Bool("each_zone") && Parts(a.Cards("cards")).Count > 1;

    private static string Chooser(BlockArgs a) => Offers(a.Definition) && !a.Cards("cards").IsSpecific() ? a.Choice("chooser") : "you";

    /// <summary>True when the block picks its cards in a way of its own here, compiled by <see cref="CardGroupAction"/>'s selection step.</summary>
    public static bool Applies(BlockArgs a) => Offers(a.Definition) && !a.Cards("cards").IsSpecific() && (Uncounted(a) || Pick(a) != "any" || Each(a) || Chooser(a) != "you");

    /// <summary>"any number of", "1 random": the count's words, or null for the usual ones.</summary>
    public static string? CountWords(BlockArgs a)
    {
        if (!Offers(a.Definition) || a.Cards("cards").IsSpecific()) return null;
        if (AnyNumber(a)) return "any number of";
        if (NumberOf(a)) return "";
        if (Chooser(a) == "random" && a.CountMode == "exact") return $"{a.Int("count").ToString(CultureInfo.InvariantCulture)} random";
        return null;
    }

    /// <summary>The places "1 each from" counts apart: the field is one place.</summary>
    internal static List<Zone> PlacesOf(Zone zones) => Places(zones);

    private static List<Zone> Places(Zone zones)
    {
        var places = new List<Zone>();
        foreach (var z in new[] { Zone.Hand, Zone.Deck, Zone.ExtraDeck, Zone.Graveyard, Zone.Banished })
            if (zones.HasFlag(z)) places.Add(z);
        if ((zones & (Zone.Field | Zone.PendulumZone)) != Zone.None) places.Add(zones & (Zone.Field | Zone.PendulumZone));
        return places;
    }

    /// <summary>
    /// What "1 each" keeps apart, as a check of the picked group g each: the places, or with one place on both sides of
    /// the field, the players.
    /// </summary>
    private static List<string> Parts(CardFilter f)
    {
        var places = Places(f.Zones);
        if (places.Count > 1) return places.Select(z => $"g:FilterCount(Card.IsLocation,nil,{Lua.Locations(z)})<=1").ToList();
        if (places.Count == 1 && f.Owner == CardOwner.Either) return ["g:FilterCount(Card.IsControler,nil,tp)<=1", "g:FilterCount(Card.IsControler,nil,1-tp)<=1"];
        return [];
    }

    /// <summary>"your GY and your opponent's GY": the one place on both sides, for "1 each from".</summary>
    private static string BothSides(Zone zones)
    {
        var place = (zones & (Zone.Field | Zone.PendulumZone)) != Zone.None ? "field" : zones switch
        {
            Zone.Hand => "hand", Zone.Deck => "Deck", Zone.ExtraDeck => "Extra Deck", Zone.Graveyard => "GY", _ => "banished cards",
        };
        return $"your {place} and your opponent's {place}";
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The cards' words with the group rule in place: "2 "Rokket" monsters with different names from your Deck",
    /// "monsters from your GY whose total Levels equal 8". With "1 each", where they are is said at the end of the
    /// sentence instead (<see cref="EachText"/>).
    /// </summary>
    public static string CardsText(BlockArgs a, string preposition = "from")
    {
        var f = a.Cards("cards");
        var pick = Pick(a);
        var before = pick switch
        {
            "names" => "with different names",
            "attributes" => "with different Attributes",
            "types" => "with different Types",
            "levels" => "with different Levels",
            "same_name" => "with the same name",
            "same_attribute" => "with the same Attribute",
            "same_type" => "with the same Type",
            "same_level" => "with the same Level",
            _ => null,
        };
        var each = Each(a);
        var text = FilterText.Describe(f, a.Pack, plural: a.IsPlural, preposition: preposition, ownName: a.CardName, withExcept: false, beforeLocation: before, withLocation: !each);
        var sum = Num(a.Int("sum"));
        return pick switch
        {
            "level_sum" => $"{text} whose total Levels equal {sum}",
            "level_max" => $"{text} whose total Levels equal {sum} or less",
            "atk_max" => $"{text} whose combined ATK is less than or equal to {sum}",
            _ => text,
        };
    }

    /// <summary>", 1 each from your hand, Deck, and GY": said at the end of the sentence, or "".</summary>
    public static string EachText(BlockArgs a)
    {
        // ", up to the number of "Ritual Beast" monsters you control".
        var text = Offers(a.Definition) && NumberOf(a) && !a.Cards("cards").IsSpecific()
            ? ", up to the number of " + FilterText.Describe(a.Cards("count_cards"), a.Pack, plural: true, ownName: a.CardName)
            : "";
        if (!Each(a)) return text;
        var f = a.Cards("cards");
        if (Places(f.Zones).Count == 1) return text + ", 1 each from " + BothSides(f.Zones);
        return text + ", 1 each " + FilterText.LocationText(f, "from", true).Replace(", or ", ", and ", StringComparison.Ordinal).Replace(" or ", " and ", StringComparison.Ordinal);
    }

    /// <summary>"up to the number of ...": the Lua count of those cards, or null.</summary>
    internal static string? NumberOfLua(BlockContext ctx)
    {
        if (!NumberOf(ctx.Args)) return null;
        var c = ctx.Script.Filter(ctx.Args.Cards("count_cards"), null, "ctfilter");
        return $"Duel.GetMatchingGroupCount({c.Function},tp,{c.Self},{c.Opp},{c.Exclude},e,tp)";
    }

    /// <summary>
    /// The group rule as Lua, given the pool of cards it picks from: the activation check that enough cards fit, and how
    /// the cards are picked (by <paramref name="who"/>). Null when there is no group rule.
    /// </summary>
    internal static (Func<string, string> Check, Func<string, string> Select)? Rule(BlockContext ctx, int min, string max, string who, string? checkMax = null)
    {
        var a = ctx.Args;
        var pick = Pick(a);
        var each = Each(a);
        if (pick == "any" && !each) return null;
        var sum = Num(a.Int("sum"));
        // The activation check can't use a local of the target step: it counts again ("up to the number of ...").
        var cmax = checkMax ?? max;
        // "whose total Levels equal 8", without more: the engine's own sum selection, as official cards use.
        if (pick == "level_sum" && !each)
            return (pool => $"{pool}:CheckWithSumEqual(Card.GetLevel,{sum},{Num(min)},{cmax})",
                    pool => $"{pool}:SelectWithSumEqual({who},Card.GetLevel,{sum},{Num(min)},{max})");
        // "with different names": the engine's checks (aux.dncheck...), and enough different ones among the cards.
        var (helper, by) = pick switch
        {
            "names" => ("aux.dncheck", "Card.GetCode"),
            "attributes" => ("aux.dabcheck", "Card.GetAttribute"),
            "types" => ("aux.drccheck", "Card.GetRace"),
            "levels" => ("aux.dlvcheck", "Card.GetLevel"),
            _ => ((string?)null, (string?)null),
        };
        if (helper is not null && !each)
            return (pool => $"{pool}:GetClassCount({by})>={Num(min)}", pool => $"{pool}:SelectSubGroup({who},{helper},false,{Num(min)},{max})");
        var tests = new List<string>();
        if (helper is not null) tests.Add($"{helper}(g)");
        tests.AddRange(pick switch
        {
            "same_name" => ["g:GetClassCount(Card.GetCode)==1"],
            "same_attribute" => ["aux.SameValueCheck(g,Card.GetAttribute)"],
            "same_type" => ["aux.SameValueCheck(g,Card.GetRace)"],
            "same_level" => ["g:GetClassCount(Card.GetLevel)==1"],
            "level_sum" => [$"g:GetSum(Card.GetLevel)=={sum}"],
            "level_max" => [$"g:GetSum(Card.GetLevel)<={sum}"],
            "atk_max" => [$"g:GetSum(Card.GetAttack)<={sum}"],
            _ => Array.Empty<string>(),
        });
        // "1 each from your hand and GY": at most 1 card from each place.
        if (each)
            tests.AddRange(Parts(a.Cards("cards")));
        // "1 each from your GY and your opponent's GY" asks whose each card is: the player is passed along.
        var player = tests.Any(t => t.Contains("tp", StringComparison.Ordinal));
        var fn = ctx.Script.AddFunction("gcheck", player ? "g,tp" : "g", [$"return {string.Join(" and ", tests)}"]);
        var extra = player ? ",tp" : "";
        return (pool => $"{pool}:CheckSubGroup({fn},{Num(min)},{cmax}{extra})", pool => $"{pool}:SelectSubGroup({who},{fn},false,{Num(min)},{max}{extra})");
    }

    /// <summary>
    /// Mistakes the selection blanks can make: "1 random card" is an exact number of cards nobody targets, and the
    /// opponent never picks the targets of your effect.
    /// </summary>
    internal static bool Check(BlockContext ctx, string name, bool targeting, int min, int max)
    {
        var a = ctx.Args;
        var e = ctx.Effect;
        if (a.CountMode == "all") { e.Error($"\"{name}\": \"all\" cards are not picked, so they can't be picked as a group, at random or by your opponent. Pick exactly or up to a number."); return false; }
        if (Pick(a) == "level_sum" && min == max && a.Int("count") > 0 && !Uncounted(a) && a.Int("count") > a.Int("sum"))
            e.Warn($"\"{name}\": {a.Int("count")} monsters with a Level each can't have a total Level of only {a.Int("sum")}.");
        if (Each(a) && !Uncounted(a) && a.CountMode == "exact" && a.Int("count") != Parts(a.Cards("cards")).Count)
            e.Warn($"\"{name}\": \"1 each\" from {Parts(a.Cards("cards")).Count} places takes {Parts(a.Cards("cards")).Count} cards, not {a.Int("count")}.");
        switch (Chooser(a))
        {
            case "random":
                if (targeting) { e.Error($"\"{name}\": cards picked at random aren't targeted. Turn off targeting."); return false; }
                if (min != max) { e.Error($"\"{name}\": pick an exact number of random cards, not \"up to\" or \"any number\"."); return false; }
                if (Pick(a) != "any" || Each(a)) { e.Error($"\"{name}\": random cards can't also be picked as a group (\"with different names\"...)."); return false; }
                break;
            case "opponent":
                if (targeting) { e.Error($"\"{name}\": your opponent can't pick the targets of your effect. Turn off targeting."); return false; }
                break;
        }
        return true;
    }

    /// <summary>"your opponent chooses 1 monster in your GY, and you Special Summon it": the sentence when the opponent picks.</summary>
    internal static bool OpponentPicks(BlockArgs a) => Chooser(a) == "opponent";
}

public abstract partial class CardGroupAction
{
    /// <summary>
    /// Whether the block offers the selection blanks (<see cref="Selection"/>): "with different names", "any number of",
    /// "1 random card", "your opponent chooses". Only the actions that move cards with the shared steps below offer them.
    /// </summary>
    protected virtual bool OffersSelections => false;

    /// <summary>The selection blanks for <see cref="BuildParams"/>.</summary>
    protected IReadOnlyList<ParamSpec> SelectionParams => OffersSelections ? Selection.Params(AllowsAll) : [];

    /// <summary>"Your opponent chooses 1 monster in your GY, and you Special Summon it."</summary>
    public override string Describe(BlockArgs args)
    {
        if (!Selection.OpponentPicks(args)) return base.Describe(args) + Selection.EachText(args);
        var single = !args.IsPlural && !(args.CountMode == "up_to" && args.Int("count") > 1);
        // Their own cards: "your opponent chooses 1 card in their Extra Deck".
        var cards = Selection.CardsText(args, "in").Replace("your opponent controls", "they control", StringComparison.Ordinal)
            .Replace("your opponent's ", "their ", StringComparison.Ordinal);
        return $"your opponent chooses {args.Describe("count")} {cards}{args.ExceptText()}{Selection.EachText(args)}, " +
               $"and you {TargetedAction(args, single ? "it" : "them", single)}";
    }

    /// <summary>
    /// The cards picked as a group, at random or by the opponent: the same steps as picking them plainly, with the pool
    /// of matching cards checked and picked from as official cards do (Group.SelectSubGroup with aux.dncheck,
    /// Group.RandomSelect, Duel.SelectMatchingCard by 1-tp).
    /// </summary>
    private void CompileSelection(BlockContext ctx, CardFilter filter, FilterRef f, bool targeting)
    {
        var e = ctx.Effect;
        var a = ctx.Args;
        var any = Selection.Uncounted(a);
        var n = any ? 99 : a.Int("count");
        var min = any || a.CountMode == "up_to" ? 1 : n;
        if (!Selection.Check(ctx, Name, targeting, min, n)) return;
        var who = Selection.OpponentPicks(a) ? "1-tp" : "tp";
        var random = a.Choice("chooser") == "random";
        // "up to the number of ...": counted as the effect is activated, and again as it resolves.
        var counted = Selection.NumberOfLua(ctx);
        var ct = counted is null ? null : e.NewLocal("ct");
        var max = ct ?? Num(n);
        if (counted is not null) e.TargetChecks.Add($"{counted}>0");
        var rule = Selection.Rule(ctx, min, max, who, counted);
        if (Category.Length > 0) e.AddCategory(Category);
        foreach (var c in ExtraCategories(filter)) e.AddCategory(c);
        var space = Space(any ? 1 : n, filter, ctx);
        if (space is not null) e.TargetChecks.Add(space);
        PlusChecks(ctx);
        var g = e.NewLocal("g");
        var r = e.NewLocal("r");
        var body = new List<string>();
        if (targeting)
        {
            if (filter.Zones.HasFlag(Zone.Deck) || filter.Zones.HasFlag(Zone.Hand) || filter.Zones.HasFlag(Zone.ExtraDeck))
            {
                e.Error($"\"{Name}\": cards in the Deck, hand or Extra Deck are hidden and can't be targeted. Turn off targeting or pick the field, GY or banished cards.");
                return;
            }
            // "up to the number of ...", like "up to 3", may be 1 card, which can be checked again; a group can't.
            e.SetChkc(rule is null && min == 1 ? f.ChkcCheck : "false");
            var tg = e.NewLocal("tg");
            e.TargetLines.Add($"Duel.Hint(HINT_SELECTMSG,tp,{Hint})");
            if (rule is { } targeted)
            {
                // The cards that can be targeted, picked as a group, then made the targets.
                var pool = $"Duel.GetMatchingGroup({f.Function},tp,{f.Self},{f.Opp},{f.Exclude},e,tp):Filter(Card.IsCanBeEffectTarget,nil,e)";
                e.TargetChecks.Add(targeted.Check(pool));
                var tpg = e.NewLocal("pg");
                if (ct is not null) e.TargetLines.Add($"local {ct}={counted}");
                e.TargetLines.Add($"local {tpg}={pool}");
                e.TargetLines.Add($"local {tg}={targeted.Select(tpg)}");
                e.TargetLines.Add($"Duel.SetTargetCard({tg})");
            }
            else
            {
                e.TargetChecks.Add($"Duel.IsExistingTarget({f.Function},tp,{f.Self},{f.Opp},{min},{f.Exclude},e,tp)");
                if (ct is not null) e.TargetLines.Add($"local {ct}={counted}");
                e.TargetLines.Add($"local {tg}=Duel.SelectTarget(tp,{f.Function},tp,{f.Self},{f.Opp},{min},{max},{f.Exclude},e,tp)");
            }
            if (Category.Length > 0) e.TargetLines.Add($"Duel.SetOperationInfo(0,{Category},{tg},{tg}:GetCount(),0,0)");
            body.Add($"local {g}=Duel.GetChainInfo(0,CHAININFO_TARGET_CARDS):Filter(Card.IsRelateToEffect,nil,e)");
            body.Add($"local {r}=0");
            body.Add(space is null ? $"if {g}:GetCount()>0 then" : $"if {g}:GetCount()>0 and {space} then");
            body.AddRange(ApplyLines(g, r, ctx).Select(l => "\t" + l));
            body.AddRange(After(g, f).Select(l => "\t" + l));
            body.Add("end");
            e.AddStep(body, r, ctx.Link);
            return;
        }
        var check = $"Duel.GetMatchingGroup({f.Function},tp,{f.Self},{f.Opp},{f.Exclude},e,tp)";
        e.TargetChecks.Add(rule is { } r0 ? r0.Check(check) : $"Duel.IsExistingMatchingCard({f.Function},tp,{f.Self},{f.Opp},{min},{f.Exclude},e,tp)");
        if (Category.Length > 0) e.TargetLines.Add($"Duel.SetOperationInfo(0,{Category},nil,{min},{f.InfoPlayer},{f.InfoLocation})");
        body.Add($"local {r}=0");
        if (ct is not null) body.Add($"local {ct}={counted}");
        var guard = new[] { space, ct is null ? null : $"{ct}>0" }.Where(x => x is not null).ToList();
        if (guard.Count > 0) body.Add($"if {string.Join(" and ", guard)} then");
        var ind = guard.Count == 0 ? "" : "\t";
        var pg = e.NewLocal("pg");
        if (random || rule is not null) body.Add($"{ind}local {pg}=Duel.GetMatchingGroup({f.ForSelection},tp,{f.Self},{f.Opp},{f.Exclude},e,tp)");
        if (random) body.Add($"{ind}local {g}={pg}:RandomSelect(tp,{Num(n)})");
        else
        {
            body.Add($"{ind}Duel.Hint(HINT_SELECTMSG,{who},{Hint})");
            body.Add(rule is { } picked
                ? $"{ind}local {g}={picked.Select(pg)}"
                : $"{ind}local {g}=Duel.SelectMatchingCard({who},{f.ForSelection},tp,{f.Self},{f.Opp},{min},{max},{f.Exclude},e,tp)");
        }
        // A group the cards don't fit is no pick at all.
        body.Add($"{ind}if {g} and {g}:GetCount()>0 then");
        if ((filter.Zones & Zone.Field) != Zone.None) body.Add($"{ind}\tDuel.HintSelection({g})");
        body.AddRange(ApplyLines(g, r, ctx).Select(l => $"{ind}\t{l}"));
        body.AddRange(After(g, f).Select(l => $"{ind}\t{l}"));
        body.Add($"{ind}end");
        if (guard.Count > 0) body.Add("end");
        e.AddStep(body, r, ctx.Link);
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// The first DO block of an effect with "●" options, saying how the option is chosen: "activate 1 of these effects"
/// (as the effect is activated, the default without this block), "apply 1 of these effects" (as it resolves), "your
/// opponent chooses 1 of these effects for you to apply", and "(but you can only use each effect of "X" once per turn)".
/// Under it, an IF block that starts an option is what that option needs to be chosen: "● If you control no monsters: ...".
/// </summary>
public sealed class ChooseEffectsAction : ActionBlock
{
    public const string BlockId = "do.choose_effects";
    public override string Id => BlockId;
    public override string Group => "Options";
    public override string Name => "Choose 1 of these effects (how the \"●\" options are chosen)";
    public override string Sentence => "The \"●\" options below: {who} {count} of them {when}";
    public override string Help => "Put it first among the DO blocks, then link each option's first block with \"●\". Without it, you choose an option as " +
                                   "the effect is activated. \"When it resolves\" reads \"apply 1 of these effects\"; your opponent always chooses as it resolves. " +
                                   "An IF block that starts an option is what that option needs: \"● If you control no monsters: ...\". " +
                                   "A COST block that starts an option is that option's own cost: \"● Discard 1 card; draw 1 card\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who chooses", new("you", "", "you choose"), new("opponent", "", "your opponent chooses")),
        // "Activate 1 or both of these effects": with 2 options, both can be chosen, applied one after the other.
        ParamSpec.Choice("count", "How many", new("one", "1"), new("both", "1 or both"))
            .ShowIf(b => b.Values.GetValueOrDefault("who", "you") == "you"),
        ParamSpec.Choice("when", "When", new("activate", "", "as the effect is activated"), new("apply", "", "as the effect resolves"))
            .ShowIf(b => b.Values.GetValueOrDefault("who", "you") == "you"),
        ParamSpec.Flag("once_each", "you can only use each option once per turn", false),
    ];

    /// <summary>The header block of these DO blocks, or null.</summary>
    public static BlockInstance? Header(IReadOnlyList<BlockInstance> blocks) => blocks.Count > 0 && blocks[0].BlockId == BlockId ? blocks[0] : null;

    /// <summary>The DO blocks without the header: the options themselves.</summary>
    public static List<BlockInstance> Options(IReadOnlyList<BlockInstance> blocks) => Header(blocks) is null ? blocks.ToList() : blocks.Skip(1).ToList();

    /// <summary>"Activate 1 or both of these effects".</summary>
    public static bool Both(BlockInstance header) =>
        header.Values.GetValueOrDefault("count") == "both" && header.Values.GetValueOrDefault("who", "you") == "you";

    /// <summary>"1 or both" for the effect's options: the option name the game lists for choosing both.</summary>
    public static bool ChoosesBoth(EffectEvent effect) => Header(effect.Do) is { } h && Both(h);

    public const string BothText = "Both of these effects.";

    private static bool Resolving(BlockInstance header) =>
        header.Values.GetValueOrDefault("who") == "opponent" || header.Values.GetValueOrDefault("when") == "apply";

    /// <summary>The header itself reads as nothing: <see cref="Lead"/> is its wording before the options.</summary>
    public override string Describe(BlockArgs args) => "";

    /// <summary>
    /// The words before the options, with their punctuation: "You can activate 1 of these effects (but you can only use
    /// each effect of "X" once per turn);", "Apply 1 of these effects.", "You can activate this effect; your opponent
    /// chooses 1 of these effects for you to apply." Null without a header.
    /// </summary>
    public static string? Lead(EffectEvent effect, string costs, bool optional, string cardName)
    {
        if (Header(effect.Do) is not { } h) return null;
        var once = h.Values.GetValueOrDefault("once_each") == "true";
        string choose;
        if (h.Values.GetValueOrDefault("who") == "opponent")
        {
            choose = "your opponent chooses 1 of these effects for you to apply";
            if (once) choose += $", but you cannot apply that same effect of \"{cardName}\" again this turn";
            var lead = costs.Length > 0 ? $"{costs}; {choose}" : optional ? $"activate this effect; {choose}" : choose;
            return (optional ? "You can " + lead : SentenceCase.Upper(lead)) + ".";
        }
        var apply = h.Values.GetValueOrDefault("when") == "apply";
        var count = Both(h) ? "1 or both" : "1";
        choose = apply ? $"apply {count} of these effects" : $"activate {count} of these effects";
        if (once) choose += apply ? $", but you cannot apply that same effect of \"{cardName}\" again this turn" : $" (but you can only use each effect of \"{cardName}\" once per turn)";
        var body = costs.Length == 0 ? choose : $"{costs}, then {choose}";
        return (optional ? "You can " + body : SentenceCase.Upper(body)) + (apply ? "." : ";");
    }

    /// <summary>True for an IF block that starts an option under a header: the option's own condition ("● If ...:").</summary>
    public static bool IsOptionCondition(IReadOnlyList<BlockInstance> doBlocks, BlockInstance b)
    {
        if (Header(doBlocks) is null || Chance.IsGate(b) || BlockRegistry.Find(b.BlockId) is not ConditionBlock) return false;
        var options = Options(doBlocks);
        var at = options.IndexOf(b);
        return at == 0 || at > 0 && b.Link == ActionLink.Option;
    }

    /// <summary>The flag "you can only use each effect once per turn" keeps for one option.</summary>
    private static string Flag(EffectBuilder e, int branch) => $"id+{Num(5000 + 16 * (e.Index - 1) + branch)}";

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (!ReferenceEquals(Header(e.Source.Do), ctx.Args.Instance))
        {
            e.Error("\"Choose 1 of these effects\" goes first among the DO blocks, before the \"●\" options.");
            return;
        }
        if (ctx.Args.Instance.Optional) e.Warn("\"Choose 1 of these effects\" can't be optional; make the effect optional instead (its WHEN).");
        var options = Options(e.Source.Do);
        if (options.Count == 0 || options.Skip(1).Count(b => b.Link == ActionLink.Option) == 0)
            e.Warn("\"Choose 1 of these effects\" needs at least 2 options: link each option's first DO block with \"●\".");
        e.OptionsOnResolution = Resolving(ctx.Args.Instance);
        e.OptionChooser = ctx.Args.Choice("who") == "opponent" ? "1-tp" : "tp";
        if (ctx.Args.Bool("once_each")) e.OptionFlag = branch => Flag(e, branch);
        if (Both(ctx.Args.Instance))
        {
            if (options.Count == 0 || 1 + options.Skip(1).Count(b => b.Link == ActionLink.Option && !Chance.IsGate(b)) != 2)
                e.Error("\"1 or both\" of these effects needs exactly 2 options.");
            else if (ctx.Args.Bool("once_each"))
                e.Error("\"1 or both\" of these effects can't also be \"each effect once per turn\".");
            else e.OptionBoth = true;
        }
    }

    /// <summary>After the options compiled: options chosen as the effect resolves can't target (targets are chosen at activation).</summary>
    public static void CheckOptions(EffectBuilder e)
    {
        if (e.OptionsOnResolution && e.Branches.Values.Any(b => b.Chkc is not null))
            e.Error("An option chosen as the effect resolves can't target: targets are chosen when the effect is activated. Choose the option as the effect is activated, or turn off targeting.");
        if (e.OptionsOnResolution && e.Branches.Values.Any(b => b.CostLines.Count > 0))
            e.Error("An option's own cost is paid as the effect is activated: choose the option as the effect is activated (\"activate 1 of these effects\").");
        if (!e.OptionBoth || e.Branches.Count != 2) return;
        // "1 or both": a third choice that needs, sets up and does what both options do.
        var (one, two) = (e.Branches.Values.First(), e.Branches.Values.Last());
        if (one.Chkc is not null && two.Chkc is not null)
        {
            e.Error("With \"1 or both\" of these effects, only one of the 2 options can target.");
            return;
        }
        var both = e.BranchOf(e.Branches.Keys.Max() + 1);
        both.Checks.AddRange(one.Checks.Concat(two.Checks));
        both.CostChecks.AddRange(one.CostChecks.Concat(two.CostChecks));
        both.CostLines.AddRange(one.CostLines.Concat(two.CostLines));
        both.Lines.AddRange(one.Lines.Concat(two.Lines));
        foreach (var c in one.Categories.Concat(two.Categories)) if (!both.Categories.Contains(c)) both.Categories.Add(c);
        both.Chkc = one.Chkc ?? two.Chkc;
        both.Combines = [e.Branches.Keys.Min(), e.Branches.Keys.Min() + 1];
    }
}

/// <summary>
/// "Reveal 3 "Monarch" Spells/Traps from your Deck, your opponent chooses 1 for you to add to your hand, and you shuffle
/// the rest back into your Deck": you pick the cards, show them, and your opponent picks (or randomly picks) the one
/// that is added, Special Summoned or Set.
/// </summary>
public sealed class RevealPickAction : ActionBlock
{
    public const string BlockId = "do.reveal_pick";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "Reveal cards from the Deck, your opponent picks 1 for you";
    public override string Sentence => "Reveal {count} {cards}, your opponent {how} 1 for you to {then}, and you {rest}";
    public override string Help => "As \"Pantheism of the Monarchs\" or \"You're in Danger!\": you choose the cards to reveal; the opponent's pick is what you get.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many to reveal", 3, 2, 5),
        new ParamSpec
        {
            Key = "cards", Label = "Which cards", Type = ParamType.Cards, AllowedZones = Zone.Deck,
            DefaultFilter = () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster },
        },
        ParamSpec.Choice("how", "How your opponent picks", new("chooses", "chooses"), new("random", "randomly picks")),
        ParamSpec.Choice("then", "What you do with it", new("hand", "add to your hand"), new("summon", "Special Summon"), new("set", "Set")),
        ParamSpec.Choice("rest", "The rest", new("deck", "shuffle the rest into the Deck"), new("grave", "send the rest to the GY")),
        ParamSpec.Flag("names", "with different names", false),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        var cards = FilterText.Describe(f, args.Pack, plural: true, ownName: args.CardName, withExcept: false, beforeLocation: args.Bool("names") ? "with different names" : null);
        return $"reveal {args.Int("count")} {cards}{args.ExceptText()}, your opponent {args.Describe("how")} 1 for you to {args.Describe("then")}, and you {args.Describe("rest")}";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var a = ctx.Args;
        var filter = a.Cards("cards").Clone();
        if (filter.ThisCard || filter.Zones != Zone.Deck)
        {
            e.Error($"\"{Name}\" reveals cards from your Deck: pick cards in your Deck.");
            return;
        }
        filter.Owner = CardOwner.You;
        var then = a.Choice("then");
        if (then == "set" && filter.CardType is not (FilterCardType.Spell or FilterCardType.Trap or FilterCardType.SpellTrap))
        {
            e.Error($"\"{Name}\": only Spells and Traps are Set this way. Pick Spells or Traps, or another way to use the card.");
            return;
        }
        if (then == "summon" && filter.CardType == FilterCardType.Any) filter.CardType = FilterCardType.Monster;
        var able = then switch { "hand" => "c:IsAbleToHand()", "summon" => "c:IsCanBeSpecialSummoned(e,0,tp,false,false)", _ => "c:IsSSetable()" };
        if (a.Choice("rest") == "grave") able += " and c:IsAbleToGrave()";
        var f = ctx.Script.Filter(filter, able);
        var n = Num(a.Int("count"));
        var names = a.Bool("names");
        var pool = $"Duel.GetMatchingGroup({f.Function},tp,LOCATION_DECK,0,{f.Exclude},e,tp)";
        e.TargetChecks.Add(names ? $"{pool}:GetClassCount(Card.GetCode)>={n}" : $"Duel.IsExistingMatchingCard({f.Function},tp,LOCATION_DECK,0,{n},{f.Exclude},e,tp)");
        var room = then switch { "summon" => "Duel.GetLocationCount(tp,LOCATION_MZONE)>0", "set" => "Duel.GetLocationCount(tp,LOCATION_SZONE)>0", _ => null };
        if (then == "set" && filter.SpellTrapKind == SpellTrapKind.Field) room = null;
        if (room is not null) e.TargetChecks.Add(room);
        switch (then)
        {
            case "hand":
                e.AddCategory("CATEGORY_TOHAND");
                e.AddCategory("CATEGORY_SEARCH");
                e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_TOHAND,nil,1,tp,LOCATION_DECK)");
                break;
            case "summon":
                e.AddCategory("CATEGORY_SPECIAL_SUMMON");
                e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_DECK)");
                break;
        }
        if (a.Choice("rest") == "grave")
        {
            e.AddCategory("CATEGORY_TOGRAVE");
            e.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_TOGRAVE,nil,{Num(a.Int("count") - 1)},tp,LOCATION_DECK)");
        }
        var (pg, sg, tg, r) = (e.NewLocal("pg"), e.NewLocal("sg"), e.NewLocal("tg"), e.NewLocal("r"));
        var hint = then switch { "hand" => "HINTMSG_ATOHAND", "summon" => "HINTMSG_SPSUMMON", _ => "HINTMSG_SET" };
        var enough = names ? $"{pg}:GetClassCount(Card.GetCode)>={n}" : $"{pg}:GetCount()>={n}";
        var lines = new List<string>
        {
            $"local {r}=0",
            $"local {pg}=Duel.GetMatchingGroup({f.Function},tp,LOCATION_DECK,0,{f.Exclude},e,tp)",
            $"if {enough}{(room is null ? "" : " and " + room)} then",
            "\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_CONFIRM)",
            names ? $"\tlocal {sg}={pg}:SelectSubGroup(tp,aux.dncheck,false,{n},{n})" : $"\tlocal {sg}={pg}:Select(tp,{n},{n},nil)",
            $"\tif {sg} then",
            $"\t\tDuel.ConfirmCards(1-tp,{sg})",
        };
        if (a.Choice("how") == "random") lines.Add($"\t\tlocal {tg}={sg}:RandomSelect(1-tp,1)");
        else lines.AddRange([$"\t\tDuel.Hint(HINT_SELECTMSG,1-tp,{hint})", $"\t\tlocal {tg}={sg}:Select(1-tp,1,1,nil)"]);
        lines.Add("\t\tDuel.ShuffleDeck(tp)");
        lines.AddRange(then switch
        {
            // The card was just shown to both players: it isn't shown again as it is added.
            "hand" => [$"\t\t{tg}:GetFirst():SetStatus(STATUS_TO_HAND_WITHOUT_CONFIRM,true)", $"\t\t{r}=Duel.SendtoHand({tg},nil,REASON_EFFECT)"],
            "summon" => [$"\t\t{r}=Duel.SpecialSummon({tg},0,tp,tp,false,false,POS_FACEUP)"],
            _ => [$"\t\t{r}=Duel.SSet(tp,{tg})"],
        });
        if (a.Choice("rest") == "grave")
        {
            lines.Add($"\t\t{sg}:Sub({tg})");
            lines.Add($"\t\tDuel.SendtoGrave({sg},REASON_EFFECT)");
        }
        lines.AddRange(["\tend", "end"]);
        e.AddStep(lines, r, ctx.Link);
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// "Target 1 monster you control and 1 card your opponent controls; destroy them": two kinds of targets chosen
/// together, each with its own picker, and one action on all of them.
/// </summary>
public sealed class PairTargetAction : ActionBlock
{
    public const string BlockId = "do.target_pair";
    public override string Id => BlockId;
    public override string Group => "Cards";
    public override string Name => "Target cards of two kinds (1 you control and 1 your opponent controls)";
    public override string Sentence => "Target {count} {cards} and {count2} {cards2}; {action} them";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many of the first", 1, 1, 3),
        ParamSpec.Cards("cards", "The first targets", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
        ParamSpec.Int("count2", "How many of the second", 1, 1, 3),
        ParamSpec.Cards("cards2", "The second targets", () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Field }),
        ParamSpec.Choice("action", "What happens to them",
            new("destroy", "destroy"), new("banish", "banish"), new("hand", "return to the hand"), new("deck", "shuffle into the Deck"), new("grave", "send to the GY")),
    ];

    private static (string Category, string Hint, string? Able, string Apply) Way(string action) => action switch
    {
        "banish" => ("CATEGORY_REMOVE", "HINTMSG_REMOVE", "c:IsAbleToRemove()", "Duel.Remove({0},POS_FACEUP,REASON_EFFECT)"),
        "hand" => ("CATEGORY_TOHAND", "HINTMSG_RTOHAND", "c:IsAbleToHand()", "Duel.SendtoHand({0},nil,REASON_EFFECT)"),
        "deck" => ("CATEGORY_TODECK", "HINTMSG_TODECK", "c:IsAbleToDeck()", "Duel.SendtoDeck({0},nil,SEQ_DECKSHUFFLE,REASON_EFFECT)"),
        "grave" => ("CATEGORY_TOGRAVE", "HINTMSG_TOGRAVE", "c:IsAbleToGrave()", "Duel.SendtoGrave({0},REASON_EFFECT)"),
        _ => ("CATEGORY_DESTROY", "HINTMSG_DESTROY", null, "Duel.Destroy({0},REASON_EFFECT)"),
    };

    /// <summary>
    /// True when no card can be both kinds of target: controlled by different players, in different places, face-up and
    /// face-down, or a monster and a Spell/Trap.
    /// </summary>
    public static bool Disjoint(CardFilter a, CardFilter b)
    {
        if (a.Owner != CardOwner.Either && b.Owner != CardOwner.Either && a.Owner != b.Owner) return true;
        if ((a.Zones & b.Zones) == Zone.None) return true;
        if (a.FaceUpOnly && b.FaceDownOnly || a.FaceDownOnly && b.FaceUpOnly) return true;
        static bool Monster(CardFilter f) => f.CardType == FilterCardType.Monster;
        static bool SpellTrap(CardFilter f) => f.CardType is FilterCardType.Spell or FilterCardType.Trap or FilterCardType.SpellTrap;
        return Monster(a) && SpellTrap(b) || SpellTrap(a) && Monster(b) || a.CardType is FilterCardType.Spell && b.CardType is FilterCardType.Trap
               || a.CardType is FilterCardType.Trap && b.CardType is FilterCardType.Spell;
    }

    private static string Cards(BlockArgs a, string count, string cards)
    {
        var n = a.Int(count);
        return $"{n} {FilterText.Describe(a.Cards(cards), a.Pack, plural: n != 1, ownName: a.CardName)}";
    }

    public override (string Target, string Action)? DescribeTargeting(BlockArgs args)
    {
        var verb = args.Choice("action") switch { "hand" => "return them to the hand", "deck" => "shuffle them into the Deck", "grave" => "send them to the GY", var v => v + " them" };
        return ($"target {Cards(args, "count", "cards")} and {Cards(args, "count2", "cards2")}", verb);
    }

    public override string Describe(BlockArgs args) => DescribeTargeting(args) is { } t ? $"{t.Target}; {t.Action}" : "";

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var a = ctx.Args;
        var (f1, f2) = (a.Cards("cards"), a.Cards("cards2"));
        foreach (var f in new[] { f1, f2 })
        {
            if (f.ThisCard || f.Zones == Zone.None || (f.Zones & (Zone.Deck | Zone.Hand | Zone.ExtraDeck)) != Zone.None)
            {
                e.Error($"\"{Name}\": targets are cards on the field, in the GY or banished. Pick those places for both kinds of targets.");
                return;
            }
        }
        // The same card could be both kinds of target: the blocks pick two kinds that can't overlap.
        if (!Disjoint(f1, f2))
        {
            e.Error($"\"{Name}\": the two kinds of targets must be in different places or controlled by different players (\"you control\" and \"your opponent controls\").");
            return;
        }
        var (category, hint, able, apply) = Way(a.Choice("action"));
        var r1 = ctx.Script.Filter(f1, able);
        var r2 = ctx.Script.Filter(f2, able);
        var (n1, n2) = (a.Int("count").ToString(CultureInfo.InvariantCulture), a.Int("count2").ToString(CultureInfo.InvariantCulture));
        e.AddCategory(category);
        e.SetChkc("false");
        e.TargetChecks.Add($"Duel.IsExistingTarget({r1.Function},tp,{r1.Self},{r1.Opp},{n1},{r1.Exclude},e,tp)");
        e.TargetChecks.Add($"Duel.IsExistingTarget({r2.Function},tp,{r2.Self},{r2.Opp},{n2},{r2.Exclude},e,tp)");
        var (g1, g2) = (e.NewLocal("tg"), e.NewLocal("tg"));
        e.TargetLines.AddRange(
        [
            $"Duel.Hint(HINT_SELECTMSG,tp,{hint})",
            $"local {g1}=Duel.SelectTarget(tp,{r1.Function},tp,{r1.Self},{r1.Opp},{n1},{n1},{r1.Exclude},e,tp)",
            $"Duel.Hint(HINT_SELECTMSG,tp,{hint})",
            $"local {g2}=Duel.SelectTarget(tp,{r2.Function},tp,{r2.Self},{r2.Opp},{n2},{n2},{r2.Exclude},e,tp)",
            $"{g1}:Merge({g2})",
            $"Duel.SetOperationInfo(0,{category},{g1},{g1}:GetCount(),0,0)",
        ]);
        var (g, r) = (e.NewLocal("g"), e.NewLocal("r"));
        e.AddStep(
        [
            $"local {g}=Duel.GetChainInfo(0,CHAININFO_TARGET_CARDS):Filter(Card.IsRelateToEffect,nil,e)",
            $"local {r}=0",
            $"if {g}:GetCount()>0 then",
            $"\t{r}={string.Format(CultureInfo.InvariantCulture, apply, g)}",
            "end",
        ], r, ctx.Link);
    }
}
