using CardCrafter.Core.Assistant;
using CardCrafter.Core.Model;

namespace CardCrafter.App.ViewModels;

/// <summary>The editor as the assistant sees it: the open pack, the selected card, and a way to show its changes.</summary>
public sealed partial class MainViewModel : IAssistantWorkspace
{
    private AssistantViewModel? _assistant;

    public AssistantViewModel Assistant => _assistant ??= new AssistantViewModel(this, _settings, _dialogs);

    CardProject? IAssistantWorkspace.Card => SelectedCard?.Project;

    void IAssistantWorkspace.CardChanged()
    {
        MarkDirty();
        SelectedCard?.ReloadFromProject();
    }

    CardProject IAssistantWorkspace.AddCard(CardKind kind)
    {
        AddCard(kind switch { CardKind.Spell => "Spell", CardKind.Trap => "Trap", _ => "Monster" });
        return SelectedCard!.Project;
    }

    bool IAssistantWorkspace.SelectCard(int index)
    {
        if (index < 0 || index >= Cards.Count) return false;
        SelectedCard = Cards[index];
        return true;
    }

    /// <summary>Every card of the pack as it is now, to put back with <see cref="RestoreCards"/>.</summary>
    public CardsSnapshot SnapshotCards() =>
        new(_pack, _pack.Cards.Select(c => (c, PackSerializer.Clone(c))).ToList(), SelectedCard is { } s ? Cards.IndexOf(s) : -1);

    /// <summary>
    /// Puts every card back as the snapshot has it: changed cards get their old contents, cards added since go away.
    /// A snapshot of another pack (one opened since) does nothing.
    /// </summary>
    public bool RestoreCards(CardsSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot.Pack, _pack)) return false;
        foreach (var (card, copy) in snapshot.Cards)
            foreach (var p in typeof(CardProject).GetProperties().Where(p => p.CanWrite)) p.SetValue(card, p.GetValue(copy));
        _pack.Cards.RemoveAll(c => snapshot.Cards.All(s => !ReferenceEquals(s.Card, c)));
        Cards.Clear();
        foreach (var c in _pack.Cards) Cards.Add(NewCardViewModel(c));
        SelectedCard = snapshot.Selected >= 0 && snapshot.Selected < Cards.Count ? Cards[snapshot.Selected] : Cards.FirstOrDefault();
        MarkDirty();
        return true;
    }
}

/// <summary>The pack's cards at one moment: each card with a copy of its contents then.</summary>
public sealed record CardsSnapshot(CardPack Pack, IReadOnlyList<(CardProject Card, CardProject Copy)> Cards, int Selected);
