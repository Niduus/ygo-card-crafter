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

    public FilterEditorViewModel(CardFilter filter, Zone allowedZones, bool materialMode, Func<CardPack> pack, Action changed, bool ownerChoice = false, bool allowThisCard = false)
    {
        AllowThisCard = allowThisCard;
        _f = filter;
        _pack = pack;
        _changed = changed;
        AllowedZones = allowedZones;
        MaterialMode = materialMode;
        _ownerChoice = ownerChoice;
    }

    private readonly bool _ownerChoice;

    public Zone AllowedZones { get; }
    /// <summary>Summon materials: only "what the card is" matters, not where it is.</summary>
    public bool MaterialMode { get; }
    public bool ShowWhere => !MaterialMode && (AllowedZones == Zone.None || System.Numerics.BitOperations.PopCount((uint)AllowedZones) > 1);
    public bool ShowOwner => _ownerChoice || (!MaterialMode && AllowedZones == Zone.None);

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

    public bool InHand { get => Has(Zone.Hand); set => Set(Zone.Hand, value, nameof(InHand)); }
    public bool InDeck { get => Has(Zone.Deck); set => Set(Zone.Deck, value, nameof(InDeck)); }
    public bool InExtra { get => Has(Zone.ExtraDeck); set => Set(Zone.ExtraDeck, value, nameof(InExtra)); }
    public bool InMonsterZone { get => Has(Zone.MonsterZone); set => Set(Zone.MonsterZone, value, nameof(InMonsterZone)); }
    public bool InSpellTrapZone { get => Has(Zone.SpellTrapZone); set => Set(Zone.SpellTrapZone, value, nameof(InSpellTrapZone)); }
    public bool InGraveyard { get => Has(Zone.Graveyard); set => Set(Zone.Graveyard, value, nameof(InGraveyard)); }
    public bool InBanished { get => Has(Zone.Banished); set => Set(Zone.Banished, value, nameof(InBanished)); }

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
    public IReadOnlyList<ArchetypeOption> ArchetypeOptions => _archetypeOptions ??=
        [new(0, "Any archetype"), .. _pack().Archetypes.Select(a => new ArchetypeOption(a.Code, $"{a.Name} (0x{a.Code:x})"))];

    public ArchetypeOption? Archetype
    {
        get => ArchetypeOptions.FirstOrDefault(a => a.Code == _f.Archetype) ?? ArchetypeOptions[0];
        set { if (value is null || value.Code == _f.Archetype) return; _f.Archetype = value.Code; Changed(); }
    }

    public string CardCode
    {
        get => _f.CardCode == 0 ? "" : _f.CardCode.ToString(CultureInfo.InvariantCulture);
        set
        {
            var code = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            if (code == _f.CardCode) return;
            _f.CardCode = code;
            Changed();
        }
    }

    public decimal? LevelMin { get => _f.LevelMin; set { if (_f.LevelMin == (int?)value) return; _f.LevelMin = (int?)value; Changed(); } }
    public decimal? LevelMax { get => _f.LevelMax; set { if (_f.LevelMax == (int?)value) return; _f.LevelMax = (int?)value; Changed(); } }
    public decimal? AtkMin { get => _f.AtkMin; set { if (_f.AtkMin == (int?)value) return; _f.AtkMin = (int?)value; Changed(); } }
    public decimal? AtkMax { get => _f.AtkMax; set { if (_f.AtkMax == (int?)value) return; _f.AtkMax = (int?)value; Changed(); } }
    public bool FaceUpOnly { get => _f.FaceUpOnly; set { if (_f.FaceUpOnly == value) return; _f.FaceUpOnly = value; Changed(); } }
    public bool PointedToByThisCard { get => _f.PointedToByThisCard; set { if (_f.PointedToByThisCard == value) return; _f.PointedToByThisCard = value; Changed(); } }
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
        OnPropertyChanged(nameof(Summary));
    }
}
