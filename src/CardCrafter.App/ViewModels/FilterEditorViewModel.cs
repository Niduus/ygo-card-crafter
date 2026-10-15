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

    public FilterEditorViewModel(CardFilter filter, Zone allowedZones, bool materialMode, Func<CardPack> pack, Action changed)
    {
        _f = filter;
        _pack = pack;
        _changed = changed;
        AllowedZones = allowedZones;
        MaterialMode = materialMode;
    }

    public Zone AllowedZones { get; }
    /// <summary>Summon materials: only "what the card is" matters, not where it is.</summary>
    public bool MaterialMode { get; }
    public bool ShowWhere => !MaterialMode && (AllowedZones == Zone.None || System.Numerics.BitOperations.PopCount((uint)AllowedZones) > 1);
    public bool ShowOwner => !MaterialMode && AllowedZones == Zone.None;

    public string Summary => MaterialMode
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

    public IReadOnlyList<Option<CardAttribute>> AttributeOptions => Lists.AttributesOrAny;
    public Option<CardAttribute> Attribute
    {
        get => Options.Find(Lists.AttributesOrAny, _f.Attribute);
        set { if (value is null || Equals(_f.Attribute, value.Value)) return; _f.Attribute = value.Value; Changed(); }
    }

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
