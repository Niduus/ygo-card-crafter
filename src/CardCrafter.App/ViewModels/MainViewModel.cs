using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CardCrafter.App.Services;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CardCrafter.Core.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

public sealed partial class ArchetypeViewModel(Archetype model, Action changed) : ObservableObject
{
    public Archetype Model { get; } = model;
    public string Name { get => Model.Name; set { if (Model.Name == value) return; Model.Name = value; OnPropertyChanged(); changed(); } }
    public string Code
    {
        get => $"0x{Model.Code:x}";
        set
        {
            var s = value.Trim().ToLowerInvariant().Replace("0x", "");
            if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v is > 0 and <= 0xffff) Model.Code = v;
            OnPropertyChanged();
            changed();
        }
    }
}

/// <summary>The whole window: the open pack, its cards, saving, exporting and the Omega connection.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings;
    private CardPack _pack = new();

    public MainViewModel(IDialogService dialogs, AppSettings? settings = null)
    {
        _dialogs = dialogs;
        _settings = settings ?? AppSettings.Load();
        LoadPack(StarterPack(), null);
        IsDirty = false;
    }

    public CardPack Pack => _pack;
    public ObservableCollection<CardViewModel> Cards { get; } = [];
    public ObservableCollection<ArchetypeViewModel> Archetypes { get; } = [];

    [ObservableProperty] private CardViewModel? _selectedCard;
    [ObservableProperty] private string? _packPath;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _status = "Ready.";

    public string Title => $"{(IsDirty ? "• " : "")}{_pack.Name} — YGO Card Crafter";
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(Title));
    public bool HasSelection => SelectedCard is not null;
    partial void OnSelectedCardChanged(CardViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    public string PackName
    {
        get => _pack.Name;
        set { if (_pack.Name == value) return; _pack.Name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); MarkDirty(); }
    }

    public string PackAuthor
    {
        get => _pack.Author;
        set { if (_pack.Author == value) return; _pack.Author = value; OnPropertyChanged(); MarkDirty(); }
    }

    private void MarkDirty() => IsDirty = true;

    // ---------------------------------------------------------------- packs

    public static CardPack StarterPack()
    {
        var pack = new CardPack { Name = "My Custom Cards" };
        var card = new CardProject
        {
            Card = new CardData { Id = 100000001, Name = "My First Card", Attribute = CardAttribute.Light, Race = CardRace.Warrior, Level = 4, Atk = 1600, Def = 1200 },
        };
        card.Effects.Add(EffectTemplates.All.First(t => t.Name == "Search on summon").Create());
        card.Card.Text = Core.Text.EffectTextGenerator.Generate(card, pack);
        pack.Cards.Add(card);
        return pack;
    }

    private void LoadPack(CardPack pack, string? path)
    {
        _pack = pack;
        PackPath = path;
        Cards.Clear();
        foreach (var c in pack.Cards) Cards.Add(NewCardViewModel(c));
        Archetypes.Clear();
        foreach (var a in pack.Archetypes) Archetypes.Add(new ArchetypeViewModel(a, OnArchetypesChanged));
        SelectedCard = Cards.FirstOrDefault();
        IsDirty = false;
        OnPropertyChanged(nameof(PackName));
        OnPropertyChanged(nameof(PackAuthor));
        OnPropertyChanged(nameof(Title));
    }

    private CardViewModel NewCardViewModel(CardProject p) =>
        new(p, () => _pack, MarkDirty) { PackFolder = PackPath is null ? null : Path.GetDirectoryName(PackPath) };

    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsDirty) return true;
        return await _dialogs.ConfirmAsync("Unsaved changes", $"\"{_pack.Name}\" has unsaved changes. Discard them?", "Discard", "Keep editing");
    }

    [RelayCommand]
    private async Task NewPack()
    {
        if (!await ConfirmDiscardAsync()) return;
        LoadPack(StarterPack(), null);
        Status = "Started a new pack.";
    }

    [RelayCommand]
    private async Task OpenPack()
    {
        if (!await ConfirmDiscardAsync()) return;
        var path = await _dialogs.OpenFileAsync("Open a card pack", "Card Crafter pack", "ycc");
        if (path is not null) OpenPath(path);
    }

    public bool OpenPath(string path)
    {
        try
        {
            LoadPack(PackSerializer.Load(path), path);
            _settings.AddRecent(path);
            _settings.Save();
            Status = $"Opened {Path.GetFileName(path)}.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            Status = $"Couldn't open {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    public async Task<bool> SavePack()
    {
        if (PackPath is null) return await SavePackAs();
        return SaveTo(PackPath);
    }

    [RelayCommand]
    public async Task<bool> SavePackAs()
    {
        var path = await _dialogs.SaveFileAsync("Save card pack", PackExporter.SafeFileName(_pack.Name) + PackSerializer.Extension, "Card Crafter pack", "ycc");
        return path is not null && SaveTo(path);
    }

    private bool SaveTo(string path)
    {
        try
        {
            PackSerializer.Save(_pack, path);
            PackPath = path;
            foreach (var c in Cards) c.PackFolder = Path.GetDirectoryName(path);
            IsDirty = false;
            _settings.AddRecent(path);
            _settings.Save();
            Status = $"Saved {Path.GetFileName(path)}.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't save: {ex.Message}";
            return false;
        }
    }

    // ---------------------------------------------------------------- cards

    private long NextId()
    {
        var max = _pack.Cards.Select(c => c.Card.Id).DefaultIfEmpty(100000000).Max();
        return Math.Max(max + 1, 100000001);
    }

    [RelayCommand]
    private void AddCard(string kind)
    {
        var data = new CardData { Id = NextId(), Name = "New Card" };
        switch (kind)
        {
            case "Spell": data.Kind = CardKind.Spell; data.Name = "New Spell"; break;
            case "Trap": data.Kind = CardKind.Trap; data.Name = "New Trap"; break;
            default: data.Name = "New Monster"; break;
        }
        var project = new CardProject { Card = data };
        _pack.Cards.Add(project);
        var vm = NewCardViewModel(project);
        Cards.Add(vm);
        SelectedCard = vm;
        MarkDirty();
        Status = $"Added {data.Name} with passcode {data.Id}.";
    }

    [RelayCommand]
    private void DuplicateCard()
    {
        if (SelectedCard is null) return;
        var copy = PackSerializer.Clone(SelectedCard.Project);
        copy.Card.Id = NextId();
        copy.Card.Name += " (copy)";
        _pack.Cards.Insert(_pack.Cards.IndexOf(SelectedCard.Project) + 1, copy);
        var vm = NewCardViewModel(copy);
        Cards.Insert(Cards.IndexOf(SelectedCard) + 1, vm);
        SelectedCard = vm;
        MarkDirty();
    }

    [RelayCommand]
    private async Task DeleteCard()
    {
        if (SelectedCard is null) return;
        var card = SelectedCard;
        if (!await _dialogs.ConfirmAsync("Delete card", $"Delete \"{card.ListLabel}\" from this pack?", "Delete")) return;
        var i = Cards.IndexOf(card);
        _pack.Cards.Remove(card.Project);
        Cards.Remove(card);
        SelectedCard = Cards.Count == 0 ? null : Cards[Math.Min(i, Cards.Count - 1)];
        MarkDirty();
    }

    /// <summary>Imports cards from an existing .cdb. Scripts found next to it come in as hand-written Lua.</summary>
    [RelayCommand]
    private async Task ImportCdb()
    {
        var path = await _dialogs.OpenFileAsync("Import cards from a database", "Card database", "cdb");
        if (path is null) return;
        try
        {
            var rows = CdbFile.Read(path);
            var scriptDir = Path.Combine(Path.GetDirectoryName(path)!, "script");
            int added = 0, scripts = 0;
            foreach (var row in rows)
            {
                if (_pack.Cards.Any(c => c.Card.Id == row.Id)) continue;
                var project = new CardProject { Card = CdbEncoder.Decode(row) };
                var lua = Path.Combine(scriptDir, $"c{row.Id}.lua");
                if (File.Exists(lua))
                {
                    project.UseCustomLua = true;
                    project.CustomLua = await File.ReadAllTextAsync(lua);
                    scripts++;
                }
                _pack.Cards.Add(project);
                Cards.Add(NewCardViewModel(project));
                added++;
            }
            MarkDirty();
            Status = $"Imported {added} cards ({scripts} with scripts). Cards already in the pack were skipped.";
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            Status = $"Couldn't import: {ex.Message}";
        }
    }

    public void AddTemplate(EffectTemplate template)
    {
        if (SelectedCard is null) return;
        SelectedCard.AddTemplate(template);
        Status = SelectedCard.Project.Card.Kind == template.For
            ? $"Added \"{template.Name}\". Change any block to make it your own."
            : $"Added \"{template.Name}\". It is meant for {template.For} cards, so check the warnings.";
    }

    // ---------------------------------------------------------------- archetypes

    [RelayCommand]
    private void AddArchetype()
    {
        var used = _pack.Archetypes.Select(a => a.Code).ToHashSet();
        var code = 0xa00;
        while (used.Contains(code)) code++;
        var a = new Archetype { Code = code, Name = "New Archetype" };
        _pack.Archetypes.Add(a);
        Archetypes.Add(new ArchetypeViewModel(a, OnArchetypesChanged));
        OnArchetypesChanged();
    }

    [RelayCommand]
    private void RemoveArchetype(ArchetypeViewModel? vm)
    {
        if (vm is null) return;
        _pack.Archetypes.Remove(vm.Model);
        Archetypes.Remove(vm);
        OnArchetypesChanged();
    }

    private void OnArchetypesChanged()
    {
        MarkDirty();
        foreach (var c in Cards)
        {
            c.RebuildArchetypes();
            c.Refresh();
        }
    }

    // ---------------------------------------------------------------- Omega

    [ObservableProperty] private string? _omegaPath;
    [ObservableProperty] private string _omegaStatus = "Looking for YGO Omega…";
    [ObservableProperty] private bool _omegaFound;
    [ObservableProperty] private OmegaInventory? _inventory;
    [ObservableProperty] private string _scanReport = "";

    public bool CanInstall => OmegaFound && Inventory?.SupportsLooseFiles == true;
    partial void OnInventoryChanged(OmegaInventory? value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnOmegaFoundChanged(bool value) => OnPropertyChanged(nameof(CanInstall));

    /// <summary>Finds Omega on its own: saved setting, registry, standard folders, then a drive search.</summary>
    [RelayCommand]
    public async Task DetectOmega()
    {
        OmegaStatus = "Looking for YGO Omega…";
        var saved = _settings.OmegaPath;
        var found = await Task.Run(() => OmegaLocator.Find(saved));
        if (found is null)
        {
            OmegaFound = false;
            OmegaPath = null;
            Inventory = null;
            OmegaStatus = "YGO Omega not found. Use \"Browse…\" to point to its folder.";
            return;
        }
        await UseOmegaFolder(found.Root, $"found via {found.FoundBy}");
    }

    [RelayCommand]
    private async Task BrowseOmega()
    {
        var folder = await _dialogs.PickFolderAsync("Select your YGO Omega folder");
        if (folder is null) return;
        if (!OmegaLocator.LooksLikeOmega(folder))
        {
            if (!await _dialogs.ConfirmAsync("Not an Omega folder?", "This folder doesn't contain the YGO Omega program. Use it anyway?", "Use it")) return;
        }
        await UseOmegaFolder(folder, "chosen by you");
    }

    private async Task UseOmegaFolder(string root, string how)
    {
        OmegaPath = root;
        OmegaFound = true;
        _settings.OmegaPath = root;
        _settings.Save();
        OmegaStatus = $"YGO Omega: {root} ({how}). Scanning…";
        Inventory = await Task.Run(() => OmegaInventory.Scan(root));
        ScanReport = Inventory.Report();
        OmegaStatus = Inventory.SupportsLooseFiles
            ? $"YGO Omega: {root} — ready to install cards."
            : $"YGO Omega: {root} — cards will be exported to a folder (see the Omega tab).";
    }

    [RelayCommand]
    private async Task CopyReport()
    {
        await _dialogs.CopyToClipboardAsync(ScanReport);
        Status = "Scan report copied. Paste it into Discord or a bug report.";
    }

    // ---------------------------------------------------------------- export and install

    public string ExportFolder => Path.Combine(AppSettings.DefaultExportFolder, PackExporter.SafeFileName(_pack.Name));

    [RelayCommand]
    private async Task Export()
    {
        var result = await Task.Run(() => PackExporter.Export(_pack, ExportFolder, PackPath is null ? null : Path.GetDirectoryName(PackPath)));
        Status = Summary(result, $"Exported to {result.OutputFolder}");
        _dialogs.OpenFolder(result.OutputFolder);
    }

    [RelayCommand]
    public async Task Install()
    {
        if (Inventory is null || OmegaPath is null)
        {
            await _dialogs.ShowAsync("YGO Omega not found", "Card Crafter couldn't find YGO Omega. Use \"Browse…\" on the Omega tab first.");
            return;
        }
        var export = await Task.Run(() => PackExporter.Export(_pack, ExportFolder, PackPath is null ? null : Path.GetDirectoryName(PackPath)));
        var result = OmegaInstaller.Install(export, Inventory, AppSettings.BackupFolder);
        if (result.NeedsAdmin)
        {
            var restart = await _dialogs.ConfirmAsync("Administrator needed",
                "YGO Omega is in a protected folder (like Program Files), so Windows needs administrator permission to copy cards there.\n\n" +
                "Restart Card Crafter as administrator and install?", "Restart as admin", "Cancel");
            if (restart && await SavePack()) RestartElevated();
            return;
        }
        if (!result.Success)
        {
            await _dialogs.ShowAsync("Install didn't finish", result.Error ?? "Unknown error.");
            return;
        }
        var backups = result.BackedUp.Count > 0 ? $" Replaced files were backed up to {result.BackupFolder}." : "";
        Status = Summary(export, $"Installed into YGO Omega") + backups;
    }

    private static string Summary(ExportResult r, string what)
    {
        var skipped = r.Cards.Count(c => c.Skipped);
        return skipped == 0
            ? $"{what}: {r.Written} card scripts."
            : $"{what}: {r.Written} card scripts. {skipped} card(s) have errors and were not written; fix them on the Script tab.";
    }

    private void RestartElevated()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is null || PackPath is null) return;
        var psi = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas" };
        psi.ArgumentList.Add(PackPath);
        psi.ArgumentList.Add("--install");
        try
        {
            Process.Start(psi);
            RequestClose?.Invoke();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Status = "Administrator permission was not given, so nothing was installed.";
        }
    }

    /// <summary>Raised when the app should close, e.g. after restarting as administrator.</summary>
    public event Action? RequestClose;

    /// <summary>Handles command-line arguments: a pack to open, and --install to install it straight away.</summary>
    public async Task StartAsync(string[] args)
    {
        var path = args.FirstOrDefault(a => a.EndsWith(PackSerializer.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        path ??= _settings.LastPackPath is { } last && File.Exists(last) ? last : null;
        if (path is not null) OpenPath(path);
        await DetectOmega();
        if (args.Contains("--install") && CanInstall) await Install();
    }

    public void OpenExportFolder() => _dialogs.OpenFolder(ExportFolder);
}
