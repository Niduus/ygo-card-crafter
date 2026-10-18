using CardCrafter.Core.Combos;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;

namespace CardCrafter.Core.Assistant;

/// <summary>What the assistant works on: the open pack and the card the user has selected.</summary>
public interface IAssistantWorkspace
{
    CardPack Pack { get; }

    /// <summary>The selected card, or null when the pack has none.</summary>
    CardProject? Card { get; }

    /// <summary>Tells the editor the card was changed outside it, so it shows the change and marks the pack unsaved.</summary>
    void CardChanged();

    /// <summary>Adds a new card of that kind to the pack and selects it.</summary>
    CardProject AddCard(CardKind kind);

    /// <summary>Selects the pack's card at that index (0-based).</summary>
    bool SelectCard(int index);

    /// <summary>The cards combos can use: the pack's own and every card in YGO Omega's databases.</summary>
    ComboCatalog ComboCards { get; }

    /// <summary>Tells the Combos tab the pack's combos were changed outside it.</summary>
    void CombosChanged();
}

/// <summary>A workspace that isn't attached to an editor: a pack in memory, for tests and tools.</summary>
public sealed class PackWorkspace(CardPack pack) : IAssistantWorkspace
{
    private int _selected;

    public CardPack Pack { get; } = pack;
    public CardProject? Card => _selected < Pack.Cards.Count ? Pack.Cards[_selected] : null;
    public int Changes { get; private set; }

    public void CardChanged() => Changes++;

    /// <summary>The game's cards, for combos; without it combos only know the pack's cards.</summary>
    public CardLibrary? Library { get; set; }
    public ComboCatalog ComboCards => ComboCatalog.Build(Pack, Library);
    public void CombosChanged() => Changes++;

    public CardProject AddCard(CardKind kind)
    {
        var project = new CardProject { Card = new CardData { Kind = kind, Id = PasscodeGenerator.Generate(Pack.Cards.Select(c => c.Card.Id)) } };
        Pack.Cards.Add(project);
        _selected = Pack.Cards.Count - 1;
        return project;
    }

    public bool SelectCard(int index)
    {
        if (index < 0 || index >= Pack.Cards.Count) return false;
        _selected = index;
        return true;
    }
}
