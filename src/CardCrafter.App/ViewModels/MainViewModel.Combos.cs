using CardCrafter.Core.Combos;
using CardCrafter.Core.Omega;

namespace CardCrafter.App.ViewModels;

/// <summary>The Combos tab, and the cards it can show: the pack's own and every card in YGO Omega's databases.</summary>
public sealed partial class MainViewModel
{
    private CombosViewModel? _combos;
    private CardLibrary? _comboLibrary;
    private List<ComboCard>? _libraryCards;

    public CombosViewModel Combos => _combos ??= new CombosViewModel(this, _dialogs);

    /// <summary>The pack's cards as they are now, then the game's (once its databases have been read).</summary>
    public ComboCatalog ComboCards
    {
        get
        {
            var library = _comboLibrary;
            if (library is null) return ComboCatalog.Build(_pack, null);
            _libraryCards ??= library.Cards.Select(c => new ComboCard(c.Id, c.Name, c.Data, c.Text)).ToList();
            var pack = ComboCatalog.Build(_pack, null).All;
            return new ComboCatalog(pack.Concat(_libraryCards), library.AltArts);
        }
    }

    /// <summary>Reads Omega's databases for the Combos tab (once), then tells it to show the names.</summary>
    public async Task LoadComboLibraryAsync()
    {
        try
        {
            var library = await GetLibraryAsync();
            if (ReferenceEquals(library, _comboLibrary)) return;
            _comboLibrary = library;
            _libraryCards = null;
            _combos?.CatalogChanged();
        }
        catch (Exception ex)
        {
            CrashLogHook?.Invoke("read cards for combos", ex);
        }
    }

    /// <summary>The art of a card: the pack card's own, or the game's picture of it.</summary>
    public string? ArtPathFor(long id)
    {
        var own = _pack.Cards.FirstOrDefault(c => c.Card.Id == id);
        if (own is not null) return PackExporter.ResolveArtwork(own.ArtworkPath, PackFolder);
        if (Inventory?.BestImageFolder is not { } folder) return null;
        foreach (var ext in new[] { ".jpg", ".png" })
        {
            var path = Path.Combine(folder, id + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Marks the pack changed after an edit on the Combos tab.</summary>
    internal void CombosEdited() => MarkDirty();
}
