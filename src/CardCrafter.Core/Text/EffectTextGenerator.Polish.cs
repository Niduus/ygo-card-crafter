namespace CardCrafter.Core.Text;

/// <summary>Wording that keeps the parts of an effect's text in the order printed cards use, whatever order its blocks are in.</summary>
public static partial class EffectTextGenerator
{
    /// <summary>The IF blocks' "during ..." clauses, the phase before the turn: "during the Main Phase", "during your opponent's turn".</summary>
    private static List<string> During(List<string> conditions) =>
        conditions.Where(t => t.StartsWith("during ", StringComparison.Ordinal))
            .OrderBy(t => t.EndsWith(" Phase", StringComparison.Ordinal) || t.Contains(" Phase ", StringComparison.Ordinal) ? 0 : 1)
            .ToList();

    /// <summary>
    /// "During the Main Phase, if this card is in your GY (Quick Effect)", "During your opponent's turn, if you control a
    /// Dragon monster", "If this card is Normal Summoned during your turn while ...": when the effect happens is said first.
    /// The phase and turn read together, so "during the Main Phase, during your opponent's turn" becomes "during your
    /// opponent's Main Phase".
    /// </summary>
    private static string TimingFirst(List<string> during, string cond, string joiner)
    {
        if (during.Count == 0) return cond;
        var timing = string.Join(", ", during);
        if (cond.Length == 0) return timing;
        return joiner == "while" ? $"{timing} {Clause(cond, joiner)}" : $"{timing}, {Clause(cond, joiner)}";
    }

    private static readonly string[] Summons = ["Normal", "Tribute", "Flip", "Special", "Ritual", "Fusion", "Synchro", "Xyz", "Pendulum", "Link"];

    /// <summary>"Normal or Special Summoned", "Normal, Flip, or Special Summoned": the ways of Summoning in the order cards say them.</summary>
    private static List<string> SummonOrder(List<string> labels) =>
        labels.All(l => Summons.Contains(l)) ? labels.OrderBy(l => Array.IndexOf(Summons, l)).ToList() : labels;
}
