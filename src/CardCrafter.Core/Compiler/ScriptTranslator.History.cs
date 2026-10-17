using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>
/// Flags a script keeps on this card for its conditions: "if this card destroyed a monster by battle" (a watcher on its
/// battles, Number 89: Diablosis, Armed Dragon LV5) and "if this card was activated this turn" (its activation's cost,
/// Dark City at Midnight, Lunalight Masquerade). The IF blocks write their own watcher, so it is no piece of its own.
/// </summary>
public static partial class ScriptTranslator
{
    private static readonly Regex BattleFlagReg = new(@"^e:GetHandler\(\):RegisterFlagEffect\((id(?:\+\w+)?),RESET_EVENT\+RESETS_STANDARD\+RESET_PHASE\+PHASE_(END|BATTLE),0,1\)$");

    private static readonly Regex ActivationFlagReg = new(
        @"^if chk==0 then return true end e:GetHandler\(\):RegisterFlagEffect\((id(?:\+\w+)?),RESET_EVENT\+RESETS_STANDARD\+RESET_PHASE\+PHASE_END,EFFECT_FLAG_OATH,1\)$");

    /// <summary>The function a reference names ("s.regop", "c95474755.regop"), as one line; null when the script has none.</summary>
    private static string? FlatFunction(string reference)
    {
        if (_sourceLua is not { } lua || Regex.Match(reference.Trim(), @"^[\w]+\.(\w+)$") is not { Success: true } name) return null;
        return ScriptFunctions(lua).TryGetValue(name.Groups[1].Value, out var body) ? body : null;
    }

    /// <summary>The flag only <paramref name="function"/> sets in the whole script, so a condition on it means what that function remembers.</summary>
    private static bool OnlySetter(string flag, string function) =>
        _sourceLua is { } lua && ScriptFunctions(lua).Count(f => f.Value.Contains($"RegisterFlagEffect({flag},", StringComparison.Ordinal)) == 1
        && Regex.Matches(lua, $@"\b{Regex.Escape(function)}\b").Count >= 1;

    /// <summary>"e:GetHandler():RegisterFlagEffect(id,...)" on this card's battle destructions: "destroyed" (this turn) or "destroyed_bp"; null for anything else.</summary>
    private static (string Flag, string What)? BattleWatcher(HashSet<string> type, string code, Dictionary<string, string> set)
    {
        if (!type.SetEquals(["EFFECT_TYPE_SINGLE", "EFFECT_TYPE_CONTINUOUS"]) || code != "EVENT_BATTLE_DESTROYING") return null;
        if (set.Keys.Any(k => k is not ("Type" or "Code" or "Property" or "Operation"))) return null;
        if (set.GetValueOrDefault("Property") is { } prop && prop != "EFFECT_FLAG_CANNOT_DISABLE") return null;
        if (set.GetValueOrDefault("Operation") is not { } op || FlatFunction(op) is not { } body || BattleFlagReg.Match(body) is not { Success: true } m) return null;
        return (m.Groups[1].Value, m.Groups[2].Value == "END" ? "destroyed" : "destroyed_bp");
    }

    /// <summary>An activation's cost that only leaves the "activated this turn" flag on the card.</summary>
    private static bool IsActivationFlag(string cost) => FlatFunction(cost) is { } body && ActivationFlagReg.IsMatch(body);

    /// <summary>
    /// "return e:GetHandler():GetFlagEffect(id)~=0" where the flag is the battle watcher's or the activation's: the IF block
    /// that remembers the same. Null for other flags.
    /// </summary>
    private static BlockInstance? FlagConditionBlock(string body)
    {
        if (_sourceLua is not { } lua) return null;
        var normal = Regex.Replace(body, $@"\b{_sourceId}\b", "id");
        var m = Regex.Match(normal, @"^return (?:e:GetHandler\(\)|c):GetFlagEffect\((id(?:\+\w+)?)\)(?:~=0|>0)$");
        if (!m.Success) return null;
        var flag = m.Groups[1].Value;
        var fns = ScriptFunctions(lua);
        var setters = fns.Where(f => f.Value.Contains($"RegisterFlagEffect({flag},", StringComparison.Ordinal)).ToList();
        if (setters.Count != 1) return null;
        var (name, setter) = (setters[0].Key, setters[0].Value);
        if (BattleFlagReg.Match(setter) is { Success: true } battle && battle.Groups[1].Value == flag
            && Regex.IsMatch(lua, $@"(\w+):SetCode\(EVENT_BATTLE_DESTROYING\)\s*\n(?:\s*\1:\w+\(.*\)\s*\n)*?\s*\1:SetOperation\((?:s|c\d+)\.{name}\)"))
            return Block("if.battled_this_turn").With("what", battle.Groups[2].Value == "END" ? "destroyed" : "destroyed_bp");
        if (ActivationFlagReg.Match(setter) is { Success: true } act && act.Groups[1].Value == flag
            && Regex.IsMatch(lua, $@"(\w+):SetType\(EFFECT_TYPE_ACTIVATE\)\s*\n(?:\s*\1:\w+\(.*\)\s*\n)*?\s*\1:SetCost\((?:s|c\d+)\.{name}\)"))
            return Block(ActivatedThisTurnCondition.BlockId);
        return null;
    }
}
