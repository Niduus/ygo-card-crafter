using System.Globalization;
using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// The rules of the turn, the hand and Life Points: skipped phases and turns (Time Seal, Number 60, Terminal World), two
/// Battle Phases (An Unfortunate Report), the hand size limit (Enervating Mist, Hieroglyph Lithograph), damage and LP gain
/// swapped (Darklord Nurse Reficule, Rainbow Life), cards that cannot be discarded or returned to the Deck (Goblin of
/// Greed, G.B. Hunter) and fewer zones (Terminal World NEXT). Read only in the shapes the blocks write.
/// </summary>
public static partial class ScriptTranslator
{
    /// <summary>
    /// "if Duel.GetTurnPlayer()==tp then e1:SetLabel(Duel.GetTurnCount()) e1:SetCondition(s.skipcon) e1:SetReset(R,2)
    /// else e1:SetReset(R,1) end" inside a temporary effect: kept as one Reset setting that names the test and both counts
    /// (and the label, when the longer one skips this turn). Returns how many lines it takes, or null when it is not this.
    /// </summary>
    private static int? TurnResetIdiom(List<string> lines, int i, Dictionary<string, Dictionary<string, string>> vars)
    {
        var head = Regex.Match(lines[i], @"^if (.+) then$");
        if (!head.Success || head.Groups[1].Value.Contains(" then", StringComparison.Ordinal)) return null;
        string? v = null, label = null, condition = null, reset = null, then = null;
        var j = i + 1;
        for (; j < lines.Count && lines[j] != "else"; j++)
        {
            var m = Regex.Match(lines[j], @"^(\w+):Set(Label|Condition|Reset)\((.*)\)$");
            if (!m.Success || !vars.ContainsKey(m.Groups[1].Value) || v is not null && v != m.Groups[1].Value) return null;
            v = m.Groups[1].Value;
            var value = m.Groups[3].Value.Replace(" ", "");
            switch (m.Groups[2].Value)
            {
                case "Label" when label is null: label = value; break;
                case "Condition" when condition is null: condition = value; break;
                case "Reset" when reset is null && Regex.Match(value, @"^([\w+]+),(\d+)$") is { Success: true } rm: reset = rm.Groups[1].Value; then = rm.Groups[2].Value; break;
                default: return null;
            }
        }
        if (v is null || reset is null || j + 2 >= lines.Count || label is not null != condition is not null || label is not null && label != "Duel.GetTurnCount()") return null;
        var other = Regex.Match(lines[j + 1].Replace(" ", ""), $@"^{Regex.Escape(v)}:SetReset\(([\w+]+)(?:,(\d+))?\)$");
        if (!other.Success || other.Groups[1].Value != reset || lines[j + 2] != "end") return null;
        var set = vars[v];
        var test = Regex.Replace(head.Groups[1].Value, @"\s+", "").Replace("Duel.GetTurnPlayer()~=tp", "Duel.GetTurnPlayer()==1-tp", StringComparison.Ordinal);
        var (longer, shorter) = (Int(then!), other.Groups[2].Success ? Int(other.Groups[2].Value) : 1);
        // Yowie: "if it is your turn, 1 End Phase of theirs, else 2" is the same as asking whether it is theirs.
        if (label is null && longer < shorter && test is "Duel.GetTurnPlayer()==tp" or "Duel.GetTurnPlayer()==1-tp")
        {
            test = test.EndsWith("==tp", StringComparison.Ordinal) ? "Duel.GetTurnPlayer()==1-tp" : "Duel.GetTurnPlayer()==tp";
            (longer, shorter) = (shorter, longer);
        }
        set["Reset"] = TurnResetKey(reset, test, longer, shorter, label is not null);
        if (label is not null)
        {
            set["Label"] = label;
            set["Condition"] = condition!;
        }
        return j + 3 - i;
    }

    private static string TurnResetKey(string reset, string test, int longer, int shorter, bool label) =>
        $"{reset}@if:{test}@then:{longer.ToString(CultureInfo.InvariantCulture)}@else:{shorter.ToString(CultureInfo.InvariantCulture)}{(label ? "@label" : "")}";

    /// <summary>The Reset setting <see cref="TurnResetIdiom"/> keeps for what the block writes.</summary>
    private static string TurnResetKey(SkipPhaseAction.Lasting shape) =>
        TurnResetKey(shape.Reset, Regex.Replace(shape.If, @"\s+", ""), shape.Then, shape.Else ?? 1, shape.Label);

    private static string? PlayersOf(string targetRange) => targetRange switch { "1,0" => "you", "0,1" => "opponent", "1,1" => "both", _ => null };

    /// <summary>"return bit.band(r,REASON_EFFECT)~=0": the damage is from an effect.</summary>
    private static bool EffectDamageOnly(string? value, Functions fns) =>
        value is not null && Regex.IsMatch(value, @"^\w+\.\w+$") && fns.Get(value) is var (pars, _) && pars.Length >= 3 && pars[2] == "r"
        && Squash(Body(value, fns)) is "returnbit.band(r,REASON_EFFECT)~=0" or "returnbit.band(r,REASON_EFFECT)>0" or "returnr&REASON_EFFECT~=0" or "returnr&REASON_EFFECT>0";

    /// <summary>
    /// An effect an operation puts on a player for a while that these blocks write: "skip your opponent's next Draw Phase",
    /// "for the rest of this Duel, your hand size limit becomes 7", "each time you would take damage, gain that amount of
    /// LP instead". Null when it is none of these.
    /// </summary>
    private static BlockInstance? TurnRuleTemp(TempEffect temp, TargetInfo target, Functions fns)
    {
        if (temp.Parts is not [var (set, holder)]) return null;
        // Registered to the player the effect targets, which is its own player.
        if (holder == "p" && target.Player != "tp") return null;
        if (holder is not ("player" or "p") || !Flags(set.GetValueOrDefault("Type") ?? "").SetEquals(["EFFECT_TYPE_FIELD"]) || !HasFlags(set, "EFFECT_FLAG_PLAYER_TARGET")) return null;
        var code = set.GetValueOrDefault("Code") ?? "";
        var players = PlayersOf(Setting(set, "TargetRange"));
        var reset = Setting(set, "Reset");
        var value = set.GetValueOrDefault("Value");
        if (code is "EFFECT_SKIP_TURN" or "EFFECT_BP_TWICE" || TurnPhases.PhaseOf(code) is not null)
        {
            if (players is not ("you" or "opponent") || !Only(set, "Code", "Property", "TargetRange", "Reset", "Label", "Condition") || !reset.Contains("@if:", StringComparison.Ordinal))
                return null;
            if (set.GetValueOrDefault("Condition") is { } con && Squash(Body(con, fns)) != "returnDuel.GetTurnCount()~=e:GetLabel()") return null;
            var what = code switch { "EFFECT_SKIP_TURN" => "turn", "EFFECT_BP_TWICE" => "bp_twice", _ => TurnPhases.PhaseOf(code)! };
            foreach (var when in what is "turn" or "bp_twice" ? ["next"] : new[] { "next", "next_turn" })
                for (var n = 1; n <= (when == "next" && what is not ("turn" or "bp_twice") ? 5 : 1); n++)
                {
                    if (TurnResetKey(SkipPhaseAction.Shape(players, what, when, n)) != reset) continue;
                    var block = Block(SkipPhaseAction.BlockId).With("who", players).With("what", what).With("when", when).With("count", n);
                    // Time Seal: it can't be activated while the opponent already skips their Draw Phase.
                    var player = players == "you" ? "tp" : "1-tp";
                    if (target.Check is { } check && SplitTopLevelAnd(Squash(check)).Contains($"notDuel.IsPlayerAffectedByEffect({player},{code})"))
                        block = block.With("unless_applied", true);
                    return block;
                }
            return null;
        }
        if (code == "EFFECT_HAND_LIMIT" && players is not null && value is not null && Regex.IsMatch(value, @"^\d+$") && Only(set, "Code", "Property", "TargetRange", "Value", "Reset")
            && reset switch { "" => "duel", "RESET_PHASE+PHASE_END" => "turn", "RESET_PHASE+PHASE_END,2" => "next", _ => null } is { } duration)
        {
            var limit = Int(value);
            var block = Block(HandLimitAction.BlockId).With("duration", duration).With("who", players);
            return limit >= HandLimit.NoLimit ? block.With("mode", "none") : limit <= 60 ? block.With("mode", "limit").With("limit", limit) : null;
        }
        if (holder != "player" || reset != "RESET_PHASE+PHASE_END" || !Only(set, "Code", "Property", "TargetRange", "Value", "Reset")) return null;
        // Rainbow Life, D/D/D Rebel King Leonidas.
        if (code == "EFFECT_REVERSE_DAMAGE" && players == "you" && value == "1") return Block(TurnPlayerRuleAction.BlockId).With("rule", "reverse_damage");
        if (code == "EFFECT_REVERSE_DAMAGE" && players == "both" && EffectDamageOnly(value, fns)) return Block(TurnPlayerRuleAction.BlockId).With("rule", "reverse_effect_both");
        if (code == "EFFECT_REVERSE_RECOVER" && players == "opponent" && value == "1") return Block(TurnPlayerRuleAction.BlockId).With("rule", "opp_reverse_recover");
        return null;
    }

    /// <summary>
    /// A lasting rule on the players that these blocks write: "both players skip their Main Phase 2", "your opponent's hand
    /// size limit becomes 5", "any effect that would make your opponent gain LP inflicts ... damage", "cards on the field
    /// cannot be returned to the Deck", "neither player can activate an effect by discarding", "... more than 3 monsters".
    /// </summary>
    private static bool TurnRuleLasting(EffectEvent effect, HashSet<string> type, string code, Dictionary<string, string> set, string? range, Functions fns)
    {
        if (!type.SetEquals(["EFFECT_TYPE_FIELD"]) || range is null || !HasFlags(set, "EFFECT_FLAG_PLAYER_TARGET")) return false;
        var players = PlayersOf(Setting(set, "TargetRange"));
        var value = set.GetValueOrDefault("Value");
        if (players is null) return false;
        BlockInstance? block = null;
        if (TurnPhases.PhaseOf(code) is { } phase && Only(set, "Code", "Property", "Range", "TargetRange", "Condition"))
            block = Block(SkipPhasePassive.BlockId).With("who", players).With("phase", phase);
        else if (code == "EFFECT_HAND_LIMIT" && value is not null && Regex.IsMatch(value, @"^\d+$") && Only(set, "Code", "Property", "Range", "TargetRange", "Value", "Condition"))
        {
            var limit = Int(value);
            block = limit >= HandLimit.NoLimit ? Block(HandLimitPassive.BlockId).With("who", players).With("mode", "none")
                : limit <= 60 ? Block(HandLimitPassive.BlockId).With("who", players).With("mode", "limit").With("limit", limit) : null;
        }
        else if (code == "EFFECT_REVERSE_RECOVER" && players == "opponent" && value == "1" && Only(set, "Code", "Property", "Range", "TargetRange", "Value", "Condition"))
            block = Block("passive.player_rule").With("rule", "opp_reverse_recover");
        else if (code == "EFFECT_REVERSE_DAMAGE" && players is "you" or "both" && EffectDamageOnly(value, fns) && Only(set, "Code", "Property", "Range", "TargetRange", "Value", "Condition"))
            block = Block("passive.player_rule").With("rule", players == "you" ? "reverse_effect_damage" : "reverse_effect_damage_both");
        // G.B. Hunter: "Cards on the field cannot be returned to the Deck."
        else if (code == "EFFECT_CANNOT_TO_DECK" && players == "both" && set.GetValueOrDefault("Target") == "aux.TargetBoolFunction(Card.IsLocation,LOCATION_ONFIELD)"
                 && Only(set, "Code", "Property", "Range", "TargetRange", "Target", "Condition"))
            block = Block(HandDeckLockPassive.BlockId).With("what", "to_deck").With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.Field });
        // Infinite Light: "return c:IsSetCard(0x4a) and c:IsLocation(LOCATION_MZONE)", cards of either player where they are.
        else if (code == "EFFECT_CANNOT_TO_DECK" && players == "both" && set.GetValueOrDefault("Target") is { } tt && Regex.IsMatch(tt, @"^\w+\.\w+$")
                 && fns.Get(tt) is var (tpars, _) && tpars.Length == 2 && tpars[1] == "c" && Regex.Match(Body(tt, fns), @"^return (.+)$") is { Success: true } tm
                 && !tm.Groups[1].Value.Contains(" or ", StringComparison.Ordinal) && Only(set, "Code", "Property", "Range", "TargetRange", "Target", "Condition"))
        {
            var cards = new CardFilter { Owner = CardOwner.Either };
            foreach (var raw in SplitTopLevelAnd(tm.Groups[1].Value))
            {
                var t = raw.Trim();
                var where = t switch
                {
                    "c:IsOnField()" or "c:IsLocation(LOCATION_ONFIELD)" => Model.Zone.Field,
                    "c:IsLocation(LOCATION_MZONE)" => Model.Zone.MonsterZone,
                    "c:IsLocation(LOCATION_SZONE)" => Model.Zone.SpellTrapZone,
                    _ => Model.Zone.None,
                };
                if (where != Model.Zone.None && cards.Zones == Model.Zone.None) cards.Zones = where;
                else Term(t, cards);
            }
            if (cards.Zones == Model.Zone.None) return false;
            block = Block(HandDeckLockPassive.BlockId).With("what", "to_deck").With("cards", cards);
        }
        // Goblin of Greed: discards as a cost, by the players it names.
        else if (code == "EFFECT_CANNOT_DISCARD_HAND" && value == "1" && set.GetValueOrDefault("Target") is { } dt && Regex.IsMatch(dt, @"^\w+\.\w+$")
                 && fns.Get(dt) is var (dpars, _) && dpars.Length == 4 && dpars[3] == "r"
                 && Squash(Body(dt, fns)) is "returnr&REASON_COST==REASON_COST" or "returnbit.band(r,REASON_COST)==REASON_COST" or "returnbit.band(r,REASON_COST)~=0"
                 && Only(set, "Code", "Property", "Range", "TargetRange", "Target", "Value", "Condition"))
        {
            var owner = players switch { "you" => CardOwner.You, "opponent" => CardOwner.Opponent, _ => CardOwner.Either };
            block = Block(HandDeckLockPassive.BlockId).With("what", "discard_cost").With("cards", new CardFilter { Owner = owner, Zones = Model.Zone.Hand });
        }
        // Terminal World NEXT: "... more than 3 monsters or more than 3 Spell/Trap Cards on their field", one zone kind per effect.
        else if (code is "EFFECT_MAX_MZONE" or "EFFECT_MAX_SZONE" && value is not null && Regex.IsMatch(value, @"^\w+\.\w+$") && fns.Get(value) is var (zpars, _)
                 && zpars.Length == 4 && zpars[1] == "fp" && zpars[3] == "r" && Only(set, "Code", "Property", "Range", "TargetRange", "Value", "Condition"))
        {
            var body = Squash(Body(value, fns));
            var monsters = code == "EFFECT_MAX_MZONE";
            var m = monsters ? Regex.Match(body, @"^ifr~=LOCATION_REASON_TOFIELDthenreturn5endreturn([1-4])$")
                : Regex.Match(body, @"^ifr~=LOCATION_REASON_TOFIELDthenreturn5endlocalct=([1-4])fori=5,7doifDuel\.GetFieldCard\(fp,LOCATION_SZONE,i\)thenct=ct-1endendreturnct$");
            if (m.Success)
                block = Block(ZoneLimitPassive.BlockId).With("who", players).With("which", monsters ? "monsters" : "spells").With(monsters ? "monsters" : "spells", Int(m.Groups[1].Value));
        }
        if (block is null) return false;
        effect.Do.Add(block);
        return true;
    }
}
