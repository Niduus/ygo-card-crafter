using System.Globalization;
using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// What cards remember (Blocks/Memory.cs): "banish it until your next Standby Phase" (a temporary banish and a delayed
/// return of the same card), "pay LP in multiples of 100" with the LP paid kept in the effect's label, and cards marked
/// with a flag one effect puts on them and another effect's picker reads. Each is read only where the blocks do exactly
/// what the script does.
/// </summary>
public static partial class ScriptTranslator
{
    // ---------------------------------------------------------------- the LP paid

    /// <summary>Whether the effect being read paid LP in multiples, kept in its label ("the LP paid").</summary>
    [ThreadStatic] private static bool _lpPaid;

    private static readonly Regex LpMultiples = new(
        @"^if chk==0 then return Duel\.CheckLPCost\(tp,(100|500|1000),true\) end (?:local (\w+)=Duel\.GetLP\(tp\) )?" +
        @"local (\w+)=math\.floor\((?:math\.min\((?:\2|Duel\.GetLP\(tp\)),(\d+)\)|(?:\2|Duel\.GetLP\(tp\)))/\1\) local (\w+)=\{\} " +
        @"for (\w+)=1,\3 do \5\[\6\]=\6\*\1 end (?:Duel\.Hint\([^()]*\) )?local (\w+)=Duel\.AnnounceNumber\(tp,table\.unpack\(\5\)\) " +
        @"Duel\.PayLPCost\(tp,\7,true\) e:SetLabel\(\7\)$");

    /// <summary>
    /// Cosmic Tree Irmistil's "pay 1000, 2000, or 3000 LP": the same choice written with a while loop, "at most K times the
    /// step", with the label set before or after paying (the payment can't fail once checked).
    /// </summary>
    private static readonly Regex LpMultiplesLoop = new(
        @"^if chk==0 then return Duel\.CheckLPCost\(tp,(100|500|1000),true\) end local (\w+)=Duel\.GetLP\(tp\) local (\w+)=\{\} " +
        @"local (\w+)=math\.floor\(\(?\2\)?/\1\) local (\w+)=1 while \5<=\4 and \5<=(\d+) do \3\[\5\]=\5\*\1 \5=\5\+1 end " +
        @"(?:Duel\.Hint\([^()]*(?:\([^()]*\))?[^()]*\) )?local (\w+)=Duel\.AnnounceNumber\(tp,table\.unpack\(\3\)\) " +
        @"(?:e:SetLabel\(\7\) Duel\.PayLPCost\(tp,\7,true\)|Duel\.PayLPCost\(tp,\7,true\) e:SetLabel\(\7\))$");

    /// <summary>
    /// Psychic Blade's "Pay LP in multiples of 100 (max. 2000)" kept in the effect's label (e:SetLabel), as the cost block
    /// writes it. Null for any other cost.
    /// </summary>
    private static List<BlockInstance>? LpMultiplesCost(string reference, Functions fns)
    {
        if (reference.StartsWith("aux.", StringComparison.Ordinal)) return null;
        var (_, body) = fns.Get(reference);
        var text = string.Join(" ", JoinContinuations(body).Select(l => l.Trim()).Where(l => l.Length > 0));
        int step, max;
        Match m;
        if ((m = LpMultiples.Match(text)).Success) (step, max) = (Int(m.Groups[1].Value), m.Groups[4].Success ? Int(m.Groups[4].Value) : 0);
        else if ((m = LpMultiplesLoop.Match(text)).Success && Int(m.Groups[6].Value) > 0) (step, max) = (Int(m.Groups[1].Value), Int(m.Groups[1].Value) * Int(m.Groups[6].Value));
        else return null;
        // A most below one step can't be paid at all.
        if (max > 0 && max < step || max > 99999) return null;
        // "e:GetLabel()" is the LP paid only while nothing else sets the label: no other function of the card may.
        var own = Regex.Match(reference.Trim(), @"\.(\w+)$").Groups[1].Value;
        if (fns.ByName.Any(f => f.Key != own && f.Value.Body.Any(l => Regex.IsMatch(l, @"\be:SetLabel\(")))) return null;
        _lpPaid = true;
        return [Block("cost.pay_lp").With("how", "multiple").With("step", step).With("max", max)];
    }

    /// <summary>"e:GetLabel()" in an operation after that cost: the LP paid.</summary>
    private static (string Source, CardFilter? Cards)? LpPaidBase(string x, bool lpPaid) => lpPaid && x == "e:GetLabel()" ? ("lp_paid", null) : null;

    // ---------------------------------------------------------------- banished until a Standby Phase

    /// <summary>
    /// "Banish it until your next Standby Phase", read as one line: a target (or this card) banished temporarily, then an
    /// effect at a Standby Phase that returns it to the field if it is still the same banished card (a flag reset when it
    /// moves, or the effect's relation to it). Only for effects used during your Main Phase (Ignition Effects, Spells other
    /// than Quick-Play), where the official timings and the block's agree; the line becomes
    /// "Duel.CardCrafterBanishUntil(tc,standby_your)".
    /// </summary>
    private static string BanishUntilRewrite(string lua)
    {
        return Regex.Replace(lua, @"(?ms)^function ([\w]+)\.(\w+)\(e,tp,eg,ep,ev,re,r,rp\)\n(.*?)^end", fn =>
        {
            var body = fn.Groups[3].Value;
            if (!body.Contains("REASON_EFFECT+REASON_TEMPORARY", StringComparison.Ordinal)) return fn.Value;
            try
            {
                var rewritten = BanishUntilBody(lua, fn.Groups[1].Value, fn.Groups[2].Value, body);
                return rewritten is null ? fn.Value : $"function {fn.Groups[1].Value}.{fn.Groups[2].Value}(e,tp,eg,ep,ev,re,r,rp)\n{rewritten}end";
            }
            catch (Untranslatable) { return fn.Value; }
        });
    }

    /// <summary>The function's lines with the temporary banish and its delayed return as one line, or null.</summary>
    private static string? BanishUntilBody(string lua, string prefix, string name, string body)
    {
        var lines = body.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
        var at = lines.FindIndex(l => Regex.IsMatch(l, @"Duel\.Remove\((\w+|e:GetHandler\(\)),0,REASON_EFFECT\+REASON_TEMPORARY\)"));
        if (at < 0 || lines.Skip(at + 1).Any(l => l.Contains("REASON_TEMPORARY", StringComparison.Ordinal))) return null;
        var head = lines[at].Trim();
        var rm = Regex.Match(head, @"^if (.*?)Duel\.Remove\((\w+|e:GetHandler\(\)),0,REASON_EFFECT\+REASON_TEMPORARY\)(~=0|>0|==0)(.*?) then( return end)?$");
        if (!rm.Success) return null;
        var x = rm.Groups[2].Value;
        // "if Duel.Remove(tc,...)==0 then return end": the rest of the function is the delayed return.
        var gated = rm.Groups[3].Value != "==0";
        if (gated == rm.Groups[5].Success || !gated && rm.Groups[1].Value.Length > 0) return null;
        var tail = rm.Groups[4].Value;
        if (tail.Length > 0 && !Regex.IsMatch(tail, $@"^ and {Regex.Escape(x)}:IsLocation\(LOCATION_REMOVED\)$")) return null;
        // The effect must only be used during its controller's Main Phase.
        if (!MainPhaseOnly(lua, $"{prefix}.{name}")) return null;

        // The delayed return: the lines right after, up to Duel.RegisterEffect(v,tp) and the effect's relation to the card.
        var end = at + 1;
        string? v = null;
        var region = new List<string>();
        for (; end < lines.Count; end++)
        {
            var l = lines[end].Trim();
            region.Add(l);
            Match m;
            if ((m = Regex.Match(l, @"^local (\w+)=Effect\.CreateEffect\((?:c|e:GetHandler\(\))\)$")).Success) { if (v is not null) return null; v = m.Groups[1].Value; continue; }
            if (v is not null && l == $"Duel.RegisterEffect({v},tp)")
            {
                if (end + 1 < lines.Count && lines[end + 1].Trim() == $"{x}:CreateEffectRelation({v})") region.Add(lines[++end].Trim());
                break;
            }
        }
        if (v is null || end >= lines.Count) return null;
        // Lines after the return's registration in the same block (or the function's end) stay.
        var timing = DelayedReturn(lua, prefix, x, v, region);
        if (timing is null) return null;
        var call = $"Duel.CardCrafterBanishUntil({x},{timing})";
        var result = new List<string>();
        result.AddRange(lines.Take(at));
        var indent = Regex.Match(lines[at], @"^\s*").Value;
        result.Add(gated
            ? $"{indent}if {rm.Groups[1].Value}{call}{rm.Groups[3].Value} then"
            : $"{indent}if {call}==0 then return end");
        result.AddRange(lines.Skip(end + 1));
        return string.Join("\n", result) + "\n";
    }

    /// <summary>
    /// Whether the effect whose operation is <paramref name="operation"/> can only be used during its controller's Main
    /// Phase: an Ignition Effect, or the activation of a Spell Card other than a Quick-Play Spell.
    /// </summary>
    private static bool MainPhaseOnly(string lua, string operation)
    {
        var uses = Regex.Matches(lua, $@"(\w+):SetOperation\({Regex.Escape(operation)}\)");
        if (uses.Count != 1) return false;
        var v = uses[0].Groups[1].Value;
        // Only the function that creates the effect: other functions use the same local names for their own effects.
        var maker = Regex.Matches(lua, @"(?ms)^function [\w.:]+\([^)]*\)\n.*?^end").FirstOrDefault(f => f.Index <= uses[0].Index && uses[0].Index < f.Index + f.Length);
        if (maker is null) return false;
        var fn = maker.Value;
        // Copies of the effect may change when it is used.
        if (Regex.IsMatch(fn, $@"=\s*{Regex.Escape(v)}:Clone\(\)")) return false;
        if (Regex.Matches(fn, $@"\blocal {Regex.Escape(v)}=").Count != 1) return false;
        var types = Regex.Matches(fn, $@"\b{Regex.Escape(v)}:SetType\(([\w+]+)\)");
        if (types.Count != 1) return false;
        var type = types[0].Groups[1].Value;
        if (type == "EFFECT_TYPE_IGNITION") return true;
        return type == "EFFECT_TYPE_ACTIVATE" && Regex.IsMatch(fn, $@"\b{Regex.Escape(v)}:SetCode\(EVENT_FREE_CHAIN\)")
            && _sourceCard is { Kind: CardKind.Spell } card && card.SpellKind != SpellKind.QuickPlay;
    }

    /// <summary>What a time check reads, and where the turn is: registered in the controller's Main Phase of turn 10.</summary>
    private sealed class Moment
    {
        public int Turn { get; init; } = 10;
        public bool YourTurn => Turn % 2 == 0;
        public string Phase { get; init; } = "PHASE_MAIN1";
        public Dictionary<string, int> Values { get; } = [];
    }

    private static readonly string[] PhaseOrder = ["PHASE_DRAW", "PHASE_STANDBY", "PHASE_MAIN1", "PHASE_BATTLE_START", "PHASE_BATTLE_STEP", "PHASE_DAMAGE", "PHASE_DAMAGE_CAL", "PHASE_BATTLE", "PHASE_MAIN2", "PHASE_END"];

    /// <summary>A number at <paramref name="at"/>: the turn count, a number, or a value kept before ("e:GetLabel()", a local).</summary>
    private static int Number(string x, Moment at)
    {
        x = StripParens(Squash(x));
        if (int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        if (x == "Duel.GetTurnCount()") return at.Turn;
        if (at.Values.TryGetValue(x, out var kept)) return kept;
        Match m;
        if ((m = Regex.Match(x, @"^Duel\.GetTurnCount\(\)\+(\d+)$")).Success) return at.Turn + Int(m.Groups[1].Value);
        throw No($"its delayed return keeps a number the blocks can't follow ({Short(x)})");
    }

    /// <summary>
    /// A test at <paramref name="at"/>: whose turn, which phase, the turn count against kept numbers. A check that the card
    /// is still the same ("tc:GetFlagEffect(id)~=0", "IsRelateToEffect(e)") holds, and is recorded in <paramref name="checks"/>.
    /// </summary>
    private static bool Holds(string cond, Moment at, HashSet<string> checks)
    {
        var spaced = StripParens(Regex.Replace(cond, @"\s+", " ").Trim());
        var ors = SplitTopLevel(spaced, " or ");
        if (ors.Count > 1) return ors.Select(o => Holds(o, at, checks)).ToList().Any(b => b);
        var ands = SplitTopLevel(spaced, " and ");
        if (ands.Count > 1) return ands.Select(a => Holds(a, at, checks)).ToList().All(b => b);
        if (spaced.StartsWith("not ", StringComparison.Ordinal) || spaced.StartsWith("not(", StringComparison.Ordinal)) return !Holds(spaced[3..], at, checks);
        var c = Squash(spaced);
        if (c == "true") return true;
        if (c == "false") return false;
        Match m;
        if ((m = Regex.Match(c, @"^(?:Duel\.GetTurnPlayer\(\)(==|~=)tp|tp(==|~=)Duel\.GetTurnPlayer\(\))$")).Success)
            return (m.Groups[1].Value + m.Groups[2].Value == "==") == at.YourTurn;
        if ((m = Regex.Match(c, @"^Duel\.GetCurrentPhase\(\)(==|~=|<=|>=|<|>)(PHASE_\w+)$")).Success)
        {
            var (now, then) = (Array.IndexOf(PhaseOrder, at.Phase), Array.IndexOf(PhaseOrder, m.Groups[2].Value));
            if (then < 0) throw No($"its delayed return checks a phase the blocks can't follow ({Short(c)})");
            return m.Groups[1].Value switch { "==" => now == then, "~=" => now != then, "<=" => now <= then, ">=" => now >= then, "<" => now < then, _ => now > then };
        }
        if ((m = Regex.Match(c, @"^(?:\w+|e:GetLabelObject\(\)):GetFlagEffect\((id|\d+)\)(?:~=0|>0)$")).Success) { checks.Add("flag:" + m.Groups[1].Value); return true; }
        if ((m = Regex.Match(c, @"^(?:\w+|e:GetLabelObject\(\)):GetFlagEffectLabel\((id|\d+)\)==(?:e:GetLabel\(\)|fid)$")).Success) { checks.Add("flag:" + m.Groups[1].Value); return true; }
        if (Regex.IsMatch(c, @"^(?:\w+|e:GetLabelObject\(\)):IsRelateToEffect\(e\)$")) { checks.Add("relation"); return true; }
        if ((m = Regex.Match(c, @"^(.+?)(==|~=|<=|>=|<|>)(.+)$")).Success)
        {
            var (a, b) = (Number(m.Groups[1].Value, at), Number(m.Groups[3].Value, at));
            return m.Groups[2].Value switch { "==" => a == b, "~=" => a != b, "<=" => a <= b, ">=" => a >= b, "<" => a < b, _ => a > b };
        }
        throw No($"its delayed return checks something the blocks can't follow ({Short(c)})");
    }


    /// <summary>
    /// The block's "until ..." for a delayed return, from the lines that register it, or null when it isn't one the
    /// blocks have. The effect is played from the controller's Main Phase: it must come back in the first Standby Phase
    /// the block's timing names, once, and only while it is still the same card.
    /// </summary>
    private static string? DelayedReturn(string lua, string prefix, string x, string v, List<string> region)
    {
        // Registered now: the controller's Main Phase.
        var now = new Moment();
        var set = new Dictionary<string, string>();
        string? flag = null, flagReset = null, flagCount = null;
        var relation = false;
        // The if / else branches the lines are in: whether each one's test holds now, and whether it is in its else.
        var branches = new Stack<(bool Holds, bool Else)>();
        bool Live() => branches.All(b => b.Holds != b.Else);
        foreach (var l in Statements(region))
        {
            Match m;
            if (l == "else") { if (branches.Count == 0) return null; var top = branches.Pop(); branches.Push((top.Holds, true)); continue; }
            if ((m = Regex.Match(l, @"^if (.+) then$")).Success) { branches.Push((Holds(m.Groups[1].Value, now, []), false)); continue; }
            if (l == "end") { if (branches.Count == 0) return null; branches.Pop(); continue; }
            if (!Live()) continue;
            if ((m = Regex.Match(l, @"^local (\w+)=(?:e:GetHandler\(\)|c):GetFieldID\(\)$")).Success) { now.Values[m.Groups[1].Value] = 7777; continue; }
            if ((m = Regex.Match(l, @"^(?:local )?(\w+)=(\d+)$")).Success) { now.Values[m.Groups[1].Value] = Int(m.Groups[2].Value); continue; }
            if ((m = Regex.Match(l, @"^local (\w+)=(.+) and (\d+) or (\d+)$")).Success)
            {
                now.Values[m.Groups[1].Value] = Holds(m.Groups[2].Value, now, []) ? Int(m.Groups[3].Value) : Int(m.Groups[4].Value);
                continue;
            }
            if ((m = Regex.Match(l, $@"^{Regex.Escape(x)}:RegisterFlagEffect\((id|\d+),([\w+]+),(?:0|EFFECT_FLAG_CLIENT_HINT),(\w+)(?:,(\w+))?(?:,aux\.Stringid\(\w+,\d+\))?\)$")).Success)
            {
                if (flag is not null) return null;
                (flag, flagReset, flagCount) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                continue;
            }
            if (l == $"{x}:CreateEffectRelation({v})") { relation = true; continue; }
            if ((m = Regex.Match(l, $@"^{Regex.Escape(v)}:Set(\w+)\((.*)\)$")).Success) { set[m.Groups[1].Value] = m.Groups[2].Value; continue; }
            if (l == $"local {v}=Effect.CreateEffect(c)" || l == $"local {v}=Effect.CreateEffect(e:GetHandler())" || l == $"Duel.RegisterEffect({v},tp)") continue;
            return null;
        }
        if (branches.Count != 0) return null;
        if (set.GetValueOrDefault("Type") != "EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS" || set.GetValueOrDefault("Code") != "EVENT_PHASE+PHASE_STANDBY"
            || set.GetValueOrDefault("CountLimit") != "1" || set.GetValueOrDefault("LabelObject") != x || !set.ContainsKey("Operation")) return null;
        if (set.Keys.Except(["Type", "Code", "CountLimit", "LabelObject", "Operation", "Condition", "Reset", "Label", "Value", "Description", "Property"]).Any()) return null;
        if (set.TryGetValue("Property", out var prop) && prop != "EFFECT_FLAG_IGNORE_IMMUNE") return null;

        // The kept numbers: e:GetLabel() and e:GetValue() when the effect is checked later.
        var later = new Dictionary<string, int>();
        if (set.TryGetValue("Label", out var label))
        {
            var parts = SplitArgs(label);
            for (var i = 0; i < parts.Count; i++) later["label" + i.ToString(CultureInfo.InvariantCulture)] = Number(parts[i], now);
        }
        if (set.TryGetValue("Value", out var value)) later["e:GetValue()"] = Number(value, now);
        // How many Standby Phases (of whose turns) it lasts.
        string? resetFlags = null;
        var resetCount = 0;
        if (set.TryGetValue("Reset", out var reset))
        {
            var r = SplitArgs(reset);
            resetFlags = Squash(r[0]);
            resetCount = r.Count > 1 ? Number(r[1], now) : 1;
            if (!Regex.IsMatch(resetFlags, @"^RESET_PHASE\+PHASE_STANDBY(?:\+RESET_SELF_TURN|\+RESET_OPPO_TURN)?$")) return null;
        }

        // The condition and the operation, at each turn's Standby Phase.
        var checks = new HashSet<string>();
        List<string> Lines(string fn) => Regex.Match(lua, $@"(?ms)^function {Regex.Escape(fn)}\(e,tp,eg,ep,ev,re,r,rp\)\n(.*?)^end") is { Success: true } f
            ? JoinContinuations(f.Groups[1].Value.Split('\n').ToList()).Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
            : throw No("its delayed return calls a function it doesn't define");
        bool Unique(string fn) => Regex.Matches(lua, $@"\b{Regex.Escape(fn)}\b").Count == 2;
        var op = set["Operation"];
        if (!op.StartsWith(prefix + ".", StringComparison.Ordinal) || !Unique(op)) return null;
        var opLines = Lines(op).Where(s => s is not ("local tc=e:GetLabelObject()" or "e:Reset()")).ToList();
        string? opCheck = null;
        if (opLines.Count == 3 && Regex.Match(opLines[0], @"^if (.+) then$") is { Success: true } og && opLines[2] == "end") { opCheck = og.Groups[1].Value; opLines = [opLines[1]]; }
        if (opLines is not ["Duel.ReturnToField(e:GetLabelObject())" or "Duel.ReturnToField(tc)"]) return null;
        if (opCheck is not null) Holds(opCheck, new Moment { Turn = 11 }, checks);
        var con = set.GetValueOrDefault("Condition");
        if (con is not null && (!con.StartsWith(prefix + ".", StringComparison.Ordinal) || !Unique(con))) return null;
        var conLines = con is null ? [] : Lines(con).Where(s => s != "local tc=e:GetLabelObject()").ToList();
        bool ConditionAt(Moment at)
        {
            foreach (var (k, n) in later)
                if (k.StartsWith("label", StringComparison.Ordinal)) { if (k == "label0") at.Values["e:GetLabel()"] = n; }
                else at.Values[k] = n;
            foreach (var cl in conLines)
            {
                Match m;
                if ((m = Regex.Match(cl, @"^local ([\w,]+)=e:GetLabel\(\)$")).Success)
                {
                    var names = m.Groups[1].Value.Split(',');
                    for (var i = 0; i < names.Length; i++)
                        at.Values[names[i]] = later.TryGetValue("label" + i.ToString(CultureInfo.InvariantCulture), out var n) ? n : 0;
                    continue;
                }
                if ((m = Regex.Match(cl, @"^if (.+) then return false end$")).Success) { if (Holds(m.Groups[1].Value, at, checks)) return false; continue; }
                if ((m = Regex.Match(cl, @"^return (.+)$")).Success) return Holds(m.Groups[1].Value, at, checks);
                throw No($"its delayed return checks something the blocks can't follow ({Short(cl)})");
            }
            return true;
        }
        // The first Standby Phase it returns the card in, from the next turn on.
        int? fires = null;
        var spent = 0;
        for (var turn = now.Turn + 1; turn <= now.Turn + 4 && fires is null; turn++)
        {
            var at = new Moment { Turn = turn, Phase = "PHASE_STANDBY" };
            var alive = resetFlags is null || spent < resetCount;
            if (alive && ConditionAt(at)) fires = turn;
            var counts = resetFlags is not null && (resetFlags.EndsWith("RESET_SELF_TURN", StringComparison.Ordinal) ? at.YourTurn
                : resetFlags.EndsWith("RESET_OPPO_TURN", StringComparison.Ordinal) ? !at.YourTurn : true);
            if (counts) spent++;
        }
        if (fires is not { } f) return null;
        // It must only return the same card: a flag that is gone once the card moves, still there then, or the effect's relation to it.
        if (relation ? !checks.Contains("relation") : flag is null || !checks.Contains("flag:" + flag)) return null;
        if (flag is not null && !relation)
        {
            if (!flagReset!.Contains("RESET_EVENT+RESETS_STANDARD", StringComparison.Ordinal)) return null;
            var fr = Squash(flagReset).Replace("RESET_EVENT+RESETS_STANDARD", "", StringComparison.Ordinal).TrimStart('+');
            var fcount = Number(flagCount!, now);
            // The flag's own end: at the end of turns (PHASE_END) or Standby Phases; still there when the card returns.
            var ends = fr switch
            {
                "" => int.MaxValue,
                "RESET_PHASE+PHASE_END" => Enumerable.Range(now.Turn, f - now.Turn).Count(),
                "RESET_PHASE+PHASE_STANDBY" => Enumerable.Range(now.Turn + 1, f - now.Turn - 1).Count(),
                "RESET_PHASE+PHASE_STANDBY+RESET_SELF_TURN" => Enumerable.Range(now.Turn + 1, f - now.Turn - 1).Count(t => t % 2 == 0),
                "RESET_PHASE+PHASE_STANDBY+RESET_OPPO_TURN" => Enumerable.Range(now.Turn + 1, f - now.Turn - 1).Count(t => t % 2 == 1),
                _ => -1,
            };
            if (ends < 0 || ends != int.MaxValue && ends >= fcount) return null;
            // The flag is only this return's: nothing else in the script uses it.
            var uses = new Regex($@"FlagEffect(?:Label)?\({Regex.Escape(flag)}\b");
            var own = string.Join("\n", region.Concat(Lines(op)).Concat(con is null ? [] : Lines(con)));
            if (uses.Matches(lua).Count != uses.Matches(own).Count) return null;
        }
        var opponent = resetFlags?.EndsWith("RESET_OPPO_TURN", StringComparison.Ordinal) == true || conLines.Any(cl => Regex.IsMatch(Squash(cl), @"Duel\.GetTurnPlayer\(\)~=tp|tp~=Duel\.GetTurnPlayer\(\)"));
        return (f - now.Turn) switch
        {
            1 => opponent ? "standby_opp" : "standby_next",
            2 => "standby_your",
            _ => null,
        };
    }

    /// <summary>The marked line of <see cref="BanishUntilRewrite"/> as the banish block, or null for any other line.</summary>
    private static BlockInstance? MemoryAction(string line, Dictionary<string, (CardFilter Filter, int Count)> groups, TargetInfo target, bool targetCard)
    {
        var m = Regex.Match(line, @"^Duel\.CardCrafterBanishUntil\((\w+|e:GetHandler\(\)),(standby_your|standby_next|standby_opp)\)$");
        if (!m.Success) return null;
        if (m.Groups[1].Value is "c" or "e:GetHandler()")
            return ChecksThisCardIsThere() ? Block("do.banish").With("cards", new CardFilter { ThisCard = true }).With("until", m.Groups[2].Value) : throw No("it banishes this card in a way the blocks can't");
        if (CardsOf(m.Groups[1].Value, groups, target, targetCard) is not { Count: 1 } banished) throw No("it banishes cards for a while in a way the blocks can't");
        // The block only picks cards that can be banished: so must the script.
        if (_opValues?.Fns is not { } fns || !fns.ByName.Values.Any(f => f.Body.Any(l => l.Contains("IsAbleToRemove", StringComparison.Ordinal))))
            throw No("it banishes cards for a while that it doesn't check can be banished");
        return CountedBlock("do.banish", banished).With("until", m.Groups[2].Value);
    }
}
