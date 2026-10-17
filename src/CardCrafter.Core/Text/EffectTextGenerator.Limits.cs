using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>Where a Spell/Trap Card's "You can only activate 1 "X" per turn." goes.</summary>
public static partial class EffectTextGenerator
{
    /// <summary>"You can only activate 1 "X" per turn." (or "per Duel.") for the card's activation.</summary>
    private static string ActivationLimit(EffectEvent activation, string name) =>
        activation.Limit == OncePerTurn.Duel ? $"You can only activate 1 \"{name}\" per Duel." : $"You can only activate 1 \"{name}\" per turn.";

    /// <summary>
    /// The card's limited activation when other effects follow it: printed cards close with its "You can only activate 1
    /// "X" per turn." after them (Cyber Emergency, Artifact Sanctum). Null when the activation's text ends the card.
    /// </summary>
    private static EffectEvent? LateActivationLimit(List<EffectEvent> effects)
    {
        var at = effects.FindIndex(e => e.Limit is OncePerTurn.Hard or OncePerTurn.Duel && e.When is { } w
                                        && BlockRegistry.Find(w.BlockId) is { } t && (t is ActivateTrigger || DetailedTriggers.IsCardActivation(t, w)));
        return at >= 0 && effects.Skip(at + 1).Any(e => e.When is not null) ? effects[at] : null;
    }
}
