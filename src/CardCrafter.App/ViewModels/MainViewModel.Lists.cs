using System.Collections.ObjectModel;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>The Lists tab: archetypes, custom Types and banlists of the pack.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<BanlistViewModel> Banlists { get; } = [];
    [ObservableProperty] private BanlistViewModel? _selectedBanlist;
    public bool HasSelectedBanlist => SelectedBanlist is not null;
    partial void OnSelectedBanlistChanged(BanlistViewModel? value) => OnPropertyChanged(nameof(HasSelectedBanlist));

    private void LoadBanlists()
    {
        Banlists.Clear();
        foreach (var b in _pack.Banlists) Banlists.Add(new BanlistViewModel(b, MarkDirty));
        SelectedBanlist = Banlists.FirstOrDefault();
    }

    private long NextBanlistId() => Math.Max(PackBanlist.FirstId, _pack.Banlists.Select(b => b.Id + 1).DefaultIfEmpty(PackBanlist.FirstId).Max());

    [RelayCommand]
    private void NewBanlist()
    {
        var list = new PackBanlist { Id = NextBanlistId(), Name = string.IsNullOrWhiteSpace(_pack.Name) ? "My banlist" : _pack.Name + " banlist" };
        AddBanlist(list);
        Status = $"Made the banlist \"{list.Name}\". Search for cards to put on it.";
    }

    private void AddBanlist(PackBanlist list)
    {
        _pack.Banlists.Add(list);
        var vm = new BanlistViewModel(list, MarkDirty);
        Banlists.Add(vm);
        SelectedBanlist = vm;
        MarkDirty();
    }

    [RelayCommand]
    private void RemoveBanlist()
    {
        if (SelectedBanlist is not { } vm) return;
        _pack.Banlists.Remove(vm.Model);
        Banlists.Remove(vm);
        SelectedBanlist = Banlists.FirstOrDefault();
        MarkDirty();
    }

    // ---- scanning Omega's banlists

    public ObservableCollection<ScannedBanlist> ScannedBanlists { get; } = [];
    [ObservableProperty] private string _banlistScanStatus = "Scan YGO Omega to see its banlists, or browse a database file.";

    /// <summary>Reads the banlists of every card database in YGO Omega.</summary>
    [RelayCommand]
    private async Task ScanOmegaBanlists()
    {
        if (Inventory is not { } inv)
        {
            BanlistScanStatus = "YGO Omega wasn't found. Use \"Scan a database file…\".";
            return;
        }
        BanlistScanStatus = "Reading Omega's banlists…";
        var found = await Task.Run(() => inv.CardDatabases.SelectMany(db =>
        {
            try { return CdbFile.ReadBanlists(db.Path).Select(l => new ScannedBanlist(l, Path.GetFileName(db.Path))); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException) { return []; }
        }).ToList());
        ShowScanned(found, "YGO Omega");
    }

    [RelayCommand]
    private async Task ScanBanlistFile()
    {
        if (await _dialogs.OpenFileAsync("Scan the banlists in a database", "Card database", "db", "cdb") is not { } path) return;
        var found = await Task.Run(() =>
        {
            try { return CdbFile.ReadBanlists(path).Select(l => new ScannedBanlist(l, Path.GetFileName(path))).ToList(); }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException) { return []; }
        });
        ShowScanned(found, Path.GetFileName(path));
    }

    private void ShowScanned(List<ScannedBanlist> found, string where)
    {
        ScannedBanlists.Clear();
        foreach (var s in found) ScannedBanlists.Add(s);
        BanlistScanStatus = found.Count == 0 ? $"{where} has no banlists." : $"{found.Count} banlist{(found.Count == 1 ? "" : "s")} in {where}. Copy one to change it.";
    }

    /// <summary>Copies a scanned banlist into the pack as a new list of its own, to edit.</summary>
    [RelayCommand]
    private void CopyBanlist(ScannedBanlist? scanned)
    {
        if (scanned is null) return;
        var list = new PackBanlist
        {
            Id = NextBanlistId(), Name = scanned.Title + " (copy)", Flags = scanned.List.Flags,
            Entries = scanned.List.Entries.Select(e => new BanlistEntry { Code = e.Code, Limit = e.Limit, Name = e.Name }).ToList(),
        };
        AddBanlist(list);
        Status = $"Copied \"{scanned.Title}\" ({list.Entries.Count} cards) into the pack as \"{list.Name}\".";
    }

    // ---- adding cards

    [ObservableProperty] private string _banlistSearch = "";
    public ObservableCollection<BanlistCandidate> BanlistCandidates { get; } = [];
    public IReadOnlyList<Option<Limitation>> BanlistLimitOptions => Lists.Limitations.Where(o => o.Value != Limitation.Unlimited).ToList();
    [ObservableProperty] private Option<Limitation>? _banlistAddLimit;
    private CancellationTokenSource? _banlistSearchCts;

    partial void OnBanlistSearchChanged(string value) => _ = SearchBanlistCandidatesAsync(value);

    private async Task SearchBanlistCandidatesAsync(string query)
    {
        _banlistSearchCts?.Cancel();
        var cts = _banlistSearchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(150, cts.Token);
            var own = _pack.Cards.Where(c => query.Length > 0 && c.Card.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(c => new BanlistCandidate(c.Card.Id, c.Card.Name, $"{c.Card.Id} · this pack")).ToList();
            var library = await GetLibraryAsync();
            var found = query.Trim().Length < 2 ? [] : await Task.Run(() => library.Search(query, SearchIn.Name, null, 40), cts.Token);
            if (cts.IsCancellationRequested) return;
            BanlistCandidates.Clear();
            foreach (var c in own.Concat(found.Select(f => new BanlistCandidate(f.Id, f.Name, $"{f.Id} · {f.TypeLine}"))).DistinctBy(c => c.Code).Take(40))
                BanlistCandidates.Add(c);
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void AddToBanlist(BanlistCandidate? candidate)
    {
        if (candidate is null) return;
        if (SelectedBanlist is null) NewBanlist();
        var limit = BanlistAddLimit?.Value ?? Limitation.Forbidden;
        SelectedBanlist!.Add(candidate.Code, candidate.Name, limit);
        Status = $"\"{candidate.Name}\" is {Lists.Limitations.First(o => o.Value == limit).Label} on \"{SelectedBanlist.Name}\".";
    }
}
