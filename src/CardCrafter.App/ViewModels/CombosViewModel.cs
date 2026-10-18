using System.Collections.ObjectModel;
using Avalonia.Media;
using CardCrafter.App.Services;
using CardCrafter.App.Views;
using CardCrafter.Core.Combos;
using CardCrafter.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

public enum ComboMode { Deck, Steps, Play }

/// <summary>A combo in the tab's list.</summary>
public sealed class ComboListItem(Combo model) : ObservableObject
{
    public Combo Model { get; } = model;
    public string Name => Model.Name.Trim().Length > 0 ? Model.Name : "(unnamed combo)";
    public void Refresh() => OnPropertyChanged(nameof(Name));
    public override string ToString() => Name;
}

/// <summary>A card in the deck lists, with how many copies there are.</summary>
public sealed record ComboDeckEntry(long Code, string Name, int Count, string Section, IBrush Wash, IBrush Stripe, string Tip)
{
    public string CountText => $"×{Count}";
    public bool InMain => Section == "Main";
}

/// <summary>A card in the opening hand.</summary>
public sealed record ComboHandChip(int Index, string Name, IBrush Stripe, bool CanRemove);

/// <summary>A row of the step list.</summary>
public sealed partial class ComboStepRow : ObservableObject
{
    [ObservableProperty] private int _number;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _problems = "";
    public bool IsFlagged => Problems.Length > 0;
    public bool HasNote => Note.Length > 0;
    partial void OnProblemsChanged(string value) => OnPropertyChanged(nameof(IsFlagged));
    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
}

/// <summary>
/// The Combos tab: the pack's combos, each with its deck and opening hand, the steps (written in the step editor, the
/// board worked out by replaying them) and a viewer that plays them back step by step. Every change can be undone.
/// </summary>
public sealed partial class CombosViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly IDialogService _dialogs;
    private readonly Stack<(List<Combo> Combos, int Selected, int Step)> _undo = new();
    private string? _lastEdit;
    private bool _loading;
    private ComboReplay? _replay;

    public CombosViewModel(MainViewModel main, IDialogService dialogs)
    {
        _main = main;
        _dialogs = dialogs;
        Catalog = main.ComboCards;
        Reload();
        _ = main.LoadComboLibraryAsync();
    }

    public ComboCatalog Catalog { get; private set; }
    private CardPack Pack => _main.Pack;

    public ObservableCollection<ComboListItem> Items { get; } = [];
    [ObservableProperty] private ComboListItem? _selected;
    public Combo? Combo => Selected?.Model;
    public bool HasCombo => Combo is not null;
    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty] private ComboMode _mode = ComboMode.Steps;
    public bool IsDeckMode { get => Mode == ComboMode.Deck; set { if (value) Mode = ComboMode.Deck; } }
    public bool IsStepsMode { get => Mode == ComboMode.Steps; set { if (value) Mode = ComboMode.Steps; } }
    public bool IsPlayMode { get => Mode == ComboMode.Play; set { if (value) Mode = ComboMode.Play; } }
    public bool ShowDeck => HasCombo && IsDeckMode;
    public bool ShowSteps => HasCombo && IsStepsMode;
    public bool ShowPlay => HasCombo && IsPlayMode;

    partial void OnModeChanged(ComboMode value)
    {
        OnPropertyChanged(nameof(IsDeckMode));
        OnPropertyChanged(nameof(IsStepsMode));
        OnPropertyChanged(nameof(IsPlayMode));
        OnPropertyChanged(nameof(ShowDeck));
        OnPropertyChanged(nameof(ShowSteps));
        OnPropertyChanged(nameof(ShowPlay));
        if (value == ComboMode.Play) RefreshPlay();
    }

    partial void OnSelectedChanged(ComboListItem? value)
    {
        if (_loading) return;
        _lastEdit = null;
        PlayPosition = 0;
        ShowEndBoard = false;
        RefreshAll(selectStep: Combo is { Steps.Count: > 0 } c ? c.Steps.Count - 1 : -1);
    }

    /// <summary>Shows the pack's combos again, after the pack was opened or the assistant or an undo changed them.</summary>
    public void Reload(bool keepSelection = false)
    {
        var index = keepSelection && Selected is { } s ? Items.IndexOf(s) : 0;
        var step = SelectedStepIndex;
        _loading = true;
        Items.Clear();
        foreach (var combo in Pack.Combos) Items.Add(new ComboListItem(combo));
        Selected = Items.Count == 0 ? null : Items[Math.Clamp(index, 0, Items.Count - 1)];
        _loading = false;
        if (!keepSelection) { _undo.Clear(); UndoCommand.NotifyCanExecuteChanged(); }
        Catalog = _main.ComboCards;
        OnPropertyChanged(nameof(IsEmpty));
        RefreshAll(selectStep: keepSelection ? Math.Min(step, (Combo?.Steps.Count ?? 0) - 1) : Combo is { Steps.Count: > 0 } c ? c.Steps.Count - 1 : -1);
    }

    /// <summary>The game's cards were read: names and texts can be shown.</summary>
    public void CatalogChanged()
    {
        Catalog = _main.ComboCards;
        RefreshAll(SelectedStepIndex);
    }

    private string Name(long code) => Catalog.Name(code, Combo?.Names);

    // ---------------------------------------------------------------- undo

    /// <summary>Records the combos before a change. Typing into one field counts as one change until another field is edited.</summary>
    private void Remember(string? key = null)
    {
        if (key is not null && key == _lastEdit) return;
        _lastEdit = key;
        _undo.Push((Pack.Combos.Select(PackSerializer.Clone).ToList(), Selected is { } s ? Items.IndexOf(s) : -1, SelectedStepIndex));
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void Changed()
    {
        _main.CombosEdited();
        if (Combo is { } c) ComboFile.RememberNames(c, Catalog);
    }

    private bool CanUndo() => _undo.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0) return;
        var (combos, selected, step) = _undo.Pop();
        Pack.Combos = combos;
        _lastEdit = null;
        _loading = true;
        Items.Clear();
        foreach (var c in Pack.Combos) Items.Add(new ComboListItem(c));
        Selected = selected >= 0 && selected < Items.Count ? Items[selected] : Items.FirstOrDefault();
        _loading = false;
        OnPropertyChanged(nameof(IsEmpty));
        RefreshAll(Math.Min(step, (Combo?.Steps.Count ?? 0) - 1));
        UndoCommand.NotifyCanExecuteChanged();
        _main.CombosEdited();
        _main.Status = "Undid the last change to the combos.";
    }

    // ---------------------------------------------------------------- combos

    public string ComboName
    {
        get => Combo?.Name ?? "";
        set
        {
            if (Combo is null || Combo.Name == value) return;
            Remember("name");
            Combo.Name = value;
            Selected!.Refresh();
            OnPropertyChanged();
            Changed();
        }
    }

    public string ComboNotes
    {
        get => Combo?.Notes ?? "";
        set
        {
            if (Combo is null || Combo.Notes == value) return;
            Remember("notes");
            Combo.Notes = value;
            OnPropertyChanged();
            Changed();
        }
    }

    private void AddCombo(Combo combo, string status)
    {
        Remember();
        Pack.Combos.Add(combo);
        var item = new ComboListItem(combo);
        Items.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
        Selected = item;
        Changed();
        _main.Status = status;
    }

    [RelayCommand]
    private void NewCombo()
    {
        // A new combo starts with the deck of the one open, so another line for the same deck is one click away.
        var combo = new Combo { Name = $"Combo {Pack.Combos.Count + 1}" };
        if (Combo is { } current)
        {
            combo.Deck = PackSerializer.Clone(current.Deck);
            combo.Names = new Dictionary<long, string>(current.Names);
        }
        AddCombo(combo, combo.Deck.IsEmpty
            ? "Made a new combo. Import a deck on \"Deck & hand\", pick the opening hand, then add the steps."
            : "Made a new combo with the same deck. Pick the opening hand, then add the steps.");
        Mode = ComboMode.Deck;
    }

    [RelayCommand]
    private void DuplicateCombo()
    {
        if (Combo is not { } current) return;
        var copy = PackSerializer.Clone(current);
        copy.Name = current.Name + " (copy)";
        AddCombo(copy, $"Copied \"{current.Name}\".");
    }

    [RelayCommand]
    private async Task DeleteCombo()
    {
        if (Selected is not { } item) return;
        if (item.Model.Steps.Count > 0 && !await _dialogs.ConfirmAsync("Delete combo", $"Delete \"{item.Name}\" and its {item.Model.Steps.Count} steps? Undo brings it back.", "Delete", "Keep"))
            return;
        Remember();
        var index = Items.IndexOf(item);
        Pack.Combos.Remove(item.Model);
        Items.Remove(item);
        OnPropertyChanged(nameof(IsEmpty));
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        if (Selected is null) RefreshAll(-1);
        Changed();
        _main.Status = $"Deleted \"{item.Name}\".";
    }

    [RelayCommand]
    private async Task ImportComboFile()
    {
        if (await _dialogs.OpenFileAsync("Import a combo", "Card Crafter combo", "yccombo") is not { } path) return;
        ImportComboFrom(path);
    }

    public bool ImportComboFrom(string path)
    {
        try
        {
            var combo = ComboFile.Load(path);
            AddCombo(combo, $"Imported \"{combo.Name}\" from {Path.GetFileName(path)}.");
            Mode = ComboMode.Play;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _main.Status = $"Couldn't import {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    private async Task ExportComboFile()
    {
        if (Combo is not { } combo) return;
        var path = await _dialogs.SaveFileAsync("Export this combo", Core.Omega.PackExporter.SafeFileName(combo.Name) + ComboFile.Extension, "Card Crafter combo", "yccombo");
        if (path is null) return;
        try
        {
            ComboFile.Save(combo, path, Catalog);
            _main.Status = $"Saved \"{combo.Name}\" to {Path.GetFileName(path)}. Anyone with Card Crafter can open it with Import.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.Status = $"Couldn't save the combo: {ex.Message}";
        }
    }

    public string ComboAsText => Combo is null ? "" : ComboText.ToText(Combo, Catalog);

    [RelayCommand]
    private async Task CopyAsText()
    {
        if (Combo is null) return;
        await _dialogs.CopyToClipboardAsync(ComboAsText);
        _main.Status = $"Copied \"{Combo.Name}\" as text, ready to paste into Discord.";
    }

    // ---------------------------------------------------------------- deck

    public ObservableCollection<ComboDeckEntry> MainDeck { get; } = [];
    public ObservableCollection<ComboDeckEntry> ExtraDeck { get; } = [];
    public ObservableCollection<ComboDeckEntry> SideDeck { get; } = [];
    public string MainLabel => $"Main Deck ({Combo?.Deck.Main.Count ?? 0})";
    public string ExtraLabel => $"Extra Deck ({Combo?.Deck.Extra.Count ?? 0})";
    public string SideLabel => $"Side Deck ({Combo?.Deck.Side.Count ?? 0})";
    public bool DeckIsEmpty => Combo?.Deck.IsEmpty ?? true;
    [ObservableProperty] private string _pasteText = "";
    [ObservableProperty] private string _importReport = "";
    public bool HasImportReport => ImportReport.Length > 0;
    partial void OnImportReportChanged(string value) => OnPropertyChanged(nameof(HasImportReport));
    [ObservableProperty] private string _deckSearch = "";
    public ObservableCollection<ComboCard> DeckSearchResults { get; } = [];
    partial void OnDeckSearchChanged(string value)
    {
        DeckSearchResults.Clear();
        if (value.Trim().Length < 2) return;
        foreach (var c in Catalog.Search(value, 12)) DeckSearchResults.Add(c);
    }

    private void RefreshDeck()
    {
        void Fill(ObservableCollection<ComboDeckEntry> list, IEnumerable<long> codes, string section)
        {
            list.Clear();
            foreach (var g in codes.GroupBy(c => c))
            {
                var info = Catalog.Find(g.Key);
                list.Add(new ComboDeckEntry(g.Key, Name(g.Key), g.Count(), section,
                    info is null ? Brushes.Transparent : CardTypeColors.Wash(info.Data),
                    info is null ? Brushes.Gray : CardTypeColors.Stripe(info.Data),
                    info is null ? $"{Name(g.Key)} ({g.Key}): not in this pack or YGO Omega's databases" : $"{info.Name}\n\n{info.Text}"));
            }
        }
        var deck = Combo?.Deck ?? new ComboDeck();
        Fill(MainDeck, deck.Main, "Main");
        Fill(ExtraDeck, deck.Extra, "Extra");
        Fill(SideDeck, deck.Side, "Side");
        OnPropertyChanged(nameof(MainLabel));
        OnPropertyChanged(nameof(ExtraLabel));
        OnPropertyChanged(nameof(SideLabel));
        OnPropertyChanged(nameof(DeckIsEmpty));
    }

    private async Task SetDeck(DeckImportResult result, string from)
    {
        if (Combo is not { } combo) return;
        if (result.Cards == 0)
        {
            ImportReport = result.Unknown.Count > 0 ? $"No card was found. Not found: {string.Join(", ", result.Unknown)}" : "There were no cards to import.";
            return;
        }
        if (!combo.Deck.IsEmpty && combo.Steps.Count > 0
            && !await _dialogs.ConfirmAsync("Replace the deck", $"Replace the deck of \"{combo.Name}\"? Its steps stay; steps whose cards aren't in the new deck get flagged.", "Replace", "Cancel"))
            return;
        Remember();
        combo.Deck = result.Deck;
        Changed();
        ImportReport = $"Imported {result.Cards} cards from {from}: {result.Summary}." + (result.Unknown.Count > 0 ? " Check the spelling, or add the cards to this pack." : "");
        RefreshAll(SelectedStepIndex);
        _main.Status = ImportReport;
    }

    [RelayCommand]
    private async Task ImportYdk()
    {
        if (await _dialogs.OpenFileAsync("Import a deck", "Deck file", "ydk", "txt") is not { } path) return;
        await ImportDeckFrom(path);
    }

    public async Task ImportDeckFrom(string path)
    {
        try
        {
            await SetDeck(DeckImport.Read(await File.ReadAllTextAsync(path), Catalog), Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ImportReport = $"Couldn't read {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportPasted()
    {
        if (PasteText.Trim().Length == 0) { ImportReport = "Paste a deck list in the box first."; return; }
        await SetDeck(DeckImport.Read(PasteText, Catalog), "the list");
    }

    [RelayCommand]
    private async Task UsePackCards() => await SetDeck(DeckImport.FromPack(Pack), "this pack");

    [RelayCommand]
    private async Task ExportYdk()
    {
        if (Combo is not { } combo) return;
        var path = await _dialogs.SaveFileAsync("Save the deck", Core.Omega.PackExporter.SafeFileName(combo.Name) + ".ydk", "Deck file", "ydk");
        if (path is null) return;
        try
        {
            await File.WriteAllTextAsync(path, DeckImport.ToYdk(combo.Deck));
            _main.Status = $"Saved the deck to {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.Status = $"Couldn't save the deck: {ex.Message}";
        }
    }

    private List<long>? SectionList(string section) => section switch
    {
        "Main" => Combo?.Deck.Main,
        "Extra" => Combo?.Deck.Extra,
        "Side" => Combo?.Deck.Side,
        _ => null,
    };

    [RelayCommand]
    private void AddCopy(ComboDeckEntry? entry)
    {
        if (entry is null || SectionList(entry.Section) is not { } list) return;
        Remember();
        list.Insert(list.LastIndexOf(entry.Code) + 1, entry.Code);
        Changed();
        RefreshAll(SelectedStepIndex);
    }

    [RelayCommand]
    private void RemoveCopy(ComboDeckEntry? entry)
    {
        if (entry is null || SectionList(entry.Section) is not { } list) return;
        Remember();
        list.RemoveAt(list.LastIndexOf(entry.Code));
        Changed();
        RefreshAll(SelectedStepIndex);
    }

    [RelayCommand]
    private void AddToDeck(ComboCard? card)
    {
        if (card is null || Combo is not { } combo) return;
        Remember();
        (card.Data.IsExtraDeck ? combo.Deck.Extra : combo.Deck.Main).Add(card.Id);
        Changed();
        RefreshAll(SelectedStepIndex);
        _main.Status = $"Added {card.Name} to the {(card.Data.IsExtraDeck ? "Extra" : "Main")} Deck.";
    }

    // ---------------------------------------------------------------- opening hand

    public ObservableCollection<ComboHandChip> HandChips { get; } = [];
    public string HandLabel => Combo is null ? "" : Combo.RandomHand ? $"Opening hand ({Combo.HandSize} at random)" : $"Opening hand ({Combo.StartingHand.Count})";
    public string HandProblems => _replay is null ? "" : string.Join("\n", _replay.StartProblems);
    public bool HasHandProblems => HandProblems.Length > 0;

    public bool RandomHand
    {
        get => Combo?.RandomHand ?? false;
        set
        {
            if (Combo is null || Combo.RandomHand == value) return;
            Remember();
            Combo.RandomHand = value;
            if (value && Combo.Seed == 0) Combo.Seed = Random.Shared.Next(1, int.MaxValue);
            Changed();
            RefreshAll(SelectedStepIndex);
        }
    }

    public decimal HandSize
    {
        get => Combo?.HandSize ?? 5;
        set
        {
            var n = (int)Math.Clamp(value, 1, 10);
            if (Combo is null || Combo.HandSize == n) return;
            Remember("handsize");
            Combo.HandSize = n;
            Changed();
            RefreshAll(SelectedStepIndex);
        }
    }

    private void RefreshHand()
    {
        HandChips.Clear();
        if (Combo is not { } combo || _replay is null) return;
        if (combo.RandomHand)
            foreach (var c in _replay.Start.Hand) HandChips.Add(new ComboHandChip(-1, Name(c.Code), StripeOf(c.Code), false));
        else
            for (int i = 0; i < combo.StartingHand.Count; i++)
                HandChips.Add(new ComboHandChip(i, Name(combo.StartingHand[i]), StripeOf(combo.StartingHand[i]), true));
        OnPropertyChanged(nameof(HandLabel));
        OnPropertyChanged(nameof(HandProblems));
        OnPropertyChanged(nameof(HasHandProblems));
        OnPropertyChanged(nameof(RandomHand));
        OnPropertyChanged(nameof(HandSize));
    }

    private IBrush StripeOf(long code) => Catalog.Find(code) is { } c ? CardTypeColors.Stripe(c.Data) : Brushes.Gray;

    [RelayCommand]
    private void ToHand(ComboDeckEntry? entry)
    {
        if (entry is null || Combo is not { } combo) return;
        Remember();
        combo.RandomHand = false;
        combo.StartingHand.Add(entry.Code);
        Changed();
        RefreshAll(SelectedStepIndex);
    }

    [RelayCommand]
    private void RemoveFromHand(ComboHandChip? chip)
    {
        if (chip is null || Combo is not { } combo || chip.Index < 0 || chip.Index >= combo.StartingHand.Count) return;
        Remember();
        combo.StartingHand.RemoveAt(chip.Index);
        Changed();
        RefreshAll(SelectedStepIndex);
    }

    [RelayCommand]
    private void ClearHand()
    {
        if (Combo is not { StartingHand.Count: > 0 } combo) return;
        Remember();
        combo.StartingHand.Clear();
        Changed();
        RefreshAll(SelectedStepIndex);
    }

    /// <summary>Practice: another random hand.</summary>
    [RelayCommand]
    private void Redraw()
    {
        if (Combo is not { } combo) return;
        Remember();
        combo.RandomHand = true;
        combo.Seed = Random.Shared.Next(1, int.MaxValue);
        Changed();
        RefreshAll(SelectedStepIndex);
    }

    /// <summary>Practice: keeps the random hand as the combo's fixed opening hand.</summary>
    [RelayCommand]
    private void KeepHand()
    {
        if (Combo is not { RandomHand: true } combo || _replay is null) return;
        Remember();
        combo.StartingHand = _replay.Start.Hand.Select(c => c.Code).ToList();
        combo.RandomHand = false;
        Changed();
        RefreshAll(SelectedStepIndex);
    }

    // ---------------------------------------------------------------- steps

    public ObservableCollection<ComboStepRow> StepRows { get; } = [];
    [ObservableProperty] private int _selectedStepIndex = -1;
    [ObservableProperty] private ComboStepEditorViewModel? _editor;
    [ObservableProperty] private ComboBoardViewModel? _stepsBoard;
    public bool HasSteps => StepRows.Count > 0;
    public bool HasEditor => Editor is not null;
    partial void OnEditorChanged(ComboStepEditorViewModel? value) => OnPropertyChanged(nameof(HasEditor));
    public string StepsBoardLabel => SelectedStepIndex < 0 ? "The opening hand" : $"The board after step {SelectedStepIndex + 1}";
    public string StepsSummary => Combo is null ? "" : _replay is { FlaggedCount: > 0 } r
        ? $"{Combo.Steps.Count} steps · {r.FlaggedCount} can't be played as written (⚠)"
        : $"{Combo.Steps.Count} steps";

    public IReadOnlyList<Option<ComboStepKind>> StepKinds { get; } =
        Enum.GetValues<ComboStepKind>().Select(k => new Option<ComboStepKind>(k, ComboText.Kind(k))).ToList();

    private bool _selectingStep;
    partial void OnSelectedStepIndexChanged(int value)
    {
        if (_selectingStep) return;
        _lastEdit = null;
        RefreshEditor();
        RefreshStepsBoard();
        OnPropertyChanged(nameof(StepsBoardLabel));
    }

    private void SelectStep(int index)
    {
        _selectingStep = true;
        SelectedStepIndex = Combo is null ? -1 : Math.Clamp(index, -1, Combo.Steps.Count - 1);
        _selectingStep = false;
        RefreshEditor();
        RefreshStepsBoard();
        OnPropertyChanged(nameof(StepsBoardLabel));
    }

    /// <summary>Replays the combo and shows everything again; <paramref name="selectStep"/> is the step to select afterwards.</summary>
    private void RefreshAll(int selectStep)
    {
        _replay = Combo is null ? null : ComboReplay.Run(Combo, Catalog);
        OnPropertyChanged(nameof(Combo));
        OnPropertyChanged(nameof(HasCombo));
        OnPropertyChanged(nameof(ShowDeck));
        OnPropertyChanged(nameof(ShowSteps));
        OnPropertyChanged(nameof(ShowPlay));
        OnPropertyChanged(nameof(ComboName));
        OnPropertyChanged(nameof(ComboNotes));
        RefreshDeck();
        RefreshHand();
        RefreshRows();
        SelectStep(selectStep);
        RefreshPlay();
    }

    /// <summary>After an edit inside a step: the replay, the rows and the board change, the editor stays (unless asked).</summary>
    internal void StepEdited(bool rebuildEditor)
    {
        _replay = Combo is null ? null : ComboReplay.Run(Combo, Catalog);
        Changed();
        RefreshHand();
        RefreshRows();
        if (rebuildEditor) RefreshEditor();
        else Editor?.RefreshProblems();
        RefreshStepsBoard();
        RefreshPlay();
    }

    internal void RememberStepEdit(string field) => Remember($"{field}:{SelectedStepIndex}");

    internal IReadOnlyList<string> ProblemsOf(int step) => _replay is { } r && step >= 0 && step < r.Steps.Count ? r.Steps[step].Problems : [];

    internal IEnumerable<string> MovesOf(int step) =>
        _replay is { } r && step >= 0 && step < r.Steps.Count ? r.Steps[step].Moves.Select(m => ComboText.Moved(m, Name)) : [];

    private void RefreshRows()
    {
        var steps = Combo?.Steps ?? [];
        while (StepRows.Count > steps.Count) StepRows.RemoveAt(StepRows.Count - 1);
        while (StepRows.Count < steps.Count) StepRows.Add(new ComboStepRow());
        for (int i = 0; i < steps.Count; i++)
        {
            var row = StepRows[i];
            row.Number = i + 1;
            row.Text = ComboText.Describe(steps[i], Name);
            row.Note = steps[i].Note.Trim();
            row.Problems = _replay is null ? "" : string.Join("\n", _replay.Steps[i].Problems);
        }
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(StepsSummary));
        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// True while the editor is being replaced. The view's drop-downs then rebind to the new editor and can write back
    /// a selection the user never made; the editor ignores those.
    /// </summary>
    internal bool SwitchingEditor { get; private set; }

    private void RefreshEditor()
    {
        SwitchingEditor = true;
        try
        {
            Editor = Combo is { } c && _replay is not null && SelectedStepIndex >= 0 && SelectedStepIndex < c.Steps.Count
                ? new ComboStepEditorViewModel(this, c.Steps[SelectedStepIndex], SelectedStepIndex, _replay)
                : null;
            // Show the new editor's own values in case a drop-down kept the old editor's selection.
            Editor?.Resync();
        }
        finally
        {
            SwitchingEditor = false;
        }
    }

    private void RefreshStepsBoard()
    {
        if (_replay is null || Combo is null) { StepsBoard = null; return; }
        var touched = SelectedStepIndex >= 0 && SelectedStepIndex < _replay.Steps.Count ? _replay.Steps[SelectedStepIndex].Touched : [];
        StepsBoard = new ComboBoardViewModel(_replay.BoardAfter(SelectedStepIndex + 1), Catalog, Combo.Names, touched);
    }

    /// <summary>Adds a step of that kind after the selected one.</summary>
    [RelayCommand]
    private void AddStep(Option<ComboStepKind>? kind)
    {
        if (Combo is not { } combo || kind is null) return;
        Remember();
        var at = SelectedStepIndex < 0 ? combo.Steps.Count : SelectedStepIndex + 1;
        combo.Steps.Insert(at, new ComboStep { Kind = kind.Value });
        Changed();
        RefreshAll(at);
        Mode = ComboMode.Steps;
    }

    [RelayCommand]
    private void DeleteStep()
    {
        if (Combo is not { } combo || SelectedStepIndex < 0 || SelectedStepIndex >= combo.Steps.Count) return;
        Remember();
        var at = SelectedStepIndex;
        combo.Steps.RemoveAt(at);
        Changed();
        RefreshAll(Math.Min(at, combo.Steps.Count - 1));
    }

    [RelayCommand]
    private void DuplicateStep()
    {
        if (Combo is not { } combo || SelectedStepIndex < 0 || SelectedStepIndex >= combo.Steps.Count) return;
        Remember();
        var at = SelectedStepIndex;
        combo.Steps.Insert(at + 1, PackSerializer.Clone(combo.Steps[at]));
        Changed();
        RefreshAll(at + 1);
    }

    private bool CanMoveUp() => SelectedStepIndex > 0;
    private bool CanMoveDown() => Combo is { } c && SelectedStepIndex >= 0 && SelectedStepIndex < c.Steps.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveStepUp() => MoveStep(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveStepDown() => MoveStep(1);

    private void MoveStep(int by)
    {
        if (Combo is not { } combo) return;
        var from = SelectedStepIndex;
        var to = from + by;
        if (from < 0 || to < 0 || to >= combo.Steps.Count) return;
        Remember();
        (combo.Steps[from], combo.Steps[to]) = (combo.Steps[to], combo.Steps[from]);
        Changed();
        RefreshAll(to);
    }

    // ---------------------------------------------------------------- play

    [ObservableProperty] private int _playPosition;
    [ObservableProperty] private bool _showEndBoard;
    [ObservableProperty] private ComboBoardViewModel? _playBoard;
    [ObservableProperty] private ComboCard? _playCard;
    public int PlayMax => Combo?.Steps.Count ?? 0;
    public double PlaySlider { get => PlayPosition; set => PlayPosition = (int)Math.Round(value); }
    public string PlayHeader => ShowEndBoard ? $"The end, after {PlayMax} steps" : PlayPosition == 0 ? $"Opening hand · {PlayMax} steps" : $"Step {PlayPosition} of {PlayMax}";
    public string PlayTitle { get; private set; } = "";
    public string PlayNote { get; private set; } = "";
    public bool HasPlayNote => PlayNote.Length > 0;
    public string PlayProblems { get; private set; } = "";
    public bool HasPlayProblems => PlayProblems.Length > 0;
    public IReadOnlyList<ComboEffectLine> PlayEffects { get; private set; } = [];
    public bool HasPlayEffects => PlayEffects.Count > 0;
    public IReadOnlyList<string> PlayMoves { get; private set; } = [];
    public bool HasPlayMoves => PlayMoves.Count > 0;
    public string? PlayArt => PlayCard is { } c ? _main.ArtPathFor(c.Id) : null;
    public string PlayTypeLine => PlayCard is { } c ? Core.Text.CardFaceText.TypeLine(c.Data, Pack) : "";
    public bool HasPlayCard => PlayCard is not null;
    /// <summary>The card is drawn with the card textures when they are loaded; otherwise a plain name tile stands in.</summary>
    public bool ShowPlayFace => PlayCard is not null && Services.FrameImages.Count > 0;
    public bool ShowPlayTile => PlayCard is not null && !ShowPlayFace;
    public IBrush PlayWash => PlayCard is { } c ? CardTypeColors.Wash(c.Data) : Brushes.Transparent;
    public IBrush PlayStripe => PlayCard is { } c ? CardTypeColors.Stripe(c.Data) : Brushes.Transparent;
    public string PlayCardLine => PlayCard is not { } c ? ""
        : c.Data.IsMonster ? $"{PlayTypeLine}  ·  {(c.Data.IsLink ? $"LINK-{c.Data.LinkRating}" : $"ATK {Stat(c.Data.Atk)} / DEF {Stat(c.Data.Def)}")}"
        : $"{Core.Text.CardFaceText.SpellTrapLine(c.Data).Label}{(Core.Text.CardFaceText.SpellTrapLine(c.Data).Kind is { Length: > 0 } k ? " · " + k : "")}";
    private static string Stat(int v) => Core.Text.CardFaceText.Stat(v);
    public IReadOnlyList<ComboEndGroup> EndGroups { get; private set; } = [];
    public bool ShowStepDetail => !ShowEndBoard;

    partial void OnPlayPositionChanged(int value)
    {
        if (value < 0 || value > PlayMax) { PlayPosition = Math.Clamp(value, 0, PlayMax); return; }
        RefreshPlay();
    }

    partial void OnShowEndBoardChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStepDetail));
        RefreshPlay();
    }

    private bool CanGoBack() => PlayPosition > 0;
    private bool CanGoOn() => PlayPosition < PlayMax;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PlayFirst() { ShowEndBoard = false; PlayPosition = 0; }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PlayPrevious() { ShowEndBoard = false; PlayPosition--; }

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void PlayNext() => PlayPosition++;

    [RelayCommand(CanExecute = nameof(CanGoOn))]
    private void PlayLast() => PlayPosition = PlayMax;

    /// <summary>Opens the viewer at the selected step.</summary>
    [RelayCommand]
    private void PlayFromHere()
    {
        PlayPosition = Math.Max(0, SelectedStepIndex + 1);
        Mode = ComboMode.Play;
    }

    private void RefreshPlay()
    {
        if (PlayPosition > PlayMax) { PlayPosition = PlayMax; return; }
        PlayFirstCommand.NotifyCanExecuteChanged();
        PlayPreviousCommand.NotifyCanExecuteChanged();
        PlayNextCommand.NotifyCanExecuteChanged();
        PlayLastCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlayMax));
        OnPropertyChanged(nameof(PlaySlider));
        OnPropertyChanged(nameof(PlayHeader));
        if (_replay is null || Combo is not { } combo)
        {
            PlayBoard = null;
            PlayCard = null;
            return;
        }
        var position = ShowEndBoard ? combo.Steps.Count : PlayPosition;
        var step = position > 0 && !ShowEndBoard ? combo.Steps[position - 1] : null;
        var outcome = step is null ? null : _replay.Steps[position - 1];
        PlayBoard = new ComboBoardViewModel(_replay.BoardAfter(position), Catalog, combo.Names, outcome?.Touched ?? []);
        if (step is null)
        {
            PlayTitle = "Opening hand: " + (_replay.Start.Hand.Count > 0 ? string.Join(", ", _replay.Start.Hand.Select(c => Name(c.Code))) : "(none)");
            PlayNote = combo.Notes.Trim();
            PlayProblems = string.Join("\n", _replay.StartProblems);
            PlayMoves = [];
            PlayCard = null;
            PlayEffects = [];
        }
        else
        {
            PlayTitle = $"{position}. {ComboText.Describe(step, Name)}";
            PlayNote = step.Note.Trim();
            PlayProblems = string.Join("\n", outcome!.Problems);
            PlayMoves = outcome.Moves.Select(m => ComboText.Moved(m, Name)).ToList();
            PlayCard = step.Code != 0 ? Catalog.Find(step.Code) : null;
            PlayEffects = PlayCard is null ? [] : CardEffects.Split(PlayCard.Text)
                .Select(p => new ComboEffectLine(p.Text, p.IsEffect ? p.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "", p.IsEffect && p.Number == step.Effect, !p.IsEffect))
                .ToList();
        }
        EndGroups = ShowEndBoard ? BuildEndGroups(_replay.End) : [];
        foreach (var p in new[] { nameof(PlayTitle), nameof(PlayNote), nameof(HasPlayNote), nameof(PlayProblems), nameof(HasPlayProblems), nameof(PlayMoves),
                     nameof(HasPlayMoves), nameof(PlayEffects), nameof(HasPlayEffects), nameof(PlayArt), nameof(PlayTypeLine), nameof(HasPlayCard),
                     nameof(ShowPlayFace), nameof(ShowPlayTile), nameof(PlayWash), nameof(PlayStripe), nameof(PlayCardLine), nameof(EndGroups), nameof(PlayHeader) })
            OnPropertyChanged(p);
    }

    /// <summary>The end board: the cards on the field, in hand, in the GY and banished, each with its text.</summary>
    private List<ComboEndGroup> BuildEndGroups(ComboBoard board)
    {
        ComboEndCard Card(BoardCard c, string detail)
        {
            var info = Catalog.Find(c.Code);
            return new ComboEndCard(Name(c.Code), detail, info?.Text ?? "",
                info is null ? Brushes.Transparent : CardTypeColors.Wash(info.Data), info is null ? Brushes.Gray : CardTypeColors.Stripe(info.Data));
        }
        string Detail(BoardSpot s)
        {
            var parts = new List<string> { ComboText.Zone(s.Zone) };
            if (s.Card.FaceDown) parts.Add("Set");
            else if (s.Card.Position == ComboPosition.Defense) parts.Add("Defense Position");
            if (s.Card.Materials.Count > 0) parts.Add("materials: " + string.Join(", ", s.Card.Materials.Select(m => Name(m.Code))));
            return string.Join(" · ", parts);
        }
        var groups = new List<ComboEndGroup>();
        var field = board.FieldSpots().Where(s => s.Zone != ComboZone.XyzMaterial).Select(s => Card(s.Card, Detail(s))).ToList();
        if (field.Count > 0) groups.Add(new ComboEndGroup($"On the field ({field.Count})", field));
        if (board.Hand.Count > 0) groups.Add(new ComboEndGroup($"In hand ({board.Hand.Count})", board.Hand.Select(c => Card(c, "")).ToList()));
        if (board.Graveyard.Count > 0) groups.Add(new ComboEndGroup($"In the GY ({board.Graveyard.Count})", board.Graveyard.AsEnumerable().Reverse().Select(c => Card(c, "")).ToList()));
        if (board.Banished.Count > 0) groups.Add(new ComboEndGroup($"Banished ({board.Banished.Count})", board.Banished.Select(c => Card(c, c.FaceDown ? "face-down" : "")).ToList()));
        if (groups.Count == 0) groups.Add(new ComboEndGroup("Nothing on the board yet", []));
        return groups;
    }
}
