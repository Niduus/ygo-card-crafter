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
