using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Compiler;

/// <summary>What a block sees while it compiles: its values, the effect being built, and the card.</summary>
public sealed class BlockContext(BlockArgs args, EffectBuilder effect)
{
    public BlockArgs Args { get; } = args;
    public EffectBuilder Effect { get; } = effect;
    public ScriptBuilder Script => Effect.Script;
    public CardData Card => Effect.Card;
    public ActionLink Link => Args.Instance.Link;
}
