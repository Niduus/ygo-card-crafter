using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// Zones and fields: zones that cannot be used, players who must get rid of monsters, cards taken from an Xyz Monster's
// materials, and monsters sent from the zones a player chooses.

/// <summary>
/// "You can choose 1 unused Main Monster Zone or Spell &amp; Trap Zone; it cannot be used while this monster is face-up
/// on the field" (Kashtira Shangri-Ira, Excode Talker): the zones are chosen as the effect is activated, and kept with it.
/// </summary>
public sealed class DisableZoneAction : ActionBlock
{
    public const string BlockId = "do.disable_zone";
    public override string Id => BlockId;
    public override string Group => "Field";
    public override string Name => "Choose unused zones; they cannot be used";
    public override string Sentence => "Choose {count} unused {zones}; they cannot be used {duration}";
    public override string Help => "The zones are chosen when the effect is activated (before the semicolon), like a target.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many zones", 1, 1, 5),
        ParamSpec.Choice("zones", "Zones",
            new("any", "Main Monster Zone or Spell & Trap Zone", "Main Monster Zones or Spell & Trap Zones (either field)"),
            new("mmz", "Main Monster Zone", "Main Monster Zones (either field)"),
            new("opp_mmz", "Main Monster Zone your opponent controls", "your opponent's Main Monster Zones"),
            new("opp_any", "Main Monster Zone or Spell & Trap Zone your opponent controls", "your opponent's Main Monster Zones or Spell & Trap Zones")),
        ParamSpec.Choice("duration", "For",
            new("face_up", "while this card is face-up on the field"),
            new("turn", "for the rest of this turn"),
            new("next", "until the end of the next turn")),
    ];

    private static string Zones(BlockArgs args)
    {
        var n = args.Int("count");
        return args.Choice("zones") switch
        {
            "mmz" => n == 1 ? "Main Monster Zone" : "Main Monster Zones",
            "opp_mmz" => n == 1 ? "Main Monster Zone your opponent controls" : "Main Monster Zones your opponent controls",
            "opp_any" => n == 1 ? "Main Monster Zone or Spell & Trap Zone your opponent controls" : "Main Monster Zones or Spell & Trap Zones your opponent controls",
            _ => n == 1 ? "Main Monster Zone or Spell & Trap Zone" : "Main Monster Zones or Spell & Trap Zones",
        };
    }

    /// <summary>"while this monster is face-up on the field", as monsters say it.</summary>
    private static string Duration(BlockArgs args) => args.Choice("duration") == "face_up" && args.Card is { IsMonster: true }
        ? "while this monster is face-up on the field" : args.Describe("duration");

    private static string Chosen(BlockArgs args) => $"choose {args.Int("count").ToString(CultureInfo.InvariantCulture)} unused {Zones(args)}";

    private static string Rule(BlockArgs args) => $"{(args.Int("count") == 1 ? "it" : "they")} cannot be used {Duration(args)}";

    public override string Describe(BlockArgs args) => $"{Chosen(args)}; {Rule(args)}";

    public override (string Target, string Action)? DescribeTargeting(BlockArgs args) => (Chosen(args), Rule(args));

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (e.TargetLines.Any(l => l.StartsWith("Duel.SetTargetParam(", StringComparison.Ordinal)))
        {
            e.Error($"\"{Name}\": another block of this effect already keeps a number with the chain link (a declaration or an amount).");
            return;
        }
        var n = ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture);
        var zones = ctx.Args.Choice("zones");
        var (self, opp) = zones switch
        {
            "mmz" => ("LOCATION_MZONE", "LOCATION_MZONE"),
            "opp_mmz" => ("0", "LOCATION_MZONE"),
            "opp_any" => ("0", "LOCATION_ONFIELD"),
            _ => ("LOCATION_ONFIELD", "LOCATION_ONFIELD"),
        };
        // The unused zones that can be chosen, as official cards count them.
        var counts = new List<string>();
        if (self != "0") counts.Add("Duel.GetLocationCount(tp,LOCATION_MZONE,PLAYER_NONE,0)");
        counts.Add("Duel.GetLocationCount(1-tp,LOCATION_MZONE,PLAYER_NONE,0)");
        if (self == "LOCATION_ONFIELD") counts.Add("Duel.GetLocationCount(tp,LOCATION_SZONE,PLAYER_NONE,0)");
        if (opp == "LOCATION_ONFIELD") counts.Add("Duel.GetLocationCount(1-tp,LOCATION_SZONE,PLAYER_NONE,0)");
        e.TargetChecks.Add($"{string.Join("+", counts)}>={n}");
        var dis = e.NewLocal("dis");
        // The Extra Monster Zones are never chosen.
        e.TargetLines.Add($"local {dis}=Duel.SelectDisableField(tp,{n},{self},{opp},0xe000e0)");
        e.TargetLines.Add($"Duel.SetTargetParam({dis})");
        e.TargetLines.Add($"Duel.Hint(HINT_ZONE,tp,{dis})");
        var zone = e.NewLocal("zone");
        var lines = new List<string>
        {
            $"local {zone}=Duel.GetChainInfo(0,CHAININFO_TARGET_PARAM)",
            // Chosen as tp sees the field; the rule counts from player 0's side.
            $"if tp==1 then {zone}=(({zone}&0xffff)<<16)|(({zone}>>16)&0xffff) end",
        };
        var r = e.NewLocal("r");
        lines.Add($"local {r}=0");
        var duration = ctx.Args.Choice("duration");
        if (duration == "face_up")
        {
            var range = Zones_Range(ctx);
            lines.AddRange(
            [
                "if c:IsRelateToEffect(e) and c:IsFaceup() then",
                "\tlocal ez=Effect.CreateEffect(c)",
                "\tez:SetType(EFFECT_TYPE_FIELD)",
                $"\tez:SetRange({range})",
                "\tez:SetProperty(EFFECT_FLAG_CANNOT_DISABLE)",
                "\tez:SetCode(EFFECT_DISABLE_FIELD)",
                $"\tez:SetValue({zone})",
                "\tez:SetReset(RESET_EVENT+RESETS_STANDARD)",
                "\tc:RegisterEffect(ez)",
                $"\t{r}=1",
                "end",
            ]);
        }
        else
        {
            lines.AddRange(
            [
                "local ez=Effect.CreateEffect(c)",
                "ez:SetType(EFFECT_TYPE_FIELD)",
                "ez:SetCode(EFFECT_DISABLE_FIELD)",
                $"ez:SetValue({zone})",
                duration == "next" ? "ez:SetReset(RESET_PHASE+PHASE_END,2)" : "ez:SetReset(RESET_PHASE+PHASE_END)",
                "Duel.RegisterEffect(ez,tp)",
                $"{r}=1",
            ]);
        }
        e.AddStep(lines, r, ctx.Link);
    }

    /// <summary>Where this card is while the zones stay unusable: where its effect is used from, or the field.</summary>
    private static string Zones_Range(BlockContext ctx) => ctx.Effect.Range is { } r && !r.Contains("HAND", StringComparison.Ordinal) && !r.Contains("GRAVE", StringComparison.Ordinal)
        ? r : ctx.Card.IsMonster ? "LOCATION_MZONE" : "LOCATION_SZONE";
}

/// <summary>
/// "They must banish monsters they control, face-down, so they only control 1 monster" (Kashtira Big Bang), "Your
/// opponent must send monsters they control to the GY so they only control 1 monster" (Weighbridge): the player picks
/// which ones go, by the game's rules (they are not destroyed or targeted).
/// </summary>
public sealed class MustReduceAction : ActionBlock
{
    public const string BlockId = "do.must_reduce";
    public override string Id => BlockId;
    public override string Group => "Field";
    public override string Name => "A player must send or banish cards so they only control some";
    public override string Sentence => "{who} must {how} {what} they control so they only control {keep}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("each", "Each player", "each player who controls more"), new("opponent", "Your opponent"), new("you", "You")),
        ParamSpec.Choice("how", "How", new("banish_facedown", "banish", "banish face-down"), new("grave", "send", "send to the GY"), new("banish", "banish", "banish face-up")),
        ParamSpec.Choice("what", "What", new("monster", "monsters"), new("card", "cards")),
        ParamSpec.Int("keep", "So they only control", 1, 0, 10),
    ];

    public override string Describe(BlockArgs args)
    {
        var keep = args.Int("keep");
        var what = args.Choice("what");
        var noun = keep == 1 ? what : what + "s";
        var kept = $"{keep.ToString(CultureInfo.InvariantCulture)} {noun}";
        var (subject, they, control) = args.Choice("who") switch
        {
            "opponent" => ("your opponent", "they", "control"),
            "you" => ("you", "you", "control"),
            _ => ($"each player who controls {(keep + 1).ToString(CultureInfo.InvariantCulture)} or more {what}s", "they", "control"),
        };
        var verb = args.Choice("how") switch
        {
            "grave" => $"send {what}s {they} control to the GY",
            "banish" => $"banish {what}s {they} control",
            _ => $"banish {what}s {they} control, face-down,",
        };
        return $"{subject} must {verb} so {they} only {control} {kept}";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var keep = ctx.Args.Int("keep").ToString(CultureInfo.InvariantCulture);
        var loc = ctx.Args.Choice("what") == "card" ? "LOCATION_ONFIELD" : "LOCATION_MZONE";
        var how = ctx.Args.Choice("how");
        var (able, move, hint, category) = how switch
        {
            "grave" => ("Card.IsAbleToGrave", "Duel.SendtoGrave({0},REASON_RULE,{1})", "HINTMSG_TOGRAVE", "CATEGORY_TOGRAVE"),
            "banish" => ("Card.IsAbleToRemove", "Duel.Remove({0},POS_FACEUP,REASON_RULE,{1})", "HINTMSG_REMOVE", "CATEGORY_REMOVE"),
            _ => ("Card.IsAbleToRemove", "Duel.Remove({0},POS_FACEDOWN,REASON_RULE,{1})", "HINTMSG_REMOVE", "CATEGORY_REMOVE"),
        };
        var ableArgs = how == "grave" ? "" : how == "banish" ? ",{0},POS_FACEUP,REASON_RULE" : ",{0},POS_FACEDOWN,REASON_RULE";
        var players = ctx.Args.Choice("who") switch { "opponent" => new[] { "1-tp" }, "you" => ["tp"], _ => ["tp", "1-tp"] };
        e.AddCategory(category);
        e.TargetChecks.Add("(" + string.Join(" or ", players.Select(p => $"Duel.GetFieldGroupCount({p},{loc},0)>{keep}")) + ")");
        var r = e.NewLocal("r");
        var lines = new List<string> { $"local {r}=0" };
        foreach (var p in players)
        {
            var g = e.NewLocal("g");
            lines.AddRange(
            [
                $"local {g}=Duel.GetFieldGroup({p},{loc},0)",
                // As many as they must and can get rid of: cards that can't be moved stay.
                $"local ct=math.min(#{g}-{keep},{g}:FilterCount({able},nil{string.Format(CultureInfo.InvariantCulture, ableArgs, p)}))",
                "if ct>0 then",
                $"\tDuel.Hint(HINT_SELECTMSG,{p},{hint})",
                $"\tlocal sg={g}:FilterSelect({p},{able},ct,ct,nil{string.Format(CultureInfo.InvariantCulture, ableArgs, p)})",
                $"\t{r}={r}+{string.Format(CultureInfo.InvariantCulture, move, "sg", p)}",
                "end",
            ]);
        }
        e.AddStep(lines, r, ctx.Link);
    }
}

/// <summary>
/// "Target 1 "Kashtira" Xyz Monster you control; add 1 of your "Kashtira" monsters attached to it to your hand" (Kashtira
/// Big Bang): cards taken from the materials of this effect's target, or of this card. "That card" in the steps after is
/// the card taken.
/// </summary>
public sealed class FromMaterialsAction : ActionBlock
{
    public const string BlockId = "do.from_materials";
    public override string Id => BlockId;
    public override string Group => "Xyz";
    public override string Name => "Add / Special Summon / send cards attached to an Xyz Monster";
    public override string Sentence => "{how} {count} {whose} {cards} attached to {from}";
    public override string Help => "\"Attached to it\" needs a block before it that targets the Xyz Monster (\"Target cards\").";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("how", "What", new("hand", "Add"), new("special", "Special Summon"), new("grave", "Send"), new("banish", "Banish")),
        ParamSpec.Int("count", "How many", 1, 1, 5),
        ParamSpec.Choice("whose", "Whose", new("you", "of your", "you own"), new("any", "", "anyone's")),
        new ParamSpec
        {
            Key = "cards", Label = "Which materials", Type = ParamType.Cards, WhereFromBlock = true,
            DefaultFilter = () => new CardFilter { Zones = Zone.None, CardType = FilterCardType.Monster },
        },
        ParamSpec.Choice("from", "Attached to", new("target", "it", "this effect's target"), new("this", "this card")),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var f = args.Cards("cards").Clone();
        f.Zones = Zone.None;
        var mine = args.Choice("whose") == "you";
        var noun = FilterText.Describe(f, args.Pack, plural: mine || n != 1, withLocation: false, ownName: args.CardName);
        // "1 of your "Kashtira" monsters attached to it", "1 monster attached to this card".
        var cards = mine ? $"{n.ToString(CultureInfo.InvariantCulture)} of your {noun}" : $"{n.ToString(CultureInfo.InvariantCulture)} {noun}";
        var from = args.Describe("from");
        return args.Choice("how") switch
        {
            "special" => $"Special Summon {cards} attached to {from}",
            "grave" => $"send {cards} attached to {from} to the GY",
            "banish" => $"banish {cards} attached to {from}",
            _ => $"add {cards} attached to {from} to your hand",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var how = ctx.Args.Choice("how");
        var f = ctx.Args.Cards("cards").Clone();
        f.Zones = Zone.None;
        f.Owner = CardOwner.Either;
        var tests = new List<string>();
        if (ctx.Args.Choice("whose") == "you") tests.Add("c:GetOwner()==tp");
        tests.Add(how switch
        {
            "special" => "c:IsCanBeSpecialSummoned(e,0,tp,false,false)",
            "grave" => "c:IsAbleToGrave()",
            "banish" => "c:IsAbleToRemove()",
            _ => "c:IsAbleToHand()",
        });
        var filter = ctx.Script.Filter(f, string.Join(" and ", tests), "matfilter");
        string holder;
        if (ctx.Args.Choice("from") == "target")
        {
            if (CardRefs.GroupLua(ctx, CardRef.Target) is not { } g) return;
            holder = $"{g}:GetFirst()";
        }
        else
        {
            holder = "c";
            e.TargetChecks.Add($"e:GetHandler():GetOverlayGroup():IsExists({filter.Function},1,nil,e,tp)");
        }
        e.AddCategory(how switch { "special" => "CATEGORY_SPECIAL_SUMMON", "grave" => "CATEGORY_TOGRAVE", "banish" => "CATEGORY_REMOVE", _ => "CATEGORY_TOHAND" });
        var n = ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture);
        var r = e.NewLocal("r");
        var tc = e.NewLocal("tc");
        var mg = e.NewLocal("mg");
        var hint = how switch { "special" => "HINTMSG_SPSUMMON", "grave" => "HINTMSG_TOGRAVE", "banish" => "HINTMSG_REMOVE", _ => "HINTMSG_ATOHAND" };
        var move = how switch
        {
            "special" => "Duel.SpecialSummon(sg,0,tp,tp,false,false,POS_FACEUP)",
            "grave" => "Duel.SendtoGrave(sg,REASON_EFFECT)",
            "banish" => "Duel.Remove(sg,POS_FACEUP,REASON_EFFECT)",
            _ => "Duel.SendtoHand(sg,nil,REASON_EFFECT)",
        };
        var room = how == "special" ? $" and Duel.GetLocationCount(tp,LOCATION_MZONE)>={n}" : "";
        var lines = new List<string>
        {
            $"local {r}=0",
            $"local {tc}={holder}",
            $"if {tc} and {tc}:IsType(TYPE_XYZ){room} then",
            $"\tlocal {mg}={tc}:GetOverlayGroup():Filter({filter.Function},nil,e,tp)",
            $"\tif #{mg}>={n} then",
            $"\t\tDuel.Hint(HINT_SELECTMSG,tp,{hint})",
            $"\t\tlocal sg={mg}:Select(tp,{n},{n},nil)",
            $"\t\t{r}={move}",
        };
        if (how == "hand") lines.Add("\t\tif sg:IsExists(Card.IsLocation,1,nil,LOCATION_HAND) then Duel.ConfirmCards(1-tp,sg) Duel.ShuffleHand(tp) end");
        lines.AddRange(["\tend", "end"]);
        e.AddStep(lines, r, ctx.Link);
    }
}

/// <summary>
/// "Choose that many of your opponent's occupied Main Monster Zones; your opponent must send the monsters in those zones
/// to the GY" (The Winged Dragon of Ra, Sun God of Darkness): zones, not monsters, are chosen, so nothing is targeted.
/// </summary>
public sealed class SendZonesAction : ActionBlock
{
    public const string BlockId = "do.send_zones";
    public override string Id => BlockId;
    public override string Group => "Field";
    public override string Name => "Choose your opponent's occupied zones; they send the monsters in them to the GY";
    public override string Sentence => "Choose {many} {count} of your opponent's occupied Main Monster Zones; your opponent must send the monsters in those zones to the GY";
    public override string Help => "\"That many\" is as many as the cards used for the cost, like \"discard any number of cards\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("many", "How many", new("number", "", "a number"), new("cost", "that many", "as many as the cards used for the cost")),
        ParamSpec.Int("count", "Number", 1, 1, 5).ShowIf(b => b.Values.GetValueOrDefault("many", "number") == "number"),
    ];

    private static string Chosen(BlockArgs args)
    {
        if (args.Choice("many") == "cost") return "choose that many of your opponent's occupied Main Monster Zones";
        var n = args.Int("count");
        return n == 1 ? "choose 1 of your opponent's occupied Main Monster Zones" : $"choose {n.ToString(CultureInfo.InvariantCulture)} of your opponent's occupied Main Monster Zones";
    }

    private static string Rule(BlockArgs args) => args.Choice("many") == "number" && args.Int("count") == 1
        ? "your opponent must send the monster in that zone to the GY"
        : "your opponent must send the monsters in those zones to the GY";

    public override string Describe(BlockArgs args) => $"{Chosen(args)}; {Rule(args)}";

    public override (string Target, string Action)? DescribeTargeting(BlockArgs args) => (Chosen(args), Rule(args));

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (e.TargetLines.Any(l => l.StartsWith("Duel.SetTargetParam(", StringComparison.Ordinal)))
        {
            e.Error($"\"{Name}\": another block of this effect already keeps a number with the chain link (a declaration or an amount).");
            return;
        }
        string count;
        if (ctx.Args.Choice("many") == "cost")
        {
            if (!e.CostCounts) e.Error($"\"{Name}\": \"that many\" needs a COST block that discards, sends or banishes any number of cards.");
            count = "e:GetLabel()";
        }
        else count = ctx.Args.Int("count").ToString(CultureInfo.InvariantCulture);
        var occupied = ctx.Script.AddFunction("mmzfilter", "c", ["return c:GetSequence()<5"]);
        e.AddCategory("CATEGORY_TOGRAVE");
        e.TargetChecks.Add($"Duel.IsExistingMatchingCard({occupied},tp,0,LOCATION_MZONE,1,nil)");
        var zones = e.NewLocal("zones");
        e.TargetLines.AddRange(
        [
            // Only the opponent's occupied Main Monster Zones can be chosen.
            "local zf=0",
            $"for mc in aux.Next(Duel.GetMatchingGroup({occupied},tp,0,LOCATION_MZONE,nil)) do zf=zf|(1<<(mc:GetSequence()+16)) end",
            $"local zct=math.min({count},Duel.GetMatchingGroupCount({occupied},tp,0,LOCATION_MZONE,nil))",
            $"local {zones}=Duel.SelectField(tp,zct,0,LOCATION_MZONE,~zf)",
            $"Duel.SetTargetParam({zones})",
            $"Duel.Hint(HINT_ZONE,tp,{zones})",
            $"Duel.SetOperationInfo(0,CATEGORY_TOGRAVE,nil,zct,1-tp,LOCATION_MZONE)",
        ]);
        var r = e.NewLocal("r");
        var inZone = ctx.Script.AddFunction("zonefilter", "c,zones", ["return c:GetSequence()<5 and zones&(1<<(c:GetSequence()+16))~=0"]);
        e.AddStep(
        [
            $"local {r}=0",
            "local zs=Duel.GetChainInfo(0,CHAININFO_TARGET_PARAM)",
            $"local zg=Duel.GetMatchingGroup({inZone},tp,0,LOCATION_MZONE,nil,zs)",
            $"if #zg>0 then {r}=Duel.SendtoGrave(zg,REASON_RULE,1-tp) end",
        ], r, ctx.Link);
    }
}
