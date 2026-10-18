using System.Collections.ObjectModel;
using CardCrafter.Core.Combos;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CardCrafter.App.ViewModels;

/// <summary>A card the step can use, where it is at that point of the combo: "Snake-Eye Ash · Hand".</summary>
public sealed record ComboCardChoice(long Code, int Copy, ComboZone Zone, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Edits one step: its kind, its card (picked from the cards where this kind of step takes them, on the board just before
/// it), where the card comes from and goes, the effect of its text it used, what moved with it, and the player's note.
/// </summary>
public sealed partial class ComboStepEditorViewModel : ObservableObject
{
    private readonly CombosViewModel _owner;
    private readonly ComboStep _step;
    private readonly ComboBoard _before;

    public ComboStepEditorViewModel(CombosViewModel owner, ComboStep step, int index, ComboReplay replay)
    {
        _owner = owner;
        _step = step;
        Index = index;
        _before = replay.BoardAfter(index);
        _kind = Options.Find(owner.StepKinds, step.Kind);
        FromChoices = ZoneOptions(ComboRules.FromChoices(step.Kind));
        ToChoices = ZoneOptions(ComboRules.ToChoices(step.Kind));
        _cardSearch = "";
        CardChoices = BuildCardChoices();
        _card = CurrentChoice(CardChoices, step.Code, step.Copy, step.From, addMissing: true);
        EffectChoices = BuildEffectChoices();
        foreach (var (m, i) in step.Materials.Select((m, i) => (m, i))) Materials.Add(new ComboMaterialRowViewModel(this, m, i));
    }

    public int Index { get; }
    public string Header => $"Step {Index + 1}";
    private ComboCatalog Catalog => _owner.Catalog;
    private string Name(long code) => _owner.Catalog.Name(code, _owner.Combo?.Names);

    private static IReadOnlyList<Option<ComboZone>> ZoneOptions(IEnumerable<ComboZone> zones) =>
        zones.Distinct().Select(z => new Option<ComboZone>(z, z == ComboZone.Auto ? "Auto" : ComboText.Zone(z))).ToList();

    // ---------------------------------------------------------------- kind

    private Option<ComboStepKind> _kind;
    public IReadOnlyList<Option<ComboStepKind>> Kinds => _owner.StepKinds;
    public Option<ComboStepKind> Kind
    {
        get => _kind;
        set
        {
            if (value is null || _owner.SwitchingEditor || value.Value == _step.Kind) return;
            _owner.RememberStepEdit("kind");
            _step.Kind = value.Value;
            // Places the old kind used may make no sense for the new one.
            if (!ComboRules.FromChoices(value.Value).Contains(_step.From)) _step.From = ComboZone.Auto;
            if (!ComboRules.ToChoices(value.Value).Contains(_step.To)) _step.To = ComboZone.Auto;
            if (!ComboRules.NeedsCard(value.Value)) { _step.Code = 0; _step.Copy = 0; }
            _owner.StepEdited(rebuildEditor: true);
        }
    }

    public bool NeedsCard => ComboRules.NeedsCard(_step.Kind);
    public bool IsInterruption => _step.Kind == ComboStepKind.Interruption;
    public string CardLabel => _step.Kind switch
    {
        ComboStepKind.Interruption => "The opponent's card",
        ComboStepKind.Attach or ComboStepKind.Detach => "The Xyz Monster",
        ComboStepKind.Draw => "The card drawn",
        _ => "Card",
    };

    // ---------------------------------------------------------------- card

    public IReadOnlyList<ComboCardChoice> CardChoices { get; private set; }
    private ComboCardChoice? _card;
    public ComboCardChoice? Card
    {
        get => _card;
        set
        {
            if (value is null || _owner.SwitchingEditor || (value.Code == _step.Code && value.Copy == _step.Copy && value.Zone == _step.From)) return;
            _owner.RememberStepEdit("card");
            if (value.Code != _step.Code) _step.Effect = 0;
            _step.Code = value.Code;
            _step.Copy = value.Copy;
            if (value.Zone != ComboZone.Auto || _step.From == ComboZone.Token) _step.From = value.Zone;
            _owner.StepEdited(rebuildEditor: true);
        }
    }

    /// <summary>The opponent's card or a Token can be any card: they are found by name.</summary>
    public bool ShowsSearch => _step.Kind == ComboStepKind.Interruption || _step.From == ComboZone.Token;
    private string _cardSearch;
    public string CardSearch
    {
        get => _cardSearch;
        set
        {
            if (_cardSearch == value || _owner.SwitchingEditor) return;
            _cardSearch = value;
            OnPropertyChanged();
            CardChoices = BuildCardChoices();
            OnPropertyChanged(nameof(CardChoices));
            _card = CurrentChoice(CardChoices, _step.Code, _step.Copy, _step.From, addMissing: true);
            OnPropertyChanged(nameof(Card));
        }
    }

    private List<ComboCardChoice> BuildCardChoices()
    {
        if (ShowsSearch)
        {
            var zone = _step.From == ComboZone.Token ? ComboZone.Token : ComboZone.Auto;
            var found = _cardSearch.Trim().Length > 0
                ? Catalog.Search(_cardSearch, 30)
                : _owner.Combo is { } combo && zone == ComboZone.Auto
                    ? combo.Deck.Playable.Concat(combo.Deck.Side).Distinct().Select(Catalog.Find).OfType<ComboCard>().ToList()
                    : [];
            return found.Select(c => new ComboCardChoice(c.Id, 0, zone, c.Name)).ToList();
        }
        if (_step.Kind == ComboStepKind.Draw && _before.Deck.Count > 0)
            return [new ComboCardChoice(0, 0, ComboZone.Deck, "The top card (at random)"), .. Spots(ComboRules.SearchZones(_step.Kind), null)];
        var zones = _step.From is ComboZone.Auto or ComboZone.Token ? ComboRules.SearchZones(_step.Kind) : [_step.From, .. ComboRules.SearchZones(_step.Kind)];
        return Spots(zones, null);
    }

    /// <summary>The cards in those zones on the board before this step, each copy once, in the order of the zones.</summary>
    internal List<ComboCardChoice> Spots(IReadOnlyList<ComboZone> zones, BoardCard? except)
    {
        var copies = _before.Spots().GroupBy(s => s.Card.Code).ToDictionary(g => g.Key, g => g.Count());
        return _before.Spots()
            .Where(s => !ReferenceEquals(s.Card, except) && zones.Any(z => ComboRules.ZoneMatches(s.Zone, z)))
            .OrderBy(s => IndexOf(zones, s.Zone))
            .Select(s => new ComboCardChoice(s.Card.Code, s.Card.Copy, s.Zone,
                $"{Name(s.Card.Code)}{(copies[s.Card.Code] > 1 ? $" #{s.Card.Copy}" : "")} · {ComboText.Zone(s.Zone)}"))
            .DistinctBy(c => (c.Code, c.Copy))
            .ToList();
    }

    private static int IndexOf(IReadOnlyList<ComboZone> zones, ComboZone zone)
    {
        for (int i = 0; i < zones.Count; i++) if (ComboRules.ZoneMatches(zone, zones[i])) return i;
        return zones.Count;
    }

    /// <summary>The choice for the card the step names; one marked "not there" is added when the card isn't there any more.</summary>
    internal ComboCardChoice? CurrentChoice(IReadOnlyList<ComboCardChoice> choices, long code, int copy, ComboZone from, bool addMissing)
    {
        if (code == 0) return choices.FirstOrDefault(c => c.Code == 0);
        var match = choices.FirstOrDefault(c => c.Code == code && c.Copy == copy && (from == ComboZone.Auto || c.Zone == from || from == ComboZone.Token))
                    ?? choices.FirstOrDefault(c => c.Code == code && (copy == 0 || c.Copy == copy))
                    ?? choices.FirstOrDefault(c => c.Code == code);
        if (match is not null || !addMissing) return match;
        var missing = new ComboCardChoice(code, copy, from, $"{Name(code)} (not there at this point)");
        if (choices is List<ComboCardChoice> list) list.Insert(0, missing);
        return missing;
    }

    // ---------------------------------------------------------------- zones

    public IReadOnlyList<Option<ComboZone>> FromChoices { get; }
    public IReadOnlyList<Option<ComboZone>> ToChoices { get; }
    public bool ShowFrom => NeedsCard && !IsInterruption && FromChoices.Count > 1;
    public bool ShowTo => NeedsCard && !IsInterruption && ToChoices.Count > 1;

    public Option<ComboZone> From
    {
        get => Options.Find(FromChoices, _step.From);
        set
        {
            if (value is null || _owner.SwitchingEditor || value.Value == _step.From) return;
            _owner.RememberStepEdit("from");
            _step.From = value.Value;
            _owner.StepEdited(rebuildEditor: true);
        }
    }

    public Option<ComboZone> To
    {
        get => Options.Find(ToChoices, _step.To);
        set
        {
            if (value is null || _owner.SwitchingEditor || value.Value == _step.To) return;
            _owner.RememberStepEdit("to");
            _step.To = value.Value;
            if (_step.Slot > SlotChoices.Count) _step.Slot = 0;
            _owner.StepEdited(rebuildEditor: true);
        }
    }

    /// <summary>Where the card ends up: the zone picked, or where this kind of step puts it.</summary>
    private ComboZone Destination => _step.To != ComboZone.Auto ? _step.To : ComboRules.DefaultTo(_step.Kind, Catalog.Find(_step.Code)?.Data);

    public IReadOnlyList<Option<int>> SlotChoices => Destination switch
    {
        ComboZone.ExtraMonsterZone => Options.Of((0, "First free"), (1, "Left"), (2, "Right")),
        ComboZone.PendulumZone => Options.Of((0, "First free"), (1, "Left scale"), (2, "Right scale")),
        ComboZone.MonsterZone or ComboZone.SpellTrapZone => Options.Of((0, "First free"), (1, "Zone 1"), (2, "Zone 2"), (3, "Zone 3"), (4, "Zone 4"), (5, "Zone 5")),
        _ => [],
    };
    public bool ShowSlot => NeedsCard && !IsInterruption && SlotChoices.Count > 0 && _step.Kind != ComboStepKind.ChangePosition
                            && (_step.Kind != ComboStepKind.ActivateEffect || _step.To != ComboZone.Auto);

    public Option<int>? Slot
    {
        get => SlotChoices.Count == 0 ? null : Options.Find(SlotChoices, _step.Slot);
        set
        {
            if (value is null || _owner.SwitchingEditor || value.Value == _step.Slot) return;
            _owner.RememberStepEdit("slot");
            _step.Slot = value.Value;
            _owner.StepEdited(rebuildEditor: false);
        }
    }

    public IReadOnlyList<Option<ComboPosition>> PositionChoices => _step.Kind switch
    {
        ComboStepKind.NormalSummon or ComboStepKind.TributeSummon or ComboStepKind.FusionSummon or ComboStepKind.SynchroSummon
            or ComboStepKind.XyzSummon or ComboStepKind.RitualSummon => Options.Of((ComboPosition.Auto, "Attack Position"), (ComboPosition.Defense, "Defense Position")),
        ComboStepKind.SpecialSummon => Options.Of((ComboPosition.Auto, "Attack Position"), (ComboPosition.Defense, "Defense Position"), (ComboPosition.FaceDownDefense, "Face-down Defense")),
        ComboStepKind.Banish => Options.Of((ComboPosition.Auto, "Face-up"), (ComboPosition.FaceDown, "Face-down")),
        ComboStepKind.ChangePosition => Options.Of((ComboPosition.Auto, "Switch (Attack ↔ Defense)"), (ComboPosition.Attack, "Attack Position"), (ComboPosition.Defense, "Defense Position"),
            (ComboPosition.FaceDownDefense, "Face-down Defense"), (ComboPosition.FaceUp, "Flip face-up"), (ComboPosition.FaceDown, "Set face-down")),
        _ => [],
    };
    public bool ShowPosition => PositionChoices.Count > 0;

    public Option<ComboPosition>? Position
    {
        get => PositionChoices.Count == 0 ? null : Options.Find(PositionChoices, _step.Position);
        set
        {
            if (value is null || _owner.SwitchingEditor || value.Value == _step.Position) return;
            _owner.RememberStepEdit("position");
            _step.Position = value.Value;
            _owner.StepEdited(rebuildEditor: false);
        }
    }

    // ---------------------------------------------------------------- effect

    public IReadOnlyList<Option<int>> EffectChoices { get; }
    public bool ShowEffect => NeedsCard && EffectChoices.Count > 1;

    private List<Option<int>> BuildEffectChoices()
    {
        var list = new List<Option<int>> { new(0, "No effect picked") };
        if (_step.Code != 0 && Catalog.Find(_step.Code) is { } card)
            foreach (var p in CardEffects.Split(card.Text).Where(p => p.IsEffect))
                list.Add(new Option<int>(p.Number, $"{p.Number}. {Short(p.Text)}"));
        return list;
    }

    private static string Short(string text) => text.Length <= 80 ? text : text[..77].TrimEnd() + "…";

    public Option<int> Effect
    {
        get => Options.Find(EffectChoices, _step.Effect);
        set
        {
            if (value is null || _owner.SwitchingEditor || value.Value == _step.Effect) return;
            _owner.RememberStepEdit("effect");
            _step.Effect = value.Value;
            OnPropertyChanged(nameof(EffectText));
            OnPropertyChanged(nameof(HasEffectText));
            _owner.StepEdited(rebuildEditor: false);
        }
    }

    public string EffectText => _step.Effect > 0 && Catalog.Find(_step.Code) is { } c ? CardEffects.Effect(c.Text, _step.Effect) ?? "" : "";
    public bool HasEffectText => EffectText.Length > 0;

    // ---------------------------------------------------------------- materials

    public ObservableCollection<ComboMaterialRowViewModel> Materials { get; } = [];
    public string? MaterialsLabel => ComboRules.MaterialsLabel(_step.Kind);
    public bool ShowMaterials => MaterialsLabel is not null;

    /// <summary>What a moved card can be: any card on the board before the step, except the step's own.</summary>
    internal List<ComboCardChoice> MaterialChoices()
    {
        var main = _before.Spots().FirstOrDefault(s => s.Card.Code == Catalog.Canonical(_step.Code) && (_step.Copy == 0 || s.Card.Copy == _step.Copy))?.Card;
        return _step.Kind == ComboStepKind.Detach && main is not null
            ? main.Materials.Select(m => new ComboCardChoice(m.Code, m.Copy, ComboZone.XyzMaterial, $"{Name(m.Code)} · Xyz Material")).ToList()
            : Spots(ComboRules.MaterialSearchZones(_step.Kind), main);
    }

    [RelayCommand]
    private void AddMaterial()
    {
        var used = _step.Materials.Select(m => (m.Code, m.Copy)).ToHashSet();
        var choices = MaterialChoices();
        var pick = choices.FirstOrDefault(c => !used.Contains((c.Code, c.Copy)) && ComboBoard.IsOnField(c.Zone))
                   ?? choices.FirstOrDefault(c => !used.Contains((c.Code, c.Copy)));
        if (pick is null) return;
        _owner.RememberStepEdit("materials-add");
        _step.Materials.Add(new ComboMove { Code = pick.Code, Copy = pick.Copy, From = pick.Zone });
        _owner.StepEdited(rebuildEditor: true);
    }

    internal void RemoveMaterial(int index)
    {
        if (index < 0 || index >= _step.Materials.Count) return;
        _owner.RememberStepEdit($"materials-remove-{index}");
        _step.Materials.RemoveAt(index);
        _owner.StepEdited(rebuildEditor: true);
    }

    internal void MaterialEdited(int index, string field, Action change, bool rebuild)
    {
        _owner.RememberStepEdit($"material{index}-{field}");
        change();
        _owner.StepEdited(rebuild);
    }

    // ---------------------------------------------------------------- note and problems

    public string Note
    {
        get => _step.Note;
        set
        {
            if (_step.Note == value || _owner.SwitchingEditor) return;
            _owner.RememberStepEdit("note");
            _step.Note = value;
            _owner.StepEdited(rebuildEditor: false);
        }
    }

    public string Problems => string.Join("\n", _owner.ProblemsOf(Index));
    public bool HasProblems => Problems.Length > 0;
    public string Moved => string.Join("\n", _owner.MovesOf(Index));
    public bool HasMoved => Moved.Length > 0;

    internal bool SwitchingEditor => _owner.SwitchingEditor;

    /// <summary>Tells the view to read every value again.</summary>
    internal void Resync()
    {
        OnPropertyChanged(string.Empty);
        foreach (var m in Materials) m.Resync();
    }

    internal void RefreshProblems()
    {
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(Moved));
        OnPropertyChanged(nameof(HasMoved));
    }
}

/// <summary>One card moved with the step: which card, from where, to where.</summary>
public sealed class ComboMaterialRowViewModel : ObservableObject
{
    private readonly ComboStepEditorViewModel _editor;
    private readonly ComboMove _move;
    private readonly int _index;

    public ComboMaterialRowViewModel(ComboStepEditorViewModel editor, ComboMove move, int index)
    {
        _editor = editor;
        _move = move;
        _index = index;
        CardChoices = editor.MaterialChoices();
        _card = editor.CurrentChoice(CardChoices, move.Code, move.Copy, move.From, true);
        RemoveCommand = new RelayCommand(() => editor.RemoveMaterial(index));
    }

    public IReadOnlyList<ComboCardChoice> CardChoices { get; }
    private readonly ComboCardChoice? _card;
    public ComboCardChoice? Card
    {
        get => _card;
        set
        {
            if (value is null || _editor.SwitchingEditor || (value.Code == _move.Code && value.Copy == _move.Copy && value.Zone == _move.From)) return;
            _editor.MaterialEdited(_index, "card", () =>
            {
                _move.Code = value.Code;
                _move.Copy = value.Copy;
                _move.From = value.Zone;
            }, rebuild: true);
        }
    }

    public IReadOnlyList<Option<ComboZone>> FromChoices { get; } = ComboRules.MaterialFromChoices.Select(z => new Option<ComboZone>(z, z == ComboZone.Auto ? "Auto" : ComboText.Zone(z))).ToList();
    public IReadOnlyList<Option<ComboZone>> ToChoices { get; } = ComboRules.MaterialToChoices.Select(z => new Option<ComboZone>(z, z == ComboZone.Auto ? "Auto" : ComboText.Zone(z))).ToList();

    public Option<ComboZone> From
    {
        get => Options.Find(FromChoices, _move.From);
        set
        {
            if (value is null || _editor.SwitchingEditor || value.Value == _move.From) return;
            _editor.MaterialEdited(_index, "from", () => _move.From = value.Value, rebuild: true);
        }
    }

    public Option<ComboZone> To
    {
        get => Options.Find(ToChoices, _move.To);
        set
        {
            if (value is null || _editor.SwitchingEditor || value.Value == _move.To) return;
            _editor.MaterialEdited(_index, "to", () => _move.To = value.Value, rebuild: false);
        }
    }

    public IRelayCommand RemoveCommand { get; }

    internal void Resync() => OnPropertyChanged(string.Empty);
}
