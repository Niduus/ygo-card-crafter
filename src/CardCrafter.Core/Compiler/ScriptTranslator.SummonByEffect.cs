using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Whole operations that are one block: Synchro/Xyz/Link Summons by effect, Rank-Up-Magic, Pendulum Scale changes,
/// "you can conduct 1 Pendulum Summon in addition to your Pendulum Summon" and a Pendulum Monster placing itself in
/// the Pendulum Zone. Each is read only in the exact shapes the official scripts write; anything else stays script.
/// </summary>
public static partial class ScriptTranslator
{
    /// <summary>The whole script being read, for checks across its effects (count limit codes).</summary>
    [ThreadStatic] private static string? _sourceLua;

    /// <summary>The DO blocks for an operation (and its target step) that is one of these, or null.</summary>
    private static List<BlockInstance>? SummonByEffectOf(string operation, string? target, Functions fns)
    {
        var op = WholeLines(operation, fns);
        if (op is null) return null;
        var tg = target is null ? [] : WholeLines(target, fns);
        if (tg is null) return null;
        return RankUpOf(op, tg, fns) ?? ExtraDeckSummonOf(op, tg, fns) ?? PendulumScaleOf(op, tg) ?? ExtraPendulumOf(op, tg, fns) ?? PlaceInPendulumZoneOf(op, tg);
    }

    /// <summary>A function's lines, trimmed and joined where a statement goes on, with the script's prefix written "s.".</summary>
    private static List<string>? WholeLines(string reference, Functions fns)
    {
        if (!Regex.IsMatch(reference.Trim(), $@"^{Regex.Escape(fns.Prefix)}\.\w+$")) return null;
        var (_, body) = fns.Get(reference);
        return JoinContinuations(body).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal))
            .Select(l => Regex.Replace(l, @"\b(?:c\d+|s)\.(?=\w)", "s.")).ToList();
    }

    /// <summary>"s.filter" back to the script's own name for it.</summary>
    private static string Own(string name, Functions fns) => name.StartsWith("s.", StringComparison.Ordinal) ? fns.Prefix + name[1..] : name;

    /// <summary>The "and" terms of a filter function, its parameters replaced by what they stand for, after any "local x=..." lines.</summary>
    private static (List<string> Terms, string? Guard)? FilterBody(string name, Functions fns, IReadOnlyDictionary<string, string> args)
    {
        var (pars, body) = fns.Get(Own(name, fns));
        var lines = JoinContinuations(body).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal))
            .Select(l => Regex.Replace(l, @"\b(?:c\d+|s)\.(?=\w)", "s.")).ToList();
        var values = new Dictionary<string, string>(args);
        string Sub(string text) => values.Aggregate(text, (t, kv) => Regex.Replace(t, $@"(?<![\w.:]){Regex.Escape(kv.Key)}\b", kv.Value));
        string? guard = null;
        for (var i = 0; i < lines.Count - 1; i++)
        {
            Match m;
            if ((m = Regex.Match(lines[i], @"^local (\w+)=(.+)$")).Success && !pars.Contains(m.Groups[1].Value)) values[m.Groups[1].Value] = Sub(m.Groups[2].Value);
            else if (lines[i].StartsWith("if ", StringComparison.Ordinal) && lines[i].EndsWith(" then return false end", StringComparison.Ordinal) && guard is null) guard = Sub(lines[i]);
            else return null;
        }
        var ret = Regex.Match(lines[^1], @"^return (.+)$");
        if (!ret.Success) return null;
        // Values are put in as written ("rk+1" with rk=c:GetRank()): the patterns that read them only take a number added.
        var terms = SplitTopLevelAnd(Sub(ret.Groups[1].Value)).Select(t => t.Trim()).ToList();
        return (terms, guard);
    }

    // ---------------------------------------------------------------- Rank-Up-Magic

    private static readonly Regex RankUpFieldOp = new(
        @"^(?:local c=e:GetHandler\(\)\n)?local tc=Duel\.GetFirstTarget\(\)\n" +
        @"(?:if not aux\.MustMaterialCheck\(tc,tp,EFFECT_MUST_BE_XMATERIAL\) then return end\nif |if not aux\.MustMaterialCheck\(tc,tp,EFFECT_MUST_BE_XMATERIAL\) or )" +
        @"tc:IsFacedown\(\) or not tc:IsRelateTo(?:Effect\(e\)|Chain\(\)) or tc:IsControler\(1-tp\) or tc:IsImmuneToEffect\(e\) then return end\n" +
        @"Duel\.Hint\(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON\)\n" +
        @"local g=Duel\.SelectMatchingCard\(tp,(?<f2>s\.\w+),tp,LOCATION_EXTRA,0,1,1,nil,e,tp,tc(?<args>(?:,[^\n]+)?)\)\n" +
        @"local sc=g:GetFirst\(\)\nif sc then\n" +
        @"local mg=tc:GetOverlayGroup\(\)\nif (?:mg:GetCount\(\)~=0|#mg~=0|#mg>0|mg:GetCount\(\)>0) then\nDuel\.Overlay\(sc,mg\)\nend\n" +
        @"sc:SetMaterial\(Group\.FromCards\(tc\)\)\nDuel\.Overlay\(sc,Group\.FromCards\(tc\)\)\n" +
        @"Duel\.SpecialSummon\(sc,SUMMON_TYPE_XYZ,tp,tp,false,false,POS_FACEUP\)\nsc:CompleteProcedure\(\)\nend$");

    private static readonly Regex RankUpFieldTarget = new(
        @"^(?:local c=e:GetHandler\(\)\n)?if chkc then return (?:chkc:IsControler\(tp\) and chkc:IsLocation\(LOCATION_MZONE\)|chkc:IsLocation\(LOCATION_MZONE\) and chkc:IsControler\(tp\)) and (?<f1>s\.\w+)\(chkc,e,tp\) end\n" +
        @"if chk==0 then return Duel\.IsExistingTarget\(\k<f1>,tp,LOCATION_MZONE,0,1,nil,e,tp\) end\n" +
        @"Duel\.Hint\(HINT_SELECTMSG,tp,HINTMSG_(?:TARGET|FACEUP)\)\n" +
        @"(?:local g=)?Duel\.SelectTarget\(tp,\k<f1>,tp,LOCATION_MZONE,0,1,1,nil,e,tp\)\n" +
        @"Duel\.SetOperationInfo\(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_EXTRA\)$");

    private static readonly Regex RankUpGraveOp = new(
        @"^if Duel\.GetLocationCount\(tp,LOCATION_MZONE\)<=0 then return end\nlocal tc=Duel\.GetFirstTarget\(\)\n" +
        @"if not tc:IsRelateTo(?:Effect\(e\)|Chain\(\)) or tc:IsImmuneToEffect\(e\) then return end\n" +
        @"if Duel\.SpecialSummon\(tc,0,tp,tp,false,false,POS_FACEUP\)==0 then return end\n" +
        @"if not aux\.MustMaterialCheck\(tc,tp,EFFECT_MUST_BE_XMATERIAL\) then return end\n" +
        @"Duel\.Hint\(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON\)\n" +
        @"local g=Duel\.SelectMatchingCard\(tp,(?<f2>s\.\w+),tp,LOCATION_EXTRA,0,1,1,nil,e,tp,tc(?<args>(?:,[^\n]+)?)\)\n" +
        @"local sc=g:GetFirst\(\)\nif sc then\nDuel\.BreakEffect\(\)\n" +
        @"sc:SetMaterial\(Group\.FromCards\(tc\)\)\nDuel\.Overlay\(sc,Group\.FromCards\(tc\)\)\n" +
        @"Duel\.SpecialSummon\(sc,SUMMON_TYPE_XYZ,tp,tp,false,false,POS_FACEUP\)\nsc:CompleteProcedure\(\)\nend$");

    private static readonly Regex RankUpGraveTarget = new(
        @"^if chkc then return (?:chkc:IsControler\(tp\) and chkc:IsLocation\(LOCATION_GRAVE\)|chkc:IsLocation\(LOCATION_GRAVE\) and chkc:IsControler\(tp\)) and (?<f1>s\.\w+)\(chkc,e,tp\) end\n" +
        @"if chk==0 then return Duel\.IsPlayerCanSpecialSummonCount\(tp,2\) and Duel\.GetLocationCount\(tp,LOCATION_MZONE\)>0 and Duel\.IsExistingTarget\(\k<f1>,tp,LOCATION_GRAVE,0,1,nil,e,tp\) end\n" +
        @"Duel\.Hint\(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON\)\n" +
        @"local g=Duel\.SelectTarget\(tp,\k<f1>,tp,LOCATION_GRAVE,0,1,1,nil,e,tp\)\n" +
        @"Duel\.SetOperationInfo\(0,CATEGORY_SPECIAL_SUMMON,g,2,tp,LOCATION_EXTRA\)$");

    /// <summary>
    /// "Target 1 Xyz Monster you control; Special Summon from your Extra Deck, 1 Xyz Monster that is 1 Rank higher than
    /// that monster you control, by using it as material": the Rank-Up block, with what the new monster must be.
    /// </summary>
    private static List<BlockInstance>? RankUpOf(List<string> op, List<string> tg, Functions fns)
    {
        var opText = string.Join("\n", op);
        var tgText = string.Join("\n", tg);
        Match o, t;
        bool grave;
        if ((o = RankUpFieldOp.Match(opText)).Success && (t = RankUpFieldTarget.Match(tgText)).Success) grave = false;
        else if ((o = RankUpGraveOp.Match(opText)).Success && (t = RankUpGraveTarget.Match(tgText)).Success) grave = true;
        else return null;
        var f2 = o.Groups["f2"].Value;
        var opArgs = o.Groups["args"].Value.Length == 0 ? [] : SplitArgs(o.Groups["args"].Value[1..]).Select(a => a.Trim()).ToList();

        // The Xyz Monster used as material, and the check that it has one to become.
        var material = new CardFilter { Owner = CardOwner.You, Zones = grave ? Model.Zone.Graveyard : Model.Zone.MonsterZone, CardType = FilterCardType.Monster };
        if (FilterBody(t.Groups["f1"].Value, fns, new Dictionary<string, string>()) is not { Guard: null } f1) throw No("its Rank-Up picks the Xyz Monster in a way the block can't");
        bool faceup = false, xyz = false, exists = false, must = false, canSummon = false;
        foreach (var term in f1.Terms)
        {
            Match m;
            if (term == "c:IsFaceup()" && !grave) faceup = true;
            else if (term == "c:IsType(TYPE_XYZ)") xyz = true;
            else if (term == "aux.MustMaterialCheck(c,tp,EFFECT_MUST_BE_XMATERIAL)") must = true;
            else if (term == "c:IsCanBeSpecialSummoned(e,0,tp,false,false)" && grave) canSummon = true;
            else if ((m = Regex.Match(term, $@"^Duel\.IsExistingMatchingCard\({Regex.Escape(f2)},tp,LOCATION_EXTRA,0,1,nil,e,tp,c(?<args>(?:,.+)?)\)$")).Success)
            {
                // The same values as the operation passes, read from the material.
                var args = m.Groups["args"].Value.Length == 0 ? [] : SplitArgs(m.Groups["args"].Value[1..]).Select(a => a.Trim()).ToList();
                if (!args.SequenceEqual(opArgs.Select(a => Regex.Replace(a, @"\btc\b", "c")))) throw No("its Rank-Up checks a different monster than it Summons");
                exists = true;
            }
            else Term(term, material);
        }
        if (!xyz || !exists || !must || (!grave && !faceup) || (grave && !canSummon)) throw No("its Rank-Up checks the Xyz Monster in a way the block can't");
        material.Zones = grave ? Model.Zone.Graveyard : Model.Zone.MonsterZone;
        material.Owner = CardOwner.You;
        material.MonsterKind = MonsterKind.Xyz;
        material.CardType = FilterCardType.Monster;
        material.FaceUpOnly = false;

        // The new Xyz Monster: its Rank against the material's (mc), and what else it must be.
        var (pars, _) = fns.Get(Own(f2, fns));
        if (pars.Length < 4 || pars[0] != "c" || pars[1] != "e" || pars[2] != "tp" || pars.Length - 4 != opArgs.Count) throw No("its Rank-Up's filter takes values the block can't follow");
        var mc = pars[3];
        var map = new Dictionary<string, string>();
        for (var i = 0; i < opArgs.Count; i++) map[pars[4 + i]] = Regex.Replace(opArgs[i], @"\btc\b", "mc");
        if (mc != "mc") map[mc] = "mc";
        if (FilterBody(f2, fns, map) is not { } f2Body) throw No("its Rank-Up picks the new Xyz Monster in a way the block can't");
        var summoned = new CardFilter { Owner = CardOwner.You, Zones = Model.Zone.ExtraDeck, CardType = FilterCardType.Monster };
        string rank = "any", same = "none";
        int ranks = 1;
        bool sxyz = false, canBeMaterial = false, xyzSummon = false, room = false, race = false, attribute = false;
        foreach (var term in f2Body.Terms)
        {
            Match m;
            if ((m = Regex.Match(term, @"^c:IsRank\(mc:GetRank\(\)([+-]\d+)?\)$")).Success && rank == "any")
            {
                var by = m.Groups[1].Success ? Int(m.Groups[1].Value) : 0;
                (rank, ranks) = by > 0 ? ("up", by) : by < 0 ? ("down", -by) : ("same", 1);
            }
            else if ((m = Regex.Match(term, @"^\(c:IsRank\(mc:GetRank\(\)\+(\d+)\) or c:IsRank\(mc:GetRank\(\)-\1\)\)$")).Success && rank == "any") (rank, ranks) = ("either", Int(m.Groups[1].Value));
            else if (term == "c:IsRankBelow(mc:GetRank()-1)" && rank == "any") rank = "lower";
            else if (term == "c:IsRace(mc:GetRace())") race = true;
            else if (term == "c:IsAttribute(mc:GetAttribute())") attribute = true;
            else if (term == "c:IsType(TYPE_XYZ)") sxyz = true;
            else if (term == "mc:IsCanBeXyzMaterial(c)") canBeMaterial = true;
            else if (term == "c:IsCanBeSpecialSummoned(e,SUMMON_TYPE_XYZ,tp,false,false)") xyzSummon = true;
            else if (term == "Duel.GetLocationCountFromEx(tp,tp,mc,c)>0") room = true;
            else if (Regex.IsMatch(term, @"\bmc\b")) throw No($"its Rank-Up compares the monsters in a way the block can't ({Short(term)})");
            else Term(term, summoned);
        }
        if (!canBeMaterial || !xyzSummon || !room || ranks > 12) throw No("its Rank-Up Summons in a way the block can't");
        // Without "is an Xyz Monster", only a Rank above the material's leaves out monsters with no Rank.
        if (!sxyz && rank is not ("up" or "same")) throw No("its Rank-Up could Summon a monster without a Rank");
        // "Number C88" needs "Number 88" under it: the block always checks it, as most Rank-Up-Magic cards do.
        var c88 = f2Body.Guard is "if c:GetOriginalCode()==6165656 and not mc:IsCode(48995978) then return false end"
            or "if c:GetOriginalCode()==6165656 and mc:GetCode()~=48995978 then return false end";
        if (f2Body.Guard is not null && !c88) throw No("its Rank-Up has a check the block doesn't");
        if (!c88 && (summoned.Archetype == 0 || NumberSets.Contains(summoned.Archetype) || summoned.Archetype2 != 0 && NumberSets.Contains(summoned.Archetype2)))
            throw No("its Rank-Up could Summon \"Number C88\" in a way the block doesn't");
        summoned.Owner = CardOwner.You;
        summoned.Zones = Model.Zone.ExtraDeck;
        summoned.CardType = FilterCardType.Monster;
        if (sxyz) summoned.MonsterKind = MonsterKind.Xyz;
        same = race && attribute ? "both" : race ? "race" : attribute ? "attribute" : "none";
        return [Block(RankUpAction.BlockId).With("material", material).With("cards", summoned).With("rank", rank).With("ranks", ranks).With("same", same)];
    }

    /// <summary>The archetypes "Number C88: Gimmick Puppet Disaster Leo" belongs to.</summary>
    private static readonly HashSet<int> NumberSets = [0x48, 0x1048, 0x83, 0x1083];

    // ---------------------------------------------------------------- Synchro/Xyz/Link Summons by effect

    private static readonly Regex SummonableCheck = new(@"^if chk==0 then return Duel\.IsExistingMatchingCard\((?<f>[\w.]+),tp,LOCATION_EXTRA,0,1,nil(?:,nil)?\) end$");

    /// <summary>
    /// Wonder Xyz, Urgent Tuning, World Legacy Awakens: "Xyz Summon 1 Xyz Monster using monsters you control", the
    /// operation doing nothing else.
    /// </summary>
    private static List<BlockInstance>? ExtraDeckSummonOf(List<string> op, List<string> tg, Functions fns)
    {
        if (tg.Count != 2 || SummonableCheck.Match(tg[0]) is not { Success: true } check || tg[1] != "Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,nil,1,tp,LOCATION_EXTRA)") return null;
        var f = check.Groups["f"].Value;
        var text = string.Join("\n", op);
        var fe = Regex.Escape(f);
        var m = Regex.Match(text,
            $@"^local g=Duel\.GetMatchingGroup\({fe},tp,LOCATION_EXTRA,0,nil(?:,nil)?\)\nif (?:g:GetCount\(\)>0|#g>0) then\nDuel\.Hint\(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON\)\n" +
            @"local (?<v>\w+)=g:Select\(tp,1,1,nil\)\nDuel\.(?<kind>Synchro|Xyz|Link)Summon\(tp,\k<v>:GetFirst\(\),nil\)\nend$");
        if (!m.Success)
            m = Regex.Match(text,
                $@"^Duel\.Hint\(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON\)\nlocal g=Duel\.SelectMatchingCard\(tp,{fe},tp,LOCATION_EXTRA,0,1,1,nil(?:,nil)?\)\n" +
                @"local tc=g:GetFirst\(\)\nif tc then\nDuel\.(?<kind>Synchro|Xyz|Link)Summon\(tp,tc,nil\)\nend$");
        if (!m.Success) return null;
        var kind = m.Groups["kind"].Value;
        var cards = new CardFilter { Owner = CardOwner.You, Zones = Model.Zone.ExtraDeck, CardType = FilterCardType.Monster };
        if (f != $"Card.Is{kind}Summonable")
        {
            if (FilterBody(f, fns, new Dictionary<string, string>()) is not { Guard: null } body) throw No("its Summon picks the monster in a way the block can't");
            var summonable = false;
            foreach (var term in body.Terms)
            {
                if (term == $"c:Is{kind}Summonable(nil)") summonable = true;
                else Term(term, cards);
            }
            if (!summonable) throw No("its Summon picks the monster in a way the block can't");
        }
        cards.Owner = CardOwner.You;
        cards.Zones = Model.Zone.ExtraDeck;
        cards.CardType = FilterCardType.Monster;
        return [Block(ExtraDeckSummonAction.BlockId).With("kind", kind.ToLowerInvariant()).With("cards", cards)];
    }

    // ---------------------------------------------------------------- Pendulum Scales

    private static readonly Regex ScaleOp = new(
        @"^local c=e:GetHandler\(\)\nif not c:IsRelateTo(?:Effect\(e\)|Chain\(\)) or c:GetLeftScale\(\)(?<guard>>=|<=|==)(?<limit>\d+) then return end\n" +
        @"local e1=Effect\.CreateEffect\(c\)\ne1:SetType\(EFFECT_TYPE_SINGLE\)\ne1:SetCode\(EFFECT_(?<code>UPDATE|CHANGE)_LSCALE\)\ne1:SetValue\((?<value>-?\d+)\)\n" +
        @"e1:SetReset\(RESET_EVENT\+RESETS_STANDARD\+RESET_DISABLE(?<turn>\+RESET_PHASE\+PHASE_END)?\)\nc:RegisterEffect\(e1\)\n" +
        @"local e2=e1:Clone\(\)\ne2:SetCode\(EFFECT_\k<code>_RSCALE\)\nc:RegisterEffect\(e2\)$");

    /// <summary>
    /// Performapal Trumpanda's "increase this card's Pendulum Scale by 1 (max. 12)", Parrotrio's "make this card's
    /// Pendulum Scale become 7 until the end of this turn". The activation must check what the block checks.
    /// </summary>
    private static List<BlockInstance>? PendulumScaleOf(List<string> op, List<string> tg)
    {
        var m = ScaleOp.Match(string.Join("\n", op));
        if (!m.Success) return null;
        var limit = Int(m.Groups["limit"].Value);
        var value = Int(m.Groups["value"].Value);
        var (mode, check, amount) = (m.Groups["code"].Value, m.Groups["guard"].Value) switch
        {
            ("CHANGE", "==") when value == limit => ("becomes", $"~={limit}", value),
            // By 1: the change never passes the limit, as the block's own clamp.
            ("UPDATE", ">=") when value == 1 => ("increase", $"<{limit}", 1),
            ("UPDATE", "<=") when value == -1 => ("reduce", $">{limit}", 1),
            _ => ((string?)null, "", 0),
        };
        if (mode is null || limit > 13 || Math.Abs(value) > 13) throw No("it changes the Pendulum Scale in a way the block doesn't");
        var text = string.Join(" ", tg);
        if (text.Length == 0) throw No("its Pendulum Scale change can be activated when it would change nothing");
        if (!Regex.IsMatch(text, $@"^(?:local c=e:GetHandler\(\) )?if chk==0 then return (?:e:GetHandler\(\)|c):GetLeftScale\(\){Regex.Escape(check)} end$"))
            throw No("its Pendulum Scale change is activated in a way the block isn't");
        return [Block(PendulumScaleAction.BlockId).With("mode", mode).With("amount", amount).With("limit", mode == "becomes" ? 12 : limit)
            .With("duration", m.Groups["turn"].Success ? "turn" : "always")];
    }

    /// <summary>
    /// Stargazer Magician's "this card's Pendulum Scale becomes 4" (with its "unless ..." as IF blocks): one lasting
    /// effect on the left Scale and its copy on the right, in the Pendulum Zone. Null when the piece isn't that.
    /// </summary>
    private static TranslatedPiece? PendulumScalePieceOf(ScriptPiece piece, List<Dictionary<string, string>> sets, Functions fns)
    {
        if (sets.Count != 2) return null;
        var change = (sets[0].GetValueOrDefault("Code"), sets[1].GetValueOrDefault("Code")) switch
        {
            ("EFFECT_CHANGE_LSCALE", "EFFECT_CHANGE_RSCALE") => true,
            ("EFFECT_UPDATE_LSCALE", "EFFECT_UPDATE_RSCALE") => false,
            _ => (bool?)null,
        };
        if (change is null) return null;
        var rest = sets.Select(s => s.Where(kv => kv.Key != "Code").OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToList()).ToList();
        if (!rest[0].SequenceEqual(rest[1])) return null;
        var set = sets[0];
        if (set.Keys.Any(k => k is not ("Type" or "Code" or "Property" or "Range" or "Value" or "Condition"))
            || set.GetValueOrDefault("Type") != "EFFECT_TYPE_SINGLE" || set.GetValueOrDefault("Property") != "EFFECT_FLAG_SINGLE_RANGE" || set.GetValueOrDefault("Range") != "LOCATION_PZONE"
            || !Regex.IsMatch(set.GetValueOrDefault("Value") ?? "", @"^-?\d+$"))
            throw No("its Pendulum Scale change isn't one the block has");
        var value = Int(set["Value"]);
        var mode = change.Value ? "becomes" : value < 0 ? "reduce" : "increase";
        if (change.Value && value < 0 || Math.Abs(value) > 13) throw No("its Pendulum Scale change isn't one the block has");
        var effect = new EffectEvent { Label = piece.Title, When = Block("when.while_face_up").With("zone", "pendulum_zone") };
        effect.Do.Add(Block(PendulumScalePassive.BlockId).With("mode", mode).With("amount", Math.Abs(value)));
        if (set.GetValueOrDefault("Condition") is { } condition) effect.If.AddRange(Conditions(condition, fns));
        return new TranslatedPiece { Piece = piece, Effect = effect };
    }

    // ---------------------------------------------------------------- an extra Pendulum Summon

    private static readonly Regex ExtraPendulumOp = new(
        @"^local c=e:GetHandler\(\)\nlocal e1=Effect\.CreateEffect\(c\)\ne1:SetDescription\(aux\.Stringid\((?:id|\d+),\d+\)\)\ne1:SetType\(EFFECT_TYPE_FIELD\)\n" +
        @"e1:SetCode\(EFFECT_EXTRA_PENDULUM_SUMMON\)\ne1:SetProperty\(EFFECT_FLAG_PLAYER_TARGET\)\ne1:SetTargetRange\(1,0\)\ne1:SetCountLimit\(1,(?<code>id|\d+)\)\n" +
        @"e1:SetValue\((?<value>[\w.]+)\)\ne1:SetReset\(RESET_PHASE\+PHASE_END\)\nDuel\.RegisterEffect\(e1,tp\)\n" +
        @"Duel\.RegisterFlagEffect\(tp,\k<code>,RESET_PHASE\+PHASE_END,0,1\)$");

    /// <summary>
    /// Extra Pendulum: "During your Main Phase this turn, you can conduct 1 Pendulum Summon of a monster(s) from your
    /// Extra Deck in addition to your Pendulum Summon. (You can only gain this effect once per turn.)"
    /// </summary>
    private static List<BlockInstance>? ExtraPendulumOf(List<string> op, List<string> tg, Functions fns)
    {
        var m = ExtraPendulumOp.Match(string.Join("\n", op));
        if (!m.Success) return null;
        var code = m.Groups["code"].Value;
        if (code != "id" && Int64(code) != _sourceId) throw No("its extra Pendulum Summon shares its limit with another card");
        if (tg.Count != 1 || tg[0] != $"if chk==0 then return Duel.GetFlagEffect(tp,{code})==0 end") throw No("its extra Pendulum Summon is activated in a way the block isn't");
        // The limit is the card's own: no other effect of the script may count with the same code.
        if (_sourceLua is { } lua && Regex.Matches(lua, $@"SetCountLimit\(1,(?:id|{_sourceId})\b").Count > 1) throw No("its extra Pendulum Summon shares its limit with another effect");
        var cards = new CardFilter { CardType = FilterCardType.Monster };
        var from = "any";
        var value = m.Groups["value"].Value;
        if (value != "aux.TRUE")
        {
            if (FilterBody(value, fns, new Dictionary<string, string>()) is not { Guard: null } body) throw No("its extra Pendulum Summon picks monsters in a way the block can't");
            foreach (var term in body.Terms)
            {
                if (term == "c:IsLocation(LOCATION_HAND)" && from == "any") from = "hand";
                else if (term == "c:IsLocation(LOCATION_EXTRA)" && from == "any") from = "extra";
                else Term(term, cards);
            }
        }
        cards.Zones = Model.Zone.None;
        cards.CardType = FilterCardType.Monster;
        return [Block(ExtraPendulumAction.BlockId).With("cards", cards).With("from", from)];
    }

    // ---------------------------------------------------------------- this card to the Pendulum Zone

    private static readonly Regex PlaceSelfOp = new(
        @"^(?:local c=e:GetHandler\(\)\n(?:if c:IsRelateTo(?:Effect\(e\)|Chain\(\)) then\nDuel\.MoveToField\(c,tp,tp,LOCATION_PZONE,POS_FACEUP,true\)\nend|" +
        @"if c:IsRelateTo(?:Effect\(e\)|Chain\(\)) then Duel\.MoveToField\(c,tp,tp,LOCATION_PZONE,POS_FACEUP,true\) end|" +
        @"if not c:IsRelateTo(?:Effect\(e\)|Chain\(\)) then return end\nDuel\.MoveToField\(c,tp,tp,LOCATION_PZONE,POS_FACEUP,true\))|" +
        @"if e:GetHandler\(\):IsRelateTo(?:Effect\(e\)|Chain\(\)) then\nDuel\.MoveToField\(e:GetHandler\(\),tp,tp,LOCATION_PZONE,POS_FACEUP,true\)\nend)$");

    /// <summary>"You can place this card in your Pendulum Zone": a free Pendulum Zone to activate, then this card is placed.</summary>
    private static List<BlockInstance>? PlaceInPendulumZoneOf(List<string> op, List<string> tg)
    {
        if (!PlaceSelfOp.IsMatch(string.Join("\n", op))) return null;
        if (tg.Count != 1 || tg[0] is not ("if chk==0 then return Duel.CheckLocation(tp,LOCATION_PZONE,0) or Duel.CheckLocation(tp,LOCATION_PZONE,1) end"
            or "if chk==0 then return (Duel.CheckLocation(tp,LOCATION_PZONE,0) or Duel.CheckLocation(tp,LOCATION_PZONE,1)) end"))
            throw No("it places this card in the Pendulum Zone in a way the block doesn't");
        return [Block(PlaceAction.BlockId).With("cards", new CardFilter { ThisCard = true }).With("where", "pzone")];
    }

    /// <summary>"If this card in the Monster Zone is destroyed" and the like: the destroyed trigger's choice, or null.</summary>
    private static string? PendulumDestroyed(string body) => body switch
    {
        "return c:IsPreviousLocation(LOCATION_MZONE) and c:IsFaceup()" or "return e:GetHandler():IsPreviousLocation(LOCATION_MZONE) and e:GetHandler():IsFaceup()" => "in_mzone",
        "return bit.band(r,REASON_EFFECT+REASON_BATTLE)~=0 and c:IsPreviousLocation(LOCATION_MZONE) and c:IsFaceup()"
            or "return c:IsReason(REASON_BATTLE+REASON_EFFECT) and c:IsPreviousLocation(LOCATION_MZONE) and c:IsFaceup()"
            or "return c:IsReason(REASON_EFFECT+REASON_BATTLE) and c:IsPreviousLocation(LOCATION_MZONE) and c:IsFaceup()" => "in_mzone_battle_effect",
        "return c:IsPreviousLocation(LOCATION_PZONE)" or "return e:GetHandler():IsPreviousLocation(LOCATION_PZONE)" => "in_pzone",
        _ => null,
    };
}
