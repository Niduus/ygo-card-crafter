using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// Blocks added from a scan of every official card script: the actions, lasting effects and conditions
// real cards use most that the first set of blocks couldn't express.

internal static class Resets
{
    public const string EndOfTurn = "RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END";
    public const string WhileFaceUp = "RESET_EVENT+RESETS_STANDARD";

    public static ChoiceOption[] Duration =>
    [
        new("turn", "until the end of this turn"), new("always", "", "while face-up"),
        new("opp_turn", "until the end of your opponent's turn"), new("next", "until the end of the next turn"),
    ];

    /// <summary>
    /// The SetReset arguments for the block's "duration", with <paramref name="extra"/> flags (such as "+RESET_DISABLE")
    /// added: "until the end of your opponent's turn" lasts 2 End Phases when used in your turn, 1 in theirs, whoever
    /// controls the monster, and "the next turn" always 2.
    /// </summary>
    public static string Of(BlockContext ctx, string extra = "") => Of(ctx.Args.Choice("duration"), extra);

    public static string Of(string duration, string extra = "") => duration switch
    {
        "turn" => EndOfTurn + extra,
        "opp_turn" => EndOfTurn + extra + ",Duel.GetTurnPlayer()==e:GetHandlerPlayer() and 2 or 1",
        "next" => EndOfTurn + extra + ",2",
        _ => WhileFaceUp + extra,
    };
}

// ---------------------------------------------------------------- negating Summons and attacks

public sealed class NegateSummonAction : ActionBlock
{
    public override string Id => "do.negate_summon";
    public override string Group => "Negate";
    public override string Name => "Negate the Summon";
    public override string Sentence => "Negate the Summon {and}";
    public override string Help => "Like Solemn Strike: use with the WHEN timing \"when your opponent would Normal or Special Summon a monster(s)\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("and", "And",
            new("destroy", "and if you do, destroy that monster(s)"),
            new("banish", "and if you do, banish that monster(s)"),
            new("nothing", "", "and nothing else")),
    ];

    public override string Describe(BlockArgs args) =>
        args.Choice("and") == "nothing" ? "negate the Summon" : $"negate the Summon, {args.Describe("and")}";

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (!Timing.IsWouldSummonTiming(e.Code))
        {
            e.Error("\"Negate the Summon\" needs the WHEN timing \"when your opponent would Normal or Special Summon a monster(s)\".");
            return;
        }
        var follow = ctx.Args.Choice("and");
        var r = e.NewLocal("r");
        e.AddCategory("CATEGORY_DISABLE_SUMMON");
        e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_DISABLE_SUMMON,eg,eg:GetCount(),0,0)");
        var lines = new List<string> { "Duel.NegateSummon(eg)", $"local {r}=eg:GetCount()" };
        if (follow == "destroy")
        {
            e.AddCategory("CATEGORY_DESTROY");
            e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_DESTROY,eg,eg:GetCount(),0,0)");
            lines.Add("Duel.Destroy(eg,REASON_EFFECT)");
        }
        if (follow == "banish")
        {
            e.AddCategory("CATEGORY_REMOVE");
            e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_REMOVE,eg,eg:GetCount(),0,0)");
            lines.Add("Duel.Remove(eg,POS_FACEUP,REASON_EFFECT)");
        }
        e.AddStep(lines, r, ctx.Link);
    }
}

public sealed class NegateAttackAction : ActionBlock
{
    public override string Id => "do.negate_attack";
    public override string Group => "Battle";
    public override string Name => "Negate the attack";
    public override string Sentence => "Negate the attack {then}";
    public override string Help => "Use with a WHEN that happens during an attack, such as \"when an opponent's monster declares an attack\".";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("then", "Then", new("nothing", "", "and nothing else"), new("end_bp", ", then end the Battle Phase")),
    ];

    public override string Describe(BlockArgs args) => "negate the attack" + args.Describe("then");

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        if (e.Code is not ("EVENT_ATTACK_ANNOUNCE" or "EVENT_BE_BATTLE_TARGET" or "EVENT_PRE_DAMAGE_CALCULATE" or "EVENT_BATTLE_START")
            && !(e.Conditions.Any(c => c.Contains("PHASE_BATTLE", StringComparison.Ordinal))))
            e.Warn("\"Negate the attack\" only does something during an attack: use a WHEN such as \"when an opponent's monster declares an attack\".");
        var r = e.NewLocal("r");
        var lines = new List<string> { $"local {r}=Duel.NegateAttack() and 1 or 0" };
        if (ctx.Args.Choice("then") == "end_bp")
            lines.AddRange([$"if {r}>0 then", "\tDuel.SkipPhase(Duel.GetTurnPlayer(),PHASE_BATTLE,RESET_PHASE+PHASE_BATTLE_STEP,1)", "end"]);
        e.AddStep(lines, r, ctx.Link);
    }
}

// ---------------------------------------------------------------- hands and positions

public sealed class DiscardAction : ActionBlock
{
    public override string Id => "do.discard";
    public override string Group => "Cards";
    public override string Name => "Discard cards (as an effect)";
    public override string Sentence => "{who} discard(s) {count} card(s) {how}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("opponent", "Your opponent"), new("you", "You"), new("each", "Each player")),
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ParamSpec.Choice("how", "How", new("chosen", "", "of their choice"), new("random", "at random")),
    ];

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var cards = n == 1 ? "1 card" : $"{n} cards";
        var how = args.Choice("how") == "random" ? " at random" : "";
        return args.Choice("who") switch
        {
            "you" => $"discard {cards}{how}",
            "each" => $"each player discards {cards}{how}",
            _ => $"your opponent discards {cards}{how}",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var n = ctx.Args.Int("count");
        var random = ctx.Args.Choice("how") == "random";
        var who = ctx.Args.Choice("who");
        var players = who switch { "you" => new[] { "tp" }, "each" => ["tp", "1-tp"], _ => ["1-tp"] };
        e.AddCategory(who == "you" ? "CATEGORY_HANDES_SELF" : "CATEGORY_HANDES");
        foreach (var p in players) e.TargetChecks.Add($"Duel.GetFieldGroupCount({p},LOCATION_HAND,0)>={n}");
        e.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_HANDES,nil,0,{(players.Length == 2 ? "PLAYER_ALL" : players[0])},{n})");
        var r = e.NewLocal("r");
        var lines = new List<string> { $"local {r}=0" };
        foreach (var p in players)
        {
            if (random)
            {
                var g = e.NewLocal("g");
                lines.Add($"local {g}=Duel.GetFieldGroup({p},LOCATION_HAND,0)");
                lines.Add($"if {g}:GetCount()>0 then");
                lines.Add($"\t{r}={r}+Duel.SendtoGrave({g}:RandomSelect({p},math.min({n},{g}:GetCount())),REASON_EFFECT+REASON_DISCARD)");
                lines.Add("end");
            }
            else lines.Add($"{r}={r}+Duel.DiscardHand({p},nil,{n},{n},REASON_EFFECT+REASON_DISCARD)");
        }
        e.AddStep(lines, r, ctx.Link);
    }
}

public sealed class ChangePositionAction : CardGroupAction
{
    public override string Id => "do.change_position";
    public override string Group => "Monsters";
    public override string Name => "Change battle positions";
    public override string Sentence => "Change {count} {cards} {target} {to}";
    protected override string Category => "CATEGORY_POSITION";
    protected override string Hint => "HINTMSG_POSCHANGE";
    protected override string TargetedWording => "change {0}";
    protected override bool AllowsThisCard => true;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Int("count", "How many", 1, 1, 10),
        ParamSpec.Cards("cards", "Which monsters", DefaultCards, Zone.MonsterZone, thisCard: true),
        ParamSpec.Flag("target", "(targeting)", false),
        ParamSpec.Choice("to", "To",
            new("defense", "to face-up Defense Position"),
            new("attack", "to face-up Attack Position"),
            new("facedown", "to face-down Defense Position"),
            new("toggle", "to the other battle position", "to the other battle position"),
            // Mimighoul: "change 1 face-down monster on the field to face-up Attack or Defense Position".
            new("faceup_choice", "to face-up Attack or Defense Position", "to face-up Attack or Defense Position (your choice)")),
    ];

    public override string Describe(BlockArgs args)
    {
        return args.Choice("to") == "toggle"
            ? $"change the battle position of {Some(args)}{args.ExceptText()}"
            : $"change {Some(args)} {args.Describe("to")}{args.ExceptText()}";
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) =>
        args.Choice("to") == "toggle" ? $"change {(single ? "its" : "their")} battle position{(single ? "" : "s")}" : $"change {pronoun} {args.Describe("to")}";

    protected override string? AbleCheck(BlockContext ctx) => ctx.Args.Choice("to") switch
    {
        "facedown" => "c:IsCanTurnSet()",
        "defense" => "c:IsCanChangePosition() and not c:IsPosition(POS_FACEUP_DEFENSE)",
        "attack" => "c:IsCanChangePosition() and not c:IsPosition(POS_FACEUP_ATTACK)",
        "faceup_choice" => "c:IsFacedown() and c:IsCanChangePosition()",
        _ => "c:IsCanChangePosition()",
    };

    protected override string Apply(string g, BlockContext ctx) => ctx.Args.Choice("to") switch
    {
        "facedown" => $"Duel.ChangePosition({g},POS_FACEDOWN_DEFENSE)",
        "defense" => $"Duel.ChangePosition({g},POS_FACEUP_DEFENSE)",
        "attack" => $"Duel.ChangePosition({g},POS_FACEUP_ATTACK)",
        // Each one as the player chooses, as official Mimighoul do (Duel.SelectPosition).
        "faceup_choice" => $"{ctx.Script.AddFunction("choosepos", "g,tp", ["local n=0", "for tc in aux.Next(g) do", "\tlocal pos=Duel.SelectPosition(tp,tc,POS_FACEUP)", "\tn=n+Duel.ChangePosition(tc,pos)", "end", "return n"])}({g},tp)",
        _ => $"Duel.ChangePosition({g},POS_FACEUP_DEFENSE,POS_FACEUP_DEFENSE,POS_FACEUP_ATTACK,POS_FACEUP_ATTACK)",
    };
}

// ---------------------------------------------------------------- control, materials, Summons

public sealed class TakeControlAction : CardGroupAction
{
    public override string Id => "do.take_control";
    public override string Group => "Monsters";
    public override string Name => "Take control of monsters";
    public override string Sentence => "Take control of {count} {cards} {target} {duration}";
    protected override string Category => "CATEGORY_CONTROL";
    protected override string Hint => "HINTMSG_CONTROL";
    protected override string TargetedWording => "take control of {0}";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Int("count", "How many", 1, 1, 5),
        // Saved before "all" was offered: no mode is "exactly". Sun God Domination: "take control of all monsters your opponent controls".
        ModeParam,
        ParamSpec.Cards("cards", "Which monsters", DefaultCards, Zone.MonsterZone),
        ParamSpec.Flag("target", "(targeting)", true),
        ParamSpec.Choice("duration", "Until", new("turn", "until the End Phase"), new("always", "", "permanently"),
            new("battle", "until the end of the Battle Phase")),
    ];

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) =>
        $"take control of {pronoun} {args.Describe("duration")}".TrimEnd();

    protected override string? AbleCheck(BlockContext ctx) => "c:IsControlerCanBeChanged()";
    protected override string? SpaceCheck(int count, CardFilter f, BlockContext ctx) => $"Duel.GetLocationCount(tp,LOCATION_MZONE,tp,LOCATION_REASON_CONTROL)>={count}";
    protected override string Apply(string g, BlockContext ctx) => ctx.Args.Choice("duration") switch
    {
        "turn" => $"Duel.GetControl({g},tp,PHASE_END,1)",
        // Number F0: Utopic Future: back to its controller as the Battle Phase ends.
        "battle" => $"Duel.GetControl({g},tp,PHASE_BATTLE,1)",
        _ => $"Duel.GetControl({g},tp)",
    };
}

public sealed class AttachMaterialAction : CardGroupAction
{
    public override string Id => "do.attach";
    public override string Group => "Monsters";
    public override string Name => "Attach cards to this card as material";
    public override string Sentence => "Attach {amount} {count_cards} {count} {cards} {pick} {pick2} {pick3} {sum} {inc_cards} {target} to this card as material {chooser}";
    public override string Help => "Xyz Monsters only: the cards go under this card as Xyz Materials.";
    protected override string Category => "";
    protected override string Hint => "HINTMSG_XMATERIAL";
    protected override string TargetedWording => "attach {0} to this card as material";
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster };
    protected override string? AbleCheck(BlockContext ctx) => "c:IsCanOverlay()";
    // Tantrum Toddler: "attach up to 1 card each from your GY and your opponent's GY to this card as material".
    protected override bool OffersSelections => true;

    protected override string Apply(string g, BlockContext ctx)
    {
        var fn = ctx.Script.AddFunction("attach", "g,e", ["local c=e:GetHandler()", "if not c:IsRelateToEffect(e) or c:IsFacedown() then return 0 end", "Duel.Overlay(c,g)", "return g:GetCount()"]);
        return $"{fn}({g},e)";
    }

    public override void Compile(BlockContext ctx)
    {
        if (!ctx.Card.IsXyz) ctx.Effect.Error("\"Attach cards to this card\" needs this card to be an Xyz Monster.");
        base.Compile(ctx);
    }
}

public sealed class NormalSummonAction : CardGroupAction
{
    public override string Id => "do.normal_summon";
    public override string Group => "Monsters";
    public override string Name => "Normal Summon a monster";
    public override string Sentence => "Normal Summon {count} {cards}";
    public override string Help => "An extra Normal Summon right now, like Ultimate Offering. It doesn't use up the turn's Normal Summon.";
    protected override string Category => "CATEGORY_SUMMON";
    protected override string Hint => "HINTMSG_SUMMON";
    protected override string TargetedWording => "Normal Summon {0}";
    protected override int MaxCount => 1;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster };

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
        [ParamSpec.Int("count", "How many", 1, 1, 1), ParamSpec.Cards("cards", "Which monster", DefaultCards, Zone.Hand)];

    protected override string? AbleCheck(BlockContext ctx) => "c:IsSummonable(true,nil)";
    protected override string Apply(string g, BlockContext ctx)
    {
        var fn = ctx.Script.AddFunction("nsummon", "g,tp", ["local tc=g:GetFirst()", "if not tc then return 0 end", "Duel.Summon(tp,tc,true,nil)", "return 1"]);
        return $"{fn}({g},tp)";
    }
}

public sealed class TokenAction : ActionBlock
{
    public override string Id => "do.tokens";
    public override string Group => "Monsters";
    public override string Name => "Special Summon Tokens";
    public override string Sentence => "Special Summon {count} {max} Token(s) (passcode {token}) {position} {side}";
    public override string Help => "Make the Token as its own card in this pack (Monster, frame \"Token\") and enter its passcode here; its Level, Type, Attribute, ATK and DEF come from that card.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Int("count", "How many", 1, 1, 5),
        // Number 42: Galaxy Tomahawk: "Special Summon as many "Battle Eagle Tokens" ... as possible".
        ParamSpec.Flag("max", "as many as possible", false),
        ParamSpec.Int("token", "Token passcode", 0, 0, int.MaxValue),
        ParamSpec.Choice("position", "Position", new("faceup", "", "face-up"), new("attack", "in Attack Position"), new("defense", "in Defense Position")),
        ParamSpec.Choice("side", "Whose field", new("you", "", "to your field"), new("opponent", "to your opponent's field")),
    ];

    private static CardData? TokenCard(BlockArgs args) =>
        args.Pack?.Cards.FirstOrDefault(c => c.Card.Id == args.Int("token"))?.Card;

    public override string Describe(BlockArgs args)
    {
        var n = args.Int("count");
        var t = TokenCard(args);
        var stats = t is null ? "" :
            $" ({args.Pack!.TypeName(t.Race)}/{EnumNames.Display(t.Attribute)}/Level {t.Level}/ATK {t.Atk}/DEF {t.Def})";
        var name = t is null ? "Token" : $"\"{t.Name}\"";
        if (t is not null && !t.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)) name += " Token";
        var side = args.Choice("side") == "opponent" ? " to your opponent's field" : "";
        var pos = args.Describe("position");
        // "as many "Battle Eagle Tokens" as possible": the plural inside the name's quotes.
        var many = name.EndsWith('"') ? name[..^1] + "s\"" : name + "s";
        if (args.Bool("max")) return $"Special Summon as many {many}{stats} as possible{(pos.Length > 0 ? " " + pos : "")}{side}";
        return $"Special Summon {(n == 1 ? "1" : n.ToString(CultureInfo.InvariantCulture))} {name}{(n == 1 ? "" : "s")}{stats}{(pos.Length > 0 ? " " + pos : "")}{side}";
    }

    public override void Compile(BlockContext ctx)
    {
        var e = ctx.Effect;
        var t = TokenCard(ctx.Args);
        if (t is null)
        {
            e.Error("\"Special Summon Tokens\": enter the passcode of a Token card in this pack (a Monster with the Token frame).");
            return;
        }
        var n = ctx.Args.Int("count");
        var p = ctx.Args.Choice("side") == "opponent" ? "1-tp" : "tp";
        var pos = ctx.Args.Choice("position") switch { "attack" => "POS_FACEUP_ATTACK", "defense" => "POS_FACEUP_DEFENSE", _ => "POS_FACEUP" };
        var check = $"Duel.IsPlayerCanSpecialSummonMonster(tp,{t.Id},0,TYPES_TOKEN_MONSTER,{t.Atk},{t.Def},{t.Level},{EnumNames.LuaConstant(t.Race)},{EnumNames.LuaConstant(t.Attribute)},{pos},{p})";
        e.AddCategory("CATEGORY_SPECIAL_SUMMON");
        e.AddCategory("CATEGORY_TOKEN");
        if (ctx.Args.Bool("max"))
        {
            // One for each free Monster Zone, or 1 while only 1 monster can be Summoned at a time (Blue-Eyes Spirit Dragon).
            e.TargetChecks.Add($"Duel.GetLocationCount({p},LOCATION_MZONE,tp)>0");
            e.TargetChecks.Add(check);
            e.TargetLines.Add($"local ft=Duel.GetLocationCount({p},LOCATION_MZONE,tp)");
            e.TargetLines.Add("if Duel.IsPlayerAffectedByEffect(tp,59822133) then ft=1 end");
            e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_TOKEN,nil,ft,0,0)");
            e.TargetLines.Add("Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,ft,0,0)");
            var rm = e.NewLocal("r");
            e.AddStep(
            [
                $"local {rm}=0",
                $"local ft=Duel.GetLocationCount({p},LOCATION_MZONE,tp)",
                "if Duel.IsPlayerAffectedByEffect(tp,59822133) then ft=math.min(ft,1) end",
                $"if ft>0 and {check} then",
                "\tfor i=1,ft do",
                $"\t\tlocal token=Duel.CreateToken(tp,{t.Id})",
                $"\t\tif Duel.SpecialSummonStep(token,0,tp,{p},false,false,{pos}) then {rm}={rm}+1 end",
                "\tend",
                "\tDuel.SpecialSummonComplete()",
                "end",
            ], rm, ctx.Link);
            return;
        }
        e.TargetChecks.Add($"Duel.GetLocationCount({p},LOCATION_MZONE,tp)>={n}");
        if (n > 1) e.TargetChecks.Add("not Duel.IsPlayerAffectedByEffect(tp,59822133)");
        e.TargetChecks.Add(check);
        e.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_TOKEN,nil,{n},0,0)");
        e.TargetLines.Add($"Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,{n},0,0)");
        var r = e.NewLocal("r");
        e.AddStep(
        [
            $"local {r}=0",
            $"if Duel.GetLocationCount({p},LOCATION_MZONE,tp)>={n} and {check}{(n > 1 ? " and not Duel.IsPlayerAffectedByEffect(tp,59822133)" : "")} then",
            $"\tfor i=1,{n} do",
            $"\t\tlocal token=Duel.CreateToken(tp,{t.Id})",
            $"\t\tif Duel.SpecialSummonStep(token,0,tp,{p},false,false,{pos}) then {r}={r}+1 end",
            "\tend",
            "\tDuel.SpecialSummonComplete()",
            "end",
        ], r, ctx.Link);
    }
}

// ---------------------------------------------------------------- stats, Levels, battle

public sealed class SetAttackAction : CardGroupAction
{
    public override string Id => "do.set_atk";
    public override string Group => "Monsters";
    public override string Name => "Monsters' ATK/DEF become a value (halve, 0, another monster's ATK...)";
    public override string Sentence => "{count} {cards} {target}: {stat} becomes {mode} {amount} {source} {duration}";
    public override string Help => "\"Original ATK\" changes what the monster is printed with, so later gains and losses still apply to it (Apollousa). " +
                                   "\"The ATK of 1 ...\" picks that card as the effect resolves.";
    protected override string Category => "CATEGORY_ATKCHANGE";
    protected override string Hint => "HINTMSG_FACEUP";
    protected override string TargetedWording => "{0}";
    protected override bool AllowsThisCard => true;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Int("count", "How many", 1, 1, 10),
        // Saved before "all" was offered: no count mode is "exactly".
        CountModeParam,
        ParamSpec.Cards("cards", "Which monsters", DefaultCards, Zone.MonsterZone, thisCard: true),
        ParamSpec.Flag("target", "(targeting)", true),
        // Saved before the stat was a choice: ATK.
        ParamSpec.Choice("stat", "Stat", new("atk", "ATK"), new("def", "DEF"), new("both", "ATK/DEF"),
            new("orig_atk", "original ATK"), new("orig_def", "original DEF"), new("orig_both", "original ATK/DEF")),
        ParamSpec.Choice("mode", "Becomes", new("half", "half its current ATK"), new("zero", "0"), new("value", "", "a number:"),
            new("of_card", "the ATK of 1", "the ATK of 1 card picked below")),
        ParamSpec.Int("amount", "Number (for \"a number\")", 0, 0, 99999),
        ParamSpec.Cards("source", "The card whose ATK it becomes",
                () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true })
            .WhenUsed().ShowIf(b => b.Values.GetValueOrDefault("mode") == "of_card"),
        ParamSpec.Choice("duration", "Until", Resets.Duration),
        KeptParam,
    ];

    /// <summary>"ATK", "ATK/DEF", "original ATK/DEF".</summary>
    private static string Stat(BlockArgs args) => args.Describe("stat");

    /// <summary>"ATK/DEF" are two stats: they "become".</summary>
    private static bool Both(BlockArgs args) => args.Choice("stat") is "both" or "orig_both";

    /// <summary>"halved", "0", "1000", "the ATK of 1 monster in your GY" (its DEF, for DEF).</summary>
    private static string Becomes(BlockArgs args) => args.Choice("mode") switch
    {
        "half" => "halved",
        "zero" => "0",
        "of_card" => $"the {(args.Choice("stat") is "def" or "orig_def" ? "DEF" : "ATK")} of 1 {FilterText.Describe(args.Cards("source"), args.Pack, ownName: args.CardName)}",
        _ => args.Int("amount").ToString(CultureInfo.InvariantCulture),
    };

    private static string Tail(BlockArgs args) => args.Describe("duration") is { Length: > 0 } d ? " " + d : "";

    /// <summary>"is halved", "becomes 0", "are halved" and "become 0" for ATK/DEF or several monsters.</summary>
    private static string Verb(BlockArgs args, bool single) => args.Choice("mode") == "half"
        ? (single && !Both(args) ? "is halved" : "are halved")
        : $"{(single && !Both(args) ? "becomes" : "become")} {Becomes(args)}";

    public override string Describe(BlockArgs args)
    {
        if (args.IsThisCard) return $"this card's {Stat(args)} {Verb(args, true)}{Tail(args)}";
        return $"{StatOf(args, Stat(args))} {Verb(args, One(args))}{Tail(args)}" + args.ExceptText();
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) =>
        $"{(single ? "its" : "their")} {Stat(args)} {Verb(args, single)}{Tail(args)}";

    protected override string? AbleCheck(BlockContext ctx) => "c:IsFaceup()";

    private static FilterRef SourceFilter(BlockContext ctx) => ctx.Script.Filter(ctx.Args.Cards("source"), "c:IsType(TYPE_MONSTER)", "atksrcfilter");

    public override void Compile(BlockContext ctx)
    {
        if (ctx.Args.Choice("stat") is "def" or "orig_def") ctx.Effect.AddCategory("CATEGORY_DEFCHANGE");
        if (ctx.Args.Choice("mode") == "of_card")
        {
            var src = SourceFilter(ctx);
            ctx.Effect.TargetChecks.Add($"Duel.IsExistingMatchingCard({src.Function},tp,{src.Self},{src.Opp},1,{src.Exclude},e,tp)");
        }
        base.Compile(ctx);
    }

    protected override string Apply(string g, BlockContext ctx)
    {
        var stat = ctx.Args.Choice("stat");
        // The engine's codes: the final ATK/DEF, or the original ones (later changes still apply to those).
        var codes = stat switch
        {
            "def" => new[] { ("EFFECT_SET_DEFENSE_FINAL", "GetDefense") },
            "both" => [("EFFECT_SET_ATTACK_FINAL", "GetAttack"), ("EFFECT_SET_DEFENSE_FINAL", "GetDefense")],
            "orig_atk" => [("EFFECT_SET_BASE_ATTACK", "GetBaseAttack")],
            "orig_def" => [("EFFECT_SET_BASE_DEFENSE", "GetBaseDefense")],
            "orig_both" => [("EFFECT_SET_BASE_ATTACK", "GetBaseAttack"), ("EFFECT_SET_BASE_DEFENSE", "GetBaseDefense")],
            _ => [("EFFECT_SET_ATTACK_FINAL", "GetAttack")],
        };
        var body = new List<string> { "local n=0", "local tc=g:GetFirst()", "while tc do", "\tif tc:IsFaceup() then" };
        var indent = "\t\t";
        if (ctx.Args.Choice("mode") == "of_card")
        {
            // The card whose ATK it becomes, picked as the effect resolves.
            var src = SourceFilter(ctx);
            var get = stat is "def" or "orig_def" ? "GetDefense" : "GetAttack";
            body.AddRange(
            [
                "\t\tlocal tp=e:GetHandlerPlayer()",
                "\t\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_FACEUP)",
                $"\t\tlocal sc=Duel.SelectMatchingCard(tp,{src.Function},tp,{src.Self},{src.Opp},1,1,tc,e,tp):GetFirst()",
                "\t\tif sc then",
                "\t\t\tif sc:IsOnField() then Duel.HintSelection(Group.FromCards(sc)) else Duel.ConfirmCards(1-tp,sc) end",
                $"\t\t\tlocal v=math.max(sc:{get}(),0)",
            ]);
            indent = "\t\t\t";
        }
        var i = 0;
        foreach (var (code, getter) in codes)
        {
            var value = ctx.Args.Choice("mode") switch
            {
                "half" => $"math.ceil(tc:{getter}()/2)",
                "zero" => "0",
                "of_card" => "v",
                _ => ctx.Args.Int("amount").ToString(CultureInfo.InvariantCulture),
            };
            // The first effect keeps its old name, so cards saved before the other stats compile the same.
            var v = i++ == 0 ? "e1" : $"e{i}";
            body.AddRange(
            [
                $"{indent}local {v}=Effect.CreateEffect(e:GetHandler())", $"{indent}{v}:SetType(EFFECT_TYPE_SINGLE)", .. KeptLines(ctx, v, indent), $"{indent}{v}:SetCode({code})",
                $"{indent}{v}:SetValue({value})", $"{indent}{v}:SetReset({Resets.Of(ctx)})", $"{indent}tc:RegisterEffect({v})",
            ]);
        }
        body.Add($"{indent}n=n+1");
        if (ctx.Args.Choice("mode") == "of_card") body.Add("\t\tend");
        body.AddRange(["\tend", "\ttc=g:GetNext()", "end", "return n"]);
        var fn = ctx.Script.AddFunction("setatk", "g,e", body);
        return $"{fn}({g},e)";
    }
}

public sealed class ChangeLevelAction : CardGroupAction
{
    public override string Id => "do.change_level";
    public override string Group => "Monsters";
    public override string Name => "Change monsters' Levels";
    public override string Sentence => "{count} {cards} {target}: Level {mode} {amount} {source} {duration}";
    public override string Help => "\"Becomes the Level/Rank of 1 ...\" picks that monster as the effect resolves (Convex Knight). The two \"with this card\" " +
                                   "ways take 1 monster and this card: \"the Level of that monster or this card becomes the Level of the other\", \"the Levels of both " +
                                   "that monster and this card become the combined original Levels of those 2 monsters\".";
    protected override string Category => "";
    protected override string Hint => "HINTMSG_FACEUP";
    protected override string TargetedWording => "{0}";
    protected override bool AllowsThisCard => true;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Int("count", "How many", 1, 1, 10),
        // Saved before "all" was offered: no count mode is "exactly".
        CountModeParam,
        ParamSpec.Cards("cards", "Which monsters", DefaultCards, Zone.MonsterZone, thisCard: true),
        ParamSpec.Flag("target", "(targeting)", true),
        ParamSpec.Choice("mode", "Level", new("becomes", "becomes"), new("up", "increases by"), new("down", "decreases by"),
            new("updown", "increases or decreases by", "increases or decreases by (your choice)"),
            new("of_card", "becomes the Level/Rank of 1", "becomes the Level/Rank of 1 monster picked below"),
            new("one_other", "", "with this card: one becomes the other's Level"),
            new("combined", "", "with this card: both become their combined original Levels")),
        ParamSpec.Int("amount", "Amount", 1, 1, 12).ShowIf(b => b.Values.GetValueOrDefault("mode", "becomes") is "becomes" or "up" or "down" or "updown"),
        ParamSpec.Cards("source", "The monster whose Level/Rank it becomes",
                () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true }, Zone.Field)
            .WhenUsed().ShowIf(b => b.Values.GetValueOrDefault("mode") == "of_card"),
        ParamSpec.Choice("duration", "Until", Resets.Duration),
        KeptParam,
    ];

    /// <summary>The ways that pair the picked monster with this card.</summary>
    private static bool WithThisCard(string mode) => mode is "one_other" or "combined";

    private static string Change(BlockArgs args, bool single) => args.Choice("mode") switch
    {
        "up" => $"Level{(single ? "" : "s")} increase{(single ? "s" : "")} by {args.Int("amount")}",
        "down" => $"Level{(single ? "" : "s")} decrease{(single ? "s" : "")} by {args.Int("amount")}",
        "of_card" => $"Level{(single ? "" : "s")} become{(single ? "s" : "")} the Level/Rank of 1 {Source(args)}",
        _ => $"Level{(single ? "" : "s")} become{(single ? "s" : "")} {args.Int("amount")}",
    };

    /// <summary>"Machine monster on the field": the monster whose Level/Rank it becomes.</summary>
    private static string Source(BlockArgs args) => FilterText.Describe(args.Cards("source"), args.Pack, ownName: args.CardName);

    private static string Duration(BlockArgs args) => args.Describe("duration") is { Length: > 0 } d ? " " + d : "";

    /// <summary>
    /// "the Level of that monster or this card becomes the Level of the other", "the Levels of both that monster and this
    /// card become the combined original Levels of those 2 monsters, until the end of this turn".
    /// </summary>
    private static string Paired(BlockArgs args, string monster) => args.Choice("mode") == "combined"
        ? $"the Levels of both {monster} and this card become the combined original Levels of those 2 monsters{(Duration(args) is { Length: > 0 } d ? "," + d : "")}"
        : $"the Level of {monster} or this card becomes the Level of the other{Duration(args)}";

    public override string Describe(BlockArgs args)
    {
        var mode = args.Choice("mode");
        if (WithThisCard(mode) && !args.IsThisCard) return Paired(args, Some(args)) + args.ExceptText();
        // "increase or decrease the Level of 1 monster on the field by 1".
        if (mode == "updown")
            return args.IsThisCard ? $"increase or decrease this card's Level by {args.Int("amount")}{Duration(args)}"
                : $"increase or decrease the Level{(One(args) ? "" : "s")} of {Some(args)} by {args.Int("amount")}{Duration(args)}{args.ExceptText()}";
        if (args.IsThisCard) return $"this card's {Change(args, true)}{Duration(args)}";
        // "the Level of 1 monster becomes 4", "the Levels of all face-up monsters you control increase by 1".
        var change = Change(args, One(args));
        var noun = change.IndexOf(' ');
        return $"{StatOf(args, change[..noun])} {change[(noun + 1)..]}{Duration(args)}" + args.ExceptText();
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single) => args.Choice("mode") switch
    {
        "one_other" or "combined" => Paired(args, "that monster"),
        "updown" => $"increase or decrease {(single ? "its Level" : "their Levels")} by {args.Int("amount")}{Duration(args)}",
        _ => $"{(single ? "its" : "their")} {Change(args, single)}{Duration(args)}",
    };

    protected override string? AbleCheck(BlockContext ctx) => "c:IsFaceup() and c:IsLevelAbove(1)";

    public override void Compile(BlockContext ctx)
    {
        var mode = ctx.Args.Choice("mode");
        if (WithThisCard(mode))
        {
            Zones.RequireMonster(ctx, Name);
            if (ctx.Args.Cards("cards").ThisCard || ctx.Args.Int("count") != 1 || ctx.Args.CountMode != "exact")
            {
                ctx.Effect.Error($"\"{Name}\": \"with this card\" takes exactly 1 other monster (tick \"other\" in the picker) and this card.");
                return;
            }
            if (!ctx.Args.Cards("cards").ExcludeThisCard) ctx.Effect.Warn($"\"{Name}\": tick \"other\" in the picker, so the monster paired with this card isn't this card.");
            ctx.Effect.TargetChecks.Add("e:GetHandler():IsLevelAbove(1)");
        }
        if (mode == "of_card")
        {
            var src = SourceFilter(ctx);
            // A monster whose Level/Rank this one can take, other than the one changing when that is this card.
            ctx.Effect.TargetChecks.Add($"Duel.IsExistingMatchingCard({src.Function},tp,{src.Self},{src.Opp},1,{(ctx.Args.Cards("cards").ThisCard ? "e:GetHandler()" : src.Exclude)},e,tp)");
        }
        base.Compile(ctx);
    }

    private static FilterRef SourceFilter(BlockContext ctx) =>
        ctx.Script.Filter(ctx.Args.Cards("source"), "(c:IsType(TYPE_XYZ) and c:GetRank()>0 or c:GetLevel()>0)", "lvsrcfilter");

    protected override string Apply(string g, BlockContext ctx)
    {
        var amount = ctx.Args.Int("amount");
        var mode = ctx.Args.Choice("mode");
        var reset = Resets.Of(ctx);
        var kept = KeptLines(ctx, "e1", "\t\t").ToList();
        // One Level change on a monster: lv is the new Level (CHANGE) or the change (UPDATE).
        List<string> Register(string code, string value, string indent) =>
        [
            $"{indent}local e1=Effect.CreateEffect(e:GetHandler())", $"{indent}e1:SetType(EFFECT_TYPE_SINGLE)", .. kept.Select(k => indent + k.TrimStart('\t')),
            $"{indent}e1:SetCode({code})", $"{indent}e1:SetValue({value})", $"{indent}e1:SetReset({reset})", $"{indent}tc:RegisterEffect(e1)",
        ];
        switch (mode)
        {
            case "one_other" or "combined":
            {
                var body = new List<string>
                {
                    "local c=e:GetHandler()",
                    "local tc=g:GetFirst()",
                    "if not tc or not c:IsRelateToEffect(e) or c:IsFacedown() or not c:IsLocation(LOCATION_MZONE) or tc:IsFacedown() then return 0 end",
                };
                if (mode == "combined")
                {
                    // Both take the total of their original Levels.
                    body.Add("local lv=c:GetOriginalLevel()+tc:GetOriginalLevel()");
                    body.Add("for mc in aux.Next(Group.FromCards(c,tc)) do");
                    body.AddRange(Register("EFFECT_CHANGE_LEVEL", "lv", "\t").Select(l => l.Replace("tc:RegisterEffect", "mc:RegisterEffect", StringComparison.Ordinal)));
                    body.AddRange(["end", "return 2"]);
                }
                else
                {
                    // The player picks the one that changes; it takes the other's Level.
                    body.AddRange(
                    [
                        "if tc:GetLevel()==c:GetLevel() then return 0 end",
                        "local pg=Group.FromCards(c,tc)",
                        "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_FACEUP)",
                        "local mc=pg:Select(tp,1,1,nil):GetFirst()",
                        "local lv=(mc==c and tc or c):GetLevel()",
                    ]);
                    body.AddRange(Register("EFFECT_CHANGE_LEVEL", "lv", "").Select(l => l.Replace("tc:RegisterEffect", "mc:RegisterEffect", StringComparison.Ordinal)));
                    body.Add("return 1");
                }
                var paired = ctx.Script.AddFunction("pairlv", "g,e,tp", body);
                return $"{paired}({g},e,tp)";
            }
            case "updown":
            {
                // Up or down as the player declares the new Level; a Level the change can't take below 1 only goes up.
                var n = amount.ToString(CultureInfo.InvariantCulture);
                var fn = ctx.Script.AddFunction("updownlv", "g,e,tp",
                [
                    "local n=0", "local tc=g:GetFirst()", "while tc do", "\tif tc:IsFaceup() then",
                    "\t\tlocal lv=tc:GetLevel()",
                    $"\t\tlocal d={n}",
                    $"\t\tif lv>{n} then",
                    "\t\t\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_LVRANK)",
                    $"\t\t\td=Duel.AnnounceNumber(tp,lv+{n},lv-{n})-lv",
                    "\t\tend",
                    .. Register("EFFECT_UPDATE_LEVEL", "d", "\t\t"), "\t\tn=n+1",
                    "\tend", "\ttc=g:GetNext()", "end", "return n",
                ]);
                return $"{fn}({g},e,tp)";
            }
            case "of_card":
            {
                var src = SourceFilter(ctx);
                var fn = ctx.Script.AddFunction("copylv", "g,e,tp",
                [
                    "local n=0", "local tc=g:GetFirst()", "while tc do", "\tif tc:IsFaceup() then",
                    "\t\tDuel.Hint(HINT_SELECTMSG,tp,HINTMSG_FACEUP)",
                    $"\t\tlocal sc=Duel.SelectMatchingCard(tp,{src.Function},tp,{src.Self},{src.Opp},1,1,tc,e,tp):GetFirst()",
                    "\t\tif sc then",
                    "\t\t\tDuel.HintSelection(Group.FromCards(sc))",
                    "\t\t\tlocal lv=sc:IsType(TYPE_XYZ) and sc:GetRank() or sc:GetLevel()",
                    .. Register("EFFECT_CHANGE_LEVEL", "lv", "\t\t\t"), "\t\t\tn=n+1",
                    "\t\tend",
                    "\tend", "\ttc=g:GetNext()", "end", "return n",
                ]);
                return $"{fn}({g},e,tp)";
            }
        }
        var (code, value) = mode switch
        {
            "up" => ("EFFECT_UPDATE_LEVEL", amount.ToString(CultureInfo.InvariantCulture)),
            "down" => ("EFFECT_UPDATE_LEVEL", (-amount).ToString(CultureInfo.InvariantCulture)),
            _ => ("EFFECT_CHANGE_LEVEL", amount.ToString(CultureInfo.InvariantCulture)),
        };
        var changed = ctx.Script.AddFunction("changelv", "g,e",
        [
            "local n=0", "local tc=g:GetFirst()", "while tc do", "\tif tc:IsFaceup() then",
            "\t\tlocal e1=Effect.CreateEffect(e:GetHandler())", "\t\te1:SetType(EFFECT_TYPE_SINGLE)", .. KeptLines(ctx, "e1", "\t\t"), $"\t\te1:SetCode({code})",
            $"\t\te1:SetValue({value})", $"\t\te1:SetReset({reset})", "\t\ttc:RegisterEffect(e1)", "\t\tn=n+1",
            "\tend", "\ttc=g:GetNext()", "end", "return n",
        ]);
        return $"{changed}({g},e)";
    }
}

public sealed class SelfLevelAction : ActionBlock
{
    public override string Id => "do.self_level";
    public override string Group => "This card";
    public override string Name => "This card's Level changes";
    public override string Sentence => "This card's Level {mode} {amount} {duration}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("mode", "Level", new("becomes", "becomes"), new("up", "increases by"), new("down", "decreases by")),
        ParamSpec.Int("amount", "Amount", 1, 1, 12),
        ParamSpec.Choice("duration", "Until", Resets.Duration),
    ];

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var amount = ctx.Args.Int("amount");
        var (code, value) = ctx.Args.Choice("mode") switch
        {
            "up" => ("EFFECT_UPDATE_LEVEL", amount),
            "down" => ("EFFECT_UPDATE_LEVEL", -amount),
            _ => ("EFFECT_CHANGE_LEVEL", amount),
        };
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddStep(
        [
            $"local {r}=0",
            "if c:IsFaceup() and c:IsRelateToEffect(e) then",
            "\tlocal le=Effect.CreateEffect(c)",
            "\tle:SetType(EFFECT_TYPE_SINGLE)",
            $"\tle:SetCode({code})",
            $"\tle:SetValue({value.ToString(CultureInfo.InvariantCulture)})",
            $"\tle:SetReset({Resets.Of(ctx, "+RESET_DISABLE")})",
            "\tc:RegisterEffect(le)",
            $"\t{r}=1",
            "end",
        ], r, ctx.Link);
    }
}

/// <summary>"It cannot attack this turn", "this card can attack directly this turn" and the like, for chosen monsters or this card.</summary>
public sealed class BattleRuleAction : CardGroupAction
{
    public override string Id => "do.battle_rule";
    public override string Group => "Battle";
    public override string Name => "Monsters can't attack / can attack directly / attack twice";
    public override string Sentence => "{count} {cards} {target} {rule} this turn";
    protected override string Category => "";
    protected override string Hint => "HINTMSG_FACEUP";
    protected override string TargetedWording => "{0}";
    protected override bool AllowsThisCard => true;
    protected override Func<CardFilter> DefaultCards => () => new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    protected override IReadOnlyList<ParamSpec> BuildParams() =>
    [
        ParamSpec.Int("count", "How many", 1, 1, 10),
        // Saved before "all" was offered: no mode is "exactly".
        ModeParam,
        ParamSpec.Cards("cards", "Which monsters", DefaultCards, Zone.MonsterZone, thisCard: true),
        ParamSpec.Flag("target", "(targeting)", true),
        ParamSpec.Choice("rule", "What",
            new("cannot_attack", "cannot attack"),
            new("direct", "can attack directly"),
            new("twice", "can make a second attack during each Battle Phase"),
            new("pierce", "inflicts piercing battle damage"),
            new("cannot_be_targeted", "cannot be targeted by your opponent's card effects"),
            new("cannot_be_destroyed", "cannot be destroyed by battle"),
            new("cannot_be_destroyed_both", "cannot be destroyed by battle or card effects"),
            new("cannot_declare", "cannot declare an attack"),
            new("cannot_be_targeted_any", "cannot be targeted by card effects"),
            new("unaffected_other", "is unaffected by other cards' effects"),
            new("tuner", "is treated as a Tuner"),
            new("cannot_direct", "cannot attack directly"),
            new("cannot_be_destroyed_opp", "cannot be destroyed by your opponent's card effects")),
        KeptParam,
    ];

    /// <summary>The effects each monster gets: one per code.</summary>
    public static (string Code, string Value)[] Rule(string rule) => rule switch
    {
        "direct" => [("EFFECT_DIRECT_ATTACK", "1")],
        "twice" => [("EFFECT_EXTRA_ATTACK", "1")],
        "pierce" => [("EFFECT_PIERCE", "1")],
        "cannot_be_targeted" => [("EFFECT_CANNOT_BE_EFFECT_TARGET", "aux.tgoval")],
        "cannot_be_destroyed" => [("EFFECT_INDESTRUCTABLE_BATTLE", "1")],
        "cannot_be_destroyed_both" => [("EFFECT_INDESTRUCTABLE_BATTLE", "1"), ("EFFECT_INDESTRUCTABLE_EFFECT", "1")],
        "cannot_declare" => [("EFFECT_CANNOT_ATTACK_ANNOUNCE", "1")],
        "cannot_be_targeted_any" => [("EFFECT_CANNOT_BE_EFFECT_TARGET", "1")],
        // "Unaffected by card effects, except its own": the value function is written per script.
        "unaffected_other" => [("EFFECT_IMMUNE_EFFECT", "@unaffected")],
        "tuner" => [("EFFECT_ADD_TYPE", "TYPE_TUNER")],
        "cannot_direct" => [("EFFECT_CANNOT_DIRECT_ATTACK", "1")],
        "cannot_be_destroyed_opp" => [("EFFECT_INDESTRUCTABLE_EFFECT", "aux.indoval")],
        _ => [("EFFECT_CANNOT_ATTACK", "1")],
    };

    public override string Describe(BlockArgs args)
    {
        var rule = args.Describe("rule");
        if (!One(args)) rule = rule.Replace("inflicts", "inflict", StringComparison.Ordinal);
        return $"{Subject(args)} {rule} this turn{args.ExceptText()}";
    }

    protected override string TargetedAction(BlockArgs args, string pronoun, bool single)
    {
        var rule = args.Describe("rule");
        if (!single) rule = rule.Replace("inflicts", "inflict", StringComparison.Ordinal);
        return $"{(single ? "it" : "they")} {rule} this turn";
    }

    protected override string? AbleCheck(BlockContext ctx) => "c:IsFaceup()";

    protected override string Apply(string g, BlockContext ctx)
    {
        var body = new List<string> { "local n=0", "local tc=g:GetFirst()", "while tc do", "\tif tc:IsFaceup() then" };
        var i = 0;
        foreach (var (code, rawValue) in Rule(ctx.Args.Choice("rule")))
        {
            var value = rawValue == "@unaffected" ? ctx.Script.AddFunction("unaffval", "e,re", ["return e:GetHandler()~=re:GetOwner()"]) : rawValue;
            var v = $"e{++i}";
            body.AddRange([$"\t\tlocal {v}=Effect.CreateEffect(e:GetHandler())", $"\t\t{v}:SetType(EFFECT_TYPE_SINGLE)", .. KeptLines(ctx, v, "\t\t"), $"\t\t{v}:SetCode({code})",
                $"\t\t{v}:SetValue({value})", $"\t\t{v}:SetReset({Resets.EndOfTurn})", $"\t\ttc:RegisterEffect({v})"]);
        }
        body.AddRange(["\t\tn=n+1", "\tend", "\ttc=g:GetNext()", "end", "return n"]);
        var fn = ctx.Script.AddFunction("battlerule", "g,e", body);
        return $"{fn}({g},e)";
    }
}

public sealed class SelfBattleRuleAction : ActionBlock
{
    public override string Id => "do.self_battle_rule";
    public override string Group => "This card";
    public override string Name => "This card can attack directly / attack twice / pierce this turn";
    public override string Sentence => "This card {rule} this turn";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("rule", "What",
            new("direct", "can attack directly"),
            new("twice", "can make a second attack during each Battle Phase"),
            new("all", "can attack all monsters your opponent controls once each"),
            new("pierce", "inflicts piercing battle damage"),
            new("cannot_be_destroyed", "cannot be destroyed by battle"),
            new("unaffected", "is unaffected by your opponent's card effects")),
    ];

    public override void Compile(BlockContext ctx)
    {
        Zones.RequireMonster(ctx, Name);
        var (code, value) = ctx.Args.Choice("rule") switch
        {
            "twice" => ("EFFECT_EXTRA_ATTACK", "1"),
            "all" => ("EFFECT_ATTACK_ALL", "1"),
            "pierce" => ("EFFECT_PIERCE", "1"),
            "cannot_be_destroyed" => ("EFFECT_INDESTRUCTABLE_BATTLE", "1"),
            "unaffected" => ("EFFECT_IMMUNE_EFFECT", ctx.Script.AddFunction("immunity", "e,te", ["return te:GetOwnerPlayer()~=e:GetHandlerPlayer()"])),
            _ => ("EFFECT_DIRECT_ATTACK", "1"),
        };
        var r = ctx.Effect.NewLocal("r");
        ctx.Effect.AddStep(
        [
            $"local {r}=0",
            "if c:IsFaceup() and c:IsRelateToEffect(e) then",
            "\tlocal be=Effect.CreateEffect(c)",
            "\tbe:SetType(EFFECT_TYPE_SINGLE)",
            $"\tbe:SetCode({code})",
            $"\tbe:SetValue({value})",
            $"\tbe:SetReset({Resets.EndOfTurn}+RESET_DISABLE)",
            "\tc:RegisterEffect(be)",
            $"\t{r}=1",
            "end",
        ], r, ctx.Link);
    }
}

// ---------------------------------------------------------------- player-wide rules for the turn

public sealed class SummonRestrictionAction : ActionBlock
{
    public override string Id => "do.summon_restriction";
    public override string Group => "Turn";
    public override string Name => "Lock: a player can't Special Summon / activate / attack this turn";
    public override string Sentence => "{duration}, {who} cannot {what}, except {cards}";
    public override string Help => "The common modern restriction, e.g. \"for the rest of this turn, you cannot Special Summon monsters from the Extra Deck, except Link Monsters\", " +
                                   "or \"your opponent cannot activate cards or effects\". It starts when the effect resolves. Leave \"except\" as plain monsters for no exception.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("duration", "For", new("turn", "For the rest of this turn"), new("next", "Until the end of the next turn"), new("duel", "For the rest of this Duel")),
        ParamSpec.Choice("who", "Who", new("you", "you"), new("opponent", "your opponent"), new("both", "neither player")),
        ParamSpec.Choice("what", "Cannot", Locks.What),
        ParamSpec.Cards("cards", "Except / which (plain monsters for none)", () => new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Dark }, Zone.MonsterZone),
        ParamSpec.Flag("from_hand", "only if this card was activated from the hand", false),
    ];

    private static string Duration(BlockArgs args) => args.Choice("duration") switch
    {
        "next" => "until the end of the next turn",
        "duel" => "for the rest of this Duel",
        _ => "for the rest of this turn",
    };

    public override string Describe(BlockArgs args)
    {
        if (args.Bool("from_hand")) return "";
        var what = Locks.WhatOf(args);
        // A Spell/Trap Card's activation: the lock starts when the card resolves, and printed cards say so. Your own lock
        // with an exception reads as Over Tuning's: "you cannot ... for the rest of this turn after this card resolves, except ...".
        if (args.Choice("duration") == "turn" && DetailedTriggers.ActivatesThisCard(args.Effect))
        {
            const string after = "for the rest of this turn after this card resolves";
            if (args.Choice("who") == "you" && what != "search" && Locks.HasExcept(what, args.Cards("cards")))
            {
                var (head, except) = Locks.Split(what, args.Cards("cards"), args);
                return $"{head} {after}{except}";
            }
            return $"{after}, {Locks.Sentence(args.Choice("who"), what, args.Cards("cards"), args)}";
        }
        return $"{Duration(args)}, {Locks.Sentence(args.Choice("who"), what, args.Cards("cards"), args)}";
    }

    /// <summary>Dominus: "If you activated this card from your hand, you cannot activate the effects of LIGHT monsters for the rest of this Duel."</summary>
    public override string Afterword(BlockArgs args) =>
        args.Bool("from_hand")
            ? $"If you activated this card from your hand, {Locks.Sentence(args.Choice("who"), Locks.WhatOf(args), args.Cards("cards"), args)} {Duration(args)}."
            : "";

    public override void Compile(BlockContext ctx)
    {
        var what = Locks.WhatOf(ctx.Args);
        var lines = new List<string>();
        for (var part = 0; part < Locks.Parts(what); part++)
        {
            var v = part == 0 ? "sl" : $"sl{part + 1}";
            lines.Add($"local {v}=Effect.CreateEffect(e:GetHandler())");
            lines.AddRange(Locks.Lines(ctx, v, ctx.Args.Choice("who"), what, ctx.Args.Cards("cards"), null, part: part));
            switch (ctx.Args.Choice("duration"))
            {
                case "next": lines.Add($"{v}:SetReset(RESET_PHASE+PHASE_END,2)"); break;
                case "duel": break;
                default: lines.Add($"{v}:SetReset(RESET_PHASE+PHASE_END)"); break;
            }
            lines.Add($"Duel.RegisterEffect({v},tp)");
        }
        if (ctx.Args.Bool("from_hand"))
        {
            if (ctx.Card.Kind != CardKind.Trap) ctx.Effect.Warn("\"Only if activated from the hand\" is for Trap Cards that can be activated from the hand.");
            // Its own sentence: it applies whatever the rest of the effect did.
            ctx.Effect.AddFinally(["if e:GetHandler():IsStatus(STATUS_ACT_FROM_HAND) then", .. lines.Select(l => "\t" + l), "end"]);
            return;
        }
        ctx.Effect.AddStep(lines, null, ctx.Link);
    }
}

public sealed class ExtraNormalSummonAction : ActionBlock
{
    public override string Id => "do.extra_normal_summon";
    public override string Group => "Turn";
    public override string Name => "You can Normal Summon an extra monster this turn";
    public override string Sentence => "This turn, you can Normal Summon 1 {cards} in addition to your Normal Summon/Set";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Cards("cards", "Which monster", () => new CardFilter { CardType = FilterCardType.Monster }, Zone.MonsterZone),
    ];

    public override string Describe(BlockArgs args) =>
        $"during this turn, you can Normal Summon 1 {FilterText.Describe(args.Cards("cards"), args.Pack, withLocation: false)} in addition to your Normal Summon/Set (you can only gain this effect once per turn)";

    public override void Compile(BlockContext ctx)
    {
        var f = ctx.Script.MaterialFilter(ctx.Args.Cards("cards"), "extrasumfilter");
        var lines = new List<string>
        {
            "if Duel.GetFlagEffect(tp,id+1000)==0 then",
            "\tlocal es=Effect.CreateEffect(e:GetHandler())",
            "\tes:SetType(EFFECT_TYPE_FIELD)",
            "\tes:SetCode(EFFECT_EXTRA_SUMMON_COUNT)",
            "\tes:SetTargetRange(LOCATION_HAND+LOCATION_MZONE,0)",
        };
        if (f != "nil") lines.Add($"\tes:SetTarget({ctx.Script.AddFunction("extrasumtg", "e,c", [$"return {f}(c)"])})");
        lines.AddRange(["\tes:SetReset(RESET_PHASE+PHASE_END)", "\tDuel.RegisterEffect(es,tp)", "\tDuel.RegisterFlagEffect(tp,id+1000,RESET_PHASE+PHASE_END,0,1)", "end"]);
        ctx.Effect.AddStep(lines, null, ctx.Link);
    }
}
