using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>Gemini and Union monsters, Trap Monsters, cards placed on the Deck, battle positions and face-down banishing.</summary>
public static partial class ScriptTranslator
{
    // ---------------------------------------------------------------- whole-script rewrites

    /// <summary>
    /// Steps the blocks say in one, read as one line each: "take 1 monster from your Deck and place it on top of your Deck"
    /// (shuffle, move it to the top, look at it), and "equip it to this card" (equip, then the limit that only this card
    /// can hold it).
    /// </summary>
    private static string MechanicsRewrite(string lua)
    {
        _activationCannotBeNegated = Regex.IsMatch(lua, @"SetType\(EFFECT_TYPE_ACTIVATE\)") && lua.Contains("EFFECT_FLAG_CANNOT_INACTIVATE", StringComparison.Ordinal);
        lua = Regex.Replace(lua, @"(?m)^([ \t]*)Duel\.ShuffleDeck\(tp\)[ \t]*\n[ \t]*Duel\.MoveSequence\((\w+),SEQ_DECKTOP\)[ \t]*\n[ \t]*Duel\.ConfirmDecktop\(tp,1\)[ \t]*$",
            "$1Duel.CardCrafterDeckTop($2)");
        lua = EndPhaseRewrite(lua);
        // "Inflict 500 damage to both players": both at once.
        lua = Regex.Replace(lua, @"(?m)^([ \t]*)Duel\.Damage\((1-tp|tp),(\d+),REASON_EFFECT,true\)[ \t]*\n[ \t]*Duel\.Damage\((1-tp|tp),\3,REASON_EFFECT,true\)[ \t]*\n[ \t]*Duel\.RDComplete\(\)[ \t]*$",
            m => m.Groups[2].Value != m.Groups[4].Value ? $"{m.Groups[1].Value}Duel.CardCrafterDamageBoth({m.Groups[3].Value})" : m.Value);
        // "if not Duel.Equip(tp,tc,c) then return end" or "if Duel.Equip(tp,tc,c) then ... end", with the equip limit between.
        return Regex.Replace(lua,
            @"(?m)^([ \t]*)(if not Duel\.Equip\(tp,(\w+),c(?:,true)?\) then return end|if Duel\.Equip\(tp,(\w+),c(?:,true)?\) then)[ \t]*\n" +
            @"[ \t]*local (\w+)=Effect\.CreateEffect\((?:c|e:GetHandler\(\))\)[ \t]*\n((?:[ \t]*\5:Set\w+\([^\n]*\)[ \t]*\n){5,6})[ \t]*(\w+):RegisterEffect\(\5\)[ \t]*(\n[ \t]*end[ \t]*(?=\n))?",
            m =>
            {
                var tc = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
                var gated = m.Groups[2].Value.StartsWith("if not ", StringComparison.Ordinal);
                // "if Duel.Equip(...) then" closes right after the limit; after "if not ... then return end" an "end" closes something else.
                if (m.Groups[7].Value != tc || !gated && !m.Groups[8].Success) return m.Value;
                var sets = m.Groups[6].Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => l[(m.Groups[5].Value.Length + 1)..]).ToHashSet();
                var value = sets.Select(s => Regex.Match(s, @"^SetValue\(((?:s|c\d+)\.\w+)\)$")).FirstOrDefault(v => v.Success);
                if (value is null) return m.Value;
                var limit = Regex.Match(lua, $@"function {Regex.Escape(value.Groups[1].Value)}\(e,c\)\s*\n\s*(return [^\n]*?)\s*\n\s*end");
                string[] common = ["SetType(EFFECT_TYPE_SINGLE)", "SetProperty(EFFECT_FLAG_OWNER_RELATE)", "SetCode(EFFECT_EQUIP_LIMIT)", "SetReset(RESET_EVENT+RESETS_STANDARD)", value.Value];
                // Only this card can hold it: its owner, or the card kept as the effect's label.
                var owner = limit.Success && limit.Groups[1].Value == "return e:GetOwner()==c" && sets.SetEquals(common);
                var label = limit.Success && limit.Groups[1].Value is "return c==e:GetLabelObject()" or "return e:GetLabelObject()==c" && sets.SetEquals([.. common, "SetLabelObject(c)"]);
                if (!owner && !label) return m.Value;
                // "if not ... then return end": what follows happens only if it was equipped ("if ... then" read as
                // "and if you do"; its own "end" keeps the script's blocks closed where they were).
                var indent = m.Groups[1].Value;
                return gated ? $"{indent}if Duel.CardCrafterEquipToThis({tc})~=0 then\n{indent}end{m.Groups[8].Value}" : $"{indent}Duel.CardCrafterEquipToThis({tc})";
            });
    }

    /// <summary>
    /// "..., but destroy it during the End Phase": the card just acted on gets a flag with this card's field id, and an
    /// effect at the next End Phase destroys (returns, banishes...) it if it still has that flag. Read as one line, when the
    /// helper functions are the usual ones and the step right before acted on that same card.
    /// </summary>
    private static string EndPhaseRewrite(string lua)
    {
        return Regex.Replace(lua,
            @"(?m)^([^\n]*\n)([ \t]*)local fid=(?:e:GetHandler\(\)|c):GetFieldID\(\)[ \t]*\n[ \t]*(\w+):RegisterFlagEffect\((id|\d+),RESET_EVENT\+RESETS_STANDARD,0,1,fid\)[ \t]*\n" +
            @"[ \t]*local (\w+)=Effect\.CreateEffect\((?:e:GetHandler\(\)|c)\)[ \t]*\n((?:[ \t]*\5:Set\w+\([^\n]*\)[ \t]*\n){8})[ \t]*Duel\.RegisterEffect\(\5,tp\)[ \t]*$",
            m =>
            {
                var tc = m.Groups[3].Value;
                // The step before acted on this very card: "if ... Duel.SpecialSummon(tc,0,tp,tp,...)~=0 then".
                if (!Regex.IsMatch(m.Groups[1].Value, $@"Duel\.\w+\({Regex.Escape(tc)},")) return m.Value;
                var sets = m.Groups[6].Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => l[(m.Groups[5].Value.Length + 1)..]).ToHashSet();
                var con = sets.Select(s => Regex.Match(s, @"^SetCondition\(((?:s|c\d+)\.\w+)\)$")).FirstOrDefault(x => x.Success);
                var op = sets.Select(s => Regex.Match(s, @"^SetOperation\(((?:s|c\d+)\.\w+)\)$")).FirstOrDefault(x => x.Success);
                if (con is null || op is null) return m.Value;
                if (!sets.SetEquals(["SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS)", "SetCode(EVENT_PHASE+PHASE_END)", "SetCountLimit(1)", "SetProperty(EFFECT_FLAG_IGNORE_IMMUNE)",
                        "SetLabel(fid)", $"SetLabelObject({tc})", con.Value, op.Value]))
                    return m.Value;
                string? FunctionBody(string name) =>
                    Regex.Match(lua, $@"function {Regex.Escape(name)}\(e,tp,eg,ep,ev,re,r,rp\)\s*\n(.*?)\nend", RegexOptions.Singleline) is { Success: true } f
                        ? string.Join(" ", f.Groups[1].Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)) : null;
                // The flag must still be this card's: otherwise the effect ends itself.
                var flag = Regex.Escape(m.Groups[4].Value);
                var conBody = FunctionBody(con.Groups[1].Value) ?? "";
                if (!Regex.IsMatch(conBody, $@"^(?:local tc=e:GetLabelObject\(\) if tc:GetFlagEffectLabel\({flag}\)~=e:GetLabel\(\)|if e:GetLabelObject\(\):GetFlagEffectLabel\({flag}\)~=e:GetLabel\(\)) then e:Reset\(\) return false else return true end$"))
                    return m.Value;
                var opBody = (FunctionBody(op.Groups[1].Value) ?? "").Replace("local tc=e:GetLabelObject() ", "").Replace("e:GetLabelObject()", "tc");
                var how = opBody switch
                {
                    "Duel.Destroy(tc,REASON_EFFECT)" => "destroy",
                    "Duel.SendtoHand(tc,nil,REASON_EFFECT)" => "hand",
                    "Duel.Remove(tc,POS_FACEUP,REASON_EFFECT)" => "banish",
                    "Duel.SendtoDeck(tc,nil,SEQ_DECKSHUFFLE,REASON_EFFECT)" or "Duel.SendtoDeck(tc,nil,2,REASON_EFFECT)" => "deck",
                    "Duel.SendtoGrave(tc,REASON_EFFECT)" => "grave",
                    _ => null,
                };
                return how is null ? m.Value : $"{m.Groups[1].Value}{m.Groups[2].Value}Duel.CardCrafterEndPhase({how})";
            });
    }

    // ---------------------------------------------------------------- the Chain

    /// <summary>"Duel.SetChainLimit(...)" lines of the target step being read, as blocks for the effect.</summary>
    [ThreadStatic] private static List<BlockInstance>? _chainLimits;

    /// <summary>
    /// Blocks an activated effect's settings stand for: "This card's activation and effect cannot be negated" for its
    /// flags. Starts the target step's list of chain limits afresh.
    /// </summary>
    private static List<BlockInstance> MechanicsCosts(HashSet<string> type, HashSet<string> props)
    {
        _chainLimits = [];
        if (!props.Contains("EFFECT_FLAG_CANNOT_INACTIVATE")) return [];
        if (!CannotBeNegatedCost.Flags.All(props.Contains)) throw No("its activation cannot be negated in a way the blocks can't say");
        return [Block(CannotBeNegatedCost.BlockId)];
    }

    /// <summary>The chain limits the target step set, as "players can't respond to this activation" blocks.</summary>
    private static List<BlockInstance> TakeChainLimits()
    {
        var taken = _chainLimits ?? [];
        _chainLimits = [];
        return taken;
    }

    /// <summary>
    /// "Duel.SetChainLimit(aux.FALSE)" (neither player can respond) or a limit function of this card: who may still respond
    /// and with what. False for any other line.
    /// </summary>
    private static bool ChainLimitLine(string line, Functions fns)
    {
        var m = Regex.Match(line, @"^Duel\.SetChainLimit\(([\w.]+)\)$");
        if (!m.Success) return false;
        string who, what;
        if (m.Groups[1].Value == "aux.FALSE") (who, what) = ("both", "any");
        else
        {
            var (pars, body) = fns.Get(m.Groups[1].Value);
            if (pars.Length != 3) return false;
            var text = string.Join(" ", body.Select(l => l.Trim()).Where(l => l.Length > 0));
            var (e, p, t) = (Regex.Escape(pars[0]), Regex.Escape(pars[1]), Regex.Escape(pars[2]));
            var r = Regex.Match(text, $@"^return (?:({t}=={p}|{p}=={t})(?: or |$))?(not {e}:IsActiveType\(TYPE_MONSTER\)|not {e}:IsHasType\(EFFECT_TYPE_ACTIVATE\))?$");
            if (!r.Success || !r.Groups[1].Success && !r.Groups[2].Success) return false;
            who = r.Groups[1].Success ? "opponent" : "both";
            what = !r.Groups[2].Success ? "any" : r.Groups[2].Value.Contains("TYPE_MONSTER", StringComparison.Ordinal) ? "monster" : "spelltrap";
        }
        (_chainLimits ??= []).Add(Block("cost.chain_limit").With("who", who).With("what", what));
        return true;
    }

    /// <summary>Whether the script's Spell/Trap Card activation can't be negated, which writes the card's own "cannot be negated" too.</summary>
    [ThreadStatic] private static bool _activationCannotBeNegated;

    /// <summary>
    /// The SINGLE EFFECT_CANNOT_DISABLE a Spell/Trap Card gives itself next to an activation that can't be negated: the
    /// "cannot be negated" block writes it.
    /// </summary>
    private static bool IsCannotNegateCompanion(HashSet<string> type, string code, Dictionary<string, string> set, CardData card) =>
        _activationCannotBeNegated && !card.IsMonster && type.SetEquals(["EFFECT_TYPE_SINGLE"]) && code == "EFFECT_CANNOT_DISABLE"
        && set.Keys.All(k => k is "Type" or "Code" or "Property") && Flags(set.GetValueOrDefault("Property") ?? "").SetEquals(["EFFECT_FLAG_CANNOT_DISABLE", "EFFECT_FLAG_UNCOPYABLE"]);

    // ---------------------------------------------------------------- statements in initial_effect

    /// <summary>"aux.EnableUnionAttribute(c,s.filter)": the Union block, with the monsters it can be equipped to.</summary>
    private static TranslatedPiece? MechanicsStatement(string line, Functions fns)
    {
        var m = Regex.Match(line, @"^aux\.EnableUnionAttribute\(c,([\w.]+)\)$");
        if (!m.Success) return null;
        var f = Material(m.Groups[1].Value, fns);
        // The block writes its filter with what a monster is (Type, Attribute, name...); anything else stays script.
        var full = FilterCompiler.Conditions(f).Where(t => t != "c:IsType(TYPE_MONSTER)").ToHashSet();
        var builder = new ScriptBuilder(new CardProject(), null);
        var fn = builder.MaterialFilter(f);
        var kept = fn == "nil" ? [] : Regex.Match(builder.Functions[0], @"return (.*)\n").Groups[1].Value.Split(" and ").ToHashSet();
        if (!full.SetEquals(kept)) throw No("its Union monsters are picked in a way the blocks can't");
        return new TranslatedPiece
        {
            Piece = null!,
            Effect = new EffectEvent { Label = "Union", When = Block("when.while_face_up"), Do = [Block(UnionPassive.BlockId).With("cards", f)] },
        };
    }

    // ---------------------------------------------------------------- conditions

    /// <summary>"SetCondition(aux.IsDualState)": the Gemini block. Null for any other condition.</summary>
    private static List<BlockInstance>? MechanicsCondition(string condition) =>
        condition == "aux.IsDualState" ? [Block(GeminiCondition.BlockId)] : null;

    /// <summary>"return aux.IsDualState(e)", alone or as one part of "A and B".</summary>
    private static List<BlockInstance>? MechanicsConditionBody(string body) =>
        body == "return aux.IsDualState(e)" ? [Block(GeminiCondition.BlockId)] : null;

    // ---------------------------------------------------------------- lasting effects

    /// <summary>
    /// "All face-up monsters on the field become DARK" (FIELD EFFECT_CHANGE_ATTRIBUTE or EFFECT_CHANGE_RACE with one Type or
    /// Attribute): the "become" block. False for any other lasting effect.
    /// </summary>
    private static bool MechanicsLasting(EffectEvent effect, HashSet<string> type, string code, Dictionary<string, string> set, Functions fns)
    {
        if (!type.SetEquals(["EFFECT_TYPE_FIELD"]) || code is not ("EFFECT_CHANGE_RACE" or "EFFECT_CHANGE_ATTRIBUTE")) return false;
        if (!set.Keys.All(k => k is "Type" or "Code" or "Range" or "TargetRange" or "Target" or "Value" or "Condition")) return false;
        if (set.GetValueOrDefault("TargetRange") is not { } tr || set.GetValueOrDefault("Value") is not { } value) return false;
        var block = Block(BecomePassive.BlockId);
        if (code == "EFFECT_CHANGE_RACE")
        {
            var race = Enum.GetValues<CardRace>().Where(r => r != CardRace.None).FirstOrDefault(r => EnumNames.LuaConstant(r) == value);
            if (race == CardRace.None) return false;
            block.With("what", "race").With("race", race.ToString());
        }
        else
        {
            var attribute = Enum.GetValues<CardAttribute>().Where(a => a != CardAttribute.None).FirstOrDefault(a => EnumNames.LuaConstant(a) == value);
            if (attribute == CardAttribute.None) return false;
            block.With("what", "attribute").With("attribute", attribute.ToString());
        }
        var target = set.GetValueOrDefault("Target");
        var filter = FieldTargets(tr, target, fns);
        if (filter.Zones != Model.Zone.MonsterZone) return false;
        // With a target function the block checks it, and that the monster is face-up: so must the script.
        if (target is not null && !(target.StartsWith(fns.Prefix + ".", StringComparison.Ordinal) && Body(target, fns).Contains("c:IsFaceup()", StringComparison.Ordinal)))
            throw No("its lasting effect changes monsters' Type or Attribute picked in a way the blocks can't");
        if (target is null == BecomePassive.NeedsTarget(filter)) return false;
        effect.Do.Add(block.With("cards", filter));
        return true;
    }

    // ---------------------------------------------------------------- whole effects

    /// <summary>
    /// An effect whose target step and operation together are one block of this file, read whole: a Trap Monster's
    /// "Special Summon this card as ...", or "switch control of 1 monster you control and 1 your opponent controls".
    /// </summary>
    private static BlockInstance? WholeEffectOf(HashSet<string> type, Dictionary<string, string> set, Functions fns, CardData card) =>
        TrapMonsterOf(type, set, fns, card) ?? SwapControlOf(set, fns);

    /// <summary>The body of one of the script's functions as one line, or "" when it isn't one.</summary>
    private static string Joined(string reference, Functions fns)
    {
        try { return string.Join(" ", fns.Get(reference).Body.Select(l => l.Trim()).Where(l => l.Length > 0)); }
        catch (Untranslatable) { return ""; }
    }

    private static readonly Regex SwapTarget = new(
        @"^if chkc then return false end if chk==0 then return Duel\.IsExistingTarget\(([\w.]+),tp,(LOCATION_MZONE,0|0,LOCATION_MZONE),1,nil\) and Duel\.IsExistingTarget\(([\w.]+),tp,(LOCATION_MZONE,0|0,LOCATION_MZONE),1,nil\) end " +
        @"Duel\.Hint\(HINT_SELECTMSG,tp,HINTMSG_CONTROL\) local (\w+)=Duel\.SelectTarget\(tp,([\w.]+),tp,(LOCATION_MZONE,0|0,LOCATION_MZONE),1,1,nil\) " +
        @"Duel\.Hint\(HINT_SELECTMSG,tp,HINTMSG_CONTROL\) local (\w+)=Duel\.SelectTarget\(tp,([\w.]+),tp,(LOCATION_MZONE,0|0,LOCATION_MZONE),1,1,nil\) " +
        @"(?:\5:Merge\(\8\) Duel\.SetOperationInfo\(0,CATEGORY_CONTROL,\5,2,0,0\)|\8:Merge\(\5\) Duel\.SetOperationInfo\(0,CATEGORY_CONTROL,\8,2,0,0\))$");

    /// <summary>
    /// "Target 1 monster you control and 1 monster your opponent controls; switch control of both monsters": two targets,
    /// one on each side, each checked to be able to change control with a zone free, then Duel.SwapControl.
    /// </summary>
    private static BlockInstance? SwapControlOf(Dictionary<string, string> set, Functions fns)
    {
        if (set.GetValueOrDefault("Target") is not { } tg || set.GetValueOrDefault("Operation") is not { } op) return null;
        var t = SwapTarget.Match(Joined(tg, fns));
        if (!t.Success) return null;
        if (!Regex.IsMatch(Joined(op, fns), @"^local g=Duel\.GetChainInfo\(0,CHAININFO_TARGET_CARDS\) local a=g:GetFirst\(\) local b=g:GetNext\(\) if a:IsRelateToEffect\(e\) and b:IsRelateToEffect\(e\) then Duel\.SwapControl\(a,b\) end$"))
            return null;
        // Each side: the filter its check and its selection use, the same one.
        var sides = new Dictionary<string, string>();
        foreach (var (fn, where) in new[] { (t.Groups[1].Value, t.Groups[2].Value), (t.Groups[3].Value, t.Groups[4].Value), (t.Groups[6].Value, t.Groups[7].Value), (t.Groups[9].Value, t.Groups[10].Value) })
        {
            if (sides.TryGetValue(where, out var seen) && seen != fn) return null;
            sides[where] = fn;
        }
        if (sides.Count != 2) return null;
        CardFilter SideFilter(string fn)
        {
            var body = Body(fn, fns);
            const string able = " and c:IsAbleToChangeControler() and Duel.GetMZoneCount(tp,c,tp,LOCATION_REASON_CONTROL)>0";
            var r = Regex.Match(body, @"^return (.+)$");
            if (!r.Success || !body.EndsWith(able, StringComparison.Ordinal) && body != "return " + able[5..])
                throw No("it switches control of monsters picked in a way the blocks can't");
            var f = new CardFilter { CardType = FilterCardType.Monster };
            var terms = body == "return " + able[5..] ? "" : body["return ".Length..^able.Length];
            foreach (var term in terms.Length == 0 ? [] : SplitTopLevelAnd(terms)) Term(term.Trim(), f);
            f.CardType = FilterCardType.Monster;
            f.Zones = Model.Zone.None;
            return f;
        }
        return Block(SwapControlAction.BlockId).With("mine", SideFilter(sides["LOCATION_MZONE,0"])).With("theirs", SideFilter(sides["0,LOCATION_MZONE"]));
    }

    // ---------------------------------------------------------------- Trap Monsters

    private static readonly Regex TrapMonsterCan = new(
        @"Duel\.IsPlayerCanSpecialSummonMonster\(tp,(id|\d+),(\w+),(TYPES_NORMAL_TRAP_MONSTER|TYPES_EFFECT_TRAP_MONSTER),(\d+),(\d+),(\d+),(RACE_\w+),(ATTRIBUTE_\w+)\)");

    /// <summary>
    /// A Trap Card's "Special Summon this card as an Effect Monster (...)", written as official Trap Monsters write it: the
    /// target checks that its cost was checked, a free zone and the monster's stats; the operation Summons it with them.
    /// </summary>
    private static BlockInstance? TrapMonsterOf(HashSet<string> type, Dictionary<string, string> set, Functions fns, CardData card)
    {
        if (card.Kind != CardKind.Trap || !type.SetEquals(["EFFECT_TYPE_ACTIVATE"])) return null;
        if (set.GetValueOrDefault("Target") is not { } tg || set.GetValueOrDefault("Operation") is not { } op) return null;
        var target = Joined(tg, fns);
        var operation = Joined(op, fns);
        var can = TrapMonsterCan.Match(target);
        if (!can.Success) return null;
        var c = Regex.Escape(can.Value);
        if (!Regex.IsMatch(target, $@"^(?:local c=e:GetHandler\(\) )?if chk==0 then return e:IsCostChecked\(\) and Duel\.GetLocationCount\(tp,LOCATION_MZONE\)>0 and {c} end Duel\.SetOperationInfo\(0,CATEGORY_SPECIAL_SUMMON,(?:e:GetHandler\(\)|c),1,0,0\)$"))
            return null;
        var normal = can.Groups[3].Value == "TYPES_NORMAL_TRAP_MONSTER";
        var kind = normal ? "NORMAL" : "EFFECT";
        var om = Regex.Match(operation, $@"^local c=e:GetHandler\(\) if not {c} then return end c:AddMonsterAttribute\((?:TYPE_{kind}\+TYPE_TRAP|TYPE_TRAP\+TYPE_{kind})\) Duel\.SpecialSummon\(c,SUMMON_VALUE_SELF,tp,tp,true,false,(POS_FACEUP|POS_FACEUP_DEFENSE|POS_FACEUP_ATTACK)\)$");
        if (!om.Success) return null;
        // The script's own passcode and archetypes, which the block writes itself.
        if (can.Groups[1].Value != "id" && Int64(can.Groups[1].Value) != _sourceId) return null;
        var setcode = can.Groups[2].Value;
        var packed = setcode == "0" ? 0 : setcode.StartsWith("0x", StringComparison.Ordinal) ? Convert.ToInt64(setcode[2..], 16) : -1;
        if (packed != CdbEncoder.PackSetcodes(card.Archetypes)) return null;
        var race = Enum.GetValues<CardRace>().Where(r => r != CardRace.None).FirstOrDefault(r => EnumNames.LuaConstant(r) == can.Groups[7].Value);
        var attribute = Enum.GetValues<CardAttribute>().Where(a => a != CardAttribute.None).FirstOrDefault(a => EnumNames.LuaConstant(a) == can.Groups[8].Value);
        if (race == CardRace.None || attribute == CardAttribute.None) return null;
        var level = Int(can.Groups[6].Value);
        if (level is < 1 or > 12) return null;
        return Block(TrapMonsterAction.BlockId)
            .With("kind", normal ? "normal" : "effect")
            .With("race", race.ToString())
            .With("attribute", attribute.ToString())
            .With("level", level)
            .With("atk", Int(can.Groups[4].Value))
            .With("def", Int(can.Groups[5].Value))
            .With("position", om.Groups[1].Value switch { "POS_FACEUP_DEFENSE" => "defense", "POS_FACEUP_ATTACK" => "attack", _ => "faceup" });
    }

    // ---------------------------------------------------------------- actions

    /// <summary>
    /// Actions of this file's blocks: placing a card on top of or under the Deck, banishing face-down, and changing battle
    /// positions in the ways the position block does exactly. Null when the line is none of them, so the usual reading goes on.
    /// </summary>
    private static BlockInstance? MechanicsAction(string line, Dictionary<string, (CardFilter Filter, int Count)> groups, TargetInfo target, bool targetCard, CardData card, ref bool self)
    {
        Match m;
        // "Special Summon this card", written with e:GetHandler() instead of c.
        if ((m = Regex.Match(line, @"^Duel\.SpecialSummon\(e:GetHandler\(\),(.*)$")).Success)
            return Action("Duel.SpecialSummon(c," + m.Groups[1].Value, groups, target, targetCard, card, ref self);
        if ((m = Regex.Match(line, @"^Duel\.SendtoDeck\((\w+|e:GetHandler\(\)),nil,(SEQ_DECKTOP|SEQ_DECKBOTTOM|0|1),REASON_EFFECT\)$")).Success)
        {
            var where = m.Groups[2].Value is "SEQ_DECKBOTTOM" or "1" ? "bottom" : "top";
            if (m.Groups[1].Value is "c" or "e:GetHandler()")
                return Block(PlaceOnDeckAction.BlockId).With("cards", new CardFilter { ThisCard = true }).With("where", where);
            if (CardsOf(m.Groups[1].Value, groups, target, targetCard) is not { } placed || placed.Count != 1 || placed.Filter.Zones.HasFlag(Model.Zone.Deck)) return null;
            return Block(PlaceOnDeckAction.BlockId).With("cards", placed.Filter).With("target", placed.Targeted).With("where", where);
        }
        // From MechanicsRewrite: a card picked from your own Deck, moved to its top.
        if ((m = Regex.Match(line, @"^Duel\.CardCrafterDeckTop\((\w+)\)$")).Success)
        {
            if (CardsOf(m.Groups[1].Value, groups, target, targetCard) is not { Targeted: false, Count: 1 } taken
                || taken.Filter.Zones != Model.Zone.Deck || taken.Filter.Owner != CardOwner.You)
                throw No("it moves a card to the top of the Deck in a way the blocks can't");
            return Block(PlaceOnDeckAction.BlockId).With("cards", taken.Filter).With("where", "top");
        }
        // "Look at the top 3 cards of your Deck, then place them on top of the Deck in any order."
        if ((m = Regex.Match(line, @"^Duel\.SortDecktop\(tp,(tp|1-tp),(\d+)\)$")).Success)
            return Block(SortDeckTopAction.BlockId).With("whose", m.Groups[1].Value == "tp" ? "you" : "opponent").With("count", Int(m.Groups[2].Value));
        // From EndPhaseRewrite: "..., but destroy it during the End Phase".
        if ((m = Regex.Match(line, @"^Duel\.CardCrafterEndPhase\((\w+)\)$")).Success)
            return Block(EndPhaseAction.BlockId).With("how", m.Groups[1].Value);
        if ((m = Regex.Match(line, @"^Duel\.CardCrafterDamageBoth\((\d+)\)$")).Success)
            return Block(DamageBothAction.BlockId).With("amount", Int(m.Groups[1].Value));
        // From MechanicsRewrite: "equip it to this card", with the limit that only this card can hold it.
        if ((m = Regex.Match(line, @"^Duel\.CardCrafterEquipToThis\((\w+)\)$")).Success)
        {
            if (CardsOf(m.Groups[1].Value, groups, target, targetCard) is not { Count: 1 } equipped || equipped.Filter.ThisCard)
                throw No("it equips cards to this card in a way the blocks can't");
            var f = equipped.Filter.Clone();
            if (f.CardType != FilterCardType.Monster) throw No("it equips cards that may not be monsters to this card");
            // The block never picks this card itself; on the field the script must not either.
            if (f.Zones.HasFlag(Model.Zone.MonsterZone) && !f.ExcludeThisCard) throw No("it may pick this card to equip to itself");
            return Block(EquipToThisAction.BlockId).With("cards", f).With("target", equipped.Targeted);
        }
        // "Banish it face-down": the filter must ask what the block asks (that it can be banished face-down).
        if ((m = Regex.Match(line, @"^Duel\.Remove\((\w+),POS_FACEDOWN,REASON_EFFECT\)$")).Success)
        {
            if (_opValues?.Fns is not { } fns || !fns.ByName.Values.Any(f => f.Body.Any(l => l.Contains("IsAbleToRemove(tp,POS_FACEDOWN)", StringComparison.Ordinal)))) return null;
            if (CardsOf(m.Groups[1].Value, groups, target, targetCard) is not { } banished) return null;
            return CountedBlock("do.banish", banished).With("facedown", true);
        }
        if ((m = Regex.Match(line, @"^Duel\.ChangePosition\((\w+),(\w+)(?:,(\w+),(\w+),(\w+))?\)$")).Success)
            return PositionOf(m, groups, target, targetCard);
        return null;
    }

    private sealed record PickedCards(CardFilter Filter, int Count, bool Targeted);

    /// <summary>The cards a step acts on: a group the operation picked, or the target.</summary>
    private static PickedCards? CardsOf(string what, Dictionary<string, (CardFilter Filter, int Count)> groups, TargetInfo target, bool targetCard)
    {
        if (groups.TryGetValue(what, out var g)) return new(g.Filter, g.Count, false);
        if (what == "tc" && targetCard && target.Filter is not null) return new(target.Filter, target.Count, true);
        return null;
    }

    /// <summary>A card block for picked cards: how many, "up to" or "all", and whether they are targeted.</summary>
    private static BlockInstance CountedBlock(string id, PickedCards cards)
    {
        var def = BlockRegistry.Find(id)!;
        var block = Block(id).With("count", Math.Max(1, Math.Abs(cards.Count))).With("cards", cards.Filter);
        if (cards.Count < 0)
        {
            if (def.Param("mode")?.Choices.Any(c => c.Value == "up_to") != true) throw No("it acts on a varying number of cards, which that block can't");
            block = block.With("mode", "up_to");
        }
        if (cards.Count == 0)
        {
            if (def.Param("mode")?.Choices.Any(c => c.Value == "all") != true) throw No("it acts on every matching card, which that block can't");
            block = block.With("mode", "all");
        }
        if (def.Param("target") is not null) block = block.With("target", cards.Targeted);
        else if (cards.Targeted) throw No("it targets in a way that block can't");
        return block;
    }

    /// <summary>Battle positions a monster can be in: face-up Attack, face-up Defense, face-down Defense.</summary>
    private enum Pos { Attack, Defense, FaceDown }

    /// <summary>
    /// "Duel.ChangePosition(tc,POS_FACEUP_DEFENSE,0,POS_FACEUP_ATTACK,0)" and its kin: the position block whose choice moves
    /// every monster the script can pick to the same position, and can pick exactly those monsters. Null when none does.
    /// </summary>
    private static BlockInstance? PositionOf(Match m, Dictionary<string, (CardFilter Filter, int Count)> groups, TargetInfo target, bool targetCard)
    {
        if (CardsOf(m.Groups[1].Value, groups, target, targetCard) is not { } cards || cards.Filter.Zones != Model.Zone.MonsterZone) return null;
        Pos? Arg(string s) => s switch { "POS_FACEUP_ATTACK" => Pos.Attack, "POS_FACEUP_DEFENSE" => Pos.Defense, "POS_FACEDOWN_DEFENSE" => Pos.FaceDown, _ => null };
        var single = !m.Groups[3].Success;
        // Where a face-up Attack, face-up Defense and face-down Defense monster ends up; "0" leaves it as it is.
        var args = single ? [m.Groups[2].Value, m.Groups[2].Value, m.Groups[2].Value] : new[] { m.Groups[2].Value, m.Groups[4].Value, m.Groups[5].Value };
        var official = new Dictionary<Pos, Pos>();
        var starts = new[] { Pos.Attack, Pos.Defense, Pos.FaceDown };
        for (int i = 0; i < 3; i++)
        {
            if (args[i] == "0" && !single) { official[starts[i]] = starts[i]; continue; }
            if (Arg(args[i]) is not { } to) return null;
            official[starts[i]] = to;
        }
        // The face-down Attack Position slot never applies: monsters are never face-down in Attack Position.
        if (!single && m.Groups[3].Value != "0" && Arg(m.Groups[3].Value) is null) return null;
        var f = cards.Filter;
        var possible = f.Position switch
        {
            FilterPosition.Attack or FilterPosition.FaceUpAttack => [Pos.Attack],
            FilterPosition.Defense => [Pos.Defense, Pos.FaceDown],
            FilterPosition.FaceUpDefense => [Pos.Defense],
            FilterPosition.FaceDownDefense => [Pos.FaceDown],
            _ => starts.ToList(),
        };
        if (f.FaceUpOnly) possible.Remove(Pos.FaceDown);
        if (f.FaceDownOnly) possible.RemoveAll(p => p != Pos.FaceDown);
        if (possible.Count == 0) return null;
        // What each choice of the block does, and to which monsters (its own check on the cards it picks).
        var choices = new (string To, Dictionary<Pos, Pos> Moves, string Check)[]
        {
            ("defense", new() { [Pos.Attack] = Pos.Defense, [Pos.FaceDown] = Pos.Defense }, "IsCanChangePosition"),
            ("attack", new() { [Pos.Defense] = Pos.Attack, [Pos.FaceDown] = Pos.Attack }, "IsCanChangePosition"),
            ("toggle", new() { [Pos.Attack] = Pos.Defense, [Pos.Defense] = Pos.Attack, [Pos.FaceDown] = Pos.Attack }, "IsCanChangePosition"),
            ("facedown", new() { [Pos.Attack] = Pos.FaceDown, [Pos.Defense] = Pos.FaceDown }, "IsCanTurnSet"),
        };
        foreach (var (to, moves, check) in choices)
        {
            if (!possible.All(p => moves.TryGetValue(p, out var end) && end == official[p])) continue;
            // The block only picks monsters that can change position (or be Set): the script's filter must ask the same.
            if (_opValues?.Fns is not { } fns || !fns.ByName.Values.Any(fn => fn.Body.Any(l => l.Contains(check + "()", StringComparison.Ordinal) || l.Contains("Card." + check, StringComparison.Ordinal))))
                return null;
            return CountedBlock("do.change_position", cards).With("to", to);
        }
        return null;
    }
}
