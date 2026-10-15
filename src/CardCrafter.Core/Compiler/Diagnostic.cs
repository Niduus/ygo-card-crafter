namespace CardCrafter.Core.Compiler;

public enum Severity { Info, Warning, Error }

/// <summary>A problem found while building a card. EffectIndex is 1-based, null for card-wide problems.</summary>
public sealed record Diagnostic(Severity Severity, string Message, int? EffectIndex = null)
{
    public override string ToString() =>
        (EffectIndex is { } i ? $"[{Severity}] Effect {i}: " : $"[{Severity}] ") + Message;
}
