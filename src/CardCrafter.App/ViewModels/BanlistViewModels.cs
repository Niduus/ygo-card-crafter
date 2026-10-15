using System.Collections.ObjectModel;
using CardCrafter.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>One card on a banlist and its limit.</summary>
public sealed partial class BanlistEntryViewModel(BanlistEntry model, Action changed, Action<BanlistEntryViewModel> remove) : ObservableObject
{
    public BanlistEntry Model { get; } = model;
    public string Name => Model.Name.Length > 0 ? Model.Name : Model.Code.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Code => Model.Code.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public IReadOnlyList<Option<Limitation>> LimitOptions => Lists.Limitations;

    public Option<Limitation> Limit
    {
        get => Options.Find(Lists.Limitations, (Limitation)Model.Limit);
        set { if (value is null || (int)value.Value == Model.Limit) return; Model.Limit = (int)value.Value; OnPropertyChanged(); changed(); }
    }

    [RelayCommand]
    private void Remove() => remove(this);
}

/// <summary>A banlist being edited on the Lists tab.</summary>
public sealed partial class BanlistViewModel : ObservableObject
{
    private readonly Action _changed;

    public BanlistViewModel(PackBanlist model, Action changed)
    {
        Model = model;
        _changed = changed;
        foreach (var e in model.Entries) Entries.Add(new BanlistEntryViewModel(e, Changed, Remove));
    }

    public PackBanlist Model { get; }
    public ObservableCollection<BanlistEntryViewModel> Entries { get; } = [];

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name == value) return; Model.Name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Label)); _changed(); }
    }

    public string Label => $"{(Model.Name.Length > 0 ? Model.Name : "(no name)")}  ·  {Entries.Count} card{(Entries.Count == 1 ? "" : "s")}";
    public string IdText => $"Banlist id {Model.Id}";

    /// <summary>"3 Forbidden · 5 Limited · 2 Semi-Limited".</summary>
    public string Summary
    {
        get
        {
            var parts = new[] { (0, "Forbidden"), (1, "Limited"), (2, "Semi-Limited"), (3, "Unlimited") }
                .Select(x => (Count: Model.Entries.Count(e => e.Limit == x.Item1), x.Item2)).Where(x => x.Count > 0).Select(x => $"{x.Count} {x.Item2}");
            var text = string.Join("  ·  ", parts);
            return text.Length == 0 ? "No cards yet. Search for cards below to add them." : text;
        }
    }

    /// <summary>Adds a card, or changes its limit when it is already on the list.</summary>
    public void Add(long code, string name, Limitation limit)
    {
        if (Entries.FirstOrDefault(e => e.Model.Code == code) is { } existing)
        {
            existing.Limit = Options.Find(Lists.Limitations, limit);
            return;
        }
        var entry = new BanlistEntry { Code = code, Name = name, Limit = (int)limit };
        Model.Entries.Add(entry);
        Entries.Insert(0, new BanlistEntryViewModel(entry, Changed, Remove));
        Changed();
    }

    private void Remove(BanlistEntryViewModel vm)
    {
        Model.Entries.Remove(vm.Model);
        Entries.Remove(vm);
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Label));
        _changed();
    }
}

/// <summary>A banlist found in YGO Omega or a database file, shown so it can be copied into the pack.</summary>
public sealed record ScannedBanlist(PackBanlist List, string Source)
{
    public string Title => List.Name.Length > 0 ? List.Name : $"Banlist {List.Id}";
    public string Detail => $"{Source}  ·  id {List.Id}  ·  {List.Entries.Count(e => e.Limit == 0)} Forbidden, {List.Entries.Count(e => e.Limit == 1)} Limited, {List.Entries.Count(e => e.Limit == 2)} Semi-Limited";
}

/// <summary>A card that can be put on a banlist: one of Omega's or one of this pack's.</summary>
public sealed record BanlistCandidate(long Code, string Name, string Detail);
