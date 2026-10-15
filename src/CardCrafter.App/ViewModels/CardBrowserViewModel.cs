using System.Collections.ObjectModel;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>
/// The card browser: searches every card in YGO Omega's databases by name and by words in its text, shows a card
/// with its script split into effects, and copies a whole card or single effects into the pack.
/// </summary>
public sealed partial class CardBrowserViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CardLibrary _library = CardLibrary.Empty;
    private CancellationTokenSource? _search;
    public const int MaxResults = 300;

    public CardBrowserViewModel(MainViewModel main) => _main = main;

    public IReadOnlyList<Option<SearchIn>> SearchInOptions { get; } = Options.Of(
        (Core.Omega.SearchIn.NameAndText, "Name and text"), (Core.Omega.SearchIn.Name, "Name only"), (Core.Omega.SearchIn.Text, "Card text only"));
    public IReadOnlyList<Option<CardKind?>> KindOptions { get; } = Options.Of<CardKind?>(
        (null, "All cards"), (CardKind.Monster, "Monsters"), (CardKind.Spell, "Spells"), (CardKind.Trap, "Traps"));

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private Option<SearchIn>? _searchWhere;
    [ObservableProperty] private Option<CardKind?>? _kind;
    [ObservableProperty] private string _resultSummary = "Loading cards…";
    [ObservableProperty] private bool _isLoading = true;
    public ObservableCollection<LibraryCard> Results { get; } = [];

    [ObservableProperty] private LibraryCard? _selected;
    [ObservableProperty] private string _scriptStatus = "";
    [ObservableProperty] private string? _script;
    [ObservableProperty] private string? _scriptSource;
    public ObservableCollection<ScriptPiece> Pieces { get; } = [];

    public bool HasSelection => Selected is not null;
    public bool HasScript => Script is not null;
    public bool CanFetchScript => Selected is not null && Script is null;
    public bool HasCurrentCard => _main.SelectedCard is not null;
    public string CurrentCardName => _main.SelectedCard?.Name is { Length: > 0 } n ? $"\"{n}\"" : "the selected card";

    partial void OnSearchWhereChanged(Option<SearchIn>? value) => _ = RunSearchAsync();
    partial void OnKindChanged(Option<CardKind?>? value) => _ = RunSearchAsync();
    partial void OnQueryChanged(string value) => _ = RunSearchAsync(debounce: true);
    partial void OnSelectedChanged(LibraryCard? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        LoadScript();
    }
    partial void OnScriptChanged(string? value)
    {
        OnPropertyChanged(nameof(HasScript));
        OnPropertyChanged(nameof(CanFetchScript));
    }

    /// <summary>Loads Omega's cards (in the background) and shows the first results.</summary>
    public async Task LoadAsync()
    {
        SearchWhere ??= SearchInOptions[0];
        Kind ??= KindOptions[0];
        IsLoading = true;
        _library = await _main.GetLibraryAsync();
        IsLoading = false;
        await RunSearchAsync();
    }

    /// <summary>Uses a database file instead of Omega's, e.g. when Omega isn't installed here.</summary>
    [RelayCommand]
    private async Task BrowseDatabase()
    {
        if (await _main.PickDatabaseToBrowseAsync() is not { } path) return;
        IsLoading = true;
        _library = await Task.Run(() => CardLibrary.Load([path]));
        IsLoading = false;
        await RunSearchAsync();
    }

    private async Task RunSearchAsync(bool debounce = false)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        try
        {
            if (debounce) await Task.Delay(200, cts.Token);
            var (query, where, kind, library) = (Query, SearchWhere?.Value ?? Core.Omega.SearchIn.NameAndText, Kind?.Value, _library);
            var found = await Task.Run(() => library.Search(query, where, kind), cts.Token);
            if (cts.IsCancellationRequested) return;
            Results.Clear();
            foreach (var c in found.Take(MaxResults)) Results.Add(c);
            ResultSummary = library.Cards.Count == 0
                ? (_main.OmegaFound ? "No cards found in Omega's databases." : "YGO Omega wasn't found. Use \"Browse a database file…\".")
                : found.Count > MaxResults ? $"Showing the first {MaxResults} of {found.Count:N0} cards. Add words to narrow it down."
                : $"{found.Count:N0} card{(found.Count == 1 ? "" : "s")} of {library.Cards.Count:N0}";
        }
        catch (OperationCanceledException) { }
    }

    private void LoadScript()
    {
        Pieces.Clear();
        Script = null;
        ScriptSource = null;
        if (Selected is not { } card) { ScriptStatus = ""; return; }
        if (CardLibrary.FindScript(card, _main.ScriptFolders) is { } found)
        {
            // Shown relative to the Omega folder when it is inside it.
            var from = _main.OmegaPath is { } root && Path.IsPathRooted(found.From) && Path.GetFullPath(found.From).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(root, found.From) : found.From;
            ShowScript(found.Lua, from);
        }
        else
            ScriptStatus = "No script for this card was found in Omega's folders.";
    }

    private void ShowScript(string lua, string from)
    {
        Script = lua;
        ScriptSource = from;
        foreach (var piece in LuaPieces.Split(lua, Selected!.Id, Selected.Row.Strings)) Pieces.Add(piece);
        ScriptStatus = Pieces.Count == 0
            ? $"Script from {from}. It couldn't be split into effects, but \"Use as a new card\" copies it whole."
            : $"Script from {from}: {Pieces.Count} part{(Pieces.Count == 1 ? "" : "s")}.";
    }

    /// <summary>Gets the official script from the ygopro-scripts repository on GitHub when Omega's folders don't have it.</summary>
    [RelayCommand]
    private async Task FetchScript()
    {
        if (Selected is not { } card) return;
        ScriptStatus = "Downloading the script…";
        var lua = await _main.DownloadOfficialScriptAsync(card.Id);
        if (Selected != card) return;
        if (lua is null) ScriptStatus = "ygopro-scripts has no script for this card, or GitHub couldn't be reached.";
        else ShowScript(lua, "github.com/Fluorohydride/ygopro-scripts");
    }

    [RelayCommand]
    private void UseAsNewCard()
    {
        if (Selected is { } card) _main.AddCardFromLibrary(card, Script);
        OnPropertyChanged(nameof(HasCurrentCard));
        OnPropertyChanged(nameof(CurrentCardName));
    }

    [RelayCommand]
    private void AddPiece(ScriptPiece piece) => _main.AddScriptPieceToSelected(piece);

    [RelayCommand]
    private void CopyText()
    {
        if (Selected is { } card) _main.AddTextToSelected(card.Text);
    }
}
