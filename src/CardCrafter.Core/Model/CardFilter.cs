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
    /// <summary>The Pendulum Zones, part of the Spell & Trap Zones: "a card in your Pendulum Zone".</summary>
    PendulumZone = 0x80,
    Field = MonsterZone | SpellTrapZone,
}

public enum FilterCardType { Any, Monster, Spell, Trap, SpellTrap }

/// <summary>A battle position a monster must be in.</summary>
public enum FilterPosition { Any, Attack, Defense, FaceUpAttack, FaceUpDefense, FaceDownDefense }

/// <summary>What kind of Spell or Trap: "Continuous Spell", "Equip Spell", "Field Spell"...</summary>
public enum SpellTrapKind { Any, Normal, Continuous, QuickPlay, Field, Equip, Ritual, Counter }

/// <summary>How a monster on the field was Summoned: "Special Summoned monster", "monster Special Summoned from the Extra Deck".</summary>
public enum FilterSummoned { Any, Special, FromExtra, Tribute, ThisTurn }

/// <summary>What kind of monster: Effect, Tuner, Fusion and so on. "Non-" kinds are the ones without it.</summary>
/// <summary>
/// "That card": the picker means cards the effect already has instead of cards it picks, as card text says "it",
/// "the targeted monster", "the sent monster" or "that monster".
/// </summary>
public enum CardRef
{
    None,
    /// <summary>This effect's target(s), still related to it.</summary>
    Target,
    /// <summary>The card(s) the DO step before affected (Duel.GetOperatedGroup()).</summary>
    Affected,
    /// <summary>The card(s) used for the effect's cost.</summary>
    Cost,
    /// <summary>The monster this card is battling, or destroyed by battle (GetBattleTarget).</summary>
    BattleTarget,
    /// <summary>Your monster that is battling (Duel.GetBattleMonster).</summary>
    Battling,
    /// <summary>The monster this card is equipped to (GetEquipTarget): "the equipped monster".</summary>
    Equipped,
    /// <summary>The cards whose event started this effect (eg): "1 of those Link Monsters".</summary>
    Event,
}

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
    /// <summary>A second archetype, 0 for none: ""Dominators" or "Dominus Purge" Traps".</summary>
    public int Archetype2 { get; set; }
    /// <summary>A specific card's passcode, 0 for any.</summary>
    public long CardCode { get; set; }
    public int? LevelMin { get; set; }
    public int? LevelMax { get; set; }
    public int? AtkMin { get; set; }
    public int? AtkMax { get; set; }
    public int? DefMin { get; set; }
    public int? DefMax { get; set; }
    /// <summary>More names it may have instead of <see cref="CardCode"/>: ""Dark Magician" or "Dark Magician Girl"".</summary>
    public long[] AltCodes { get; set; } = [];
    /// <summary>A card it mentions in its text (passcode), 0 for none: "a card that mentions "Dark Magician"".</summary>
    public long MentionsCode { get; set; }
    /// <summary>With <see cref="MentionsCode"/>: that card itself also counts ("X", or a card that mentions it).</summary>
    public bool MentionsOrIs { get; set; }
    public FilterPosition Position { get; set; }
    public SpellTrapKind SpellTrapKind { get; set; }
    public FilterSummoned Summoned { get; set; }
    /// <summary>Only cards in the hand that aren't revealed already: "reveal 1 card in your hand".</summary>
    public bool NotRevealed { get; set; }
    public bool FaceUpOnly { get; set; }
    /// <summary>Only face-down cards: "Face-down cards in your Spell & Trap Zone".</summary>
    public bool FaceDownOnly { get; set; }
    /// <summary>
    /// With <see cref="FilterCardType.Monster"/>: "Monster Cards", which also counts monsters placed in the Spell &amp; Trap
    /// Zone as Continuous Spells (what the card is printed as, not what it is treated as now).
    /// </summary>
    public bool MonsterCards { get; set; }
    public bool ExcludeThisCard { get; set; }
    /// <summary>This exact card, not any card with its name: "Special Summon this card from your hand".</summary>
    public bool ThisCard { get; set; }
    /// <summary>"That card": cards the effect already has (its target, the card the step before affected...), or None for picked cards.</summary>
    public CardRef That { get; set; }
    /// <summary>"except "Card Name"": not a card with this card's name.</summary>
    public bool ExceptOwnName { get; set; }
    /// <summary>Not this card (passcode), 0 for none: "except "Dark Magician"".</summary>
    public long ExceptCode { get; set; }
    /// <summary>Only cards in the zones this Link monster's arrows point to.</summary>
    public bool PointedToByThisCard { get; set; }
    /// <summary>Only the monster in the center Main Monster Zone (Elfnote).</summary>
    public bool CenterZone { get; set; }
    /// <summary>Further requirements, each a property compared with a value: "original ATK 2000 or more", "not DARK".</summary>
    public List<CardRequirement> Requirements { get; set; } = [];
    /// <summary>Any one of <see cref="Requirements"/> is enough ("a DARK monster or a Dragon monster"); otherwise all must hold.</summary>
    public bool AnyRequirement { get; set; }

    /// <summary>The cards are given, not picked: this card itself, or "that card" (<see cref="That"/>).</summary>
    public bool IsSpecific() => ThisCard || That != CardRef.None;

    public CardFilter Clone()
    {
        var copy = (CardFilter)MemberwiseClone();
        copy.AltCodes = [.. AltCodes];
        copy.Requirements = Requirements.Select(r => r.Clone()).ToList();
        return copy;
    }

    public static CardFilter OpponentField(FilterCardType type = FilterCardType.Any) =>
        new() { Owner = CardOwner.Opponent, Zones = Zone.Field, CardType = type };
}
