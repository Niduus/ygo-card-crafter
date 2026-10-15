using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>One Fusion Material on the Card tab: what it must be, and how many.</summary>
public sealed partial class MaterialSlotViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<MaterialSlotViewModel> _remove;

    public MaterialSlotViewModel(MaterialSlot slot, Func<CardPack> pack, Action changed, Action<MaterialSlotViewModel> remove)
    {
        Slot = slot;
        _changed = changed;
        _remove = remove;
        _pack = pack;
        Filter = new FilterEditorViewModel(slot.Filter, Zone.None, true, pack, () => { OnPropertyChanged(nameof(Count)); OnPropertyChanged(nameof(Summary)); changed(); });
    }

    private readonly Func<CardPack> _pack;

    /// <summary>What the material must be, singular for one card: ""Aleister" monster", "EARTH or LIGHT monsters".</summary>
    public string Summary => FilterText.Describe(Slot.Filter, _pack(), plural: Slot.Max != 1, withLocation: false);

    public MaterialSlot Slot { get; }
    public FilterEditorViewModel Filter { get; }

    public decimal? Min
    {
        get => Slot.Min;
        set { var v = Math.Max(1, (int)(value ?? 1)); if (Slot.Min == v) return; Slot.Min = v; OnPropertyChanged(); OnPropertyChanged(nameof(Count)); _changed(); }
    }

    /// <summary>0 means "or more".</summary>
    public decimal? Max
    {
        get => Slot.Max;
        set { var v = Math.Max(0, (int)(value ?? 0)); if (Slot.Max == v) return; Slot.Max = v; OnPropertyChanged(); OnPropertyChanged(nameof(Count)); OnPropertyChanged(nameof(Summary)); _changed(); }
    }

    /// <summary>"1", "2+" or "1-3", as the card will print it.</summary>
    public string Count => Slot.Max == 0 ? $"{Slot.Min}+" : Slot.Min == Slot.Max ? $"{Slot.Min}" : $"{Slot.Min}-{Slot.Max}";

    [RelayCommand]
    private void Remove() => _remove(this);
}
