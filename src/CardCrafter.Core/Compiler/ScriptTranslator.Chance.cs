using System.Globalization;
using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Coin tosses and die rolls with a step for each result, declarations ("Declare 1 card name; ..."), and the Toon
/// monsters' rules. Each is read only in the shapes official scripts write, where the blocks do exactly the same: a toss
/// the effect makes before anything else (or right after checking its target), results that are tests of the toss
/// alone, and declarations kept with the chain link.
/// </summary>
public static partial class ScriptTranslator
{
    // ---------------------------------------------------------------- declarations

    /// <summary>
    /// A declaration a target step makes: the target step (with the script's passcode), its kind, the local holding it,
    /// and where it is kept ("param", "label").
    /// </summary>
    private sealed record Declaration(string Target, string Kind, string Local, BlockInstance Block)
    {
        public string? Kept { get; set; }
    }

    [ThreadStatic] private static Declaration? _declaration;

    /// <summary>
    /// A line of a target step that declares: "local ac=Duel.AnnounceCard(tp)", "local rc=Duel.AnnounceRace(tp,1,RACE_ALL)",
    /// then "Duel.SetTargetParam(ac)" or "e:SetLabel(ac)". The declaration becomes the DO blocks' first block.
    /// </summary>
    private static bool DeclarationLine(string line, string target)
    {
        Match m;
        if ((m = Regex.Match(line, @"^local (\w+)=Duel\.Announce(Card|Race|Attribute|Level)\(tp(?:,(.*))?\)$")).Success)
        {
            var args = m.Groups[3].Value;
            var block = Block(DeclareAction.BlockId);
            // "Duel.AnnounceLevel(tp,1,8)": a Level from 1 to 8.
            var levels = Regex.Match(args, @"^(\d+),(\d+)$");
            var kind = (m.Groups[2].Value, args) switch
            {
                ("Card", "") => "name",
                ("Card", "TYPE_MONSTER,OPCODE_ISTYPE") => "monster_name",
                ("Race", "1,RACE_ALL") => "race",
                ("Attribute", "1,ATTRIBUTE_ALL") => "attribute",
                ("Level", "") => "level",
                ("Level", _) when levels.Success && Int(levels.Groups[1].Value) is >= 1 and <= 12 && Int(levels.Groups[2].Value) is >= 1 and <= 12 => "level",
                _ => throw No($"it declares from a list the blocks don't offer ({Short(line)})"),
            };
            if (levels.Success && kind == "level") block.With("min", Int(levels.Groups[1].Value)).With("max", Int(levels.Groups[2].Value));
            if (_declaration?.Target == target) throw No("it declares more than one thing");
            _declaration = new Declaration(target, kind, m.Groups[1].Value, block.With("what", kind));
            return true;
        }
        if (_declaration is { } d && d.Target == target && d.Kept is null
            && (m = Regex.Match(line, @"^(?:Duel\.SetTargetParam|e:SetLabel)\((\w+)\)$")).Success && m.Groups[1].Value == d.Local)
        {
            d.Kept = line.StartsWith("Duel.", StringComparison.Ordinal) ? "param" : "label";
            return true;
        }
        return false;
    }

    /// <summary>
    /// "c:IsRace(rc)" with rc the declared Type, written as a marker by <see cref="WithDeclared"/>: a "declared" requirement.
    /// Also "a card that has a coin tossing effect" and "that requires a die roll".
    /// </summary>
    private static bool DeclaredTerm(string term, CardFilter f)
    {
        var m = Regex.Match(term, @"^(not )?(?:c:IsCardCrafterDeclared_(\w+)\(\)|c:IsEffectProperty\(aux\.EffectPropertyFilter\(EFFECT_FLAG_(COIN|DICE)\)\))$");
        if (!m.Success) return false;
        var property = m.Groups[2].Success ? "declared_" + m.Groups[2].Value : m.Groups[3].Value == "COIN" ? "coin_effect" : "dice_effect";
        f.Requirements.Add(new CardRequirement { Property = property, Op = m.Groups[1].Success ? RequirementOp.IsNot : RequirementOp.Is });
        return true;
    }

    /// <summary>
    /// The operation's lines with the declared value read back ("local ac=Duel.GetChainInfo(0,CHAININFO_TARGET_PARAM)")
    /// taken out, and the filters it is passed to rewritten so they test "the declared" name, Type, Attribute or Level.
    /// </summary>
    private static List<string> WithDeclared(List<string> lines, Declaration d, Functions fns)
    {
        var read = d.Kept == "param" ? @"Duel\.GetChainInfo\(0,CHAININFO_TARGET_PARAM\)" : @"e:GetLabel\(\)";
        var at = lines.FindIndex(l => Regex.IsMatch(l, $@"^local \w+={read}$"));
        if (at < 0) throw No("it doesn't use what it declared the way the blocks do");
        var v = Regex.Match(lines[at], @"^local (\w+)=").Groups[1].Value;
        var result = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i == at) continue;
            var line = lines[i];
            if (!Regex.IsMatch(line, $@"\b{Regex.Escape(v)}\b")) { result.Add(line); continue; }
            // "Duel.GetMatchingGroup(s.filter,tp,LOCATION_MZONE,LOCATION_MZONE,nil,arc)": the filter's second parameter is the declared value.
            var m = Regex.Match(line, $@"^(.*Duel\.(?:GetMatchingGroup|SelectMatchingCard|IsExistingMatchingCard|GetMatchingGroupCount)\()((?:aux\.NecroValleyFilter\()?)([\w.]+)(\)?)(,.*),{Regex.Escape(v)}\)(.*)$");
            if (!m.Success) throw No($"it uses what it declared in a way the blocks don't ({Short(line)})");
            var (pars, body) = fns.Get(m.Groups[3].Value);
            if (pars.Length != 2) throw No($"it uses what it declared in a way the blocks don't ({Short(line)})");
            var p = Regex.Escape(pars[1]);
            var tests = d.Kind switch
            {
                "name" or "monster_name" => new[] { ("IsCode", "name"), ("IsOriginalCodeRule", "orig_name") },
                "race" => [("IsRace", "race")],
                "attribute" => [("IsAttribute", "attribute")],
                _ => [("IsLevel", "level")],
            };
            var rewritten = body.Select(l => tests.Aggregate(l, (t, x) => Regex.Replace(t, $@"\bc:{x.Item1}\({p}\)", $"c:IsCardCrafterDeclared_{x.Item2}()"))).ToList();
            if (rewritten.Any(l => Regex.IsMatch(l, $@"\b{p}\b"))) throw No("its filter uses what it declared in a way the blocks don't");
            var name = Synthetic(fns, ["c"], rewritten);
            result.Add($"{m.Groups[1].Value}{m.Groups[2].Value}{name}{m.Groups[4].Value}{m.Groups[5].Value}){m.Groups[6].Value}");
        }
        return result;
    }

    /// <summary>A function of the lines given, added to <paramref name="fns"/> for the other readers, by its reference ("s.__cc1").</summary>
    private static string Synthetic(Functions fns, string[] pars, List<string> body)
    {
        var name = "__cc" + (fns.ByName.Count + 1).ToString(CultureInfo.InvariantCulture);
        fns.ByName[name] = (pars, body);
        return $"{fns.Prefix}.{name}";
    }

    /// <summary>
    /// Psi-Blocker, Prohibition and Sales Ban's halves: a field effect labelled with the declared card name that forbids
    /// cards with that original name, or stops them from being activated or Special Summoned.
    /// </summary>
    private static BlockInstance? DeclaredLockOf(List<string> lines, Declaration d, Functions fns)
    {
        if (d.Kind is not ("name" or "monster_name") || d.Kept != "param") return null;
        var rest = lines.Where(l => l is not ("local c=e:GetHandler()" or "Duel.AdjustInstantly(c)" or "Duel.AdjustInstantly(e:GetHandler())")).ToList();
        if (rest.Count < 3) return null;
        var read = Regex.Match(rest[0], @"^local (\w+)=Duel\.GetChainInfo\(0,CHAININFO_TARGET_PARAM\)$");
        if (!read.Success) return null;
        var ac = read.Groups[1].Value;
        var i = 1;
        var hint = Regex.IsMatch(rest[i], $@"^(?:c|e:GetHandler\(\)):SetHint\(CHINT_CARD,{ac}\)$");
        if (hint) i++;
        var create = Regex.Match(rest[i], @"^local (\w+)=Effect\.CreateEffect\((?:c|e:GetHandler\(\))\)$");
        if (!create.Success) return null;
        var v = create.Groups[1].Value;
        var set = new Dictionary<string, string>();
        for (i++; i < rest.Count - 1; i++)
        {
            var s = Regex.Match(rest[i], $@"^{v}:Set(\w+)\((.*)\)$");
            if (!s.Success) return null;
            set[s.Groups[1].Value] = s.Groups[2].Value;
        }
        var register = rest[^1];
        var onCard = Regex.IsMatch(register, $@"^(?:c|e:GetHandler\(\)):RegisterEffect\({v}\)$");
        if (!onCard && register != $"Duel.RegisterEffect({v},tp)") return null;
        if (set.GetValueOrDefault("Type") != "EFFECT_TYPE_FIELD" || set.GetValueOrDefault("Label") != ac) return null;
        var duration = (set.GetValueOrDefault("Reset"), onCard) switch
        {
            ("RESET_PHASE+PHASE_END", false) => "turn",
            ("RESET_PHASE+PHASE_END+RESET_OPPO_TURN", false) => "opp_next",
            ("RESET_EVENT+RESETS_STANDARD", true) => "face_up",
            _ => null,
        };
        if (duration is null || duration == "face_up" && !set.ContainsKey("Range") || duration != "face_up" && set.ContainsKey("Range")) return null;
        string Joined(string reference) { try { return Body(reference, fns); } catch (Untranslatable) { return ""; } }
        bool Names(string reference, params string[] pars)
        {
            var (ps, _) = fns.Get(reference);
            if (!ps.SequenceEqual(pars)) return false;
            var body = Regex.Replace(Joined(reference), @"^local (\w+)=e:GetLabel\(\) return (.+)$", m => "return " + Regex.Replace(m.Groups[2].Value, $@"\b{m.Groups[1].Value}\b", "e:GetLabel()"));
            return body == "return c:IsOriginalCodeRule(e:GetLabel())" || body == "return re:GetHandler():IsOriginalCodeRule(e:GetLabel())";
        }
        var block = Block(DeclaredLockAction.BlockId).With("duration", duration);
        switch (set.GetValueOrDefault("Code"))
        {
            case "EFFECT_FORBIDDEN":
            {
                if (!hint || set.GetValueOrDefault("Property") != "EFFECT_FLAG_SET_AVAILABLE+EFFECT_FLAG_IGNORE_IMMUNE" || set.GetValueOrDefault("TargetRange") != "0xff,0xff"
                    || set.Keys.Except(["Type", "Property", "Code", "TargetRange", "Target", "Label", "Reset", "Range"]).Any() || set.GetValueOrDefault("Target") is not { } ban)
                    return null;
                // While face-up, cards already on the field when it was activated are not affected (Prohibition).
                var body = Joined(ban);
                var ok = duration == "face_up"
                    ? Regex.IsMatch(body, @"^local (\w+)=e:GetLabel\(\) return c:IsOriginalCodeRule\(\1\) and \(not c:IsOnField\(\) or c:GetRealFieldID\(\)>e:GetFieldID\(\)\)$")
                      && lines.Any(l => l is "Duel.AdjustInstantly(c)" or "Duel.AdjustInstantly(e:GetHandler())")
                    : Names(ban, "e", "c");
                return ok && fns.Get(ban).Params.SequenceEqual(["e", "c"]) ? block.With("what", "used") : null;
            }
            case "EFFECT_CANNOT_ACTIVATE" or "EFFECT_CANNOT_SPECIAL_SUMMON":
            {
                if (hint || set.GetValueOrDefault("Property") != "EFFECT_FLAG_PLAYER_TARGET") return null;
                var who = set.GetValueOrDefault("TargetRange")?.Replace(" ", "") switch { "1,1" => "both", "0,1" => "opponent", "1,0" => "you", _ => null };
                if (who is null) return null;
                var activate = set["Code"] == "EFFECT_CANNOT_ACTIVATE";
                var key = activate ? "Value" : "Target";
                if (set.Keys.Except(["Type", "Property", "Code", "TargetRange", key, "Label", "Reset", "Range"]).Any() || set.GetValueOrDefault(key) is not { } fn) return null;
                if (!(activate ? Names(fn, "e", "re", "tp") : Names(fn, "e", "c") || Names(fn, "e", "c", "sump", "sumtype", "sumpos", "targetp") || Names(fn, "e", "c", "sump", "sumtype", "sumpos", "targetp", "se")))
                    return null;
                return block.With("what", activate ? "activate" : "special").With("who", who);
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- coins and dice

    /// <summary>
    /// The DO blocks of an operation that tosses coins, rolls dice or uses what its target step declared, or null for
    /// any other operation. Untranslatable when it does so in a way the blocks don't do exactly.
    /// </summary>
    private static List<BlockInstance>? ChanceOperation(EffectEvent effect, string operation, string? target, Functions fns, TargetInfo targeting, CardData card)
    {
        var declaration = _declaration is { } d && d.Target == $"{fns.Id}:{target}" ? d : null;
        _declaration = null;
        var (pars, raw) = fns.Get(operation);
        var lines = Statements(JoinContinuations(raw).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)));
        var chance = lines.Any(l => l.Contains("Duel.TossCoin(", StringComparison.Ordinal) || l.Contains("Duel.TossDice(", StringComparison.Ordinal));
        if (declaration is null && !chance) return null;
        // Functions made here for the other readers, apart from the script's own.
        var own = fns with { ByName = new Dictionary<string, (string[] Params, List<string> Body)>(fns.ByName) };
        var blocks = new List<BlockInstance>();
        if (declaration is not null)
        {
            if (declaration.Kept is null) throw No("it doesn't keep what it declared the way the blocks keep it");
            if (chance) throw No("it declares and tosses a coin or rolls a die, which the blocks don't do together");
            blocks.Add(declaration.Block);
            if (DeclaredLockOf(lines, declaration, own) is { } locked) blocks.Add(locked);
            else blocks.AddRange(Operation(Synthetic(own, pars, WithDeclared(lines, declaration, own)), own, targeting, card));
        }
        else blocks.AddRange(ChanceSteps(lines, pars, own, targeting, card));
        // The blocks must fit together as the script does: a test compile of the effect.
        var check = PackSerializer.Clone(effect);
        check.Do = blocks.Select(PackSerializer.Clone).ToList();
        var data = PackSerializer.Clone(card);
        data.Id = 100000000;
        var compiled = ScriptCompiler.Compile(new CardProject { Card = data, Effects = [check] });
        if (compiled.Diagnostics.FirstOrDefault(x => x.Severity == Severity.Error) is { } error)
            throw No($"its {(chance ? "coin toss or die roll" : "declaration")} doesn't fit the blocks ({Short(error.Message)})");
        return blocks;
    }

    /// <summary>"if res==1 then Duel.Draw(tp,2,REASON_EFFECT)" and "else ... end" on one line: one statement per line.</summary>
    private static List<string> Statements(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            Match m;
            if ((m = Regex.Match(line, @"^((?:else)?if .+? then) (?!return end$)(.+?)( end)?$")).Success)
            {
                result.Add(m.Groups[1].Value);
                result.Add(m.Groups[2].Value);
                if (m.Groups[3].Success) result.Add("end");
            }
            else if ((m = Regex.Match(line, @"^else (.+?)( end)?$")).Success)
            {
                result.Add("else");
                result.Add(m.Groups[1].Value);
                if (m.Groups[2].Success) result.Add("end");
            }
            else result.Add(line);
        }
        return result;
    }

    /// <summary>How a line opens or closes a Lua block: +1 for "if ... then" and loops, -1 for "end".</summary>
    private static int Depth(string line) =>
        line == "end" ? -1
        : Regex.IsMatch(line, @"^if .+ then$") || Regex.IsMatch(line, @"^(?:for|while) .+ do$") || line.StartsWith("function", StringComparison.Ordinal) ? 1
        : 0;

    /// <summary>
    /// "local res=Duel.TossCoin(tp,1) if res==1 then ... else ... end", "local c1,c2,c3=Duel.TossCoin(tp,3) if c1+c2+c3<2 then
    /// return end ...", "local d=Duel.TossDice(tp,1) if d==1 or d==6 then ...": the toss or roll block, then a result
    /// block and the steps for each result.
    /// </summary>
    private static List<BlockInstance> ChanceSteps(List<string> lines, string[] pars, Functions fns, TargetInfo targeting, CardData card)
    {
        // What each result's steps need from the start: this card, the target.
        var prefix = new List<string>();
        var i = 0;
        var guarded = false;
        for (; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l == "local c=e:GetHandler()" || l == "local tc=Duel.GetFirstTarget()" && targeting.Filter is not null
                || l == "local p=Duel.GetChainInfo(0,CHAININFO_TARGET_PLAYER)" && targeting.Player is not null)
            {
                prefix.Add(l);
                continue;
            }
            // The check on the target before the toss, which the toss block makes the same way.
            if (targeting.Filter is not null && targeting.Count == 1 && Regex.IsMatch(l, @"^if not (?:tc or not )?tc:IsRelateToEffect\(e\) then return end$")) { guarded = true; continue; }
            if (targeting.Filter is not null && targeting.Count == 1 && Regex.IsMatch(l, @"^if (?:tc and )?tc:IsRelateToEffect\(e\) then$"))
            {
                // Only when it holds the whole rest of the operation.
                var depth = 0;
                var closes = -1;
                for (var j = i; j < lines.Count; j++)
                {
                    depth += Depth(lines[j]);
                    if (depth == 0) { closes = j; break; }
                }
                if (closes != lines.Count - 1) throw No("it checks its target in a way the toss block doesn't");
                lines = lines.Take(lines.Count - 1).ToList();
                guarded = true;
                continue;
            }
            break;
        }
        // The toss block tosses only while the target is there: the script must check it first too.
        if (targeting.Filter is not null && !guarded) throw No("it tosses a coin or rolls a die even when its target is gone");
        string? call = null;
        if (i < lines.Count && lines[i] == "Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_COIN)") i++;
        Match m;
        if (i < lines.Count && (m = Regex.Match(lines[i], @"^local (\w+)=Duel\.AnnounceCoin\(tp\)$")).Success) { call = m.Groups[1].Value; i++; }
        if (i >= lines.Count || !(m = Regex.Match(lines[i], @"^local ([\w,]+)=Duel\.Toss(Coin|Dice)\(tp,(\d)\)$")).Success)
            throw No($"it does something before its coin toss or die roll the blocks don't ({Short(i < lines.Count ? lines[i] : "")})");
        var coin = m.Groups[2].Value == "Coin";
        var count = Int(m.Groups[3].Value);
        var names = m.Groups[1].Value.Split(',');
        if (names.Length != count || call is not null && (!coin || count != 1) || count > (coin ? 5 : 3)) throw No("it tosses coins or rolls dice in a way the blocks don't");
        i++;
        var result = string.Join("+", names);
        // Every result there can be: heads counted, called right (1) or wrong (0), or the die's total.
        var all = call is not null ? new[] { 0, 1 } : coin ? Enumerable.Range(0, count + 1).ToArray() : Enumerable.Range(count, 5 * count + 1).ToArray();
        var blocks = new List<BlockInstance> { coin ? Block(TossCoinAction.BlockId).With("count", count).With("call", call is not null) : Block(RollDieAction.BlockId).With("count", count) };
        var text = _sourceCard?.Text ?? "";
        var bullets = text.Contains('●') && Regex.IsMatch(text, coin ? @"●\s*(?:\d+ )?(?:Heads?|Tails?):" : @"●\s*\d");

        HashSet<int> Values(string cond)
        {
            // "(dice1==1 or dice1==6)": the whole test in brackets.
            if (Regex.Match(cond.Trim(), @"^\((.*)\)$") is { Success: true } whole && Balanced(whole.Groups[1].Value)) cond = whole.Groups[1].Value;
            var any = new HashSet<int>();
            foreach (var alt in Regex.Split(cond, @"\s+or\s+"))
            {
                var both = all.ToHashSet();
                foreach (var atom in Regex.Split(alt, @"\s+and\s+"))
                {
                    var a = Regex.Replace(atom.Trim(), @"^\((.*)\)$", "$1");
                    HashSet<int> one;
                    if (call is not null)
                        one = a == $"{call}~={result}" || a == $"{result}~={call}" ? [1] : a == $"{call}=={result}" || a == $"{result}=={call}" ? [0]
                            : throw No($"its coin toss result is tested in a way the blocks don't ({Short(cond)})");
                    else
                    {
                        var c = Regex.Match(a, $@"^\(?{Regex.Escape(result)}\)?(==|~=|>=|<=|>|<)(\d+)$");
                        if (!c.Success) throw No($"its {(coin ? "coin toss" : "die roll")} result is tested in a way the blocks don't ({Short(cond)})");
                        var n = Int(c.Groups[2].Value);
                        one = all.Where(x => c.Groups[1].Value switch { "==" => x == n, "~=" => x != n, ">=" => x >= n, "<=" => x <= n, ">" => x > n, _ => x < n }).ToHashSet();
                    }
                    both.IntersectWith(one);
                }
                any.UnionWith(both);
            }
            return any;
        }

        BlockInstance? Gate(HashSet<int> set)
        {
            if (set.Count == all.Length) return null;
            if (set.Count == 0) throw No("one of its results can never happen");
            BlockInstance? gate = null;
            if (call is not null) gate = Block(CoinResultCondition.BlockId).With("result", set.Contains(1) ? "right" : "wrong");
            else if (coin && count == 1) gate = Block(CoinResultCondition.BlockId).With("result", set.Contains(1) ? "heads" : "tails");
            else if (coin && set.Count == 1) gate = set.Single() == 0
                ? Block(CoinResultCondition.BlockId).With("result", "tails_n").With("n", count)
                : Block(CoinResultCondition.BlockId).With("result", "heads_n").With("n", set.Single());
            else if (coin && set.Max() == count && set.Count == count - set.Min() + 1 && set.Min() > 0)
                gate = Block(CoinResultCondition.BlockId).With("result", "heads_min").With("n", set.Min());
            else if (!coin && count == 1)
            {
                gate = Block(DieResultCondition.BlockId);
                foreach (var x in set) gate.With("r" + x.ToString(CultureInfo.InvariantCulture), true);
            }
            if (gate is null) throw No($"its {(coin ? "coin toss" : "die roll")} results are ones the result blocks can't say");
            return gate.Linked(bullets ? ActionLink.Option : ActionLink.And);
        }

        void Case(HashSet<int> set, List<string> body)
        {
            // Steps that test or use the result again ("then if the result was 3 heads"): more than one result block says.
            if (body.Any(l => names.Append(call).OfType<string>().Any(n => Regex.IsMatch(l, $@"(?<![\w.:]){Regex.Escape(n)}\b"))))
                throw No("its steps use the coin toss or die roll result again in a way the blocks don't");
            var steps = body.Count == 0 ? [] : Operation(Synthetic(fns, pars, [.. prefix, .. body]), fns, targeting, card);
            if (steps.Count == 0) return;
            if (Gate(set) is { } gate) blocks.Add(gate);
            blocks.AddRange(steps);
        }

        var rest = lines.Skip(i).ToList();
        var seen = new HashSet<int>();
        for (var j = 0; j < rest.Count;)
        {
            var l = rest[j];
            // "if c1+c2+c3<2 then return end": the rest happens on the other results.
            if ((m = Regex.Match(l, @"^if (.+) then return end$")).Success)
            {
                var other = all.ToHashSet();
                other.ExceptWith(Values(m.Groups[1].Value));
                other.ExceptWith(seen);
                Case(other, rest.Skip(j + 1).ToList());
                break;
            }
            if ((m = Regex.Match(l, @"^if (.+) then$")).Success)
            {
                // if / elseif / else ... end: each arm is the results the arms before it didn't take.
                var taken = new HashSet<int>();
                var cond = m.Groups[1].Value;
                var arm = new List<string>();
                var depth = 0;
                int k;
                for (k = j + 1; k < rest.Count; k++)
                {
                    var x = rest[k];
                    if (depth == 0 && (x == "end" || x == "else" || Regex.IsMatch(x, @"^elseif .+ then$")))
                    {
                        var set = cond == "else" ? all.ToHashSet() : Values(cond);
                        set.ExceptWith(taken);
                        taken.UnionWith(set);
                        Case(set, arm);
                        arm = [];
                        if (x == "end") break;
                        cond = x == "else" ? "else" : Regex.Match(x, @"^elseif (.+) then$").Groups[1].Value;
                        continue;
                    }
                    depth += Depth(x);
                    arm.Add(x);
                }
                if (k >= rest.Count) throw No("its coin toss or die roll results are written in a way the blocks can't follow");
                j = k + 1;
                continue;
            }
            // "Duel.Damage(p,(d1+d2+d3)*100,REASON_EFFECT)": the result as an amount.
            if ((m = Regex.Match(l, $@"^Duel\.(Damage|Recover)\((1-tp|tp|p),\(?{Regex.Escape(result)}\)?\*(\d+),REASON_EFFECT\)$")).Success && !coin)
            {
                var who = m.Groups[2].Value == "p" ? targeting.Player : m.Groups[2].Value;
                if (who is null) throw No("it inflicts damage to a player the blocks can't say");
                blocks.Add(Block(LifeFromValueAction.BlockId).With("what", m.Groups[1].Value == "Damage" ? "damage" : "gain").With("who", who == "tp" ? "you" : "opponent")
                    .With("value", "roll").With("how", "per").With("amount", Int(m.Groups[3].Value)).With("zero_ok", true));
                j++;
                continue;
            }
            throw No($"it does something besides its coin toss or die roll results the blocks don't ({Short(l)})");
        }
        if (blocks.Count == 1) throw No("its coin toss or die roll results do nothing the blocks recognise");
        return blocks;
    }

    // ---------------------------------------------------------------- Toon monsters

    private const string ToonWorldCode = "15259703";

    /// <summary>"Cannot attack the turn it is Summoned" and the other Toon rules: one of the pieces official Toon monsters are made of.</summary>
    private static TranslatedPiece? ToonPieceOf(ScriptPiece piece, List<Dictionary<string, string>> sets, Functions fns)
    {
        if (ToonRuleOf(sets, fns) is not { } rule) return null;
        var effect = new EffectEvent { Label = piece.Title, When = Block("when.while_face_up"), Do = [Block(ToonRulesPassive.BlockId).With("rule", rule)] };
        return new TranslatedPiece { Piece = piece, Effect = effect };
    }

    private static string? ToonRuleOf(List<Dictionary<string, string>> sets, Functions fns)
    {
        string Joined(string reference) { try { return Body(reference, fns); } catch (Untranslatable) { return ""; } }
        bool Only(Dictionary<string, string> set, params string[] keys) => set.Keys.ToHashSet().SetEquals(keys);
        string Fn(string reference) => Regex.Replace(Joined(reference), @"\b(?:c\d+|s)\.", "s.");
        bool Filter(string reference, string want) => Fn(reference) == want;
        var codes = sets.Select(s => s.GetValueOrDefault("Code")).ToList();
        // "Cannot attack the turn it is Summoned": on each Summon, it can't attack until the end of the turn.
        if (sets.All(s => Only(s, "Type", "Code", "Operation") && s["Type"] == "EFFECT_TYPE_SINGLE+EFFECT_TYPE_CONTINUOUS" && s["Operation"] == sets[0]["Operation"])
            && Fn(sets[0]["Operation"]) is "local e1=Effect.CreateEffect(e:GetHandler()) e1:SetType(EFFECT_TYPE_SINGLE) e1:SetCode(EFFECT_CANNOT_ATTACK) e1:SetReset(RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END) e:GetHandler():RegisterEffect(e1)")
        {
            if (codes.ToHashSet().SetEquals(["EVENT_SUMMON_SUCCESS", "EVENT_SPSUMMON_SUCCESS", "EVENT_FLIP_SUMMON_SUCCESS"]) && codes.Count == 3) return "summon_turn";
            if (codes is ["EVENT_SPSUMMON_SUCCESS"]) return "spsummon_turn";
            return null;
        }
        if (sets.Count != 1) return null;
        var set = sets[0];
        // "If "Toon World" on the field is destroyed, destroy this card."
        if (Only(set, "Type", "Range", "Code", "Condition", "Operation") && set["Type"] == "EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS" && set["Range"] == "LOCATION_MZONE"
            && set["Code"] == "EVENT_LEAVE_FIELD" && Fn(set["Operation"]) == "Duel.Destroy(e:GetHandler(),REASON_EFFECT)"
            && Regex.Match(Fn(set["Condition"]), @"^return eg:IsExists\(([\w.]+),1,nil\)$") is { Success: true } con
            && Filter(fns.Prefix + con.Groups[1].Value[1..],
                $"return c:IsReason(REASON_DESTROY) and c:IsPreviousPosition(POS_FACEUP) and c:GetPreviousCodeOnField()=={ToonWorldCode} and c:IsPreviousLocation(LOCATION_ONFIELD)"))
            return "world_destroyed";
        // "While you control "Toon World" and your opponent controls no Toon monsters, this card can attack your opponent directly."
        if (Only(set, "Type", "Code", "Condition") && set["Type"] == "EFFECT_TYPE_SINGLE" && set["Code"] == "EFFECT_DIRECT_ATTACK"
            && Regex.Match(Fn(set["Condition"]), @"^return Duel\.IsExistingMatchingCard\(([\w.]+),tp,LOCATION_ONFIELD,0,1,nil\) and not Duel\.IsExistingMatchingCard\(([\w.]+),tp,0,LOCATION_MZONE,1,nil\)$") is { Success: true } dir
            && Filter(fns.Prefix + dir.Groups[1].Value[1..], $"return c:IsFaceup() and c:IsCode({ToonWorldCode})") && Filter(fns.Prefix + dir.Groups[2].Value[1..], "return c:IsFaceup() and c:IsType(TYPE_TOON)"))
            return "direct_world";
        // "You must pay 500 LP to declare an attack with this monster."
        if (Only(set, "Type", "Code", "Cost", "Operation") && set["Type"] == "EFFECT_TYPE_SINGLE" && set["Code"] == "EFFECT_ATTACK_COST"
            && fns.Get(set["Cost"]).Params.SequenceEqual(["e", "c", "tp"]) && Fn(set["Cost"]) == "return Duel.CheckLPCost(tp,500)" && Fn(set["Operation"]) == "Duel.PayLPCost(tp,500)")
            return "attack_cost";
        return null;
    }

    /// <summary>
    /// "Can attack your opponent directly, unless they control a Toon monster, in which case this card must target a Toon
    /// monster for its attacks": three effects of the older Toons (direct attack, the attack targets, no direct attack
    /// while they have one) as one rule. The first takes the rule; the other two need no block of their own.
    /// </summary>
    private static void ToonDirectUnless(TranslationResult result)
    {
        bool Has(ScriptPiece p, string code) => p.Code.Contains($"SetCode({code})", StringComparison.Ordinal);
        var direct = result.Pieces.FindIndex(p => Has(p.Piece, "EFFECT_DIRECT_ATTACK") && !Has(p.Piece, "EFFECT_CANNOT_DIRECT_ATTACK"));
        var targets = result.Pieces.FindIndex(p => Has(p.Piece, "EFFECT_CANNOT_SELECT_BATTLE_TARGET"));
        var noDirect = result.Pieces.FindIndex(p => Has(p.Piece, "EFFECT_CANNOT_DIRECT_ATTACK"));
        if (direct < 0 || targets < 0 || noDirect < 0) return;
        var pieces = new[] { direct, targets, noDirect }.Select(x => result.Pieces[x].Piece).ToList();
        var fns = new Functions(ParseFunctions(pieces[0]), pieces[0].Prefix, pieces[0].SourceId);
        string Joined(ScriptPiece p, string reference)
        {
            try { return Regex.Replace(Body(reference, new Functions(ParseFunctions(p), p.Prefix, p.SourceId)), @"\b(?:c\d+|s)\.", "s."); }
            catch (Untranslatable) { return ""; }
        }
        // Each piece's settings, one effect each.
        Dictionary<string, string>? Settings(ScriptPiece p)
        {
            var lines = p.Code.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (lines.Count < 3 || Regex.Match(lines[0], @"^local (\w+)=Effect\.CreateEffect\(c\)$") is not { Success: true } cm) return null;
            var v = cm.Groups[1].Value;
            if (lines[^1] != $"c:RegisterEffect({v})") return null;
            var set = new Dictionary<string, string>();
            foreach (var l in lines.Skip(1).SkipLast(1))
            {
                var sm = Regex.Match(l, $@"^{v}:Set(\w+)\((.*)\)$");
                if (!sm.Success) return null;
                set[sm.Groups[1].Value] = sm.Groups[2].Value;
            }
            return set;
        }
        var sets = pieces.Select(Settings).ToList();
        if (sets.Any(s => s is null || s.GetValueOrDefault("Type") != "EFFECT_TYPE_SINGLE" || !s.ContainsKey("Condition"))) return;
        var (d, t, n) = (sets[0]!, sets[1]!, sets[2]!);
        if (d.Count != 3 || t.Count != 4 || n.Count != 3 || !t.ContainsKey("Value")) return;
        // The opponent has a face-up Toon monster: the attack targets must be Toons, and there is no direct attack.
        var toons = Regex.Match(Joined(pieces[1], t["Condition"]), @"^return Duel\.IsExistingMatchingCard\(([\w.]+),tp,0,LOCATION_MZONE,1,nil\)$");
        if (!toons.Success || Joined(pieces[1], fns.Prefix + toons.Groups[1].Value[1..]) != "return c:IsFaceup() and c:IsType(TYPE_TOON)") return;
        if (Joined(pieces[2], n["Condition"]) != Joined(pieces[1], t["Condition"])) return;
        if (Joined(pieces[0], d["Condition"]) != "return not " + Joined(pieces[1], t["Condition"])["return ".Length..]) return;
        if (Joined(pieces[1], t["Value"]) != "return not c:IsType(TYPE_TOON) or c:IsFacedown()") return;
        var effect = new EffectEvent { Label = pieces[0].Title, When = Block("when.while_face_up"), Do = [Block(ToonRulesPassive.BlockId).With("rule", "direct_unless")] };
        result.Pieces[direct] = new TranslatedPiece { Piece = pieces[0], Effect = effect };
        result.Pieces[targets] = new TranslatedPiece { Piece = pieces[1], Ignored = true };
        result.Pieces[noDirect] = new TranslatedPiece { Piece = pieces[2], Ignored = true };
    }
}
