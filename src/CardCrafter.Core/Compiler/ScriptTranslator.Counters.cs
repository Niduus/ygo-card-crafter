using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Counters in official scripts: "c:AddCounter(0x1,1)", "RemoveCounter(tp,0x1,3,REASON_COST)", "c:GetCounter(0x1)*300",
/// "return e:GetHandler():GetCounter(0x1)>=3", "c:EnableCounterPermit(0x1)". Each is read as the counter blocks only
/// when they do the same with the same counter.
/// </summary>
public static partial class ScriptTranslator
{
    private const string CounterNumber = @"(0x[0-9a-fA-F]+|\d+)";

    /// <summary>A counter id as scripts write it ("0x1", "4110"); null for 0 (GetCounter(0) counts kinds) or anything else.</summary>
    private static int? CounterOf(string s)
    {
        s = s.Trim();
        int n;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { if (!int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) return null; }
        else if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return null;
        return n is > 0 and <= 0xffff ? n : null;
    }

    /// <summary>The "can have these counters placed on it" checks a card filter makes ("c:IsCanAddCounter(0x1,1)").</summary>
    private static readonly ConditionalWeakTable<CardFilter, List<(int Id, int N)>> Addable = new();

    private static void NoteAddable(CardFilter f, int id, int n) => Addable.GetOrCreateValue(f).Add((id, n));

    /// <summary>Whether the cards were picked by "c:IsCanAddCounter(id,n)": the check the place-counters block makes.</summary>
    private static bool PickedAddable(CardFilter f, int id, int n) => Addable.TryGetValue(f, out var l) && l.Count == 1 && l[0] == (id, n);

    /// <summary>
    /// A filter's counter terms: "c:IsCanAddCounter(0x1,1)" (noted for the block that places them), "c:GetCounter(0x1)>=2"
    /// as a "counters on it" requirement, "c:GetCounter(0)>0" as "has a counter on it". False for other terms.
    /// </summary>
    private static bool CounterTerm(string term, CardFilter f)
    {
        Match m;
        if ((m = Regex.Match(term, $@"^c:IsCanAddCounter\({CounterNumber},(\d+)\)$")).Success && CounterOf(m.Groups[1].Value) is { } able)
        {
            NoteAddable(f, able, Int(m.Groups[2].Value));
            return true;
        }
        if (!(m = Regex.Match(term, @"^c:GetCounter\((0x[0-9a-fA-F]+|\d+)\)(>=|<=|==|~=|>|<)(\d+)$")).Success) return false;
        var n = Int(m.Groups[3].Value);
        var op = m.Groups[2].Value;
        if (m.Groups[1].Value is "0" or "0x0")
        {
            // The number of kinds of counters: only "some" or "none" says the same as a count of counters.
            bool? some = (op, n) switch { (">", 0) or ("~=", 0) or (">=", 1) => true, ("==", 0) or ("<", 1) or ("<=", 0) => false, _ => null };
            if (some is not { } has) return false;
            f.Requirements.Add(new CardRequirement { Property = "counters", Op = has ? RequirementOp.Is : RequirementOp.IsNot });
            return true;
        }
        if (CounterOf(m.Groups[1].Value) is not { } id) return false;
        f.Requirements.Add(new CardRequirement
        {
            Property = CardProperties.CounterCount, Value = n, Values = [id],
            Op = op switch
            {
                ">=" => RequirementOp.GreaterOrEqual, "<=" => RequirementOp.LessOrEqual, "==" => RequirementOp.Equal,
                "~=" => RequirementOp.NotEqual, ">" => RequirementOp.Greater, _ => RequirementOp.Less,
            },
        });
        return true;
    }

    /// <summary>
    /// Counters as a value: "c:GetCounter(0x1)" (on the monster "its" means), "Duel.GetCounter(tp,1,0,0x1)" (on your
    /// field), "Duel.GetMatchingGroup(...):GetSum(Card.GetCounter,0x1)". Null for anything else.
    /// </summary>
    private static (string Source, CardFilter? Cards)? CounterBase(string x, ValueScope s, Functions fns, out int? counter)
    {
        counter = null;
        Match m;
        if ((m = Regex.Match(x, $@"^(.+):GetCounter\({CounterNumber}\)$")).Success && s.IsIts(StripParens(m.Groups[1].Value)) && CounterOf(m.Groups[2].Value) is { } own)
        {
            counter = own;
            return ("its_counters", null);
        }
        // Duel.GetCounter(p,s,o,id): the counters on the cards on p's field (s) and the other player's (o).
        if ((m = Regex.Match(x, $@"^Duel\.GetCounter\((.+),([01]),([01]),{CounterNumber}\)$")).Success && CounterOf(m.Groups[4].Value) is { } field)
        {
            var (mine, theirs) = (m.Groups[2].Value == "1", m.Groups[3].Value == "1");
            if (!mine && !theirs) return null;
            var side = Side(m.Groups[1].Value, s);
            if (side is null && !(mine && theirs && m.Groups[1].Value is "0" or "1")) return null;
            var owner = mine && theirs ? CardOwner.Either : (mine == (side == 0)) ? CardOwner.You : CardOwner.Opponent;
            counter = field;
            return ("sum_counters", new CardFilter { Owner = owner, Zones = Model.Zone.Field, CardType = FilterCardType.Any });
        }
        if ((m = Regex.Match(x, @"^Duel\.(GetMatchingGroup|GetFieldGroup)\(")).Success)
        {
            var open = m.Length - 1;
            var close = Closing(x, open);
            if (close < 0) return null;
            var rest = x[(close + 1)..];
            if (Regex.Match(rest, $@"^:GetSum\(Card\.GetCounter,{CounterNumber}\)$") is not { Success: true } sum || CounterOf(sum.Groups[1].Value) is not { } summed) return null;
            if (GroupOf(m.Groups[1].Value, x[(open + 1)..close], s, fns) is not { } cards) return null;
            counter = summed;
            return ("sum_counters", cards);
        }
        return null;
    }

    /// <summary>
    /// An operation's counter step: "c:AddCounter(0x1,1)" on this card, "tc:AddCounter(0x100e,1)" on the target,
    /// "c:RemoveCounter(tp,0x1,1,REASON_EFFECT)", "Duel.RemoveCounter(tp,1,0,0x1,2,REASON_EFFECT)". Null for others.
    /// </summary>
    private static BlockInstance? CounterAction(string line, TargetInfo target, bool targetCard)
    {
        Match m;
        if ((m = Regex.Match(line, $@"^(c|e:GetHandler\(\)|tc):AddCounter\({CounterNumber},(\d+)\)$")).Success && CounterOf(m.Groups[2].Value) is { } id)
        {
            var n = Int(m.Groups[3].Value);
            if (n is < 1 or > 99) return null;
            var block = Block(PlaceCounterAction.BlockId).With("amount", n).WithCounter(id);
            if (m.Groups[1].Value != "tc") return block.With("cards", new CardFilter { ThisCard = true });
            if (!targetCard || target.Filter is null) return null;
            // The block targets only cards the counters can go on; the script must have asked the same.
            if (!PickedAddable(target.Filter, id, n)) throw No("it places counters on a target it doesn't check can hold them");
            return block.With("count", target.Count).With("cards", target.Filter).With("target", true);
        }
        if ((m = Regex.Match(line, $@"^(?:c|e:GetHandler\(\)):RemoveCounter\(tp,{CounterNumber},(\d+),REASON_EFFECT\)$")).Success && CounterOf(m.Groups[1].Value) is { } own)
            return RemoveCounters(RemoveCounterAction.BlockId, "this", own, m.Groups[2].Value);
        if ((m = Regex.Match(line, $@"^Duel\.RemoveCounter\(tp,1,([01]),{CounterNumber},(\d+),REASON_EFFECT\)$")).Success && CounterOf(m.Groups[2].Value) is { } field)
            return RemoveCounters(RemoveCounterAction.BlockId, m.Groups[1].Value == "1" ? "field" : "yours", field, m.Groups[3].Value);
        return null;
    }

    private static BlockInstance? RemoveCounters(string blockId, string from, int id, string count)
    {
        var n = Int(count);
        return n is < 1 or > 99 ? null : Block(blockId).With("amount", n).WithCounter(id).With("from", from);
    }

    /// <summary>"Remove 3 Spell Counters from this card" and "remove 2 Kaiju Counters from anywhere on the field" as costs.</summary>
    private static BlockInstance? CounterCost(string line)
    {
        Match m;
        if ((m = Regex.Match(line, $@"^(?:c|e:GetHandler\(\)):RemoveCounter\(tp,{CounterNumber},(\d+),REASON_COST\)$")).Success && CounterOf(m.Groups[1].Value) is { } own)
            return RemoveCounters(RemoveCounterCost.BlockId, "this", own, m.Groups[2].Value);
        if ((m = Regex.Match(line, $@"^Duel\.RemoveCounter\(tp,1,([01]),{CounterNumber},(\d+),REASON_COST\)$")).Success && CounterOf(m.Groups[2].Value) is { } field)
            return RemoveCounters(RemoveCounterCost.BlockId, m.Groups[1].Value == "1" ? "field" : "yours", field, m.Groups[3].Value);
        return null;
    }

    /// <summary>
    /// "If this card has 3 or more Spell Counters on it" (return c:GetCounter(0x1)>=3) and "if there are 4 or more Spell
    /// Counters on your field" (return Duel.GetCounter(tp,1,0,0x1)>=4). Null for other conditions.
    /// </summary>
    private static BlockInstance? CounterCondition(string body, Functions fns)
    {
        Match m;
        if ((m = Regex.Match(body, $@"^return (?:c|e:GetHandler\(\)):GetCounter\({CounterNumber}\)(>=|<=|==|~=|>|<)(\d+)$")).Success && CounterOf(m.Groups[1].Value) is { } id)
        {
            var n = Int(m.Groups[3].Value);
            (string Cmp, int N)? how = (m.Groups[2].Value, n) switch
            {
                (">=", _) => ("at_least", n), (">", _) => ("at_least", n + 1), ("==", _) => ("exactly", n), ("<=", _) => ("at_most", n),
                ("<", > 0) => ("at_most", n - 1), ("~=", 0) => ("at_least", 1), _ => null,
            };
            if (how is not { } h || h.N > 99) return null;
            return Block(SelfCountersCondition.BlockId).With("cmp", h.Cmp).With("amount", h.N).WithCounter(id);
        }
        if ((m = Regex.Match(body, @"^return (Duel\.GetCounter\(.+\))(>=|<=|==|~=|>|<)(\d+)$")).Success)
        {
            var scope = new ValueScope { You = ["tp"], Handler = ["c", "e:GetHandler()"] };
            if (CounterBase(m.Groups[1].Value, scope, fns, out var counter) is not { Source: "sum_counters", Cards: { } cards }) return null;
            var op = m.Groups[2].Value switch { ">=" => "ge", "<=" => "le", "==" => "eq", "~=" => "ne", ">" => "gt", _ => "lt" };
            return Block(CompareValuesCondition.BlockId).With("value", "sum_counters").With(ValueSources.CardsKey, cards).WithCounter(counter)
                .With("op", op).With("right", "number").With("amount", Int(m.Groups[3].Value));
        }
        return null;
    }

    /// <summary>
    /// "Duel.SelectTarget(tp,Card.IsCanAddCounter,tp,0,LOCATION_MZONE,1,1,nil,0x100e,1)": a target the counters can go
    /// on. False for other lines.
    /// </summary>
    private static bool CounterTarget(string line, TargetInfo info)
    {
        var m = Regex.Match(line, $@"^(?:(?:local\s+)?\w+\s*=\s*)?Duel\.SelectTarget\(tp,Card\.IsCanAddCounter,tp,([\w+]+),([\w+]+),(\d+),\3,(nil|e:GetHandler\(\)|c),{CounterNumber},(\d+)\)$");
        if (!m.Success || CounterOf(m.Groups[5].Value) is not { } id) return false;
        // Only face-up cards on the field can have counters placed on them.
        var f = new CardFilter { CardType = FilterCardType.Any, FaceUpOnly = true };
        Locate(f, m.Groups[1].Value, m.Groups[2].Value);
        if ((f.Zones & ~Model.Zone.Field) != Model.Zone.None) return false;
        if (f.Zones == Model.Zone.MonsterZone) f.CardType = FilterCardType.Monster;
        f.ExcludeThisCard = m.Groups[4].Value != "nil";
        NoteAddable(f, id, Int(m.Groups[6].Value));
        info.Filter = f;
        info.Count = Int(m.Groups[3].Value);
        return true;
    }

    /// <summary>"c:EnableCounterPermit(0x1)" and "c:SetCounterLimit(0x1,3)": read with the blocks that use this card's counters (<see cref="CounterPermits"/>).</summary>
    private static TranslatedPiece? CounterPermit(string line) =>
        Regex.IsMatch(line, $@"^c:(?:EnableCounterPermit\({CounterNumber}\)|SetCounterLimit\({CounterNumber},\d+\))$") ? new TranslatedPiece { Piece = null!, Ignored = true } : null;

    /// <summary>
    /// The counters this card can hold: the blocks let a card hold the counters they place on it, remove from it or count
    /// on it ("for each Spell Counter on it"), and "(max. 3)" is the place-counters block's. A permit or limit no block
    /// accounts for, or a counter the blocks would let this card hold that the script doesn't, stays as script.
    /// </summary>
    private static void CounterPermits(TranslationResult result)
    {
        var placed = new Dictionary<int, List<BlockInstance>>();
        var held = new Dictionary<int, List<int>>();
        // "Each time ... is activated, place ...": the pieces, and where this card is while they count.
        var accumulators = new List<(int Piece, string? Zone)>();
        for (int i = 0; i < result.Pieces.Count; i++)
            foreach (var e in result.Pieces[i].Effects)
            foreach (var b in e.If.Concat(e.Cost).Concat(e.Do))
            {
                if (b.BlockId == CounterEachTimePassive.BlockId) accumulators.Add((i, e.When?.Values.GetValueOrDefault("zone")));
                int? id = b.BlockId switch
                {
                    PlaceCounterAction.BlockId when b.Filters.GetValueOrDefault("cards")?.ThisCard == true => Counters.IdOf(b),
                    CounterEachTimePassive.BlockId => Counters.IdOf(b),
                    RemoveCounterAction.BlockId or RemoveCounterCost.BlockId when b.Values.GetValueOrDefault("from", "this") == "this" => Counters.IdOf(b),
                    SelfCountersCondition.BlockId => Counters.IdOf(b),
                    "passive.self_stats_per" when b.Values.GetValueOrDefault("per") == "counters" => Counters.IdOf(b),
                    _ => null,
                };
                if (id is not { } n) continue;
                if (!held.TryGetValue(n, out var pieces)) held[n] = pieces = [];
                pieces.Add(i);
                if (b.BlockId is PlaceCounterAction.BlockId or CounterEachTimePassive.BlockId) { if (!placed.TryGetValue(n, out var list)) placed[n] = list = []; list.Add(b); }
            }
        var permitted = new HashSet<int>();
        var marks = new List<string?>();
        for (int i = 0; i < result.Pieces.Count; i++)
        {
            var p = result.Pieces[i];
            if (!p.Ignored || p.Piece is null) continue;
            var line = string.Join(" ", p.Piece.Code.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)));
            Match m;
            string? reason = null;
            if ((m = Regex.Match(line, $@"^c:EnableCounterPermit\({CounterNumber}\)$")).Success && CounterOf(m.Groups[1].Value) is { } id)
            {
                permitted.Add(id);
                if (!held.ContainsKey(id)) reason = $"it lets this card hold {Counters.Name(id)}s, which the blocks only do for a card whose own blocks use them";
            }
            else if ((m = Regex.Match(line, $@"^c:SetCounterLimit\({CounterNumber},(\d+)\)$")).Success && CounterOf(m.Groups[1].Value) is { } lid)
            {
                var max = Int(m.Groups[2].Value);
                if (!placed.TryGetValue(lid, out var blocks) || max is < 1 or > 99) reason = "it limits the counters on this card without placing them itself";
                else foreach (var b in blocks) b.With("max", max);
            }
            else if (line.Contains("SetOperation(aux.chainreg)", StringComparison.Ordinal))
            {
                // The mark an "each time ... is activated" counter reads: one goes with each.
                string? zone;
                try { zone = Regex.Match(line, @"SetRange\(([^)]*)\)") is { Success: true } rm ? Zone(rm.Groups[1].Value) : null; }
                catch (Untranslatable) { zone = null; }
                marks.Add(zone);
                if (zone is null || !accumulators.Any(a => a.Zone == zone))
                    reason = "it marks the activations a counter is placed for, which goes with placing them each time";
            }
            else continue;
            if (reason is not null) result.Pieces[i] = new TranslatedPiece { Piece = p.Piece, Reason = reason };
        }
        // Without the mark, the counter would be placed for activations before this card was face-up.
        foreach (var (i, zone) in accumulators.Where(a => !marks.Contains(a.Zone)).ToList())
            if (result.Pieces[i].Translated)
                result.Pieces[i] = new TranslatedPiece { Piece = result.Pieces[i].Piece, Reason = "it places counters for activations it doesn't mark as they are chained" };
        // A counter this card could not hold in the script, which the blocks would let it hold.
        foreach (var (id, pieces) in held)
            if ((id & 0x1000) == 0 && !permitted.Contains(id))
                foreach (var i in pieces.Distinct())
                    if (result.Pieces[i].Translated)
                        result.Pieces[i] = new TranslatedPiece { Piece = result.Pieces[i].Piece, Reason = $"it uses {Counters.Name(id)}s on this card, which the script doesn't let it hold" };
    }

    /// <summary>
    /// The Spell Counter cards' two Continuous effects: "EVENT_CHAINING aux.chainreg" marks an activation, and
    /// "EVENT_CHAIN_SOLVED" places the counter when a marked one resolves ("if re:IsHasType(EFFECT_TYPE_ACTIVATE) and
    /// re:IsActiveType(TYPE_SPELL) and c:GetFlagEffect(FLAG_ID_CHAINING)>0 then c:AddCounter(0x1,1) end"). The first
    /// needs no block of its own (checked with the second in <see cref="CounterPermits"/>); the second is
    /// passive.counter_each_time. Null for other effects.
    /// </summary>
    private static TranslatedPiece? CounterAccumulator(ScriptPiece piece, EffectEvent effect, HashSet<string> type, string code, Dictionary<string, string> set,
        string? range, HashSet<string> props, Functions fns)
    {
        if (!type.SetEquals(["EFFECT_TYPE_FIELD", "EFFECT_TYPE_CONTINUOUS"]) || range is null) return null;
        if (code == "EVENT_CHAINING" && set.GetValueOrDefault("Operation") == "aux.chainreg" && props.IsSubsetOf(["EFFECT_FLAG_CANNOT_DISABLE"])
            && set.Keys.All(k => k is "Type" or "Code" or "Range" or "Property" or "Operation"))
            return new TranslatedPiece { Piece = piece, Ignored = true };
        if (code != "EVENT_CHAIN_SOLVED" || props.Count > 0 || !set.TryGetValue("Operation", out var op)
            || set.Keys.Any(k => k is not ("Type" or "Code" or "Range" or "Operation" or "Condition" or "CountLimit")))
            return null;
        string Read(string reference)
        {
            var body = Body(reference, fns);
            // "local p=Duel.GetChainInfo(ev,CHAININFO_TRIGGERING_PLAYER)": the player who activated it, rp.
            body = Regex.Replace(body, @"^local (\w+)=Duel\.GetChainInfo\(ev,CHAININFO_TRIGGERING_PLAYER\) (.*)$", m => Regex.Replace(m.Groups[2].Value, $@"(?<![\w.]){m.Groups[1].Value}(?![\w(])", "rp"));
            return Regex.Replace(body, @"(?<![\w.:])c:", "e:GetHandler():");
        }
        var terms = new List<string>();
        if (set.GetValueOrDefault("Condition") is { } condition)
        {
            if (Regex.Match(Read(condition), @"^return (.+)$") is not { Success: true } cm) return null;
            terms.AddRange(SplitTopLevelAnd(cm.Groups[1].Value));
        }
        Match m;
        var add = $@"e:GetHandler\(\):AddCounter\({CounterNumber},(\d+)\)";
        var text = Read(op);
        if ((m = Regex.Match(text, $@"^if (.+) then {add} end$")).Success) terms.AddRange(SplitTopLevelAnd(m.Groups[1].Value));
        else if (!(m = Regex.Match(text, $@"^(){add}$")).Success) return null;
        if (CounterOf(m.Groups[2].Value) is not { } id || Int(m.Groups[3].Value) is < 1 or > 99) return null;
        string who = "any", what = "any";
        var marked = false;
        foreach (var t in terms.Select(Squash))
        {
            switch (t)
            {
                case "rp==tp": who = "you"; break;
                case "rp==1-tp" or "rp~=tp": who = "opponent"; break;
                case "re:IsActiveType(TYPE_MONSTER)" when what == "any": what = "monster"; break;
                case "re:IsActiveType(TYPE_SPELL)" or "re:IsHasType(EFFECT_TYPE_ACTIVATE)" when what is "any" or "spell_half": what = what == "spell_half" ? "spell" : "spell_half"; break;
                case "e:GetHandler():GetFlagEffect(FLAG_ID_CHAINING)>0" or "e:GetHandler():GetFlagEffect(FLAG_ID_CHAINING)~=0" or "e:GetHandler():GetFlagEffect(1)>0": marked = true; break;
                default: return null;
            }
        }
        // Without the mark it would count activations from before this card was face-up.
        if (!marked || what == "spell_half") return null;
        effect.When = Block("when.while_face_up").With("zone", Zone(range));
        effect.Do.Add(Block(CounterEachTimePassive.BlockId).With("who", who).With("what", what).With("amount", Int(m.Groups[3].Value)).WithCounter(id));
        return new TranslatedPiece { Piece = piece, Effect = effect };
    }

    // ---------------------------------------------------------------- every card of a group

    /// <summary>
    /// "local tc=g:GetFirst() while tc do ... tc=g:GetNext() end", "for tc in aux.Next(g) do ... end" and
    /// "for tc in g:Iter() do ... end" over every card a group matched: the body done to each card, as one block on
    /// "all" of them ("each face-up monster you control gains 500 ATK"). Also reads the group of cards a counter can be
    /// placed on (Duel.GetMatchingGroup(Card.IsCanAddCounter,...,0x100e,1)), with no block. Null for anything else.
    /// </summary>
    private static (BlockInstance? Block, int End)? GroupLoop(List<string> body, int index, Dictionary<string, (CardFilter Filter, int Count)> groups, List<TempEffect> temps)
    {
        var line = body[index].Trim();
        Match m;
        if ((m = Regex.Match(line, $@"^local (\w+)=Duel\.GetMatchingGroup\(Card\.IsCanAddCounter,tp,([\w+]+),([\w+]+),(nil|e:GetHandler\(\)|c),{CounterNumber},(\d+)\)$")).Success
            && CounterOf(m.Groups[5].Value) is { } able)
        {
            var f = new CardFilter { CardType = FilterCardType.Any, FaceUpOnly = true };
            Locate(f, m.Groups[2].Value, m.Groups[3].Value);
            if ((f.Zones & ~Model.Zone.Field) != Model.Zone.None) return null;
            if (f.Zones == Model.Zone.MonsterZone) f.CardType = FilterCardType.Monster;
            f.ExcludeThisCard = m.Groups[4].Value != "nil";
            NoteAddable(f, able, Int(m.Groups[6].Value));
            groups[m.Groups[1].Value] = (f, 0);
            return (null, index);
        }
        string group;
        var inner = new List<string>();
        int end;
        if ((m = Regex.Match(line, @"^(?:local )?tc=(\w+):GetFirst\(\)$")).Success)
        {
            group = m.Groups[1].Value;
            var i = index + 1;
            if (i < body.Count && body[i].Trim() == "if not tc then return end") i++;
            if (i >= body.Count || body[i].Trim() != "while tc do") return null;
            for (i++; i < body.Count && body[i].Trim() != $"tc={group}:GetNext()"; i++) inner.Add(body[i].Trim());
            if (i + 1 >= body.Count || body[i + 1].Trim() != "end") return null;
            end = i + 1;
        }
        else if ((m = Regex.Match(line, @"^for tc in (?:aux\.Next\((\w+)\)|(\w+):Iter\(\)) do$")).Success)
        {
            group = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var i = index + 1;
            for (; i < body.Count && body[i].Trim() != "end"; i++) inner.Add(body[i].Trim());
            if (i >= body.Count) return null;
            end = i;
        }
        else return null;
        if (!groups.TryGetValue(group, out var g) || g.Count != 0) return null;
        inner.RemoveAll(l => l.Length == 0 || l.StartsWith("--", StringComparison.Ordinal));
        // Only plain steps: anything that branches, or names another card, isn't the same as one block on each card.
        if (inner.Any(l => Regex.IsMatch(l, @"^(?:if|for|while|local|else|elseif)\b") || l == "end")) return null;
        BlockInstance? each = inner switch
        {
            [var only] when only.StartsWith("@TEMP ", StringComparison.Ordinal) => EachTemp(temps[Int(only[6..])], g.Filter),
            [var only] when Regex.Match(only, $@"^tc:AddCounter\({CounterNumber},(\d+)\)$") is { Success: true } am && CounterOf(am.Groups[1].Value) is { } id
                => PickedAddable(g.Filter, id, Int(am.Groups[2].Value)) && Int(am.Groups[2].Value) is >= 1 and <= 99
                    ? Block(PlaceCounterAction.BlockId).With("amount", Int(am.Groups[2].Value)).WithCounter(id).With("mode", "all").With("cards", g.Filter) : null,
            _ => null,
        };
        if (each is not null) return (each, end);
        // What it does, for the report: the effects' codes, or the first step.
        var what = string.Join("; ", inner.Select(l => l.StartsWith("@TEMP ", StringComparison.Ordinal)
            ? string.Join("+", temps[Int(l[6..])].Parts.Select(p => (p.Set.GetValueOrDefault("Code") ?? "?").Replace("EFFECT_", "")))
            : l));
        throw No($"it does something to each card of a group the blocks can't ({Short(what)})");
    }

    /// <summary>
    /// An effect given to each card of a group, as a block on all of them: ATK/DEF gained or lost (a number, or a live
    /// value), a Level or ATK that becomes a live value, or a battle rule for this turn. Untranslatable, saying why,
    /// when it isn't one exactly.
    /// </summary>
    private static BlockInstance EachTemp(TempEffect temp, CardFilter cards)
    {
        var parts = temp.Parts;
        var codes = parts.Select(p => p.Set.GetValueOrDefault("Code") ?? "").ToList();
        Untranslatable Fail(string why) => No($"it does something to each card of a group the blocks can't ({string.Join("+", codes.Select(c => c.Replace("EFFECT_", "")))}: {why})");
        if (parts.Any(p => p.Holder != "tc")) throw Fail("it puts effects on other cards too");
        // EFFECT_FLAG_CANNOT_DISABLE keeps an effect a card gives itself while it is negated: it matters only when this
        // card can be one of them, and is the blocks' "this card keeps it".
        var maybeSelf = _sourceCard?.Kind is null or CardKind.Monster && cards.Owner != CardOwner.Opponent && !cards.ExcludeThisCard;
        var kept = new List<bool>();
        foreach (var (set, _) in parts)
        {
            if (set.GetValueOrDefault("Type") != "EFFECT_TYPE_SINGLE") throw Fail("an effect of another type");
            var props = Flags(set.GetValueOrDefault("Property") ?? "");
            // "Only while on the field": the effect resets when the card leaves anyway.
            if (props.Remove("EFFECT_FLAG_SINGLE_RANGE") && set.GetValueOrDefault("Range") != "LOCATION_MZONE") throw Fail("it applies somewhere else");
            kept.Add(props.Remove("EFFECT_FLAG_CANNOT_DISABLE") && maybeSelf);
            if (props.Count > 0 || set.Keys.Any(k => k is not ("Type" or "Code" or "Value" or "Reset" or "Property" or "Range"))
                || set.ContainsKey("Range") && !set.ContainsKey("Property"))
                throw Fail("settings the blocks don't have");
        }
        var value = parts[0].Set.GetValueOrDefault("Value");
        var reset = parts[0].Set.GetValueOrDefault("Reset");
        if (reset is null || parts.Any(p => p.Set.GetValueOrDefault("Value") != value || p.Set.GetValueOrDefault("Reset") != reset)) throw Fail("parts that differ");
        var flags = Flags(Squash(reset));
        string duration;
        if (flags.SetEquals(["RESET_EVENT", "RESETS_STANDARD"])) duration = "always";
        else if (flags.SetEquals(["RESET_EVENT", "RESETS_STANDARD", "RESET_PHASE", "PHASE_END"])) duration = "turn";
        else throw Fail("it lasts for a time the blocks don't have");
        // The blocks act on face-up monsters only.
        if (cards.Zones != Model.Zone.MonsterZone || !cards.FaceUpOnly) throw Fail("it also gives it to face-down cards");
        if (kept.Distinct().Count() > 1) throw Fail("parts that differ");
        BlockInstance All(string id, string modeKey = "mode") => Block(id).With(modeKey, "all").With("cards", cards).With("target", false).With("kept", kept[0]);

        // "They cannot attack this turn", "they can attack directly this turn".
        if (codes.Count == 1 && duration == "turn")
        {
            var rule = (codes[0], value) switch
            {
                ("EFFECT_CANNOT_ATTACK", null or "1") => "cannot_attack",
                ("EFFECT_DIRECT_ATTACK", null or "1") => "direct",
                ("EFFECT_EXTRA_ATTACK", "1") => "twice",
                ("EFFECT_PIERCE", null or "1") => "pierce",
                ("EFFECT_CANNOT_BE_EFFECT_TARGET", "aux.tgoval") => "cannot_be_targeted",
                _ => null,
            };
            if (rule is not null) return All("do.battle_rule").With("rule", rule);
        }
        string? stat = codes.ToHashSet() switch
        {
            var c when codes.Count == 1 && c.Contains("EFFECT_UPDATE_ATTACK") => "atk",
            var c when codes.Count == 1 && c.Contains("EFFECT_UPDATE_DEFENSE") => "def",
            var c when codes.Count == 2 && c.SetEquals(["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"]) => "both",
            _ => null,
        };
        if (stat is not null && value is not null && Regex.IsMatch(value, @"^-?\d+$") && Int(value) is var n and not 0 && Math.Abs(n) <= 99999)
            return All("do.change_stats").With("dir", n < 0 ? "lose" : "gain").With("amount", Math.Abs(n)).With("stat", stat).With("duration", duration);
        // "Their Levels become 4", "increase by 1"; "their ATK becomes 0", "is halved".
        if (codes.Count == 1 && value is not null && Regex.IsMatch(value, @"^-?\d+$"))
        {
            var v = Int(value);
            if (codes[0] == "EFFECT_CHANGE_LEVEL" && v is >= 1 and <= 12)
                return All("do.change_level", "count_mode").With("mode", "becomes").With("amount", v).With("duration", duration);
            if (codes[0] == "EFFECT_UPDATE_LEVEL" && v != 0 && Math.Abs(v) <= 12)
                return All("do.change_level", "count_mode").With("mode", v < 0 ? "down" : "up").With("amount", Math.Abs(v)).With("duration", duration);
            if (codes[0] == "EFFECT_SET_ATTACK_FINAL" && v is >= 0 and <= 99999)
                return All("do.set_atk", "count_mode").With("mode", v == 0 ? "zero" : "value").With("amount", v).With("duration", duration);
        }
        if (codes is ["EFFECT_SET_ATTACK_FINAL"] && value is "math.ceil(tc:GetAttack()/2)")
            return All("do.set_atk", "count_mode").With("mode", "half").With("duration", duration);
        // A live value: worked out for each card as the block does, so it must not change as the cards gain it.
        (string Stat, string Dir)? sd = codes.Count == 1 ? StatOf(codes[0])
            : codes.ToHashSet().SetEquals(["EFFECT_UPDATE_ATTACK", "EFFECT_UPDATE_DEFENSE"]) ? ("both", "gain")
            : codes.ToHashSet().SetEquals(["EFFECT_SET_ATTACK_FINAL", "EFFECT_SET_DEFENSE_FINAL"]) ? ("both", "becomes") : null;
        if (sd is not { } s || value is null || _opValues is not { } ov) throw Fail("an effect the blocks don't have");
        if (Regex.IsMatch(value, @"^-?\d+$")) throw Fail("a number the blocks only take as a live value");
        if (Fresh(value, ov) is not { } expr || OperationValue(expr, "tc", ov) is not { } live || s.Dir == "becomes" && live.Negative) throw Fail($"an amount the blocks can't work out ({Short(value)})");
        if (!SteadyWhileGained(live, s.Stat)) throw Fail("an amount that changes as the cards gain it");
        return live.On(All(StatsFromValueAction.BlockId).With("stat", s.Stat).With("dir", s.Dir == "becomes" ? "becomes" : live.Negative ? "lose" : "gain").With("duration", duration));
    }

    /// <summary>
    /// Whether a value stays the same while the monsters gain the stat one by one: totals of that stat change, and so do
    /// counts of cards picked by their stats.
    /// </summary>
    private static bool SteadyWhileGained(LiveValue v, string stat)
    {
        static bool ByStats(CardFilter f) => f.AtkMin is not null || f.AtkMax is not null || f.DefMin is not null || f.DefMax is not null
            || f.LevelMin is not null || f.LevelMax is not null || f.Requirements.Count > 0;
        if (v.Cards is { } cards && ByStats(cards)) return false;
        return v.Source switch
        {
            "sum_atk" => stat is "def" or "level",
            "sum_def" => stat is "atk" or "level",
            "sum_level" => stat != "level",
            _ => true,
        };
    }

    /// <summary>
    /// The place-counters block on this card asks, when an effect is activated, whether the counters can be placed
    /// (<see cref="PlaceCounterAction.ActivationCheck"/>): the script's activation check must ask the same.
    /// </summary>
    private static void CounterActivationChecks(EffectEvent effect, TargetInfo target, HashSet<string> type)
    {
        var mandatory = type.Contains("EFFECT_TYPE_TRIGGER_F") || type.Contains("EFFECT_TYPE_FLIP") && !type.Contains("EFFECT_TYPE_TRIGGER_O");
        var activation = type.Contains("EFFECT_TYPE_ACTIVATE");
        foreach (var b in effect.Do.Where(b => b.BlockId == PlaceCounterAction.BlockId && b.Filters.GetValueOrDefault("cards")?.ThisCard == true))
        {
            var ask = PlaceCounterAction.ActivationCheck(mandatory, activation, Counters.Hex(Counters.IdOf(b) ?? 0x1), b.Values.GetValueOrDefault("amount", "1"));
            var terms = target.Check is { } check ? SplitTopLevelAnd(Squash(check)).Select(t => t.Replace("c:", "e:GetHandler():", StringComparison.Ordinal).Replace(",c)", ",e:GetHandler())", StringComparison.Ordinal)).ToList() : [];
            var counterTerms = terms.Where(t => t.Contains("AddCounter(", StringComparison.Ordinal)).ToList();
            if (ask is null ? counterTerms.Count > 0 : !(counterTerms.Count == 1 && counterTerms[0] == ask))
                throw No(ask is null ? "its activation checks the counters can be placed, which a mandatory trigger's block doesn't"
                    : "its activation checks whether the counters can be placed differently from the block");
        }
    }
}
