using System.Globalization;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

// The rules of the turn, the hand and Life Points that cards change: skipping a phase or a whole turn (Time Seal,
// Number 60: Dugares the Timeless, the Elemental Lords), two Battle Phases (An Unfortunate Report), the hand size limit
// (Hieroglyph Lithograph, Infinite Cards), damage and LP gain swapped (Darklord Nurse Reficule), cards that cannot be
// discarded or returned to the Deck (Goblin of Greed, G.B. Hunter) and how many cards a player can place on the field
// (Terminal World NEXT).

/// <summary>The phases a card can skip, with the engine effect that skips each.</summary>
internal static class TurnPhases
{
    public static readonly ChoiceOption[] Phases =
    [
        new("draw", "Draw Phase"), new("standby", "Standby Phase"), new("main1", "Main Phase 1"),
        new("battle", "Battle Phase"), new("main2", "Main Phase 2"),
    ];

    public static string Name(string phase) => Phases.FirstOrDefault(p => p.Value == phase)?.Label ?? "Battle Phase";

    public static string SkipCode(string phase) => phase switch
    {
        "draw" => "EFFECT_SKIP_DP",
        "standby" => "EFFECT_SKIP_SP",
        "main1" => "EFFECT_SKIP_M1",
        "main2" => "EFFECT_SKIP_M2",
        _ => "EFFECT_SKIP_BP",
    };

    /// <summary>The phase an effect code skips, or null when it skips none.</summary>
    public static string? PhaseOf(string code) => code switch
    {
        "EFFECT_SKIP_DP" => "draw",
        "EFFECT_SKIP_SP" => "standby",
        "EFFECT_SKIP_M1" => "main1",
        "EFFECT_SKIP_BP" => "battle",
        "EFFECT_SKIP_M2" => "main2",
        _ => null,
    };
}

/// <summary>
/// "Skip your next Draw Phase", "your opponent skips their next Battle Phase", "skip the Battle Phase of your next turn",
/// "skip your opponent's next turn", "your opponent conducts their next Battle Phase twice": an effect on the player
/// that lasts until that phase (or turn) has come, written as the official cards write each one.
/// </summary>
public sealed class SkipPhaseAction : ActionBlock
{
    public const string BlockId = "do.skip_phase";
    public override string Id => BlockId;
    public override string Group => "Turn";
    public override string Name => "Skip a phase or a turn / conduct the next Battle Phase twice";
    public override string Sentence => "{who} {what} {when} {count}";
    public override string Help => "\"Next\" is the next one to come, which can be later this turn (Runick: \"skip your next Battle Phase\"); " +
                                   "\"of the next turn\" waits for that player's next turn (Number 60: \"skip the Battle Phase of your next turn\").";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("you", "You"), new("opponent", "Your opponent")),
        ParamSpec.Choice("what", "What", [.. TurnPhases.Phases.Select(p => new ChoiceOption(p.Value, "skip the " + p.Label)),
            new("turn", "skip the next turn"), new("bp_twice", "conduct the next Battle Phase twice")]),
        ParamSpec.Choice("when", "Which one", new("next", "the next one", "the next one (\"your next Battle Phase\")"),
                new("next_turn", "the one of the next turn", "the one of the next turn (\"the Battle Phase of your next turn\")"))
            .ShowIf(b => IsPhase(b.Values.GetValueOrDefault("what", "draw"))),
        ParamSpec.Int("count", "How many (\"your next 2 Draw Phases\")", 1, 1, 5)
            .ShowIf(b => IsPhase(b.Values.GetValueOrDefault("what", "draw")) && b.Values.GetValueOrDefault("when", "next") == "next"),
        // Time Seal: "if chk==0 then return not Duel.IsPlayerAffectedByEffect(1-tp,EFFECT_SKIP_DP) end".
        ParamSpec.Flag("unless_applied", "cannot be activated while the player already skips it", false).Unprinted(),
    ];

    private static bool IsPhase(string what) => what is not ("turn" or "bp_twice");

    private static string When(BlockArgs args) => IsPhase(args.Choice("what")) ? args.Choice("when") : "next";

    private static int Count(BlockArgs args) => IsPhase(args.Choice("what")) && When(args) == "next" ? Math.Max(1, args.Int("count")) : 1;

    public override string Describe(BlockArgs args)
    {
        var you = args.Choice("who") == "you";
        var what = args.Choice("what");
        if (what == "turn") return you ? "skip your next turn" : "skip your opponent's next turn";
        if (what == "bp_twice") return you ? "you can conduct your next Battle Phase twice" : "your opponent conducts their next Battle Phase twice";
        var phase = TurnPhases.Name(what);
        if (When(args) == "next_turn") return you ? $"skip the {phase} of your next turn" : $"skip the {phase} of your opponent's next turn";
        var n = Count(args);
        var phases = n == 1 ? phase : $"{n.ToString(CultureInfo.InvariantCulture)} {phase}s";
        return you ? $"skip your next {phases}" : $"your opponent skips their next {phases}";
    }

    /// <summary>
    /// How the effect lasts: the reset, when it lasts one more turn (and then not for this one, by the turn count it was
    /// made in), and the counts, as the official script of each kind writes it. Shared with the translator, which reads
    /// back only these shapes.
    /// </summary>
    public sealed record Lasting(string Reset, string If, int Then, int? Else, bool Label);

    public static Lasting Shape(string who, string what, string when, int count)
    {
        var player = who == "you" ? "tp" : "1-tp";
        var turnReset = who == "you" ? "RESET_SELF_TURN" : "RESET_OPPO_TURN";
        var theirs = $"Duel.GetTurnPlayer()=={player}";
        const string end = "RESET_PHASE+PHASE_END";
        // Gamble, The Six Shinobi: a skipped turn, counted by End Phases of that player's turns.
        if (what == "turn") return new($"{end}+{turnReset}", theirs, 2, 1, false);
        if (when == "next_turn" || what is "main1" or "main2" && when == "next")
        {
            // Yowie: the next Draw Phase is in their next turn whatever phase it is now.
            if (what == "draw") return new($"{end}+{turnReset}", theirs, 2, 1, false);
            // Number 60, the Elemental Lords: "the Battle Phase of your next turn"; Break the Destiny: "their next Main Phase 1".
            var already = when == "next" ? $" and Duel.GetCurrentPhase()>={(what == "main1" ? "PHASE_MAIN1" : "PHASE_MAIN2")}" : "";
            return new($"{end}+{turnReset}", theirs + already, count + 1, count, true);
        }
        // Time Seal, Solomon's Lawbook, Frozen Soul, An Unfortunate Report: until the phase itself has gone by.
        return what switch
        {
            "draw" => new($"RESET_PHASE+PHASE_DRAW+{turnReset}", $"{theirs} and Duel.GetCurrentPhase()==PHASE_DRAW", count + 1, count == 1 ? null : count, false),
            "standby" => new($"RESET_PHASE+PHASE_STANDBY+{turnReset}", $"{theirs} and Duel.GetCurrentPhase()==PHASE_STANDBY", count + 1, count == 1 ? null : count, true),
            _ => new($"RESET_PHASE+PHASE_BATTLE+{turnReset}", $"{theirs} and (Duel.GetCurrentPhase()>=PHASE_BATTLE_START and Duel.GetCurrentPhase()<=PHASE_BATTLE)", count + 1, count, true),
        };
    }

    public static string CodeOf(string what) => what switch
    {
        "turn" => "EFFECT_SKIP_TURN",
        "bp_twice" => "EFFECT_BP_TWICE",
        _ => TurnPhases.SkipCode(what),
    };

    public override void Compile(BlockContext ctx)
    {
        var who = ctx.Args.Choice("who");
        var what = ctx.Args.Choice("what");
        var code = CodeOf(what);
        var player = who == "you" ? "tp" : "1-tp";
        var shape = Shape(who, what, When(ctx.Args), Count(ctx.Args));
        string Reset(int? n) => n is { } c ? $"sk:SetReset({shape.Reset},{c.ToString(CultureInfo.InvariantCulture)})" : $"sk:SetReset({shape.Reset})";
        var then = new List<string>();
        if (shape.Label)
        {
            var con = ctx.Script.AddFunction("skipcon", "e", ["return Duel.GetTurnCount()~=e:GetLabel()"]);
            then.AddRange(["sk:SetLabel(Duel.GetTurnCount())", $"sk:SetCondition({con})"]);
        }
        then.Add(Reset(shape.Then));
        if (ctx.Args.Bool("unless_applied")) ctx.Effect.TargetChecks.Add($"not Duel.IsPlayerAffectedByEffect({player},{code})");
        ctx.Effect.AddStep(
        [
            "local sk=Effect.CreateEffect(e:GetHandler())",
            "sk:SetType(EFFECT_TYPE_FIELD)",
            "sk:SetProperty(EFFECT_FLAG_PLAYER_TARGET)",
            $"sk:SetCode({code})",
            $"sk:SetTargetRange({(who == "you" ? "1,0" : "0,1")})",
            $"if {shape.If} then",
            .. then.Select(l => "\t" + l),
            "else",
            "\t" + Reset(shape.Else),
            "end",
            "Duel.RegisterEffect(sk,tp)",
        ], null, ctx.Link);
    }
}

/// <summary>"Both players skip their Main Phase 2" (Terminal World): a phase skipped every turn while this card is face-up.</summary>
public sealed class SkipPhasePassive : PassiveBlock
{
    public const string BlockId = "passive.skip_phase";
    public override string Id => BlockId;
    public override string Group => "Players";
    public override string Name => "Players skip a phase every turn";
    public override string Sentence => "{who} skip(s) {phase}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("both", "Both players"), new("opponent", "Your opponent"), new("you", "You")),
        ParamSpec.Choice("phase", "Phase", TurnPhases.Phases),
    ];

    public override string Describe(BlockArgs args)
    {
        var phase = TurnPhases.Name(args.Choice("phase"));
        return args.Choice("who") switch
        {
            "both" => $"both players skip their {phase}",
            "opponent" => $"your opponent skips their {phase}",
            _ => $"skip your {phase}",
        };
    }

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        var players = Players(ctx.Args.Choice("who"));
        var code = TurnPhases.SkipCode(ctx.Args.Choice("phase"));
        ctx.Effect.AddPassive(v =>
        [
            $"{v}:SetType(EFFECT_TYPE_FIELD)",
            $"{v}:SetProperty(EFFECT_FLAG_PLAYER_TARGET)",
            $"{v}:SetRange({range})",
            $"{v}:SetTargetRange({players})",
            $"{v}:SetCode({code})",
        ]);
    }

    /// <summary>The target range of a rule on "both" players, "you" or your "opponent".</summary>
    internal static string Players(string who) => who switch { "both" => "1,1", "opponent" => "0,1", _ => "1,0" };
}

/// <summary>"Your hand size limit becomes 7", "there is no limit to the hand size for each player".</summary>
internal static class HandLimit
{
    public static IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Whose", new("you", "Your"), new("opponent", "Your opponent's"), new("both", "Each player's")),
        ParamSpec.Choice("mode", "Limit", new("limit", "becomes"), new("none", "there is no limit", "there is no limit (Infinite Cards)")),
        ParamSpec.Int("limit", "Hand size limit", 7, 0, 60).ShowIf(b => b.Values.GetValueOrDefault("mode", "limit") == "limit"),
    ];

    /// <summary>The value Infinite Cards gives: more cards than a hand ever holds.</summary>
    public const int NoLimit = 100;

    public static string Text(BlockArgs args)
    {
        var who = args.Choice("who");
        if (args.Choice("mode") == "none")
            return who switch
            {
                "both" => "there is no limit to the hand size for each player",
                "opponent" => "there is no limit to your opponent's hand size",
                _ => "there is no limit to your hand size",
            };
        var n = args.Int("limit").ToString(CultureInfo.InvariantCulture);
        return who switch
        {
            "both" => $"the hand size limit of each player becomes {n}",
            "opponent" => $"your opponent's hand size limit becomes {n}",
            _ => $"your hand size limit becomes {n}",
        };
    }

    public static string Value(BlockArgs args) => (args.Choice("mode") == "none" ? NoLimit : args.Int("limit")).ToString(CultureInfo.InvariantCulture);

    /// <summary>The effect's settings: a rule on the players, as Enervating Mist and Hieroglyph Lithograph write it.</summary>
    public static List<string> Lines(BlockArgs args, string v, string? range)
    {
        var lines = new List<string> { $"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetCode(EFFECT_HAND_LIMIT)", $"{v}:SetProperty(EFFECT_FLAG_PLAYER_TARGET)" };
        if (range is not null) lines.Add($"{v}:SetRange({range})");
        lines.Add($"{v}:SetTargetRange({SkipPhasePassive.Players(args.Choice("who"))})");
        lines.Add($"{v}:SetValue({Value(args)})");
        return lines;
    }
}

/// <summary>"Your opponent's hand size limit becomes 5" (Enervating Mist), "the hand size limit of each player becomes 3" (Finite Cards).</summary>
public sealed class HandLimitPassive : PassiveBlock
{
    public const string BlockId = "passive.hand_limit";
    public override string Id => BlockId;
    public override string Group => "Players";
    public override string Name => "Hand size limit becomes N (or no limit)";
    public override string Sentence => "{who} hand size limit {mode} {limit}";
    public override string Help => "The cards a player must discard down to at the end of their turn (normally 6).";
    public override IReadOnlyList<ParamSpec> Params => HandLimit.Params;

    public override string Describe(BlockArgs args) => HandLimit.Text(args);

    public override void Compile(BlockContext ctx)
    {
        var lines = HandLimit.Lines(ctx.Args, "{v}", RangeOf(ctx));
        ctx.Effect.AddPassive(v => lines.Select(l => l.Replace("{v}", v)));
    }
}

/// <summary>"During the current Duel, your hand size limit becomes 7" (Hieroglyph Lithograph): the limit from when it resolves.</summary>
public sealed class HandLimitAction : ActionBlock
{
    public const string BlockId = "do.hand_limit";
    public override string Id => BlockId;
    public override string Group => "Turn";
    public override string Name => "Hand size limit becomes N, for a while";
    public override string Sentence => "{duration}, {who} hand size limit {mode} {limit}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("duration", "For", new("duel", "For the rest of this Duel"), new("turn", "For the rest of this turn"), new("next", "Until the end of the next turn")),
        .. HandLimit.Params,
    ];

    public override string Describe(BlockArgs args)
    {
        var duration = args.Choice("duration") switch { "turn" => "for the rest of this turn", "next" => "until the end of the next turn", _ => "for the rest of this Duel" };
        return $"{duration}, {HandLimit.Text(args)}";
    }

    public override void Compile(BlockContext ctx)
    {
        var lines = new List<string> { "local hl=Effect.CreateEffect(e:GetHandler())" };
        lines.AddRange(HandLimit.Lines(ctx.Args, "hl", null));
        switch (ctx.Args.Choice("duration"))
        {
            case "turn": lines.Add("hl:SetReset(RESET_PHASE+PHASE_END)"); break;
            case "next": lines.Add("hl:SetReset(RESET_PHASE+PHASE_END,2)"); break;
        }
        lines.Add("Duel.RegisterEffect(hl,tp)");
        ctx.Effect.AddStep(lines, null, ctx.Link);
    }
}

/// <summary>
/// "Cards on the field cannot be returned to the Deck" (G.B. Hunter), "neither player can activate an effect by
/// discarding from their hand" (Goblin of Greed), "your opponent cannot discard cards": rules on the players for the
/// cards the picker allows.
/// </summary>
public sealed class HandDeckLockPassive : PassiveBlock
{
    public const string BlockId = "passive.hand_deck_lock";
    public override string Id => BlockId;
    public override string Group => "Players";
    public override string Name => "Floodgate: cards cannot be returned to the Deck / discarded";
    public override string Sentence => "{cards} cannot be {what}";
    public override string Help => "For discards, the picker's \"whose\" says whose discards are stopped: your opponent's, yours or both players'. " +
                                   "Discards are stopped where the player picks the cards (costs and their own effects), as on official cards.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "Cannot be",
            new("to_deck", "returned to the Deck"),
            new("discard_cost", "discarded to activate an effect"),
            new("discard", "discarded")),
        ParamSpec.Cards("cards", "Which cards", () => new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field },
            Zone.Field | Zone.Hand | Zone.Graveyard | Zone.Banished),
    ];

    private static bool Plain(CardFilter f) => FilterCompiler.Conditions(f).Count == 0 && !f.ExcludeThisCard;

    public override string Describe(BlockArgs args)
    {
        var what = args.Choice("what");
        var f = args.Cards("cards");
        if (what == "to_deck") return $"{FilterText.Describe(f, args.Pack, plural: true, preposition: "", ownName: args.CardName)} cannot be returned to the Deck";
        var hand = f.Clone();
        hand.Zones = Zone.None;
        var (subject, their) = f.Owner switch
        {
            CardOwner.You => ("you cannot", "your"),
            CardOwner.Opponent => ("your opponent cannot", "their"),
            _ => ("neither player can", "their"),
        };
        if (what == "discard_cost")
            return Plain(hand) ? $"{subject} activate an effect by discarding from {their} hand"
                : $"{subject} discard {FilterText.Describe(Anyone(hand), args.Pack, plural: true, withLocation: false, ownName: args.CardName)} to activate an effect";
        return $"{subject} discard {(Plain(hand) ? "cards" : FilterText.Describe(Anyone(hand), args.Pack, plural: true, withLocation: false, ownName: args.CardName))}";
    }

    private static CardFilter Anyone(CardFilter f)
    {
        var g = f.Clone();
        g.Owner = CardOwner.Either;
        return g;
    }

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        var what = ctx.Args.Choice("what");
        var filter = ctx.Args.Cards("cards").Clone();
        if (what == "to_deck")
        {
            var zones = filter.Zones == Zone.None ? Zone.Field : filter.Zones;
            var loc = Lua.Locations(zones);
            var cards = filter.Clone();
            cards.Zones = Zone.None;
            cards.Owner = CardOwner.Either;
            string target;
            // G.B. Hunter: every card on the field.
            if (Plain(cards) && filter.Owner == CardOwner.Either) target = $"aux.TargetBoolFunction(Card.IsLocation,{loc})";
            else
            {
                var tests = new List<string> { $"c:IsLocation({loc})" };
                if (filter.Owner == CardOwner.You) tests.Add("c:IsControler(e:GetHandlerPlayer())");
                if (filter.Owner == CardOwner.Opponent) tests.Add("c:IsControler(1-e:GetHandlerPlayer())");
                if (!Plain(cards)) tests.Add($"{ctx.Script.Filter(cards).Function}(c,e,e:GetHandlerPlayer())");
                if (filter.ExcludeThisCard) tests.Add("c~=e:GetHandler()");
                target = ctx.Script.AddFunction("tdlimit", "e,c", [$"return {string.Join(" and ", tests)}"]);
            }
            ctx.Effect.AddPassive(v =>
            [
                $"{v}:SetType(EFFECT_TYPE_FIELD)",
                $"{v}:SetCode(EFFECT_CANNOT_TO_DECK)",
                $"{v}:SetRange({range})",
                $"{v}:SetProperty(EFFECT_FLAG_PLAYER_TARGET)",
                $"{v}:SetTargetRange(1,1)",
                $"{v}:SetTarget({target})",
            ]);
            return;
        }
        // The player who discards is the one stopped: whose cards the picker names.
        var players = filter.Owner switch { CardOwner.You => "1,0", CardOwner.Opponent => "0,1", _ => "1,1" };
        var hand = filter.Clone();
        hand.Zones = Zone.None;
        hand.Owner = CardOwner.Either;
        var checks = new List<string>();
        if (what == "discard_cost") checks.Add("r&REASON_COST==REASON_COST");
        if (!Plain(hand)) checks.Add($"{ctx.Script.Filter(hand).Function}(dc,e,e:GetHandlerPlayer())");
        // No target at all stops every discard, as the engine reads it.
        var dtarget = checks.Count == 0 ? null : ctx.Script.AddFunction("discardlimit", "e,dc,re,r", [$"return {string.Join(" and ", checks)}"]);
        ctx.Effect.AddPassive(v =>
        {
            var lines = new List<string>
            {
                $"{v}:SetType(EFFECT_TYPE_FIELD)",
                $"{v}:SetProperty(EFFECT_FLAG_PLAYER_TARGET)",
                $"{v}:SetRange({range})",
                $"{v}:SetTargetRange({players})",
                $"{v}:SetCode(EFFECT_CANNOT_DISCARD_HAND)",
            };
            if (dtarget is not null) lines.Add($"{v}:SetTarget({dtarget})");
            lines.Add($"{v}:SetValue(1)");
            return lines;
        });
    }
}

/// <summary>
/// "Cards cannot be placed on the field if it would give any player more than 3 monsters or more than 3 Spell/Trap Cards
/// on their field" (Terminal World NEXT): fewer Monster and Spell & Trap Zones a player can place cards in.
/// </summary>
public sealed class ZoneLimitPassive : PassiveBlock
{
    public const string BlockId = "passive.zone_limit";
    public override string Id => BlockId;
    public override string Group => "Players";
    public override string Name => "Floodgate: players can have only N monsters / Spells & Traps";
    public override string Sentence => "Cards cannot be placed on the field if it would give {who} more than {monsters} monsters {which} {spells} Spell/Trap Cards";
    public override string Help => "Cards already on the field stay; only cards placed later are stopped. The Field Zone and Pendulum Zones count as Spell/Trap Cards.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("who", "Who", new("both", "any player"), new("opponent", "your opponent"), new("you", "you")),
        ParamSpec.Choice("which", "Limit", new("both", "monsters and Spell/Trap Cards"), new("monsters", "monsters only"), new("spells", "Spell/Trap Cards only")),
        ParamSpec.Int("monsters", "Most monsters", 3, 1, 4).ShowIf(b => b.Values.GetValueOrDefault("which", "both") != "spells"),
        ParamSpec.Int("spells", "Most Spell/Trap Cards", 3, 1, 4).ShowIf(b => b.Values.GetValueOrDefault("which", "both") != "monsters"),
    ];

    public override string Describe(BlockArgs args)
    {
        var (who, side) = args.Choice("who") switch { "opponent" => ("your opponent", "their"), "you" => ("you", "your"), _ => ("any player", "their") };
        var parts = new List<string>();
        if (args.Choice("which") != "spells") parts.Add($"more than {args.Int("monsters")} monster{(args.Int("monsters") == 1 ? "" : "s")}");
        if (args.Choice("which") != "monsters") parts.Add($"more than {args.Int("spells")} Spell/Trap Card{(args.Int("spells") == 1 ? "" : "s")}");
        return $"cards cannot be placed on the field if it would give {who} {string.Join(" or ", parts)} on {side} field";
    }

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        var players = SkipPhasePassive.Players(ctx.Args.Choice("who"));
        var which = ctx.Args.Choice("which");
        if (which != "spells")
        {
            var n = ctx.Args.Int("monsters").ToString(CultureInfo.InvariantCulture);
            var mvalue = ctx.Script.AddFunction("mvalue", "e,fp,rp,r", ["if r~=LOCATION_REASON_TOFIELD then return 5 end", $"return {n}"]);
            ctx.Effect.AddPassive(v => Limit(v, "EFFECT_MAX_MZONE", range, players, mvalue));
        }
        if (which != "monsters")
        {
            // The Field Zone and Pendulum Zones hold Spell/Trap Cards too, so each card in them takes one of the zones left.
            var n = ctx.Args.Int("spells").ToString(CultureInfo.InvariantCulture);
            var svalue = ctx.Script.AddFunction("svalue", "e,fp,rp,r",
                ["if r~=LOCATION_REASON_TOFIELD then return 5 end", $"local ct={n}", "for i=5,7 do", "\tif Duel.GetFieldCard(fp,LOCATION_SZONE,i) then ct=ct-1 end", "end", "return ct"]);
            ctx.Effect.AddPassive(v => Limit(v, "EFFECT_MAX_SZONE", range, players, svalue));
        }
    }

    private static IEnumerable<string> Limit(string v, string code, string range, string players, string value) =>
    [
        $"{v}:SetType(EFFECT_TYPE_FIELD)",
        $"{v}:SetProperty(EFFECT_FLAG_PLAYER_TARGET)",
        $"{v}:SetCode({code})",
        $"{v}:SetRange({range})",
        $"{v}:SetTargetRange({players})",
        $"{v}:SetValue({value})",
    ];
}
