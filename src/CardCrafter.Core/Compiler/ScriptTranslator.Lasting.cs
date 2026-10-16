using System.Globalization;
using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Lasting effects: the rules a card keeps while face-up, the effects an operation gives a card or a player for a while,
/// and the turn-long rules a cost puts on this card or its player. Each shape is read only when the block writes the same
/// effect, with the same settings, as the official script; anything else stays script.
/// </summary>
public static partial class ScriptTranslator
{
    private const string EndOfTurnReset = "RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END";
    private const string WhileFaceUpReset = "RESET_EVENT+RESETS_STANDARD";

    private static string Setting(Dictionary<string, string> set, string key) => (set.GetValueOrDefault(key) ?? "").Replace(" ", "");

    /// <summary>The flags an effect sets, whatever order they are written in.</summary>
    private static bool HasFlags(Dictionary<string, string> set, params string[] flags) =>
        Flags(set.GetValueOrDefault("Property") ?? "").SetEquals(flags);

    /// <summary>True when the effect sets nothing but the given settings (its type and description aside).</summary>
    private static bool Only(Dictionary<string, string> set, params string[] keys) =>
        set.Keys.All(k => k is "Type" or "Description" || keys.Contains(k));

    // ---------------------------------------------------------------- costs

    /// <summary>
    /// A turn-long rule a cost puts on this card or its player that the lock list doesn't read: "this card cannot attack
    /// the turn you activate this effect", "you cannot conduct your Battle Phase the turn you activate this card". Null
    /// when it is none of these; throws when it is one of them written in a way the blocks don't.
    /// </summary>
    private static BlockInstance? LastingCost(TempEffect temp, List<string> rawBody, Functions fns)
    {
        // The "if chk==0 then return ... end" check, which says whether it is "the turn you activate this".
        var check = string.Join(" ", rawBody.Select(l => l.Trim())).Replace("local c=e:GetHandler() ", "");
        var chk = Regex.Match(check, @"if chk==0 then return (.*?) end");
        var asks = chk.Success ? Regex.Replace(chk.Groups[1].Value, @"\bc:", "e:GetHandler():") : "";
        // "You cannot Normal or Special Summon monsters the turn you activate this card, except FIRE monsters."
        if (temp.Parts.Count is 2 or 3 && temp.Parts.All(p => p.Holder == "player") && temp.Parts.Select(p => LockOf(p.Set, fns)).ToList() is var locks
            && locks.All(l => l is { Who: "you" }) && CombinedLock(locks.Select(l => l!.Value.What)) is "normal_special" or "normal" or "summon_any" && locks[0] is { } lk)
        {
            var what = CombinedLock(locks.Select(l => l!.Value.What))!;
            var same = locks.All(l => FilterCompiler.Conditions(l!.Value.Except).SequenceEqual(FilterCompiler.Conditions(lk.Except)));
            var asked = SplitTopLevelAnd(asks);
            if (what != "normal_special" || !same || temp.Parts.Any(p => !HasFlags(p.Set, "EFFECT_FLAG_PLAYER_TARGET", "EFFECT_FLAG_OATH") || Setting(p.Set, "Reset") != "RESET_PHASE+PHASE_END"
                    || Setting(p.Set, "TargetRange") != "1,0" || !Only(p.Set, "Code", "Property", "TargetRange", "Target", "Reset"))
                || !asked.Contains("Duel.GetCustomActivityCount(id,tp,ACTIVITY_SUMMON)==0") && !asked.Any(a => Regex.IsMatch(a, $@"^Duel\.GetCustomActivityCount\({_sourceId},tp,ACTIVITY_SUMMON\)==0$"))
                || !asked.Contains("Duel.GetCustomActivityCount(id,tp,ACTIVITY_SPSUMMON)==0") && !asked.Any(a => Regex.IsMatch(a, $@"^Duel\.GetCustomActivityCount\({_sourceId},tp,ACTIVITY_SPSUMMON\)==0$")))
                throw No("its cost stops Normal and Special Summons in a way the blocks don't");
            return Block("cost.summon_restriction").With("what", what).With("when", "whole_turn").With("cards", lk.Except);
        }
        if (temp.Parts is not [var only]) return null;
        var set = only.Set;
        var code = set.GetValueOrDefault("Code");
        if (only.Holder == "c" && code is "EFFECT_CANNOT_ATTACK" or "EFFECT_CANNOT_ATTACK_ANNOUNCE" or "EFFECT_CANNOT_DIRECT_ATTACK")
        {
            var rule = code switch { "EFFECT_CANNOT_ATTACK_ANNOUNCE" => "declare", "EFFECT_CANNOT_DIRECT_ATTACK" => "direct", _ => "attack" };
            if (!HasFlags(set, "EFFECT_FLAG_CANNOT_DISABLE", "EFFECT_FLAG_OATH") || Setting(set, "Reset") != EndOfTurnReset
                || !Only(set, "Code", "Property", "Reset") || !SplitTopLevelAnd(asks).Contains(ThisCardTurnCost.Rule(rule).Check))
                throw No("its cost puts a rule on this card for the turn in a way the blocks don't");
            return Block(ThisCardTurnCost.BlockId).With("rule", rule);
        }
        // "You cannot conduct your Battle Phase the turn you activate this card."
        if (only.Holder == "player" && code == "EFFECT_CANNOT_BP")
        {
            if (!HasFlags(set, "EFFECT_FLAG_PLAYER_TARGET", "EFFECT_FLAG_OATH") || Setting(set, "TargetRange") != "1,0" || Setting(set, "Reset") != "RESET_PHASE+PHASE_END"
                || !Only(set, "Code", "Property", "TargetRange", "Reset"))
                throw No("its cost stops the Battle Phase in a way the blocks don't");
            var whole = SplitTopLevelAnd(asks).Contains("Duel.GetActivityCount(tp,ACTIVITY_BATTLE_PHASE)==0");
            return Block("cost.summon_restriction").With("what", "bp").With("when", whole ? "whole_turn" : "rest").With("cards", new CardFilter { CardType = FilterCardType.Monster });
        }
        return null;
    }

    // ---------------------------------------------------------------- operations

    /// <summary>
    /// "Target 1 monster; it gains 500 ATK and its Level increases by 1": the blocks target with one block per effect
    /// (or per option), so an effect that does several things to its target stays script.
    /// </summary>
    private static void OneTargetingBlock(EffectEvent effect)
    {
        var option = 0;
        var targeting = new HashSet<int>();
        foreach (var b in effect.Do)
        {
            if (b.Link == ActionLink.Option) option++;
            if (b.Values.GetValueOrDefault("target") != "true" || BlockRegistry.Find(b.BlockId)?.Param("target") is null) continue;
            if (!targeting.Add(option)) throw No("it does several things to its target, which the blocks say with one block");
        }
    }

    /// <summary>
    /// An effect an operation gives that finishes the step before it, or that goes with the next one: "Special Summon this
    /// card, but banish it when it leaves the field", and "negate its effects" written as two effects. True when the
    /// line is taken care of (the next one may be emptied).
    /// </summary>
    private static bool LastingTempLine(int index, List<string> body, List<TempEffect> temps, List<BlockInstance> blocks, TargetInfo target, bool targetCard)
    {
        var temp = temps[Int(body[index].Trim()[6..])];
        if (temp.Parts is [{ Holder: "c" } redirect] && redirect.Set.GetValueOrDefault("Code") == "EFFECT_LEAVE_FIELD_REDIRECT"
            && blocks.LastOrDefault() is { BlockId: "do.special_summon_self" } summoned && summoned.Link is ActionLink.And or ActionLink.IfYouDo
            && IsBanishRedirect(redirect.Set))
        {
            summoned.Values["banish"] = "true";
            return true;
        }
        // "e1: EFFECT_DISABLE ... e2: EFFECT_DISABLE_EFFECT", each created on its own: one "negate its effects"; the same
        // for "cannot be destroyed by battle or card effects" and "gains ATK/DEF".
        if (temp.Parts is [var first] && index + 1 < body.Count && body[index + 1].Trim() is var next && next.StartsWith("@TEMP ", StringComparison.Ordinal)
            && temps[Int(next[6..])].Parts is [var second] && second.Holder == first.Holder
            && (first.Set.GetValueOrDefault("Code"), second.Set.GetValueOrDefault("Code")) is var pair
            && (pair is ("EFFECT_DISABLE", "EFFECT_DISABLE_EFFECT") && first.Holder == "tc" && Setting(first.Set, "Reset") == Setting(second.Set, "Reset")
                || pair is ("EFFECT_INDESTRUCTABLE_BATTLE", "EFFECT_INDESTRUCTABLE_EFFECT") or ("EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE") && SameButCode(first.Set, second.Set)))
        {
            temp.Parts.Add(second);
            body[index + 1] = "";
        }
        return false;
    }

    /// <summary>Two effects with the same settings, apart from their code (and description).</summary>
    private static bool SameButCode(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        static IEnumerable<string> Rest(Dictionary<string, string> s) =>
            s.Where(kv => kv.Key is not ("Code" or "Description")).Select(kv => kv.Key + "=" + kv.Value.Replace(" ", "")).Order(StringComparer.Ordinal);
        return Rest(a).SequenceEqual(Rest(b));
    }

    /// <summary>"Banish it when it leaves the field", as every official script writes it.</summary>
    private static bool IsBanishRedirect(Dictionary<string, string> set) =>
        set.GetValueOrDefault("Value") == "LOCATION_REMOVED" && HasFlags(set, "EFFECT_FLAG_CANNOT_DISABLE")
        && Setting(set, "Reset") == "RESET_EVENT+RESETS_REDIRECT" && Only(set, "Code", "Property", "Reset", "Value");

    /// <summary>
    /// A temporary effect the blocks in <see cref="TempAction"/> don't read: rules for monsters on the field for the rest
    /// of the turn, and battle rules or Levels for the target. Null when it is none of these.
    /// </summary>
    private static BlockInstance? LastingTemp(TempEffect temp, TargetInfo target, bool targetCard, Functions fns)
    {
        var parts = temp.Parts;
        var codes = parts.Select(p => p.Set.GetValueOrDefault("Code") ?? "").ToList();
        var first = parts[0].Set;
        var holder = parts[0].Holder;
        if (holder == "player")
        {
            // "For the rest of this turn, you take no battle damage."
            if (parts.Count == 1 && codes[0] == "EFFECT_AVOID_BATTLE_DAMAGE" && Flags(first.GetValueOrDefault("Type") ?? "").SetEquals(["EFFECT_TYPE_FIELD"]) && HasFlags(first, "EFFECT_FLAG_PLAYER_TARGET") && Setting(first, "TargetRange") == "1,0"
                && first.GetValueOrDefault("Value") == "1" && Setting(first, "Reset") == "RESET_PHASE+PHASE_END" && Only(first, "Code", "Property", "TargetRange", "Value", "Reset"))
                return Block(TurnPlayerRuleAction.BlockId).With("rule", "no_battle");
            return TurnField(parts.Select(p => p.Set).ToList(), codes, fns);
        }
        if (holder != "tc" || !targetCard || target.Filter is null || !target.Filter.FaceUpOnly
            || parts.Any(p => !Flags(p.Set.GetValueOrDefault("Type") ?? "").SetEquals(["EFFECT_TYPE_SINGLE"]))) return null;
        // The blocks act on the target only while it is face-up, as these official effects check.
        BlockInstance OnTarget(string id) => Block(id).With("count", target.Count).With("cards", target.Filter).With("target", true);
        var reset = Setting(first, "Reset");
        if (parts.Any(p => Setting(p.Set, "Reset") != reset)) return null;
        // "Its Level increases by 1 until the end of this turn."
        if (parts.Count == 1 && codes[0] == "EFFECT_UPDATE_LEVEL" && reset is EndOfTurnReset or WhileFaceUpReset
            && int.TryParse(first.GetValueOrDefault("Value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv) && lv != 0
            && Only(first, "Code", "Value", "Reset", "Property"))
            return OnTarget("do.change_level").With("mode", lv < 0 ? "down" : "up").With("amount", Math.Abs(lv)).With("duration", reset == EndOfTurnReset ? "turn" : "always");
        // "It can attack directly / inflicts piercing battle damage / cannot be destroyed by battle this turn."
        if (reset == EndOfTurnReset && parts.All(p => Only(p.Set, "Code", "Value", "Reset", "Property")))
        {
            var value = first.GetValueOrDefault("Value");
            var rule = string.Join("+", codes.Order(StringComparer.Ordinal)) switch
            {
                "EFFECT_PIERCE" => "pierce",
                "EFFECT_DIRECT_ATTACK" => "direct",
                "EFFECT_EXTRA_ATTACK" when value == "1" => "twice",
                "EFFECT_INDESTRUCTABLE_BATTLE" when value == "1" => "cannot_be_destroyed",
                "EFFECT_INDESTRUCTABLE_BATTLE+EFFECT_INDESTRUCTABLE_EFFECT" when parts.All(p => p.Set.GetValueOrDefault("Value") == "1") => "cannot_be_destroyed_both",
                "EFFECT_CANNOT_ATTACK_ANNOUNCE" => "cannot_declare",
                "EFFECT_CANNOT_BE_EFFECT_TARGET" when value == "1" => "cannot_be_targeted_any",
                "EFFECT_ADD_TYPE" when value == "TYPE_TUNER" => "tuner",
                "EFFECT_IMMUNE_EFFECT" when value is not null && Regex.IsMatch(value, @"^\w+\.\w+$")
                    && Regex.Replace(Body(value, fns), @"\bte\b", "re") is "return e:GetHandler()~=re:GetOwner()" or "return re:GetOwner()~=e:GetHandler()" => "unaffected_other",
                _ => null,
            };
            if (rule is not null) return OnTarget("do.battle_rule").With("rule", rule);
        }
        return null;
    }

    /// <summary>
    /// "For the rest of this turn, Dragon monsters you control cannot be destroyed by battle": effects on the monsters
    /// of the field, registered to the player, that do.turn_field writes the same way. Null otherwise.
    /// </summary>
    private static BlockInstance? TurnField(List<Dictionary<string, string>> sets, List<string> codes, Functions fns)
    {
        var first = sets[0];
        if (sets.Any(s => Flags(s.GetValueOrDefault("Type") ?? "") is var t && !t.SetEquals(["EFFECT_TYPE_FIELD"])
                          || s.ContainsKey("Property") || Setting(s, "Reset") != "RESET_PHASE+PHASE_END" || !Only(s, "Code", "TargetRange", "Target", "Value", "Reset")
                          || Setting(s, "TargetRange") != Setting(first, "TargetRange") || s.GetValueOrDefault("Target") != first.GetValueOrDefault("Target")))
            return null;
        var range = Regex.Match(Setting(first, "TargetRange"), @"^(LOCATION_MZONE|0),(LOCATION_MZONE|0)$");
        if (!range.Success || range.Groups[1].Value == "0" && range.Groups[2].Value == "0") return null;
        var values = sets.Select(s => s.GetValueOrDefault("Value")).ToList();
        bool Same() => values.All(v => v == values[0]);
        int amount = 0;
        bool Number() => Same() && int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out amount) && amount != 0;
        var rule = string.Join("+", codes.Order(StringComparer.Ordinal)) switch
        {
            "EFFECT_UPDATE_ATTACK" when Number() => amount > 0 ? "gain_atk" : "lose_atk",
            "EFFECT_UPDATE_DEFENSE" when Number() && amount > 0 => "gain_def",
            "EFFECT_UPDATE_ATTACK+EFFECT_UPDATE_DEFENSE" when Number() => amount > 0 ? "gain_both" : "lose_both",
            "EFFECT_INDESTRUCTABLE_BATTLE" when values[0] == "1" => "indestructible_battle",
            "EFFECT_INDESTRUCTABLE_EFFECT" when values[0] == "1" => "indestructible_effect",
            "EFFECT_INDESTRUCTABLE_BATTLE+EFFECT_INDESTRUCTABLE_EFFECT" when Same() && values[0] == "1" => "indestructible_both",
            "EFFECT_CANNOT_BE_EFFECT_TARGET" when values[0] == "aux.tgoval" => "untargetable",
            "EFFECT_PIERCE" => "pierce",
            "EFFECT_DIRECT_ATTACK" => "direct",
            "EFFECT_CANNOT_ATTACK" => "cannot_attack",
            "EFFECT_DISABLE+EFFECT_DISABLE_EFFECT" when values.All(v => v is null) => "negated",
            _ => null,
        };
        if (rule is null) return null;
        // The monsters it applies to, face-up or not, as the official target range and target pick them.
        var cards = new CardFilter { CardType = FilterCardType.Monster, Zones = Model.Zone.MonsterZone };
        Locate(cards, range.Groups[1].Value, range.Groups[2].Value);
        if (first.GetValueOrDefault("Target") is { } tg)
        {
            List<string> terms;
            if (Regex.Match(tg, @"^aux\.TargetBoolFunction\((Card\.\w+),([\w+]+)\)$") is { Success: true } tb) terms = [$"c:{tb.Groups[1].Value[5..]}({tb.Groups[2].Value})"];
            else
            {
                var tbody = Body(tg, fns);
                var r = Regex.Match(tbody, @"^return (.+)$");
                if (!r.Success || tbody.Contains(" or ", StringComparison.Ordinal)) return null;
                terms = SplitTopLevelAnd(r.Groups[1].Value).Select(x => x.Trim()).ToList();
            }
            foreach (var term in terms)
            {
                if (term is "c~=e:GetHandler()" or "e:GetHandler()~=c") { cards.ExcludeThisCard = true; continue; }
                Term(term, cards);
            }
            cards.CardType = FilterCardType.Monster;
            cards.Zones = Model.Zone.MonsterZone;
        }
        var block = Block(TurnFieldAction.BlockId).With("rule", rule).With("cards", cards);
        return rule.StartsWith("gain_", StringComparison.Ordinal) || rule.StartsWith("lose_", StringComparison.Ordinal) ? block.With("amount", Math.Abs(amount)) : block;
    }

    // ---------------------------------------------------------------- locks

    /// <summary>
    /// A Pendulum Scale's "You cannot Pendulum Summon monsters, except "Deskbot" monsters", from the target of its
    /// EFFECT_CANNOT_SPECIAL_SUMMON. Null for other locks.
    /// </summary>
    private static CardFilter? PendulumLock(string body, Dictionary<string, string> set)
    {
        var text = Regex.Replace(body, @"\bsumtp\b", "sumtype");
        text = text.Replace("if not c then return false end ", "", StringComparison.Ordinal);
        const string pend = "bit.band(sumtype,SUMMON_TYPE_PENDULUM)==SUMMON_TYPE_PENDULUM";
        if (!text.Contains(pend, StringComparison.Ordinal)) return null;
        // Lasting on a Pendulum Scale: written so it can't be negated; for the turn, a plain lock on the player.
        var lasting = set.ContainsKey("Range");
        if (lasting ? !HasFlags(set, "EFFECT_FLAG_PLAYER_TARGET", "EFFECT_FLAG_CANNOT_DISABLE", "EFFECT_FLAG_CAN_FORBIDDEN") || Setting(set, "Range") != "LOCATION_PZONE"
                    : !HasFlags(set, "EFFECT_FLAG_PLAYER_TARGET"))
            throw No("its Pendulum Summon lock is written in a way the blocks don't");
        var except = new CardFilter { CardType = FilterCardType.Monster };
        string? spared = null;
        Match m;
        if ((m = Regex.Match(text, $@"^if (c:\w+\([^()]*\)) then return false end return {Regex.Escape(pend)}$")).Success) spared = m.Groups[1].Value;
        else if ((m = Regex.Match(text, $@"^return not (.+?) and {Regex.Escape(pend)}$")).Success
                 || (m = Regex.Match(text, $@"^return {Regex.Escape(pend)} and not (.+)$")).Success) spared = m.Groups[1].Value;
        else if (text != "return " + pend) throw No($"its Pendulum Summon lock picks monsters in a way the blocks can't ({Short(text)})");
        if (spared is not null)
        {
            if (spared.StartsWith('(') && spared.EndsWith(')') && Balanced(spared[1..^1])) spared = spared[1..^1];
            if (spared.Contains(" or ", StringComparison.Ordinal) || spared.Contains("not ", StringComparison.Ordinal)) throw No($"its Pendulum Summon lock picks monsters in a way the blocks can't ({Short(text)})");
            foreach (var t in SplitTopLevelAnd(spared)) Term(t.Trim(), except);
            except.CardType = FilterCardType.Monster;
            except.Zones = Model.Zone.None;
        }
        return except;
    }

    /// <summary>"cannot activate Spell/Trap Cards": only the activations of cards (re:IsHasType(EFFECT_TYPE_ACTIVATE)). Null for others.</summary>
    private static string? ActivationLock(string body) => Regex.Replace(body, @"\bte:", "re:") switch
    {
        "return re:IsHasType(EFFECT_TYPE_ACTIVATE)" => "activate_cards",
        "return re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:GetHandler():IsType(TYPE_SPELL)" or "return re:GetHandler():IsType(TYPE_SPELL) and re:IsHasType(EFFECT_TYPE_ACTIVATE)" => "activate_spell_cards",
        "return re:IsHasType(EFFECT_TYPE_ACTIVATE) and re:GetHandler():IsType(TYPE_TRAP)" or "return re:GetHandler():IsType(TYPE_TRAP) and re:IsHasType(EFFECT_TYPE_ACTIVATE)" => "activate_trap_cards",
        "return re:GetActivateLocation()==LOCATION_GRAVE" => "activate_gy",
        _ => null,
    };

    // ---------------------------------------------------------------- conditions

    /// <summary>"If this card attacks" / "battles" / "is attacked": who is in the battle. Null for other conditions.</summary>
    private static List<BlockInstance>? LastingCondition(string body)
    {
        var text = Regex.Replace(body.Replace("e:GetHandler()", "c", StringComparison.Ordinal), @"\s+", " ");
        var how = text switch
        {
            "return Duel.GetAttacker()==c" or "return c==Duel.GetAttacker()" => "attacks",
            "return Duel.GetAttackTarget()==c" or "return c==Duel.GetAttackTarget()" => "attacked",
            "return Duel.GetAttacker()==c or Duel.GetAttackTarget()==c" or "return c==Duel.GetAttacker() or c==Duel.GetAttackTarget()" => "battles",
            _ => null,
        };
        return how is null ? null : [Block(BattlingCondition.BlockId).With("how", how)];
    }

    // ---------------------------------------------------------------- lasting effects

    /// <summary>
    /// "Unaffected by your opponent's activated monster effects": an immunity value read part by part (whose effects,
    /// which kind, activated or not), as passive.immune's choice. Null when the parts make none of its choices.
    /// </summary>
    private static string? ImmuneWhose(string body)
    {
        if (!body.StartsWith("return ", StringComparison.Ordinal) || body.Contains(" or ", StringComparison.Ordinal)) return null;
        bool opp = false, other = false, activated = false;
        string? kind = null;
        foreach (var raw in SplitTopLevelAnd(body[7..]))
        {
            var t = raw.Trim();
            Match m;
            if (t is "te:GetOwnerPlayer()~=e:GetHandlerPlayer()" or "e:GetHandlerPlayer()~=te:GetOwnerPlayer()") opp = true;
            // On this card's own effect, its owner is this card.
            else if (t is "te:GetOwner()~=e:GetOwner()" or "te:GetOwner()~=e:GetHandler()" or "e:GetHandler()~=te:GetOwner()" or "e:GetOwner()~=te:GetOwner()") other = true;
            else if (t == "te:IsActivated()") activated = true;
            else if ((m = Regex.Match(t, @"^te:IsActiveType\((TYPE_MONSTER|TYPE_SPELL|TYPE_TRAP|TYPE_SPELL\+TYPE_TRAP)\)$")).Success && kind is null) kind = m.Groups[1].Value;
            else return null;
        }
        return (opp, other, activated, kind) switch
        {
            (true, false, false, null) => "opponent",
            (false, true, false, null) => "other",
            (false, false, false, "TYPE_SPELL+TYPE_TRAP") => "spelltrap",
            (false, false, false, "TYPE_SPELL") => "spell",
            (false, false, false, "TYPE_TRAP") => "trap",
            (false, true, false, "TYPE_MONSTER") => "monster",
            (true, false, false, "TYPE_MONSTER") => "opp_monster",
            (true, false, false, "TYPE_SPELL") => "opp_spell",
            (true, false, false, "TYPE_TRAP") => "opp_trap",
            (true, false, false, "TYPE_SPELL+TYPE_TRAP") => "opp_spelltrap",
            (true, false, true, null) => "opp_activated",
            (true, false, true, "TYPE_MONSTER") => "opp_activated_monster",
            (false, true, true, null) => "other_activated",
            _ => null,
        };
    }

    /// <summary>Lasting effects the main reader doesn't know: written on <paramref name="effect"/>, or false.</summary>
    private static bool MoreLasting(EffectEvent effect, HashSet<string> type, string code, Dictionary<string, string> set, string? range, Functions fns, CardData card)
    {
        var value = set.GetValueOrDefault("Value");
        // "While face-up on the field, this card is also DARK-Attribute", "this card's name is also treated as ..." (ScriptTranslator.TreatedAs.cs).
        if (TreatedAsLasting(effect, type, code, set, fns, card)) return true;
        // An Equip Spell's "The equipped monster gains 300 DEF".
        if (type.SetEquals(["EFFECT_TYPE_EQUIP"]) && !(card.Kind == CardKind.Spell && card.SpellKind != SpellKind.Equip) && code == "EFFECT_UPDATE_DEFENSE"
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var def) && def != 0 && Only(set, "Code", "Value", "Condition"))
        {
            effect.Do.Add(Block("passive.equipped").With("rule", def < 0 ? "lose_def" : "gain_def").With("amount", Math.Abs(def)));
            return true;
        }
        // "This card can be treated as 2 Tributes for the Tribute Summon of a Dragon monster."
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_DOUBLE_TRIBUTE" && value is not null && Only(set, "Code", "Value", "Condition"))
        {
            var text = Regex.Replace(Body(value, fns), @"\s+", " ");
            var m = Regex.Match(text, @"^local ec=e:GetHandler\(\) return (?:(.+) and )?\(?ec:IsFaceup\(\) or c:GetControler\(\)==ec:GetControler\(\)\)?$");
            if (!m.Success) throw No($"its double Tribute is for monsters the blocks can't pick ({Short(text)})");
            var f = new CardFilter { CardType = FilterCardType.Monster };
            if (m.Groups[1].Success)
            {
                if (m.Groups[1].Value.Contains(" or ", StringComparison.Ordinal)) throw No($"its double Tribute is for monsters the blocks can't pick ({Short(text)})");
                foreach (var t in SplitTopLevelAnd(m.Groups[1].Value)) Term(t.Trim(), f);
            }
            f.CardType = FilterCardType.Monster;
            f.Zones = Model.Zone.None;
            effect.Do.Add(Block(DoubleTributePassive.BlockId).With("cards", f));
            return true;
        }
        // "If "Advanced Dark" is not in a Field Zone, send this card to the GY": the IF blocks say when.
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_SELF_TOGRAVE" && range is not null && HasFlags(set, "EFFECT_FLAG_SINGLE_RANGE")
            && Only(set, "Code", "Range", "Property", "Condition"))
        {
            effect.Do.Add(Block(SelfDestroyPassive.BlockId).With("how", "grave"));
            return true;
        }
        // "Cannot be Normal Summoned, unless you control a "Ghostrick" monster": the IF blocks say when it can't.
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code is "EFFECT_CANNOT_SUMMON" or "EFFECT_CANNOT_FLIP_SUMMON" or "EFFECT_CANNOT_MSET" && card.IsMonster
            && set.ContainsKey("Condition") && Only(set, "Code", "Condition"))
        {
            effect.When = Block("when.while_face_up");
            effect.Do.Add(Block(CannotBeSummonedPassive.BlockId).With("how", code switch { "EFFECT_CANNOT_FLIP_SUMMON" => "flip", "EFFECT_CANNOT_MSET" => "set", _ => "normal" }));
            return true;
        }
        // "If you control an "Artmage" monster, you can activate this card the turn it was Set."
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_TRAP_ACT_IN_SET_TURN" && card.Kind == CardKind.Trap && HasFlags(set, "EFFECT_FLAG_SET_AVAILABLE")
            && Only(set, "Code", "Property", "Condition"))
        {
            effect.When = Block("when.while_face_up");
            effect.Do.Add(Block(ActivateSetTurnPassive.BlockId));
            return true;
        }
        // "This card can make up to 2 attacks on monsters", "can attack while in face-up Defense Position".
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && Only(set, "Code", "Value", "Range", "Property", "Condition")
            && (range is null ? !set.ContainsKey("Property") : range == "LOCATION_MZONE" && HasFlags(set, "EFFECT_FLAG_SINGLE_RANGE"))
            && (code, value) switch
            {
                ("EFFECT_EXTRA_ATTACK", "2") => "extra2",
                ("EFFECT_EXTRA_ATTACK_MONSTER", "1") => "extra_monster",
                ("EFFECT_EXTRA_ATTACK_MONSTER", "2") => "extra_monster2",
                ("EFFECT_DEFENSE_ATTACK", "1") => "defense_attack",
                ("EFFECT_DEFENSE_ATTACK", null or "0") => "defense_attack_atk",
                _ => null,
            } is { } ability)
        {
            effect.When = Block("when.while_face_up").With("zone", "monster_zone");
            effect.Do.Add(Block("passive.battle_ability").With("ability", ability));
            return true;
        }
        // "Your opponent takes no battle damage from attacks involving this card", "... takes any battle damage you would have taken".
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && Only(set, "Code", "Value", "Range", "Property", "Condition")
            && (range is null ? !set.ContainsKey("Property") : range == "LOCATION_MZONE" && HasFlags(set, "EFFECT_FLAG_SINGLE_RANGE"))
            && (code, value) switch
            {
                // Only whether this card has it counts, whatever the value.
                ("EFFECT_NO_BATTLE_DAMAGE", _) => "opp_no_battle_damage",
                ("EFFECT_REFLECT_BATTLE_DAMAGE", "1") => "reflect_battle",
                _ => null,
            } is { } selfRule)
        {
            effect.When = Block("when.while_face_up").With("zone", "monster_zone");
            effect.Do.Add(Block("passive.self_rule").With("rule", selfRule));
            return true;
        }
        // "Monsters your opponent controls must attack, if able", "your Toon monsters can attack directly".
        if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && code is "EFFECT_MUST_ATTACK" or "EFFECT_DIRECT_ATTACK" or "EFFECT_CANNOT_DIRECT_ATTACK" && range is not null
            && set.GetValueOrDefault("TargetRange") is { } tr && Only(set, "Code", "Range", "TargetRange", "Target", "Condition", "Value") && value is null or "1")
        {
            // Face-down monsters can't attack, so "face-up" in the picker changes nothing here.
            var cards = FieldTargets(tr, set.GetValueOrDefault("Target"), fns);
            var rule = code switch { "EFFECT_MUST_ATTACK" => "must_attack", "EFFECT_DIRECT_ATTACK" => "direct", _ => "cannot_direct" };
            effect.Do.Add(Block("passive.cards_rule").With("rule", rule).With("cards", cards));
            return true;
        }
        // "You take no battle damage", "any damage you take is halved": rules on this card's controller.
        if (type.SetEquals(["EFFECT_TYPE_FIELD"]) && range is not null && HasFlags(set, "EFFECT_FLAG_PLAYER_TARGET") && Setting(set, "TargetRange") == "1,0"
            && Only(set, "Code", "Range", "Property", "TargetRange", "Value", "Condition")
            && (code, value) switch
            {
                // The engine only asks whether the player is affected, whatever the value.
                ("EFFECT_AVOID_BATTLE_DAMAGE", _) => "no_battle_damage",
                ("EFFECT_CHANGE_DAMAGE", { } v) when Regex.IsMatch(v, @"^\w+\.\w+$") && fns.Get(v) is var (pars, _) && pars.Length >= 3
                    && Regex.Replace(Body(v, fns), $@"\b{Regex.Escape(pars[2])}\b", "val") == "return math.floor(val/2)" => "half_damage",
                _ => null,
            } is { } playerRule)
        {
            effect.Do.Add(Block("passive.player_rule").With("rule", playerRule));
            return true;
        }
        // "If the equipped monster would be destroyed (by battle), destroy this card instead."
        if (type.SetEquals(["EFFECT_TYPE_EQUIP"]) && !(card.Kind == CardKind.Spell && card.SpellKind != SpellKind.Equip) && code == "EFFECT_DESTROY_SUBSTITUTE"
            && HasFlags(set, "EFFECT_FLAG_IGNORE_IMMUNE") && Only(set, "Code", "Property", "Value", "Condition") && value is not null
            && (value == "1" ? "substitute"
                : Regex.IsMatch(value, @"^\w+\.\w+$") && Regex.Replace(Body(value, fns), @"\s+", "") is "returnbit.band(r,REASON_BATTLE)~=0" or "returnr&REASON_BATTLE~=0" ? "substitute_battle" : null) is { } substitute)
        {
            effect.Do.Add(Block("passive.equipped").With("rule", substitute));
            return true;
        }
        // "The equipped monster can attack directly."
        if (type.SetEquals(["EFFECT_TYPE_EQUIP"]) && !(card.Kind == CardKind.Spell && card.SpellKind != SpellKind.Equip) && code == "EFFECT_DIRECT_ATTACK"
            && Only(set, "Code", "Value", "Condition") && value is null or "1")
        {
            effect.Do.Add(Block("passive.equipped").With("rule", "direct"));
            return true;
        }
        // "This card cannot be destroyed by battle with a monster Special Summoned from the Extra Deck."
        if (type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_INDESTRUCTABLE_BATTLE" && value is not null && Regex.IsMatch(value, @"^[\w]+\.\w+$")
            && range is null or "LOCATION_MZONE" && Only(set, "Code", "Value", "Range", "Property", "Condition")
            && (range is null ? !set.ContainsKey("Property") : HasFlags(set, "EFFECT_FLAG_SINGLE_RANGE")))
        {
            var (pars, _) = fns.Get(value);
            var text = Regex.Replace(Body(value, fns), @"\s+", " ");
            var m = Regex.Match(text, @"^return (.+)$");
            if (pars.Length < 2 || pars[1] != "c" || !m.Success || text.Contains(" or ", StringComparison.Ordinal) || text.Contains("e:GetHandler()", StringComparison.Ordinal))
                throw No($"it is safe from battle with monsters the blocks can't pick ({Short(text)})");
            var with = new CardFilter { CardType = FilterCardType.Monster };
            foreach (var t in SplitTopLevelAnd(m.Groups[1].Value)) Term(t.Trim(), with);
            with.CardType = FilterCardType.Monster;
            with.Zones = Model.Zone.None;
            effect.When = Block("when.while_face_up").With("zone", "monster_zone");
            effect.Do.Add(Block("passive.indestructible").With("by", "battle_with").With("with", with));
            return true;
        }
        return false;
    }
}
