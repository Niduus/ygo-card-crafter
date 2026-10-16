using System.Text.RegularExpressions;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>Coin tosses, die rolls and declarations, as card text says them.</summary>
public static partial class EffectTextGenerator
{
    /// <summary>
    /// "Toss a coin.\n ● Heads: Draw 2 cards.\n ● Tails: Your opponent draws 2 cards.": the lead (what comes before the
    /// first result), then each result on its own line. Dice read "Roll a six-sided die and apply the result."
    /// </summary>
    private static string ResultOptionsText(EffectEvent effect, CardPack? pack, string head, bool optional)
    {
        var options = SplitOptions(effect.Do);
        var (target, lead) = Actions(options[0], pack, effect);
        if (options[0].LastOrDefault(Chance.IsSource) is { BlockId: RollDieAction.BlockId }) lead += " and apply the result";
        var costs = string.Join(" and ", effect.Cost.Select(b => Text(b, pack, effect)).Where(t => t.Length > 0));
        var before = target is null ? costs : costs.Length == 0 ? target : $"{costs}, then {target}";
        var body = before.Length == 0 ? lead : $"{before}; {lead}";
        if (optional && body.StartsWith("you can ", StringComparison.Ordinal)) body = body["you can ".Length..];
        body = optional ? "You can " + body : SentenceCase.Upper(body);
        var text = head.Length == 0 ? body : $"{SentenceCase.Upper(head)}: {body}";
        return text + "." + string.Concat(options.Skip(1).Select(o => OptionLine + ResultLine(o, pack, effect))).TrimEnd('.');
    }

    /// <summary>"Heads: Draw 2 cards.", "1 or 2: You can Special Summon 1 monster from your GY."</summary>
    private static string ResultLine(List<BlockInstance> blocks, CardPack? pack, EffectEvent effect)
    {
        var gate = blocks[0];
        var label = BlockRegistry.Find(gate.BlockId) switch
        {
            CoinResultCondition coin => CoinResultCondition.Label(new BlockArgs(coin, gate, pack)),
            DieResultCondition die => DieResultCondition.Label(new BlockArgs(die, gate, pack)),
            _ => "",
        };
        var (target, actions) = Actions(blocks.Skip(1).ToList(), pack, effect);
        return $"{label}: {SentenceCase.Upper(target is null ? actions : $"{target}; {actions}")}.";
    }

    /// <summary>
    /// How a result IF block joins what comes before it: "toss a coin and if the result is heads, ...", "roll a six-sided
    /// die, and if you roll a 1 or 6, ...", "..., or if the result is tails, ...", and "toss a coin and call it. If you call
    /// it right, ...". Null for other blocks. <paramref name="cases"/> counts the results since the last toss or roll.
    /// </summary>
    private static string? ChanceJoiner(BlockInstance b, BlockDefinition? previous, ref int cases)
    {
        if (Chance.IsSource(b)) { cases = 0; return null; }
        if (!Chance.IsGate(b)) return null;
        var called = b.BlockId == CoinResultCondition.BlockId && b.Values.GetValueOrDefault("result") is "right" or "wrong";
        var joiner = called ? ". " : cases > 0 ? ", or " : previous is TossCoinAction ? " and " : previous is RollDieAction ? ", and " : null;
        cases++;
        return joiner;
    }

    /// <summary>
    /// "Activate this card by declaring 1 card name. Cards with that name, and their effects, cannot be used.": a Continuous
    /// card's activation that declares, as official cards word it (Prohibition, DNA Surgery).
    /// </summary>
    private static string DeclarePolish(EffectEvent effect, string text)
    {
        if (effect.When?.BlockId != "when.activate" || !effect.Do.Any(b => b.BlockId == DeclaredLockAction.BlockId && b.Values.GetValueOrDefault("duration") == "face_up"))
            return text;
        return DeclaredLead().Replace(text, m => $"Activate this card by declaring {m.Groups[1].Value}. {SentenceCase.Upper(m.Groups[2].Value)}", 1);
    }

    [GeneratedRegex(@"^Declare ([^;]+); (\w)")]
    private static partial Regex DeclaredLead();
}
