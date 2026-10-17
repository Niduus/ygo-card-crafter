using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// Monster Cards placed in the Spell & Trap Zone, and monsters that change Attribute or Type for a while.

/// <summary>
/// "If this card is destroyed while in the Monster Zone, you can place it face-up in your Spell &amp; Trap Zone as a
/// Continuous Spell, instead of sending it to the GY" (the Crystal Beasts), "If a face-up "Millennium" monster(s) you
/// control is destroyed by battle or card effect, you can place it ... instead" (Wedju Temple): the engine's
/// EFFECT_TO_GRAVE_REDIRECT_CB, given to this card or, as a lasting effect, to the monsters a picker allows.
/// </summary>
public sealed class PlaceInsteadPassive : PassiveBlock
{
    public const string BlockId = "passive.place_instead";
    public override string Id => BlockId;
    public override string Group => "Protection";
    public override string Name => "If monsters are destroyed, place them in the Spell & Trap Zone as Continuous Spells instead";
    public override string Sentence => "If {cards} is destroyed {by}, place it face-up in your Spell & Trap Zone as a Continuous Spell, instead of sending it to the GY";
    public override string Help => "Like the Crystal Beasts (this card) or Wedju Temple (\"Millennium\" monsters you control). The game asks whether to place it, " +
                                   "in its controller's Spell & Trap Zone; without a free zone there, it goes to the GY.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monsters", () => new CardFilter { ThisCard = true }, Zone.MonsterZone, thisCard: true),
        ParamSpec.Choice("by", "Destroyed", new("any", "", "however it is destroyed"), new("both", "by battle or card effect"), new("battle", "by battle"), new("effect", "by card effect")),
    ];

    public override string Describe(BlockArgs args)
    {
        var f = args.Cards("cards");
        var by = args.Describe("by");
        const string place = "you can place it face-up in your Spell & Trap Zone as a Continuous Spell, instead of sending it to the GY";
        if (f.ThisCard) return $"if this card is destroyed {(by.Length > 0 ? by + " " : "")}while in the Monster Zone, {place}";
        var noun = FilterText.Describe(f, args.Pack, false, "", withLocation: false, withExcept: false);
        var article = noun.StartsWith("other ", StringComparison.Ordinal) ? "another" : "AEIOUaeiou".Contains(noun[0]) ? "an" : "a";
        if (article == "another") noun = noun[6..];
        var where = f.Owner switch { CardOwner.You => " you control", CardOwner.Opponent => " your opponent controls", _ => " on the field" };
        return $"if {article} {noun}(s){where} is destroyed{(by.Length > 0 ? " " + by : "")}, {place}{args.ExceptText()}";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var reason = ctx.Args.Choice("by") switch { "battle" => "REASON_BATTLE", "effect" => "REASON_EFFECT", "both" => "REASON_BATTLE+REASON_EFFECT", _ => null };
        var test = "c:IsFaceup() and c:IsLocation(LOCATION_MZONE) and c:IsReason(REASON_DESTROY)" + (reason is null ? "" : $" and c:IsReason({reason})");
        // It is treated as a Continuous Spell from the moment it is placed, as the official cards make it.
        var op = ctx.Script.AddFunction("placeop", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local c=e:GetHandler()",
            "local e1=Effect.CreateEffect(c)",
            "e1:SetCode(EFFECT_CHANGE_TYPE)",
            "e1:SetType(EFFECT_TYPE_SINGLE)",
            "e1:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
            "e1:SetReset(RESET_EVENT+RESETS_STANDARD-RESET_TURN_SET)",
            "e1:SetValue(TYPE_SPELL+TYPE_CONTINUOUS)",
            "c:RegisterEffect(e1)",
        ]);
        var filter = ctx.Args.Cards("cards").Clone();
        if (filter.ThisCard)
        {
            Zones.RequireMonster(ctx, Name);
            // Only this card's own effect: the check is the effect's condition, with any IF blocks.
            e.Conditions.Add(test.Replace("c:", "e:GetHandler():", StringComparison.Ordinal));
            e.AddPassive(v =>
            [
                $"{v}:SetType(EFFECT_TYPE_SINGLE)",
                $"{v}:SetCode(EFFECT_TO_GRAVE_REDIRECT_CB)",
                $"{v}:SetProperty(EFFECT_FLAG_UNCOPYABLE)",
                $"{v}:SetOperation({op})",
            ]);
            return;
        }
        filter.Zones = Zone.MonsterZone;
        var f = ctx.Script.Filter(filter);
        var target = ctx.Script.AddFunction("placetg", "e,c", [$"return {f.Function}(c,e,e:GetHandlerPlayer())" + (filter.ExcludeThisCard ? " and c~=e:GetHandler()" : "")]);
        var con = ctx.Script.AddFunction("placecon", "e", ["local c=e:GetHandler()", $"return {test}"]);
        var range = RangeOf(ctx);
        // The monsters get the redirect as their own effect (Wedju Temple's grant).
        e.AddPassive(v =>
        [
            $"local {v}r=Effect.CreateEffect(c)",
            $"{v}r:SetType(EFFECT_TYPE_SINGLE)",
            $"{v}r:SetCode(EFFECT_TO_GRAVE_REDIRECT_CB)",
            $"{v}r:SetProperty(EFFECT_FLAG_UNCOPYABLE+EFFECT_FLAG_CANNOT_DISABLE)",
            $"{v}r:SetCondition({con})",
            $"{v}r:SetOperation({op})",
            $"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_GRANT)",
            $"{v}:SetRange({range})",
            $"{v}:SetTargetRange({f.Self},{f.Opp})",
            $"{v}:SetTarget({target})",
            $"{v}:SetLabelObject({v}r)",
        ]);
    }
}

/// <summary>
/// "Target 1 face-up monster on the field; it becomes DARK until the end of this turn" (Predaplant Verte Anaconda),
/// "declare 1 Attribute; all other monsters on the field become that Attribute until the end of this turn" (Hyperinvoked
/// Aeon): chosen monsters change Attribute or Type for a while.
/// </summary>
public sealed class BecomeAction : CardGroupAction
{
    public const string BlockId = "do.become";
    public override string Id => BlockId;
    public override string Group => "Monsters";
    public override string Name => "Monsters become an Attribute or Type (or the declared one) for a while";
    public override string Sentence => "{count} {cards} {target} become(s) {what} {race} {attribute} {duration}";
    public override string Help => "\"The declared one\" needs a \"Declare 1 Attribute\" (or Type) block before it in the same effect: \"declare 1 Attribute; all other " +
                                   "monsters on the field become that Attribute until the end of this turn\".";
    protected override string Category => "";
    protected override string Hint => "HINTMSG_FACEUP";
    protected override string TargetedWording => "{0}";
    protected override bool AllowsThisCard => true;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    private static readonly ChoiceOption[] RaceChoices = MonsterChoices.Races(r => EnumNames.Display(r));

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ModeParam,
        ParamSpec.Cards("cards", "Which monsters", DefaultCards, Zone.MonsterZone, thisCard: true),
        ParamSpec.Flag("target", "(targeting)", true),
        ParamSpec.Choice("what", "Become",
            new("attribute", "", "an Attribute"), new("race", "", "a Type"),
            new("declared_attribute", "that Attribute", "the declared Attribute"), new("declared_race", "that Type", "the declared Type")),
        new ParamSpec { Key = "race", Label = "Type", Type = ParamType.Choice, Default = RaceChoices[0].Value, Choices = RaceChoices }
            .ShowIf(b => b.Values.GetValueOrDefault("what", "attribute") == "race"),
        new ParamSpec { Key = "attribute", Label = "Attribute", Type = ParamType.Choice, Default = MonsterChoices.Attributes[0].Value, Choices = MonsterChoices.Attributes }
            .ShowIf(b => b.Values.GetValueOrDefault("what", "attribute") == "attribute"),
        ParamSpec.Choice("duration", "Until", Resets.Duration),
    ];

    /// <summary>"DARK", "a Plant monster" / "Plant monsters", "that Attribute".</summary>
    private static string Value(BlockArgs args, bool single) => args.Choice("what") switch
    {
        "attribute" => args.Describe("attribute"),
        "race" => single ? $"{(("AEIOU".Contains(args.Describe("race")[0])) ? "an" : "a")} {args.Describe("race")} monster" : $"{args.Describe("race")} monsters",
        var w => args.Describe("what"),
    };

    private static string Tail(BlockArgs args) => args.Describe("duration") is { Length: > 0 } d ? " " + d : "";

    public override string Describe(BlockArgs args)
    {
        if (args.IsThisCard) return $"this card becomes {Value(args, true)}{Tail(args)}";
        var single = One(args);
        var subject = args.CountMode == "all" ? $"all {args.Describe("cards")}" : Some(args);
        return $"{subject} {(single ? "becomes" : "become")} {Value(args, single)}{Tail(args)}{args.ExceptText()}";
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) =>
        $"{(single ? "it becomes" : "they become")} {Value(args, single)}{Tail(args)}";

    protected override string? AbleCheck(BlockContext ctx) => "c:IsFaceup()";

    public override void Compile(BlockContext ctx)
    {
        var what = ctx.Args.Choice("what");
        if (what.StartsWith("declared_", StringComparison.Ordinal) && Chance.Declared(ctx.Effect) != what["declared_".Length..])
            ctx.Effect.Error($"\"{Name}\": \"that {(what == "declared_race" ? "Type" : "Attribute")}\" needs a \"Declare 1 {(what == "declared_race" ? "Monster Type" : "Attribute")}\" block before it in this effect.");
        base.Compile(ctx);
    }

    protected override string Apply(string g, BlockContext ctx)
    {
        var what = ctx.Args.Choice("what");
        var code = what.EndsWith("race", StringComparison.Ordinal) ? "EFFECT_CHANGE_RACE" : "EFFECT_CHANGE_ATTRIBUTE";
        var value = what switch
        {
            "attribute" => EnumNames.LuaConstant(Enum.Parse<CardAttribute>(ctx.Args.Choice("attribute"))),
            "race" => EnumNames.LuaConstant(Enum.Parse<CardRace>(ctx.Args.Choice("race"))),
            // What the effect declared as it was activated, kept with its chain link.
            _ => "Duel.GetChainInfo(0,CHAININFO_TARGET_PARAM)",
        };
        var reset = Resets.Of(ctx, ctx.Args.Cards("cards").ThisCard ? "+RESET_DISABLE" : "");
        var fn = ctx.Script.AddFunction("becomefn", "g,e",
        [
            $"local v={value}",
            "local n=0", "local tc=g:GetFirst()", "while tc do", "\tif tc:IsFaceup() then",
            "\t\tlocal e1=Effect.CreateEffect(e:GetHandler())", "\t\te1:SetType(EFFECT_TYPE_SINGLE)", $"\t\te1:SetCode({code})",
            "\t\te1:SetValue(v)", $"\t\te1:SetReset({reset})", "\t\ttc:RegisterEffect(e1)", "\t\tn=n+1",
            "\tend", "\ttc=g:GetNext()", "end", "return n",
        ]);
        return $"{fn}({g},e)";
    }
}
