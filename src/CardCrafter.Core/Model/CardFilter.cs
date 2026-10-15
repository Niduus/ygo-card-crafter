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

/// <summary>What kind of monster: Effect, Tuner, Fusion and so on. "Non-" kinds are the ones without it.</summary>
public enum MonsterKind
{
    Any, Effect, NonEffect, Normal, Tuner, NonTuner, Fusion, Synchro, Xyz, Link, Ritual, Pendulum,
    Flip, Toon, Spirit, Union, Gemini, Token, NonToken,
}

/// <summary>
/// "Which cards?" — the card-picking part of a block, like object picking in an event sheet.
/// Describes where to look and what a card must be to count.
/// </summary>
public sealed class CardFilter
{
    public CardOwner Owner { get; set; } = CardOwner.You;
    public Zone Zones { get; set; } = Zone.Deck;
    public FilterCardType CardType { get; set; } = FilterCardType.Any;
    /// <summary>Effect Monster, Tuner, Link Monster... Any for every monster.</summary>
    public MonsterKind MonsterKind { get; set; }
    /// <summary>One Type, or several meaning "any of them" (Dragon or Wyrm).</summary>
    public CardRace Race { get; set; }
    /// <summary>One Attribute, or several meaning "any of them" (LIGHT or EARTH).</summary>
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
    /// <summary>This exact card, not any card with its name: "Special Summon this card from your hand".</summary>
    public bool ThisCard { get; set; }
    /// <summary>"except "Card Name"": not a card with this card's name.</summary>
    public bool ExceptOwnName { get; set; }
    /// <summary>Not this card (passcode), 0 for none: "except "Dark Magician"".</summary>
    public long ExceptCode { get; set; }
    /// <summary>Only cards in the zones this Link monster's arrows point to.</summary>
    public bool PointedToByThisCard { get; set; }

    public CardFilter Clone() => (CardFilter)MemberwiseClone();

    public static CardFilter OpponentField(FilterCardType type = FilterCardType.Any) =>
        new() { Owner = CardOwner.Opponent, Zones = Zone.Field, CardType = type };
}
