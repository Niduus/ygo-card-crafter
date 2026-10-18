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
            // "In the same column as": the cards it is compared with have a picker of their own.
            _r.Cards = IsCards ? new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster } : null;
            _cardsEditor = null;
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

    /// <summary>Junora the Power Patron of Tuning: "in the same column as the "Elfnote" monster(s) you control", the cards picked in its own picker.</summary>
    public bool IsCards => Prop.Key == CardProperties.SameColumn;

    private FilterEditorViewModel? _cardsEditor;

    /// <summary>The picker of the cards a "same column" requirement is compared with (on the field).</summary>
    public FilterEditorViewModel? CardsEditor => !IsCards ? null
        : _cardsEditor ??= new FilterEditorViewModel(_r.Cards ??= new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster },
            Zone.Field, false, () => _owner.Pack, _changed, ownerChoice: true) { Nested = true };

    public bool IsNumber => Prop.Kind == PropertyKind.Number;
    public bool IsRange => IsNumber && _r.Op is RequirementOp.Between or RequirementOp.Outside;
    public bool IsChoices => Prop.Kind is PropertyKind.Flags or PropertyKind.Options;
    public bool IsArchetype => Prop.Kind == PropertyKind.Archetype;

    // "Counters on it": which counter, kept as its id in Values (none for every counter).
    public bool IsCounter => Prop.Key == CardProperties.CounterCount;
    public IReadOnlyList<Core.Blocks.ChoiceOption> CounterOptions { get; } = Core.Blocks.Counters.Kinds(withAny: true);
    private bool _customCounter;

    public Core.Blocks.ChoiceOption? Counter
    {
        get
        {
            var value = _customCounter ? Core.Blocks.Counters.Custom : Core.Blocks.Counters.ValueOf(CardProperties.CounterOf(_r));
            return CounterOptions.FirstOrDefault(o => o.Value == value) ?? CounterOptions[0];
        }
        set
        {
            if (value is null) return;
            _customCounter = value.Value == Core.Blocks.Counters.Custom;
            _r.Values = value.Value switch
            {
                Core.Blocks.Counters.Any => [],
                Core.Blocks.Counters.Custom => _r.Values,
                var hex => [long.Parse(hex.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)],
            };
            OnPropertyChanged(nameof(IsCustomCounter));
            OnPropertyChanged(nameof(CounterId));
            _changed();
        }
    }

    public bool IsCustomCounter => IsCounter && (_customCounter || Core.Blocks.Counters.ValueOf(CardProperties.CounterOf(_r)) == Core.Blocks.Counters.Custom);

    /// <summary>The counter's id in hexadecimal ("0x1f"), for a counter that isn't in the list.</summary>
    public string CounterId
    {
        get => CardProperties.CounterOf(_r) is { } id ? Core.Blocks.Counters.Hex(id) : "";
        set
        {
            if (HexSegmentViewModel.Parse(value, 1, 0xffff) is not { } id) return;
            _r.Values = [id];
            _changed();
        }
    }
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

    /// <summary>The requirement's "or" group, 0 for none: any one requirement of a group is enough.</summary>
    public decimal? Group
    {
        get => _r.Group;
        set { var v = Math.Clamp((int)(value ?? 0), 0, 9); if (v == _r.Group) return; _r.Group = v; _changed(); }
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
