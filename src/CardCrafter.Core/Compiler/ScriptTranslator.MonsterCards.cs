using System.Text.RegularExpressions;

namespace CardCrafter.Core.Compiler;

// Monster Cards in the Spell & Trap Zone: "If this card is placed in the Spell & Trap Zone as a Continuous Spell" and
// "While this card is treated as a Continuous Spell" (the Angelechy Synchro Monsters).
public static partial class ScriptTranslator
{
    /// <summary>The trigger's event once its watchers are taken out (<see cref="PlacedAsSpellRewrite"/>).</summary>
    private const string PlacedAsSpellMarker = "CC_EVENT_PLACED_AS_SPELL";

    /// <summary>The check of the trigger and of the lasting effects: this card is a Continuous Spell (a monster placed as one).</summary>
    private const string ContinuousSpellBody = "return e:GetHandler():GetType()==TYPE_SPELL+TYPE_CONTINUOUS";

    /// <summary>
    /// "If this card is placed in the Spell &amp; Trap Zone as a Continuous Spell", as Angelechy Bastion's script writes it:
    /// a watcher of this card's moves (EVENT_MOVE) raises EVENT_CUSTOM+id at once, or keeps a flag that a watcher of the
    /// Chain (EVENT_CHAIN_SOLVED) turns into the event when the link that placed it resolves. The trigger block writes both
    /// watchers itself, so they are taken out here and the trigger's event is marked.
    /// </summary>
    private static string PlacedAsSpellRewrite(string lua)
    {
        const string head = @"(?m)^function ([\w.]+)\(e,tp,eg,ep,ev,re,r,rp\)\n\s*local c=e:GetHandler\(\)\n";
        var flagged = Regex.Match(lua, head +
            @"\s*if not c:IsLocation\(LOCATION_SZONE\) or c:GetType\(\)~=TYPE_SPELL\+TYPE_CONTINUOUS then return end\n" +
            @"\s*if Duel\.GetCurrentChain\(\)>0 then\n\s*c:RegisterFlagEffect\((id(?:\+o)?|\d+),RESET_EVENT\+RESETS_STANDARD\+RESET_CHAIN,0,1\)\n" +
            @"\s*else\n\s*Duel\.RaiseSingleEvent\(c,EVENT_CUSTOM\+id,e,0,tp,tp,0\)\n\s*end\nend\n");
        if (!flagged.Success) return lua;
        var flag = Regex.Escape(flagged.Groups[2].Value);
        var raised = Regex.Match(lua, head +
            @"\s*if c:GetType\(\)~=TYPE_SPELL\+TYPE_CONTINUOUS then return end\n" +
            $@"\s*if c:GetFlagEffect\({flag}\)~=0 then\n\s*Duel\.RaiseSingleEvent\(c,EVENT_CUSTOM\+id,e,0,tp,tp,0\)\n\s*end\nend\n");
        if (!raised.Success) return lua;
        var (move, solved) = (Regex.Escape(flagged.Groups[1].Value), Regex.Escape(raised.Groups[1].Value));
        // The two watchers, each exactly as the official scripts register them.
        var watchers = Regex.Matches(lua, @"\n[ \t]*local (\w+)=Effect\.CreateEffect\(c\)\n(?:(?![ \t]*c:RegisterEffect\(\1\)).*\n)*?[ \t]*c:RegisterEffect\(\1\)")
            .Where(m => Watcher(m.Value, "EFFECT_TYPE_SINGLE+EFFECT_TYPE_CONTINUOUS", "EVENT_MOVE", move, range: false)
                        || Watcher(m.Value, "EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS", "EVENT_CHAIN_SOLVED", solved, range: true))
            .ToList();
        if (watchers.Count != 2 || Regex.Matches(lua, @"SetCode\(EVENT_CUSTOM\+id\)").Count != 1) return lua;
        foreach (var m in watchers.OrderByDescending(m => m.Index)) lua = lua.Remove(m.Index, m.Length);
        lua = lua.Replace(flagged.Value, "", StringComparison.Ordinal).Replace(raised.Value, "", StringComparison.Ordinal);
        return lua.Replace("SetCode(EVENT_CUSTOM+id)", $"SetCode({PlacedAsSpellMarker})", StringComparison.Ordinal);
    }

    /// <summary>One watcher effect of <see cref="PlacedAsSpellRewrite"/>: its type, event and operation, and nothing else.</summary>
    private static bool Watcher(string effect, string type, string code, string operation, bool range)
    {
        var lines = effect.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var v = Regex.Match(lines[0], @"^local (\w+)=").Groups[1].Value;
        var allowed = new List<string>
        {
            $"local {v}=Effect.CreateEffect(c)", $"{v}:SetType({type})", $"{v}:SetCode({code})", $"c:RegisterEffect({v})",
        };
        if (range) allowed.Add($"{v}:SetRange(LOCATION_SZONE)");
        else allowed.Add($"{v}:SetProperty(EFFECT_FLAG_DELAY)");
        return lines.Any(l => Regex.IsMatch(l, $@"^{v}:SetOperation\({operation}\)$"))
               && lines.All(l => allowed.Contains(l) || Regex.IsMatch(l, $@"^{v}:SetOperation\({operation}\)$"))
               && lines.Contains($"{v}:SetCode({code})");
    }

    /// <summary>
    /// "While this card is treated as a Continuous Spell": a lasting effect in the Spell &amp; Trap Zone whose condition is
    /// only that this card is a Continuous Spell. Its WHEN block says so; the condition is used up. False otherwise.
    /// </summary>
    private static bool TreatedAsSpell(Model.EffectEvent effect, Dictionary<string, string> set, string? range, Functions fns)
    {
        if (range != "LOCATION_SZONE" || set.GetValueOrDefault("Condition") is not { } condition || condition.StartsWith("aux.", StringComparison.Ordinal)
            || Body(condition, fns) != ContinuousSpellBody || effect.When?.BlockId != "when.while_face_up" || _sourceCard?.IsMonster != true)
            return false;
        effect.When = Block("when.while_face_up").With("as", "spell");
        return true;
    }
}
