using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// How cards are picked and how "●" options are chosen, read from official scripts: groups picked with
/// Group.SelectSubGroup (aux.dncheck...), Group.RandomSelect and picks by the opponent, several targets and targets of two
/// kinds, "reveal 3 ..., your opponent randomly picks 1", and "Activate 1 of these effects" written with
/// aux.SelectFromOptions or Duel.SelectOption.
/// </summary>
public static partial class ScriptTranslator
{
    // ---------------------------------------------------------------- picks in operations

    /// <summary>The values a picked group gives the block that acts on it ("pick"="names", "chooser"="random"), by the group's local.</summary>
    [ThreadStatic] private static Dictionary<string, Dictionary<string, string>>? _picks;

    /// <summary>Locals holding the effect's targets that are still there ("local g=Duel.GetChainInfo(0,CHAININFO_TARGET_CARDS):Filter(...)").</summary>
    [ThreadStatic] private static HashSet<string>? _targetGroups;

    /// <summary>Locals holding all of the targets, before the ones still there are kept.</summary>
    [ThreadStatic] private static HashSet<string>? _allTargets;

    /// <summary>The cards a picked group's "up to the number of ..." counts, by the group's local.</summary>
    [ThreadStatic] private static Dictionary<string, CardFilter>? _pickCounts;

    /// <summary>"if ct==0 then return end": the counts the operation stops on when they are 0.</summary>
    [ThreadStatic] private static HashSet<string>? _countGuards;

    /// <summary>
    /// Every "up to the number of ..." of the effect: the count's filter function and places, which the activation must
    /// ask to be at least 1, as the blocks do (<see cref="VerifyCounts"/>).
    /// </summary>
    [ThreadStatic] private static List<(string Function, string Self, string Opp, string Exclude)>? _counts;

    private static void ResetPicks()
    {
        _picks = [];
        _targetGroups = [];
        _allTargets = [];
        _pickCounts = [];
        _countGuards = [];
    }

    /// <summary>Starts an effect: no "up to the number of ..." yet.</summary>
    private static void ResetCounts() => _counts = [];

    /// <summary>
    /// "local ct=Duel.GetMatchingGroupCount(s.cfilter,tp,LOCATION_MZONE,0,nil)" as a local of <paramref name="locals"/>,
    /// worked out right before it is used: the cards it counts. Null when it is no such count.
    /// </summary>
    private static (string Function, string Self, string Opp, string Exclude, CardFilter Filter)? CountOf(string name, List<KeyValuePair<string, string>> locals, Functions fns)
    {
        var value = locals.LastOrDefault(l => l.Key == name).Value;
        if (value is null || Regex.Match(value, @"^Duel\.GetMatchingGroupCount\(([\w.]+),tp,([\w+]+),([\w+]+),(nil|e:GetHandler\(\)|c)\)$") is not { Success: true } m) return null;
        var f = Filter(m.Groups[1].Value, fns);
        Locate(f, m.Groups[2].Value, m.Groups[3].Value);
        f.ExcludeThisCard = m.Groups[4].Value != "nil";
        return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, f);
    }

    /// <summary>
    /// "up to the number of ...": the blocks can't be activated while there is nothing to count. The official effect must
    /// ask the same, in its activation check or its condition; otherwise it isn't translated.
    /// </summary>
    private static void VerifyCounts(TargetInfo target, string? condition, Functions fns)
    {
        if (_counts is not { Count: > 0 } counts) return;
        var asked = new List<string>();
        if (target.Check is { } check) asked.AddRange(SplitTopLevelAnd(check).Select(t => Squash(t.Trim())));
        if (condition is not null && fns.ByName.ContainsKey(condition.Replace(fns.Prefix + ".", "")))
        {
            var body = string.Join(" ", fns.Get(condition).Body.Select(l => l.Trim()).Where(l => l.Length > 0 && l != "local c=e:GetHandler()"));
            if (Regex.Match(body, @"^return (.+)$") is { Success: true } rm) asked.AddRange(SplitTopLevelAnd(rm.Groups[1].Value).Select(t => Squash(t.Trim())));
        }
        foreach (var (fn, self, opp, exclude) in counts)
        {
            var places = $"{Regex.Escape(fn)},tp,{Regex.Escape(self)},{Regex.Escape(opp)}";
            var except = exclude == "nil" ? "nil" : @"(?:e:GetHandler\(\)|c)";
            if (!asked.Any(t => Regex.IsMatch(t, $@"^Duel\.(?:IsExistingMatchingCard\({places},1,{except}\)|GetMatchingGroupCount\({places},{except}\)(?:>0|~=0|>=1))$")))
                throw No("it picks up to the number of some cards without asking that there is 1 to count, which the blocks ask");
        }
    }

    private static bool IsTargetGroup(string name) => _targetGroups?.Contains(name) == true;

    private static readonly Dictionary<string, string> PickRules = new()
    {
        ["aux.dncheck"] = "names", ["aux.dabcheck"] = "attributes", ["aux.drccheck"] = "types", ["aux.dlvcheck"] = "levels",
    };

    /// <summary>The group rule aux.dncheck and the like check, and what Group.GetClassCount counts for it.</summary>
    private static readonly Dictionary<string, string> ClassCounts = new()
    {
        ["Card.GetCode"] = "names", ["Card.GetAttribute"] = "attributes", ["Card.GetRace"] = "types", ["Card.GetLevel"] = "levels",
    };

    /// <summary>
    /// An operation line that picks cards in a way of the selection blanks, or keeps the targets: true when it is taken
    /// care of. The group it picks is in <paramref name="groups"/> and its values in <see cref="_picks"/>, for the block
    /// that acts on it.
    /// </summary>
    private static bool SelectionLine(string line, List<string> body, int index, Dictionary<string, (CardFilter Filter, int Count)> groups, TargetInfo target, Functions fns)
    {
        _picks ??= [];
        Match m;
        // "local g=Duel.GetChainInfo(0,CHAININFO_TARGET_CARDS):Filter(Card.IsRelateToEffect,nil,e)": the targets still there.
        if ((m = Regex.Match(line, @"^local (\w+)=(?:Duel\.GetChainInfo\(0,CHAININFO_TARGET_CARDS\):Filter\(Card\.IsRelateTo(?:Effect,nil,e|Chain,nil)\)|Duel\.GetTargetsRelateToChain\(\))$")).Success
            && target.Filter is not null)
        {
            _targetGroups!.Add(m.Groups[1].Value);
            return true;
        }
        if ((m = Regex.Match(line, @"^local (\w+)=Duel\.GetChainInfo\(0,CHAININFO_TARGET_CARDS\)$")).Success && target.Filter is not null)
        {
            _allTargets!.Add(m.Groups[1].Value);
            return true;
        }
        if ((m = Regex.Match(line, @"^local (\w+)=(\w+):Filter\(Card\.IsRelateToEffect,nil,e\)$")).Success && _allTargets!.Contains(m.Groups[2].Value))
        {
            _targetGroups!.Add(m.Groups[1].Value);
            return true;
        }
        // "local tc=g:GetFirst()" for the 1 target.
        if ((m = Regex.Match(line, @"^local (\w+)=(\w+):GetFirst\(\)$")).Success && IsTargetGroup(m.Groups[2].Value) && target.Count == 1)
        {
            _targetGroups!.Add(m.Groups[1].Value);
            return true;
        }
        // "if #g==0 then return end", "if not tg then return end": the block acts only when there are cards, and nothing else follows.
        if ((m = Regex.Match(line, @"^if (?:not (\w+) or )?(?:#(\w+)==0|(\w+):GetCount\(\)(?:==0|<=0|<1)|not (\w+)) then return end$")).Success
            && new[] { 1, 2, 3, 4 }.Select(i => m.Groups[i]).Where(gr => gr.Success).Select(gr => gr.Value).Distinct().ToList() is [var guarded]
            && (IsTargetGroup(guarded) || groups.ContainsKey(guarded)) && OnlyStepLeft(body, index))
            return true;
        // "if g:GetClassCount(Card.GetCode)<2 then return end": the block checks there are enough to pick.
        if ((m = Regex.Match(line, @"^if (?:not \w+ or )?(\w+):GetClassCount\((Card\.Get\w+)\)(?:<\d+|==0) then return end$")).Success && groups.ContainsKey(m.Groups[1].Value)
            && ClassCounts.ContainsKey(m.Groups[2].Value) && OnlyStepLeft(body, index))
            return true;
        if ((m = Regex.Match(line, @"^if (\w+):GetClassCount\((Card\.Get\w+)\)(?:>=\d+|>\d+) then$")).Success && groups.ContainsKey(m.Groups[1].Value)
            && ClassCounts.ContainsKey(m.Groups[2].Value))
            return true;
        // "if sg then": the group picked, or nothing when the cards didn't fit.
        if ((m = Regex.Match(line, @"^if (\w+)(?: and (?:#\1>0|\1:GetCount\(\)>0))? then$")).Success && _picks.ContainsKey(m.Groups[1].Value)) return true;
        // "local sg=g:SelectSubGroup(tp,aux.dncheck,false,2,2)": cards with different names.
        if ((m = Regex.Match(line, @"^local (\w+)=(\w+):SelectSubGroup\((tp|1-tp),(aux\.\w+),false,(\d+),(\d+)\)$")).Success && groups.TryGetValue(m.Groups[2].Value, out var pool)
            && PickRules.TryGetValue(m.Groups[4].Value, out var rule))
        {
            var (min, max) = (Int(m.Groups[5].Value), Int(m.Groups[6].Value));
            if (min != max && min != 1) throw No("it picks a number of cards as a group the blocks can't say");
            Picked(m.Groups[1].Value, pool.Filter, min == max ? max : -max, ("pick", rule), m.Groups[3].Value == "1-tp" ? ("chooser", "opponent") : null);
            groups[m.Groups[1].Value] = (pool.Filter, min == max ? max : -max);
            return true;
        }
        // "local sg=g:SelectSubGroup(tp,s.fselect,false,1,3)" with a check of the script's own that the blocks write too:
        // "1 each from your hand, Deck, and field", "with the same name", "whose total Levels equal 8".
        if ((m = Regex.Match(line, $@"^local (\w+)=(\w+):SelectSubGroup\((tp|1-tp),({Regex.Escape(fns.Prefix)}\.\w+),false,(\d+),(\d+)(,tp)?\)$")).Success
            && groups.TryGetValue(m.Groups[2].Value, out var ownPool) && GroupRuleOf(m.Groups[4].Value, ownPool.Filter, m.Groups[7].Success, fns) is { } own)
        {
            var (min, max) = (Int(m.Groups[5].Value), Int(m.Groups[6].Value));
            if (min != max && min != 1) throw No("it picks a number of cards as a group the blocks can't say");
            var values = own.Select(kv => ((string, string)?)(kv.Key, kv.Value)).Append(m.Groups[3].Value == "1-tp" ? ("chooser", "opponent") : null).ToArray();
            Picked(m.Groups[1].Value, ownPool.Filter, min == max ? max : -max, values);
            groups[m.Groups[1].Value] = (ownPool.Filter, min == max ? max : -max);
            return true;
        }
        // "local sg=g:SelectWithSumEqual(tp,Card.GetLevel,8,1,99)": monsters whose total Levels equal 8.
        if ((m = Regex.Match(line, @"^local (\w+)=(\w+):SelectWithSumEqual\(tp,Card\.GetLevel,(\d+),(\d+),(\d+)\)$")).Success && groups.TryGetValue(m.Groups[2].Value, out var sumPool))
        {
            var (min, max) = (Int(m.Groups[4].Value), Int(m.Groups[5].Value));
            var any = min <= 1 && max >= 99;
            if (!any && min != max && min != 1) throw No("it picks a number of cards by their total Level the blocks can't say");
            var count = any ? 1 : min == max ? max : -max;
            Picked(m.Groups[1].Value, sumPool.Filter, count, ("pick", "level_sum"), ("sum", m.Groups[3].Value), any ? ("amount", "any") : null);
            groups[m.Groups[1].Value] = (sumPool.Filter, count);
            return true;
        }
        // "local sg=g:RandomSelect(tp,1)", "Duel.GetFieldGroup(tp,0,LOCATION_HAND):RandomSelect(tp,1)": cards picked at random.
        if ((m = Regex.Match(line, @"^local (\w+)=(\w+):RandomSelect\((?:tp|1-tp),(\d+)\)$")).Success && groups.TryGetValue(m.Groups[2].Value, out var randomPool))
        {
            // The blocks pick at random among the cards that can be moved: the official pool must be those cards too.
            var poolName = m.Groups[2].Value;
            var source = body.Take(index).Select(l => Regex.Match(l.Trim(), $@"^local {poolName}=Duel\.GetMatchingGroup\(([\w.]+),")).LastOrDefault(x => x.Success);
            var able = source is not null && (source.Groups[1].Value.StartsWith("Card.IsAbleTo", StringComparison.Ordinal)
                || fns.ByName.ContainsKey(source.Groups[1].Value.Replace(fns.Prefix + ".", "")) && FunctionTerms(source.Groups[1].Value, fns).Any(t => t.Contains("IsAbleTo", StringComparison.Ordinal)));
            Picked(m.Groups[1].Value, randomPool.Filter, Int(m.Groups[3].Value), ("chooser", "random"), able ? null : ("unfiltered", "true"));
            groups[m.Groups[1].Value] = (randomPool.Filter, Int(m.Groups[3].Value));
            return true;
        }
        if ((m = Regex.Match(line, @"^local (\w+)=Duel\.GetFieldGroup\(tp,([\w+]+),([\w+]+)\):RandomSelect\((?:tp|1-tp),(\d+)\)$")).Success)
        {
            var f = new CardFilter { CardType = FilterCardType.Any };
            Locate(f, m.Groups[2].Value, m.Groups[3].Value);
            Picked(m.Groups[1].Value, f, Int(m.Groups[4].Value), ("chooser", "random"), ("unfiltered", "true"));
            groups[m.Groups[1].Value] = (f, Int(m.Groups[4].Value));
            return true;
        }
        // "local g=Duel.SelectMatchingCard(1-tp,s.filter,tp,LOCATION_GRAVE,0,1,1,nil)": your opponent picks among your cards.
        if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectMatchingCard\(1-tp,([\w.()]+),tp,([\w+]+),0,(\d+),\4,(nil|e:GetHandler\(\)|c)(?:,e,tp|,e)?\)$")).Success)
        {
            var f = Filter(m.Groups[2].Value, fns);
            Locate(f, m.Groups[3].Value, "0");
            f.ExcludeThisCard = m.Groups[5].Value != "nil";
            Picked(m.Groups[1].Value, f, Int(m.Groups[4].Value), ("chooser", "opponent"), OpponentAsked(body, index));
            groups[m.Groups[1].Value] = (f, Int(m.Groups[4].Value));
            return true;
        }
        // The opponent picks (their own cards too: "your opponent sends 1 card from their Extra Deck to the GY").
        if ((m = Regex.Match(line, @"^local (\w+)=(\w+):Select\(1-tp,(\d+),\3,nil\)$")).Success && groups.TryGetValue(m.Groups[2].Value, out var theirPick))
        {
            Picked(m.Groups[1].Value, theirPick.Filter, Int(m.Groups[3].Value), ("chooser", "opponent"), OpponentAsked(body, index));
            groups[m.Groups[1].Value] = (theirPick.Filter, Int(m.Groups[3].Value));
            return true;
        }
        // "if ct==0 then return end": nothing to count, nothing to pick.
        if ((m = Regex.Match(line, @"^if (\w+)(?:==0|<=0|<1) then return end$")).Success && _opValues is { } countValues
            && CountOf(m.Groups[1].Value, countValues.Locals, fns) is not null && OnlyStepLeft(body, index))
        {
            _countGuards!.Add(m.Groups[1].Value);
            return true;
        }
        // "local g=Duel.SelectMatchingCard(tp,aux.TRUE,tp,LOCATION_MZONE,LOCATION_MZONE,1,ct,nil)": up to the number counted.
        if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectMatchingCard\(tp,([\w.()]+),tp,([\w+]+),([\w+]+),1,([A-Za-z_]\w*),(nil|e:GetHandler\(\)|c)(?:,([\w,+]+))?\)$")).Success
            && _opValues is { } ov && CountOf(m.Groups[5].Value, ov.Locals, fns) is { } counted)
        {
            // Counted right before it is used, and the operation stops when there is nothing: as the blocks count.
            if (!_countGuards!.Contains(m.Groups[5].Value) || ov.Stamps.GetValueOrDefault(m.Groups[5].Value, -1) != ov.Blocks)
                throw No("it picks up to a number it counts in a way the blocks don't");
            var f = m.Groups[7].Success ? FilterWith(m.Groups[2].Value, m.Groups[7].Value, fns) : Filter(m.Groups[2].Value, fns);
            Locate(f, m.Groups[3].Value, m.Groups[4].Value);
            f.ExcludeThisCard = m.Groups[6].Value != "nil";
            Picked(m.Groups[1].Value, f, 1, ("amount", "number_of"));
            _pickCounts![m.Groups[1].Value] = counted.Filter;
            (_counts ??= []).Add((counted.Function, counted.Self, counted.Opp, counted.Exclude));
            groups[m.Groups[1].Value] = (f, 1);
            return true;
        }
        // "Destroy up to 2 ...", "any number of": picked from the matching cards.
        if ((m = Regex.Match(line, @"^local (\w+)=Duel\.SelectMatchingCard\(tp,([\w.()]+),tp,([\w+]+),([\w+]+),1,(\d+),(nil|e:GetHandler\(\)|c)(?:,e,tp|,e)?\)$")).Success && Int(m.Groups[5].Value) > 1)
        {
            var f = Filter(m.Groups[2].Value, fns);
            Locate(f, m.Groups[3].Value, m.Groups[4].Value);
            f.ExcludeThisCard = m.Groups[6].Value != "nil";
            var max = Int(m.Groups[5].Value);
            Picked(m.Groups[1].Value, f, max >= 99 ? 1 : -max, max >= 99 ? ("amount", "any") : ("upto", "true"));
            groups[m.Groups[1].Value] = (f, max >= 99 ? 1 : -max);
            return true;
        }
        if ((m = Regex.Match(line, @"^local (\w+)=(\w+):Select\(tp,1,(\d+),nil\)$")).Success && groups.TryGetValue(m.Groups[2].Value, out var upPool) && Int(m.Groups[3].Value) > 1)
        {
            var max = Int(m.Groups[3].Value);
            Picked(m.Groups[1].Value, upPool.Filter, max >= 99 ? 1 : -max, max >= 99 ? ("amount", "any") : ("upto", "true"));
            groups[m.Groups[1].Value] = (upPool.Filter, max >= 99 ? 1 : -max);
            return true;
        }
        return false;
    }

    /// <summary>
    /// A script's own check of a picked group ("function s.fselect(g) return g:GetClassCount(Card.GetCode)==1 end") as the
    /// selection blanks: one group rule, and "1 each" from the pool's places (or from each player). Null when it checks
    /// anything else, or the same thing in a way the blocks write differently.
    /// </summary>
    private static Dictionary<string, string>? GroupRuleOf(string reference, CardFilter pool, bool withPlayer, Functions fns)
    {
        var (args, body) = fns.Get(reference);
        if (args.Length != (withPlayer ? 2 : 1) || withPlayer && args[1] != "tp") return null;
        var text = string.Join(" ", body.Select(l => l.Trim()).Where(l => l.Length > 0));
        if (Regex.Match(text, @"^return (.+)$") is not { Success: true } rm) return null;
        var g = Regex.Escape(args[0]);
        var size = $@"(?:#{g}|{g}:GetCount\(\))";
        string? pick = null, sum = null;
        var places = new List<Zone>();
        var players = new HashSet<string>();
        foreach (var raw in SplitTopLevelAnd(rm.Groups[1].Value))
        {
            var t = Squash(raw.Trim());
            Match m;
            string? rule = null;
            if ((m = Regex.Match(t, $@"^aux\.(dncheck|dabcheck|drccheck|dlvcheck)\({g}\)$")).Success) rule = PickRules["aux." + m.Groups[1].Value];
            else if ((m = Regex.Match(t, $@"^{g}:GetClassCount\((Card\.Get(?:Code|Attribute|Race|Level))\)=={size}$")).Success) rule = ClassCounts[m.Groups[1].Value];
            else if ((m = Regex.Match(t, $@"^{g}:GetClassCount\(Card\.Get(Code|Level)\)==1$")).Success) rule = m.Groups[1].Value == "Code" ? "same_name" : "same_level";
            else if ((m = Regex.Match(t, $@"^aux\.SameValueCheck\({g},Card\.Get(Attribute|Race)\)$")).Success) rule = m.Groups[1].Value == "Attribute" ? "same_attribute" : "same_type";
            else if ((m = Regex.Match(t, $@"^{g}:GetSum\(Card\.Get(Level|Attack)\)(==|<=)(\d+)$")).Success)
            {
                rule = (m.Groups[1].Value, m.Groups[2].Value) switch { ("Level", "==") => "level_sum", ("Level", _) => "level_max", ("Attack", "<=") => "atk_max", _ => null };
                if (rule is null) return null;
                sum = m.Groups[3].Value;
            }
            else if ((m = Regex.Match(t, $@"^{g}:FilterCount\(Card\.IsLocation,nil,(LOCATION_\w+)\)<=1$")).Success)
            {
                var f = new CardFilter();
                Locate(f, m.Groups[1].Value, "0");
                places.Add(f.Zones);
                continue;
            }
            else if (withPlayer && (m = Regex.Match(t, $@"^{g}:FilterCount\(Card\.IsControler,nil,(tp|1-tp)\)<=1$")).Success) { players.Add(m.Groups[1].Value); continue; }
            if (rule is null || pick is not null) return null;
            pick = rule;
        }
        var values = new Dictionary<string, string>();
        if (pick is not null) values["pick"] = pick;
        if (sum is not null) values["sum"] = sum;
        // "1 each": exactly the places the blocks keep apart, or both players for one place on both sides.
        if (places.Count > 0 || players.Count > 0)
        {
            var parts = Selection.PlacesOf(pool.Zones);
            var byPlace = players.Count == 0 && places.Count == parts.Count && places.All(parts.Contains) && places.Distinct().Count() == places.Count;
            var byPlayer = places.Count == 0 && players.Count == 2 && parts.Count == 1 && pool.Owner == CardOwner.Either;
            if (!byPlace && !byPlayer) return null;
            values["each_zone"] = "true";
        }
        return values.Count == 0 ? null : values;
    }

    /// <summary>
    /// "if g:GetCount()>0 and Duel.SelectYesNo(1-tp,...) then": the opponent decides whether they pick at all, which only
    /// the block where the opponent acts says ("your opponent can Special Summon ...").
    /// </summary>
    private static (string, string)? OpponentAsked(List<string> body, int index) =>
        body.Take(index).Any(l => l.Contains("Duel.SelectYesNo(1-tp,", StringComparison.Ordinal)) ? ("asked_opponent", "true") : null;

    /// <summary>
    /// After a "return" guard, at most one more step of the operation: the block it goes with checks the same, and
    /// no step after it would have been skipped.
    /// </summary>
    private static bool OnlyStepLeft(List<string> body, int index) =>
        body.Skip(index + 1).Select(l => l.Trim()).Count(l => l.StartsWith("@TEMP ", StringComparison.Ordinal)
            || l.StartsWith("Duel.", StringComparison.Ordinal) && !Regex.IsMatch(l, @"^Duel\.(?:Hint|HintSelection|ConfirmCards|ShuffleHand|ShuffleDeck|BreakEffect)\(")
            || Regex.IsMatch(l, @"^if .*Duel\.\w+\(.*\)(?:~=0|>0) then$")) <= 1;

    private static void Picked(string name, CardFilter filter, int count, params (string Key, string Value)?[] values)
    {
        var set = new Dictionary<string, string>();
        foreach (var v in values) if (v is { } kv) set[kv.Key] = kv.Value;
        _picks![name] = set;
    }

    /// <summary>
    /// The block acting on a picked group or on the targets takes the pick's values; untranslatable when it has no such
    /// blanks, or when it would pick differently ("up to 2" Special Summons need 2 free zones in the blocks).
    /// </summary>
    private static void ApplyPicks(string what, bool targeted, TargetInfo target, BlockInstance block)
    {
        Dictionary<string, string>? values = null;
        if (!targeted && _picks?.TryGetValue(what, out var picked) == true) values = picked;
        if (targeted && target.Pick is { } targetPick) values = targetPick;
        if (values is null) return;
        var def = BlockRegistry.Find(block.BlockId)!;
        // "up to the number of ...": the cards counted.
        var counted = targeted ? target.CountFilter : _pickCounts?.GetValueOrDefault(what);
        if (counted is not null)
        {
            if (def.Param("count_cards") is null) throw No("it picks up to the number of some cards, which that block can't");
            block.Filters["count_cards"] = counted;
        }
        foreach (var (key, value) in values)
        {
            if (key == "upto")
            {
                // "Special Summon up to 2": official cards cap it by the free zones, the block asks for 2.
                if (block.BlockId == "do.special_summon") throw No("it Special Summons up to a number of monsters, which the block checks differently");
                continue;
            }
            if (key == "asked_opponent")
            {
                if (block.BlockId != "do.opponent_special_summons") throw No("your opponent decides whether to pick the cards, which that block can't say");
                continue;
            }
            // At random among every card there: only the same when the block takes any card (it destroys).
            if (key == "unfiltered")
            {
                if (block.BlockId != "do.destroy") throw No("it picks at random among cards that might not be moved, where the blocks pick among cards that can be");
                continue;
            }
            // "Your opponent Special Summons 1 monster from their hand": that block is the opponent's pick.
            if (key == "chooser" && value == "opponent" && block.BlockId == "do.opponent_special_summons") continue;
            if (def.Param(key) is not { } spec || spec.Type == ParamType.Choice && spec.Choices.All(c => c.Value != value))
                throw No($"it picks its cards in a way that block can't ({key})");
            block.Values[key] = value;
        }
        if (values.ContainsKey("amount")) block.Values.Remove("mode");
    }

    // ---------------------------------------------------------------- targets

    /// <summary>
    /// A target step line for several targets: "up to 3" (Duel.SelectTarget(tp,f,...,1,3,...)), a second kind of target
    /// ("local g2=Duel.SelectTarget(...)" then "g1:Merge(g2)"), and targets picked as a group
    /// ("g:SelectSubGroup(tp,aux.dncheck,false,2,2)" then "Duel.SetTargetCard(sg)"). True when it is taken care of.
    /// </summary>
    private static bool SelectionTargetLine(string line, TargetInfo info, Functions fns, OperationValues values)
    {
        Match m;
        if ((m = Regex.Match(line, @"^(?:(?:local\s+)?(\w+)\s*=\s*)?Duel\.SelectTarget\(tp,([\w.]+),tp,([\w+]+),([\w+]+),(\d+),(\d+),(nil|e:GetHandler\(\)|c)(?:,e,tp)?\)$")).Success)
        {
            var (min, max) = (Int(m.Groups[5].Value), Int(m.Groups[6].Value));
            var f = Filter(m.Groups[2].Value, fns);
            Locate(f, m.Groups[3].Value, m.Groups[4].Value);
            f.ExcludeThisCard = m.Groups[7].Value != "nil";
            if (info.Filter is not null)
            {
                // The second kind of target, merged with the first.
                if (min != max || info.Second is not null || info.Count <= 0) throw No("it targets several kinds of cards in a way the blocks can't");
                info.Second = (f, max);
                info.SecondVar = m.Groups[1].Value;
                return true;
            }
            if (min == max) return false;
            if (min != 1) throw No("it targets a number of cards the blocks can't say");
            info.Filter = f;
            info.Count = -max;
            info.FirstVar = m.Groups[1].Value;
            return true;
        }
        // "Duel.SelectTarget(tp,f,tp,0,LOCATION_ONFIELD,1,ct,nil)" with "local ct=Duel.GetMatchingGroupCount(...)": up to the number counted.
        if ((m = Regex.Match(line, @"^(?:(?:local\s+)?(\w+)\s*=\s*)?Duel\.SelectTarget\(tp,([\w.]+),tp,([\w+]+),([\w+]+),1,([A-Za-z_]\w*),(nil|e:GetHandler\(\)|c)(?:,e,tp)?\)$")).Success
            && info.Filter is null && CountOf(m.Groups[5].Value, values.Locals, fns) is { } counted)
        {
            var f = Filter(m.Groups[2].Value, fns);
            Locate(f, m.Groups[3].Value, m.Groups[4].Value);
            f.ExcludeThisCard = m.Groups[6].Value != "nil";
            info.Filter = f;
            info.Count = 1;
            info.Pick = new Dictionary<string, string> { ["amount"] = "number_of" };
            info.CountFilter = counted.Filter;
            (_counts ??= []).Add((counted.Function, counted.Self, counted.Opp, counted.Exclude));
            return true;
        }
        if ((m = Regex.Match(line, @"^(\w+):Merge\((\w+)\)$")).Success && info.Second is not null && m.Groups[2].Value == info.SecondVar) return true;
        // "local sg=g:SelectSubGroup(tp,aux.dncheck,false,2,2)" with g the cards that can be targeted, then "Duel.SetTargetCard(sg)".
        if ((m = Regex.Match(line, $@"^local (\w+)=(\w+):SelectSubGroup\(tp,(aux\.\w+|{Regex.Escape(fns.Prefix)}\.\w+),false,(\d+),(\d+)(,tp)?\)$")).Success
            && (PickRules.ContainsKey(m.Groups[3].Value) && !m.Groups[6].Success || m.Groups[3].Value.StartsWith(fns.Prefix + ".", StringComparison.Ordinal))
            && values.Locals.FirstOrDefault(l => l.Key == m.Groups[2].Value).Value is { } pool
            && Regex.Match(pool, @"^Duel\.GetMatchingGroup\(([\w.]+),tp,([\w+]+),([\w+]+),(nil|e:GetHandler\(\)|c)(?:,e(?:,tp)?)?\)(:Filter\(Card\.IsCanBeEffectTarget,nil,e\))?$") is { Success: true } pm
            && info.Filter is null)
        {
            var (min, max) = (Int(m.Groups[4].Value), Int(m.Groups[5].Value));
            if (min != max && min != 1) throw No("it targets a number of cards as a group the blocks can't say");
            // The cards must be ones that can be targeted, in the filter or with Card.IsCanBeEffectTarget.
            if (!pm.Groups[5].Success && !FunctionTerms(pm.Groups[1].Value, fns).Contains("c:IsCanBeEffectTarget(e)"))
                throw No("it targets a group of cards that aren't checked to be targetable");
            var f = Filter(pm.Groups[1].Value, fns);
            Locate(f, pm.Groups[2].Value, pm.Groups[3].Value);
            f.ExcludeThisCard = pm.Groups[4].Value != "nil";
            // aux.dncheck, or the script's own check of the group the blocks write too.
            var rules = PickRules.TryGetValue(m.Groups[3].Value, out var rule) ? new Dictionary<string, string> { ["pick"] = rule }
                : GroupRuleOf(m.Groups[3].Value, f, m.Groups[6].Success, fns) ?? throw No("it targets a group of cards it checks in a way the blocks can't");
            info.Filter = f;
            info.Count = min == max ? max : -max;
            info.Pick = rules;
            info.FirstVar = m.Groups[1].Value;
            return true;
        }
        if ((m = Regex.Match(line, @"^Duel\.SetTargetCard\((\w+)\)$")).Success && info.Pick is not null && m.Groups[1].Value == info.FirstVar) return true;
        return false;
    }

    /// <summary>
    /// "Target 1 monster you control and 1 card your opponent controls; destroy them": the two kinds of targets, and one
    /// action on all of them. Null when the effect doesn't target two kinds.
    /// </summary>
    private static BlockInstance? PairTargets(TargetInfo target, List<BlockInstance> blocks)
    {
        if (target.Second is not { } second) return null;
        if (blocks is not [var only] || only.Values.GetValueOrDefault("target") != "true" || only.Values.GetValueOrDefault("count") != target.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw No("it does something to its two kinds of targets the blocks can't say");
        var action = only.BlockId switch
        {
            "do.destroy" => "destroy", "do.banish" => "banish", "do.return_to_hand" or "do.add_to_hand" => "hand", "do.shuffle_into_deck" => "deck", "do.send_to_gy" => "grave",
            _ => throw No("it does something to its two kinds of targets the blocks can't say"),
        };
        if (only.Values.Any(v => v.Key is "facedown" or "until" && v.Value is "true" or "end_phase")) throw No("it does something to its two kinds of targets the blocks can't say");
        if (!PairTargetAction.Disjoint(target.Filter!, second.Filter)) throw No("its two kinds of targets could be the same cards, which the blocks keep apart");
        return Block(PairTargetAction.BlockId).With("count", target.Count).With("cards", target.Filter!).With("count2", second.Count).With("cards2", second.Filter).With("action", action).Linked(only.Link);
    }

    // ---------------------------------------------------------------- reveal, the opponent picks

    /// <summary>
    /// Pantheism of the Monarchs, You're in Danger!: "reveal 3 cards from your Deck, your opponent (randomly) picks 1 for you
    /// to add to your hand, and you shuffle the rest into the Deck", as one operation. Null for any other.
    /// </summary>
    private static List<BlockInstance>? RevealPickOf(List<string> body, Functions fns)
    {
        // The hints, the shuffles and showing yourself the picked card change nothing.
        var lines = body.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Duel.Hint(", StringComparison.Ordinal) && l is not ("Duel.ShuffleDeck(tp)" or "local c=e:GetHandler()")
            && !Regex.IsMatch(l, @"^Duel\.ConfirmCards\(tp,\w+\)$")).ToList();
        var whole = string.Join(" ", lines);
        var m = Regex.Match(whole,
            @"^local (\w+)=Duel\.GetMatchingGroup\(([\w.]+),tp,LOCATION_DECK,0,nil(?:,e,tp)?\) " +
            @"if (?:\1:GetCount\(\)>=(\d+)|#\1>=(\d+)|\1:GetClassCount\(Card\.GetCode\)>=(\d+)) then " +
            @"local (\w+)=\1:(?:Select\(tp,(\d+),\7,nil\)|SelectSubGroup\(tp,aux\.dncheck,false,(\d+),\8\))(?: if \6 then)? " +
            @"Duel\.ConfirmCards\(1-tp,\6\) " +
            @"local (\w+)=\6:(RandomSelect\(1-tp,1\)|Select\(1-tp,1,1,nil\))(:GetFirst\(\))? " +
            @"(?:\9:GetFirst\(\):SetStatus\(STATUS_TO_HAND_WITHOUT_CONFIRM,true\) |\9:SetStatus\(STATUS_TO_HAND_WITHOUT_CONFIRM,true\) )?" +
            @"(Duel\.SendtoHand\(\9,nil,REASON_EFFECT\)|Duel\.SpecialSummon\(\9,0,tp,tp,false,false,POS_FACEUP\)|Duel\.SSet\(tp,\9(?:,tp,false)?\))" +
            @"(?: end)?(?: end)?$");
        if (!m.Success) return null;
        var n = new[] { 3, 4, 5, 7, 8 }.Select(i => m.Groups[i]).Where(g => g.Success).Select(g => Int(g.Value)).Distinct().ToList();
        if (n is not [var count] || count is < 2 or > 5) return null;
        var names = m.Groups[8].Success;
        if (names != m.Groups[5].Success && m.Groups[5].Success) return null;
        var f = Filter(m.Groups[2].Value, fns);
        f.Owner = CardOwner.You;
        f.Zones = Model.Zone.Deck;
        var then = m.Groups[12].Value.StartsWith("Duel.SendtoHand", StringComparison.Ordinal) ? "hand" : m.Groups[12].Value.StartsWith("Duel.SpecialSummon", StringComparison.Ordinal) ? "summon" : "set";
        if (then == "summon" && f.CardType == FilterCardType.Any) f.CardType = FilterCardType.Monster;
        return [Block(RevealPickAction.BlockId).With("count", count).With("cards", f).With("names", names)
            .With("how", m.Groups[10].Value.StartsWith("RandomSelect", StringComparison.Ordinal) ? "random" : "chooses").With("then", then)];
    }

    // ---------------------------------------------------------------- "Activate 1 of these effects"

    /// <summary>Statements one per line: a call written over several lines ("aux.SelectFromOptions(tp,\n{...},\n{...})") is one.</summary>
    private static List<string> OptionStatements(IEnumerable<string> body)
    {
        var result = new List<string>();
        var pending = "";
        foreach (var raw in body)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal)) continue;
            pending = pending.Length == 0 ? line : pending + (pending.EndsWith('(') || pending.EndsWith('{') || pending.EndsWith(',') ? "" : " ") + line;
            if (ParenDepth(pending) > 0 || pending.EndsWith(',')) continue;
            result.Add(pending);
            pending = "";
        }
        if (pending.Length > 0) result.Add(pending);
        // "local op = e:GetLabel()" and "if op == 1 then" as most scripts write them.
        return JoinContinuations(result).Select(s => Regex.Replace(Regex.Replace(s.Trim(), @"^local (\w+)\s*=\s*", "local $1="), @"\s*==\s*", "==")).ToList();
    }

    private static int ParenDepth(string s) => s.Count(ch => ch is '(' or '{') - s.Count(ch => ch is ')' or '}');

    /// <summary>How a statement opens or closes a block: +1 for "if ... then" and "for/while ... do", -1 for "end".</summary>
    private static int Nesting(string s)
    {
        if (s == "end") return -1;
        if (s.StartsWith("if ", StringComparison.Ordinal) && s.EndsWith(" then", StringComparison.Ordinal)) return 1;
        if ((s.StartsWith("for ", StringComparison.Ordinal) || s.StartsWith("while ", StringComparison.Ordinal)) && s.EndsWith(" do", StringComparison.Ordinal)) return 1;
        return 0;
    }

    /// <summary>
    /// "if op==1 then ... elseif op==2 then ... else ... end" (or one "if op==1 then ... end" per option, one after
    /// another) at the top of a body: each option's statements by the value it is chosen with (else: -1), and the
    /// statements around them. Null when there is no such block.
    /// </summary>
    private static (Dictionary<int, List<string>> Branches, List<string> Before, List<string> After)? Branches(List<string> body, string var)
    {
        var start = body.FindIndex(s => Regex.IsMatch(s, $@"^if {var}==\d+ then$"));
        if (start < 0) return null;
        var branches = new Dictionary<int, List<string>>();
        var i = start;
        while (i < body.Count && Regex.Match(body[i], $@"^if {var}==(\d+) then$") is { Success: true } im)
        {
            var current = Int(im.Groups[1].Value);
            if (!branches.TryAdd(current, [])) return null;
            var depth = 1;
            for (i++; i < body.Count; i++)
            {
                var s = body[i];
                if (depth == 1 && Regex.Match(s, $@"^elseif {var}==(\d+) then$") is { Success: true } em)
                {
                    current = Int(em.Groups[1].Value);
                    if (!branches.TryAdd(current, [])) return null;
                    continue;
                }
                if (depth == 1 && s == "else")
                {
                    current = -1;
                    if (!branches.TryAdd(current, [])) return null;
                    continue;
                }
                depth += Nesting(s);
                if (depth == 0) break;
                branches[current].Add(s);
            }
            if (i >= body.Count) return null;
            i++;
        }
        return (branches, body.Take(start).ToList(), body.Skip(i).ToList());
    }

    private static readonly string[] OptionArgs = ["e", "tp", "eg", "ep", "ev", "re", "r", "rp", "chk", "chkc"];

    /// <summary>A synthetic function of the card holding one option's statements, so the usual steps read it.</summary>
    private static string OptionFunction(Functions fns, string name, List<string> body)
    {
        fns.ByName[name] = (OptionArgs, body);
        return $"{fns.Prefix}.{name}";
    }

    /// <summary>"s.desop(e,tp,eg,ep,ev,re,r,rp)" as an option's whole step: that function's statements.</summary>
    private static List<string> Inline(List<string> statements, Functions fns)
    {
        if (statements is [var call] && Regex.Match(call, $@"^({Regex.Escape(fns.Prefix)}\.\w+)\(e,tp,eg,ep,ev,re,r,rp\)$") is { Success: true } cm)
            return OptionStatements(fns.Get(cm.Groups[1].Value).Body);
        return statements;
    }

    /// <summary>
    /// "Activate 1 of these effects": the options of an effect written with aux.SelectFromOptions or Duel.SelectOption,
    /// each one's target step and operation read as the usual blocks, linked with "●". Chosen as the effect resolves
    /// ("apply 1 of these effects", "your opponent chooses"), or with a check of their own ("● If you control no
    /// monsters: ..."), or once per turn each, the options get a "Choose 1 of these effects" block first. Null when the
    /// effect has no options.
    /// </summary>
    private static List<BlockInstance>? OptionsOf(Dictionary<string, string> set, Functions fns, CardData card)
    {
        if (!set.TryGetValue("Operation", out var opRef)) return null;
        var tgBody = set.TryGetValue("Target", out var tgRef) ? OptionStatements(fns.Get(tgRef).Body) : [];
        var opBody = OptionStatements(fns.Get(opRef).Body);
        static bool Chooses(string s) => s.Contains("aux.SelectFromOptions(", StringComparison.Ordinal) || s.Contains("Duel.SelectOption(", StringComparison.Ordinal);
        var inTarget = tgBody.Any(Chooses);
        var inOperation = opBody.Any(Chooses);
        if (inTarget == inOperation) return null;
        var choice = Choice(inTarget ? tgBody : opBody);
        if (choice is null) return null;
        var (who, options, bvars, choiceStart, choiceAt, opVar) = choice.Value;
        List<string> before;
        Dictionary<int, List<string>> targets = [], steps;
        if (inTarget)
        {
            // The target step: the checks, the choice kept as the label, then each option's targeting.
            var rest = tgBody.Skip(choiceAt + 1).ToList();
            if (rest.Count == 0 || rest[0] != $"e:SetLabel({opVar})") return null;
            rest.RemoveAt(0);
            if (rest.Count > 0)
            {
                // "local g=nil" and the hint that shows the opponent the option may come first.
                if (Branches(rest, opVar) is not { } tb || tb.After.Count > 0
                    || tb.Before.Any(s => !Regex.IsMatch(s, @"^local \w+=nil$|^Duel\.Hint\(HINT_OPSELECTED,1-tp,"))) return null;
                targets = tb.Branches;
            }
            before = tgBody.Take(choiceStart).ToList();
            // The operation: the same choice read back from the label.
            var labelVar = "e:GetLabel\\(\\)";
            var opLines = opBody.Where(s => s != "local c=e:GetHandler()").ToList();
            if (opLines.FindIndex(s => Regex.IsMatch(s, @"^local \w+=e:GetLabel\(\)$")) is var at and >= 0)
            {
                labelVar = Regex.Match(opLines[at], @"^local (\w+)=").Groups[1].Value;
                opLines.RemoveAt(at);
            }
            if (Branches(opLines, labelVar) is not { } ob || ob.After.Count > 0) return null;
            // A check before the option's steps ("if Duel.GetLocationCount(tp,LOCATION_MZONE)<=0 then return end") is each option's own.
            steps = ob.Branches.ToDictionary(b => b.Key, b => ob.Before.Concat(Inline(b.Value, fns)).ToList());
        }
        else
        {
            var rest = opBody.Skip(choiceAt + 1).ToList();
            if (Branches(rest, opVar) is not { } ob || ob.Before.Count > 0 || ob.After.Count > 0) return null;
            steps = ob.Branches;
            before = opBody.Take(choiceStart).ToList();
            // The target step only asks that some option can be applied, with the same checks.
            if (tgBody.Any(s => !(s.StartsWith("Duel.SetOperationInfo(", StringComparison.Ordinal) || s.StartsWith("if chk==0 then return ", StringComparison.Ordinal)
                    || Regex.IsMatch(s, @"^local b\d+=") || s == "local c=e:GetHandler()" || s.StartsWith("e:SetCategory(", StringComparison.Ordinal))))
                throw No("its target step does something beside asking that an option can be applied");
            var targetChecks = tgBody.Where(s => Regex.IsMatch(s, @"^local b\d+=")).ToDictionary(s => s[6..s.IndexOf('=')], s => s[(s.IndexOf('=') + 1)..]);
            var asked = tgBody.Select(s => Regex.Match(s, @"^if chk==0 then return (.+) end$")).FirstOrDefault(x => x.Success)?.Groups[1].Value ?? "true";
            var offered = options.All(o => o.Check is null) ? "true" : string.Join(" or ", options.Select(o => o.Check ?? "true"));
            if (asked.Replace("(", "").Replace(")", "") != offered || options.Any(o => o.Check is { } b && targetChecks.GetValueOrDefault(b) != bvars[b]))
                throw No("its activation check isn't that some option can be applied");
        }
        // Statements before the choice: "local b1=...", and nothing else.
        for (var i = 0; i < before.Count; i++)
        {
            var s = before[i];
            // "if chkc then ... end": the targets' own check, which the blocks write.
            if (s == "if chkc then")
            {
                for (var depth = 1; depth > 0 && ++i < before.Count;) depth += Nesting(before[i]);
                continue;
            }
            if (s is "local c=e:GetHandler()" || s.StartsWith("if chkc then return ", StringComparison.Ordinal) || Regex.IsMatch(s, $@"^local {opVar}=(?:0|nil)$")
                || s.StartsWith("Duel.Hint(HINT_SELECTMSG,", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(s, @"^local b\d+=")) continue;
            if (Regex.Match(s, @"^if chk==0 then return (.+) end$") is { Success: true } cm)
            {
                var asked = cm.Groups[1].Value.Replace("(", "").Replace(")", "").Split(" or ").Select(t => t.Trim()).ToHashSet();
                var offered = options.Select(o => o.Check ?? "true").ToHashSet();
                if (!asked.SetEquals(offered) && !(offered.SetEquals(["true"]) && asked.SetEquals(["true"]))) throw No("its activation check isn't that some option can be chosen");
                continue;
            }
            throw No($"its options are chosen after something the blocks don't do ({Short(s)})");
        }
        // Each option: its checks, targeting and steps.
        var dos = new List<BlockInstance>();
        var flags = 0;
        var optionFlags = new HashSet<string>();
        var conditioned = false;
        for (var k = 0; k < options.Count; k++)
        {
            var (bvar, value) = options[k];
            var targetLines = targets.TryGetValue(value, out var tl) ? tl : targets.Count == 2 && targets.ContainsKey(-1) && !targets.ContainsKey(value) ? targets[-1] : [];
            if (!steps.TryGetValue(value, out var stepLines))
            {
                if (steps.Count == 2 && steps.ContainsKey(-1) && options.Count == 2) stepLines = steps[-1];
                else throw No("one of its options does nothing the blocks can follow");
            }
            // "if tc:IsRelateToEffect(e) then Duel.Destroy(tc,REASON_EFFECT) end" as the usual steps read it.
            stepLines = Statements(Inline(stepLines, fns));
            var check = bvar is null ? "true" : bvars[bvar];
            var terms = SplitTopLevelAnd(check).Select(t => t.Trim()).Where(t => t is not ("true" or "")).ToList();
            // "you can only use each effect once per turn": a flag the option checks and sets when chosen.
            var flagged = false;
            var (registered, checkedFlags) = (new List<string>(), new List<string>());
            var cleanTargets = new List<string>();
            // "if e:IsCostChecked() then ... end": the option's own cost, paid when the effect is activated.
            var costLines = new List<string>();
            int depth = 0, inner = 0;
            foreach (var s in targetLines)
            {
                if (depth == 0 && s == "if e:IsCostChecked() then") { depth++; continue; }
                if (depth > 0 && s == "end" && inner == 0) { depth--; continue; }
                if (Regex.Match(s, @"^Duel\.RegisterFlagEffect\(tp,([\w+]+),RESET_PHASE\+PHASE_END,0,1\)$") is { Success: true } rf && inner == 0) { flagged = true; registered.Add(rf.Groups[1].Value); continue; }
                if ((s.StartsWith("e:SetCategory(", StringComparison.Ordinal) || s.StartsWith("e:SetProperty(", StringComparison.Ordinal)) && inner == 0) continue;
                if (depth > 0)
                {
                    inner += Nesting(s);
                    costLines.Add(s);
                    continue;
                }
                cleanTargets.Add(s);
            }
            // The cost as the COST blocks read it; the label it may keep would clash with the option's.
            var costBlocks = costLines.Count == 0 ? [] : costLines.Any(l => l.Contains("SetLabel", StringComparison.Ordinal))
                ? throw No("one of its options has a cost of its own that keeps a number")
                : Costs(OptionFunction(fns, $"cc_option{k + 1}cost", ["if chk==0 then return true end", .. costLines]), fns);
            var costChecks = 0;
            if (!inTarget)
            {
                // Chosen as it resolves: the flag is set in the option's own steps.
                var at = stepLines.FindIndex(s => Regex.IsMatch(s, @"^Duel\.RegisterFlagEffect\(tp,[\w+]+,RESET_PHASE\+PHASE_END,0,1\)$"));
                if (at >= 0)
                {
                    flagged = true;
                    registered.Add(Regex.Match(stepLines[at], @"^Duel\.RegisterFlagEffect\(tp,([\w+]+),").Groups[1].Value);
                    stepLines = stepLines.Where((_, i) => i != at).ToList();
                }
            }
            var conditions = new List<string>();
            var flagTerms = 0;
            var natural = 0;
            foreach (var t in terms)
            {
                if (Regex.Match(Regex.Replace(t, @"^\((.*)\)$", "$1"), @"^(?:not e:IsCostChecked\(\) or )?Duel\.GetFlagEffect\(tp,([\w+]+)\)==0(?: or not e:IsCostChecked\(\))?$") is { Success: true } ft)
                {
                    flagTerms++;
                    checkedFlags.Add(ft.Groups[1].Value);
                    continue;
                }
                // "(not e:IsCostChecked() or Duel.IsExistingMatchingCard(...))": that the option's cost can be paid, which its COST blocks ask.
                if (costBlocks.Count > 0 && Regex.IsMatch(Regex.Replace(t, @"^\((.*)\)$", "$1"), @"^not e:IsCostChecked\(\) or (?!.*IsCostChecked).+$|^(?!.*IsCostChecked).+ or not e:IsCostChecked\(\)$"))
                {
                    costChecks++;
                    continue;
                }
                if (t.Contains("IsCostChecked", StringComparison.Ordinal)) throw No("one of its options has a cost of its own");
                if (NaturalCheck(t, cleanTargets.Concat(stepLines).ToList())) { natural++; continue; }
                conditions.Add(t);
            }
            if (flagged != (flagTerms > 0)) throw No("its options are each used once per turn in a way the blocks can't say");
            // Each option keeps a flag of its own, which nothing else of the card reads: the blocks keep their own flags.
            if (flagged && (registered.Distinct().Count() != 1 || !checkedFlags.All(f => f == registered[0]) || !optionFlags.Add(registered[0])
                    || FlagUses(fns, registered[0]) != FlagUses(fns, registered[0], [tgRef, opRef])))
                throw No("its options are each used once per turn with flags the blocks can't keep");
            if (costBlocks.Count > 0 && costChecks != 1) throw No("one of its options has a cost of its own that isn't checked as the blocks check it");
            if (flagged) flags++;
            if (conditions.Count > 1) throw No("one of its options can be chosen on several conditions, which the blocks say with one");
            var name = $"cc_option{k + 1}";
            var info = Targeting(OptionFunction(fns, name + "tg", ["if chk==0 then return true end", .. cleanTargets]), fns);
            if (TakeChainLimits() is { Count: > 0 }) throw No("one of its options limits the chain, which the blocks can't say for one option");
            if (!inTarget && info.Filter is not null) throw No("an option chosen as it resolves targets");
            var blocks = Operation(OptionFunction(fns, name + "op", stepLines), fns, info, card);
            if (PairTargets(info, blocks) is { } pair) blocks = [pair];
            if (blocks.Count == 0) throw No("one of its options does nothing the blocks recognise");
            // An option offered without checking what it does ("● Draw 1 card" chosen even with an empty Deck): its blocks
            // must check nothing either.
            if (natural == 0 && ChecksSomething(blocks, card)) throw No("one of its options is offered without the checks its blocks make");
            var option = new List<BlockInstance>();
            if (conditions.Count == 1)
            {
                if (ConditionBlocks(conditions, fns) is not [var gate]) throw No($"one of its options can be chosen on a condition the blocks can't say ({Short(conditions[0])})");
                option.Add(gate);
                conditioned = true;
            }
            option.AddRange(costBlocks);
            option.AddRange(blocks);
            foreach (var b in option.Skip(1)) if (b.Link == ActionLink.Option) b.Link = ActionLink.And;
            option[0].Link = k == 0 ? ActionLink.And : ActionLink.Option;
            dos.AddRange(option);
        }
        if (flags != 0 && flags != options.Count) throw No("only some of its options are used once per turn, which the blocks can't say");
        var ownCosts = dos.Any(b => BlockRegistry.Find(b.BlockId) is { Kind: BlockKind.Cost });
        if (ownCosts && !inTarget) throw No("one of its options has a cost of its own, paid as it resolves");
        if (who == "1-tp" || !inTarget || conditioned || flags > 0 || ownCosts)
            dos.Insert(0, Block(ChooseEffectsAction.BlockId).With("who", who == "1-tp" ? "opponent" : "you").With("when", inTarget ? "activate" : "apply").With("once_each", flags > 0));
        return dos;
    }

    /// <summary>How often the card's script names a player flag: in all of its functions, or in <paramref name="only"/>.</summary>
    private static int FlagUses(Functions fns, string code, string?[]? only = null) =>
        fns.ByName.Where(f => !f.Key.StartsWith("cc_option", StringComparison.Ordinal) && (only is null || only.Any(o => o is not null && o.EndsWith("." + f.Key, StringComparison.Ordinal))))
            .Sum(f => f.Value.Body.Sum(l => Regex.Matches(l, $@"FlagEffect\(tp,{Regex.Escape(code)}[,)]").Count));

    /// <summary>Whether the blocks, as an effect of their own, ask anything to be activated ("Duel.IsPlayerCanDraw(tp,1)").</summary>
    private static bool ChecksSomething(List<BlockInstance> blocks, CardData card)
    {
        var copies = blocks.Select(b => PackSerializer.Clone(b)).ToList();
        if (copies.Count > 0) copies[0].Link = ActionLink.And;
        var probe = new CardProject
        {
            Card = new CardData { Id = card.Id, Name = card.Name, Frame = MonsterFrame.Effect, Level = 4 },
            Effects = [new EffectEvent { When = Block("when.ignition"), Do = copies }],
        };
        var lua = ScriptCompiler.Compile(probe).Lua;
        var tg = Regex.Match(lua, @"function s\.e1tg\(e,tp,eg,ep,ev,re,r,rp,chk(,chkc)?\)\n(?:\t.*\n)*?\tif chk==0 then return (.+?) end\n");
        return tg.Success && (tg.Groups[1].Success || tg.Groups[2].Value != "true");
    }

    /// <summary>
    /// A check an option makes that its own blocks make too: the cards it picks, a free zone, a draw. Anything else is a
    /// condition of the option ("If you control no monsters").
    /// </summary>
    private static bool NaturalCheck(string term, List<string> lines)
    {
        if (term.StartsWith("not ", StringComparison.Ordinal)) return false;
        if (Regex.IsMatch(term, @"^(?:Duel\.GetLocationCount\((?:tp|1-tp),LOCATION_(?:MZONE|SZONE)\)>0|Duel\.GetMZoneCount\(tp\)>0|Duel\.GetLocationCountFromEx\(tp\)>0|Duel\.IsPlayerCanDraw\((?:tp|1-tp),\d+\)|Duel\.IsPlayerCanDiscardDeck\((?:tp|1-tp),\d+\)|(?:c|e:GetHandler\(\)):Is\w+\(.*\))$"))
            return true;
        var m = Regex.Match(term, @"^Duel\.(?:IsExistingMatchingCard|IsExistingTarget|GetMatchingGroupCount)\((?:aux\.NecroValleyFilter\()?([\w.]+)\)?,");
        if (!m.Success) return false;
        var fn = Regex.Escape(m.Groups[1].Value);
        return lines.Any(l => Regex.IsMatch(l, $@"(?:SelectMatchingCard|GetMatchingGroup|SelectTarget|IsExistingTarget|IsExistingMatchingCard)\((?:tp,|1-tp,)?(?:aux\.NecroValleyFilter\()?{fn}[,)]"));
    }

    /// <summary>
    /// The choice among the options: who chooses, each option's check local and the value it is chosen with, the
    /// "local b1=..." checks, where the choice is, and the local it goes in. Null when it isn't one of the shapes official
    /// cards use.
    /// </summary>
    private static (string Who, List<(string? Check, int Value)> Options, Dictionary<string, string> Checks, int Start, int At, string Var)? Choice(List<string> body)
    {
        var checks = new Dictionary<string, string>();
        for (var i = 0; i < body.Count; i++)
        {
            var s = body[i];
            Match m;
            if ((m = Regex.Match(s, @"^local (b\d+)\s*=\s*(.+)$")).Success && !s.Contains("Select", StringComparison.Ordinal))
            {
                checks[m.Groups[1].Value] = m.Groups[2].Value.Trim();
                continue;
            }
            // "local op=aux.SelectFromOptions(tp,{b1,aux.Stringid(id,1),1},{b2,aux.Stringid(id,2),2})".
            if ((m = Regex.Match(s, @"^(?:local )?(\w+)\s*=\s*aux\.SelectFromOptions\((tp|1-tp),(.+)\)$")).Success)
            {
                var options = new List<(string?, int)>();
                var items = Regex.Matches(m.Groups[3].Value, @"\{\s*(\w+)\s*,\s*aux\.Stringid\([\w+]+,\d+\)\s*(?:,\s*(\d+)\s*)?\}");
                if (items.Count < 2) return null;
                var index = 1;
                foreach (Match item in items)
                {
                    var check = item.Groups[1].Value;
                    if (check != "true" && !checks.ContainsKey(check)) return null;
                    options.Add((check == "true" ? null : check, item.Groups[2].Success ? Int(item.Groups[2].Value) : index));
                    index++;
                }
                if (Regex.Replace(m.Groups[3].Value, @"\{[^{}]*\}", "").Trim(',', ' ').Length > 0) return null;
                return (m.Groups[2].Value, options, checks, i, i, m.Groups[1].Value);
            }
            // "if b1 and b2 then op=Duel.SelectOption(tp,a,b) elseif b1 then op=Duel.SelectOption(tp,a) else op=Duel.SelectOption(tp,b)+1 end".
            if (s.StartsWith("if b1 and b2 then", StringComparison.Ordinal) || s == "if b1 and b2 then")
            {
                var joined = s;
                var j = i;
                var depth = Nesting(s);
                while (depth > 0 && ++j < body.Count) { joined += " " + body[j]; depth += Nesting(body[j]); }
                m = Regex.Match(joined, @"^if b1 and b2 then (\w+)=Duel\.SelectOption\((tp|1-tp),aux\.Stringid\([\w+]+,\d+\),aux\.Stringid\([\w+]+,\d+\)\) " +
                                        @"elseif b1 then \1=Duel\.SelectOption\(\2,aux\.Stringid\([\w+]+,\d+\)\) (?:else|elseif b2 then) \1=Duel\.SelectOption\(\2,aux\.Stringid\([\w+]+,\d+\)\)\+1(?: else return)? end$");
                if (!m.Success || !checks.ContainsKey("b1") || !checks.ContainsKey("b2") || checks.Count != 2) return null;
                return (m.Groups[2].Value, [("b1", 0), ("b2", 1)], checks, i, j, m.Groups[1].Value);
            }
            // "local op=Duel.SelectOption(1-tp,aux.Stringid(id,2),aux.Stringid(id,3))": every option can be chosen.
            if ((m = Regex.Match(s, @"^local (\w+)=Duel\.SelectOption\((tp|1-tp),((?:aux\.Stringid\([\w+]+,\d+\),?)+)\)$")).Success && checks.Count == 0)
            {
                var n = Regex.Matches(m.Groups[3].Value, @"aux\.Stringid").Count;
                if (n < 2) return null;
                return (m.Groups[2].Value, Enumerable.Range(0, n).Select(v => ((string?)null, v)).ToList(), checks, i, i, m.Groups[1].Value);
            }
        }
        return null;
    }
}
