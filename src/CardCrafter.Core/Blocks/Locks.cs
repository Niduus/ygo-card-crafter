using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Blocks;

/// <summary>
/// Lock-outs and floodgates: "you cannot Special Summon from the Extra Deck, except Rank 4 Xyz Monsters" (Ryzeal),
/// "neither player can activate monster effects" (Majesty's Fiend), "Spell Cards cannot be activated" (Spell Canceller).
/// One list of locks is shared by the lasting, the cost ("the turn you activate this") and the DO ("for the rest of
/// this turn") blocks, and each lock compiles to the same Lua the official cards use.
/// </summary>
internal static class Locks
{
    public static readonly ChoiceOption[] What =
    [
        new("special", "Special Summon monsters"),
        new("special_extra", "Special Summon from the Extra Deck"),
        new("normal_special", "Normal or Special Summon monsters"),
        new("summon_any", "Summon monsters"),
        new("normal", "Normal Summon/Set monsters"),
        new("special_hand", "Special Summon monsters from the hand"),
        new("special_deck", "Special Summon monsters from the Deck"),
        new("special_gy", "Special Summon monsters from the GY"),
        new("activate_any", "activate cards or effects"),
        new("activate_monster", "activate monster effects"),
        new("activate_monster_hand", "activate the effects of monsters in the hand"),
        new("activate_monster_field", "activate the effects of monsters on the field"),
        new("activate_monster_gy", "activate the effects of monsters in the GY or that are banished"),
        new("activate_monster_offfield", "activate the effects of monsters in the hand, GY, and banishment"),
        new("activate_monster_hand_gy", "activate monster effects in the hand or GY"),
        new("activate_spell", "activate Spell Cards or Spell effects on the field"),
        new("activate_trap", "activate Trap Cards or Trap effects on the field"),
        new("activate_spelltrap", "activate Spell/Trap Cards or their effects on the field"),
        new("activate_monster_of", "activate the effects of these monsters:"),
        new("special_of", "Special Summon these monsters:"),
        new("attack", "declare attacks"),
        new("search", "add cards from the Deck to the hand, except by drawing them"),
        new("sset", "Set Spells/Traps"),
        new("bp", "conduct the Battle Phase"),
        new("activate_cards", "activate Spell/Trap Cards"),
        new("activate_spell_cards", "activate Spell Cards"),
        new("activate_trap_cards", "activate Trap Cards"),
        new("activate_gy", "activate the effects of cards in the GY"),
        new("pendulum", "Pendulum Summon monsters"),
        // Yummy★Snatchy: "you cannot Link Summon Link-3 or higher Link Monsters".
        new("link_of", "Link Summon these monsters:"),
        // Mimighoul Dungeon: "cannot Normal Summon monsters" (Setting is still allowed).
        new("normal_only", "Normal Summon monsters", "Normal Summon monsters (Setting them is still allowed)"),
        // Droll & Lock Bird: "cards cannot be added from either player's Main Deck to the hand", drawing included.
        new("search_all", "add cards from the Main Deck to the hand (drawing them too)"),
        // Mimighoul Dungeon: "nor declare an attack with monsters that were Special Summoned this turn".
        new("attack_of", "declare an attack with these monsters:"),
    ];

    /// <summary>The engine effects a lock is made of: one per kind of Summon it stops ("Normal or Special Summon" is two).</summary>
    public static int Parts(string what) => what switch
    {
        "normal" or "normal_special" or "search_all" => 2,
        "summon_any" => 3,
        _ => 1,
    };

    private static bool IsSummonLock(string what) =>
        what is "special" or "special_extra" or "normal_special" or "summon_any" or "normal" or "special_hand" or "special_deck" or "special_gy" or "pendulum" or "normal_only";

    /// <summary>The locks a turn-long restriction on yourself can be: the ones modern cards put on their own player.</summary>
    public static readonly ChoiceOption[] TurnWhat = What.Where(w => w.Value is "special" or "special_extra" or "activate_any" or "activate_monster" or "activate_monster_of" or "special_of" or "attack" or "bp" or "normal_special"
        or "activate_monster_hand_gy" or "link_of").ToArray();

    /// <summary>Locks whose card picker names what is locked ("the effects of LIGHT monsters"), not an exception.</summary>
    public static bool Names(string what) => what is "activate_monster_of" or "special_of" or "link_of" or "attack_of";

    public static readonly ChoiceOption[] Who =
    [
        new("both", "Neither player"),
        new("opponent", "Your opponent"),
        new("you", "You"),
    ];

    /// <summary>
    /// The lock from a block's values. Older blocks said "from the Extra Deck" with a separate "from" choice.
    /// </summary>
    public static string WhatOf(BlockArgs args)
    {
        var what = args.Choice("what");
        if (what == "special" && args.Instance.Values.GetValueOrDefault("from") == "extra") return "special_extra";
        return what;
    }

    /// <summary>True when the "except" filter picks some monsters, not every one: only then is there an exception.</summary>
    public static bool HasExcept(string what, CardFilter f) =>
        what is not ("search" or "search_all" or "bp" or "activate_cards" or "activate_spell_cards" or "activate_trap_cards" or "activate_gy") && !Names(what) && (FilterCompiler.Conditions(f).Any(c => c != "c:IsType(TYPE_MONSTER)") || f.ExceptOwnName || f.ExceptCode != 0);

    /// <summary>Locks on activating monster effects, wherever they are activated from.</summary>
    private static bool MonsterEffects(string what) => what is "activate_monster" or "activate_monster_hand" or "activate_monster_field" or "activate_monster_gy"
        or "activate_monster_offfield" or "activate_monster_hand_gy";

    private static string Verb(string what) => What.First(w => w.Value == what).Label;

    private static string Subject(string who) => who switch
    {
        "both" => "neither player can",
        "opponent" => "your opponent cannot",
        _ => "you cannot",
    };

    /// <summary>", except Rank 4 Xyz Monsters" or ", except with "Ryzeal" monsters", or nothing.</summary>
    public static string ExceptText(string what, CardFilter f, BlockArgs args)
    {
        if (!HasExcept(what, f)) return "";
        var cards = FilterText.Describe(f, args.Pack, plural: true, withLocation: false, ownName: args.CardName);
        return what switch
        {
            "attack" => $", except with {cards}",
            _ when IsSummonLock(what) || what == "sset" => $", except {cards}",
            // "you cannot activate monster effects, except "Prediction Princess" monsters'".
            _ when MonsterEffects(what) && cards.EndsWith('s') => $", except {cards}'",
            _ => $", except those of {cards}",
        };
    }

    /// <summary>"neither player can Special Summon monsters, except WIND monsters" (no capital, no period).</summary>
    public static string Sentence(string who, string what, CardFilter except, BlockArgs args)
    {
        // Spell Canceller and Jinzo: "Spell Cards, and their effects on the field, cannot be activated".
        if (who == "both" && !HasExcept(what, except))
        {
            var kind = what switch { "activate_spell" => "Spell", "activate_trap" => "Trap", "activate_spelltrap" => "Spell/Trap", _ => null };
            if (kind is not null) return $"{kind} Cards, and their effects on the field, cannot be activated";
        }
        // Droll & Lock Bird: "cards cannot be added from either player's Main Deck to the hand".
        if (what == "search_all")
            return who switch
            {
                "both" => "cards cannot be added from either player's Main Deck to the hand",
                "opponent" => "cards cannot be added from your opponent's Main Deck to their hand",
                _ => "cards cannot be added from your Main Deck to your hand",
            };
        if (what == "search")
            return who switch
            {
                "both" => "neither player can add cards from their Deck to their hand except by drawing them",
                "opponent" => "your opponent cannot add cards from their Deck to their hand except by drawing them",
                _ => "you cannot add cards from your Deck to your hand except by drawing them",
            };
        if (Names(what)) return $"{Subject(who)} {Named(what, except, args)}";
        if (what == "bp")
            return who switch { "both" => "neither player can conduct their Battle Phase", "opponent" => "your opponent cannot conduct their Battle Phase", _ => "you cannot conduct your Battle Phase" };
        return $"{Subject(who)} {Verb(what)}{ExceptText(what, except, args)}";
    }

    /// <summary>"activate the effects of LIGHT, EARTH and WIND monsters" or "Special Summon DARK monsters".</summary>
    private static string Named(string what, CardFilter f, BlockArgs args)
    {
        // "the effects of EARTH, WATER, FIRE, and WIND monsters": all of them are locked.
        var cards = FilterText.Describe(f, args.Pack, plural: true, withLocation: false, ownName: args.CardName).Replace(", or ", ", and ", StringComparison.Ordinal);
        return what switch
        {
            "special_of" => $"Special Summon {cards}",
            "link_of" => $"Link Summon {cards}",
            "attack_of" => $"declare an attack with {cards}",
            _ => $"activate the effects of {cards}",
        };
    }

    /// <summary>"you cannot Special Summon from the Extra Deck" and ", except Xyz Monsters" apart, for wording that puts the turn between them.</summary>
    public static (string Head, string Except) Split(string what, CardFilter except, BlockArgs args) =>
        what == "bp" ? ("you cannot conduct your Battle Phase", "")
        : Names(what) ? ($"you cannot {Named(what, except, args)}", "") : ($"you cannot {Verb(what)}", ExceptText(what, except, args));

    /// <summary>
    /// The settings of the lock effect <paramref name="v"/>: a field effect on the players (or on the monsters, for
    /// attacks). <paramref name="range"/> is set for a lasting effect of a card on the field; <paramref name="property"/>
    /// adds flags such as EFFECT_FLAG_OATH.
    /// </summary>
    /// <param name="part">Which of the lock's <see cref="Parts"/>: Normal Summons, Sets, Flip Summons or Special Summons.</param>
    public static List<string> Lines(BlockContext ctx, string v, string who, string what, CardFilter except, string? range, string? property = null, int part = 0)
    {
        var f = HasExcept(what, except) || Names(what) ? ctx.Script.MaterialFilter(except, "lockfilter") : "nil";
        var (self, opp) = who switch { "both" => ("1", "1"), "opponent" => ("0", "1"), _ => ("1", "0") };
        // Most locks spare the picked monsters; "of" locks hit only them.
        string Not(string card) => f == "nil" ? "" : Names(what) ? $" and {f}({card})" : $" and not {f}({card})";
        var lines = new List<string> { $"{v}:SetType(EFFECT_TYPE_FIELD)" };
        string Props(string flags) => property is null ? flags : $"{flags}+{property}";
        void Player(string code)
        {
            lines.Add($"{v}:SetProperty({Props("EFFECT_FLAG_PLAYER_TARGET")})");
            lines.Add($"{v}:SetCode({code})");
            if (range is not null) lines.Add($"{v}:SetRange({range})");
            lines.Add($"{v}:SetTargetRange({self},{opp})");
        }
        // Normal Summons, Sets and Flip Summons: the monster is all the engine passes.
        string? NonSpecial() => (what, part) switch
        {
            ("normal", 0) or ("normal_special", 0) or ("summon_any", 0) or ("normal_only", 0) => "EFFECT_CANNOT_SUMMON",
            ("normal", 1) => "EFFECT_CANNOT_MSET",
            ("summon_any", 1) => "EFFECT_CANNOT_FLIP_SUMMON",
            _ => null,
        };
        if (NonSpecial() is { } summonCode)
        {
            Player(summonCode);
            if (f != "nil") lines.Add($"{v}:SetTarget({ctx.Script.AddFunction("nsumlimit", "e,c", [$"return not {f}(c)"])})");
            return lines;
        }
        switch (what)
        {
            case "bp":
                Player("EFFECT_CANNOT_BP");
                break;
            case "sset":
                Player("EFFECT_CANNOT_SSET");
                if (f != "nil") lines.Add($"{v}:SetTarget({ctx.Script.AddFunction("ssetlimit", "e,c", [$"return not {f}(c)"])})");
                break;
            case "pendulum":
            {
                // A Pendulum Scale's "You cannot Pendulum Summon monsters, except ...": it cannot be negated, and is
                // lost while the card is forbidden, as the official scales write it.
                if (range is not null)
                {
                    lines.Add($"{v}:SetProperty({Props("EFFECT_FLAG_PLAYER_TARGET+EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_CAN_FORBIDDEN")})");
                    lines.Add($"{v}:SetCode(EFFECT_CANNOT_SPECIAL_SUMMON)");
                    lines.Add($"{v}:SetRange({range})");
                    lines.Add($"{v}:SetTargetRange({self},{opp})");
                }
                else Player("EFFECT_CANNOT_SPECIAL_SUMMON");
                lines.Add($"{v}:SetTarget({ctx.Script.AddFunction("pendlimit", "e,c,sump,sumtype,sumpos,targetp,se", ["return bit.band(sumtype,SUMMON_TYPE_PENDULUM)==SUMMON_TYPE_PENDULUM" + Not("c")])})");
                break;
            }
            case "special_hand" or "special_deck" or "special_gy":
            {
                Player("EFFECT_CANNOT_SPECIAL_SUMMON");
                var loc = what switch { "special_hand" => "LOCATION_HAND", "special_deck" => "LOCATION_DECK", _ => "LOCATION_GRAVE" };
                lines.Add($"{v}:SetTarget({ctx.Script.AddFunction("sumlimit", "e,c,sump,sumtype,sumpos,targetp,se", [$"return c:IsLocation({loc})" + Not("c")])})");
                break;
            }
            case "special" or "special_extra" or "special_of" or "normal_special" or "summon_any":
            {
                Player("EFFECT_CANNOT_SPECIAL_SUMMON");
                var test = what == "special_extra" ? "c:IsLocation(LOCATION_EXTRA)" + Not("c")
                    : what == "special_of" ? (f == "nil" ? null : $"{f}(c)")
                    : f == "nil" ? null : $"not {f}(c)";
                if (test is not null) lines.Add($"{v}:SetTarget({ctx.Script.AddFunction("sumlimit", "e,c,sump,sumtype,sumpos,targetp,se", [$"return {test}"])})");
                break;
            }
            case "search":
                Player("EFFECT_CANNOT_TO_HAND");
                lines.Add($"{v}:SetTarget(aux.TargetBoolFunction(Card.IsLocation,LOCATION_DECK))");
                break;
            // Droll & Lock Bird: nothing goes from the Main Deck to the hand, and nobody draws.
            case "search_all" when part == 0:
                Player("EFFECT_CANNOT_TO_HAND");
                lines.Add($"{v}:SetTarget(aux.TargetBoolFunction(Card.IsLocation,LOCATION_DECK))");
                break;
            case "search_all":
                Player("EFFECT_CANNOT_DRAW");
                break;
            case "link_of":
            {
                Player("EFFECT_CANNOT_SPECIAL_SUMMON");
                var test = "bit.band(sumtype,SUMMON_TYPE_LINK)==SUMMON_TYPE_LINK" + (f == "nil" ? "" : $" and {f}(c)");
                lines.Add($"{v}:SetTarget({ctx.Script.AddFunction("linklimit", "e,c,sump,sumtype,sumpos,targetp,se", [$"return {test}"])})");
                break;
            }
            case "attack" or "attack_of":
            {
                if (property is not null) lines.Add($"{v}:SetProperty({property})");
                lines.Add($"{v}:SetCode(EFFECT_CANNOT_ATTACK_ANNOUNCE)");
                if (range is not null) lines.Add($"{v}:SetRange({range})");
                lines.Add($"{v}:SetTargetRange({(self == "1" ? "LOCATION_MZONE" : "0")},{(opp == "1" ? "LOCATION_MZONE" : "0")})");
                if (f != "nil") lines.Add($"{v}:SetTarget({ctx.Script.AddFunction("atklimit", "e,c", [$"return {(what == "attack_of" ? "" : "not ")}{f}(c)"])})");
                break;
            }
            default:
            {
                Player("EFFECT_CANNOT_ACTIVATE");
                var test = ActivationTest(what) + Not("re:GetHandler()");
                lines.Add($"{v}:SetValue({ctx.Script.AddFunction("aclimit", "e,re,tp", [$"return {test}"])})");
                break;
            }
        }
        return lines;
    }

    private static string ActivationTest(string what) => what switch
    {
        "activate_monster" or "activate_monster_of" => "re:IsActiveType(TYPE_MONSTER)",
        "activate_monster_hand" => "re:IsActiveType(TYPE_MONSTER) and re:GetActivateLocation()==LOCATION_HAND",
        "activate_monster_field" => "re:IsActiveType(TYPE_MONSTER) and re:GetActivateLocation()==LOCATION_MZONE",
        "activate_monster_gy" => "re:IsActiveType(TYPE_MONSTER) and (re:GetActivateLocation()==LOCATION_GRAVE or re:GetActivateLocation()==LOCATION_REMOVED)",
        "activate_monster_offfield" => "re:IsActiveType(TYPE_MONSTER) and re:GetActivateLocation()&(LOCATION_HAND+LOCATION_GRAVE+LOCATION_REMOVED)~=0",
        "activate_monster_hand_gy" => "re:IsActiveType(TYPE_MONSTER) and (re:GetActivateLocation()==LOCATION_HAND or re:GetActivateLocation()==LOCATION_GRAVE)",
        "activate_spell" => "re:IsActiveType(TYPE_SPELL) and (re:GetHandler():IsOnField() or re:IsHasType(EFFECT_TYPE_ACTIVATE))",
        "activate_trap" => "re:IsActiveType(TYPE_TRAP) and (re:GetHandler():IsOnField() or re:IsHasType(EFFECT_TYPE_ACTIVATE))",
        "activate_spelltrap" => "re:IsActiveType(TYPE_SPELL+TYPE_TRAP) and (re:GetHandler():IsOnField() or re:IsHasType(EFFECT_TYPE_ACTIVATE))",
        "activate_cards" => "re:IsHasType(EFFECT_TYPE_ACTIVATE)",
        "activate_spell_cards" => "re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:GetHandler():IsType(TYPE_SPELL)",
        "activate_trap_cards" => "re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:GetHandler():IsType(TYPE_TRAP)",
        "activate_gy" => "re:GetActivateLocation()==LOCATION_GRAVE",
        _ => "true",
    };

    /// <summary>
    /// For "the turn you activate this": what the player must not have done yet this turn, as an activity counter and
    /// its count check. Null when the lock can't be counted.
    /// </summary>
    public static string? TurnCheck(BlockContext ctx, string what, CardFilter except)
    {
        var f = HasExcept(what, except) || Names(what) ? ctx.Script.MaterialFilter(except, "lockfilter") : "nil";
        string? activity, filter;
        switch (what)
        {
            case "special" or "special_extra" or "special_of":
            {
                // Counted when the monster is allowed; a Special Summon that breaks the lock is what counts against it.
                var breaks = what == "special_extra" ? "c:IsSummonLocation(LOCATION_EXTRA)" + (f == "nil" ? "" : $" and not {f}(c)")
                    : what == "special_of" ? (f == "nil" ? "true" : $"{f}(c)")
                    : f == "nil" ? "true" : $"not {f}(c)";
                activity = "ACTIVITY_SPSUMMON";
                filter = ctx.Script.AddFunction("counterfilter", "c", [$"return not ({breaks})"]);
                break;
            }
            // "You cannot conduct your Battle Phase the turn you activate this card": the game counts Battle Phases itself.
            case "bp":
                return "Duel.GetActivityCount(tp,ACTIVITY_BATTLE_PHASE)==0";
            // "You cannot Normal or Special Summon monsters the turn you activate this, except ...": both kinds counted.
            case "normal_special":
            {
                var both = ctx.Script.AddFunction("counterfilter", "c", [$"return {(f == "nil" ? "false" : $"{f}(c)")}"]);
                if (!ctx.Script.AddActivityCounter("ACTIVITY_SUMMON", both) || !ctx.Script.AddActivityCounter("ACTIVITY_SPSUMMON", both))
                {
                    ctx.Effect.Error("Two \"the turn you activate this\" locks on one card must lock the same thing.");
                    return null;
                }
                return "Duel.GetCustomActivityCount(id,tp,ACTIVITY_SUMMON)==0 and Duel.GetCustomActivityCount(id,tp,ACTIVITY_SPSUMMON)==0";
            }
            case "attack":
                activity = "ACTIVITY_ATTACK";
                filter = ctx.Script.AddFunction("counterfilter", "c", [$"return {(f == "nil" ? "false" : $"{f}(c)")}"]);
                break;
            case "activate_any" or "activate_monster" or "activate_monster_of" or "activate_monster_hand_gy":
            {
                // Where an effect was activated is the chain's triggering location, as Karnol's counter reads it.
                var test = what == "activate_monster_hand_gy"
                    ? "re:IsActiveType(TYPE_MONSTER) and Duel.GetChainInfo(cid,CHAININFO_TRIGGERING_LOCATION)&(LOCATION_HAND+LOCATION_GRAVE)~=0"
                    : ActivationTest(what);
                var breaks = test + (f == "nil" ? "" : Names(what) ? $" and {f}(re:GetHandler())" : $" and not {f}(re:GetHandler())");
                activity = "ACTIVITY_CHAIN";
                filter = ctx.Script.AddFunction("chainfilter", "re,tp,cid", [$"return not ({breaks})"]);
                break;
            }
            default: return null;
        }
        if (!ctx.Script.AddActivityCounter(activity, filter))
        {
            ctx.Effect.Error("Two \"the turn you activate this\" locks on one card must lock the same thing.");
            return null;
        }
        return $"Duel.GetCustomActivityCount(id,tp,{activity})==0";
    }
}

/// <summary>"Neither player can Special Summon monsters, except WIND monsters": a floodgate that lasts while this card is face-up.</summary>
public sealed class LockPassive : PassiveBlock
{
    public override string Id => "passive.lock";
    public override string Group => "Players";
    public override string Name => "Floodgate: players cannot Summon, activate, attack or search";
    public override string Sentence => "{who} {holder} cannot {what}, except {cards}";
    public override string Help => "Floodgates and lock-outs, like Vanity's Emptiness, Majesty's Fiend, Mistake, Barrier Statue or " +
                                   "Palm Ryzeal (\"You cannot Special Summon from the Extra Deck, except Rank 4 Xyz Monsters\"). " +
                                   "Leave \"except\" as plain monsters for no exception.";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        // Mimighoul Dungeon: "Any player who controls a face-down monster cannot Normal Summon monsters".
        ParamSpec.Choice("who", "Who", [.. Locks.Who, new("holder", "Any player who controls", "Any player who controls these cards:")]),
        ParamSpec.Choice("what", "Cannot", Locks.What),
        ParamSpec.Cards("cards", "Except (plain monsters for none)", () => new CardFilter { CardType = FilterCardType.Monster }, Zone.MonsterZone),
        ParamSpec.Cards("holder", "Who controls", () => new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceDownOnly = true }, Zone.Field)
            .Unprinted().WhenUsed().ShowIf(b => b.Values.GetValueOrDefault("who") == "holder"),
    ];

    /// <summary>The words a lock "on any player who controls ..." starts with: "any player who controls a face-down monster cannot ".</summary>
    public static string HolderSubject(BlockArgs args)
    {
        var f = args.Cards("holder").Clone();
        f.Owner = CardOwner.You;
        var noun = FilterText.Describe(f, args.Pack, withLocation: false, ownName: args.CardName);
        return $"any player who controls {SummonWording.Article(noun)} {noun} cannot ";
    }

    public override string Describe(BlockArgs args)
    {
        // "any player who controls a face-down monster cannot ...", worded as "your opponent cannot ..." is.
        if (args.Choice("who") == "holder")
            return HolderSubject(args) + Locks.Sentence("opponent", Locks.WhatOf(args), args.Cards("cards"), args)["your opponent cannot ".Length..];
        var text = Locks.Sentence(args.Choice("who"), Locks.WhatOf(args), args.Cards("cards"), args);
        // The Pendulum Scales' lock is written so it can't be negated, and the card says so.
        return Locks.WhatOf(args) == "pendulum" ? text + ". This effect cannot be negated" : text;
    }

    public override void Compile(BlockContext ctx)
    {
        var what = Locks.WhatOf(ctx.Args);
        var who = ctx.Args.Choice("who");
        for (var part = 0; part < Locks.Parts(what); part++)
        {
            if (who == "holder")
            {
                Holder(ctx, what, part);
                continue;
            }
            var lines = Locks.Lines(ctx, "{v}", who, what, ctx.Args.Cards("cards"), RangeOf(ctx), part: part);
            ctx.Effect.AddPassive(v => lines.Select(l => l.Replace("{v}", v)));
        }
    }

    /// <summary>
    /// "Any player who controls a face-down monster cannot ...": a lock on players is one effect per player, each while
    /// that player controls such a card; a lock on attacks checks the attacking monster's controller.
    /// </summary>
    private static void Holder(BlockContext ctx, string what, int part)
    {
        var holder = ctx.Args.Cards("holder").Clone();
        holder.Owner = CardOwner.You;
        var hf = ctx.Script.Filter(holder, null, "holdfilter");
        string Has(string player) => $"Duel.IsExistingMatchingCard({hf.Function},{player},{hf.Self},{hf.Opp},1,{hf.Exclude},e,{player})";
        var range = RangeOf(ctx);
        var both = Locks.Lines(ctx, "{v}", "both", what, ctx.Args.Cards("cards"), range, part: part);
        if (!both.Any(l => l.Contains("EFFECT_FLAG_PLAYER_TARGET", StringComparison.Ordinal)))
        {
            // Attacks: the lock is on the monsters of a player who controls such a card.
            var target = both.FirstOrDefault(l => l.StartsWith("{v}:SetTarget(", StringComparison.Ordinal));
            var inner = target is null ? null : target["{v}:SetTarget(".Length..^1];
            var fn = ctx.Script.AddFunction("holdtg", "e,c", ["local p=c:GetControler()", $"return {Has("p")}" + (inner is null ? "" : $" and {inner}(e,c)")]);
            var lines = both.Where(l => l != target).Append($"{{v}}:SetTarget({fn})").ToList();
            ctx.Effect.AddPassive(v => lines.Select(l => l.Replace("{v}", v)));
            return;
        }
        foreach (var (who, player) in new[] { ("you", "e:GetHandlerPlayer()"), ("opponent", "1-e:GetHandlerPlayer()") })
        {
            var con = ctx.Script.AddFunction("holdcon", "e", [$"return {Has(player)}"]);
            var lines = Locks.Lines(ctx, "{v}", who, what, ctx.Args.Cards("cards"), range, part: part).Append($"{{v}}:SetCondition({con})").ToList();
            ctx.Effect.AddPassive(v => lines.Select(l => l.Replace("{v}", v)));
        }
    }
}

/// <summary>"Any card sent to the GY is banished instead": Macro Cosmos, Dimensional Fissure, Kashtira Arise-Heart, Masked HERO Dark Law.</summary>
public sealed class BanishInsteadPassive : PassiveBlock
{
    public override string Id => "passive.banish_instead";
    public override string Group => "Players";
    public override string Name => "Floodgate: cards sent to the GY are banished instead";
    public override string Sentence => "Any {what} sent {from} to {whose} is banished instead";
    public override IReadOnlyList<ParamSpec> Params { get; } = BanishInstead.Params;

    public override string Describe(BlockArgs args) => BanishInstead.Text(args);

    public override void Compile(BlockContext ctx)
    {
        var lines = BanishInstead.Lines(ctx, "{v}", RangeOf(ctx));
        ctx.Effect.AddPassive(v => lines.Select(l => l.Replace("{v}", v)));
    }
}

/// <summary>"Until the end of the next turn, any card sent to the GY is banished instead": Dimension Shifter.</summary>
public sealed class BanishInsteadAction : ActionBlock
{
    public override string Id => "do.banish_instead";
    public override string Group => "Turn";
    public override string Name => "Cards sent to the GY are banished instead, for a while";
    public override string Sentence => "{duration}, any {what} sent {from} to {whose} is banished instead";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("duration", "For", new("turn", "For the rest of this turn"), new("next", "Until the end of the next turn")),
        .. BanishInstead.Params,
    ];

    public override string Describe(BlockArgs args) =>
        $"{(args.Choice("duration") == "next" ? "until the end of the next turn" : "for the rest of this turn")}, {BanishInstead.Text(args)}";

    public override void Compile(BlockContext ctx)
    {
        var lines = new List<string> { "local rd=Effect.CreateEffect(e:GetHandler())" };
        lines.AddRange(BanishInstead.Lines(ctx, "rd", null));
        lines.Add(ctx.Args.Choice("duration") == "next" ? "rd:SetReset(RESET_PHASE+PHASE_END,2)" : "rd:SetReset(RESET_PHASE+PHASE_END)");
        lines.Add("Duel.RegisterEffect(rd,tp)");
        ctx.Effect.AddStep(lines, null, ctx.Link);
    }
}

internal static class BanishInstead
{
    public static readonly IReadOnlyList<ParamSpec> Params =
    [
        ParamSpec.Choice("what", "What", new("card", "card"), new("monster", "monster")),
        ParamSpec.Choice("whose", "Whose GY", new("either", "the GY"), new("opponent", "your opponent's GY"), new("you", "your GY")),
        // Herald of the Arc Light: "Any monster sent from the hand or Main Deck to the GY is banished instead".
        ParamSpec.Choice("from", "From", new("anywhere", "", "from anywhere"), new("hand_deck", "from the hand or Main Deck")),
    ];

    public static string Text(BlockArgs args) =>
        $"any {args.Describe("what")} sent {(args.Choice("from") == "hand_deck" ? args.Describe("from") + " " : "")}to {args.Describe("whose")} is banished instead";

    public static List<string> Lines(BlockContext ctx, string v, string? range)
    {
        var whose = ctx.Args.Choice("whose");
        var tests = new List<string>();
        if (ctx.Args.Choice("what") == "monster") tests.Add("aux.DimensionalFissureTarget(e,c)");
        if (whose == "opponent") tests.Add("c:GetOwner()~=e:GetHandlerPlayer()");
        if (whose == "you") tests.Add("c:GetOwner()==e:GetHandlerPlayer()");
        var lines = new List<string>
        {
            $"{v}:SetType(EFFECT_TYPE_FIELD)",
            ctx.Args.Choice("from") == "hand_deck" ? $"{v}:SetProperty(EFFECT_FLAG_SET_AVAILABLE+EFFECT_FLAG_IGNORE_IMMUNE)"
                : $"{v}:SetProperty(EFFECT_FLAG_SET_AVAILABLE+EFFECT_FLAG_IGNORE_RANGE+EFFECT_FLAG_IGNORE_IMMUNE)",
            $"{v}:SetCode(EFFECT_TO_GRAVE_REDIRECT)",
        };
        if (range is not null) lines.Add($"{v}:SetRange({range})");
        // Everywhere (the engine ignores the range then), or only from the hand and Main Deck.
        var from = ctx.Args.Choice("from") == "hand_deck" ? "LOCATION_HAND+LOCATION_DECK" : "LOCATION_DECK";
        lines.Add(whose switch
        {
            "opponent" => $"{v}:SetTargetRange(0,{from})",
            "you" => $"{v}:SetTargetRange({from},0)",
            _ => $"{v}:SetTargetRange({from},{from})",
        });
        lines.Add($"{v}:SetValue(LOCATION_REMOVED)");
        if (tests.Count > 0) lines.Add($"{v}:SetTarget({ctx.Script.AddFunction("rmtg", "e,c", [$"return {string.Join(" and ", tests)}"])})");
        return lines;
    }
}

/// <summary>"Negate all Spell effects on the field" (Imperial Order), "... all other Trap effects" (Royal Decree), Skill Drain.</summary>
public sealed class NegateAllPassive : PassiveBlock
{
    public override string Id => "passive.negate_all";
    public override string Group => "Players";
    public override string Name => "Floodgate: negate all Spell / Trap / monster effects on the field";
    public override string Sentence => "Negate {what}";
    public override IReadOnlyList<ParamSpec> Params { get; } =
    [
        ParamSpec.Choice("what", "What",
            new("spell", "all Spell effects on the field"),
            new("other_spell", "all other Spell effects on the field"),
            new("trap", "all Trap effects on the field"),
            new("other_trap", "all other Trap effects on the field"),
            new("monster", "the effects of all face-up monsters on the field")),
    ];

    public override string Describe(BlockArgs args) => $"negate {args.Describe("what")}";

    public override void Compile(BlockContext ctx)
    {
        var range = RangeOf(ctx);
        var what = ctx.Args.Choice("what");
        if (what == "monster")
        {
            var tg = ctx.Script.AddFunction("distg", "e,c", ["return c:IsType(TYPE_EFFECT) or c:GetOriginalType()&TYPE_EFFECT~=0"]);
            ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetCode(EFFECT_DISABLE)", $"{v}:SetRange({range})",
                $"{v}:SetTargetRange(LOCATION_MZONE,LOCATION_MZONE)", $"{v}:SetTarget({tg})"]);
            return;
        }
        var type = what.EndsWith("spell", StringComparison.Ordinal) ? "TYPE_SPELL" : "TYPE_TRAP";
        var other = what.StartsWith("other_", StringComparison.Ordinal);
        var target = ctx.Script.AddFunction("distg", "e,c", [$"return {(other ? "c~=e:GetHandler() and " : "")}c:IsType({type})"]);
        // Continuous cards are negated where they are; Spells and Traps that activate are negated as they resolve.
        ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetCode(EFFECT_DISABLE)", $"{v}:SetRange({range})",
            $"{v}:SetTargetRange(LOCATION_SZONE,LOCATION_SZONE)", $"{v}:SetTarget({target})"]);
        var op = ctx.Script.AddFunction("disop", "e,tp,eg,ep,ev,re,r,rp",
        [
            "local tl=Duel.GetChainInfo(ev,CHAININFO_TRIGGERING_LOCATION)",
            $"if tl&LOCATION_SZONE~=0 and re:IsActiveType({type}){(other ? " and re:GetHandler()~=e:GetHandler()" : "")} then",
            "\tDuel.NegateEffect(ev)",
            "end",
        ]);
        ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", $"{v}:SetCode(EVENT_CHAIN_SOLVING)", $"{v}:SetRange({range})", $"{v}:SetOperation({op})"]);
        if (type == "TYPE_TRAP")
            ctx.Effect.AddPassive(v => [$"{v}:SetType(EFFECT_TYPE_FIELD)", $"{v}:SetCode(EFFECT_DISABLE_TRAPMONSTER)", $"{v}:SetRange({range})",
                $"{v}:SetTargetRange(LOCATION_MZONE,LOCATION_MZONE)", $"{v}:SetTarget({target})"]);
    }
}
