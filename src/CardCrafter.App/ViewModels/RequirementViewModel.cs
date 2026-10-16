using System.Collections.ObjectModel;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>One value of a choice requirement, ticked or not: "DARK", "Fusion".</summary>
public sealed partial class RequirementChoice(long value, string label, bool on, Action<RequirementChoice> toggled) : ObservableObject
{
    public long Value { get; } = value;
    public string Label { get; } = label;
    private bool _isChecked = on;
    public bool IsChecked { get => _isChecked; set { if (SetProperty(ref _isChecked, value)) toggled(this); } }
}

/// <summary>
/// One row of "more requirements" in the card picker: a property, a comparison that fits it, and the value(s) it takes.
/// The inputs follow the property: numbers (one or two), ticks for choices, an archetype or a card name.
/// </summary>
public sealed partial class RequirementViewModel : ObservableObject
{
    private readonly CardRequirement _r;
    private readonly FilterEditorViewModel _owner;
    private readonly Action _changed;
    private readonly Action<RequirementViewModel> _remove;

    public RequirementViewModel(CardRequirement r, FilterEditorViewModel owner, Action changed, Action<RequirementViewModel> remove)
    {
        _r = r;
        _owner = owner;
        _changed = changed;
        _remove = remove;
        PropertyOptions = CardProperties.All.Where(p => !(owner.MaterialMode && p.NeedsEffect)).Select(p => new Option<string>(p.Key, p.Label)).ToList();
        BuildChoices();
    }

    public CardRequirement Model => _r;
    private CardProperty Prop => CardProperties.Find(_r.Property) ?? CardProperties.All[0];

    public IReadOnlyList<Option<string>> PropertyOptions { get; }

    public Option<string>? Property
    {
        get => PropertyOptions.FirstOrDefault(o => o.Value == _r.Property) ?? PropertyOptions[0];
        set
        {
            if (value is null || value.Value == _r.Property) return;
            _r.Property = value.Value;
            // A new property starts with its first comparison and no values.
            _r.Op = Prop.Ops[0];
            _r.Values = [];
            if (Prop.Kind == PropertyKind.Number && _r.Value == 0) _r.Value = Prop.Key is "atk" or "def" or "base_atk" or "base_def" ? 2000 : 1;
            BuildChoices();
            OnPropertyChanged(string.Empty);
            _changed();
        }
    }

    public IReadOnlyList<Option<RequirementOp>> OpOptions => Prop.Ops.Select(o => new Option<RequirementOp>(o, CardProperties.OpLabel(o))).ToList();

    public Option<RequirementOp>? Op
    {
        get => OpOptions.FirstOrDefault(o => o.Value == _r.Op) ?? OpOptions[0];
        set
        {
            if (value is null || value.Value == _r.Op) return;
            _r.Op = value.Value;
            OnPropertyChanged(nameof(IsRange));
            _changed();
        }
    }

    public bool IsNumber => Prop.Kind == PropertyKind.Number;
    public bool IsRange => IsNumber && _r.Op is RequirementOp.Between or RequirementOp.Outside;
    public bool IsChoices => Prop.Kind is PropertyKind.Flags or PropertyKind.Options;
    public bool IsArchetype => Prop.Kind == PropertyKind.Archetype;
    public bool IsCardName => Prop.Kind == PropertyKind.CardName;

    public decimal? Value
    {
        get => _r.Value;
        set { var v = (int)(value ?? 0); if (v == _r.Value) return; _r.Value = v; _changed(); }
    }

    public decimal? Value2
    {
        get => _r.Value2;
        set { var v = (int)(value ?? 0); if (v == _r.Value2) return; _r.Value2 = v; _changed(); }
    }

    public ObservableCollection<RequirementChoice> Choices { get; } = [];

    private void BuildChoices()
    {
        Choices.Clear();
        if (!IsChoices) return;
        IEnumerable<(long Value, string Label)> options = Prop.Key is "race" or "orig_race"
            ? Lists.RacesFor(_owner.Pack, CardRace.None).Select(o => ((long)o.Value, o.Label))
            : Prop.Choices!.Select(c => (c.Value, c.Label));
        foreach (var (value, label) in options)
            Choices.Add(new RequirementChoice(value, label, _r.Values.Contains(value), Toggled));
    }

    private void Toggled(RequirementChoice c)
    {
        _r.Values.Remove(c.Value);
        if (c.IsChecked) _r.Values.Add(c.Value);
        _changed();
    }

    public IReadOnlyList<ArchetypeOption> ArchetypeOptions => _owner.ArchetypeOptions;

    public ArchetypeOption? Archetype
    {
        get => ArchetypeOptions.FirstOrDefault(a => _r.Values.Contains(a.Code)) ?? ArchetypeOptions[0];
        set
        {
            if (value is null) return;
            _r.Values = value.Code == 0 ? [] : [value.Code];
            _changed();
        }
    }

    public IEnumerable<string> CardNameChoices => _owner.CardNameChoices;
    private string? _typed;

    public string CardText
    {
        get => _typed ?? (_r.Values.FirstOrDefault() is var code and not 0
            ? Core.Text.CardNames.Find(code, _owner.Pack) is { } n ? FilterEditorViewModel.Entry(n, code) : code.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "");
        set
        {
            var code = _owner.ParseCard(value);
            _typed = code < 0 ? value : null;
            if (code < 0) return;
            _r.Values = code == 0 ? [] : [code];
            _changed();
        }
    }

    [RelayCommand]
    private void Remove() => _remove(this);
}
