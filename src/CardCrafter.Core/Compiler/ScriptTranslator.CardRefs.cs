using System.Text.RegularExpressions;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

public static partial class ScriptTranslator
{
    /// <summary>
    /// "Special Summon it, but negate its effects", as official scripts write it: the monster is Special Summoned as a step,
    /// gets EFFECT_DISABLE and EFFECT_DISABLE_EFFECT (RESET_TURN_SET) until it leaves the field, and the Summon is completed
    /// ("if Duel.SpecialSummonStep(tc,0,tp,tp,false,false,POS_FACEUP) then ... end Duel.SpecialSummonComplete()"). Marked as
    /// one statement the Action reader turns into the Special Summon block with "but negate its effects".
    /// </summary>
    private static string NegatedSummonRewrite(string lua) =>
        NegatedStep().Replace(lua, m =>
        {
            var (indent, card) = (m.Groups["i"].Value, m.Groups["card"].Value);
            if (!IsNegation(m.Groups["body"].Value, card)) return m.Value;
            var marker = $"Duel.SpecialSummon({card},CCNEGATED,tp,tp,{m.Groups["nocheck"].Value},false,{m.Groups["pos"].Value})";
            // "if tc:IsRelateToEffect(e) and Duel.SpecialSummonStep(...)": the checks before the Summon stay.
            var checks = m.Groups["checks"].Success ? m.Groups["checks"].Value[..^" and ".Length] : null;
            return checks is null ? $"{indent}{marker}" : $"{indent}if {checks} then\n{indent}\t{marker}\n{indent}end";
        });

    /// <summary>The lines between the step and its "end": the two negating effects, registered on the Summoned card.</summary>
    private static bool IsNegation(string body, string card)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var allowed = new Regex($@"^(?:local c=e:GetHandler\(\)|local \w+=Effect\.CreateEffect\((?:c|e:GetHandler\(\))\)|local \w+=\w+:Clone\(\)|\w+:SetType\(EFFECT_TYPE_SINGLE\)|\w+:SetCode\(EFFECT_DISABLE(?:_EFFECT)?\)" +
                                $@"|\w+:SetValue\(RESET_TURN_SET\)|\w+:SetReset\(RESET_EVENT\+RESETS_STANDARD\)|{Regex.Escape(card)}:RegisterEffect\(\w+(?:,true)?\))$");
        return lines.All(allowed.IsMatch)
               && lines.Any(l => l.EndsWith(":SetCode(EFFECT_DISABLE)", StringComparison.Ordinal))
               && lines.Any(l => l.EndsWith(":SetCode(EFFECT_DISABLE_EFFECT)", StringComparison.Ordinal))
               && lines.Count(l => l.Contains(":RegisterEffect(", StringComparison.Ordinal)) == 2;
    }

    [GeneratedRegex(@"(?m)^(?<i>[ \t]*)if (?<checks>(?:(?:tc|c) and |(?:tc|c):IsRelateToEffect\(e\) and |(?:tc|c):IsFaceup\(\) and )+)?Duel\.SpecialSummonStep\((?<card>tc|c),0,tp,tp,(?<nocheck>false|true),false,(?<pos>POS_FACEUP(?:_ATTACK|_DEFENSE)?)\) then[ \t]*\n" +
                    @"(?<body>(?:\k<i>[ \t]+\S.*\n|[ \t]*\n)+)\k<i>end[ \t]*\n\k<i>Duel\.SpecialSummonComplete\(\)[ \t]*$")]
    private static partial Regex NegatedStep();

    /// <summary>The marked Summon of <see cref="NegatedSummonRewrite"/>: the Special Summon it is, with "but negate its effects".</summary>
    private static BlockInstance? NegatedSummonAction(string line, Dictionary<string, (CardFilter Filter, int Count)> groups, TargetInfo target, bool targetCard, CardData card, ref bool self)
    {
        var m = Regex.Match(line, @"^Duel\.SpecialSummon\((\w+),CCNEGATED,tp,tp,(false|true),false,(POS_FACEUP(?:_ATTACK|_DEFENSE)?)\)$");
        if (!m.Success) return null;
        var block = Action($"Duel.SpecialSummon({m.Groups[1].Value},0,tp,tp,{m.Groups[2].Value},false,{m.Groups[3].Value})", groups, target, targetCard, card, ref self);
        if (block.BlockId is not ("do.special_summon" or "do.special_summon_self")) throw No("it Special Summons with its effects negated in a way the blocks don't");
        // This card Summoned by its own step "ignoring its Summoning conditions" lifts the revive limit too, which this script doesn't.
        if (block.BlockId == "do.special_summon_self" && m.Groups[2].Value == "true") throw No("it Special Summons this card ignoring its conditions but not its revive limit");
        return block.With("negate", true);
    }
}
