using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// Counters: which kind a block counts, places or removes ("Spell Counter", "A-Counter", any counter), with their
/// engine ids. Card.GetCounter(0) is the number of kinds of counters on a card, not the number of counters, so "any
/// counter" adds every kind up.
/// </summary>
public static class Counters
{
    /// <summary>The choice of counter, and the id typed in when it is "another counter".</summary>
    public const string KindKey = "counter";
    public const string IdKey = "counter_id";
    public const string Any = "any";
    public const string Custom = "custom";

    /// <summary>
    /// The counters cards name, with their engine ids (the low byte; 0x1000 means any card can hold it without being
    /// allowed to, like A-Counters). From the official scripts and card texts.
    /// </summary>
    public static readonly IReadOnlyList<(int Id, string Name)> Known =
    [
        (0x1, "Spell Counter"), (0x100e, "A-Counter"), (0x1041, "Predator Counter"), (0x1019, "Fog Counter"), (0x37, "Kaiju Counter"),
        (0x3, "Bushido Counter"), (0x6a, "Resonance Counter"), (0x1009, "Venom Counter"), (0x11, "Hyper-Venom Counter"), (0x1015, "Ice Counter"),
        (0x16, "Spellstone Counter"), (0x1038, "Cubic Counter"), (0x10, "Black Feather Counter"), (0x1002, "Wedge Counter"), (0x71, "E.C. Counter"),
        (0x35, "Symphonic Counter"), (0x8, "Morph Counter"), (0x104d, "Signal Counter"), (0x1b, "Clock Counter"), (0x4c, "Summon Counter"),
        (0x33, "Yosen Counter"), (0x104f, "Venemy Counter"), (0x25, "Chronicle Counter"), (0x1024, "String Counter"), (0x1063, "Hallucination Counter"),
        (0x6e, "Season Counter"), (0xf, "Worm Counter"), (0x5, "Shine Counter"), (0x4, "Psychic Counter"), (0xa, "Genex Counter"),
        (0xc, "Thunder Counter"), (0xd, "Greed Counter"), (0x12, "Karakuri Counter"), (0x13, "Chaos Counter"), (0x1d, "Junk Counter"),
        (0x1e, "Gate Counter"), (0x20, "Plant Counter"), (0x22, "Dragonic Counter"), (0x23, "Ocean Counter"), (0x2b, "Destiny Counter"),
        (0x6, "Crystal Counter"), (0x18, "Flower Counter"), (0x1021, "Guard Counter"), (0x2e, "Shark Counter"), (0x36, "Performage Counter"),
        (0x4a, "Athlete Counter"), (0x6c, "Access Counter"), (0x64, "GG Counter"), (0x1045, "Scale Counter"), (0x1049, "Patrol Counter"),
        (0x105c, "Burnup Counter"), (0x1a, "Payback Counter"), (0x2f, "Pumpkin Counter"), (0x43, "Defect Counter"), (0x56, "Fire Fist Counter"),
        (0x17, "Nut Counter"), (0x1039, "Zushin Counter"), (0x32, "Balloon Counter"), (0x5f, "Piece Counter"), (0x78, "Myriad Counter"),
        (0x1072, "Maiden Counter"), (0x57, "Phantasm Counter"), (0x4b, "Borrel Counter"), (0x6d, "Schoolwork Counter"), (0x75, "VIP Counter"),
    ];

    /// <summary>The B.E.S. monsters' counter has no name: "Place 3 counters on it".</summary>
    private const int Unnamed = 0x1f;

    public static string Hex(int id) => "0x" + id.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>The choices: "any counter" (when counting), the known counters, an unnamed one, and one typed in.</summary>
    public static ChoiceOption[] Kinds(bool withAny) =>
    [
        .. withAny ? [new ChoiceOption(Any, "counter", "any counter (all kinds)")] : Array.Empty<ChoiceOption>(),
        .. Known.Select(k => new ChoiceOption(Hex(k.Id), k.Name, $"{k.Name} ({Hex(k.Id)})")),
        new(Hex(Unnamed), "counter", $"counter, unnamed (B.E.S., {Hex(Unnamed)})"),
        new(Custom, "counter", "another counter (its id below)"),
    ];

    /// <summary>
    /// The counter blanks: which counter, and its id when it is another one. <paramref name="shown"/> says when the
    /// blanks apply (a value that counts counters); <paramref name="suffix"/> tells a second value's apart.
    /// </summary>
    public static ParamSpec[] Params(bool withAny, Func<BlockInstance, bool>? shown = null, string suffix = "")
    {
        var kind = KindKey + suffix;
        var first = withAny ? Any : Hex(Known[0].Id);
        var choice = ParamSpec.Choice(kind, "Counter", Kinds(withAny));
        var id = ParamSpec.HexInt(IdKey + suffix, "Counter id (hex, e.g. 0x1 for Spell Counters)", 0x1, 1, 0xffff)
            .ShowIf(b => (shown?.Invoke(b) ?? true) && b.Values.GetValueOrDefault(kind, first) == Custom);
        if (shown is not null) choice.ShowIf(shown);
        return [choice, id];
    }

    /// <summary>The counter's engine id, or null for any counter.</summary>
    public static int? Id(BlockArgs args, string suffix = "")
    {
        var kind = KindKey + suffix;
        if (args.Definition.Param(kind) is null) return null;
        return args.Choice(kind) switch
        {
            Any => null,
            Custom => args.Int(IdKey + suffix),
            var hex => int.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The choice value for an id: a known counter, or another one (the id then goes in <see cref="IdKey"/>).</summary>
    public static string ValueOf(int? id) => id is not { } n ? Any : Known.Any(k => k.Id == n) || n == Unnamed ? Hex(n) : Custom;

    /// <summary>Sets a block's counter blanks to the counter with this id (null for any counter).</summary>
    public static BlockInstance WithCounter(this BlockInstance b, int? id, string suffix = "")
    {
        b.Values[KindKey + suffix] = ValueOf(id);
        if (ValueOf(id) == Custom) b.Values[IdKey + suffix] = id!.Value.ToString(CultureInfo.InvariantCulture);
        return b;
    }

    /// <summary>The counter a placed block's blanks name, or null for any counter (or none set).</summary>
    public static int? IdOf(BlockInstance b, string suffix = "") => b.Values.GetValueOrDefault(KindKey + suffix) switch
    {
        null or Any => null,
        Custom => int.TryParse(b.Values.GetValueOrDefault(IdKey + suffix), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0x1,
        var hex => int.TryParse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) ? n : null,
    };

    /// <summary>"Spell Counter", or "counter" for any counter or one without a name.</summary>
    public static string Name(int? id) => id is { } n && Known.FirstOrDefault(k => k.Id == n) is { Name: { } name } ? name : "counter";

    public static string Name(BlockArgs args, string suffix = "") => Name(Id(args, suffix));

    /// <summary>"1 Spell Counter", "3 Spell Counters", "a counter" when <paramref name="article"/>.</summary>
    public static string Amount(int n, string name, bool article = false) =>
        n == 1 ? (article ? ("AEIOU".Contains(name[0]) ? "an " : "a ") + name : "1 " + name) : $"{n.ToString(CultureInfo.InvariantCulture)} {name}s";

    /// <summary>The Lua function that adds up every counter on a card: GetCounter(0) only says how many kinds it has.</summary>
    public static string Total(ScriptBuilder script) => script.AddFunction("allcounters", "c",
    [
        "if c:GetCounter(0)==0 then return 0 end",
        "local n=0",
        "for t=1,0xff do n=n+c:GetCounter(t)+c:GetCounter(t|0x1000) end",
        "return n",
    ]);

    /// <summary>How many of the counter are on <paramref name="card"/>: "c:GetCounter(0x1)", or every counter added up.</summary>
    public static string On(ScriptBuilder script, string card, int? id) =>
        id is { } n ? $"{card}:GetCounter({Hex(n)})" : $"{Total(script)}({card})";

    /// <summary>
    /// The same as one expression, for a card picker's requirement (which can't add a function): every counter is added
    /// up by a function written in place.
    /// </summary>
    public static string Inline(string card, int? id) => id is { } n
        ? $"{card}:GetCounter({Hex(n)})"
        : $"(function(x) local n=0 for t=1,0xff do n=n+x:GetCounter(t)+x:GetCounter(t|0x1000) end return n end)({card})";

    /// <summary>The total of the counter on a group: "g:GetSum(Card.GetCounter,0x1)".</summary>
    public static string SumOf(ScriptBuilder script, string group, int? id) =>
        id is { } n ? $"{group}:GetSum(Card.GetCounter,{Hex(n)})" : $"{group}:GetSum({Total(script)})";

    /// <summary>
    /// Lets this card hold the counter, as a card that places it on itself must: the engine only puts a counter on a
    /// card allowed to have it, unless the counter's id says any card may (0x1000). <paramref name="max"/> is "(max. 3)".
    /// </summary>
    public static void Permit(BlockContext ctx, int id, int max = 0)
    {
        if ((id & 0x1000) == 0) ctx.Script.AddInitialLine($"c:EnableCounterPermit({Hex(id)})");
        if (max > 0) ctx.Script.AddInitialLine($"c:SetCounterLimit({Hex(id)},{max.ToString(CultureInfo.InvariantCulture)})");
    }

    /// <summary>"the field", "your field", "your opponent's field" for any card on the field, else "monsters you control".</summary>
    public static string Where(CardFilter f, CardPack? pack)
    {
        var plain = new CardFilter { Owner = f.Owner, Zones = f.Zones };
        if (f.Zones == Zone.Field && FilterCompiler.Conditions(f).SequenceEqual(FilterCompiler.Conditions(plain)) && !f.ExcludeThisCard)
            return f.Owner switch { CardOwner.You => "your field", CardOwner.Opponent => "your opponent's field", _ => "the field" };
        return "the " + CardCounts.Group(f, pack);
    }
}

/// <summary>
/// "Place 1 Spell Counter on this card", "target 1 face-up monster your opponent controls; place 1 A-Counter on it",
/// "place 1 Wedge Counter on each face-up monster on the field".
/// </summary>
public sealed class PlaceCounterAction : CardGroupAction
{
    public const string BlockId = "do.place_counter";
    public override string Id => BlockId;
    public override string Group => "Counters";
    public override string Name => "Place counters on cards (Spell Counters, A-Counters...)";
    public override string Sentence => "Place {by} {amount} {counter} {counter_id} on {count} {cards} {target} {max} {value} {mem2} {counter2} {counter_id2} {of} {status}";
    public override string Help => "A card that places counters on itself can hold them; other cards need to be able to (A-Counters and other 0x1000 counters go on any card). " +
                                   "\"Equal to a value\" places as many as it says (Autumn: \"equal to the number of cards in your opponent's hand\"); \"all of them on this card\" " +
                                   "moves this card's counters onto the cards (\"place all Season Counters on this card on that card\"). Cubic Counters can carry what they do " +
                                   "(\"monsters with a Cubic Counter cannot attack, also negate their effects\").";
    protected override string Category => "CATEGORY_COUNTER";
    protected override string Hint => "HINTMSG_COUNTER";
    protected override string TargetedWording => "{0}";
    protected override bool AllowsThisCard => true;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { ThisCard = true };

    private static string By(BlockInstance b) => b.Values.GetValueOrDefault("by", "number");

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Choice("by", "How many counters", new("number", "", "a number"), new("value", "", "equal to a value:"),
            new("here", "", "all of them on this card (moved onto the cards)")),
        ParamSpec.Int("amount", "How many counters", 1, 1, 99).ShowIf(b => By(b) == "number"),
        .. Counters.Params(withAny: false),
        ParamSpec.Int("count", "On how many cards", 1, 1, 10),
        ModeParam,
        ParamSpec.Cards("cards", "On which cards", DefaultCards, Zone.Field, thisCard: true),
        ParamSpec.Flag("target", "(targeting)", false),
        ParamSpec.Int("max", "Most this card can hold (0 for no limit)", 0, 0, 99).ShowIf(b => b.Filters.TryGetValue("cards", out var f) && f.ThisCard),
        // "Equal to the number of cards in your opponent's hand": the value, with its own counter blanks for values that count counters.
        ParamSpec.Choice("value", "Equal to", ValueSources.Sources).ShowIf(b => By(b) == "value"),
        ValueSources.Also(ValueSources.MemoryParam("value", "2"), b => By(b) == "value"),
        .. Counters.Params(withAny: true, b => By(b) == "value" && ValueSources.UsesCounter(b.Values.GetValueOrDefault("value", "lp_diff")), "2"),
        ParamSpec.Cards(ValueSources.CardsKey, "Which cards (for counts and totals)", () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Hand })
            .ShowIf(b => By(b) == "value" && ValueSources.UsesCards(b.Values.GetValueOrDefault("value", "lp_diff"))).WhenUsed(),
        // Cubic Counters: "monsters with a Cubic Counter cannot attack, also negate their effects", while they have one.
        ParamSpec.Choice("status", "Monsters with it", new("none", "", "(nothing more)"),
            new("no_attack_negated", "cannot attack, also negate their effects"), new("no_attack", "cannot attack"), new("negated", "have their effects negated")),
    ];

    /// <summary>"1 Spell Counter", "Season Counters" (as many as a value), "all Season Counters on this card".</summary>
    private static string What(BlockArgs args) => By(args.Instance) switch
    {
        "value" => Counters.Name(args) + "s",
        "here" => $"all {Counters.Name(args)}s on this card",
        _ => Counters.Amount(args.Int("amount"), Counters.Name(args)),
    };

    /// <summary>" equal to the number of cards in your opponent's hand", or "".</summary>
    private static string EqualTo(BlockArgs args)
    {
        if (By(args.Instance) != "value") return "";
        var value = args.Choice("value");
        var of = value == "cost_count" ? "the number of " + ValueSources.CostNoun(args.Effect, plural: true)
            : ValueSources.Of(value, args.Cards(ValueSources.CardsKey), args.Pack, "this card", Counters.Name(args, "2"), args.Effect);
        return " equal to " + of;
    }

    /// <summary>" (monsters with a Cubic Counter cannot attack, also negate their effects)", or "".</summary>
    private static string Status(BlockArgs args) => args.Choice("status") switch
    {
        "none" => "",
        var s => $" (monsters with {Counters.Amount(1, Counters.Name(args), article: true)} {args.Describe("status")})",
    };

    private static string Max(BlockArgs args) => args.Cards("cards").ThisCard && args.Int("max") > 0 ? $" (max. {args.Int("max")})" : "";

    public override string Describe(BlockArgs args)
    {
        var tail = EqualTo(args) + Status(args);
        if (args.IsThisCard) return $"place {What(args)} on this card{tail}{Max(args)}";
        // "place all Season Counters on this card on that card".
        if (args.IsThatCard && By(args.Instance) == "here") return $"place {What(args)} on {(CardRefs.Plural(args, args.Cards("cards").That) ? "those cards" : "that card")}{tail}";
        if (args.Choice("mode") == "all")
            return $"place {What(args)} on each {FilterText.Describe(args.Cards("cards"), args.Pack, plural: false, ownName: args.CardName, withExcept: false)}{tail}{args.ExceptText()}";
        return $"place {What(args)} on {args.Describe("count")} {args.Describe("cards")}{tail}{args.ExceptText()}";
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) =>
        $"place {What(args)} on {(single ? "it" : "each of them")}{EqualTo(args)}{Status(args)}";

    private static int CounterId(BlockContext ctx) => Counters.Id(ctx.Args) ?? 0x1;

    /// <summary>The Lua number of counters placed: the number, the value as it resolves, or this card's counters (kept as the effect began).</summary>
    private static string Amount(BlockContext ctx) => By(ctx.Args.Instance) switch
    {
        "value" => ValueSources.Lua(ctx, "e:GetHandler()", "tp", counterSuffix: "2"),
        "here" => Here(ctx),
        _ => ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// This card's counters, counted as the effect begins to resolve: a Field Spell that places another one leaves the
    /// field before its counters are placed on it (Autumn).
    /// </summary>
    private static string Here(BlockContext ctx)
    {
        var v = $"here{ctx.Effect.Index.ToString(CultureInfo.InvariantCulture)}";
        var line = $"local {v}=c:GetCounter({Counters.Hex(CounterId(ctx))})";
        if (!ctx.Effect.OperationPrologue.Contains(line)) ctx.Effect.OperationPrologue.Add(line);
        return v;
    }

    /// <summary>
    /// What activating an effect that places counters on this card asks, as official cards do: a mandatory trigger
    /// places them if it can; a card being activated (still in the hand) asks whether counters can be placed at all;
    /// anything else, whether they fit on this card.
    /// </summary>
    public static string? ActivationCheck(bool mandatory, bool activation, string id, string n) =>
        mandatory ? null : activation ? $"Duel.IsCanAddCounter(tp,{id},{n},e:GetHandler())" : $"e:GetHandler():IsCanAddCounter({id},{n})";

    /// <summary>A number of counters is checked on each card; a value or this card's counters, 1 of them.</summary>
    private static string Checked(BlockContext ctx) => By(ctx.Args.Instance) == "number" ? ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture) : "1";

    protected override string? AbleCheck(BlockContext ctx) => $"c:IsCanAddCounter({Counters.Hex(CounterId(ctx))},{Checked(ctx)})";

    protected override string Apply(string g, BlockContext ctx)
    {
        var id = Counters.Hex(CounterId(ctx));
        var n = Amount(ctx);
        var status = ctx.Args.Choice("status");
        string call;
        if (status == "none")
        {
            var fn = ctx.Script.AddFunction("addcounter", "g,id,n", ["local r=0", "for tc in aux.Next(g) do", "\tif tc:AddCounter(id,n) then r=r+1 end", "end", "return r"]);
            call = $"{fn}({g},{id},{n})";
        }
        else
        {
            // As the official Cubic cards do it: the monster gets what the counter does, while it has one.
            var has = ctx.Script.AddFunction("hascounter", "e", [$"return e:GetHandler():GetCounter({id})>0"]);
            var codes = status switch { "no_attack" => new[] { "EFFECT_CANNOT_ATTACK" }, "negated" => ["EFFECT_DISABLE"], _ => ["EFFECT_CANNOT_ATTACK", "EFFECT_DISABLE"] };
            var body = new List<string> { "local r=0", "for tc in aux.Next(g) do", "\tif tc:AddCounter(id,n) then", "\t\tr=r+1" };
            foreach (var code in codes)
                body.AddRange(
                [
                    "\t\tlocal ce=Effect.CreateEffect(e:GetHandler())",
                    "\t\tce:SetType(EFFECT_TYPE_SINGLE)",
                    "\t\tce:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
                    $"\t\tce:SetCode({code})",
                    $"\t\tce:SetCondition({has})",
                    "\t\tce:SetReset(RESET_EVENT+RESETS_STANDARD)",
                    "\t\ttc:RegisterEffect(ce)",
                ]);
            body.AddRange(["\tend", "end", "return r"]);
            var fn = ctx.Script.AddFunction("addcounterst", "g,id,n,e", body);
            call = $"{fn}({g},{id},{n},e)";
        }
        // Moved: this card has them no more.
        if (By(ctx.Args.Instance) == "here")
        {
            var move = ctx.Script.AddFunction("movecounters", "c,tp,id,n,r",
                ["if r>0 and n>0 and c:GetCounter(id)>=n then c:RemoveCounter(tp,id,n,REASON_EFFECT) end", "return r"]);
            call = $"{move}(c,tp,{id},{n},{call})";
        }
        return call;
    }

    public override void Compile(BlockContext ctx)
    {
        var id = CounterId(ctx);
        var by = By(ctx.Args.Instance);
        if (by == "value" && !ValueSources.AtResolution(ctx.Args.Choice("value")))
            ctx.Effect.TargetChecks.Add($"{ValueSources.Lua(ctx, "e:GetHandler()", "tp", counterSuffix: "2")}>0");
        if (by == "here" && ctx.Args.Cards("cards").ThisCard)
        {
            ctx.Effect.Error($"\"{Name}\": \"all of them on this card\" moves this card's counters onto other cards; pick those cards.");
            return;
        }
        if (by == "here") Counters.Permit(ctx, id);
        if (ctx.Args.Choice("status") != "none" && ctx.Args.Cards("cards").ThisCard)
            ctx.Effect.Warn($"\"{Name}\": what the counter does is for the monsters it is placed on; this card gets it too.");
        var n = Amount(ctx);
        if (!ctx.Args.Cards("cards").ThisCard)
        {
            if (ctx.Args.Int("max") > 0) ctx.Effect.Warn("\"(max. ...)\" is how many this card can hold: it only applies when the counters go on this card.");
            base.Compile(ctx);
            return;
        }
        if (ctx.Args.Bool("target")) { ctx.Effect.Error($"\"{Name}\": this card itself isn't targeted. Turn off targeting."); return; }
        Counters.Permit(ctx, id, ctx.Args.Int("max"));
        var e = ctx.Effect;
        e.AddCategory("CATEGORY_COUNTER");
        if (ActivationCheck(e.IsMandatory, e.IsActivation, Counters.Hex(id), Checked(ctx)) is { } check) e.TargetChecks.Add(check);
        e.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_COUNTER,nil,{(by == "number" ? n : "1")},0,{Counters.Hex(id)})");
        var r = e.NewLocal("r");
        if (ctx.Args.Choice("status") != "none")
        {
            e.AddStep([$"local {r}=0", $"if c:IsRelateToEffect(e) then {r}={Apply("Group.FromCards(c)", ctx)} end"], r, ctx.Link);
            return;
        }
        e.AddStep(
        [
            $"local {r}=0",
            by == "number"
                ? $"if c:IsRelateToEffect(e) and c:AddCounter({Counters.Hex(id)},{n}) then {r}=1 end"
                : $"if c:IsRelateToEffect(e) and {n}>0 and c:AddCounter({Counters.Hex(id)},{n}) then {r}=1 end",
        ], r, ctx.Link);
    }
}

/// <summary>"Remove 1 Spell Counter from this card", "remove 2 Kaiju Counters from anywhere on the field".</summary>
public sealed class RemoveCounterAction : ActionBlock
{
    public const string BlockId = "do.remove_counter";
    public override string Id => BlockId;
    public override string Group => "Counters";
    public override string Name => "Remove counters (from this card or the field)";
    public override string Sentence => "Remove {amount} {counter} {counter_id} from {from}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("amount", "How many counters", 1, 1, 99),
        .. Counters.Params(withAny: false),
        ParamSpec.Choice("from", "From", RemoveCounterCost.Froms),
    ];

    public override string Describe(BlockArgs args) =>
        $"remove {Counters.Amount(args.Int("amount"), Counters.Name(args))} from {args.Describe("from")}";

    public override void Compile(BlockContext ctx)
    {
        var call = RemoveCounterCost.Call(ctx, "REASON_EFFECT");
        var r = ctx.Effect.NewLocal("r");
        // The engine removes them only when there are enough; "if you do" means they were.
        var guard = ctx.Args.Choice("from") == "this" ? "c:IsRelateToEffect(e) and " : "";
        if (ctx.Args.Choice("from") == "this") Counters.Permit(ctx, Counters.Id(ctx.Args) ?? 0x1);
        ctx.Effect.AddStep([$"local {r}=0", $"if {guard}{call.Check} then", $"\t{call.Remove}", $"\t{r}=1", "end"], r, ctx.Link);
    }
}

/// <summary>"Remove 3 Spell Counters from this card;", "remove 2 Kaiju Counters from anywhere on the field;".</summary>
public sealed class RemoveCounterCost : CostBlock
{
    public const string BlockId = "cost.remove_counter";
    public override string Id => BlockId;
    public override string Group => "Counters";
    public override string Name => "Remove counters (Spell Counters, Kaiju Counters...)";
    public override string Sentence => "Remove {amount} {counter} {counter_id} from {from}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("amount", "How many counters", 1, 1, 99),
        .. Counters.Params(withAny: false),
        ParamSpec.Choice("from", "From", Froms),
    ];

    public static readonly ChoiceOption[] Froms =
    [
        new("this", "this card"),
        new("yours", "anywhere on your field", "your field (any of your cards)"),
        new("field", "anywhere on the field", "the field (any card)"),
    ];

    public override string Describe(BlockArgs args) =>
        $"remove {Counters.Amount(args.Int("amount"), Counters.Name(args))} from {args.Describe("from")}";

    /// <summary>The check that the counters can be removed, and the removal.</summary>
    public static (string Check, string Remove) Call(BlockContext ctx, string reason)
    {
        var id = Counters.Hex(Counters.Id(ctx.Args) ?? 0x1);
        var n = ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture);
        return ctx.Args.Choice("from") switch
        {
            "yours" => ($"Duel.IsCanRemoveCounter(tp,1,0,{id},{n},{reason})", $"Duel.RemoveCounter(tp,1,0,{id},{n},{reason})"),
            "field" => ($"Duel.IsCanRemoveCounter(tp,1,1,{id},{n},{reason})", $"Duel.RemoveCounter(tp,1,1,{id},{n},{reason})"),
            _ => ($"e:GetHandler():IsCanRemoveCounter(tp,{id},{n},{reason})", $"e:GetHandler():RemoveCounter(tp,{id},{n},{reason})"),
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var call = Call(ctx, "REASON_COST");
        if (ctx.Args.Choice("from") == "this") Counters.Permit(ctx, Counters.Id(ctx.Args) ?? 0x1);
        ctx.Effect.CostChecks.Add(call.Check);
        ctx.Effect.CostLines.Add(call.Remove);
    }
}

/// <summary>"If this card has 3 or more Spell Counters on it", "if there are no counters on this card".</summary>
public sealed class SelfCountersCondition : ConditionBlock
{
    public const string BlockId = "if.self_counters";
    public override string Id => BlockId;
    public override string Group => "Counters";
    public override string Name => "This card has counters on it";
    public override string Sentence => "This card has {cmp} {amount} {counter} {counter_id}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("cmp", "How many", CardCounts.Compare),
        ParamSpec.Int("amount", "Number", 1, 0, 99),
        .. Counters.Params(withAny: true),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("amount");
        var name = Counters.Name(args);
        return (args.Choice("cmp"), n) switch
        {
            ("exactly", 0) or ("at_most", 0) => $"there are no {name}s on this card",
            ("at_least", 0) => "this card is on the field",
            ("at_least", 1) => $"there is {Counters.Amount(1, name, article: true)} on this card",
            ("exactly", _) => $"there are exactly {Counters.Amount(n, name)} on this card",
            ("at_most", _) => $"there are {n} or fewer {name}s on this card",
            _ => $"there are {n} or more {name}s on this card",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var count = Counters.On(ctx.Script, "e:GetHandler()", Counters.Id(ctx.Args));
        if (Counters.Id(ctx.Args) is { } id) Counters.Permit(ctx, id);
        ctx.Effect.Conditions.Add($"{count}{CardCounts.Op(ctx.Args.Choice("cmp"))}{ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture)}");
    }
}

/// <summary>
/// "Each time a Spell Card is activated, place 1 Spell Counter on this card when that Spell resolves (max. 3)": the
/// Spell Counter cards' way of collecting counters. A Continuous effect marks each activation while this card is face-up
/// (aux.chainreg), and the counter goes on when that activation resolves.
/// </summary>
public sealed class CounterEachTimePassive : PassiveBlock
{
    public const string BlockId = "passive.counter_each_time";
    public override string Id => BlockId;
    public override string Group => "Counters";
    public override string Name => "Each time a Spell / monster effect is activated, place counters on this card";
    public override string Sentence => "Each time {who} {what} is activated, place {amount} {counter} {counter_id} on this card when it resolves {max}";
    public override string Help => "Like Royal Magical Library: only activations while this card is face-up count, and the counter goes on when that card or effect resolves.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Whose", new("any", "", "anyone's"), new("you", "your", "yours"), new("opponent", "your opponent's", "your opponent's")),
        ParamSpec.Choice("what", "What", new("spell", "Spell Card"), new("monster", "monster effect"), new("any", "card or effect")),
        ParamSpec.Int("amount", "How many counters", 1, 1, 99),
        .. Counters.Params(withAny: false),
        ParamSpec.Int("max", "Most this card can hold (0 for no limit)", 0, 0, 99),
    ];

    public override string Describe(BlockArgs args)
    {
        var what = args.Describe("what");
        // "each time a Spell Card is activated", "each time your opponent activates a monster effect".
        var each = args.Choice("who") switch
        {
            "you" => $"each time you activate a {what}",
            "opponent" => $"each time your opponent activates a {what}",
            _ => $"each time a {what} is activated",
        };
        var that = args.Choice("what") switch { "spell" => "that Spell", "monster" => "that effect", _ => "it" };
        var max = args.Int("max") > 0 ? $" (max. {args.Int("max")})" : "";
        return $"{each}, place {Counters.Amount(args.Int("amount"), Counters.Name(args))} on this card when {that} resolves{max}";
    }

    /// <summary>What the activation must be, as the counter step checks it ("rp==1-tp and re:IsActiveType(TYPE_MONSTER)").</summary>
    public static string Check(string who, string what) => string.Join(" and ", new[]
    {
        who switch { "you" => "rp==tp", "opponent" => "rp==1-tp", _ => null },
        what switch { "spell" => "re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:IsActiveType(TYPE_SPELL)", "monster" => "re:IsActiveType(TYPE_MONSTER)", _ => null },
        "e:GetHandler():GetFlagEffect(FLAG_ID_CHAINING)>0",
    }.Where(c => c is not null));

    public override void Compile(BlockContext ctx)
    {
        var id = Counters.Id(ctx.Args) ?? 0x1;
        Counters.Permit(ctx, id, ctx.Args.Int("max"));
        var op = ctx.Script.AddFunction("counterop", "e,tp,eg,ep,ev,re,r,rp",
        [
            $"if {Check(ctx.Args.Choice("who"), ctx.Args.Choice("what"))} then",
            $"\te:GetHandler():AddCounter({Counters.Hex(id)},{ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture)})",
            "end",
        ]);
        var range = RangeOf(ctx);
        // The activation is marked as it is chained, so one that came before this card was face-up doesn't count.
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_CONTINUOUS+EFFECT_TYPE_FIELD)",
            $"{v}:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
            $"{v}:SetCode(EVENT_CHAINING)",
            $"{v}:SetRange({range})",
            $"{v}:SetOperation(aux.chainreg)",
        ]);
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_CONTINUOUS+EFFECT_TYPE_FIELD)",
            $"{v}:SetCode(EVENT_CHAIN_SOLVED)",
            $"{v}:SetRange({range})",
            $"{v}:SetOperation({op})",
        ], limited: true);
    }
}
