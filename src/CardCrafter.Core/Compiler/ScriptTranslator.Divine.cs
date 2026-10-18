using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

// The Egyptian Gods' Summoning rules, read back from their scripts: 3 Tributes to Normal Summon (cannot be Normal Set),
// a Normal Summon that cannot be negated, and no responses to it.
public static partial class ScriptTranslator
{
    /// <summary>One of the Gods' Summoning rules as a lasting effect, or null when the piece is none of them.</summary>
    private static TranslatedPiece? DivinePieceOf(ScriptPiece piece, List<Dictionary<string, string>> sets, Functions fns)
    {
        // "(cannot be Normal Set)" on its own: the "Requires 3 Tributes" block writes it with the Summon.
        if (sets is [var only] && IsNeverSet(only, fns) && Regex.IsMatch(_sourceLua ?? "", @"SetCode\(EFFECT_LIMIT_SUMMON_PROC\)"))
            return new TranslatedPiece { Piece = piece, Ignored = true };
        if (DivineRuleOf(sets, fns) is not { } block) return null;
        var effect = new EffectEvent { Label = piece.Title, When = Block("when.while_face_up"), Do = [block] };
        return new TranslatedPiece { Piece = piece, Effect = effect };
    }

    /// <summary>EFFECT_LIMIT_SET_PROC whose condition never holds: "cannot be Normal Set".</summary>
    private static bool IsNeverSet(Dictionary<string, string> set, Functions fns)
    {
        if (set.GetValueOrDefault("Code") != "EFFECT_LIMIT_SET_PROC" || set.GetValueOrDefault("Type") != "EFFECT_TYPE_SINGLE" || set.Keys.Except(["Type", "Code", "Condition"]).Any()) return false;
        try { return Regex.Replace(Body(set["Condition"], fns), @"\s+", " ") == "if not c then return true end return false"; }
        catch (Untranslatable) { return false; }
    }

    /// <summary>Whether the script has a "cannot be Normal Set" procedure (for a Summon procedure in a piece of its own).</summary>
    private static bool HasNeverSet()
    {
        var lua = _sourceLua ?? "";
        var set = Regex.Match(lua, @"(\w+):SetCode\(EFFECT_LIMIT_SET_PROC\)");
        if (!set.Success) return false;
        var con = Regex.Match(lua, $@"\b{set.Groups[1].Value}:SetCondition\(([\w.]+)\)");
        if (!con.Success) return false;
        // A function of another piece: read from the script itself.
        var fn = Regex.Match(lua, $@"function {Regex.Escape(con.Groups[1].Value)}\(e,c,minc\)\s*\n(.*?)\nend", RegexOptions.Singleline);
        return fn.Success && Regex.Replace(fn.Groups[1].Value, @"\s+", " ").Trim() == "if not c then return true end return false";
    }

    private static BlockInstance? DivineRuleOf(List<Dictionary<string, string>> sets, Functions fns)
    {
        string Fn(string reference)
        {
            try { return Regex.Replace(Regex.Replace(Body(reference, fns), @"\b(?:c\d+|s)\.", "s."), @"\s+", " "); }
            catch (Untranslatable) { return ""; }
        }
        bool Only(Dictionary<string, string> set, params string[] keys) => set.Keys.Where(k => k != "Description").ToHashSet().SetEquals(keys);
        const string Fixed = "EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_UNCOPYABLE";
        bool IsFixed(Dictionary<string, string> set) => Flags(set.GetValueOrDefault("Property") ?? "").SetEquals(Fixed.Split('+'));

        // "Requires 3 Tributes to Normal Summon (cannot be Normal Set)." or "... to Normal Summon/Set.": the Set procedure is a
        // copy of the Summon one, or one that never applies (in its own piece when the script gives it no comment).
        var alone = sets.Count == 1 && HasNeverSet();
        if ((sets.Count == 2 && sets[1].GetValueOrDefault("Code") == "EFFECT_LIMIT_SET_PROC" || alone) && sets[0].GetValueOrDefault("Code") == "EFFECT_LIMIT_SUMMON_PROC")
        {
            var proc = sets[0];
            if (!Only(proc, "Property", "Type", "Code", "Condition", "Operation", "Value") || proc["Type"] != "EFFECT_TYPE_SINGLE" || !IsFixed(proc) || proc["Value"] != "SUMMON_TYPE_ADVANCE")
                return null;
            var con = Regex.Match(Fn(proc["Condition"]), @"^if c==nil then return true end return minc<=(\d) and Duel\.CheckTribute\(c,\1\)$");
            if (!con.Success) return null;
            var n = con.Groups[1].Value;
            if (Fn(proc["Operation"]) != $"local g=Duel.SelectTribute(tp,c,{n},{n}) c:SetMaterial(g) Duel.Release(g,REASON_SUMMON+REASON_MATERIAL)") return null;
            var block = Block(TributeSummonPassive.BlockId).With("count", n);
            if (alone) return block;
            var set = sets[1];
            // The same procedure for Setting it.
            if (set.Where(kv => kv.Key != "Code").OrderBy(kv => kv.Key).SequenceEqual(proc.Where(kv => kv.Key != "Code").OrderBy(kv => kv.Key))) return block.With("set", "set");
            if (Only(set, "Type", "Code", "Condition") && set["Type"] == "EFFECT_TYPE_SINGLE" && Fn(set["Condition"]) == "if not c then return true end return false") return block;
            return null;
        }
        if (sets.Count != 1) return null;
        var one = sets[0];
        // "This card's Normal Summon cannot be negated."
        if (Only(one, "Type", "Code", "Property") && one["Type"] == "EFFECT_TYPE_SINGLE" && IsFixed(one) && one["Code"] is "EFFECT_CANNOT_DISABLE_SUMMON" or "EFFECT_CANNOT_DISABLE_SPSUMMON")
            return Block("passive.self_rule").With("rule", one["Code"] == "EFFECT_CANNOT_DISABLE_SUMMON" ? "summon_not_negated" : "spsummon_not_negated");
        // "When Normal Summoned, (other) cards and effects cannot be activated."
        if (Only(one, "Type", "Code", "Operation") && one["Type"] == "EFFECT_TYPE_SINGLE+EFFECT_TYPE_CONTINUOUS" && one["Code"] == "EVENT_SUMMON_SUCCESS")
        {
            var op = Fn(one["Operation"]);
            if (op == "Duel.SetChainLimitTillChainEnd(aux.FALSE)") return Block("passive.self_rule").With("rule", "summon_no_chain");
            // Ra: only effects of this card itself ("return function(e,rp,tp) return e:GetHandler()==c end").
            var own = Regex.Match(op, @"^Duel\.SetChainLimitTillChainEnd\(s\.(\w+)\(e:GetHandler\(\)\)\)$");
            if (own.Success && Regex.Replace(Fn(fns.Prefix + "." + own.Groups[1].Value), @"function ?\(", "function(") == "return function(e,rp,tp) return e:GetHandler()==c end")
                return Block("passive.self_rule").With("rule", "summon_no_chain_other");
        }
        return null;
    }
}
