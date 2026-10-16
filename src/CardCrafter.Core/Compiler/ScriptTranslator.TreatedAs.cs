using System.Globalization;
using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Everything "treated as": Special Summons treated as a Ritual/Fusion/Synchro/Xyz/Link Summon, a card treated as an
/// archetype, a name, an Attribute, a Type or a Normal Monster in some places, its name becoming another's, and how it
/// counts as material (Xyz and Synchro Levels, non-Tuner, the entire Ritual Tribute, Fusion substitutes, 2 Xyz
/// materials). Each shape is read only when the block writes the same effect, with the same settings, as the official
/// script; anything else stays script.
/// </summary>
public static partial class ScriptTranslator
{
    // ---------------------------------------------------------------- Special Summons treated as another Summon

    /// <summary>Set by <see cref="TreatedAsSummonAction"/>: whether the script counts the zone this card leaves, as the block does with a this-card cost.</summary>
    [ThreadStatic] private static bool? _treatedLeaving;

    private static readonly Dictionary<string, string> MustMaterialCodes = new()
    {
        ["FUSION"] = "EFFECT_MUST_BE_FMATERIAL", ["SYNCHRO"] = "EFFECT_MUST_BE_SMATERIAL", ["XYZ"] = "EFFECT_MUST_BE_XMATERIAL", ["LINK"] = "EFFECT_MUST_BE_LMATERIAL",
    };

    /// <summary>
    /// "tc:SetMaterial(nil) if Duel.SpecialSummon(tc,SUMMON_TYPE_SYNCHRO,tp,tp,false,false,POS_FACEUP)>0 then
    /// tc:CompleteProcedure() end", as one marked statement the Action reader turns into the Special Summon block's
    /// "treated as" option, when the rest of the operation and its filter check what the block checks: the "must be
    /// used as material" effects, CheckFusionMaterial, the Summon type, and the zone left from the Extra Deck.
    /// </summary>
    private static string TreatedAsRewrite(string lua)
    {
        var summon = new Regex(@"(?m)^([ \t]*)(tc|c):SetMaterial\(nil\)[ \t]*\n[ \t]*(?:if )?Duel\.SpecialSummon\(\2,SUMMON_TYPE_(RITUAL|FUSION|SYNCHRO|XYZ|LINK),tp,tp,(true|false),(true|false),(POS_FACEUP(?:_ATTACK|_DEFENSE)?)\)" +
                               @"(?:(?:~=0|>0) then[ \t]*\n[ \t]*\2:CompleteProcedure\(\)[ \t]*\n[ \t]*end|[ \t]*\n[ \t]*\2:CompleteProcedure\(\))[ \t]*$");
        // The filters to read as the block does, applied once every operation is read.
        var filters = new Dictionary<string, string>();
        var source = lua;
        lua = Regex.Replace(lua, @"(?ms)^function ([\w]+)\.(\w+)\(e,tp,eg,ep,ev,re,r,rp\)\n(.*?)^end", fn =>
        {
            var body = fn.Groups[3].Value;
            var matches = summon.Matches(body);
            if (matches.Count != 1) return fn.Value;
            var m = matches[0];
            var (kind, nocheck, nolimit) = (m.Groups[3].Value, m.Groups[4].Value, m.Groups[5].Value);
            // The block's switches: Ritual Summons lift the revive limit, and "ignoring its Summoning conditions" both.
            var ignore = nocheck == "true";
            if (nolimit != (kind == "RITUAL" || ignore ? "true" : "false")) return fn.Value;
            if (MustMaterialCodes.TryGetValue(kind, out var must))
            {
                var check = new Regex($@"(?m)^[ \t]*if not aux\.MustMaterialCheck\(nil,tp,{must}\) then return end[ \t]*\n");
                if (!check.IsMatch(body)) return fn.Value;
                body = check.Replace(body, "", 1);
            }
            // The card picked: its filter must make the block's checks, and the zone the Extra Deck monster needs is the block's.
            var prefix = fn.Groups[1].Value;
            var leaving = (bool?)null;
            if (m.Groups[2].Value == "tc")
            {
                var pick = Regex.Match(body, $@"Duel\.SelectMatchingCard\(tp,(?:aux\.NecroValleyFilter\()?{Regex.Escape(prefix)}\.(\w+)\)?,tp,[\w+]+,0,1,1,nil,e,tp(,nil|,e:GetHandler\(\)|,c)?\)");
                if (!pick.Success) return fn.Value;
                var name = pick.Groups[1].Value;
                if (FilterForTreated(source, prefix, name, kind, nocheck, nolimit) is not { } rewritten) return fn.Value;
                filters[rewritten.Old] = rewritten.New;
                leaving = pick.Groups[2].Value is ",e:GetHandler()" or ",c";
                // "...,e,tp,nil)": the extra argument is the card leaving the field, which the block works out itself.
                if (pick.Groups[2].Value.Length > 0) body = body.Remove(pick.Groups[2].Index, pick.Groups[2].Length);
                // The activation check passes the card leaving the field the same way.
                if (Regex.IsMatch(source, $@"IsExistingMatchingCard\({Regex.Escape(prefix)}\.{name},tp,[\w+]+,0,1,nil,e,tp,(?:e:GetHandler\(\)|c)\)")) leaving = true;
                // "local tc=Duel.SelectMatchingCard(...):GetFirst()": the pick, then its one card, as the operation reader follows them.
                body = Regex.Replace(body, @"(?m)^([ \t]*)local tc=(Duel\.SelectMatchingCard\(.*\)):GetFirst\(\)[ \t]*$", "$1local ccg=$2\n$1local tc=ccg:GetFirst()");
            }
            m = summon.Match(body);
            var marker = $"{m.Groups[1].Value}Duel.SpecialSummon({m.Groups[2].Value},CCTREATED{(ignore ? "I" : "")}{(leaving == true ? "L" : "")}_{kind},tp,tp,false,false,{m.Groups[6].Value})";
            body = body.Remove(m.Index, m.Length).Insert(m.Index, marker);
            return $"function {prefix}.{fn.Groups[2].Value}(e,tp,eg,ep,ev,re,r,rp)\n{body}end";
        });
        foreach (var (old, rewrittenFilter) in filters) lua = lua.Replace(old, rewrittenFilter, StringComparison.Ordinal);
        return lua;
    }

    /// <summary>
    /// The filter of the monster Summoned "treated as": it asks IsCanBeSpecialSummoned with that Summon type, a Fusion
    /// Monster CheckFusionMaterial (both written by the block), and from the Extra Deck its zone with or without the
    /// card that leaves the field ("ec", a parameter). The filter function's text, and as the block reads it, or null.
    /// </summary>
    private static (string Old, string New)? FilterForTreated(string lua, string prefix, string name, string kind, string nocheck, string nolimit)
    {
        var fn = Regex.Match(lua, $@"(?ms)^function {Regex.Escape(prefix)}\.{name}\(([^)]*)\)\n(.*?)^end");
        if (!fn.Success) return null;
        var pars = fn.Groups[1].Value.Split(',').Select(p => p.Trim()).ToArray();
        var body = fn.Groups[2].Value;
        if (!body.Contains($"c:IsCanBeSpecialSummoned(e,SUMMON_TYPE_{kind},tp,{nocheck},{nolimit})", StringComparison.Ordinal)) return null;
        if (kind == "FUSION")
        {
            var fusion = Regex.Match(body, @"\s+and c:CheckFusionMaterial\(\)(?=\W)|c:CheckFusionMaterial\(\)\s+and\s+");
            if (!fusion.Success) return null;
            body = body.Remove(fusion.Index, fusion.Length);
        }
        // "Duel.GetLocationCountFromEx(tp,tp,ec,c)>0" with ec passed in: read as the block's own check.
        var ex = Regex.Match(body, @"Duel\.GetLocationCountFromEx\(tp,tp,(\w+),c\)>0");
        if (ex.Success && ex.Groups[1].Value != "nil")
        {
            if (pars.Length != 4 || pars[3] != ex.Groups[1].Value) return null;
            body = body.Replace(ex.Value, "Duel.GetLocationCountFromEx(tp,tp,nil,c)>0", StringComparison.Ordinal);
            pars = pars[..3];
        }
        if (pars.Length != 3 || pars[0] != "c" || pars[1] != "e" || pars[2] != "tp") return null;
        return (fn.Value, $"function {prefix}.{name}({string.Join(",", pars)})\n{body}end");
    }

    /// <summary>The marked Summon of <see cref="TreatedAsRewrite"/> as the Special Summon block, or null for any other line.</summary>
    private static BlockInstance? TreatedAsSummonAction(string line, Dictionary<string, (CardFilter Filter, int Count)> groups, TargetInfo target, bool targetCard)
    {
        var m = Regex.Match(line, @"^Duel\.SpecialSummon\((\w+),CCTREATED(I?)(L?)_(\w+),tp,tp,false,false,(POS_FACEUP(?:_ATTACK|_DEFENSE)?)\)$");
        if (!m.Success) return null;
        var kind = m.Groups[4].Value.ToLowerInvariant();
        var ignore = m.Groups[2].Success && m.Groups[2].Value.Length > 0;
        _treatedLeaving = m.Groups[3].Value.Length > 0;
        var position = m.Groups[5].Value switch { "POS_FACEUP_ATTACK" => "attack", "POS_FACEUP_DEFENSE" => "defense", _ => "faceup" };
        var what = m.Groups[1].Value;
        if (what == "c")
        {
            _treatedLeaving = null;
            return Block("do.special_summon_self").With("position", position == "faceup" ? "faceup" : position).With("ignore", ignore).With("as", kind);
        }
        CardFilter filter;
        int count;
        bool targeted;
        if (groups.TryGetValue(what, out var g)) (filter, count, targeted) = (g.Filter, g.Count, false);
        else if (what == "tc" && targetCard && target.Filter is not null) (filter, count, targeted) = (target.Filter, target.Count, true);
        else throw No($"it acts on {what}, which the blocks can't follow");
        if (count != 1) throw No("it Summons a number of monsters treated as another Summon, which the block doesn't");
        filter = filter.Clone();
        filter.CardType = FilterCardType.Monster;
        return Block("do.special_summon").With("count", 1).With("cards", filter).With("target", targeted).With("position", position).With("ignore", ignore).With("as", kind);
    }

    /// <summary>
    /// After the operation: the block counts the zone this card leaves exactly when a cost moves this card, so the
    /// script must count it then (or this card is never on the field when it is used).
    /// </summary>
    private static void TreatedAsCostCheck(EffectEvent effect, string? range)
    {
        var leaving = _treatedLeaving;
        _treatedLeaving = null;
        if (leaving is null) return;
        var selfCost = effect.Cost.Any(b => b.BlockId == "cost.this_card" || b.BlockId == "cost.tribute" && b.Filters.TryGetValue("cards", out var f) && f.ThisCard);
        if (leaving == true && !selfCost) throw No("its Summon counts the zone of a card no cost moves");
        if (leaving == false && selfCost && (range ?? "").Contains("LOCATION_MZONE", StringComparison.Ordinal)) throw No("its Summon doesn't count the zone this card leaves for its cost");
    }

    // ---------------------------------------------------------------- "This card's name becomes ..."

    /// <summary>
    /// aux.EnableChangeCode(c,code[,locations[,condition]]): "This card's name becomes "X" while ..." in the places it
    /// lists, the condition as IF blocks.
    /// </summary>
    private static TranslatedPiece? ChangeCodeStatement(string line, CardData card, Functions fns)
    {
        var m = Regex.Match(line, @"^aux\.EnableChangeCode\(c,(\d+)(?:,(LOCATION_[\w+]+)(?:,([\w.]+))?)?\)$");
        if (!m.Success) return null;
        var field = card.IsMonster ? "LOCATION_MZONE" : "LOCATION_SZONE";
        var range = m.Groups[2].Success ? m.Groups[2].Value : field;
        var block = Block(NameBecomesPassive.BlockId).With("card", new CardFilter { CardCode = Int64(m.Groups[1].Value), CardType = FilterCardType.Any });
        var places = Places(range, card);
        var named = places.Block is null ? (range == "LOCATION_FZONE" ? "field_zone" : null)
            : places.Set.SetEquals(["field"]) ? "field" : places.Set.SetEquals(["field", "gy"]) ? "field_gy" : places.Set.SetEquals(["gy"]) ? "gy"
            : places.Set.SetEquals(["hand", "field"]) ? "hand_field" : null;
        if (named is not null) block = block.With("where", named);
        else if (places.Block is { } ticks) block = ticks(block.With("where", TreatedAsPlaces.Choice));
        else return null;
        var effect = new EffectEvent { Label = "Name", When = Block("when.while_face_up"), Do = [block] };
        if (m.Groups[3].Success) effect.If.AddRange(Conditions(m.Groups[3].Value, fns));
        return new TranslatedPiece { Piece = null!, Effect = effect };
    }

    /// <summary>
    /// A SetRange of places as the places ticks: the set of names it uses ("hand", "field", "gy"...) and how to tick
    /// them on a block, or no block when a place isn't one the ticks have (the Pendulum Zone).
    /// </summary>
    private static (HashSet<string> Set, Func<BlockInstance, BlockInstance>? Block) Places(string range, CardData card)
    {
        var set = new HashSet<string>();
        string? onField = null;
        foreach (var raw in range.Split('+'))
        {
            var loc = raw.Trim();
            switch (loc)
            {
                case "LOCATION_HAND": set.Add("hand"); break;
                case "LOCATION_DECK": set.Add("deck"); break;
                case "LOCATION_EXTRA": set.Add("extra"); break;
                case "LOCATION_GRAVE": set.Add("gy"); break;
                case "LOCATION_REMOVED": set.Add("banished"); break;
                case "LOCATION_MZONE": onField = onField is null ? (card.IsMonster ? "field" : "mzone") : "onfield"; set.Add(onField == "field" ? "field" : "other_field"); break;
                case "LOCATION_SZONE": onField = onField is null ? (card.IsMonster ? "szone" : "field") : "onfield"; set.Add(onField == "field" ? "field" : "other_field"); break;
                case "LOCATION_ONFIELD": onField = "onfield"; set.Add("other_field"); break;
                default: return (set, null);
            }
        }
        if (onField == "onfield") { set.Remove("field"); set.Add("other_field"); }
        return (set, b =>
        {
            foreach (var (place, key) in new[] { ("hand", "in_hand"), ("deck", "in_deck"), ("extra", "in_extra"), ("gy", "in_gy"), ("banished", "banished") })
                b = b.With(key, set.Contains(place));
            return b.With("on_field", onField ?? "none");
        });
    }

    // ---------------------------------------------------------------- "This card is (also) treated as ..."

    private static readonly HashSet<string> TreatedAsCodes =
    [
        "EFFECT_ADD_SETCODE", "EFFECT_ADD_CODE", "EFFECT_ADD_ATTRIBUTE", "EFFECT_CHANGE_ATTRIBUTE", "EFFECT_ADD_RACE", "EFFECT_CHANGE_RACE", "EFFECT_ADD_TYPE", "EFFECT_REMOVE_TYPE",
    ];

    private static readonly HashSet<string> MaterialCodes =
        ["EFFECT_XYZ_LEVEL", "EFFECT_SYNCHRO_LEVEL", "EFFECT_NONTUNER", "EFFECT_RITUAL_LEVEL", "EFFECT_FUSION_SUBSTITUTE", "EFFECT_DOUBLE_XMATERIAL"];

    /// <summary>
    /// A piece whose effects all make this card treated as something (or count as material some way): one block, or
    /// null when the piece is something else. Throws when it is one of these written in a way the blocks don't.
    /// </summary>
    private static TranslatedPiece? TreatedAsPieceOf(ScriptPiece piece, List<Dictionary<string, string>> sets, Functions fns, CardData card)
    {
        // A piece read before may have stopped between its Summon and its cost check.
        _treatedLeaving = null;
        if (sets.All(s => s.GetValueOrDefault("Type") == "EFFECT_TYPE_FIELD") && sets.All(s => s.GetValueOrDefault("Code") is "EFFECT_ADD_SETCODE" or "EFFECT_ADD_CODE" or "EFFECT_ADD_ATTRIBUTE" or "EFFECT_ADD_RACE" or "EFFECT_ADD_TYPE"))
            return CardsTreatedAsSets(piece, sets, fns);
        if (sets.Any(s => s.GetValueOrDefault("Type") != "EFFECT_TYPE_SINGLE")) return null;
        var codes = sets.Select(s => s.GetValueOrDefault("Code") ?? "").ToList();
        if (codes.All(TreatedAsCodes.Contains)) return new TranslatedPiece { Piece = piece, Effect = TreatedAsSets(piece.Title, sets, fns, card) };
        if (codes.Distinct().Count() == 1 && MaterialCodes.Contains(codes[0])) return MaterialSets(piece, sets, codes[0], fns, card);
        return null;
    }

    /// <summary>The effect of the treated-as block for these parts, one place, flags and condition for all of them.</summary>
    private static EffectEvent TreatedAsSets(string label, List<Dictionary<string, string>> sets, Functions fns, CardData card)
    {
        // One block: the same places, flags and condition for every part.
        static string Shape(Dictionary<string, string> s) => string.Join("|", s.Where(kv => kv.Key is not ("Code" or "Value" or "Description")).OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
        if (sets.Any(s => Shape(s) != Shape(sets[0]))) throw No("its parts apply in different places");
        var set = sets[0];
        if (!Only(set, "Code", "Value", "Range", "Property", "Condition")) throw No("it keeps extra state the blocks don't model");
        if (sets.Any(s => !s.ContainsKey("Value"))) throw No("it changes this card without saying to what");
        var range = set.GetValueOrDefault("Range");
        var field = card.IsMonster ? "LOCATION_MZONE" : "LOCATION_SZONE";
        string where;
        Func<BlockInstance, BlockInstance>? ticks = null;
        if (range is null && HasFlags(set, "EFFECT_FLAG_CANNOT_DISABLE", "EFFECT_FLAG_UNCOPYABLE")) where = "always";
        else if (range is not null && HasFlags(set, "EFFECT_FLAG_SINGLE_RANGE", "EFFECT_FLAG_CANNOT_DISABLE", "EFFECT_FLAG_UNCOPYABLE") && Places(range, card) is var kept
                 && (kept.Set.SetEquals(["field"]) || kept.Set.SetEquals(["field", "gy"])))
            where = kept.Set.Count == 1 ? "field" : "field_gy";
        else if (range is not null && HasFlags(set, "EFFECT_FLAG_SINGLE_RANGE"))
        {
            if (range == field) where = "face_up";
            else if (Places(range, card).Block is { } t) { where = TreatedAsPlaces.Choice; ticks = t; }
            else throw No($"it is treated as something in {range}, which the blocks can't tick");
        }
        else throw No("its \"treated as\" applies in a way the blocks don't");

        var f = new CardFilter { CardType = FilterCardType.Monster };
        bool? instead = null;
        var removesEffect = false;
        void How(bool change)
        {
            if (instead is { } i && i != change) throw No("it both adds to and replaces what this card is");
            instead = change;
        }
        foreach (var s in sets)
        {
            var value = s["Value"];
            switch (s["Code"])
            {
                case "EFFECT_ADD_SETCODE" when Regex.Match(value, @"^0x([0-9a-fA-F]+)$") is { Success: true } hex:
                    var arch = int.Parse(hex.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (f.Archetype == 0) f.Archetype = arch;
                    else if (f.Archetype2 == 0) f.Archetype2 = arch;
                    else throw No("it is treated as more archetypes than the block holds");
                    f.CardType = FilterCardType.Any;
                    break;
                case "EFFECT_ADD_CODE" when Regex.IsMatch(value, @"^\d+$") && f.CardCode == 0:
                    f.CardCode = Int64(value);
                    break;
                case "EFFECT_ADD_ATTRIBUTE" or "EFFECT_CHANGE_ATTRIBUTE" when f.Attribute == CardAttribute.None && value.Split('+').All(a => Attributes.ContainsKey(a.Trim())):
                    f.Attribute = value.Split('+').Aggregate(CardAttribute.None, (a, x) => a | Attributes[x.Trim()]);
                    How(s["Code"] == "EFFECT_CHANGE_ATTRIBUTE");
                    break;
                // "0xf": EARTH, WATER, FIRE and WIND written as one number.
                case "EFFECT_ADD_ATTRIBUTE" or "EFFECT_CHANGE_ATTRIBUTE" when f.Attribute == CardAttribute.None && Regex.Match(value, @"^0x([0-9a-fA-F]+)$") is { Success: true } bits
                    && int.Parse(bits.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) is var mask and > 0 and <= 0x7f:
                    f.Attribute = (CardAttribute)mask;
                    How(s["Code"] == "EFFECT_CHANGE_ATTRIBUTE");
                    break;
                case "EFFECT_ADD_RACE" or "EFFECT_CHANGE_RACE" when f.Race == CardRace.None && value.Split('+').All(r => Races.ContainsKey(r.Trim())):
                    f.Race = value.Split('+').Aggregate(CardRace.None, (r, x) => r | Races[x.Trim()]);
                    How(s["Code"] == "EFFECT_CHANGE_RACE");
                    break;
                case "EFFECT_ADD_TYPE" when f.MonsterKind == MonsterKind.Any && value is "TYPE_TUNER" or "TYPE_NORMAL" or "TYPE_EFFECT" or "TYPE_SPIRIT" or "TYPE_UNION" or "TYPE_DUAL" or "TYPE_FLIP" or "TYPE_TOON":
                    f.MonsterKind = MonsterKinds[value];
                    break;
                case "EFFECT_REMOVE_TYPE" when value == "TYPE_EFFECT" && !removesEffect:
                    removesEffect = true;
                    break;
                default:
                    throw No($"it is treated as something the blocks can't pick ({s["Code"]} {Short(value)})");
            }
        }
        // "This card is treated as a Normal Monster": it stops being an Effect Monster too, which "instead" says.
        if (removesEffect)
        {
            if (f.MonsterKind != MonsterKind.Normal) throw No("it stops being an Effect Monster without becoming a Normal Monster");
            How(true);
        }
        else if (f.MonsterKind == MonsterKind.Normal) How(false);
        // The block keeps "always" archetypes in the card's database entry, not as an effect.
        if (where == "always" && f.Archetype != 0 && set.GetValueOrDefault("Condition") is null)
            throw No("it treats this card as an archetype by an effect, where the block uses the card's database entry");
        if (f.Attribute == CardAttribute.None && f.Race == CardRace.None && f.MonsterKind == MonsterKind.Any && f.CardCode == 0 && f.Archetype == 0) f.CardType = FilterCardType.Any;
        else if (f.Attribute != CardAttribute.None || f.Race != CardRace.None || f.MonsterKind != MonsterKind.Any) f.CardType = FilterCardType.Monster;

        var block = Block(TreatedAsPassive.BlockId).With("how", instead == true ? "instead" : "also").With("where", where).With("as", f);
        if (ticks is not null) block = ticks(block);
        var effect = new EffectEvent { Label = label, When = Block("when.while_face_up").With("zone", where == "always" ? "auto" : card.IsMonster ? "monster_zone" : "auto"), Do = [block] };
        if (set.GetValueOrDefault("Condition") is { } condition) effect.If.AddRange(Conditions(condition, fns));
        return effect;
    }

    /// <summary>A treated-as part of a piece whose other parts are something else ("also DARK, and gains 500 ATK"): written on <paramref name="effect"/>, or false.</summary>
    private static bool TreatedAsLasting(EffectEvent effect, HashSet<string> type, string code, Dictionary<string, string> set, Functions fns, CardData card)
    {
        if (!type.SetEquals(["EFFECT_TYPE_SINGLE"]) || !TreatedAsCodes.Contains(code)) return false;
        var treated = TreatedAsSets(effect.Label, [set], fns, card);
        effect.When = treated.When;
        effect.Do.AddRange(treated.Do);
        effect.If.AddRange(treated.If);
        return true;
    }

    /// <summary>"All Fusion Monsters you control are also treated as "Frightfur" monsters": a field effect that adds to other monsters.</summary>
    private static TranslatedPiece? CardsTreatedAsSets(ScriptPiece piece, List<Dictionary<string, string>> sets, Functions fns)
    {
        static string Shape(Dictionary<string, string> s) => string.Join("|", s.Where(kv => kv.Key is not ("Code" or "Value" or "Description")).OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
        var set = sets[0];
        if (sets.Any(s => Shape(s) != Shape(set)) || !Only(set, "Code", "Value", "Range", "TargetRange", "Target", "Condition") || set.GetValueOrDefault("Range") is not { } range
            || set.GetValueOrDefault("TargetRange") is not { } targets) return null;
        var f = new CardFilter { CardType = FilterCardType.Monster };
        foreach (var s in sets)
        {
            var value = s.GetValueOrDefault("Value") ?? throw No("it changes monsters without saying to what");
            switch (s["Code"])
            {
                case "EFFECT_ADD_SETCODE" when f.Archetype == 0 && Regex.Match(value, @"^0x([0-9a-fA-F]+)$") is { Success: true } hex:
                    f.Archetype = int.Parse(hex.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture); break;
                case "EFFECT_ADD_CODE" when f.CardCode == 0 && Regex.IsMatch(value, @"^\d+$"): f.CardCode = Int64(value); break;
                case "EFFECT_ADD_ATTRIBUTE" when f.Attribute == CardAttribute.None && value.Split('+').All(a => Attributes.ContainsKey(a.Trim())):
                    f.Attribute = value.Split('+').Aggregate(CardAttribute.None, (a, x) => a | Attributes[x.Trim()]); break;
                case "EFFECT_ADD_RACE" when f.Race == CardRace.None && value.Split('+').All(r => Races.ContainsKey(r.Trim())):
                    f.Race = value.Split('+').Aggregate(CardRace.None, (r, x) => r | Races[x.Trim()]); break;
                case "EFFECT_ADD_TYPE" when f.MonsterKind == MonsterKind.Any && value is "TYPE_TUNER" or "TYPE_NORMAL" or "TYPE_EFFECT" or "TYPE_SPIRIT" or "TYPE_UNION" or "TYPE_DUAL" or "TYPE_FLIP" or "TYPE_TOON":
                    f.MonsterKind = MonsterKinds[value]; break;
                default: throw No($"it treats monsters as something the blocks can't pick ({s["Code"]} {Short(value)})");
            }
        }
        var cards = FieldTargets(targets, set.GetValueOrDefault("Target"), fns);
        if (cards.CardType != FilterCardType.Monster) throw No("it treats cards that aren't monsters as something");
        var effect = new EffectEvent
        {
            Label = piece.Title, When = Block("when.while_face_up").With("zone", Zone(range)),
            Do = [Block(CardsTreatedAsPassive.BlockId).With("cards", cards).With("as", f)],
        };
        if (set.GetValueOrDefault("Condition") is { } condition) effect.If.AddRange(Conditions(condition, fns));
        return new TranslatedPiece { Piece = piece, Effect = effect };
    }

    // ---------------------------------------------------------------- how this card counts as material

    /// <summary>The "and" terms of a value function's checks on <paramref name="card"/>, read as a filter of the monster being Summoned.</summary>
    private static CardFilter SummonedBy(string? checks, string card)
    {
        var f = new CardFilter { CardType = FilterCardType.Monster };
        if (checks is null) return f;
        if (checks.Contains(" or ", StringComparison.Ordinal) && !Regex.IsMatch(checks, @"^\(c:\w+\(\w+\) or c:\w+\(\w+\)\)$")) throw No($"it counts as material for monsters the blocks can't pick ({Short(checks)})");
        foreach (var t in SplitTopLevelAnd(checks)) Term(Regex.Replace(t.Trim(), $@"\b{card}:", "c:"), f);
        f.CardType = FilterCardType.Monster;
        f.Zones = Model.Zone.None;
        return f;
    }

    private static string Squashed(string reference, Functions fns) => Regex.Replace(Body(reference, fns), @"\s+", " ").Trim();

    private static TranslatedPiece MaterialSets(ScriptPiece piece, List<Dictionary<string, string>> sets, string code, Functions fns, CardData card)
    {
        var set = sets[0];
        var value = set.GetValueOrDefault("Value");
        BlockInstance block;
        var limit = OncePerTurn.None;
        var group = 0;
        bool RangedOnField(Dictionary<string, string> s) => s.GetValueOrDefault("Range") == "LOCATION_MZONE" && HasFlags(s, "EFFECT_FLAG_SINGLE_RANGE");
        Match m;
        switch (code)
        {
            // Lantern Shark: one effect per Level, the Level in its label.
            case "EFFECT_XYZ_LEVEL":
            {
                if (sets.Count > 2 || sets.Any(s => !RangedOnField(s) || !Only(s, "Code", "Value", "Range", "Property", "Label") || s.GetValueOrDefault("Value") != value))
                    throw No("its Xyz Level applies in a way the block doesn't");
                if (value is null || !Regex.IsMatch(value, @"^[\w]+\.\w+$")) throw No("its Xyz Level isn't a function the block writes");
                var body = Squashed(value, fns);
                var levels = new List<int>();
                string? checks = null;
                if ((m = Regex.Match(body, @"^return 0x([1-9a-c])0000\+e:GetHandler\(\):GetLevel\(\)$")).Success && sets.Count == 1 && !set.ContainsKey("Label"))
                    levels.Add(int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                else
                {
                    if ((m = Regex.Match(body, @"^if (.+) then return c:GetLevel\(\)\+0x10000\*e:GetLabel\(\) else return c:GetLevel\(\) end$")).Success) checks = m.Groups[1].Value;
                    else if (body != "return c:GetLevel()+0x10000*e:GetLabel()") throw No($"its Xyz Level is worked out in a way the block doesn't ({Short(body)})");
                    foreach (var s in sets)
                    {
                        if (!int.TryParse(s.GetValueOrDefault("Label"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv) || lv is < 1 or > 12) throw No("its Xyz Level is unusual");
                        levels.Add(lv);
                    }
                }
                block = Block(MaterialTreatedAsPassive.BlockId).With("kind", "xyz_level").With("level", levels[0]).With("level2", levels.Count > 1 ? levels[1] : 0).With("for", SummonedBy(checks, "rc"));
                break;
            }
            // "This card on the field can be treated as a Level 8 monster when used for a Synchro Summon."
            case "EFFECT_SYNCHRO_LEVEL":
            {
                if (sets.Count != 1 || value is null || !Regex.IsMatch(value, @"^[\w]+\.\w+$") || !Only(set, "Code", "Value", "Range", "Property")) throw No("its Synchro Level applies in a way the block doesn't");
                var field = RangedOnField(set);
                if (!field && set.ContainsKey("Property")) throw No("its Synchro Level applies in a way the block doesn't");
                var body = Squashed(value, fns);
                string? checks = null;
                if ((m = Regex.Match(body, @"^local lv=aux\.GetCappedLevel\(e:GetHandler\(\)\) return \((\d+)<<16\)\+lv$")).Success) { }
                else if ((m = Regex.Match(body, @"^local lv=aux\.GetCappedLevel\(e:GetHandler\(\)\) if (.+) then return \((\d+)<<16\)\+lv else return lv end$")).Success) checks = m.Groups[1].Value;
                else throw No($"its Synchro Level is worked out in a way the block doesn't ({Short(body)})");
                var lv = Int(m.Groups[checks is null ? 1 : 2].Value);
                if (lv is < 1 or > 12) throw No("its Synchro Level is unusual");
                block = Block(MaterialTreatedAsPassive.BlockId).With("kind", "synchro_level").With("level", lv).With("where", field ? "field" : "any").With("for", SummonedBy(checks, "c"));
                break;
            }
            // "If this card you control is used as Synchro Material for a Psychic monster, you can treat it as a non-Tuner."
            case "EFFECT_NONTUNER":
            {
                if (sets.Count != 1 || !RangedOnField(set) || !Only(set, "Code", "Value", "Range", "Property") || value is null || !Regex.IsMatch(value, @"^[\w]+\.\w+$"))
                    throw No("its non-Tuner treatment applies in a way the block doesn't");
                var body = Squashed(value, fns);
                if (!(m = Regex.Match(body, @"^return e:GetHandler\(\):IsControler\(c:GetControler\(\)\)(?: and (.+))?$")).Success) throw No($"it is treated as a non-Tuner in a way the block doesn't ({Short(body)})");
                block = Block(MaterialTreatedAsPassive.BlockId).With("kind", "nontuner").With("for", SummonedBy(m.Groups[1].Success ? m.Groups[1].Value : null, "c"));
                break;
            }
            // Gishki and Nekroz: "this card can be used as the entire Tribute".
            case "EFFECT_RITUAL_LEVEL":
            {
                if (sets.Count != 1 || !Only(set, "Code", "Value") || value is null || !Regex.IsMatch(value, @"^[\w]+\.\w+$")) throw No("its Ritual Level applies in a way the block doesn't");
                var body = Squashed(value, fns);
                var field = false;
                var guard = Regex.Match(body, @"^local ec=e:GetHandler\(\) local lv=aux\.GetCappedLevel\(ec\) if not ec:IsLocation\(LOCATION_MZONE\) then return lv end (.*)$");
                if (guard.Success) { field = true; body = guard.Groups[1].Value; }
                else if ((m = Regex.Match(body, @"^local lv=aux\.GetCappedLevel\(e:GetHandler\(\)\) (.*)$")).Success) body = m.Groups[1].Value;
                else throw No($"its Ritual Level is worked out in a way the block doesn't ({Short(body)})");
                string? checks = null;
                if (body == "local clv=c:GetLevel() return (lv<<16)+clv") { }
                else if ((m = Regex.Match(body, @"^if (.+) then local clv=c:GetLevel\(\) return \(lv<<16\)\+clv else return lv end$")).Success) checks = m.Groups[1].Value;
                else throw No($"its Ritual Level is worked out in a way the block doesn't ({Short(body)})");
                block = Block(MaterialTreatedAsPassive.BlockId).With("kind", "ritual_entire").With("where", field ? "field" : "any").With("for", SummonedBy(checks, "c"));
                break;
            }
            // "This card can be used as a substitute for any 1 Fusion Material whose name is specifically listed ..."
            case "EFFECT_FUSION_SUBSTITUTE":
            {
                if (sets.Count != 1) throw No("its Fusion substitute applies in a way the block doesn't");
                string? checks = null;
                if (value is not null)
                {
                    if (!Regex.IsMatch(value, @"^[\w]+\.\w+$") || !(m = Regex.Match(Squashed(value, fns), @"^return (.+)$")).Success) throw No("its Fusion substitute is for Fusion Monsters the block can't pick");
                    checks = m.Groups[1].Value;
                }
                bool field;
                if (RangedOnField(set) && Only(set, "Code", "Value", "Range", "Property")) field = true;
                else if (Only(set, "Code", "Value", "Condition") && set.GetValueOrDefault("Condition") is { } con && Regex.IsMatch(con, @"^[\w]+\.\w+$")
                         && Squashed(con, fns) == "return e:GetHandler():IsLocation(LOCATION_HAND+LOCATION_MZONE+LOCATION_GRAVE)") field = false;
                else throw No("its Fusion substitute applies in places the block doesn't");
                block = Block(MaterialTreatedAsPassive.BlockId).With("kind", "fusion_substitute").With("where", field ? "field" : "any").With("for", SummonedBy(checks, "c"));
                break;
            }
            // Drake Shark: "This card can be treated as 2 materials for the Xyz Summon of a WATER Xyz Monster that requires 3 or more materials."
            case "EFFECT_DOUBLE_XMATERIAL":
            {
                if (sets.Count != 1 || !RangedOnField(set) || !Only(set, "Code", "Value", "Range", "Property", "Target", "CountLimit") || value is null || !IsOwnId(value))
                    throw No("its double Xyz material applies in a way the block doesn't");
                string? checks = null;
                if (set.GetValueOrDefault("Target") is { } tg)
                {
                    if (!Regex.IsMatch(tg, @"^[\w]+\.\w+$") || !(m = Regex.Match(Squashed(tg, fns), @"^return (.+)$")).Success) throw No("its double Xyz material is for Xyz Monsters the block can't pick");
                    checks = m.Groups[1].Value;
                }
                limit = CountLimit(set.GetValueOrDefault("CountLimit"), piece.SourceId, out group);
                block = Block(MaterialTreatedAsPassive.BlockId).With("kind", "xyz_double").With("for", SummonedBy(checks, "c"));
                break;
            }
            default: throw No("it counts as material in a way the blocks don't");
        }
        if (!card.IsMonster) throw No("it is a material treatment of a card that isn't a monster");
        var effect = new EffectEvent { Label = piece.Title, When = Block("when.while_face_up").With("zone", "monster_zone"), Do = [block], Limit = limit, SharedLimitGroup = group };
        return new TranslatedPiece { Piece = piece, Effect = effect };
    }

    private static bool IsOwnId(string value) => value == "id" || long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n == _sourceId;

    // ---------------------------------------------------------------- "(This card is always treated as a "HERO" card.)"

    /// <summary>
    /// An official card's "(This card is always treated as a "X" card.)": its archetype is already in the card's
    /// database entry; the block says it in the card text. Null when the text has none, or names an archetype the card
    /// doesn't have.
    /// </summary>
    private static EffectEvent? AlwaysTreatedAsReminder(CardData card, CardPack? pack)
    {
        var m = Regex.Match(card.Text, @"\(This card is always treated as an? ((?:""[^""]+""(?:, and |, | and ))*""[^""]+"") card\.\)");
        if (!m.Success) return null;
        var names = Regex.Matches(m.Groups[1].Value, @"""([^""]+)""").Select(x => x.Groups[1].Value).ToList();
        if (names.Count > 2) return null;
        var codes = new List<int>();
        foreach (var name in names)
        {
            var found = card.Archetypes.Where(a => a != 0 && string.Equals(pack?.ArchetypeName(a) ?? Text.ArchetypeNames.Find(a), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count != 1 || codes.Contains(found[0])) return null;
            codes.Add(found[0]);
        }
        var f = new CardFilter { CardType = FilterCardType.Any, Archetype = codes[0], Archetype2 = codes.Count > 1 ? codes[1] : 0 };
        return new EffectEvent { Label = "Treated as", When = Block("when.while_face_up").With("zone", "auto"), Do = [Block(TreatedAsPassive.BlockId).With("how", "also").With("where", "always").With("as", f)] };
    }
}
