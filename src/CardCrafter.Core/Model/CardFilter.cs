namespace CardCrafter.Core.Model;

public enum CardOwner { You, Opponent, Either }

[Flags]
public enum Zone
{
    None = 0,
    Deck = 0x01,
    Hand = 0x02,
    MonsterZone = 0x04,
    SpellTrapZone = 0x08,
    Graveyard = 0x10,
    Banished = 0x20,
    ExtraDeck = 0x40,
    Field = MonsterZone | SpellTrapZone,
}

public enum FilterCardType { Any, Monster, Spell, Trap }

/// <summary>
/// "Which cards?" — the card-picking part of a block, like object picking in an event sheet.
/// Describes where to look and what a card must be to count.
/// </summary>
public sealed class CardFilter
{
    public CardOwner Owner { get; set; } = CardOwner.You;
    public Zone Zones { get; set; } = Zone.Deck;
    public FilterCardType CardType { get; set; } = FilterCardType.Any;
    public CardRace Race { get; set; }
    public CardAttribute Attribute { get; set; }
    /// <summary>Archetype code, 0 for any.</summary>
    public int Archetype { get; set; }
    /// <summary>A specific card's passcode, 0 for any.</summary>
    public long CardCode { get; set; }
    public int? LevelMin { get; set; }
    public int? LevelMax { get; set; }
    public int? AtkMin { get; set; }
    public int? AtkMax { get; set; }
    public bool FaceUpOnly { get; set; }
    public bool ExcludeThisCard { get; set; }

    public CardFilter Clone() => (CardFilter)MemberwiseClone();

    public static CardFilter OpponentField(FilterCardType type = FilterCardType.Any) =>
        new() { Owner = CardOwner.Opponent, Zones = Zone.Field, CardType = type };
}
