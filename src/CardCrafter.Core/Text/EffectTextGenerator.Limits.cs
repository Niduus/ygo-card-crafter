using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>Where a Spell/Trap Card's "You can only activate 1 "X" per turn." goes.</summary>
public static partial class EffectTextGenerator
{
    /// <summary>"You can only activate 1 "X" per turn." (or "per Duel.", or "2 "X" per turn") for the card's activation.</summary>
    private static string ActivationLimit(EffectEvent activation, string name)
    {
        var n = Compiler.ScriptCompiler.Times(activation);
        return activation.Limit == OncePerTurn.Duel ? $"You can only activate {n} \"{name}\" per Duel." : $"You can only activate {n} \"{name}\" per turn.";
    }

    /// <summary>"once", "twice", "thrice", "4 times".</summary>
    public static string TimesWord(int n) => n switch { 1 => "once", 2 => "twice", 3 => "thrice", _ => $"{n} times" };

    /// <summary>What goes before the effect's trigger: "Once per turn", "Up to twice per turn", "Once while face-up on the field"; null for none.</summary>
    private static string? HeadLimit(EffectEvent effect) => effect.Limit switch
    {
        OncePerTurn.Soft => Compiler.ScriptCompiler.Times(effect) == 1 ? "Once per turn" : $"Up to {TimesWord(Compiler.ScriptCompiler.Times(effect))} per turn",
        OncePerTurn.SoftAndHard => "Once per turn",
        OncePerTurn.FaceUp or OncePerTurn.FaceUpAndHard => "Once while face-up on the field",
        _ => null,
    };

    /// <summary>The sentence after the effect for a limit by card name: "You can only use this effect of "X" thrice per turn."</summary>
    private static string NameLimit(EffectEvent effect, string name, string per)
    {
        var times = effect.Limit == OncePerTurn.FaceUpAndHard ? 1 : Compiler.ScriptCompiler.Times(effect);
        return $" You can only use this effect of \"{name}\" {TimesWord(times)} per {per}.";
    }

    /// <summary>
    /// "You cannot activate both of this card's effects in the same Chain." (Sun God Unification), "You cannot activate these
    /// effects of "Elfnotes: Aristeia of Trust" in the same Chain.": one sentence for each group, at the end of the text.
    /// Printed cards name the card when one of the effects is limited by name already.
    /// </summary>
    private static IEnumerable<string> SameChainLimits(CardProject project, List<EffectEvent> effects)
    {
        foreach (var group in effects.Where(e => e.SameChainGroup > 0).GroupBy(e => e.SameChainGroup))
        {
            var members = group.ToList();
            var named = members.Any(e => e.Limit is OncePerTurn.Hard or OncePerTurn.Duel or OncePerTurn.SoftAndHard or OncePerTurn.FaceUpAndHard);
            yield return members.Count == 2 && !named
                ? "You cannot activate both of this card's effects in the same Chain."
                : $"You cannot activate these effects of \"{project.Card.Name}\" in the same Chain.";
        }
    }

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
