using System.Globalization;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CardCrafter.App.ViewModels;

public sealed record ArchetypeOption(int Code, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Edits one card filter ("which cards?") in a pop-up next to the block.</summary>
public sealed partial class FilterEditorViewModel : ObservableObject
{
    private readonly CardFilter _f;
    private readonly Action _changed;
    private readonly Func<CardPack> _pack;

    public FilterEditorViewModel(CardFilter filter, Zone allowedZones, bool materialMode, Func<CardPack> pack, Action changed, bool ownerChoice = false, bool allowThisCard = false, bool whereFromBlock = false)
    {
        _whereFromBlock = whereFromBlock;
        AllowThisCard = allowThisCard;
        _f = filter;
        _pack = pack;
        _changed = changed;
        AllowedZones = allowedZones;
        MaterialMode = materialMode;
        _ownerChoice = ownerChoice;
        foreach (var r in filter.Requirements) Requirements.Add(NewRequirement(r));
    }

    public CardPack Pack => _pack();

    // ---- more requirements: any property compared with a value, all for the same card
    public System.Collections.ObjectModel.ObservableCollection<RequirementViewModel> Requirements { get; } = [];

    /// <summary>Any one requirement is enough, instead of all of them.</summary>
    public bool AnyRequirement { get => _f.AnyRequirement; set { if (_f.AnyRequirement == value) return; _f.AnyRequirement = value; Changed(); } }

    private RequirementViewModel NewRequirement(CardRequirement r) => new(r, this, () => Changed(), RemoveRequirement);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void AddRequirement()
    {
        var r = new CardRequirement { Property = "atk", Op = RequirementOp.GreaterOrEqual, Value = 2000 };
        _f.Requirements.Add(r);
        Requirements.Add(NewRequirement(r));
        OnPropertyChanged(nameof(HasSeveralRequirements));
        Changed();
    }

    public bool HasSeveralRequirements => Requirements.Count > 1;

    private void RemoveRequirement(RequirementViewModel vm)
    {
        _f.Requirements.Remove(vm.Model);
        Requirements.Remove(vm);
        OnPropertyChanged(nameof(HasSeveralRequirements));
        Changed();
    }

    private readonly bool _ownerChoice;
    private readonly bool _whereFromBlock;

    public Zone AllowedZones { get; }
    /// <summary>Summon materials: only "what the card is" matters, not where it is.</summary>
    public bool MaterialMode { get; }
    public bool ShowWhere => !MaterialMode && !_whereFromBlock && (AllowedZones == Zone.None || System.Numerics.BitOperations.PopCount((uint)AllowedZones) > 1);
    public bool ShowOwner => !_whereFromBlock && (_ownerChoice || (!MaterialMode && AllowedZones == Zone.None));

    /// <summary>The picker offers "This card itself" (for actions that can move, destroy or Summon this exact card).</summary>
    public bool AllowThisCard { get; }

    public bool ThisCard
    {
        get => _f.ThisCard;
        set { if (_f.ThisCard == value) return; _f.ThisCard = value; OnPropertyChanged(nameof(NotThisCard)); Changed(nameof(ThisCard)); }
    }
    public bool NotThisCard => !_f.ThisCard;

    public bool ExceptOwnName { get => _f.ExceptOwnName; set { if (_f.ExceptOwnName == value) return; _f.ExceptOwnName = value; Changed(nameof(ExceptOwnName)); } }

    public string ExceptCode
    {
        get => _f.ExceptCode == 0 ? "" : _f.ExceptCode.ToString(CultureInfo.InvariantCulture);
        set
        {
            var code = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            if (code == _f.ExceptCode) return;
            _f.ExceptCode = code;
            Changed();
        }
    }

    public string Summary => _f.ThisCard ? "this card" : MaterialMode
        ? FilterText.Describe(_f, _pack(), plural: true, withLocation: false)
        : FilterText.Describe(_f, _pack(), plural: false);

    private void Changed(string? also = null)
    {
        OnPropertyChanged(nameof(Summary));
        if (also is not null) OnPropertyChanged(also);
        _changed();
    }

    public IReadOnlyList<Option<CardOwner>> OwnerOptions => Lists.Owners;
    public Option<CardOwner> Owner
    {
        get => Options.Find(Lists.Owners, _f.Owner);
        set { if (value is null || Equals(_f.Owner, value.Value)) return; _f.Owner = value.Value; Changed(); }
    }

    private bool Has(Zone z) => _f.Zones.HasFlag(z);
    private void Set(Zone z, bool on, string name)
    {
        if (Has(z) == on) return;
        _f.Zones = on ? _f.Zones | z : _f.Zones & ~z;
        Changed(name);
    }

    public bool ShowZone(Zone z) => AllowedZones == Zone.None || AllowedZones.HasFlag(z);
    public bool ShowHand => ShowZone(Zone.Hand);
    public bool ShowDeck => ShowZone(Zone.Deck);
    public bool ShowExtra => ShowZone(Zone.ExtraDeck);
    public bool ShowMonsterZone => ShowZone(Zone.MonsterZone);
    public bool ShowSpellTrapZone => ShowZone(Zone.SpellTrapZone);
    public bool ShowGraveyard => ShowZone(Zone.Graveyard);
    public bool ShowBanished => ShowZone(Zone.Banished);
    public bool ShowPendulumZone => ShowZone(Zone.PendulumZone);

    public bool InHand { get => Has(Zone.Hand); set => Set(Zone.Hand, value, nameof(InHand)); }
    public bool InDeck { get => Has(Zone.Deck); set => Set(Zone.Deck, value, nameof(InDeck)); }
    public bool InExtra { get => Has(Zone.ExtraDeck); set => Set(Zone.ExtraDeck, value, nameof(InExtra)); }
    public bool InMonsterZone { get => Has(Zone.MonsterZone); set => Set(Zone.MonsterZone, value, nameof(InMonsterZone)); }
    public bool InSpellTrapZone { get => Has(Zone.SpellTrapZone); set => Set(Zone.SpellTrapZone, value, nameof(InSpellTrapZone)); }
    public bool InGraveyard { get => Has(Zone.Graveyard); set => Set(Zone.Graveyard, value, nameof(InGraveyard)); }
    public bool InBanished { get => Has(Zone.Banished); set => Set(Zone.Banished, value, nameof(InBanished)); }
    public bool InPendulumZone { get => Has(Zone.PendulumZone); set => Set(Zone.PendulumZone, value, nameof(InPendulumZone)); }

    public IReadOnlyList<Option<FilterCardType>> TypeOptions => Lists.FilterTypes;
    public Option<FilterCardType> CardType
    {
        get => Options.Find(Lists.FilterTypes, _f.CardType);
        set { if (value is null || Equals(_f.CardType, value.Value)) return; _f.CardType = value.Value; Changed(); }
    }

    private IReadOnlyList<Option<CardRace>>? _raceOptions;
    public IReadOnlyList<Option<CardRace>> RaceOptions => _raceOptions ??= [new(CardRace.None, "Any Type"), .. Lists.RacesFor(_pack(), _f.Race)];
    public Option<CardRace> Race
    {
        get => Options.Find(RaceOptions, _f.Race);
        set { if (value is null || Equals(_f.Race, value.Value)) return; _f.Race = value.Value; Changed(); }
    }

    public IReadOnlyList<Option<FilterPosition>> PositionOptions => Lists.Positions;
    public Option<FilterPosition> Position
    {
        get => Options.Find(Lists.Positions, _f.Position);
        set { if (value is null || Equals(_f.Position, value.Value)) return; _f.Position = value.Value; Changed(); }
    }

    public IReadOnlyList<Option<SpellTrapKind>> SpellTrapKindOptions => Lists.SpellTrapKinds;
    public Option<SpellTrapKind> SpellTrapKind
    {
        get => Options.Find(Lists.SpellTrapKinds, _f.SpellTrapKind);
        set { if (value is null || Equals(_f.SpellTrapKind, value.Value)) return; _f.SpellTrapKind = value.Value; Changed(); }
    }

    public IReadOnlyList<Option<FilterSummoned>> SummonedOptions => Lists.Summoned;
    public Option<FilterSummoned> Summoned
    {
        get => Options.Find(Lists.Summoned, _f.Summoned);
        set { if (value is null || Equals(_f.Summoned, value.Value)) return; _f.Summoned = value.Value; Changed(); }
    }

    public decimal? DefMin { get => _f.DefMin; set { if (_f.DefMin == (int?)value) return; _f.DefMin = (int?)value; Changed(); } }
    public decimal? DefMax { get => _f.DefMax; set { if (_f.DefMax == (int?)value) return; _f.DefMax = (int?)value; Changed(); } }
    public bool NotRevealed { get => _f.NotRevealed; set { if (_f.NotRevealed == value) return; _f.NotRevealed = value; Changed(); } }
    public bool MentionsOrIs { get => _f.MentionsOrIs; set { if (_f.MentionsOrIs == value) return; _f.MentionsOrIs = value; Changed(); } }

    private string? _typedAlt, _typedMentions;

    /// <summary>"or also named ...": a second name the card may have instead.</summary>
    public string AltCode
    {
        get => _typedAlt ?? (_f.AltCodes.Length == 0 ? "" : NameOf(_f.AltCodes[0]));
        set
        {
            var code = ParseCard(value);
            _typedAlt = code < 0 ? value : null;
            if (code < 0) return;
            var next = code == 0 ? _f.AltCodes.Skip(1).ToArray() : [code, .. _f.AltCodes.Skip(1)];
            if (next.SequenceEqual(_f.AltCodes)) return;
            _f.AltCodes = next;
            Changed();
        }
    }

    /// <summary>"a card that mentions ...".</summary>
    public string MentionsCode
    {
        get => _typedMentions ?? (_f.MentionsCode == 0 ? "" : NameOf(_f.MentionsCode));
        set
        {
            var code = ParseCard(value);
            _typedMentions = code < 0 ? value : null;
            if (code < 0 || code == _f.MentionsCode) return;
            _f.MentionsCode = code;
            Changed();
        }
    }

    private string NameOf(long code) => CardNames.Find(code, _pack()) is { } n ? Entry(n, code) : code.ToString(CultureInfo.InvariantCulture);

    public IReadOnlyList<Option<MonsterKind>> MonsterKindOptions => Lists.MonsterKinds;
    public Option<MonsterKind> MonsterKind
    {
        get => Options.Find(Lists.MonsterKinds, _f.MonsterKind);
        set { if (value is null || Equals(_f.MonsterKind, value.Value)) return; _f.MonsterKind = value.Value; Changed(); }
    }

    // Attributes: any of the ticked ones ("LIGHT or EARTH"); none ticked means any Attribute.
    private bool HasAttr(CardAttribute a) => _f.Attribute.HasFlag(a);
    private void SetAttr(CardAttribute a, bool on, string name)
    {
        if (HasAttr(a) == on) return;
        _f.Attribute = on ? _f.Attribute | a : _f.Attribute & ~a;
        Changed(name);
    }
    public bool AttrDark { get => HasAttr(CardAttribute.Dark); set => SetAttr(CardAttribute.Dark, value, nameof(AttrDark)); }
    public bool AttrLight { get => HasAttr(CardAttribute.Light); set => SetAttr(CardAttribute.Light, value, nameof(AttrLight)); }
    public bool AttrEarth { get => HasAttr(CardAttribute.Earth); set => SetAttr(CardAttribute.Earth, value, nameof(AttrEarth)); }
    public bool AttrWater { get => HasAttr(CardAttribute.Water); set => SetAttr(CardAttribute.Water, value, nameof(AttrWater)); }
    public bool AttrFire { get => HasAttr(CardAttribute.Fire); set => SetAttr(CardAttribute.Fire, value, nameof(AttrFire)); }
    public bool AttrWind { get => HasAttr(CardAttribute.Wind); set => SetAttr(CardAttribute.Wind, value, nameof(AttrWind)); }
    public bool AttrDivine { get => HasAttr(CardAttribute.Divine); set => SetAttr(CardAttribute.Divine, value, nameof(AttrDivine)); }

    private IReadOnlyList<ArchetypeOption>? _archetypeOptions;
    /// <summary>The pack's archetypes, then the ones in YGO Omega's cards, and the filter's own if it is neither.</summary>
    public IReadOnlyList<ArchetypeOption> ArchetypeOptions => _archetypeOptions ??= BuildArchetypeOptions();

    private List<ArchetypeOption> BuildArchetypeOptions()
    {
        var list = new List<ArchetypeOption> { new(0, "Any archetype") };
        var seen = new HashSet<int> { 0 };
        foreach (var a in _pack().Archetypes) if (seen.Add(a.Code)) list.Add(new(a.Code, $"{a.Name} (0x{a.Code:x})"));
        foreach (var (code, name) in ArchetypeNames.All) if (seen.Add(code)) list.Add(new(code, $"{name} (0x{code:x})"));
        foreach (var code in new[] { _f.Archetype, _f.Archetype2 }) if (seen.Add(code)) list.Add(new(code, $"0x{code:x}"));
        return list;
    }

    public ArchetypeOption? Archetype
    {
        get => ArchetypeOptions.FirstOrDefault(a => a.Code == _f.Archetype) ?? ArchetypeOptions[0];
        set { if (value is null || value.Code == _f.Archetype) return; _f.Archetype = value.Code; Changed(); }
    }

    /// <summary>""Dominators" or "Dominus Purge"": a second archetype a card may be from instead.</summary>
    public ArchetypeOption? Archetype2
    {
        get => ArchetypeOptions.FirstOrDefault(a => a.Code == _f.Archetype2) ?? ArchetypeOptions[0];
        set { if (value is null || value.Code == _f.Archetype2) return; _f.Archetype2 = value.Code; Changed(); }
    }

    public bool FaceDownOnly
    {
        get => _f.FaceDownOnly;
        set { if (_f.FaceDownOnly == value) return; _f.FaceDownOnly = value; if (value) _f.FaceUpOnly = false; OnPropertyChanged(nameof(FaceUpOnly)); Changed(); }
    }

    /// <summary>"Dark Magician (46986414)": a card's name with its passcode, as the card-name box lists it.</summary>
    public static string Entry(string name, long id) => $"{name} ({id.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>Cards the name box suggests: the pack's cards, then every card in YGO Omega's databases.</summary>
    public IEnumerable<string> CardNameChoices =>
        _pack().Cards.Where(c => c.Card.Name.Length > 0).Select(c => Entry(c.Card.Name, c.Card.Id))
            .Concat(CardNames.All.Select(c => Entry(c.Name, c.Id))).Distinct();

    /// <summary>Reads what was typed: "Name (passcode)", a passcode, or a card's exact name. 0 when it is none of them.</summary>
    public long ParseCard(string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\((\d+)\)$");
        if (m.Success) return long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        if (_pack().Cards.FirstOrDefault(c => string.Equals(c.Card.Name, text, StringComparison.OrdinalIgnoreCase)) is { } own) return own.Card.Id;
        foreach (var (id, name) in CardNames.All)
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase)) return id;
        return -1;
    }

    private string? _typed;

    /// <summary>The card's name and passcode; typing a name or a passcode picks the card.</summary>
    public string CardCode
    {
        get => _typed ?? (_f.CardCode == 0 ? "" : CardNames.Find(_f.CardCode, _pack()) is { } n ? Entry(n, _f.CardCode) : _f.CardCode.ToString(CultureInfo.InvariantCulture));
        set
        {
            var code = ParseCard(value);
            // A name still being typed stays as typed; the card is picked once it matches one.
            _typed = code < 0 ? value : null;
            if (code < 0 || code == _f.CardCode) return;
            _f.CardCode = code;
            Changed();
        }
    }

    public decimal? LevelMin { get => _f.LevelMin; set { if (_f.LevelMin == (int?)value) return; _f.LevelMin = (int?)value; Changed(); } }
    public decimal? LevelMax { get => _f.LevelMax; set { if (_f.LevelMax == (int?)value) return; _f.LevelMax = (int?)value; Changed(); } }
    public decimal? AtkMin { get => _f.AtkMin; set { if (_f.AtkMin == (int?)value) return; _f.AtkMin = (int?)value; Changed(); } }
    public decimal? AtkMax { get => _f.AtkMax; set { if (_f.AtkMax == (int?)value) return; _f.AtkMax = (int?)value; Changed(); } }
    public bool FaceUpOnly { get => _f.FaceUpOnly; set { if (_f.FaceUpOnly == value) return; _f.FaceUpOnly = value; if (value) _f.FaceDownOnly = false; OnPropertyChanged(nameof(FaceDownOnly)); Changed(); } }
    public bool PointedToByThisCard { get => _f.PointedToByThisCard; set { if (_f.PointedToByThisCard == value) return; _f.PointedToByThisCard = value; Changed(); } }
    public bool CenterZone { get => _f.CenterZone; set { if (_f.CenterZone == value) return; _f.CenterZone = value; Changed(); } }
    public bool ExcludeThisCard { get => _f.ExcludeThisCard; set { if (_f.ExcludeThisCard == value) return; _f.ExcludeThisCard = value; Changed(); } }

    /// <summary>Called when the pack's archetype list changes so the drop-down refreshes.</summary>
    public void RefreshArchetypes()
    {
        _archetypeOptions = null;
        _raceOptions = null;
        OnPropertyChanged(nameof(RaceOptions));
        OnPropertyChanged(nameof(Race));
        OnPropertyChanged(nameof(ArchetypeOptions));
        OnPropertyChanged(nameof(Archetype));
        OnPropertyChanged(nameof(Archetype2));
        OnPropertyChanged(nameof(Summary));
    }
}
