using System.Globalization;
using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Amounts that change with the duel: "gains 300 ATK for each card in your hand", "loses ATK equal to its original ATK",
/// "inflict 200 damage for each monster you control". A Lua expression is read as one of the value blocks' sources
/// (<see cref="ValueSources"/>) only when it computes exactly the same number at the same moment.
/// </summary>
public static partial class ScriptTranslator
{
    /// <summary>
    /// A live value as the value blocks hold it; <see cref="Negative"/> for "loses" / "*-300". <see cref="Counter"/> is
    /// the counter a count of counters counts.
    /// </summary>
    private sealed record LiveValue(string Source, string How, int Amount, CardFilter? Cards, bool Negative, int? Counter = null)
    {
        public BlockInstance On(BlockInstance b)
        {
            b.With("value", Source).With("how", How);
            if (How == "per") b.With("amount", Amount);
            if (Cards is not null) b.With(ValueSources.CardsKey, Cards);
            if (ValueSources.UsesCounter(Source)) b.WithCounter(Counter);
            return b;
        }
    }

    /// <summary>What the names in a value expression mean where it is written.</summary>
    private sealed class ValueScope
    {
        /// <summary>The expressions for "you": the controller of this card, or the player of the effect.</summary>
        public HashSet<string> You { get; init; } = [];
        /// <summary>Expressions for the opponent beside "1-you", like the target player "p" of an effect aimed at them.</summary>
        public HashSet<string> Opp { get; init; } = [];
        /// <summary>The card "its" means (the monster gaining the ATK), or null when there is none.</summary>
        public string? Its { get; init; }
        /// <summary>The expressions for this card.</summary>
        public HashSet<string> Handler { get; init; } = [];
        /// <summary>Whether "ev" is the amount of damage or LP that started the effect.</summary>
        public bool Event { get; init; }

        public bool IsIts(string card) =>
            Its is not null && (card == Its || Handler.Contains(Its) && Handler.Contains(card));
    }

    /// <summary>The value sources "for each" reads naturally with: a number of things, not an amount of LP or ATK.</summary>
    private static bool Countable(string source) => source is "count" or "different_attr" or "different_race" or "different_name"
        or "its_level" or "its_rank" or "its_link" or "its_materials" or "its_equips" or "its_linked" or "its_used" or "its_counters" or "sum_counters";

    /// <summary>Sources whose value is above 0 exactly when the cards they look at exist.</summary>
    private static bool CountsCards(string source) => source is "count" or "different_attr" or "different_race" or "different_name";

    private static string Squash(string s) => Regex.Replace(s, @"\s+", "");

    /// <summary>"((a))" as "a"; "(a)*(b)" stays.</summary>
    private static string StripParens(string s)
    {
        while (s.Length >= 2 && s[0] == '(' && Closing(s, 0) == s.Length - 1) s = s[1..^1];
        return s;
    }

    /// <summary>The index of the bracket closing the one at <paramref name="open"/>, or -1.</summary>
    private static int Closing(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Splits at <paramref name="op"/> outside brackets.</summary>
    private static List<string> SplitTop(string s, char op)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == op && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }

    /// <summary>A name, a call or a chain of calls: safe to put in place of a local without brackets.</summary>
    private static bool IsAtomic(string s)
    {
        int depth = 0;
        foreach (var ch in s)
        {
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (depth == 0 && "+-*/%<>=~#, ".Contains(ch)) return false;
        }
        return true;
    }

    /// <summary>Puts the locals' expressions in place of their names.</summary>
    private static string Substitute(string expr, IEnumerable<KeyValuePair<string, string>> locals)
    {
        foreach (var (name, value) in locals)
        {
            var with = IsAtomic(value) ? value : "(" + value + ")";
            expr = Regex.Replace(expr, $@"(?<![\w.:]){Regex.Escape(name)}(?![\w(])", _ => with);
        }
        return expr;
    }

    private static int? Literal(string s)
    {
        s = StripParens(s);
        return Regex.IsMatch(s, @"^-?\d{1,6}$") ? int.Parse(s, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// The expression a value function returns, with its locals put in place: "local tp=c:GetControler() return ..."
    /// Null when the function does anything else (branches, loops, assignments).
    /// </summary>
    private static string? ReturnedExpression(List<string> body)
    {
        var lines = body.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)).ToList();
        var locals = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < lines.Count; i++)
        {
            Match m;
            if (i == lines.Count - 1)
                return (m = Regex.Match(lines[i], @"^return (.+)$")).Success ? Squash(Substitute(m.Groups[1].Value, locals)) : null;
            if (!(m = Regex.Match(lines[i], @"^local (\w+)\s*=\s*(.+)$")).Success || m.Groups[2].Value.Contains(" or ", StringComparison.Ordinal)
                || m.Groups[2].Value.Contains(" and ", StringComparison.Ordinal))
                return null;
            locals.Add(new(m.Groups[1].Value, Squash(Substitute(m.Groups[2].Value, locals))));
        }
        return null;
    }

    /// <summary>0 for "you", 1 for the opponent, null for anything else.</summary>
    private static int? Side(string player, ValueScope s)
    {
        player = StripParens(player);
        if (s.You.Contains(player)) return 0;
        if (s.Opp.Contains(player)) return 1;
        if (player.StartsWith("1-", StringComparison.Ordinal) && s.You.Contains(StripParens(player[2..]))) return 1;
        if (player.StartsWith("1-", StringComparison.Ordinal) && s.Opp.Contains(StripParens(player[2..]))) return 0;
        return null;
    }

    /// <summary>
    /// The cards a Duel.GetMatchingGroup / GetFieldGroup call looks at, as a picker; null when the call can't be one
    /// (it looks from a fixed player at different zones on each side, or leaves out a card that isn't this one).
    /// </summary>
    private static CardFilter? GroupFilter(string? fn, string player, string mine, string theirs, string? except, List<string> extra, ValueScope s, Functions fns)
    {
        CardFilter f;
        if (fn is null) f = new CardFilter { CardType = FilterCardType.Any };
        else if (extra.Count > 0 && Regex.IsMatch(fn, @"^Card\.\w+$"))
        {
            f = new CardFilter { CardType = FilterCardType.Any };
            Term($"c:{fn[5..]}({string.Join(",", extra)})", f);
        }
        else f = Filter(fn, fns);
        if (except is not null && except != "nil")
        {
            if (!s.Handler.Contains(except)) return null;
            f.ExcludeThisCard = true;
        }
        switch (Side(player, s))
        {
            case 0: Locate(f, mine, theirs); break;
            case 1: Locate(f, theirs, mine); break;
            // Duel.GetMatchingGroupCount(f,0,LOCATION_MZONE,LOCATION_MZONE,nil): both fields, whoever "0" is.
            default:
                if (player is not ("0" or "1") || mine != theirs) return null;
                Locate(f, mine, theirs);
                break;
        }
        if (fn is null && f.Zones == Model.Zone.MonsterZone) f.CardType = FilterCardType.Monster;
        return f;
    }

    /// <summary>A group expression: Duel.GetMatchingGroup(...) or Duel.GetFieldGroup(...), as its picker.</summary>
    private static CardFilter? GroupOf(string call, string args, ValueScope s, Functions fns)
    {
        var a = SplitArgs(args);
        return call switch
        {
            "GetMatchingGroup" or "GetMatchingGroupCount" when a.Count >= 5 => GroupFilter(a[0], a[1], a[2], a[3], a[4], a.Skip(5).ToList(), s, fns),
            "GetFieldGroup" or "GetFieldGroupCount" when a.Count == 3 => GroupFilter(null, a[0], a[1], a[2], null, [], s, fns),
            _ => null,
        };
    }

    /// <summary>The value an expression without its multiplier stands for: a source and the cards it reads.</summary>
    private static (string Source, CardFilter? Cards)? ValueBase(string x, ValueScope s, Functions fns)
    {
        Match m;
        if (x == "ev") return s.Event ? ("event", null) : null;
        // The monster's own numbers. (Counters are read by CounterBase.)
        if ((m = Regex.Match(x, @"^(.+):(GetLevel|GetRank|GetLink|GetOverlayCount|GetEquipCount|GetLinkedGroupCount|GetMaterialCount|GetBaseAttack|GetBaseDefense)\(\)$")).Success
            && s.IsIts(StripParens(m.Groups[1].Value)))
            return (m.Groups[2].Value switch
            {
                "GetLevel" => "its_level",
                "GetRank" => "its_rank",
                "GetLink" => "its_link",
                "GetOverlayCount" => "its_materials",
                "GetEquipCount" => "its_equips",
                "GetLinkedGroupCount" => "its_linked",
                "GetMaterialCount" => "its_used",
                "GetBaseAttack" => "its_orig_atk",
                _ => "its_orig_def",
            }, null);
        // Original ATK is never below 0 on the field: math.max(0,c:GetBaseAttack()) is the same.
        if ((m = Regex.Match(x, @"^math\.max\((?:0,(.+)|(.+),0)\)$")).Success)
        {
            var inner = StripParens(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            if (ValueBase(inner, s, fns) is { Source: "its_orig_atk" or "its_orig_def" } orig) return orig;
            var d = SplitTop(inner, '-');
            if (d.Count == 2 && LpOf(d[0], s) is { } a && LpOf(d[1], s) is { } b && a != b)
                return (a == 1 ? "lp_below" : "lp_above", null);
            return null;
        }
        if ((m = Regex.Match(x, @"^math\.abs\((.+)\)$")).Success)
        {
            var d = SplitTop(StripParens(m.Groups[1].Value), '-');
            if (d.Count == 2 && LpOf(d[0], s) is { } a && LpOf(d[1], s) is { } b && a != b) return ("lp_diff", null);
            // math.abs(Duel.GetLP(0)-Duel.GetLP(1)): the same for both players.
            if (d.Count == 2 && Regex.Match(StripParens(d[0]), @"^Duel\.GetLP\(([01])\)$") is { Success: true } p0
                && Regex.Match(StripParens(d[1]), @"^Duel\.GetLP\(([01])\)$") is { Success: true } p1 && p0.Groups[1].Value != p1.Groups[1].Value)
                return ("lp_diff", null);
            return null;
        }
        if (LpOf(x, s) is { } lp) return (lp == 0 ? "your_lp" : "opp_lp", null);

        // Counts, kinds and totals of a group of cards.
        var hash = x.StartsWith('#');
        var body = hash ? x[1..] : x;
        string call, args, rest;
        if ((m = Regex.Match(body, @"^Duel\.(GetMatchingGroupCount|GetMatchingGroup|GetFieldGroupCount|GetFieldGroup)\(")).Success)
        {
            var open = m.Length - 1;
            var close = Closing(body, open);
            if (close < 0) return null;
            call = m.Groups[1].Value;
            args = body[(open + 1)..close];
            rest = body[(close + 1)..];
        }
        else return null;
        var counting = call.EndsWith("Count", StringComparison.Ordinal);
        if (counting && (hash || rest.Length > 0)) return null;
        string? source = counting ? "count" : (hash ? (rest.Length == 0 ? "count" : null) : rest switch
        {
            ":GetCount()" => "count",
            ":GetClassCount(Card.GetCode)" => "different_name",
            ":GetClassCount(Card.GetAttribute)" => "different_attr",
            ":GetClassCount(Card.GetRace)" => "different_race",
            ":GetSum(Card.GetAttack)" => "sum_atk",
            ":GetSum(Card.GetDefense)" => "sum_def",
            ":GetSum(Card.GetLevel)" => "sum_level",
            _ => null,
        });
        if (source is null) return null;
        return GroupOf(call, args, s, fns) is { } cards ? (source, cards) : null;
    }

    /// <summary>Duel.GetLP of you (0) or your opponent (1).</summary>
    private static int? LpOf(string x, ValueScope s) =>
        Regex.Match(StripParens(x), @"^Duel\.GetLP\((.+)\)$") is { Success: true } m ? Side(m.Groups[1].Value, s) : null;

    /// <summary>
    /// A value expression as a live value: "c:GetLevel()*300", "Duel.GetMatchingGroupCount(...)*-200",
    /// "math.floor(Duel.GetLP(tp)/2)", "math.abs(Duel.GetLP(0)-Duel.GetLP(1))". Null when it isn't one exactly.
    /// </summary>
    private static LiveValue? ValueOf(string expr, ValueScope s, Functions fns)
    {
        var x = StripParens(Squash(expr));
        var neg = false;
        if (x.StartsWith('-')) { neg = true; x = StripParens(x[1..]); }
        var factors = SplitTop(x, '*');
        int amount = 1;
        string baseExpr;
        if (factors.Count == 1) baseExpr = x;
        else if (factors.Count == 2 && Literal(factors[1]) is { } n1) { amount = n1; baseExpr = factors[0]; }
        else if (factors.Count == 2 && Literal(factors[0]) is { } n2) { amount = n2; baseExpr = factors[1]; }
        else return null;
        if (amount < 0) { neg = !neg; amount = -amount; }
        if (amount is 0 or > 99999) return null;
        baseExpr = StripParens(baseExpr);
        if (baseExpr.StartsWith('-')) { neg = !neg; baseExpr = StripParens(baseExpr[1..]); }
        var how = "equal";
        if (Regex.Match(baseExpr, @"^math\.floor\((.+)\)$") is { Success: true } fm && Closing(baseExpr, "math.floor".Length) == baseExpr.Length - 1
            && SplitTop(StripParens(fm.Groups[1].Value), '/') is [var half, "2"])
        {
            how = "half";
            baseExpr = StripParens(half);
        }
        int? counter = null;
        if ((CounterBase(baseExpr, s, fns, out counter) ?? ValueBase(baseExpr, s, fns)) is not { } b) return null;
        if (amount == 1) return new LiveValue(b.Source, how, 0, b.Cards, neg, counter);
        if (how == "half") return null;
        if (amount == 2 && !Countable(b.Source)) return new LiveValue(b.Source, "double", 0, b.Cards, neg, counter);
        return Countable(b.Source) ? new LiveValue(b.Source, "per", amount, b.Cards, neg, counter) : null;
    }

    /// <summary>The stat and direction an effect code changes: ("atk", "gain") for EFFECT_UPDATE_ATTACK, ("level", "becomes")...</summary>
    private static (string Stat, string Dir)? StatOf(string code) => code switch
    {
        "EFFECT_UPDATE_ATTACK" => ("atk", "gain"),
        "EFFECT_UPDATE_DEFENSE" => ("def", "gain"),
        "EFFECT_UPDATE_LEVEL" => ("level", "gain"),
        "EFFECT_SET_ATTACK_FINAL" => ("atk", "becomes"),
        "EFFECT_SET_DEFENSE_FINAL" => ("def", "becomes"),
        "EFFECT_CHANGE_LEVEL" => ("level", "becomes"),
        _ => null,
    };

    private static CardFilter ThisCardMonster() => new() { ThisCard = true, Owner = CardOwner.You, Zones = Model.Zone.MonsterZone, CardType = FilterCardType.Monster };

    /// <summary>
    /// "This card gains 300 ATK for each ...", "monsters you control gain ATK equal to ...": a lasting EFFECT_TYPE_SINGLE
    /// or field effect whose value function returns a live value. Null when it isn't one.
    /// </summary>
    private static BlockInstance? LiveStatsPassive(HashSet<string> type, string code, Dictionary<string, string> set, string? range, HashSet<string> props, Functions fns)
    {
        var value = set.GetValueOrDefault("Value");
        if (range is null || value is null || StatOf(code) is not { } sd) return null;
        // The block's effect on this card works only where the card is (EFFECT_FLAG_SINGLE_RANGE); without that flag it would work everywhere.
        var single = type.SetEquals(["EFFECT_TYPE_SINGLE"]) && props.Contains("EFFECT_FLAG_SINGLE_RANGE");
        // A field effect with flags (EFFECT_FLAG_SET_AVAILABLE, EFFECT_FLAG_IGNORE_IMMUNE...) reaches cards the block's doesn't.
        var field = type.SetEquals(["EFFECT_TYPE_FIELD"]) && set.ContainsKey("TargetRange") && props.Count == 0;
        if (!single && !field || !Regex.IsMatch(value.Trim(), $@"^{Regex.Escape(fns.Prefix)}\.\w+$")) return null;
        var (pars, body) = fns.Get(value);
        var param = pars.Length > 1 && pars[1].Length > 0 ? pars[1] : null;
        if (ReturnedExpression(body) is not { } expr) return null;
        // In a field effect the value function's card is the monster it applies to; in a single one, this card.
        var scope = single
            ? new ValueScope
            {
                You = ["e:GetHandlerPlayer()", "e:GetHandler():GetControler()", .. param is null ? Array.Empty<string>() : [$"{param}:GetControler()"]],
                Its = "e:GetHandler()",
                Handler = ["e:GetHandler()", .. param is null ? Array.Empty<string>() : [param]],
            }
            : new ValueScope
            {
                // Only your own monsters ("LOCATION_MZONE,0"): the controller of the monster is you.
                You = ["e:GetHandlerPlayer()", "e:GetHandler():GetControler()",
                    .. param is not null && Regex.IsMatch(Squash(set["TargetRange"]), @"^LOCATION_\w+,0$") ? [$"{param}:GetControler()"] : Array.Empty<string>()],
                Its = param,
                Handler = ["e:GetHandler()"],
            };
        if (ValueOf(expr, scope, fns) is not { } live) return null;
        if (sd.Dir == "becomes" && live.Negative) return null;
        var block = live.On(Block(StatsFromValuePassive.BlockId).With("stat", sd.Stat).With("dir", sd.Dir == "becomes" ? "becomes" : live.Negative ? "lose" : "gain"));
        return block.With("cards", single ? ThisCardMonster() : FieldTargets(set["TargetRange"], set.GetValueOrDefault("Target"), fns));
    }

    // ---------------------------------------------------------------- values in operations

    /// <summary>What an operation has worked out so far, for the steps that use it.</summary>
    private sealed class OperationValues
    {
        public required Functions Fns { get; init; }
        public required TargetInfo Target { get; init; }
        /// <summary>Locals holding groups or numbers: their expressions, and how many DO blocks came before them.</summary>
        public List<KeyValuePair<string, string>> Locals { get; } = [];
        public Dictionary<string, int> Stamps { get; } = [];
        /// <summary>DO blocks so far: a value is only the same when no step came between working it out and using it.</summary>
        public int Blocks { get; set; }
        /// <summary>Whether a check on this card or the target ("if c:IsRelateToEffect(e) then") came before.</summary>
        public bool Guarded { get; set; }
    }

    [ThreadStatic] private static OperationValues? _opValues;
    /// <summary>The event of the effect being read, for "that amount" (ev).</summary>
    [ThreadStatic] private static string? _eventCode;

    private static bool AmountEvent => _eventCode is "EVENT_DAMAGE" or "EVENT_BATTLE_DAMAGE" or "EVENT_RECOVER" or "EVENT_PAY_LPCOST";

    /// <summary>"local ct=Duel.GetMatchingGroupCount(...)": remembers a group or number the next steps read. True when it is one.</summary>
    private static bool RecordValueLocal(string line, OperationValues ov)
    {
        var m = Regex.Match(line, @"^local (\w+)\s*=\s*(.+)$");
        if (!m.Success) return false;
        var name = m.Groups[1].Value;
        var used = ov.Locals.Where(l => Regex.IsMatch(m.Groups[2].Value, $@"(?<![\w.:]){Regex.Escape(l.Key)}(?![\w(])")).ToList();
        var expr = Squash(Substitute(m.Groups[2].Value, ov.Locals));
        var isQuery = Regex.IsMatch(expr, @"^#?Duel\.(?:GetMatchingGroup|GetFieldGroup)\(.*\)$") && !expr.Contains(":Get", StringComparison.Ordinal);
        if (!isQuery && OperationValue(expr, "c", ov) is null && OperationValue(expr, "tc", ov) is null) return false;
        var stamp = used.Select(l => ov.Stamps[l.Key]).DefaultIfEmpty(ov.Blocks).Min();
        ov.Locals.RemoveAll(l => l.Key == name);
        ov.Locals.Add(new(name, expr));
        ov.Stamps[name] = Math.Min(stamp, ov.Blocks);
        return !isQuery;
    }

    /// <summary>A value in an operation, with "its" meaning <paramref name="its"/>; null when it isn't one.</summary>
    private static LiveValue? OperationValue(string expr, string? its, OperationValues ov)
    {
        // "local p=Duel.GetChainInfo(0,CHAININFO_TARGET_PLAYER)": the player the target step named.
        var scope = new ValueScope
        {
            You = ["tp", .. ov.Target.Player == "tp" ? ["p"] : Array.Empty<string>()],
            Opp = ov.Target.Player == "1-tp" ? ["p"] : [],
            Its = its,
            Handler = ["c", "e:GetHandler()"],
            Event = AmountEvent,
        };
        try { return ValueOf(expr, scope, ov.Fns); }
        catch (Untranslatable) { return null; }
    }

    /// <summary>An expression with the operation's locals put in place, if they were worked out right before it is used.</summary>
    private static string? Fresh(string expr, OperationValues ov)
    {
        var used = ov.Locals.Where(l => Regex.IsMatch(expr, $@"(?<![\w.:]){Regex.Escape(l.Key)}(?![\w(])")).ToList();
        if (used.Any(l => ov.Stamps[l.Key] != ov.Blocks)) return null;
        return Squash(Substitute(expr, ov.Locals));
    }

    /// <summary>
    /// "if ct>0 and c:IsFaceup() and c:IsRelateToEffect(e) then" around a step that only gains ATK or inflicts damage
    /// equal to ct: gaining 0 ATK or inflicting 0 damage does nothing, so the "ct>0" can go. The line without it, or
    /// the line itself.
    /// </summary>
    private static string WithoutZeroCheck(string line, List<string> body, int index, List<TempEffect> temps, OperationValues ov)
    {
        var m = Regex.Match(line, @"^if (.+) then$");
        if (!m.Success || line.EndsWith(" then return end", StringComparison.Ordinal)) return line;
        var terms = SplitTopLevelAnd(m.Groups[1].Value).Select(t => t.Trim()).ToList();
        var numbers = ov.Locals.Where(l => !Regex.IsMatch(l.Value, @"^Duel\.(?:GetMatchingGroup|GetFieldGroup)\(.*\)$")).Select(l => l.Key).ToHashSet();
        var removed = new HashSet<string>();
        var keep = new List<string>();
        foreach (var t in terms)
        {
            if (Regex.Match(t, @"^(\w+)(?:>0|~=0|>=1)$") is { Success: true } z && numbers.Contains(z.Groups[1].Value)) removed.Add(z.Groups[1].Value);
            else keep.Add(t);
        }
        if (removed.Count == 0) return line;
        bool Uses(string text) => removed.Any(r => Regex.IsMatch(text, $@"(?<![\w.:]){Regex.Escape(r)}(?![\w(])"));
        // Only steps that do nothing with 0 (gain 0 ATK, inflict 0 damage) and use that number, up to the matching "end".
        int depth = 1, steps = 0;
        for (int i = index + 1; i < body.Count; i++)
        {
            var l = body[i].Trim();
            if (l == "end" && --depth == 0) break;
            if (l.Length == 0 || l.StartsWith("Duel.Hint", StringComparison.Ordinal)) continue;
            if (l.StartsWith("@TEMP ", StringComparison.Ordinal) && temps[Int(l[6..])].Parts is var parts
                && parts.All(p => p.Set.GetValueOrDefault("Code") is "EFFECT_UPDATE_ATTACK" or "EFFECT_UPDATE_DEFENSE" or "EFFECT_UPDATE_LEVEL" && Uses(p.Set.GetValueOrDefault("Value") ?? "")))
            { steps++; continue; }
            if (Regex.IsMatch(l, @"^Duel\.(?:Damage|Recover)\(") && Uses(l)) { steps++; continue; }
            return line;
        }
        if (steps == 0) return line;
        return keep.Count == 0 ? "" : $"if {string.Join(" and ", keep)} then";
    }

    /// <summary>
    /// "Inflict damage equal to ..." / "gain LP equal to ...": Duel.Damage or Duel.Recover with a live amount. Null when
    /// the amount isn't one; untranslatable when it is but the block would act differently.
    /// </summary>
    private static BlockInstance? LifeValue(string kind, string who, string amount, OperationValues ov)
    {
        var target = ov.Target;
        LiveValue? live;
        var snapshot = false;
        if (amount == "d" && target.ParamLive is { } kept)
        {
            // The amount worked out when the effect was activated (Duel.SetTargetParam(ct*300)).
            live = kept;
            snapshot = true;
        }
        else if (amount == "d")
        {
            if (!target.ParamEvent) return null;
            live = AmountEvent ? new LiveValue("event", "equal", 0, null, false) : null;
        }
        else
        {
            if (Fresh(amount, ov) is not { } expr) throw No("its amount is worked out before another step changes it");
            live = OperationValue(expr, null, ov);
        }
        if (live is null) return null;
        if (live.Negative) return null;
        if (ov.Guarded) throw No("it only changes LP while a card is still there, which the block doesn't check");
        // Whether it can be activated when the amount is 0: the script asks "above 0", or it doesn't.
        var zeroOk = live.Source != "event" && !(target.Check is { } check && SplitTopLevelAnd(check).Any(t => AmountAbove0(live, t, ov.Fns)));
        return live.On(Block(LifeFromValueAction.BlockId).With("what", kind == "Damage" ? "damage" : "gain").With("who", who))
            .With("when", snapshot ? "activate" : "resolve").With("zero_ok", zeroOk);
    }

    /// <summary>The live value a value block holds.</summary>
    private static LiveValue ValueIn(BlockInstance b) =>
        new(b.Values.GetValueOrDefault("value") ?? "", b.Values.GetValueOrDefault("how") ?? "equal",
            int.TryParse(b.Values.GetValueOrDefault("amount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
            b.Filters.GetValueOrDefault(ValueSources.CardsKey), false, Counters.IdOf(b));

    /// <summary>
    /// What the target step asks before the effect can be activated ("if chk==0 then return ... end"), for an effect
    /// with value blocks: each check must be one the blocks make themselves (the target exists, the amount is above 0)
    /// or one an IF block says ("if you have a Spell in your GY"). The IF blocks it needs.
    /// </summary>
    private static List<BlockInstance> ValueActivationChecks(EffectEvent effect, TargetInfo target, bool hasTarget, Functions fns)
    {
        var gates = new List<BlockInstance>();
        if (!hasTarget) return gates;
        if (target.Check is not { } check) throw No("its activation check is written in a way the blocks can't follow");
        var amounts = effect.Do.Where(b => b.BlockId == LifeFromValueAction.BlockId && b.Values.GetValueOrDefault("value") != "event").Select(ValueIn).ToList();
        var selfStats = effect.Do.Any(b => b.BlockId == StatsFromValueAction.BlockId && b.Filters.GetValueOrDefault("cards")?.ThisCard == true);
        foreach (var raw in SplitTopLevelAnd(check))
        {
            var term = Squash(raw);
            if (term == "true") continue;
            if (target.Filter is not null && IsTargetCheck(term, target, fns)) continue;
            if (selfStats && term is "e:GetHandler():IsFaceup()" or "c:IsFaceup()") continue;
            if (amounts.Any(a => AmountAbove0(a, term, fns))) continue;
            // "All face-up monsters you control gain ...": the block asks itself that there is one.
            if (effect.Do.Any(b => b.BlockId == StatsFromValueAction.BlockId && b.Values.GetValueOrDefault("mode") == "all" && IsExistsCheck(term, b.Filters["cards"], fns))) continue;
            if (effect.Do.All(b => b.BlockId is StatsFromValueAction.BlockId or LifeFromValueAction.BlockId) && (ExistsCheck(raw.Trim(), fns) ?? CountAbove0(term, fns)) is { } gate)
            {
                gates.Add(gate);
                continue;
            }
            throw No($"its activation asks something the blocks don't ({Short(raw.Trim())})");
        }
        return gates;
    }

    /// <summary>"Duel.GetFieldGroupCount(tp,LOCATION_HAND,0)>0" as "if you have a card in your hand"; null for other checks.</summary>
    private static BlockInstance? CountAbove0(string term, Functions fns)
    {
        if (Regex.Match(term, @"^(.+)(?:>0|~=0|>=1)$") is not { Success: true } m) return null;
        var scope = new ValueScope { You = ["tp"], Handler = ["c", "e:GetHandler()"] };
        LiveValue? v;
        try { v = ValueOf(m.Groups[1].Value, scope, fns); } catch (Untranslatable) { return null; }
        return v is { Source: "count", How: "equal" or "per", Negative: false, Cards: { } cards } ? Block("if.cards_exist").With("count", 1).With("cards", cards) : null;
    }

    /// <summary>"Duel.IsExistingTarget(f,tp,LOCATION_MZONE,0,1,nil)": the check the targeting block makes itself.</summary>
    private static bool IsTargetCheck(string term, TargetInfo target, Functions fns)
    {
        if (Regex.Match(term, @"^Duel\.IsExistingTarget\((.*)\)$") is not { Success: true } m || SplitArgs(m.Groups[1].Value) is not { Count: >= 6 } a) return false;
        if (a[4] != target.Count.ToString(CultureInfo.InvariantCulture) || a.Skip(6).Any(x => x is not ("e" or "tp"))) return false;
        var scope = new ValueScope { You = ["tp"], Handler = ["c", "e:GetHandler()"] };
        try
        {
            return GroupFilter(a[0], a[1], a[2], a[3], a[5], [], scope, fns) is { } f && SamePicker(f, target.Filter!);
        }
        catch (Untranslatable) { return false; }
    }

    /// <summary>"Duel.IsExistingMatchingCard(f,tp,LOCATION_MZONE,0,1,nil)" for exactly these cards.</summary>
    private static bool IsExistsCheck(string term, CardFilter cards, Functions fns)
    {
        if (Regex.Match(term, @"^Duel\.IsExistingMatchingCard\((.*)\)$") is not { Success: true } m || SplitArgs(m.Groups[1].Value) is not { Count: 6 } a || a[4] != "1") return false;
        var scope = new ValueScope { You = ["tp"], Handler = ["c", "e:GetHandler()"] };
        try
        {
            return GroupFilter(a[0], a[1], a[2], a[3], a[5], [], scope, fns) is { } f && SamePicker(f, cards);
        }
        catch (Untranslatable) { return false; }
    }

    /// <summary>Whether one check of the target step is "the amount is above 0".</summary>
    private static bool AmountAbove0(LiveValue live, string term, Functions fns)
    {
        if (live.How == "half") return false;
        term = Squash(term);
        // Duel.IsExistingMatchingCard(f,tp,loc,loc,1,nil): the same as a count of them above 0.
        if (Regex.Match(term, @"^Duel\.IsExistingMatchingCard\((.*)\)$") is { Success: true } ex && SplitArgs(ex.Groups[1].Value) is { Count: >= 6 } a && a[4] == "1")
            term = $"Duel.GetMatchingGroupCount({string.Join(",", a.Take(4).Concat(a.Skip(5)))})>0";
        if (Regex.Match(term, @"^(.+)(?:>0|~=0|>=1)$") is not { Success: true } pos) return false;
        var scope = new ValueScope { You = ["tp"], Its = "e:GetHandler()", Handler = ["c", "e:GetHandler()"] };
        LiveValue? asked;
        try { asked = ValueOf(pos.Groups[1].Value, scope, fns); } catch (Untranslatable) { return false; }
        if (asked is null || asked.Negative || asked.How == "half") return false;
        if (CountsCards(asked.Source) && CountsCards(live.Source)) return SamePicker(asked.Cards!, live.Cards!);
        return asked.Source == live.Source && asked.Counter == live.Counter
            && (asked.Cards is null ? live.Cards is null : live.Cards is not null && SamePicker(asked.Cards, live.Cards));
    }

    private static bool SamePicker(CardFilter a, CardFilter b)
    {
        // Every card in a Monster Zone is a monster: "any card" and "monster" there are the same.
        static List<string> Tests(CardFilter f)
        {
            if (f.Zones == Model.Zone.MonsterZone && f.CardType == FilterCardType.Any) { f = f.Clone(); f.CardType = FilterCardType.Monster; }
            return FilterCompiler.Conditions(f);
        }
        return a.Owner == b.Owner && a.Zones == b.Zones && a.ExcludeThisCard == b.ExcludeThisCard && Tests(a).SequenceEqual(Tests(b));
    }

    /// <summary>
    /// "This card gains ATK equal to ...", "it gains 300 ATK for each ... until the end of this turn": an effect an
    /// operation puts on this card or the target with a live value. Null when it isn't one.
    /// </summary>
    private static BlockInstance? LiveTempStats(TempEffect temp, TargetInfo target, bool targetCard)
    {
        if (_opValues is not { } ov) return null;
        var parts = temp.Parts;
        var holder = parts[0].Holder;
        if (holder is not ("c" or "tc") || parts.Any(p => p.Holder != holder)) return null;
        // Exactly what the block registers. EFFECT_FLAG_CANNOT_DISABLE keeps an effect a card gives itself while it is
        // negated, so it only matters when the target can be this card.
        var maybeSelf = holder == "c" || _sourceCard?.Kind is null or CardKind.Monster && target.Filter is { Owner: not CardOwner.Opponent, ExcludeThisCard: false };
        foreach (var (set, _) in parts)
        {
            if (set.GetValueOrDefault("Type") != "EFFECT_TYPE_SINGLE") return null;
            var props = Flags(set.GetValueOrDefault("Property") ?? "");
            // "Only while on the field": the effect resets when the card leaves anyway.
            if (props.Remove("EFFECT_FLAG_SINGLE_RANGE") && set.GetValueOrDefault("Range") != "LOCATION_MZONE") return null;
            if (!maybeSelf) props.Remove("EFFECT_FLAG_CANNOT_DISABLE");
            if (props.Count > 0 || set.Keys.Any(k => k is not ("Type" or "Code" or "Value" or "Reset" or "Property" or "Range"))
                || set.ContainsKey("Range") && !set.ContainsKey("Property"))
                return null;
        }
        var value = parts[0].Set.GetValueOrDefault("Value");
        if (value is null || Regex.IsMatch(value, @"^-?\d+$") || parts.Any(p => p.Set.GetValueOrDefault("Value") != value)) return null;
        var reset = parts[0].Set.GetValueOrDefault("Reset");
        if (reset is null || parts.Any(p => p.Set.GetValueOrDefault("Reset") != reset)) return null;
        // The block resets like RESETS_STANDARD, and also when this card's own effects are negated (RESET_DISABLE) for
        // what it gives itself; on another monster it doesn't end when that monster is negated.
        var flags = Flags(Squash(reset).Replace("RESETS_STANDARD_DISABLE", "RESETS_STANDARD+RESET_DISABLE", StringComparison.Ordinal));
        if (holder == "c" && !flags.Remove("RESET_DISABLE")) return null;
        string duration;
        if (flags.SetEquals(["RESET_EVENT", "RESETS_STANDARD"])) duration = "always";
        else if (flags.SetEquals(["RESET_EVENT", "RESETS_STANDARD", "RESET_PHASE", "PHASE_END"])) duration = "turn";
        else return null;
        var codes = parts.Select(p => p.Set.GetValueOrDefault("Code") ?? "").ToList();
        string stat, dir;
        if (codes.Count == 1 && StatOf(codes[0]) is { } one) (stat, dir) = one;
        else if (codes.Count == 2 && codes.ToHashSet().SetEquals(["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"])) (stat, dir) = ("both", "gain");
        else if (codes.Count == 2 && codes.ToHashSet().SetEquals(["EFFECT_SET_ATTACK_FINAL", "EFFECT_SET_DEFENSE_FINAL"])) (stat, dir) = ("both", "becomes");
        else return null;
        if (holder == "tc" && (!targetCard || target.Filter is null)) return null;
        if (Fresh(value, ov) is not { } expr) return null;
        if (OperationValue(expr, holder, ov) is not { } live || dir == "becomes" && live.Negative) return null;
        var block = live.On(Block(StatsFromValueAction.BlockId).With("stat", stat).With("dir", dir == "becomes" ? "becomes" : live.Negative ? "lose" : "gain").With("duration", duration));
        return holder == "c"
            ? block.With("cards", ThisCardMonster())
            : block.With("count", target.Count).With("cards", target.Filter!).With("target", true);
    }
}
