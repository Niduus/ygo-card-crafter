using System.Globalization;
using System.Text.RegularExpressions;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Filter terms read into a card filter's requirements (<see cref="CardFilter.Requirements"/>) and the fields the older
/// readers left out: "not DARK", "with 0 original ATK", "whose effects are negated", "face-down, or not a "Six Samurai"
/// card". A term is read only where the filter then picks exactly the cards the script does; anything else stays script.
/// </summary>
public static partial class ScriptTranslator
{
    /// <summary>
    /// What the rest of the filter must say for a requirement to act as the script's test. A requirement on a number only
    /// looks at cards that have it ("Level" at monsters with a Level), while the script's getter gives 0 for the others:
    /// "c:GetLevel()&lt;=4" also picks Spells and Xyz Monsters, unless another term of the filter leaves them out.
    /// </summary>
    private enum Proof { None, Monster, MonsterNotLink, HasLevel, Xyz, Link }

    /// <summary>The other terms of the filter function being read, which can show a requirement's guard holds anyway.</summary>
    [ThreadStatic] private static List<string>? _siblings;

    private static readonly Dictionary<string, long> TypeBits = new()
    {
        ["TYPE_MONSTER"] = 0x1, ["TYPE_SPELL"] = 0x2, ["TYPE_TRAP"] = 0x4, ["TYPE_NORMAL"] = 0x10, ["TYPE_EFFECT"] = 0x20, ["TYPE_FUSION"] = 0x40,
        ["TYPE_RITUAL"] = 0x80, ["TYPE_TRAPMONSTER"] = 0x100, ["TYPE_SPIRIT"] = 0x200, ["TYPE_UNION"] = 0x400, ["TYPE_DUAL"] = 0x800,
        ["TYPE_TUNER"] = 0x1000, ["TYPE_SYNCHRO"] = 0x2000, ["TYPE_TOKEN"] = 0x4000, ["TYPE_QUICKPLAY"] = 0x10000, ["TYPE_CONTINUOUS"] = 0x20000,
        ["TYPE_EQUIP"] = 0x40000, ["TYPE_FIELD"] = 0x80000, ["TYPE_COUNTER"] = 0x100000, ["TYPE_FLIP"] = 0x200000, ["TYPE_TOON"] = 0x400000,
        ["TYPE_XYZ"] = 0x800000, ["TYPE_PENDULUM"] = 0x1000000, ["TYPE_SPSUMMON"] = 0x2000000, ["TYPE_LINK"] = 0x4000000,
    };

    private static readonly Dictionary<string, long> SummonKinds = new()
    {
        ["SUMMON_TYPE_NORMAL"] = 0, ["SUMMON_TYPE_ADVANCE"] = 1, ["SUMMON_TYPE_FLIP"] = 2, ["SUMMON_TYPE_SPECIAL"] = 3, ["SUMMON_TYPE_FUSION"] = 4,
        ["SUMMON_TYPE_SYNCHRO"] = 5, ["SUMMON_TYPE_XYZ"] = 6, ["SUMMON_TYPE_LINK"] = 7, ["SUMMON_TYPE_RITUAL"] = 8, ["SUMMON_TYPE_PENDULUM"] = 9,
    };

    private static readonly Dictionary<string, long> SummonPlaces = new()
    {
        ["LOCATION_HAND"] = 0x2, ["LOCATION_DECK"] = 0x1, ["LOCATION_GRAVE"] = 0x10, ["LOCATION_REMOVED"] = 0x20, ["LOCATION_EXTRA"] = 0x40,
    };

    private static readonly Dictionary<string, long> Locations = new()
    {
        ["LOCATION_HAND"] = 0x2, ["LOCATION_DECK"] = 0x1, ["LOCATION_MZONE"] = 0x4, ["LOCATION_SZONE"] = 0x8, ["LOCATION_ONFIELD"] = 0xc,
        ["LOCATION_GRAVE"] = 0x10, ["LOCATION_REMOVED"] = 0x20, ["LOCATION_EXTRA"] = 0x40,
    };

    private static readonly Dictionary<string, long> Positions = new()
    {
        ["POS_ATTACK"] = 0x3, ["POS_DEFENSE"] = 0xc, ["POS_FACEUP"] = 0x5, ["POS_FACEDOWN"] = 0xa,
    };

    // ---------------------------------------------------------------- filter functions

    /// <summary>
    /// The engine's own filters and filter makers, as the card filter they are: aux.nzatk ("a face-up monster with ATK"),
    /// aux.FilterBoolFunction(Card.IsRace,RACE_DRAGON), aux.NOT(...). False for any other reference.
    /// </summary>
    private static bool KnownFilter(string reference, CardFilter f, Functions fns)
    {
        Match m;
        switch (reference)
        {
            // c:IsFaceup() and c:GetAttack()>0 (GetDefense()>0): IsAttackAbove(1) is the same test.
            case "aux.nzatk": f.FaceUpOnly = true; f.AtkMin = 1; return true;
            case "aux.nzdef": f.FaceUpOnly = true; f.DefMin = 1; return true;
            // c:IsFaceup() and not c:IsDisabled() and c:IsType(TYPE_EFFECT).
            case "aux.NegateEffectMonsterFilter": return NegatableEffectMonster(f);
        }
        if ((m = Regex.Match(reference, @"^aux\.FilterBoolFunction\(Card\.(\w+),(.+)\)$")).Success && !m.Groups[2].Value.Contains(','))
        {
            Term($"c:{m.Groups[1].Value}({m.Groups[2].Value})", f);
            return true;
        }
        if ((m = Regex.Match(reference, @"^aux\.NOT\((.+)\)$")).Success)
        {
            var inner = SingleTerm(m.Groups[1].Value.Trim(), fns) ?? throw No($"it picks cards by aux.NOT({Short(m.Groups[1].Value)})");
            Term(inner.StartsWith("not ", StringComparison.Ordinal) ? inner[4..] : "not " + inner, f);
            return true;
        }
        return false;
    }

    /// <summary>
    /// A filter with the extra arguments the engine passes it: "Duel.SelectReleaseGroup(tp,Card.IsSetCard,1,1,nil,0x69)"
    /// is "c:IsSetCard(0x69)". Other filters ignore them, as <see cref="Filter"/> reads the script's own functions.
    /// </summary>
    private static CardFilter FilterWith(string reference, string extra, Functions fns)
    {
        if (!Regex.IsMatch(reference, @"^Card\.Is(SetCard|Race|Attribute|Type|Code|Level|Rank|Link)$") || extra.Length == 0 || extra.Contains(',')) return Filter(reference, fns);
        var f = new CardFilter();
        Term($"c:{reference[5..]}({extra})", f);
        return f;
    }

    /// <summary>aux.NegateEffectMonsterFilter: "c:IsFaceup() and not c:IsDisabled() and c:IsType(TYPE_EFFECT)".</summary>
    private static bool NegatableEffectMonster(CardFilter f)
    {
        if (f.AnyRequirement || f.MonsterKind is not (MonsterKind.Any or MonsterKind.Effect)) return false;
        f.FaceUpOnly = true;
        f.CardType = FilterCardType.Monster;
        f.MonsterKind = MonsterKind.Effect;
        f.Requirements.Add(Req("negated", RequirementOp.IsNot));
        return true;
    }

    /// <summary>A filter that is one test of the card: "Card.IsPublic", "aux.FilterBoolFunction(Card.IsType,TYPE_TOKEN)", or a function returning one test.</summary>
    private static string? SingleTerm(string reference, Functions fns)
    {
        Match m;
        if ((m = Regex.Match(reference, @"^Card\.(\w+)$")).Success) return $"c:{m.Groups[1].Value}()";
        if ((m = Regex.Match(reference, @"^aux\.FilterBoolFunction\(Card\.(\w+),([^,]+)\)$")).Success) return $"c:{m.Groups[1].Value}({m.Groups[2].Value})";
        if (!Regex.IsMatch(reference, $@"^{Regex.Escape(fns.Prefix)}\.\w+$")) return null;
        var (pars, body) = fns.Get(reference);
        if (pars.Length != 1 || body.Count != 1) return null;
        var line = Regex.Match(body[0].Trim(), @"^return (.+)$");
        if (!line.Success) return null;
        var term = Regex.Replace(line.Groups[1].Value.Trim(), $@"\b{Regex.Escape(pars[0])}:", "c:");
        return SplitTopLevel(term, " or ").Count == 1 && SplitTopLevelAnd(term).Count == 1 ? Unwrap(term) : null;
    }

    /// <summary>"local lv=c:GetLevel() return lv>0 and ...": a local that only names one of the card's numbers is that number.</summary>
    private static string InlineLocals(string text)
    {
        // "... and (c:IsAbleToHand() or c:IsAbleToGrave())" at the end, already taken out, leaves its "and".
        text = Regex.Replace(text.TrimEnd(), @" and$", "");
        Match m;
        while ((m = Regex.Match(text, @"^local (\w+)=(c:Get\w+\(\)) (.*)$")).Success)
        {
            var (name, value, rest) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            // Changed later ("if atk<0 then atk=0 end"): not the same number everywhere.
            if (name is "c" or "e" or "tp" || Regex.IsMatch(rest, $@"\b{name}\s*=(?!=)")
                || !rest.StartsWith("return ", StringComparison.Ordinal) && !rest.StartsWith("local ", StringComparison.Ordinal)) return text;
            text = Regex.Replace(rest, $@"(?<![\w.:]){name}\b", value);
        }
        return text;
    }

    /// <summary>
    /// A filter function's return expression, every term of it read into <paramref name="f"/>. Tests joined by "or"
    /// become one field when they can ("c:IsRace(A) or c:IsRace(B)"), or requirements any one of which is enough.
    /// False when the "or" can't be said either way.
    /// </summary>
    private static bool Conjunction(string expr, CardFilter f)
    {
        var terms = new List<string>();
        List<string>? anyOf = null;
        foreach (var term in Conjuncts(expr))
        {
            var alternatives = SplitTopLevel(term, " or ");
            if (alternatives.Count == 1) { terms.Add(term); continue; }
            if (anyOf is not null) return false;
            anyOf = alternatives.Select(Unwrap).ToList();
        }
        var saved = _siblings;
        _siblings = terms;
        try
        {
            foreach (var term in terms) Term(term, f);
            if (anyOf is not null && !AnyOf(anyOf, f)) return false;
        }
        finally { _siblings = saved; }
        Tidy(f);
        return true;
    }

    /// <summary>The terms an expression requires all of, through brackets and "not (A or B)".</summary>
    private static IEnumerable<string> Conjuncts(string expr)
    {
        expr = Unwrap(expr.Trim());
        if (SplitTopLevel(expr, " or ").Count > 1) { yield return expr; yield break; }
        var parts = SplitTopLevelAnd(expr);
        if (parts.Count == 1)
        {
            // "not (A or B)" is "not A and not B"; "not (A and B)", "not A or not B".
            var not = Regex.Match(expr, @"^not \((.+)\)$");
            if (not.Success && Unwrap($"({not.Groups[1].Value})") != $"({not.Groups[1].Value})")
            {
                var inner = not.Groups[1].Value;
                var ors = SplitTopLevel(inner, " or ");
                if (ors.Count > 1)
                {
                    foreach (var o in ors) foreach (var t in Conjuncts(Negate(Unwrap(o)))) yield return t;
                    yield break;
                }
                var ands = SplitTopLevelAnd(inner);
                if (ands.Count > 1) { yield return string.Join(" or ", ands.Select(a => Negate(Unwrap(a)))); yield break; }
                yield return Negate(Unwrap(inner));
                yield break;
            }
            yield return expr;
            yield break;
        }
        foreach (var part in parts) foreach (var t in Conjuncts(part)) yield return t;
    }

    private static string Negate(string term) =>
        term.StartsWith("not ", StringComparison.Ordinal) ? Unwrap(term[4..]) : SplitTopLevel(term, " or ").Count > 1 || SplitTopLevelAnd(term).Count > 1 ? $"not ({term})" : "not " + term;

    /// <summary>"(A)" without its brackets, when they hold all of it.</summary>
    private static string Unwrap(string expr)
    {
        expr = expr.Trim();
        while (expr.StartsWith('(') && expr.EndsWith(')'))
        {
            int depth = 0;
            for (int i = 0; i < expr.Length; i++)
            {
                if (expr[i] == '(') depth++;
                else if (expr[i] == ')' && --depth == 0 && i < expr.Length - 1) return expr;
            }
            expr = expr[1..^1].Trim();
        }
        return expr;
    }

    /// <summary>Splits on <paramref name="word"/> (" or ") outside brackets.</summary>
    private static List<string> SplitTopLevel(string expr, string word)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            if (expr[i] == '(') depth++;
            else if (expr[i] == ')') depth--;
            else if (depth == 0 && string.CompareOrdinal(expr, i, word, 0, word.Length) == 0)
            {
                parts.Add(expr[start..i].Trim());
                start = i + word.Length;
                i += word.Length - 1;
            }
        }
        parts.Add(expr[start..].Trim());
        return parts;
    }

    /// <summary>"A or B": one field when the tests are of one kind, otherwise one requirement each, any of which is enough.</summary>
    private static bool AnyOf(List<string> alternatives, CardFilter f)
    {
        if (Merged(alternatives) is { } merged)
        {
            try
            {
                var trial = f.Clone();
                Term(merged, trial);
                Copy(trial, f);
                return true;
            }
            catch (Untranslatable) { }
        }
        var reqs = new List<CardRequirement>();
        foreach (var alt in alternatives)
        {
            if (Normalize(alt) is not { } term || RequirementOf(term) is not { } r || !Proven(r.Proof, null)) return OrCards(alternatives, f);
            reqs.Add(r.Req);
        }
        // The filter's other requirements would join the "or" list: only when they can move into fields.
        Tidy(f);
        if (f.Requirements.Count > 0 || f.AnyRequirement) return OrCards(alternatives, f);
        f.Requirements.AddRange(reqs);
        f.AnyRequirement = true;
        return true;
    }

    /// <summary>
    /// "(c:IsSetCard(0x7) and c:IsType(TYPE_MONSTER)) or c:IsCode(37694547)": two kinds of cards, "1 "Ancient Gear" monster or
    /// 1 "Geartown"" (<see cref="CardFilter.OrCards"/>). Each kind may only say what the card is: where it is, face-up and
    /// "other" are the filter's own, for both.
    /// </summary>
    private static bool OrCards(List<string> alternatives, CardFilter f)
    {
        if (alternatives.Count != 2 || f.OrCards is not null || f.AnyRequirement || f.IsSpecific()) return false;
        try
        {
            var (one, two) = (f.Clone(), f.Clone());
            if (!Conjunction(alternatives[0], one) || !Conjunction(alternatives[1], two) || !SameWhere(one, f) || !SameWhere(two, f) || two.OrCards is not null || one.OrCards is not null) return false;
            Copy(one, f);
            f.OrCards = two;
            return true;
        }
        catch (Untranslatable) { return false; }
    }

    /// <summary>The filter says the same about where its cards are and what state they are in.</summary>
    private static bool SameWhere(CardFilter a, CardFilter b) =>
        a.Zones == b.Zones && a.Owner == b.Owner && a.FaceUpOnly == b.FaceUpOnly && a.FaceDownOnly == b.FaceDownOnly && a.Position == b.Position
        && a.NotRevealed == b.NotRevealed && a.ExcludeThisCard == b.ExcludeThisCard && a.ExceptOwnName == b.ExceptOwnName && a.ExceptCode == b.ExceptCode
        && a.PointedToByThisCard == b.PointedToByThisCard && a.CenterZone == b.CenterZone && a.InThisColumn == b.InThisColumn && a.That == b.That && a.ThisCard == b.ThisCard;

    /// <summary>"c:IsRace(A) or c:IsRace(B)" as the one test "c:IsRace(A+B)", the same for codes, archetypes and Levels.</summary>
    private static string? Merged(List<string> alternatives)
    {
        var calls = alternatives.Select(a => Regex.Match(a, @"^c:(Is(?:Fusion|Link|Synchro|Xyz)?(?:Race|Attribute|Type)|IsCode|IsSetCard|IsLevel|IsRank|IsLink)\(([\w+|,]+)\)$")).ToList();
        if (calls.Any(c => !c.Success) || calls.Select(c => c.Groups[1].Value).Distinct().Count() != 1) return null;
        var fn = calls[0].Groups[1].Value;
        var join = fn.EndsWith("Race", StringComparison.Ordinal) || fn.EndsWith("Attribute", StringComparison.Ordinal) || fn.EndsWith("Type", StringComparison.Ordinal) ? "+" : ",";
        if (join == "+" && calls.Any(c => c.Groups[2].Value.Contains(','))) return null;
        return $"c:{fn}({string.Join(join, calls.Select(c => c.Groups[2].Value))})";
    }

    private static void Copy(CardFilter from, CardFilter to)
    {
        foreach (var p in typeof(CardFilter).GetProperties().Where(p => p.CanWrite)) p.SetValue(to, p.GetValue(from));
    }

    /// <summary>Requirements the filter's own fields say the same way, moved there: "Level 4 or lower monster" reads better.</summary>
    private static void Tidy(CardFilter f)
    {
        if (f.AnyRequirement) return;
        foreach (var r in f.Requirements.ToList())
        {
            if (r.Property == "level" && f.MonsterKind != MonsterKind.Xyz && f.LevelMin is null && f.LevelMax is null
                && r.Op is RequirementOp.GreaterOrEqual or RequirementOp.Greater or RequirementOp.LessOrEqual or RequirementOp.Less or RequirementOp.Equal or RequirementOp.Between)
            {
                // The requirement only looks at monsters with a Level, as IsLevelAbove/IsLevelBelow do.
                var (lo, hi) = (Math.Min(r.Value, r.Value2), Math.Max(r.Value, r.Value2));
                (f.LevelMin, f.LevelMax) = r.Op switch
                {
                    RequirementOp.GreaterOrEqual => ((int?)r.Value, (int?)null),
                    RequirementOp.Greater => (r.Value + 1, null),
                    RequirementOp.LessOrEqual => (null, r.Value),
                    RequirementOp.Less => (null, r.Value - 1),
                    RequirementOp.Equal => (r.Value, r.Value),
                    _ => (lo, hi),
                };
                f.Requirements.Remove(r);
            }
            else if (r.Property == "summoned_turn" && r.Op == RequirementOp.Is && f.Summoned == FilterSummoned.Any)
            {
                f.Summoned = FilterSummoned.ThisTurn;
                f.Requirements.Remove(r);
            }
        }
        // "Level 1 or higher" and "Level 4 or lower" together: a Level of 4 or lower is a Level already.
        // So is "whose Level is other than 4": a requirement only looks at monsters with a Level.
        if (f.MonsterKind != MonsterKind.Xyz && f.LevelMin is <= 1 && (f.LevelMax is not null || f.Requirements.Any(r => r.Property == "level"))) f.LevelMin = null;
    }

    // ---------------------------------------------------------------- terms

    /// <summary>
    /// A term that would overwrite what an earlier term of the same filter set ("c:IsSetCard(A) and c:IsSetCard(B)",
    /// "c:IsType(TYPE_EFFECT) and c:IsType(TYPE_TUNER)"): read as a requirement, so both hold.
    /// </summary>
    private static bool Overwrites(string term, string original, CardFilter f)
    {
        Match m;
        CardRequirement? r = null;
        if ((m = Regex.Match(term, @"^c:IsRace\((\w+)\)$")).Success && Races.TryGetValue(m.Groups[1].Value, out var race) && f.Race != CardRace.None && f.Race != race)
            r = Req("race", RequirementOp.Is, values: (long)race);
        else if ((m = Regex.Match(term, @"^c:IsAttribute\((\w+)\)$")).Success && Attributes.TryGetValue(m.Groups[1].Value, out var attr) && f.Attribute != CardAttribute.None && f.Attribute != attr)
            r = Req("attribute", RequirementOp.Is, values: (long)attr);
        else if ((m = Regex.Match(term, @"^c:IsSetCard\(0x([0-9a-fA-F]+)\)$")).Success && (f.Archetype != 0 || f.Archetype2 != 0)
                 && long.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) is var set && set != f.Archetype)
            r = Req("archetype", RequirementOp.Is, values: set);
        else if ((m = Regex.Match(term, @"^c:IsCode\((\d+)\)$")).Success && f.CardCode != 0 && Int64(m.Groups[1].Value) != f.CardCode)
            r = Req("name", RequirementOp.Is, values: Int64(m.Groups[1].Value));
        else if ((m = Regex.Match(term, @"^(not )?c:IsType\((TYPE_\w+)\)$")).Success && MonsterKinds.TryGetValue((m.Groups[1].Success ? "not " : "") + m.Groups[2].Value, out var kind)
                 && f.MonsterKind != MonsterKind.Any && f.MonsterKind != kind)
            r = Req("monster_type", m.Groups[1].Success ? RequirementOp.IsNot : RequirementOp.Is, values: TypeBits[m.Groups[2].Value]);
        else if (f.Summoned != FilterSummoned.Any && term switch
                 {
                     "c:IsSummonType(SUMMON_TYPE_SPECIAL)" => f.Summoned != FilterSummoned.Special,
                     "c:IsSummonType(SUMMON_TYPE_ADVANCE)" => f.Summoned != FilterSummoned.Tribute,
                     "c:IsSummonLocation(LOCATION_EXTRA)" => f.Summoned != FilterSummoned.FromExtra,
                     _ => false,
                 })
            r = term == "c:IsSummonLocation(LOCATION_EXTRA)" ? Req("summoned_from", RequirementOp.Is, values: 0x40) : Req("summon", RequirementOp.Is, values: term.Contains("SPECIAL") ? 3 : 1);
        else if (f.Position != FilterPosition.Any && term is "c:IsAttackPos()" or "c:IsPosition(POS_ATTACK)" or "c:IsDefensePos()" or "c:IsPosition(POS_DEFENSE)")
            r = Req("position", RequirementOp.Is, values: term.Contains("Attack") || term.Contains("ATTACK") ? 0x3 : 0xc);
        if (r is null) return false;
        if (f.AnyRequirement) throw No($"it picks cards by {Short(original)} as well as another test of the same kind");
        f.Requirements.Add(r);
        // Kinds and Summons say the card is a monster, as the field they would have set does.
        if (r.Property is "monster_type" or "summon" or "summoned_from" && f.CardType == FilterCardType.Any) f.CardType = FilterCardType.Monster;
        return true;
    }

    /// <summary>A term the older readers don't take, read into the filter's fields or a requirement. False when it can't be.</summary>
    private static bool RequirementTerm(string term, string original, CardFilter f)
    {
        if (Normalize(term) is not { } t) return false;
        // Spelled another way ("(c:IsFaceup())", "c:GetType()&TYPE_NORMAL~=0"): read as the plain test, by the readers that take it.
        if (t != term) { Term(t, f); return true; }
        if (FieldTerm(t, f)) return true;
        if (RequirementOf(t) is not { } r) return false;
        if (!Proven(r.Proof, original)) throw No($"it picks cards by {Short(original)}, which also counts cards that have no such number");
        if (f.AnyRequirement) return false;
        f.Requirements.Add(r.Req);
        return true;
    }

    /// <summary>One spelling for the same test: "bit.band(c:GetType(),X)" is "c:GetType()&amp;X", "c:IsAllTypes(X)" is "c:GetType()&amp;X==X".</summary>
    private static string? Normalize(string term)
    {
        term = Unwrap(term);
        // Materials' material forms, as Term reads them: the filter compiles its requirements in the form of its Summon.
        term = Regex.Replace(term, @"^(not )?c:Is(?:Fusion|Link|Synchro|Xyz)(Type|Attribute|Race|SetCard|Code)\(", "$1c:Is$2(");
        term = Regex.Replace(term, @"bit\.band\((c:Get\w+\(\)),([\w+|]+)\)", "$1&$2");
        // "(c:GetType()&X)~=0": the brackets Lua needs around a mask.
        term = Regex.Replace(term, @"^\((c:Get\w+\(\)&\(?[\w+|]+\)?)\)", "$1");
        term = Regex.Replace(term, @"^(c:Get\w+\(\))&\(([\w+|]+)\)", "$1&$2");
        // "c:GetType()&TYPE_NORMAL~=0" is IsType(TYPE_NORMAL); "==0" is "not".
        var any = Regex.Match(term, @"^c:GetType\(\)&([\w+|]+)(~=0|>0|==0)$");
        if (any.Success) term = (any.Groups[2].Value == "==0" ? "not " : "") + $"c:IsType({any.Groups[1].Value})";
        // Original Attribute and Type: "&X~=0", and "==X" or "&X==X" for one of them (a card has one printed).
        var orig = Regex.Match(term, @"^c:GetOriginal(Attribute|Race)\(\)(?:&([\w+|]+)(~=0|>0|==([\w+|]+))|==(\w+))$");
        if (orig.Success)
        {
            var mask = orig.Groups[5].Success ? orig.Groups[5].Value : orig.Groups[2].Value;
            if (orig.Groups[4].Success && orig.Groups[4].Value != mask || (orig.Groups[4].Success || orig.Groups[5].Success) && mask.IndexOfAny(['+', '|']) >= 0) return term;
            term = $"c:IsOriginal{orig.Groups[1].Value}({mask})";
        }
        var all = Regex.Match(term, @"^c:IsAllTypes\(([\w+|]+)\)$");
        if (all.Success) term = $"c:GetType()&{all.Groups[1].Value}=={all.Groups[1].Value}";
        return term;
    }

    /// <summary>Terms that are the filter's own fields, said in a way the older readers don't take.</summary>
    private static bool FieldTerm(string term, CardFilter f)
    {
        Match m;
        // "c:GetAttack()>=1500" is IsAttackAbove(1500): a card that isn't a monster has 0 ATK to the engine.
        if ((m = Regex.Match(term, @"^c:Get(Attack|Defense)\(\)(>=|>|==)(\d+)$")).Success)
        {
            var n = Int(m.Groups[3].Value);
            var (min, max) = m.Groups[2].Value switch { ">=" => (n, (int?)null), ">" => (n + 1, null), _ => (n, (int?)n) };
            if (min < 1) return false;
            if (m.Groups[1].Value == "Attack")
            {
                f.AtkMin = Math.Max(f.AtkMin ?? min, min);
                if (max is { } a) f.AtkMax = Math.Min(f.AtkMax ?? a, a);
            }
            else
            {
                f.DefMin = Math.Max(f.DefMin ?? min, min);
                if (max is { } d) f.DefMax = Math.Min(f.DefMax ?? d, d);
            }
            return true;
        }
        // The center Main Monster Zone: the sequence says so only of a card in a Monster Zone, which SettleRequirements checks.
        if (term == "c:GetSequence()==2") { f.CenterZone = true; return true; }
        // "c:GetType()==TYPE_SPELL+TYPE_RITUAL": a Spell is "Spell" and one more type at most, so that is all of them. (Not
        // Continuous Traps, which a Trap Monster can still be; and not "TYPE_SPELL" alone, which a Pendulum Zone card isn't.)
        if ((m = Regex.Match(term, @"^c:GetType\(\)==([\w+|]+)$")).Success && Mask(m.Groups[1].Value, TypeBits) is { } exact)
        {
            if (exact is 0x2 + 0x80 or 0x2 + 0x10000 or 0x2 + 0x20000 or 0x2 + 0x40000 or 0x2 + 0x80000 or 0x4 + 0x100000) return AllTypes(exact, f);
            // A Normal Trap: the filter's Normal kind leaves out every other type a Trap can have, a Trap Monster's too.
            if (exact == 0x4 && f.CardType is FilterCardType.Any or FilterCardType.Trap && f.SpellTrapKind is SpellTrapKind.Any or SpellTrapKind.Normal)
            {
                (f.CardType, f.SpellTrapKind) = (FilterCardType.Trap, SpellTrapKind.Normal);
                return true;
            }
        }
        if (term == "aux.NegateEffectMonsterFilter(c)") return NegatableEffectMonster(f);
        if (term is "aux.nzatk(c)") { f.FaceUpOnly = true; f.AtkMin = Math.Max(f.AtkMin ?? 1, 1); return true; }
        if (term is "aux.nzdef(c)") { f.FaceUpOnly = true; f.DefMin = Math.Max(f.DefMin ?? 1, 1); return true; }
        // "c:GetType()&TYPE_RITUAL+TYPE_MONSTER==TYPE_RITUAL+TYPE_MONSTER": a Ritual Monster.
        if ((m = Regex.Match(term, @"^c:GetType\(\)&([\w+|]+)==([\w+|]+)$")).Success && Mask(m.Groups[1].Value, TypeBits) is { } mask && Mask(m.Groups[2].Value, TypeBits) == mask)
            return AllTypes(mask, f);
        return false;
    }

    /// <summary>A card that has every one of the types in <paramref name="mask"/>.</summary>
    private static bool AllTypes(long mask, CardFilter f)
    {
        var basic = mask & 0x7;
        var rest = Bits(mask & ~0x7L).ToList();
        if (basic is not (0 or 0x1 or 0x2 or 0x4)) return false;
        var kind = basic switch { 0x1 => FilterCardType.Monster, 0x2 => FilterCardType.Spell, 0x4 => FilterCardType.Trap, _ => FilterCardType.Any };
        if (kind != FilterCardType.Any && f.CardType != FilterCardType.Any && f.CardType != kind) return false;
        if (kind is FilterCardType.Spell or FilterCardType.Trap)
        {
            if (rest.Count > 1) return false;
            if (rest.Count == 1)
            {
                var st = (kind, rest[0]) switch
                {
                    (FilterCardType.Spell, 0x10000) => SpellTrapKind.QuickPlay, (_, 0x20000) => SpellTrapKind.Continuous, (FilterCardType.Spell, 0x40000) => SpellTrapKind.Equip,
                    (FilterCardType.Spell, 0x80000) => SpellTrapKind.Field, (FilterCardType.Spell, 0x80) => SpellTrapKind.Ritual, (FilterCardType.Trap, 0x100000) => SpellTrapKind.Counter,
                    _ => SpellTrapKind.Any,
                };
                if (st == SpellTrapKind.Any || f.SpellTrapKind != SpellTrapKind.Any && f.SpellTrapKind != st) return false;
                f.SpellTrapKind = st;
            }
            f.CardType = kind;
            return true;
        }
        var choices = CardProperties.Find("monster_type")!.Choices!.Select(c => c.Value).ToHashSet();
        if (rest.Any(b => !choices.Contains(b)) || f.AnyRequirement) return false;
        if (kind == FilterCardType.Monster) f.CardType = FilterCardType.Monster;
        foreach (var bit in rest)
        {
            // With "a monster", one kind is the filter's own: "Ritual Monster". The others are requirements.
            var k = MonsterKinds.FirstOrDefault(p => !p.Key.StartsWith("not ", StringComparison.Ordinal) && TypeBits[p.Key] == bit).Value;
            if (kind == FilterCardType.Monster && f.MonsterKind == MonsterKind.Any && k != MonsterKind.Any) f.MonsterKind = k;
            else if (f.MonsterKind != k) f.Requirements.Add(Req("monster_type", RequirementOp.Is, values: bit));
        }
        return true;
    }

    /// <summary>
    /// A term (as <see cref="Normalize"/> spells it) as one requirement, and what the rest of the filter must say for it
    /// to be exact; null when it isn't one.
    /// </summary>
    private static (CardRequirement Req, Proof Proof)? RequirementOf(string term)
    {
        var not = term.StartsWith("not ", StringComparison.Ordinal);
        var t = not ? Unwrap(term[4..]) : term;
        var isOp = not ? RequirementOp.IsNot : RequirementOp.Is;
        Match m;
        (CardRequirement, Proof) Plain(CardRequirement r) => (r, Proof.None);

        // ---- choices
        if ((m = Regex.Match(t, @"^c:Is(Original)?Race\(([\w+|]+)\)$")).Success && Mask(m.Groups[2].Value, Races.ToDictionary(p => p.Key, p => (long)p.Value)) is { } races
            && Choices("race", races) is { } rv)
            return Plain(Req(m.Groups[1].Success ? "orig_race" : "race", isOp, values: rv));
        if ((m = Regex.Match(t, @"^c:Is(Non|Original)?Attribute\((.+)\)$")).Success && AttributeMask(m.Groups[2].Value) is { } attrs)
        {
            // IsNonAttribute(X): an Attribute other than X, which is "one of all the others".
            if (m.Groups[1].Value == "Non") attrs = 0x7f & ~attrs;
            if (Choices("attribute", attrs) is { } av) return Plain(Req(m.Groups[1].Value == "Original" ? "orig_attribute" : "attribute", isOp, values: av));
        }
        if ((m = Regex.Match(t, @"^c:IsType\(([\w+|]+)\)$")).Success && Mask(m.Groups[1].Value, TypeBits) is { } types)
        {
            if ((types & ~0x7L) == 0 && Choices("card_type", types) is { } cv) return Plain(Req("card_type", isOp, values: cv));
            if ((types & 0x7) == 0 && Choices("monster_type", types) is { } mv) return Plain(Req("monster_type", isOp, values: mv));
        }
        if ((m = Regex.Match(t, @"^c:IsSetCard\((0x[0-9a-fA-F]+(?:,0x[0-9a-fA-F]+)*)\)$")).Success)
            return Plain(Req("archetype", isOp, values: [.. m.Groups[1].Value.Split(',').Select(x => long.Parse(x[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture))]));
        if ((m = Regex.Match(t, @"^c:IsCode\((\d+(?:,\d+)*)\)$")).Success)
            return Plain(Req("name", isOp, values: [.. m.Groups[1].Value.Split(',').Select(Int64)]));
        if ((m = Regex.Match(t, @"^c:IsOriginalCodeRule\((\d+(?:,\d+)*)\)$")).Success)
            return Plain(Req("orig_name", isOp, values: [.. m.Groups[1].Value.Split(',').Select(Int64)]));
        if ((m = Regex.Match(t, @"^c:IsSummonType\((SUMMON_TYPE_\w+)\)$")).Success && SummonKinds.TryGetValue(m.Groups[1].Value, out var how))
            return Plain(Req("summon", isOp, values: how));
        if ((m = Regex.Match(t, @"^c:IsSummonLocation\(([\w+|]+)\)$")).Success && Mask(m.Groups[1].Value, SummonPlaces) is { } from)
            return Plain(Req("summoned_from", isOp, values: [.. Bits(from)]));
        if ((m = Regex.Match(t, @"^c:IsPosition\((POS_\w+)\)$")).Success && Positions.TryGetValue(m.Groups[1].Value, out var pos))
            return Plain(Req("position", isOp, values: pos));
        if ((m = Regex.Match(t, @"^c:IsLocation\(([\w+|]+)\)$")).Success && Mask(m.Groups[1].Value, Locations) is { } where && Choices("location", where) is { } lv)
            return Plain(Req("location", isOp, values: lv));
        if (t == "c:IsOnField()") return Plain(Req("location", isOp, values: [0x4, 0x8]));
        // "c:GetOriginalType()&TYPE_MONSTER~=0": a monster card, whatever it is treated as now.
        if ((m = Regex.Match(t, @"^c:GetOriginalType\(\)&([\w+|]+)(~=0|>0|==0|==([\w+|]+))$")).Success && Mask(m.Groups[1].Value, TypeBits) is { } orig
            && (!m.Groups[3].Success || Bits(orig).Count() == 1 && Mask(m.Groups[3].Value, TypeBits) == orig) && Choices("orig_card_type", orig) is { } ov)
            return Plain(Req("orig_card_type", m.Groups[2].Value == "==0" != not ? RequirementOp.IsNot : RequirementOp.Is, values: ov));
        switch (t)
        {
            case "c:IsFaceup()": return Plain(Req("position", isOp, values: 0x5));
            case "c:IsFacedown()": return Plain(Req("position", isOp, values: 0xa));
            case "c:IsAttackPos()": return Plain(Req("position", isOp, values: 0x3));
            case "c:IsDefensePos()": return Plain(Req("position", isOp, values: 0xc));
            case "c:IsDisabled()": return Plain(Req("negated", isOp));
            case "c:IsLinkState()": return Plain(Req("is_linked", isOp));
            // Not a monster: neither equal stats nor a monster that can't be Normal Summoned, so "not" needs a monster.
            case "aux.AtkEqualsDef(c)": return (Req("atk_eq_def", isOp), not ? Proof.Monster : Proof.None);
            case "c:IsSummonableCard()": return (Req("not_summonable", not ? RequirementOp.Is : RequirementOp.IsNot), not ? Proof.Monster : Proof.None);
        }
        if ((m = Regex.Match(t, @"^c:IsStatus\(([\w+|]+)\)$")).Success && m.Groups[1].Value.Split('+', '|').Order(StringComparer.Ordinal).SequenceEqual(["STATUS_FLIP_SUMMON_TURN", "STATUS_SPSUMMON_TURN", "STATUS_SUMMON_TURN"]))
            return Plain(Req("summoned_turn", isOp));
        if (t == "c:IsStatus(STATUS_SPSUMMON_TURN)") return Plain(Req("spsummoned_turn", isOp));
        if ((m = Regex.Match(t, @"^c:IsStatus\(([\w+|]+)\)$")).Success && m.Groups[1].Value.Split('+', '|').Order(StringComparer.Ordinal).SequenceEqual(["STATUS_SPSUMMON_TURN", "STATUS_SUMMON_TURN"]))
            return Plain(Req("normal_special_turn", isOp));
        // In an Extra Monster Zone: the sequence says so only of a card in a Monster Zone, which SettleRequirements checks.
        if (!not && t is "c:GetSequence()>4" or "c:GetSequence()>=5") return Plain(Req("extra_zone", RequirementOp.Is));
        // ATK changed. A card that isn't a monster has 0 for both, so "~=" leaves it out, but "==" and "not IsAttack" pick it;
        // IsAttack also reads a monster card that is a Spell for now.
        if (t is "c:GetAttack()~=c:GetBaseAttack()" or "c:GetAttack()==c:GetBaseAttack()")
        {
            var changed = t.Contains("~=") != not;
            return (Req("changed_atk", changed ? RequirementOp.Is : RequirementOp.IsNot), changed ? Proof.None : Proof.Monster);
        }
        if (t is "c:IsAttack(c:GetBaseAttack())") return (Req("changed_atk", not ? RequirementOp.Is : RequirementOp.IsNot), Proof.Monster);

        // ---- numbers
        if ((m = Regex.Match(t, @"^c:Is(Level|Rank|Link|Attack|Defense)\((\d+(?:,\d+)*)\)$")).Success)
        {
            var values = m.Groups[2].Value.Split(',').Select(Int).Distinct().Order().ToList();
            var prop = Stat(m.Groups[1].Value);
            // IsLevel(7,8): one of them, which is a range when they follow on.
            if (values[^1] - values[0] != values.Count - 1) return null;
            var r = values.Count == 1
                ? Req(prop, not ? RequirementOp.NotEqual : RequirementOp.Equal, values[0])
                : Req(prop, not ? RequirementOp.Outside : RequirementOp.Between, values[0], values[^1]);
            return (r, not ? NumberProof(prop) : Proof.None);
        }
        if ((m = Regex.Match(t, @"^c:Is(Level|Rank|Link|Attack|Defense)(Above|Below)\((\d+)\)$")).Success)
        {
            var prop = Stat(m.Groups[1].Value);
            var n = Int(m.Groups[3].Value);
            // IsAttackBelow and IsDefenseBelow leave out "?" ATK/DEF (-1).
            var r = (m.Groups[2].Value, prop) switch
            {
                ("Above", _) => Req(prop, not ? RequirementOp.Less : RequirementOp.GreaterOrEqual, n),
                (_, "atk" or "def") => Req(prop, not ? RequirementOp.Outside : RequirementOp.Between, 0, n),
                _ => Req(prop, not ? RequirementOp.Greater : RequirementOp.LessOrEqual, n),
            };
            return (r, not ? NumberProof(prop) : Proof.None);
        }
        if ((m = Regex.Match(t, @"^c:(Get\w+)\(\)(>=|<=|==|~=|>|<)(\d+)$")).Success && !not)
        {
            var n = Int(m.Groups[3].Value);
            var op = m.Groups[2].Value switch
            {
                ">=" => RequirementOp.GreaterOrEqual, "<=" => RequirementOp.LessOrEqual, "==" => RequirementOp.Equal,
                "~=" => RequirementOp.NotEqual, ">" => RequirementOp.Greater, _ => RequirementOp.Less,
            };
            var zero = op switch
            {
                RequirementOp.GreaterOrEqual => n <= 0, RequirementOp.LessOrEqual => n >= 0, RequirementOp.Equal => n == 0,
                RequirementOp.NotEqual => n != 0, RequirementOp.Greater => false, _ => n > 0,
            };
            // Counts that are only "some" or "none": "has material", "is equipped", "declared an attack".
            var state = m.Groups[1].Value switch
            {
                "GetOverlayCount" => "has_materials", "GetEquipCount" => "equipped", "GetAttackAnnouncedCount" => "attacked_turn",
                "GetBattledGroupCount" => "battled_turn", _ => null,
            };
            var some = (op, n) switch
            {
                (RequirementOp.Greater, 0) or (RequirementOp.NotEqual, 0) or (RequirementOp.GreaterOrEqual, 1) => true,
                (RequirementOp.Equal, 0) or (RequirementOp.Less, 1) or (RequirementOp.LessOrEqual, 0) => false,
                _ => (bool?)null,
            };
            if (state is not null && some is { } yes) return Plain(Req(state, yes ? RequirementOp.Is : RequirementOp.IsNot));
            var prop = m.Groups[1].Value switch
            {
                "GetAttack" => "atk", "GetDefense" => "def", "GetBaseAttack" => "base_atk", "GetBaseDefense" => "base_def", "GetLevel" => "level",
                "GetOriginalLevel" => "orig_level", "GetRank" => "rank", "GetLink" => "link", "GetOverlayCount" => "materials", "GetEquipCount" => "equips",
                "GetLinkedGroupCount" => "linked", "GetAttackAnnouncedCount" => "attacks", _ => null,
            };
            if (prop is null) return null;
            // A number 0 passes: the cards without it (0 to the engine) pass the script's test, and the rest of the filter must leave them out.
            if (zero && prop == "orig_level") return null;
            return (Req(prop, op, n), zero ? NumberProof(prop) : Proof.None);
        }
        return null;
    }

    private static string Stat(string name) => name switch { "Level" => "level", "Rank" => "rank", "Link" => "link", "Attack" => "atk", _ => "def" };

    /// <summary>What shows that a card has <paramref name="prop"/> at all.</summary>
    private static Proof NumberProof(string prop) => prop switch
    {
        "atk" or "base_atk" or "equips" or "attacks" => Proof.Monster,
        "def" or "base_def" => Proof.MonsterNotLink,
        "level" => Proof.HasLevel,
        "rank" or "materials" => Proof.Xyz,
        _ => Proof.Link,
    };

    /// <summary>Whether the filter's other terms (<see cref="_siblings"/>, not <paramref name="self"/>) show what <paramref name="proof"/> asks.</summary>
    private static bool Proven(Proof proof, string? self)
    {
        // "A monster" can also come from where the filter looks (only Monster Zones), which the whole piece shows: see SettleRequirements.
        if (proof is Proof.None or Proof.Monster) return true;
        if (_siblings is not { } siblings) return false;
        var scratch = new CardFilter();
        _siblings = null;
        try
        {
            foreach (var s in siblings.Where(s => s != self))
            {
                try { Term(s, scratch); }
                catch (Untranslatable) { }
            }
        }
        finally { _siblings = siblings; }
        var monster = scratch.CardType == FilterCardType.Monster;
        var leveled = scratch.MonsterKind != MonsterKind.Xyz && (scratch.LevelMin >= 1 || scratch.LevelMax is not null);
        return proof switch
        {
            Proof.Monster => monster,
            Proof.MonsterNotLink => monster && (scratch.MonsterKind is MonsterKind.Fusion or MonsterKind.Synchro or MonsterKind.Xyz or MonsterKind.Ritual
                || scratch.DefMin is not null || scratch.DefMax is not null
                || scratch.Requirements.Any(r => r.Property == "monster_type" && r.Op == RequirementOp.IsNot && r.Values.Contains(0x4000000))),
            Proof.HasLevel => leveled || scratch.Requirements.Any(r => r.Property == "level" && r.Op is RequirementOp.GreaterOrEqual or RequirementOp.Greater or RequirementOp.Between
                || r.Property == "level" && r.Op == RequirementOp.Equal && r.Value > 0),
            Proof.Xyz => scratch.MonsterKind == MonsterKind.Xyz,
            _ => scratch.MonsterKind == MonsterKind.Link,
        };
    }

    // ---------------------------------------------------------------- whole pieces

    /// <summary>
    /// A requirement on ATK, DEF, Rank or Link Rating looks only at cards that are monsters now, where the script's getter
    /// gives 0 to a card that isn't a monster and also reads a monster card that is a Spell for now (a Pendulum Monster in
    /// the Pendulum Zone). Where those cards can be, the filter must say "monster" itself: in the Monster Zones every card
    /// is a monster, and outside the Spell &amp; Trap Zones no monster card is a Spell.
    /// </summary>
    /// <remarks>
    /// "In the GY" among a filter's places is the filter looking only there: the requirement becomes its zones.
    /// </remarks>
    private static TranslatedPiece SettleRequirements(TranslatedPiece piece)
    {
        var filters = piece.Effects.SelectMany(e => new[] { e.When }.Concat(e.AlsoWhen).Concat(e.If).Concat(e.Cost).Concat(e.Do))
            .OfType<BlockInstance>().SelectMany(b => b.Filters.Values);
        foreach (var f in filters)
        {
            if (!f.AnyRequirement && f.Zones != Model.Zone.None && f.Requirements.FirstOrDefault(r => r.Property == "location" && r.Op == RequirementOp.Is) is { } at
                && (f.Zones & ZonesOf(at.Values)) is var narrowed && narrowed != Model.Zone.None && !f.PointedToByThisCard && !f.CenterZone)
            {
                f.Zones = narrowed;
                f.Requirements.Remove(at);
            }
            var monster = f.CardType == FilterCardType.Monster;
            var onlyMonsters = monster || f.Zones == Model.Zone.MonsterZone;
            // Not said (an event's cards) may be anywhere.
            var noSpellZone = f.Zones != Model.Zone.None && (f.Zones & Model.Zone.SpellTrapZone) == 0;
            foreach (var r in f.Requirements)
            {
                var zero = PassesZero(r);
                var proven = r.Property switch
                {
                    "atk" or "base_atk" => zero ? onlyMonsters : monster || noSpellZone,
                    "def" or "base_def" => monster || noSpellZone,
                    "equips" or "attacks" => !zero || onlyMonsters,
                    "changed_atk" => onlyMonsters,
                    "rank" => f.MonsterKind == MonsterKind.Xyz || noSpellZone,
                    "link" => f.MonsterKind == MonsterKind.Link || noSpellZone,
                    _ => true,
                };
                if (!proven) throw No($"it picks cards by {CardProperties.Find(r.Property)!.Noun switch { "" => r.Property, var n => n }} among cards that aren't monsters now");
            }
            if ((f.CenterZone || f.Requirements.Any(r => r.Property == "extra_zone")) && f.Zones != Model.Zone.MonsterZone) throw No(ZoneBySequence);
            if (f.Requirements.FindIndex(r => r.Property == "main_zone") is var seq && seq >= 0)
            {
                if (f.AnyRequirement || (f.Zones & ~Model.Zone.Field) != 0 || f.Zones == Model.Zone.None) throw No(ZoneBySequence);
                // In the Monster Zones it is "not in the Extra Monster Zone"; in the Spell & Trap Zones only a Field Spell is in the Field Zone.
                if (f.Zones == Model.Zone.MonsterZone) f.Requirements[seq] = Req("extra_zone", RequirementOp.IsNot);
                else if (f.Zones == Model.Zone.SpellTrapZone)
                {
                    if (f.SpellTrapKind is not SpellTrapKind.Any and not SpellTrapKind.Field || f.CardType is FilterCardType.Monster or FilterCardType.Trap) f.Requirements.RemoveAt(seq);
                    else f.Requirements[seq] = Req("card_type", RequirementOp.IsNot, values: 0x80000);
                }
            }
        }
        // A material can come from elsewhere than a Monster Zone, where its sequence means something else. The monster treated
        // as a Tuner is one in the Monster Zone, as the blocks check it.
        if (piece.Procedure is { } proc && typeof(SummonProcedure).GetProperties().Where(p => p.PropertyType == typeof(CardFilter) && p.Name != nameof(SummonProcedure.SynchroAsTunerFilter))
                .Select(p => (CardFilter?)p.GetValue(proc))
                .Any(f => f is not null && (f.CenterZone || f.Requirements.Any(r => r.Property == "extra_zone"))))
            throw No(ZoneBySequence);
        return piece;
    }

    private const string ZoneBySequence = "it picks cards by their zone's number (GetSequence) where that isn't only a Monster Zone";

    private static Model.Zone ZonesOf(IEnumerable<long> locations) => locations.Aggregate(Model.Zone.None, (z, l) => z | l switch
    {
        0x1 => Model.Zone.Deck, 0x2 => Model.Zone.Hand, 0x4 => Model.Zone.MonsterZone, 0x8 => Model.Zone.SpellTrapZone,
        0x10 => Model.Zone.Graveyard, 0x20 => Model.Zone.Banished, 0x40 => Model.Zone.ExtraDeck, _ => Model.Zone.None,
    });

    /// <summary>Whether a number of 0 meets the requirement: the engine's number for a card that doesn't have it.</summary>
    private static bool PassesZero(CardRequirement r)
    {
        var (lo, hi) = (Math.Min(r.Value, r.Value2), Math.Max(r.Value, r.Value2));
        return r.Op switch
        {
            RequirementOp.Equal => r.Value == 0, RequirementOp.NotEqual => r.Value != 0, RequirementOp.Greater => r.Value < 0,
            RequirementOp.GreaterOrEqual => r.Value <= 0, RequirementOp.Less => r.Value > 0, RequirementOp.LessOrEqual => r.Value >= 0,
            RequirementOp.Between => lo <= 0 && hi >= 0, RequirementOp.Outside => lo > 0 || hi < 0,
            _ => false,
        };
    }

    // ---------------------------------------------------------------- helpers

    private static CardRequirement Req(string property, RequirementOp op, int value = 0, int value2 = 0, params long[] values) =>
        new() { Property = property, Op = op, Value = value, Value2 = value2, Values = [.. values] };

    /// <summary>"TYPE_FUSION+TYPE_SYNCHRO", "RACE_DRAGON|RACE_WYRM", "0x81": the bits, or null with a name it doesn't know.</summary>
    private static long? Mask(string expr, IReadOnlyDictionary<string, long> names)
    {
        long mask = 0;
        foreach (var part in expr.Split('+', '|'))
        {
            var p = part.Trim();
            if (names.TryGetValue(p, out var v)) mask |= v;
            else if (p.StartsWith("0x", StringComparison.Ordinal) && long.TryParse(p[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) mask |= hex;
            else if (long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var dec)) mask |= dec;
            else return null;
        }
        return mask == 0 ? null : mask;
    }

    /// <summary>Attribute bits, with "ATTRIBUTE_ALL&amp;~ATTRIBUTE_WATER" for all but some.</summary>
    private static long? AttributeMask(string expr)
    {
        var names = Attributes.ToDictionary(p => p.Key, p => (long)p.Value);
        names["ATTRIBUTE_ALL"] = 0x7f;
        var but = Regex.Match(expr, @"^ATTRIBUTE_ALL&~\(?([\w+|]+)\)?$");
        if (but.Success) return Mask(but.Groups[1].Value, names) is { } x ? 0x7f & ~x : null;
        return Mask(expr, names);
    }

    private static IEnumerable<long> Bits(long mask)
    {
        for (long b = 1; b != 0 && b <= mask; b <<= 1)
            if ((mask & b) != 0) yield return b;
    }

    /// <summary>The picker's choices <paramref name="mask"/> is made of, or null when it has a bit none of them is.</summary>
    private static long[]? Choices(string property, long mask)
    {
        var choices = CardProperties.Find(property)!.Choices!.Select(c => c.Value).ToList();
        var bits = Bits(mask).ToArray();
        return bits.All(choices.Contains) ? bits : null;
    }
}
