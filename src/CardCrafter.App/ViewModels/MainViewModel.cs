using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CardCrafter.App.Services;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CardCrafter.Core.Templates;
using CardCrafter.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

public sealed partial class CustomTypeViewModel(CustomType model, Action changed) : ObservableObject
{
    public CustomType Model { get; } = model;
    public string Name { get => Model.Name; set { if (Model.Name == value) return; Model.Name = value; OnPropertyChanged(); changed(); } }
    public string Value
    {
        get => $"0x{Model.Value:x}";
        set
        {
            var s = value.Trim().ToLowerInvariant().Replace("0x", "");
            if (long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v > 0 && v != Model.Value)
            {
                Model.Value = v;
                changed();
            }
            OnPropertyChanged();
        }
    }
}

public sealed partial class ArchetypeViewModel(Archetype model, Action changed) : ObservableObject
{
    [ObservableProperty] private string _warning = "";
    public bool HasWarning => Warning.Length > 0;
    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    public Archetype Model { get; } = model;
    public string Name { get => Model.Name; set { if (Model.Name == value) return; Model.Name = value; OnPropertyChanged(); changed(); } }
    public string Code
    {
        get => $"0x{Model.Code:x}";
        set
        {
            var s = value.Trim().ToLowerInvariant().Replace("0x", "");
            if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v is > 0 and <= 0xffff && v != Model.Code)
            {
                Model.Code = v;
                changed();
            }
            OnPropertyChanged();
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
    public ObservableCollection<CustomTypeViewModel> CustomTypes { get; } = [];

    /// <summary>Everything already in Omega: passcodes in use, archetypes, custom Types. Empty until Omega is scanned.</summary>
    public OmegaCatalog Catalog { get; private set; } = OmegaCatalog.Empty;

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
            Card = new CardData { Id = PasscodeGenerator.Generate([]), Name = "My First Card", Attribute = CardAttribute.Light, Race = CardRace.Warrior, Level = 4, Atk = 1600, Def = 1200 },
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
        CustomTypes.Clear();
        foreach (var t in pack.CustomTypes) CustomTypes.Add(new CustomTypeViewModel(t, OnTypesChanged));
        RefreshCatalogLists();
        SelectedCard = Cards.FirstOrDefault();
        IsDirty = false;
        OnPropertyChanged(nameof(PackName));
        OnPropertyChanged(nameof(PackAuthor));
        OnPropertyChanged(nameof(Title));
    }

    private CardViewModel NewCardViewModel(CardProject p) =>
        new(p, () => _pack, MarkDirty, () => Catalog, NewPasscode) { PackFolder = PackPath is null ? null : Path.GetDirectoryName(PackPath) };

    /// <summary>A free passcode: not in this pack (except the card asking) and not anywhere in Omega.</summary>
    public long NewPasscode(long except = 0) =>
        PasscodeGenerator.Generate(_pack.Cards.Select(c => c.Card.Id).Where(id => id != except).Concat(Catalog.Passcodes.Keys));

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

    private long NextId() => NewPasscode();

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
        var code = Catalog.FreeArchetypeCode(_pack.Archetypes.Select(a => a.Code));
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

    // ---- archetypes already in Omega
    [ObservableProperty] private string _archetypeSearch = "";
    partial void OnArchetypeSearchChanged(string value) => RefreshCatalogLists();
    public ObservableCollection<KnownArchetype> KnownArchetypes { get; } = [];
    public bool HasCatalog => Catalog.Passcodes.Count > 0;

    [RelayCommand]
    private void UseKnownArchetype(KnownArchetype? known)
    {
        if (known is null) return;
        if (_pack.Archetypes.Any(a => a.Code == known.Code))
        {
            Status = $"\"{known.Name}\" is already in this pack.";
            return;
        }
        var a = new Archetype { Code = known.Code, Name = known.Name };
        _pack.Archetypes.Add(a);
        Archetypes.Add(new ArchetypeViewModel(a, OnArchetypesChanged));
        OnArchetypesChanged();
        Status = $"Added the \"{known.Name}\" archetype (0x{known.Code:x}). Your cards can now belong to it and search for it.";
    }

    // ---- custom Types
    public ObservableCollection<UnknownType> FoundTypes { get; } = [];
    public bool HasFoundTypes => FoundTypes.Count > 0;

    [RelayCommand]
    private void AddCustomType()
    {
        var taken = _pack.CustomTypes.Select(t => t.Value).Concat(Catalog.UnknownTypes.Select(t => t.Value)).ToHashSet();
        long value = 0x4000000;
        while (taken.Contains(value) || (value & EnumNames.StandardRaceMask) != 0) value <<= 1;
        AddType(new CustomType { Value = value, Name = "New Type" });
    }

    [RelayCommand]
    private void UseFoundType(UnknownType? found)
    {
        if (found is null) return;
        AddType(new CustomType { Value = found.Value, Name = found.LikelyName ?? $"Type 0x{found.Value:x}" });
        Status = $"Added Omega's Type 0x{found.Value:x} as \"{found.LikelyName ?? "Type"}\". Check the name against cards that use it: {string.Join(", ", found.Examples)}.";
    }

    private void AddType(CustomType t)
    {
        _pack.CustomTypes.Add(t);
        CustomTypes.Add(new CustomTypeViewModel(t, OnTypesChanged));
        OnTypesChanged();
    }

    [RelayCommand]
    private void RemoveCustomType(CustomTypeViewModel? vm)
    {
        if (vm is null) return;
        _pack.CustomTypes.Remove(vm.Model);
        CustomTypes.Remove(vm);
        OnTypesChanged();
    }

    private void OnTypesChanged()
    {
        MarkDirty();
        RefreshCatalogLists();
        foreach (var c in Cards)
        {
            c.RebuildTypes();
            c.Refresh();
        }
    }

    private void RefreshCatalogLists()
    {
        KnownArchetypes.Clear();
        var q = ArchetypeSearch.Trim();
        foreach (var k in Catalog.Archetypes.Where(k => q.Length == 0 || k.Label.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(200))
            KnownArchetypes.Add(k);
        FoundTypes.Clear();
        foreach (var t in Catalog.UnknownTypes.Where(t => _pack.CustomTypes.All(c => c.Value != t.Value))) FoundTypes.Add(t);
        OnPropertyChanged(nameof(HasFoundTypes));
        OnPropertyChanged(nameof(HasCatalog));
        foreach (var a in Archetypes)
        {
            var other = Catalog.Archetypes.FirstOrDefault(k => k.Code == a.Model.Code);
            a.Warning = other is not null && !string.Equals(other.Name, a.Model.Name, StringComparison.OrdinalIgnoreCase)
                ? $"Omega already uses 0x{a.Model.Code:x} for \"{other.Name}\" ({other.Cards} cards). Use a different code unless you mean that archetype."
                : "";
        }
    }

    private void OnArchetypesChanged()
    {
        MarkDirty();
        RefreshCatalogLists();
        foreach (var c in Cards)
        {
            c.RebuildArchetypes();
            c.Refresh();
        }
    }

    // ---------------------------------------------------------------- updates

    [ObservableProperty] private ReleaseInfo? _availableUpdate;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private double _updateProgress;
    public bool ShowUpdateBanner => AvailableUpdate is not null;
    public string UpdateBannerText => AvailableUpdate is { } r
        ? $"YGO Card Crafter {r.Version} is available (you have {UpdateChecker.CurrentVersion})."
        : "";
    public string VersionText => $"v{UpdateChecker.CurrentVersion}";
    partial void OnAvailableUpdateChanged(ReleaseInfo? value)
    {
        OnPropertyChanged(nameof(ShowUpdateBanner));
        OnPropertyChanged(nameof(UpdateBannerText));
    }

    private string UpdateRepository => _settings.UpdateRepository ?? UpdateChecker.DefaultRepository;

    /// <summary>
    /// The installed exe when running as the published single file; null otherwise (run from source, or hosted by
    /// another program such as a test runner). Only a file named exactly like the release asset is ever replaced.
    /// </summary>
    private static string? InstalledExe =>
        OperatingSystem.IsWindows() && Environment.ProcessPath is { } p
        && Path.GetFileName(p).Equals(UpdateChecker.AssetName, StringComparison.OrdinalIgnoreCase) ? p : null;

    /// <summary>Looks for a newer release. Quiet at startup; tells the user the result when they asked.</summary>
    public async Task CheckForUpdatesAsync(bool userAsked)
    {
        try
        {
            using var http = UpdateChecker.CreateClient();
            var latest = await UpdateChecker.GetLatestAsync(http, UpdateRepository);
            if (latest is null)
            {
                if (userAsked)
                    Status = $"No releases found at github.com/{UpdateRepository}. Updates need a public repository with a published release.";
                return;
            }
            if (!UpdateChecker.IsNewer(latest.Version, UpdateChecker.CurrentVersion))
            {
                if (userAsked) Status = $"You have the latest version ({UpdateChecker.CurrentVersion}).";
                return;
            }
            if (!userAsked && _settings.SkippedVersion == latest.Version.ToString()) return;
            AvailableUpdate = latest;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (userAsked) Status = $"Couldn't check for updates: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task CheckForUpdates() => CheckForUpdatesAsync(userAsked: true);

    [RelayCommand]
    private void UpdateLater()
    {
        if (AvailableUpdate is { } r)
        {
            _settings.SkippedVersion = r.Version.ToString();
            _settings.Save();
        }
        AvailableUpdate = null;
    }

    [RelayCommand]
    private void WhatsNew()
    {
        if (AvailableUpdate is { PageUrl.Length: > 0 } r) _dialogs.OpenUrl(r.PageUrl);
    }

    /// <summary>Downloads the new exe, swaps it in for the running one, and restarts into it.</summary>
    [RelayCommand]
    private async Task UpdateNow()
    {
        if (AvailableUpdate is not { } release || IsUpdating) return;
        var exe = InstalledExe;
        if (exe is null)
        {
            await _dialogs.ShowAsync("Update", "Updating in place only works in the Windows exe. Opening the download page instead.");
            _dialogs.OpenUrl(release.PageUrl);
            return;
        }
        if (IsDirty)
        {
            var save = await _dialogs.ConfirmAsync("Save first?", $"Save \"{_pack.Name}\" before updating? Card Crafter restarts after the update.", "Save", "Cancel");
            if (!save || !await SavePack()) return;
        }

        IsUpdating = true;
        try
        {
            using var http = UpdateChecker.CreateClient();
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p * 100;
                Status = $"Downloading {release.Version}… {p:P0}";
            });
            var download = await UpdateChecker.DownloadAsync(http, release, Path.Combine(AppSettings.Folder, "Updates"), progress);
            try
            {
                SelfUpdater.Swap(exe, download);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                await _dialogs.ShowAsync("Couldn't replace the program",
                    $"Windows didn't allow replacing {exe} ({ex.Message}). This usually means Card Crafter is in a protected folder. " +
                    "The release page will open so you can download the new version yourself.");
                _dialogs.OpenUrl(release.PageUrl);
                return;
            }
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (PackPath is not null) psi.ArgumentList.Add(PackPath);
            Process.Start(psi);
            IsDirty = false;
            RequestClose?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Status = $"Update failed: {ex.Message}";
            await _dialogs.ShowAsync("Update failed", ex.Message);
        }
        finally
        {
            IsUpdating = false;
        }
    }

    // ---------------------------------------------------------------- saving card images

    public IReadOnlyList<Services.ImageSize> ImageSizes => Services.ImageSize.Presets;
    [ObservableProperty] private Services.ImageSize _selectedImageSize = Services.ImageSize.Presets[2];
    [ObservableProperty] private decimal? _customImageWidth = 1000;
    public bool IsCustomImageSize => SelectedImageSize.Width == 0;
    partial void OnSelectedImageSizeChanged(Services.ImageSize value) => OnPropertyChanged(nameof(IsCustomImageSize));

    private int ImageWidth => SelectedImageSize.Width > 0 ? SelectedImageSize.Width : (int)Math.Clamp(CustomImageWidth ?? 813, 64, 8000);

    private void SaveImage(CardViewModel card, string path) =>
        Services.CardImageExporter.Save(card.Data, card.TypeLine, card.FaceText, card.ResolvedArtwork, ImageWidth, path);

    [RelayCommand]
    private async Task SaveCardImage()
    {
        if (SelectedCard is not { } card) return;
        var path = await _dialogs.SaveFileAsync("Save card image", Services.CardImageExporter.FileName(card.Data, ".png"), "PNG or JPG image", "png");
        if (path is null) return;
        try
        {
            SaveImage(card, path);
            Status = $"Saved {Path.GetFileName(path)} at {ImageWidth} px wide.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't save the image: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAllCardImages()
    {
        var folder = await _dialogs.PickFolderAsync("Save an image of every card into…");
        if (folder is null) return;
        int saved = 0;
        foreach (var card in Cards)
        {
            try
            {
                SaveImage(card, Path.Combine(folder, Services.CardImageExporter.FileName(card.Data, ".png")));
                saved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status = $"Couldn't save {card.ListLabel}: {ex.Message}";
            }
        }
        Status = $"Saved {saved} card images at {ImageWidth} px wide to {folder}.";
        _dialogs.OpenFolder(folder);
    }

    // ---------------------------------------------------------------- card frame images

    [ObservableProperty] private string _frameStatus = "Built-in frames";

    [RelayCommand]
    private async Task ChooseFrameFolder()
    {
        var folder = await _dialogs.PickFolderAsync("Choose a folder of card frame images");
        if (folder is null) return;
        var count = Services.FrameImages.Load(folder);
        if (count == 0)
        {
            await _dialogs.ShowAsync("No frame images found",
                "That folder has none of the expected images. Name them like effect.png, normal.png, synchro.png, xyz.png, link.png, " +
                "spell.png, trap.png (plus optional attribute-dark.png, level.png, rank.png...). See the README for the full list.");
            return;
        }
        _settings.FrameFolder = folder;
        _settings.Save();
        FrameStatus = $"{count} frame images from {Path.GetFileName(folder)}";
    }

    /// <summary>Downloads the card-frame textures from the OmegaCustom repository and uses them.</summary>
    [RelayCommand]
    private async Task DownloadOmegaCustomFrames()
    {
        var folder = Path.Combine(AppSettings.Folder, "Frames", "OmegaCustom");
        try
        {
            FrameStatus = "Downloading OmegaCustom textures…";
            using var http = UpdateChecker.CreateClient();
            var progress = new Progress<double>(p => FrameStatus = $"Downloading OmegaCustom textures… {p:P0}");
            var saved = await Services.FrameImages.DownloadOmegaCustomAsync(http, folder, progress);
            if (saved == 0)
            {
                FrameStatus = "Built-in frames";
                await _dialogs.ShowAsync("Download failed", $"No textures could be downloaded from github.com/{Services.FrameImages.OmegaCustomRepo}.");
                return;
            }
            var count = Services.FrameImages.Load(folder);
            _settings.FrameFolder = folder;
            _settings.Save();
            FrameStatus = $"OmegaCustom textures ({count})";
            Status = $"Using {count} card textures from github.com/{Services.FrameImages.OmegaCustomRepo}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            FrameStatus = Services.FrameImages.Count > 0 ? FrameStatus : "Built-in frames";
            await _dialogs.ShowAsync("Download failed", ex.Message);
        }
    }

    [RelayCommand]
    private void UseBuiltInFrames()
    {
        Services.FrameImages.Load(null);
        _settings.FrameFolder = null;
        _settings.Save();
        FrameStatus = "Built-in frames";
    }

    private void LoadSavedFrames()
    {
        if (_settings.FrameFolder is not { } folder) return;
        var count = Services.FrameImages.Load(folder);
        FrameStatus = count > 0 ? $"{count} frame images from {Path.GetFileName(folder)}" : "Built-in frames";
    }

    /// <summary>With no frames chosen, use OmegaCustom textures already applied to the Omega install.</summary>
    private void UseOmegaTexturesIfPresent()
    {
        if (_settings.FrameFolder is not null || Services.FrameImages.Count > 0) return;
        if (Services.FrameImages.FindInOmega(OmegaPath) is not { } dir) return;
        var count = Services.FrameImages.Load(dir);
        if (count > 0) FrameStatus = $"Omega's textures ({count})";
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
        OmegaInstall? found;
        try { found = await Task.Run(() => OmegaLocator.Find(saved)); }
        catch (Exception ex)
        {
            CrashLogHook?.Invoke("find omega", ex);
            found = null;
        }
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
        try
        {
            Inventory = await Task.Run(() => OmegaInventory.Scan(root));
        }
        catch (Exception ex)
        {
            CrashLogHook?.Invoke("scan omega", ex);
            Inventory = null;
            OmegaStatus = $"YGO Omega: {root}. Couldn't scan the folder: {ex.Message}";
            return;
        }
        ScanReport = Inventory.Report();
        UseOmegaTexturesIfPresent();
        var inventory = Inventory;
        var ownDb = PackExporter.SafeFileName(_pack.Name);
        try
        {
            Catalog = await Task.Run(() => OmegaCatalog.Load(inventory, ownDb));
        }
        catch (Exception ex)
        {
            CrashLogHook?.Invoke("read omega databases", ex);
            Catalog = OmegaCatalog.Empty;
            ScanReport += $"\nCouldn't read Omega's card databases: {ex.Message}\n";
        }
        foreach (var p in Catalog.Problems) ScanReport += $"\nCouldn't read {p}";
        RefreshCatalogLists();
        foreach (var c in Cards) c.Refresh();
        ScanReport += $"\nIn the game: {Catalog.Passcodes.Count} passcodes in use, {Catalog.Archetypes.Count} archetypes, {Catalog.UnknownTypes.Count} non-standard monster Types.\n";
        foreach (var t in Catalog.UnknownTypes) ScanReport += $"  Type {t.Label}\n";
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

    /// <summary>Where unexpected errors are reported (the app's crash log). Null in tests.</summary>
    public static Action<string, Exception>? CrashLogHook { get; set; }

    /// <summary>Raised when the app should close, e.g. after restarting as administrator.</summary>
    public event Action? RequestClose;

    /// <summary>Handles command-line arguments: a pack to open, and --install to install it straight away.</summary>
    public async Task StartAsync(string[] args)
    {
        var path = args.FirstOrDefault(a => a.EndsWith(PackSerializer.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        path ??= _settings.LastPackPath is { } last && File.Exists(last) ? last : null;
        LoadSavedFrames();
        if (path is not null) OpenPath(path);
        if (InstalledExe is { } exe) SelfUpdater.CleanupOld(exe);
        // Checking for updates runs alongside everything else and never blocks or fails startup.
        if (_settings.CheckForUpdates) _ = CheckForUpdatesAsync(userAsked: false);
        await DetectOmega();
        if (args.Contains("--install") && CanInstall) await Install();
    }

    public void OpenExportFolder() => _dialogs.OpenFolder(ExportFolder);
}
