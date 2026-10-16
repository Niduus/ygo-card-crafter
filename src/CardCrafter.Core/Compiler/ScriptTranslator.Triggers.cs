using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// WHEN blocks with details: an activation and what it is ("When your opponent activates a Spell Card that targets a
/// monster you control"), a Summon about to happen, an attack and its target. A condition becomes the block's
/// details only when every part of it is one the block checks the same way; the parts the block doesn't have become
/// IF blocks, or the trigger stays as script.
/// </summary>
public static partial class ScriptTranslator
{
    /// <summary>
    /// The negation checks a chain condition made itself ("the activation can be negated", "this card isn't destroyed by
    /// battle"). The Negate blocks write those checks too, so after the DO blocks are read, the checks they write must
    /// be the same ones.
    /// </summary>
    private sealed record ChainChecks(bool Negatable, bool Disablable, bool BattleDestroyed, bool BattleMatters);

    [ThreadStatic] private static ChainChecks? _chainChecks;

    /// <summary>The WHEN block: the basic table's, or else one with details read from the condition.</summary>
    private static (BlockInstance When, bool UsedCondition) TriggerOrDetailed(HashSet<string> type, string code, string? range, string? condition, bool alsoSpecial, Functions fns, bool tributed, HashSet<string> props)
    {
        _chainChecks = null;
        _pendingIf = null;
        _pendingAlsoWhen = null;
        try
        {
            return Trigger(type, code, range, condition, alsoSpecial, fns, tributed);
        }
        catch (Untranslatable)
        {
            if (DetailedTrigger(type, code, range, condition, props, fns, alsoSpecial, tributed) is { } detailed) return detailed;
            _chainChecks = null;
            _pendingIf = null;
            _pendingAlsoWhen = null;
            throw;
        }
    }

    /// <summary>More WHEN blocks the effect starts on ("or if a monster is Special Summoned"), from copies of the effect on other events.</summary>
    [ThreadStatic] private static List<BlockInstance>? _pendingAlsoWhen;

    /// <summary>The "or when" blocks the trigger read, once.</summary>
    private static List<BlockInstance> TakePendingAlsoWhen()
    {
        var pending = _pendingAlsoWhen ?? [];
        _pendingAlsoWhen = null;
        return pending;
    }

    /// <summary>The IF blocks the trigger read beside its own details, once.</summary>
    private static List<BlockInstance> TakePendingIf()
    {
        var pending = _pendingIf ?? [];
        _pendingIf = null;
        return pending;
    }

    /// <summary>The WHEN for a trigger the basic table doesn't have, or null.</summary>
    private static (BlockInstance When, bool UsedCondition)? DetailedTrigger(HashSet<string> type, string code, string? range, string? condition, HashSet<string> props, Functions fns,
        bool alsoSpecial, bool tributed)
    {
        try
        {
            // Copies on more events ("Normal or Special Summoned", "or Tributed"): only the basic table and the summon blocks read those.
            if (tributed) return ThisCardWithConditions(type, code, range, condition, props, fns, alsoSpecial, tributed);
            if (alsoSpecial)
            {
                if (CardsDetailOf(type, code, range, condition, props, fns) is { } cards && cards.When.Values.GetValueOrDefault("event") == "normal"
                    && cards.When.Values.GetValueOrDefault("summon_type", "any") == "any")
                {
                    var special = new BlockInstance(cards.When.BlockId) { Values = new(cards.When.Values), Filters = cards.When.Filters.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()) };
                    special.Values["event"] = "special";
                    _pendingAlsoWhen = [special];
                    return cards;
                }
                return ThisCardWithConditions(type, code, range, condition, props, fns, alsoSpecial, tributed);
            }
            return code switch
            {
                "EVENT_CHAINING" => ActivationOf(type, range, condition, props, fns),
                "EVENT_SUMMON" or "EVENT_SPSUMMON" or "EVENT_FLIP_SUMMON" => SummonAttemptOf(type, code, range, condition, fns),
                "EVENT_ATTACK_ANNOUNCE" or "EVENT_BE_BATTLE_TARGET" => AttackOf(type, code, range, condition, props, fns),
                "EVENT_BATTLE_START" or "EVENT_BATTLE_CONFIRM" or "EVENT_PRE_DAMAGE_CALCULATE" or "EVENT_BATTLED" or "EVENT_DAMAGE_STEP_END" => BattleOf(type, code, range, condition, fns),
                "EVENT_BATTLE_DESTROYING" => DestroysByBattleOf(type, condition, fns),
                _ => null,
            } ?? ThisCardDetailOf(type, code, condition, props, fns) ?? CardsDetailOf(type, code, range, condition, props, fns)
              ?? ThisCardWithConditions(type, code, range, condition, props, fns, false, false);
        }
        catch (Untranslatable)
        {
            // A detail that can't be read: the trigger's own reason is the clearer one.
            return null;
        }
    }

    // ---------------------------------------------------------------- reading conditions

    /// <summary>
    /// A condition function's checks, as terms that must all hold: "local tg=Duel.GetChainInfo(...)" put in place,
    /// "if X then return false end" turned into "not X", and "c" written as e:GetHandler(). Null when the function does
    /// anything else.
    /// </summary>
    private static List<string>? ConditionTerms(string? condition, Functions fns)
    {
        if (condition is null) return [];
        // aux.NegateSummonCondition and the like, used as the condition itself.
        if (Regex.IsMatch(condition, @"^aux\.\w+$")) return [condition + "(e,tp,eg,ep,ev,re,r,rp)"];
        string text;
        try { text = string.Join(" ", fns.Get(condition).Body.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal))); }
        catch (Untranslatable) { return null; }
        var locals = new List<KeyValuePair<string, string>> { new("c", "e:GetHandler()") };
        var terms = new List<string>();
        // "The attacker, or the attack target if the attacker is the opponent's": your battling monster.
        text = Regex.Replace(text, @"local (\w+)=Duel\.GetAttacker\(\) if (?:not \1:IsControler\(tp\)|\1:IsControler\(1-tp\)) then \1=Duel\.GetAttackTarget\(\) end",
            "local $1=Duel.GetBattleMonster(tp)");
        text = Regex.Replace(text, @"local (\w+)=Duel\.GetAttacker\(\) local (\w+)=Duel\.GetAttackTarget\(\) if (?:not \1:IsControler\(tp\)|\1:IsControler\(1-tp\)) then \1,\2=\2,\1 end",
            "local $1,$2=Duel.GetBattleMonster(tp)");
        var rest = text.Trim();
        while (rest.Length > 0)
        {
            Match m;
            if ((m = Regex.Match(rest, @"^local (\w+(?:,\w+)*)=(\S+)\s*")).Success)
            {
                var names = m.Groups[1].Value.Split(',');
                var value = Substitute(m.Groups[2].Value, locals);
                if (names.Length == 1)
                {
                    if (names[0] == "c" && value == "e:GetHandler()") { rest = rest[m.Length..]; continue; }
                    locals.Insert(0, new(names[0], value));
                }
                else if (Regex.Match(value, @"^Duel\.GetChainInfo\(ev,(CHAININFO_\w+(?:,CHAININFO_\w+)*)\)$") is { Success: true } info && info.Groups[1].Value.Split(',') is var parts && parts.Length == names.Length)
                {
                    for (int i = 0; i < names.Length; i++) locals.Insert(0, new(names[i], $"Duel.GetChainInfo(ev,{parts[i]})"));
                }
                else if (Regex.Match(value, @"^Duel\.GetBattleMonster\((tp|1-tp)\)$") is { Success: true } bm && names.Length == 2)
                {
                    var other = bm.Groups[1].Value == "tp" ? "1-tp" : "tp";
                    locals.Insert(0, new(names[0], $"Duel.GetBattleMonster({bm.Groups[1].Value})"));
                    locals.Insert(0, new(names[1], $"Duel.GetBattleMonster({other})"));
                }
                else return null;
                rest = rest[m.Length..];
                continue;
            }
            if ((m = Regex.Match(rest, @"^if (.+?) then return(?: false)? end\s*")).Success)
            {
                var cond = Substitute(m.Groups[1].Value, locals);
                terms.AddRange(SplitTopLevelAnd(Negated(cond)).Select(Tidy));
                rest = rest[m.Length..];
                continue;
            }
            if ((m = Regex.Match(rest, @"^return (.+)$")).Success)
            {
                terms.AddRange(SplitTopLevelAnd(Substitute(m.Groups[1].Value, locals)).Select(Tidy));
                rest = "";
                continue;
            }
            return null;
        }
        return terms.Count == 0 ? null : terms;
    }

    /// <summary>A term without its outer brackets and spaces around operators.</summary>
    private static string Tidy(string term)
    {
        term = StripParens(term.Trim());
        return Regex.Replace(term, @"\s*(==|~=|<=|>=)\s*", "$1");
    }

    /// <summary>Splits on " or " outside brackets.</summary>
    private static List<string> SplitTopLevelOr(string expr)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            if (expr[i] == '(') depth++;
            else if (expr[i] == ')') depth--;
            else if (depth == 0 && string.CompareOrdinal(expr, i, " or ", 0, 4) == 0)
            {
                parts.Add(expr[start..i].Trim());
                start = i + 4;
                i += 3;
            }
        }
        parts.Add(expr[start..].Trim());
        return parts;
    }

    /// <summary>"not (a or b)" as "not a and not b"; "not x==y" as "x~=y".</summary>
    private static string Negated(string expr)
    {
        expr = StripParens(expr.Trim());
        var ors = SplitTopLevelOr(expr);
        if (ors.Count > 1) return string.Join(" and ", ors.Select(Negated));
        var ands = SplitTopLevelAnd(expr);
        if (ands.Count > 1) return "(" + string.Join(" or ", ands.Select(Negated)) + ")";
        if (expr.StartsWith("not ", StringComparison.Ordinal)) return StripParens(expr[4..].Trim());
        var cmp = Regex.Match(expr, @"^([^=~<>]+)(==|~=)([^=~<>]+)$");
        if (cmp.Success) return cmp.Groups[1].Value.Trim() + (cmp.Groups[2].Value == "==" ? "~=" : "==") + cmp.Groups[3].Value.Trim();
        return "not " + expr;
    }

    /// <summary>"rp==1-tp", "ep~=tp", "tp~=ep": who did it, as "opponent" or "you"; null for any other term.</summary>
    private static string? PlayerTerm(string term, params string[] players)
    {
        foreach (var p in players)
        {
            if (term == $"{p}==1-tp" || term == $"{p}~=tp" || term == $"tp~={p}" || term == $"1-tp=={p}") return "opponent";
            if (term == $"{p}==tp" || term == $"tp=={p}" || term == $"{p}~=1-tp") return "you";
        }
        return null;
    }

    /// <summary>Terms that aren't the trigger's own, as IF blocks; null when one isn't a condition the blocks have.</summary>
    private static List<BlockInstance>? ConditionBlocks(IEnumerable<string> terms, Functions fns)
    {
        var blocks = new List<BlockInstance>();
        foreach (var term in terms)
        {
            if (term == "aux.exccon(e)") { blocks.Add(Block("if.not_sent_this_turn")); continue; }
            try { blocks.AddRange(ConditionBody("return " + term, fns)); }
            catch (Untranslatable) { return null; }
        }
        return MergeAllAre(blocks);
    }

    /// <summary>The card filter a list of terms on one card says ("rc:IsSetCard(0x38)" with "rc" for the card); null when one isn't a filter term.</summary>
    private static bool FilterTerm(string term, string card, CardFilter f)
    {
        var esc = Regex.Escape(card);
        if (!Regex.IsMatch(term, $@"^(?:not )?{esc}:\w+\(")) return false;
        var plain = Regex.Replace(term, $@"(?<![\w.:]){esc}:", "c:");
        // Its own checks only: nothing else about the duel inside.
        if (plain.Contains(card, StringComparison.Ordinal) && card != "c") return false;
        return TermInto(plain, f);
    }

    /// <summary>
    /// A filter term read into the card picker. False when the picker can't say it, and for the checks the card picker
    /// leaves to the blocks that move cards ("can be added to the hand"): on an event's cards they are checks of their own.
    /// </summary>
    private static bool TermInto(string term, CardFilter f)
    {
        var fresh = new CardFilter();
        try { Term(term, fresh); }
        catch (Untranslatable) { return false; }
        if (System.Text.Json.JsonSerializer.Serialize(fresh) == System.Text.Json.JsonSerializer.Serialize(new CardFilter())) return false;
        Term(term, f);
        return true;
    }

    // ---------------------------------------------------------------- activations (EVENT_CHAINING)

    private const string TargetCards = "Duel.GetChainInfo(ev,CHAININFO_TARGET_CARDS)";
    private const string TriggeringLocation = "Duel.GetChainInfo(ev,CHAININFO_TRIGGERING_LOCATION)";

    /// <summary>
    /// "When your opponent activates a Spell Card that targets a monster you control", "If a "Dragonmaid" monster's
    /// effect is activated in the GY": the activation's details from the condition, the rest as IF blocks.
    /// </summary>
    private static (BlockInstance When, bool UsedCondition)? ActivationOf(HashSet<string> type, string? range, string? condition, HashSet<string> props, Functions fns)
    {
        string mode;
        var optional = type.Contains("EFFECT_TYPE_TRIGGER_O");
        if (type.SetEquals(["EFFECT_TYPE_QUICK_O"])) mode = "quick";
        else if (type.SetEquals(["EFFECT_TYPE_ACTIVATE"])) mode = "activate";
        // "If ...": optional triggers that can't miss their timing; mandatory ones never miss it.
        else if (type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_TRIGGER_O"]) && props.Contains("EFFECT_FLAG_DELAY") || type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_TRIGGER_F"])) mode = "trigger";
        else return null;
        var damageStep = DamageStepOf(props);
        if (damageStep is null) return null;
        var terms = ConditionTerms(condition, fns);
        if (terms is null) return null;

        string? who = null, from = null, targets = null, includes = null;
        bool monster = false, activate = false, notActivate = false, spell = false, trap = false, spellTrap = false, monsterOrCard = false;
        bool negatable = false, disablable = false, battleDestroyed = false, cardTarget = false, targetGuard = false;
        var card = new CardFilter { CardType = FilterCardType.Any, Zones = Model.Zone.None };
        CardFilter? targetCards = null;
        var rest = new List<string>();
        foreach (var raw in terms)
        {
            var term = raw.Replace("eg:GetFirst()", "re:GetHandler()", StringComparison.Ordinal);
            if (PlayerTerm(term, "rp", "ep") is { } p)
            {
                if (who is not null && who != p) return null;
                who = p;
                continue;
            }
            switch (term)
            {
                case "Duel.IsChainNegatable(ev)": negatable = true; continue;
                case "Duel.IsChainDisablable(ev)": disablable = true; continue;
                case "not e:GetHandler():IsStatus(STATUS_BATTLE_DESTROYED)": battleDestroyed = true; continue;
                case "re:IsActiveType(TYPE_MONSTER)": monster = true; continue;
                case "re:IsHasType(EFFECT_TYPE_ACTIVATE)": activate = true; continue;
                case "not re:IsHasType(EFFECT_TYPE_ACTIVATE)": notActivate = true; continue;
                case "re:IsActiveType(TYPE_SPELL)": spell = true; continue;
                case "re:IsActiveType(TYPE_TRAP)": trap = true; continue;
                case "re:IsActiveType(TYPE_SPELL+TYPE_TRAP)" or "re:IsActiveType(TYPE_TRAP+TYPE_SPELL)": spellTrap = true; continue;
                case "re:IsActiveType(TYPE_MONSTER) or re:IsHasType(EFFECT_TYPE_ACTIVATE)" or "re:IsHasType(EFFECT_TYPE_ACTIVATE) or re:IsActiveType(TYPE_MONSTER)": monsterOrCard = true; continue;
                case "re:IsHasProperty(EFFECT_FLAG_CARD_TARGET)": cardTarget = true; continue;
                case TargetCards: targetGuard = true; continue;
                case "re:GetHandler()~=e:GetHandler()" or "e:GetHandler()~=re:GetHandler()": card.ExcludeThisCard = true; continue;
                case "re:IsHasCategory(CATEGORY_SPECIAL_SUMMON)": includes = Once(includes, "special_summon"); continue;
                case "re:IsHasCategory(CATEGORY_DESTROY)": includes = Once(includes, "destroy"); continue;
                case "re:IsHasCategory(CATEGORY_SEARCH) or re:IsHasCategory(CATEGORY_DRAW)" or "re:IsHasCategory(CATEGORY_DRAW) or re:IsHasCategory(CATEGORY_SEARCH)": includes = Once(includes, "search"); continue;
                case "aux.damcon1(e,tp,eg,ep,ev,re,r,rp)": includes = Once(includes, "damage_you"); continue;
                case "aux.damcon1(e,1-tp,eg,ep,ev,re,r,rp)": includes = Once(includes, "damage_opponent"); continue;
            }
            if (TriggeringFrom(term) is { } loc)
            {
                if (from is not null && from != loc) return null;
                from = loc;
                continue;
            }
            if (TargetOf(term, fns) is { } t)
            {
                if (targets is not null) return null;
                (targets, targetCards) = t;
                continue;
            }
            if (FilterTerm(term, "re:GetHandler()", card)) continue;
            // Anything else must not be about the activation: it becomes an IF block.
            if (Regex.IsMatch(term, @"\b(?:re|rp|ep|ev|eg)\b")) return null;
            rest.Add(term);
        }
        if (includes == "")
            return null;

        // What kind of activation: the block's own pairs of checks, nothing else.
        var what = (monster, activate, notActivate, spell, trap, spellTrap, monsterOrCard) switch
        {
            (false, false, false, false, false, false, false) => "any",
            (false, true, false, false, false, false, false) or (false, true, false, false, false, true, false) => "spelltrap_card",
            (false, true, false, true, false, false, false) => "spell_card",
            (false, true, false, false, true, false, false) => "trap_card",
            (true, false, false, false, false, false, false) or (true, false, true, false, false, false, false) => "monster",
            (false, false, false, true, false, false, false) => "spell_any",
            (false, false, false, false, true, false, false) => "trap_any",
            (false, false, false, false, false, true, false) => "spelltrap_any",
            (false, false, true, false, false, false, false) => "effect",
            (false, false, false, false, false, false, true) => "card_or_monster",
            _ => null,
        };
        if (what is null) return null;
        // A check of the targets needs the "targets cards" check and the nil check, as the block writes them.
        if (targets is not null && !cardTarget) return null;
        if (targets is null && (cardTarget || targetGuard)) return null;
        // "rc:IsRace(RACE_DRAGON)": the card picker checks it as a monster.
        var blocks = ConditionBlocks(rest, fns);
        if (blocks is null) return null;

        var when = Block(ActivationTrigger.BlockId).With("mode", mode).With("who", who ?? "either").With("what", what).With("from", from ?? "any")
            .With("card", card).With("targets", targets ?? "none").With("includes", includes ?? "none").With("damage_step", damageStep);
        if (targetCards is not null) when.With("target_cards", targetCards);
        if (mode != "activate") when.With("zone", Zone(range));
        if (mode == "trigger") when.With("optional", optional);
        // A monster on the field is destroyed by battle in the Damage Step: only then can it matter.
        _chainChecks = new ChainChecks(negatable, disablable, battleDestroyed, damageStep != "no" && (range is null || range.Contains("LOCATION_MZONE", StringComparison.Ordinal) || range.Contains("LOCATION_ONFIELD", StringComparison.Ordinal)));
        _pendingIf = blocks;
        return (when, true);
    }

    /// <summary>Sets a detail only once: a second, different one makes it "" (unclear).</summary>
    private static string Once(string? current, string value) => current is null || current == value ? value : "";

    /// <summary>The Damage Step choice for an effect's properties, or null when they say something the blocks don't.</summary>
    private static string? DamageStepOf(HashSet<string> props)
    {
        bool step = props.Contains("EFFECT_FLAG_DAMAGE_STEP"), cal = props.Contains("EFFECT_FLAG_DAMAGE_CAL");
        return (step, cal) switch { (false, false) => "no", (true, true) => "yes", (true, false) => "step", _ => null };
    }

    /// <summary>"loc==LOCATION_HAND", "bit.band(loc,LOCATION_ONFIELD)~=0" on the triggering location: where it was activated.</summary>
    private static string? TriggeringFrom(string term)
    {
        var loc = Regex.Escape(TriggeringLocation);
        string? mask = null;
        Match m;
        // A single place: "==" and "&" say the same, except for the Spell & Trap Zone (a Field Spell is in two).
        if ((m = Regex.Match(term, $@"^{loc}==(LOCATION_(?:HAND|MZONE|GRAVE|REMOVED))$")).Success || (m = Regex.Match(term, $@"^(LOCATION_(?:HAND|MZONE|GRAVE|REMOVED))=={loc}$")).Success)
            mask = m.Groups[1].Value;
        else if ((m = Regex.Match(term, $@"^(?:bit\.band\({loc},\(?([\w+|]+)\)?\)|bit\.band\(\(?([\w+|]+)\)?,{loc}\)|\(?{loc}&\(?([\w+|]+)\)?\)?|\(?\(?([\w+|]+)\)?&{loc}\)?)(?:~=0|>0)$")).Success)
            mask = new[] { m.Groups[1], m.Groups[2], m.Groups[3], m.Groups[4] }.First(g => g.Success).Value;
        if (mask is null) return null;
        var set = mask.Split('+', '|').Select(s => s.Trim()).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        return string.Join("+", set) switch
        {
            "LOCATION_HAND" => "hand",
            "LOCATION_ONFIELD" => "field",
            "LOCATION_MZONE" => "mzone",
            "LOCATION_SZONE" => "szone",
            "LOCATION_GRAVE" => "gy",
            "LOCATION_REMOVED" => "banished",
            "LOCATION_GRAVE+LOCATION_HAND" => "hand_gy",
            "LOCATION_GRAVE+LOCATION_ONFIELD" => "field_gy",
            "LOCATION_GRAVE+LOCATION_REMOVED" => "gy_banished",
            "LOCATION_HAND+LOCATION_ONFIELD" => "hand_field",
            _ => null,
        };
    }

    /// <summary>
    /// "tg:IsContains(c)", "tg:IsExists(s.tfilter,1,nil,tp)" on the activation's targets: what it targets, as the
    /// block's choice (and the cards it picks for "that targets:"). Null for any other term.
    /// </summary>
    private static (string Targets, CardFilter? Cards)? TargetOf(string term, Functions fns)
    {
        var tg = Regex.Escape(TargetCards);
        if (term == $"{TargetCards}:IsContains(e:GetHandler())") return ("this_card", null);
        Match m;
        if (term == $"{TargetCards}:IsExists(Card.IsOnField,1,nil)") return ("on_field", null);
        if ((m = Regex.Match(term, $@"^{tg}:IsExists\(Card\.IsLocation,1,nil,(LOCATION_MZONE|LOCATION_ONFIELD)\)$")).Success)
            return m.Groups[1].Value == "LOCATION_ONFIELD" ? ("on_field", null)
                : ("cards", new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster });
        if (!(m = Regex.Match(term, $@"^{tg}:IsExists\(([\w.]+),1,nil(?:,(tp|e,tp))?\)$")).Success) return null;
        var (pars, body) = fns.Get(m.Groups[1].Value);
        var text = string.Join(" ", body.Select(l => l.Trim()).Where(l => l.Length > 0));
        var r = Regex.Match(text, @"^return (.+)$");
        if (!r.Success) return null;
        // The filter's own parameters: the card, then the player (and maybe the effect) passed in.
        var player = m.Groups[2].Value switch { "tp" => pars.ElementAtOrDefault(1), "e,tp" => pars.ElementAtOrDefault(2), _ => null };
        var f = new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.None, CardType = FilterCardType.Any };
        bool onField = false, mzone = false, controller = false;
        foreach (var raw in SplitTopLevelAnd(r.Groups[1].Value))
        {
            var t = Tidy(raw);
            if (player is not null)
            {
                if (t == $"c:IsControler({player})") { f.Owner = CardOwner.You; controller = true; continue; }
                if (t == $"c:IsControler(1-{player})") { f.Owner = CardOwner.Opponent; controller = true; continue; }
            }
            switch (t)
            {
                case "c:IsOnField()" or "c:IsLocation(LOCATION_ONFIELD)": onField = true; continue;
                case "c:IsLocation(LOCATION_MZONE)": mzone = true; continue;
                case "c:IsLocation(LOCATION_GRAVE)": f.Zones |= Model.Zone.Graveyard; continue;
            }
            if (Regex.IsMatch(t, @"\b(?:tp|e)\b")) return null;
            try { Term(t, f); }
            catch (Untranslatable) { return null; }
        }
        if (mzone) f.Zones = Model.Zone.MonsterZone;
        else if (onField) f.Zones = Model.Zone.Field;
        else if (f.Zones == Model.Zone.None) return null;
        if (!controller && f.Zones != Model.Zone.Graveyard) f.Owner = CardOwner.Either;
        if (!controller && f.Zones == Model.Zone.Graveyard) f.Owner = CardOwner.Either;
        if (f.Zones == Model.Zone.MonsterZone && f.CardType == FilterCardType.Any) f.CardType = FilterCardType.Monster;
        // The block's own simple choices.
        var plain = FilterCompiler.Conditions(f).All(c => c == "c:IsType(TYPE_MONSTER)") && !f.ExcludeThisCard;
        if (plain && f.Owner == CardOwner.You && f.Zones == Model.Zone.Field && f.CardType == FilterCardType.Any) return ("your_card", null);
        if (plain && f.Owner == CardOwner.You && f.Zones == Model.Zone.MonsterZone) return ("your_monster", null);
        if (plain && f.Owner == CardOwner.Either && f.Zones == Model.Zone.Field && f.CardType == FilterCardType.Any) return ("on_field", null);
        return ("cards", f);
    }

    /// <summary>IF blocks a trigger read from its condition beside its own details.</summary>
    [ThreadStatic] private static List<BlockInstance>? _pendingIf;

    /// <summary>
    /// After the DO blocks: the negation checks the blocks write must be the condition's own. "Not destroyed by battle"
    /// only matters for a monster on the field whose effect can be used in the Damage Step.
    /// </summary>
    private static void VerifyChainChecks(EffectEvent effect, CardData card)
    {
        if (_chainChecks is not { } official) return;
        _chainChecks = null;
        var check = PackSerializer.Clone(card);
        check.Id = 100000000;
        var result = ScriptCompiler.Compile(new CardProject { Card = check, Effects = [PackSerializer.Clone(effect)] });
        if (result.HasErrors) throw No("its blocks don't fit together");
        var lua = result.Lua;
        var written = new ChainChecks(lua.Contains("Duel.IsChainNegatable(ev)", StringComparison.Ordinal), lua.Contains("Duel.IsChainDisablable(ev)", StringComparison.Ordinal),
            lua.Contains("STATUS_BATTLE_DESTROYED", StringComparison.Ordinal), official.BattleMatters);
        var battleMatters = official.BattleMatters && card.IsMonster;
        if (written.Negatable != official.Negatable || written.Disablable != official.Disablable || battleMatters && written.BattleDestroyed != official.BattleDestroyed)
            throw No("its check that the activation can be negated isn't the one its negation makes");
    }

    // ---------------------------------------------------------------- Summons about to happen (EVENT_SUMMON, EVENT_SPSUMMON)

    /// <summary>"When your opponent would Special Summon a monster(s)", "When a monster(s) would be Summoned" (Solemn Strike, Black Horn of Heaven).</summary>
    private static (BlockInstance When, bool UsedCondition)? SummonAttemptOf(HashSet<string> type, string code, string? range, string? condition, Functions fns)
    {
        string mode;
        if (type.SetEquals(["EFFECT_TYPE_QUICK_O"])) mode = "quick";
        else if (type.SetEquals(["EFFECT_TYPE_ACTIVATE"])) mode = "activate";
        else return null;
        var terms = ConditionTerms(condition, fns);
        if (terms is null) return null;
        string? who = null, check = null;
        var one = false;
        var cards = new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.None, CardType = FilterCardType.Monster };
        var rest = new List<string>();
        foreach (var term in terms)
        {
            if (PlayerTerm(term, "ep", "rp") is { } p)
            {
                if (who is not null && who != p) return null;
                who = p;
                continue;
            }
            switch (term)
            {
                case "Duel.GetCurrentChain()==0": check = Once(check, "current"); continue;
                case "aux.NegateSummonCondition(e,tp,eg,ep,ev,re,r,rp)" or "aux.NegateSummonCondition()" or "Duel.GetReadyChain()==0": check = Once(check, "ready"); continue;
                case "eg:GetCount()==1" or "#eg==1": one = true; continue;
                case "eg:IsExists(Card.IsSummonPlayer,1,nil,1-tp)": who = Once(who, "opponent"); continue;
                case "eg:IsExists(Card.IsSummonPlayer,1,nil,tp)": who = Once(who, "you"); continue;
            }
            Match m;
            if ((m = Regex.Match(term, @"^eg:IsExists\(([\w.]+),1,nil(?:,(tp|1-tp|e,tp))?\)$")).Success)
            {
                var (pars, body) = fns.Get(m.Groups[1].Value);
                var text = string.Join(" ", body.Select(l => l.Trim()).Where(l => l.Length > 0));
                if (Regex.Match(text, @"^return (.+)$") is not { Success: true } r) return null;
                var player = m.Groups[2].Value switch { "tp" or "1-tp" => pars.ElementAtOrDefault(1), "e,tp" => pars.ElementAtOrDefault(2), _ => null };
                var flip = m.Groups[2].Value == "1-tp";
                foreach (var raw in SplitTopLevelAnd(r.Groups[1].Value))
                {
                    var t = Tidy(raw);
                    if (player is not null && Regex.Match(t, $@"^c:IsSummonPlayer\((1-)?{Regex.Escape(player)}\)$") is { Success: true } sp)
                    {
                        who = Once(who, sp.Groups[1].Success != flip ? "opponent" : "you");
                        continue;
                    }
                    if (Regex.IsMatch(t, @"\b(?:tp|e)\b")) return null;
                    Term(t, cards);
                }
                continue;
            }
            if (Regex.IsMatch(term, @"\b(?:re|rp|ep|ev|eg)\b")) return null;
            rest.Add(term);
        }
        // A Summon is negated only while no chain is building: the block always checks that.
        if (check is null or "" || who == "") return null;
        var blocks = ConditionBlocks(rest, fns);
        if (blocks is null) return null;
        var how = code switch { "EVENT_SUMMON" => "normal", "EVENT_FLIP_SUMMON" => "flip", _ => "special" };
        var when = Block(SummonAttemptTrigger.BlockId).With("mode", mode).With("who", who ?? "either").With("how", how).With("cards", cards)
            .With("count", one ? "one" : "any").With("check", check);
        if (mode == "quick") when.With("zone", Zone(range));
        _pendingIf = blocks;
        return (when, true);
    }

    /// <summary>"Negate the Summon, and if you do, destroy it": an operation that only negates a Summon. Null for any other.</summary>
    private static List<BlockInstance>? SummonNegation(string whole) => whole switch
    {
        "Duel.NegateSummon(eg)" => [Block("do.negate_summon").With("and", "nothing")],
        "Duel.NegateSummon(eg) Duel.Destroy(eg,REASON_EFFECT)" => [Block("do.negate_summon").With("and", "destroy")],
        "Duel.NegateSummon(eg) Duel.Remove(eg,POS_FACEUP,REASON_EFFECT)" => [Block("do.negate_summon").With("and", "banish")],
        _ => null,
    };

    // ---------------------------------------------------------------- attacks (EVENT_ATTACK_ANNOUNCE, EVENT_BE_BATTLE_TARGET)

    /// <summary>
    /// "When an opponent's monster declares an attack targeting a "Blackwing" monster you control", "When an attack is
    /// declared involving your Warrior monster", "When a monster you control is targeted for an attack".
    /// </summary>
    private static (BlockInstance When, bool UsedCondition)? AttackOf(HashSet<string> type, string code, string? range, string? condition, HashSet<string> props, Functions fns)
    {
        string mode;
        var optional = type.Contains("EFFECT_TYPE_TRIGGER_O");
        if (type.SetEquals(["EFFECT_TYPE_QUICK_O"])) mode = "quick";
        else if (type.SetEquals(["EFFECT_TYPE_ACTIVATE"])) mode = "activate";
        // "When ...": the block's trigger can miss its timing, like these.
        else if ((type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_TRIGGER_O"]) && !props.Contains("EFFECT_FLAG_DELAY")) || type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_TRIGGER_F"])) mode = "trigger";
        else return null;
        var terms = ConditionTerms(condition, fns);
        if (terms is null) return null;
        // The attacker is the event's card for an attack declaration; the attack target for "is targeted for an attack".
        var first = code == "EVENT_ATTACK_ANNOUNCE" ? "Duel.GetAttacker()" : "Duel.GetAttackTarget()";
        var attacker = new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster };
        var target = new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster };
        var mine = new CardFilter { Owner = CardOwner.You, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster };
        bool direct = false, hasTarget = false, involving = false;
        var rest = new List<string>();
        foreach (var raw in terms)
        {
            var term = raw.Replace("eg:GetFirst()", first, StringComparison.Ordinal).Replace("Duel.GetAttacker():GetControler()~=tp", "Duel.GetAttacker():IsControler(1-tp)", StringComparison.Ordinal)
                .Replace("Duel.GetAttacker():GetControler()==tp", "Duel.GetAttacker():IsControler(tp)", StringComparison.Ordinal);
            // An attack declaration's player is the turn player, whose monster attacks.
            if (code == "EVENT_ATTACK_ANNOUNCE" && PlayerTerm(term, "ep") is { } ep) term = ep == "opponent" ? "Duel.GetTurnPlayer()~=tp" : "Duel.GetTurnPlayer()==tp";
            switch (term)
            {
                // The attacker is the turn player's monster.
                case "Duel.GetTurnPlayer()~=tp" or "tp~=Duel.GetTurnPlayer()" or "Duel.GetTurnPlayer()==1-tp" or "Duel.GetAttacker():IsControler(1-tp)":
                    if (attacker.Owner == CardOwner.You) return null;
                    attacker.Owner = CardOwner.Opponent;
                    continue;
                case "Duel.GetTurnPlayer()==tp" or "tp==Duel.GetTurnPlayer()" or "Duel.GetAttacker():IsControler(tp)":
                    if (attacker.Owner == CardOwner.Opponent) return null;
                    attacker.Owner = CardOwner.You;
                    continue;
                case "Duel.GetAttackTarget()==nil" or "not Duel.GetAttackTarget()": direct = true; continue;
                case "Duel.GetAttackTarget()~=nil" or "Duel.GetAttackTarget()": hasTarget = true; continue;
                case "Duel.GetAttackTarget():IsControler(tp)": target.Owner = CardOwner.You; hasTarget = true; continue;
                case "Duel.GetAttackTarget():IsControler(1-tp)": target.Owner = CardOwner.Opponent; hasTarget = true; continue;
                case "Duel.GetBattleMonster(tp)" or "Duel.GetBattleMonster(tp)~=nil" or "Duel.GetBattleMonster(tp):IsControler(tp)": involving = true; continue;
                case "Duel.GetAttacker()" or "Duel.GetAttacker()~=nil": continue;
            }
            if (FilterTerm(term, "Duel.GetAttacker()", attacker)) continue;
            if (FilterTerm(term, "Duel.GetAttackTarget()", target)) { hasTarget = true; continue; }
            if (FilterTerm(term, "Duel.GetBattleMonster(tp)", mine)) { involving = true; continue; }
            if (Regex.IsMatch(term, @"\b(?:re|rp|ep|ev|eg)\b|GetAttack(?:er|Target)\(\)|GetBattleMonster")) return null;
            rest.Add(term);
        }
        string how;
        if (involving)
        {
            // Only your battling monster: nothing else about the attack.
            if (direct || hasTarget || FilterCompiler.Conditions(attacker).Any(c => c != "c:IsType(TYPE_MONSTER)") || attacker.Owner != CardOwner.Either || code != "EVENT_ATTACK_ANNOUNCE") return null;
            how = "involving";
        }
        else if (code == "EVENT_BE_BATTLE_TARGET")
        {
            if (direct) return null;
            how = "targeted";
        }
        else how = direct ? (hasTarget ? "" : "direct") : hasTarget ? "targeting" : "any";
        if (how == "") return null;
        var blocks = ConditionBlocks(rest, fns);
        if (blocks is null) return null;
        var when = Block(AttackDeclaredTrigger.BlockId).With("mode", mode).With("how", how).With("attacker", attacker).With("target", target).With("mine", mine);
        if (mode != "activate") when.With("zone", Zone(range));
        if (mode == "trigger") when.With("optional", optional);
        _pendingIf = blocks;
        return (when, true);
    }

    // ---------------------------------------------------------------- this card (SINGLE triggers)

    /// <summary>Events whose "r" and "rp" are the card's own reason and reason player, so either can be read for the other.</summary>
    private static bool ReasonIsTheCards(string code) =>
        code is "EVENT_TO_GRAVE" or "EVENT_DESTROYED" or "EVENT_REMOVE" or "EVENT_LEAVE_FIELD" or "EVENT_TO_DECK" or "EVENT_RELEASE" or "EVENT_DISCARD";

    /// <summary>Utility conditions written out: aux.dogcon is "destroyed by an opponent's card while you controlled it".</summary>
    private static readonly Dictionary<string, string[]> AuxSelfConditions = new()
    {
        ["aux.dogcon(e,tp,eg,ep,ev,re,r,rp)"] = ["c:IsPreviousControler(tp)", "c:IsReason(REASON_DESTROY)", "rp==1-tp"],
        ["aux.dogfcon(e,tp,eg,ep,ev,re,r,rp)"] = ["c:IsPreviousLocation(LOCATION_ONFIELD)", "c:IsPreviousControler(tp)", "c:IsReason(REASON_DESTROY)", "rp==1-tp"],
    };

    /// <summary>
    /// "If this card in your possession is destroyed by your opponent's card effect", "If this face-up card leaves the
    /// field", "If this card is sent to the GY as Link Material": a trigger on this card, read into its details. Any check
    /// that isn't one of them, and isn't about this card or the event, becomes an IF block.
    /// </summary>
    private static (BlockInstance When, bool UsedCondition)? ThisCardDetailOf(HashSet<string> type, string code, string? condition, HashSet<string> props, Functions fns)
    {
        bool optional;
        if (type.SetEquals(["EFFECT_TYPE_SINGLE", "EFFECT_TYPE_TRIGGER_O"])) optional = true;
        else if (type.SetEquals(["EFFECT_TYPE_SINGLE", "EFFECT_TYPE_TRIGGER_F"])) optional = false;
        else return null;
        var happening = ThisCardDetailTrigger.Happenings.FirstOrDefault(h => h.Code == code);
        if (happening is null) return null;
        var raw = ConditionTerms(condition, fns);
        if (raw is null) return null;
        var terms = new List<string>();
        foreach (var t0 in raw)
        {
            var t = Regex.Replace(t0, @"(?<![\w.:])e:GetHandler\(\)", "c");
            if (AuxSelfConditions.TryGetValue(t, out var parts)) { terms.AddRange(parts); continue; }
            terms.Add(t);
        }
        var ownReason = ReasonIsTheCards(code);
        string? from = null, face = null, summoned = null, now = null, material = null;
        string? battleOrOpp = null;
        bool yours = false, notReturned = false, opp = false;
        var reasons = new List<string>();
        var summoner = new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.None, CardType = FilterCardType.Monster };
        var rest = new List<string>();
        foreach (var raw1 in terms)
        {
            var t = raw1;
            Match m;
            // "bit.band(r,REASON_EFFECT)~=0" is the card's own reason for these events.
            if ((ownReason || code == "EVENT_TO_HAND" || code == "EVENT_BATTLE_DESTROYED")
                && ((m = Regex.Match(t, @"^(?:bit\.band\(r,\(?([\w+|]+)\)?\)|\(?r&\(?([\w+|]+)\)?\)?)(?:~=0|>0)$")).Success
                    || (m = Regex.Match(t, @"^(?:bit\.band\(r,(REASON_\w+)\)|\(?r&(REASON_\w+)\)?)==\1\2$")).Success))
            {
                var mask = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
                // A draw adds its own reason to the card, which the event's reason doesn't have.
                if (code == "EVENT_TO_HAND" && mask != "REASON_EFFECT") return null;
                t = $"c:IsReason({mask.Replace('|', '+')})";
            }
            // "bit.band(r,0x41)==0x41": every one of those reasons.
            if ((m = Regex.Match(t, @"^(?:bit\.band\((r|c:GetReason\(\)),(0x[0-9a-fA-F]+)\)|\(?(r|c:GetReason\(\))&(0x[0-9a-fA-F]+)\)?)==(0x[0-9a-fA-F]+)$")).Success
                && (m.Groups[1].Value + m.Groups[3].Value == "c:GetReason()" || ownReason))
            {
                var mask = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[4].Value;
                if (!string.Equals(mask, m.Groups[5].Value, StringComparison.OrdinalIgnoreCase) || ReasonNames(mask) is not { } names) return null;
                reasons.AddRange(names.Select(n => $"c:IsReason({n})"));
                continue;
            }
            if ((ownReason || code == "EVENT_TO_HAND") && PlayerTerm(t, "rp") == "opponent") t = "c:GetReasonPlayer()==1-tp";
            if (t is "c:GetReasonPlayer()==1-tp" or "c:GetReasonPlayer()~=tp") { opp = true; continue; }
            // "destroyed by battle or an opponent's card effect".
            if (SplitTopLevelOr(StripParens(t)) is ["c:IsReason(REASON_BATTLE)", var other])
            {
                var inner = SplitTopLevelAnd(StripParens(other)).Select(Tidy)
                    .Select(x => ownReason && PlayerTerm(x, "rp") == "opponent" || x == "c:GetReasonPlayer()~=tp" ? "c:GetReasonPlayer()==1-tp" : x).ToHashSet();
                if (inner.SetEquals(["c:GetReasonPlayer()==1-tp", "c:IsReason(REASON_EFFECT)"])) { battleOrOpp = Once(battleOrOpp, "battle_or_opponent_effect"); continue; }
                if (inner.SetEquals(["c:GetReasonPlayer()==1-tp", "c:IsReason(REASON_EFFECT)", "c:IsPreviousControler(tp)"])) { battleOrOpp = Once(battleOrOpp, "battle_or_opponent_effect_own"); continue; }
                return null;
            }
            if ((m = Regex.Match(t, @"^(not )?c:IsPreviousLocation\(([\w+|]+)\)$")).Success)
            {
                var key = m.Groups[1].Success ? (m.Groups[2].Value == "LOCATION_ONFIELD" ? "not_field" : null)
                    : ThisCardDetailTrigger.Places.FirstOrDefault(p => p.Lua is { } l && !l.StartsWith("not:", StringComparison.Ordinal) && SameMask(l, m.Groups[2].Value)).Key;
                if (key is null || from is not null) return null;
                from = key;
                continue;
            }
            switch (t)
            {
                case "c:IsPreviousPosition(POS_FACEUP)": face = Once(face, "up"); continue;
                case "c:IsPreviousPosition(POS_FACEDOWN)": face = Once(face, "down"); continue;
                case "c:IsPreviousControler(tp)": yours = true; continue;
                case "not c:IsReason(REASON_RETURN)": notReturned = true; continue;
                case "c:IsLocation(LOCATION_GRAVE)": now = Once(now, "gy"); continue;
                case "c:IsLocation(LOCATION_REMOVED)": now = Once(now, now == "faceup" ? "banished_faceup" : "banished"); continue;
                case "c:IsLocation(LOCATION_HAND)": now = Once(now, "hand"); continue;
                case "c:IsLocation(LOCATION_DECK)": now = Once(now, "deck"); continue;
                case "c:IsLocation(LOCATION_EXTRA)": now = Once(now, now == "faceup" ? "extra_faceup" : "extra"); continue;
                case "c:IsLocation(LOCATION_GRAVE+LOCATION_REMOVED)" or "c:IsLocation(LOCATION_REMOVED+LOCATION_GRAVE)": now = Once(now, "gy_or_banished"); continue;
                // Face-up where it is now, read with the place.
                case "c:IsFaceup()" when code is "EVENT_TO_DECK" or "EVENT_REMOVE" or "EVENT_BE_MATERIAL" or "EVENT_DESTROYED" or "EVENT_TO_GRAVE" or "EVENT_LEAVE_FIELD":
                    now = now switch { null => "faceup", "extra" => "extra_faceup", "banished" => "banished_faceup", _ => "" };
                    continue;
            }
            if ((m = Regex.Match(t, @"^c:IsReason\((REASON_\w+(?:\+REASON_\w+)*)\)$")).Success) { reasons.Add(t); continue; }
            if ((m = Regex.Match(t, @"^c:IsSummonType\(SUMMON_TYPE_(\w+)\)$")).Success)
            {
                var how = m.Groups[1].Value == "ADVANCE" ? "tribute" : m.Groups[1].Value.ToLowerInvariant();
                if (how is not ("special" or "normal" or "tribute" or "fusion" or "synchro" or "xyz" or "link" or "ritual" or "pendulum")) return null;
                summoned = Once(summoned, how);
                continue;
            }
            if (code == "EVENT_BE_MATERIAL" && (m = Regex.Match(t, @"^r==(REASON_\w+)$")).Success)
            {
                var kind = ThisCardDetailTrigger.Materials.FirstOrDefault(k => k.Reason == m.Groups[1].Value).Key;
                if (kind is null) return null;
                material = Once(material, kind);
                continue;
            }
            if (code == "EVENT_BE_MATERIAL" && FilterTerm(t, "c:GetReasonCard()", summoner)) continue;
            // Anything else about this card or the event can't be an IF block.
            if (Regex.IsMatch(t, @"(?<![\w.])(?:c|r|rp|re|eg|ep|ev)\b")) return null;
            rest.Add(t);
        }
        if (face == "" || now is "" or "extra" || summoned == "" || material == "") return null;
        var by = battleOrOpp is null ? Cause(reasons, opp) : reasons.Count == 0 && !opp && battleOrOpp != "" ? battleOrOpp : null;
        if (by is null) return null;
        var blocks = ConditionBlocks(rest, fns);
        if (blocks is null) return null;
        var when = Block(ThisCardDetailTrigger.BlockId).With("event", ThisCardDetailTrigger.Happenings.First(h => h.Code == code).Key)
            .With("from", from ?? "any").With("face", face ?? "any").With("yours", yours).With("summoned", summoned ?? "any")
            .With("by", by).With("now", now ?? "any").With("material", material ?? "any").With("summoner", summoner).With("not_returned", notReturned)
            .With("wording", props.Contains("EFFECT_FLAG_DELAY") ? "if" : "when").With("optional", optional);
        _pendingIf = blocks;
        return (when, true);
    }

    private static readonly (string Name, long Bit)[] ReasonBits =
    [
        ("REASON_DESTROY", 0x1), ("REASON_RELEASE", 0x2), ("REASON_MATERIAL", 0x8), ("REASON_SUMMON", 0x10), ("REASON_BATTLE", 0x20),
        ("REASON_EFFECT", 0x40), ("REASON_COST", 0x80), ("REASON_RULE", 0x400), ("REASON_DISCARD", 0x4000), ("REASON_RETURN", 0x20000),
        ("REASON_FUSION", 0x40000), ("REASON_SYNCHRO", 0x80000), ("REASON_RITUAL", 0x100000), ("REASON_XYZ", 0x200000),
        ("REASON_DRAW", 0x2000000), ("REASON_REVEAL", 0x8000000), ("REASON_LINK", 0x10000000),
    ];

    /// <summary>"0x4040" as REASON_DISCARD and REASON_EFFECT; null when it has a bit the list doesn't name.</summary>
    private static List<string>? ReasonNames(string hex)
    {
        var value = Convert.ToInt64(hex[2..], 16);
        var names = ReasonBits.Where(b => (value & b.Bit) != 0).ToList();
        return names.Aggregate(0L, (a, b) => a | b.Bit) == value ? names.Select(b => b.Name).ToList() : null;
    }

    /// <summary>"LOCATION_HAND+LOCATION_ONFIELD" and "LOCATION_ONFIELD+LOCATION_HAND" are the same places.</summary>
    private static bool SameMask(string a, string b) =>
        a.Split('+', '|').Select(s => s.Trim()).OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(b.Split('+', '|').Select(s => s.Trim()).OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>The block's "because of" choice for the reasons a condition checks; null when they are another mix.</summary>
    private static string? Cause(List<string> reasons, bool opp)
    {
        var set = reasons.Select(r => r.Replace("REASON_EFFECT+REASON_BATTLE", "REASON_BATTLE+REASON_EFFECT", StringComparison.Ordinal)).ToHashSet();
        bool Is(params string[] want) => set.SetEquals(want.Select(w => $"c:IsReason({w})"));
        if (opp)
        {
            if (set.Count == 0) return "opponent";
            if (Is("REASON_EFFECT")) return "opponent_effect";
            if (Is("REASON_DESTROY")) return "opponent_destroy";
            if (Is("REASON_DESTROY", "REASON_EFFECT")) return "opponent_destroy_effect";
            if (Is("REASON_DISCARD", "REASON_EFFECT")) return "opponent_discard_effect";
            return null;
        }
        if (set.Count == 0) return "any";
        if (Is("REASON_BATTLE")) return "battle";
        if (Is("REASON_EFFECT")) return "effect";
        if (Is("REASON_BATTLE+REASON_EFFECT")) return "battle_effect";
        if (Is("REASON_DESTROY")) return "destroy";
        if (Is("REASON_DESTROY", "REASON_BATTLE+REASON_EFFECT")) return "destroy_battle_effect";
        if (Is("REASON_DESTROY", "REASON_EFFECT")) return "destroy_effect";
        if (Is("REASON_COST")) return "cost";
        if (Is("REASON_DISCARD")) return "discard";
        if (Is("REASON_DISCARD", "REASON_EFFECT")) return "discard_effect";
        if (Is("REASON_REVEAL")) return "reveal";
        if (Is("REASON_SUMMON")) return "summon";
        return null;
    }

    // ---------------------------------------------------------------- battles (EVENT_BATTLE_START ... EVENT_DAMAGE_STEP_END)

    /// <summary>
    /// "During damage calculation, if your "roid" monster battles", "If this card attacks a Defense Position monster,
    /// before damage calculation", "At the end of the Damage Step, if this card battled an opponent's monster".
    /// </summary>
    private static (BlockInstance When, bool UsedCondition)? BattleOf(HashSet<string> type, string code, string? range, string? condition, Functions fns)
    {
        string mode;
        bool single = false, optional = type.Contains("EFFECT_TYPE_TRIGGER_O");
        if (type.SetEquals(["EFFECT_TYPE_QUICK_O"])) mode = "quick";
        else if (type.SetEquals(["EFFECT_TYPE_ACTIVATE"])) mode = "activate";
        else if (type.SetEquals(["EFFECT_TYPE_SINGLE", "EFFECT_TYPE_TRIGGER_O"]) || type.SetEquals(["EFFECT_TYPE_SINGLE", "EFFECT_TYPE_TRIGGER_F"])) { mode = "trigger"; single = true; }
        else if (type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_TRIGGER_O"]) || type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_TRIGGER_F"])) mode = "trigger";
        else return null;
        var window = code switch
        {
            "EVENT_BATTLE_START" => "start",
            "EVENT_BATTLE_CONFIRM" => "before",
            "EVENT_BATTLED" => "after",
            "EVENT_DAMAGE_STEP_END" => "end",
            _ => "calc",
        };
        var raw = ConditionTerms(condition, fns);
        if (raw is null) return null;
        var terms = raw.Select(t => Regex.Replace(t, @"(?<![\w.:])e:GetHandler\(\)", "c")).ToList();
        // Whose battle: this card's (its own event, or checked), your monster's, or anyone's.
        string? who = single ? "this_card" : null;
        var left = new List<string>();
        foreach (var t in terms)
        {
            switch (t)
            {
                case "Duel.GetAttacker()==c" or "c==Duel.GetAttacker()": who = Once(who == "this_card" && single ? null : who, "attacks"); continue;
                case "Duel.GetAttackTarget()==c" or "c==Duel.GetAttackTarget()": who = Once(who == "this_card" && single ? null : who, "attacked"); continue;
                case "c==Duel.GetAttacker() or c==Duel.GetAttackTarget()" or "Duel.GetAttacker()==c or Duel.GetAttackTarget()==c": who = Once(who, "this_card"); continue;
                case "Duel.GetBattleDamage(tp)>0": who = Once(who, "damage_you"); continue;
                case "Duel.GetBattleMonster(tp)" or "Duel.GetBattleMonster(tp)~=nil": who = Once(who, "yours"); continue;
            }
            left.Add(t);
        }
        if (who == "") return null;
        // "if this card battles a monster": its battle target, read from anywhere.
        if (who is null && left.Any(t => t.Contains("c:GetBattleTarget()", StringComparison.Ordinal))) who = "this_card";
        // The battling monster and the one it battles, as the block names them.
        var self = who switch { "yours" => "Duel.GetBattleMonster(tp)", "this_card" or "attacks" or "attacked" => "c", _ => null };
        var other = who switch { "yours" => "Duel.GetBattleMonster(1-tp)", "this_card" or "attacks" or "attacked" => "c:GetBattleTarget()", _ => null };
        var mine = new CardFilter { Owner = CardOwner.You, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster };
        var vs = new CardFilter { Owner = CardOwner.Either, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster };
        string? versus = null, relate = null;
        var rest = new List<string>();
        foreach (var t0 in left)
        {
            var t = t0;
            // Its battle target is the attack target when it attacks, and the attacker when it is attacked.
            if (who == "attacks") t = t.Replace("Duel.GetAttackTarget()", "c:GetBattleTarget()", StringComparison.Ordinal);
            if (who == "attacked") t = t.Replace("Duel.GetAttacker()", "c:GetBattleTarget()", StringComparison.Ordinal);
            if (self is not null && (t == $"{self}:IsRelateToBattle()")) { relate = Once(relate, "battle"); continue; }
            if (self == "c" && t == "aux.dsercon(e,tp,eg,ep,ev,re,r,rp)") { relate = Once(relate, "battle_or_destroyed"); continue; }
            if (other is not null && (t == other || t == $"{other}~=nil")) { versus ??= "monster"; continue; }
            if (self is not null && t == $"{self}:IsStatus(STATUS_OPPO_BATTLE)" && window is "after" or "end") { versus = Once(versus is "monster" ? null : versus, "opponent"); continue; }
            if (other is not null && window is not ("after" or "end") && t == $"{other}:IsControler(1-tp)") { vs.Owner = CardOwner.Opponent; versus = versus is null or "monster" ? "cards" : versus; continue; }
            if (other is not null && FilterTerm(t, other, vs)) { versus = versus is null or "monster" ? "cards" : versus; continue; }
            if (who == "yours" && FilterTerm(t, "Duel.GetBattleMonster(tp)", mine)) continue;
            if (Regex.IsMatch(t, @"(?<![\w.])(?:c|r|rp|re|eg|ep|ev)\b|GetAttack(?:er|Target)\(\)|GetBattleMonster|GetBattleTarget")) return null;
            rest.Add(t);
        }
        if (versus == "" || relate == "") return null;
        // "an opponent's monster" said with the controller check alone.
        if (versus == "cards" && vs.Owner == CardOwner.Opponent && FilterCompiler.Conditions(vs).All(c => c == "c:IsType(TYPE_MONSTER)")) versus = "opponent";
        var blocks = ConditionBlocks(rest, fns);
        if (blocks is null) return null;
        var when = Block(BattleWindowTrigger.BlockId).With("mode", mode).With("window", window).With("who", who ?? "any").With("mine", mine)
            .With("versus", versus ?? "any").With("vs", vs).With("relate", relate ?? "any");
        if (mode != "activate" && !single) when.With("zone", Zone(range));
        if (mode == "trigger") when.With("optional", optional);
        // Only an effect on this card itself is its own event; the block makes the other kinds watch from a zone.
        if (single != (mode == "trigger" && who is "this_card" or "attacks" or "attacked")) return null;
        _pendingIf = blocks;
        return (when, true);
    }

    /// <summary>"If this card destroys a monster by battle (and sends it to the GY)": the checks the scripts use, as the block's choice.</summary>
    private static (BlockInstance When, bool UsedCondition)? DestroysByBattleOf(HashSet<string> type, string? condition, Functions fns)
    {
        bool optional;
        if (type.SetEquals(["EFFECT_TYPE_SINGLE", "EFFECT_TYPE_TRIGGER_O"])) optional = true;
        else if (type.SetEquals(["EFFECT_TYPE_SINGLE", "EFFECT_TYPE_TRIGGER_F"])) optional = false;
        else return null;
        var raw = ConditionTerms(condition, fns);
        if (raw is null) return null;
        bool relate = false, opp = false, monster = false, gy = false;
        var rest = new List<string>();
        foreach (var t0 in raw)
        {
            var t = Regex.Replace(t0, @"(?<![\w.:])e:GetHandler\(\)", "c");
            switch (t)
            {
                case "aux.bdcon(e,tp,eg,ep,ev,re,r,rp)": relate = true; continue;
                case "aux.bdocon(e,tp,eg,ep,ev,re,r,rp)": relate = opp = true; continue;
                case "aux.bdgcon(e,tp,eg,ep,ev,re,r,rp)": relate = monster = gy = true; continue;
                case "aux.bdogcon(e,tp,eg,ep,ev,re,r,rp)": relate = opp = monster = gy = true; continue;
                case "c:IsRelateToBattle()": relate = true; continue;
                case "c:IsStatus(STATUS_OPPO_BATTLE)": opp = true; continue;
                case "c:GetBattleTarget():IsType(TYPE_MONSTER)": monster = true; continue;
                case "c:GetBattleTarget():IsLocation(LOCATION_GRAVE)": gy = true; continue;
                // The monster it destroyed exists: always so when this card destroys one.
                case "c:GetBattleTarget()": continue;
            }
            if (Regex.IsMatch(t, @"(?<![\w.])(?:c|r|rp|re|eg|ep|ev)\b|GetBattleTarget")) return null;
            rest.Add(t);
        }
        // The block's choices: each is exactly one of the official checks.
        string? versus = (relate, opp, monster, gy) switch
        {
            (true, true, false, false) => "opponent",
            (true, true, true, true) => "opponent",
            (true, false, false, false) => "any",
            (true, false, true, false) => "monster",
            (true, false, true, true) => "any",
            (false, false, false, false) => "unchecked",
            (false, false, true, true) => "unchecked",
            _ => null,
        };
        if (versus is null) return null;
        var blocks = ConditionBlocks(rest, fns);
        if (blocks is null) return null;
        _pendingIf = blocks;
        return (Block("when.destroys_by_battle").With("versus", versus).With("sent", gy ? "gy" : "any").With("optional", optional), true);
    }

    /// <summary>
    /// "If this card is targeted for an attack, while it is in Attack Position": a trigger on this card the basic table
    /// reads without a condition, with its condition as IF blocks. Only when the block can't miss its timing exactly
    /// when the script can't.
    /// </summary>
    private static (BlockInstance When, bool UsedCondition)? ThisCardWithConditions(HashSet<string> type, string code, string? range, string? condition, HashSet<string> props, Functions fns,
        bool alsoSpecial, bool tributed)
    {
        if (condition is null || !(type.SetEquals(["EFFECT_TYPE_SINGLE", "EFFECT_TYPE_TRIGGER_O"]) || type.SetEquals(["EFFECT_TYPE_SINGLE", "EFFECT_TYPE_TRIGGER_F"]))) return null;
        BlockInstance basic;
        try { basic = Trigger(type, code, range, null, alsoSpecial, fns, tributed).When; }
        catch (Untranslatable) { return null; }
        var terms = ConditionTerms(condition, fns);
        if (terms is null) return null;
        var blocks = ConditionBlocks(terms, fns);
        if (blocks is null) return null;
        if (type.Contains("EFFECT_TYPE_TRIGGER_O"))
        {
            var check = new CardData { Id = 100000000, Name = "T", Level = 4 };
            var lua = ScriptCompiler.Compile(new CardProject { Card = check, Effects = [new EffectEvent { When = basic, Do = [Block("do.draw")] }] }).Lua;
            if (lua.Contains("EFFECT_FLAG_DELAY", StringComparison.Ordinal) != props.Contains("EFFECT_FLAG_DELAY")) return null;
        }
        _pendingIf = blocks;
        return (basic, true);
    }

    // ---------------------------------------------------------------- matching cards (FIELD triggers on eg)

    /// <summary>Events with one card only, where "eg:GetFirst()" is the same as "one of them".</summary>
    private static bool OneCardEvent(string code) => code is "EVENT_SUMMON_SUCCESS" or "EVENT_FLIP_SUMMON_SUCCESS";

    /// <summary>
    /// "If a "Blue-Eyes" monster(s) you control is destroyed by battle or card effect", "If your opponent Special Summons
    /// a monster(s) from the Extra Deck": a trigger on the event's cards, read into the block's details. Checks that
    /// aren't about the cards or the event become IF blocks.
    /// </summary>
    private static (BlockInstance When, bool UsedCondition)? CardsDetailOf(HashSet<string> type, string code, string? range, string? condition, HashSet<string> props, Functions fns)
    {
        bool optional;
        if (type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_TRIGGER_O"])) optional = true;
        else if (type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_TRIGGER_F"])) optional = false;
        else return null;
        var happening = CardsDetailTrigger.Happenings.FirstOrDefault(h => h.Code == code);
        if (happening is null) return null;
        var terms = ConditionTerms(condition, fns);
        if (terms is null) return null;
        var d = new CardsDetail();
        string? player = null, other = null;
        var notThis = false;
        var one = false;
        var sawCards = false;
        var rest = new List<string>();
        foreach (var t in terms.Select(x => Regex.Replace(x, @"(?<![\w.:])e:GetHandler\(\)", "c")))
        {
            Match m;
            if (t is "not eg:IsContains(c)") { notThis = true; continue; }
            if (t is "eg:GetCount()==1" or "#eg==1") { one = true; continue; }
            if (PlayerTerm(t, "rp") is { } rp && code != "EVENT_SUMMON_SUCCESS" && code != "EVENT_FLIP_SUMMON_SUCCESS" && code != "EVENT_SPSUMMON_SUCCESS")
            {
                player = Once(player, rp);
                continue;
            }
            // The Summoning player of a Normal or Flip Summon.
            if (OneCardEvent(code) && PlayerTerm(t, "ep") is { } ep) { if (!d.Whose("summoner", ep == "you" ? CardOwner.You : CardOwner.Opponent)) return null; continue; }
            if ((m = Regex.Match(t, @"^eg:IsExists\(([\w.]+),1,(nil|c)(?:,(.+))?\)$")).Success)
            {
                if (sawCards) return null;
                sawCards = true;
                if (m.Groups[2].Value == "c") other = "other";
                if (!d.Read(m.Groups[1].Value, m.Groups[3].Success ? SplitArgs(m.Groups[3].Value) : [], happening, fns)) return null;
                continue;
            }
            // A Normal or Flip Summon has one monster: "eg:GetFirst()" is that monster.
            if (OneCardEvent(code) && t.Contains("eg:GetFirst()", StringComparison.Ordinal))
            {
                if (t is "eg:GetFirst()~=c" or "c~=eg:GetFirst()") { notThis = true; continue; }
                var on = t.Replace("eg:GetFirst()", "EC", StringComparison.Ordinal);
                if (Regex.IsMatch(on, @"(?<![\w.])c")) return null;
                on = on.Replace("EC", "c", StringComparison.Ordinal);
                if (on == "c") continue;
                if (!d.Term(on, happening)) return null;
                sawCards = true;
                continue;
            }
            if (Regex.IsMatch(t, @"(?<![\w.])(?:c|r|rp|re|eg|ep|ev)\b")) return null;
            rest.Add(t);
        }
        var blocks = ConditionBlocks(rest, fns);
        if (blocks is null) return null;
        var filter = d.Filter;
        var otherChoice = (other, notThis) switch { ("other", true) => "other_not_this", ("other", false) => "other", (_, true) => "not_this", _ => "any" };
        var when = Block(CardsDetailTrigger.BlockId).With("cards", filter).With("whose", d.WhoseKey ?? "control").With("event", happening.Key)
            .With("summon_type", d.SummonType ?? "any").With("summon_from", d.SummonFrom ?? "any").With("from", d.From ?? "any").With("face", d.Face ?? "any")
            .With("by", d.By() ?? "any").With("player", player ?? "any").With("now", d.Now ?? "any").With("other", otherChoice).With("count", one ? "one" : "any").With("previous", d.Previous)
            .With("zone", Zone(range)).With("wording", props.Contains("EFFECT_FLAG_DELAY") ? "if" : "when").With("optional", optional);
        if (d.By() is null || player == "" || DamageStepOf(props) is not { } damageStep) return null;
        when.With("damage_step", damageStep);
        _pendingIf = blocks;
        return (when, true);
    }

    /// <summary>What a filter on the event's cards says, read term by term into the block's details.</summary>
    private sealed class CardsDetail
    {
        public CardFilter Filter { get; } = new() { Owner = CardOwner.Either, Zones = Model.Zone.None, CardType = FilterCardType.Any };
        public string? WhoseKey, SummonType, SummonFrom, From, Face, Now;
        /// <summary>The picker's details were read from what the cards were on the field; or from what they are now.</summary>
        public bool Previous, Current;

        /// <summary>"c:GetPreviousRaceOnField()&amp;RACE_MACHINE~=0" as "c:IsRace(RACE_MACHINE)", for the cards as they were on the field; null for any other term.</summary>
        private static string? PreviousTerm(string t)
        {
            Match m;
            if ((m = Regex.Match(t, @"^c:IsPreviousSetCard\((0x[0-9a-fA-F]+(?:,0x[0-9a-fA-F]+)?)\)$")).Success) return $"c:IsSetCard({m.Groups[1].Value})";
            if ((m = Regex.Match(t, @"^(?:bit\.band\(c:GetPrevious(Race|Attribute|Type)OnField\(\),(\w+)\)|\(?c:GetPrevious(Race|Attribute|Type)OnField\(\)&(\w+)\)?)(?:~=0|>0)$")).Success)
            {
                var what = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[3].Value;
                var value = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[4].Value;
                return $"c:Is{what}({value})";
            }
            if ((m = Regex.Match(t, @"^c:GetPreviousCodeOnField\(\)==(\d+)$")).Success) return $"c:IsCode({m.Groups[1].Value})";
            if ((m = Regex.Match(t, @"^c:GetPrevious(Level|Attack|Defense)OnField\(\)(>=|<=)(\d+)$")).Success)
                return $"c:Is{m.Groups[1].Value}{(m.Groups[2].Value == ">=" ? "Above" : "Below")}({m.Groups[3].Value})";
            return null;
        }
        private readonly List<string> _reasons = [];
        private bool _opp;

        public bool Whose(string key, CardOwner owner)
        {
            // "c:IsControler(tp) and c:IsPreviousControler(tp)": the same player both times.
            if (WhoseKey is "now" or "before" && key is "now" or "before" && WhoseKey != key && Filter.Owner == owner) { WhoseKey = "both"; return true; }
            if (WhoseKey == "both" && key is "now" or "before" && Filter.Owner == owner) return true;
            if (WhoseKey is not null && (WhoseKey != key || Filter.Owner != owner)) return false;
            WhoseKey = key;
            Filter.Owner = owner;
            return true;
        }

        private bool _battleOrOpp;

        public string? By() => _battleOrOpp ? (_reasons.Count == 0 && !_opp ? "battle_or_opponent_effect" : null) : ThisCardDetailCause(_reasons, _opp);

        /// <summary>The filter function the event's cards are checked with, its extra arguments put in place.</summary>
        public bool Read(string reference, List<string> args, CardsDetailTrigger.Happening happening, Functions fns)
        {
            // "Card.IsControler,1,nil,1-tp": one check with its argument.
            if (Regex.Match(reference, @"^Card\.(\w+)$") is { Success: true } card)
                return Term($"c:{card.Groups[1].Value}({string.Join(",", args)})", happening);
            if (reference == "aux.TRUE") return true;
            string[] pars;
            List<string> body;
            try { (pars, body) = fns.Get(reference); }
            catch (Untranslatable) { return false; }
            var text = string.Join(" ", body.Select(l => l.Trim()).Where(l => l.Length > 0));
            if (Regex.Match(text, @"^return (.+)$") is not { Success: true } r) return false;
            var expr = r.Groups[1].Value;
            // The filter's own names for what is passed in: "function s.cfilter(c,tp)" called with 1-tp.
            if (pars.Length - 1 < args.Count) return false;
            for (int i = 0; i < args.Count; i++)
            {
                var value = args[i] switch { "tp" or "1-tp" => args[i], _ => null };
                if (value is null) return false;
                expr = Regex.Replace(expr, $@"(?<![\w.:]){Regex.Escape(pars[i + 1])}(?![\w(])", value == "1-tp" ? "OPP" : "TP");
            }
            expr = expr.Replace("1-OPP", "TP", StringComparison.Ordinal).Replace("1-TP", "OPP", StringComparison.Ordinal);
            // Parameters it has but isn't given are nil: any use of them is something else.
            foreach (var p in pars.Skip(1 + args.Count))
                if (Regex.IsMatch(expr, $@"(?<![\w.:]){Regex.Escape(p)}(?![\w(])")) return false;
            expr = expr.Replace("OPP", "1-tp", StringComparison.Ordinal).Replace("TP", "tp", StringComparison.Ordinal);
            if (expr.Contains(" or ", StringComparison.Ordinal) && SplitTopLevelOr(expr).Count > 1) return false;
            foreach (var t in SplitTopLevelAnd(expr))
                if (!Term(Tidy(t), happening)) return false;
            return true;
        }

        public bool Term(string t, CardsDetailTrigger.Happening happening)
        {
            Match m;
            if ((m = Regex.Match(t, @"^c:(IsControler|IsPreviousControler|IsSummonPlayer)\((tp|1-tp)\)$")).Success)
            {
                var key = m.Groups[1].Value switch { "IsControler" => "now", "IsPreviousControler" => "before", _ => "summoner" };
                return Whose(key, m.Groups[2].Value == "tp" ? CardOwner.You : CardOwner.Opponent);
            }
            if ((m = Regex.Match(t, @"^(not )?c:IsPreviousLocation\(([\w+|]+)\)$")).Success)
            {
                var key = m.Groups[1].Success ? (m.Groups[2].Value == "LOCATION_ONFIELD" ? "not_field" : null)
                    : ThisCardDetailTrigger.Places.FirstOrDefault(p => p.Lua is { } l && !l.StartsWith("not:", StringComparison.Ordinal) && SameMask(l, m.Groups[2].Value)).Key;
                if (key is null || From is not null && From != key) return false;
                From = key;
                return true;
            }
            switch (t)
            {
                case "c:IsPreviousPosition(POS_FACEUP)": Face = Once(Face, "up"); return Face != "";
                case "c:IsPreviousPosition(POS_FACEDOWN)": Face = Once(Face, "down"); return Face != "";
                case "c:IsLocation(LOCATION_GRAVE)": Now = Once(Now, "gy"); return Now != "";
                case "c:IsLocation(LOCATION_REMOVED)": Now = Once(Now, "banished"); return Now != "";
                case "c:IsLocation(LOCATION_HAND)": Now = Once(Now, "hand"); return Now != "";
                case "c:IsLocation(LOCATION_DECK)": Now = Once(Now, "deck"); return Now != "";
                case "c:GetReasonPlayer()==1-tp" or "c:GetReasonPlayer()~=tp": _opp = true; return true;
                // "a monster" for events only monsters have.
                case "c:IsType(TYPE_MONSTER)" or "c:IsLocation(LOCATION_MZONE)" when happening.Key is "special" or "normal" or "flip" or "battle_destroyed":
                    Filter.CardType = FilterCardType.Monster;
                    return true;
                case "c:IsPreviousLocation(LOCATION_MZONE)" when From is null: From = "mzone"; return true;
            }
            if ((m = Regex.Match(t, @"^c:IsReason\((REASON_\w+(?:\+REASON_\w+)*)\)$")).Success) { _reasons.Add(t); return true; }
            // "destroyed by battle or an opponent's card effect".
            if (SplitTopLevelOr(StripParens(t)) is ["c:IsReason(REASON_BATTLE)", var other])
            {
                var inner = SplitTopLevelAnd(StripParens(other)).Select(Tidy).Select(x => x == "c:GetReasonPlayer()~=tp" ? "c:GetReasonPlayer()==1-tp" : x).ToHashSet();
                if (!inner.SetEquals(["c:GetReasonPlayer()==1-tp", "c:IsReason(REASON_EFFECT)"]) || _battleOrOpp) return false;
                _battleOrOpp = true;
                return true;
            }
            if (happening.Key is "special" or "normal" or "flip")
            {
                if ((m = Regex.Match(t, @"^c:IsSummonType\(SUMMON_TYPE_(\w+)\)$")).Success)
                {
                    var how = m.Groups[1].Value == "ADVANCE" ? "tribute" : m.Groups[1].Value.ToLowerInvariant();
                    if (how is not ("ritual" or "fusion" or "synchro" or "xyz" or "pendulum" or "link" or "tribute") || SummonType is not null) return false;
                    SummonType = how;
                    return true;
                }
                if ((m = Regex.Match(t, @"^c:IsSummonLocation\(([\w+|]+)\)$")).Success)
                {
                    var key = CardsDetailTrigger.SummonFrom.FirstOrDefault(s => s.Lua is { } l && SameMask(l, m.Groups[1].Value)).Key;
                    if (key is null || SummonFrom is not null) return false;
                    SummonFrom = key;
                    return true;
                }
            }
            // What they were on the field: the picker's details, checked as they were there.
            if (PreviousTerm(t) is { } was)
            {
                if (Current || !TermInto(was, Filter)) return false;
                Previous = true;
                return true;
            }
            // "Except its own name" is checked as the card is now in the block too.
            if (Regex.IsMatch(t, @"^c:Is(?:SetCard|Race|Attribute|Code|Type|Level\w*|Rank\w*|Attack\w*|Defense\w*)\(|^not c:Is(?:SetCard|Race|Attribute|Type)\(")) Current = true;
            if (Previous && Current) return false;
            // What the cards are: the card picker. Their state before they moved can't be read as their state now.
            if (t.Contains("Previous", StringComparison.Ordinal) || Regex.IsMatch(t, @"\b(?:tp|e|r|rp|re|eg)\b") && !Regex.IsMatch(t, @"^c:Is\w+\([\w+]+\)$")) return false;
            if (Regex.IsMatch(t, @"(?<![\w.:])(?!c:)\w+:")) return false;
            return TermInto(t, Filter);
        }
    }

    private static string? ThisCardDetailCause(List<string> reasons, bool opp) => Cause(reasons, opp);
}
