using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

public static partial class EffectTextGenerator
{
    /// <summary>A Spirit monster's rule: "During the End Phase of the turn this card is Normal Summoned or flipped face-up: Return it to the hand."</summary>
    private static string SpiritText(CardProject project) =>
        project.Card.IsMonster && project.Card.Abilities.HasFlag(MonsterAbility.Spirit)
            ? "During the End Phase of the turn this card is Normal Summoned or flipped face-up: Return it to the hand."
            : "";

    /// <summary>
    /// The Gemini rule and the effects it gives, each on its own "●" line: "... to have it become an Effect Monster with this
    /// effect. ● ..." Empty when no effect waits for the Gemini Summon.
    /// </summary>
    private static string GeminiText(CardProject project, CardPack? pack, string? combined)
    {
        var effects = project.Effects.Where(GeminiCondition.Has).ToList();
        if (effects.Count == 0) return "";
        var texts = effects.Select(e => Describe(e, project.Card, pack, limitSentence: combined is null || !UsesCombinedLimit(e))).Where(t => t.Length > 0).ToList();
        return GeminiCondition.Rule(texts.Count) + string.Concat(texts.Select(t => OptionLine + t));
    }
}
