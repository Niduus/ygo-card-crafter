using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>Organize on the Effects tab, and the archetype reminder line on the Card tab.</summary>
public sealed partial class CardViewModel
{
    // ---- Organize
    /// <summary>What the last Organize changed (or why it changed nothing), shown above the effects.</summary>
    [ObservableProperty] private string _organizeReport = "";
    public bool HasOrganizeReport => OrganizeReport.Length > 0;
    partial void OnOrganizeReportChanged(string value) => OnPropertyChanged(nameof(HasOrganizeReport));

    /// <summary>The effects and typed-over strings before the last Organize, and the effects it left, to undo it while nothing else changed.</summary>
    private (List<EffectEvent> Effects, List<string> Strings, string After)? _beforeOrganize;

    /// <summary>Undo is offered until the effects are changed in another way.</summary>
    [ObservableProperty] private bool _canUndoOrganize;

    /// <summary>
    /// Puts the effects in the order printed cards use and tidies them (Core's <see cref="CardOrganizer"/>); every change
    /// keeps what the card does. The report lists the changes, and Undo puts the effects back.
    /// </summary>
    [RelayCommand]
    private void Organize()
    {
        var before = (PackSerializer.Clone(Project.Effects), Project.StringOverrides.ToList());
        var result = CardOrganizer.Organize(Project, _pack());
        OrganizeReport = result.Summary;
        if (!result.Changed)
        {
            _beforeOrganize = null;
            CanUndoOrganize = false;
            return;
        }
        _beforeOrganize = (before.Item1, before.Item2, EffectsJson());
        ReloadFromProject();
    }

    [RelayCommand]
    private void UndoOrganize()
    {
        if (_beforeOrganize is not { } before || EffectsJson() != before.After) return;
        Project.Effects = before.Effects;
        Project.StringOverrides = before.Strings;
        _beforeOrganize = null;
        OrganizeReport = "Undid Organize: the effects are as they were.";
        ReloadFromProject();
    }

    [RelayCommand]
    private void DismissOrganizeReport()
    {
        OrganizeReport = "";
        _beforeOrganize = null;
        CanUndoOrganize = false;
    }

    private string EffectsJson() => System.Text.Json.JsonSerializer.Serialize(new { Project.Effects, Project.StringOverrides }, PackSerializer.Options);

    // ---- "(This card is always treated as a "X" card.)"
    public bool ArchetypeReminder
    {
        get => C.ArchetypeReminder;
        set { if (C.ArchetypeReminder == value) return; C.ArchetypeReminder = value; OnPropertyChanged(); Changed(); }
    }

    /// <summary>Why the typed card text needs the archetype line, or empty.</summary>
    [ObservableProperty] private string _archetypeReminderNote = "";
    public bool HasArchetypeReminderNote => ArchetypeReminderNote.Length > 0;
    partial void OnArchetypeReminderNoteChanged(string value) => OnPropertyChanged(nameof(HasArchetypeReminderNote));

    /// <summary>Puts "(This card is always treated as a "X" card.)" into the typed card text, where official cards print it.</summary>
    [RelayCommand]
    private void AddArchetypeReminder() => Text = Core.Text.ArchetypeReminder.AddTo(Project, _pack());

    /// <summary>Called at the end of every refresh.</summary>
    private void RefreshOrganizeAndReminder(CardPack pack)
    {
        ArchetypeReminderNote = Core.Text.ArchetypeReminder.Check(Project, pack)?.Message ?? "";
        CanUndoOrganize = _beforeOrganize is { } before && EffectsJson() == before.After;
    }
}
