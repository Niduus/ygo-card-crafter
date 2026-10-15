namespace CardCrafter.Core.Model;

public enum SummonProcedureKind { None, Fusion, Synchro, Xyz, Link }

/// <summary>How an Extra Deck monster is summoned. Compiles to the engine's built-in procedures.</summary>
public sealed class SummonProcedure
{
    public SummonProcedureKind Kind { get; set; }
    /// <summary>Fusion: the exact materials by passcode. Leave empty to use <see cref="MaterialFilter"/>.</summary>
    public List<long> FusionMaterialCodes { get; set; } = [];
    /// <summary>Fusion (with no codes), Xyz, Link and Synchro non-Tuner: what a material must be.</summary>
    public CardFilter MaterialFilter { get; set; } = new() { CardType = FilterCardType.Monster };
    /// <summary>Synchro only: what the Tuner must be.</summary>
    public CardFilter TunerFilter { get; set; } = new() { CardType = FilterCardType.Monster };
    /// <summary>
    /// Fusion materials one by one, e.g. 1 "Aleister" monster + 1 LIGHT or EARTH monster. Each slot is a specific card
    /// (its filter's card code) or any card the filter allows. One slot may take more than one card ("2+ Fiend monsters").
    /// When empty, <see cref="FusionMaterialCodes"/> or <see cref="MaterialFilter"/> is used.
    /// </summary>
    public List<MaterialSlot> Materials { get; set; } = [];
    /// <summary>Xyz: the Level of the materials, 0 for the same as the Rank.</summary>
    public int XyzMaterialLevel { get; set; }
    /// <summary>Link: the materials must include at least one card <see cref="IncludeFilter"/> allows ("including a "Sky Striker" monster").</summary>
    public bool Including { get; set; }
    public CardFilter IncludeFilter { get; set; } = new() { CardType = FilterCardType.Monster };
    public int MinMaterials { get; set; } = 2;
    /// <summary>0 means "no maximum" (Synchro) or "Link Rating" (Link).</summary>
    public int MaxMaterials { get; set; }
}

/// <summary>One Fusion Material: a card, or any card that passes the filter, <see cref="Min"/> to <see cref="Max"/> of them.</summary>
public sealed class MaterialSlot
{
    public CardFilter Filter { get; set; } = new() { CardType = FilterCardType.Monster };
    public int Min { get; set; } = 1;
    /// <summary>0 means "or more" (1+).</summary>
    public int Max { get; set; } = 1;
    public bool Repeats => Min != 1 || Max != 1;
}

/// <summary>A custom card: its database data, its effects, and how its script is produced.</summary>
public sealed class CardProject
{
    public CardData Card { get; set; } = new();
    public SummonProcedure Procedure { get; set; } = new();
    public List<EffectEvent> Effects { get; set; } = [];

    /// <summary>Equip Spells: the monsters this card can be equipped to (whose, and what they must be).</summary>
    public CardFilter EquipFilter { get; set; } = new() { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };

    /// <summary>When true the hand-written <see cref="CustomLua"/> is used instead of the generated script.</summary>
    public bool UseCustomLua { get; set; }
    public string CustomLua { get; set; } = "";
    /// <summary>
    /// The database's str1..str16 for a hand-written script, which refers to them with aux.Stringid.
    /// Generated scripts use their effect labels instead.
    /// </summary>
    public List<string> ScriptStrings { get; set; } = [];

    /// <summary>Path to the card's artwork image, relative to the pack file or absolute.</summary>
    public string? ArtworkPath { get; set; }
}

/// <summary>A pack's own monster Type, such as Omega's custom Types. The value must match what the game uses.</summary>
public sealed class CustomType
{
    public long Value { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Archetype
{
    public int Code { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A pack of custom cards that is saved, shared and exported together.</summary>
public sealed class CardPack
{
    /// <summary>2: the default card pool became Custom (ot 4), which YGO Omega needs to list custom cards.</summary>
    public int FormatVersion { get; set; } = CurrentFormat;
    public const int CurrentFormat = 2;
    public string Name { get; set; } = "My Custom Cards";
    public string Author { get; set; } = "";
    /// <summary>The copyright line printed bottom right, like "©1996 KAZUKI TAKAHASHI". Empty uses "©year author".</summary>
    public string Copyright { get; set; } = "";
    /// <summary>
    /// The card pool ("ot") for the pack's cards. Custom (4) by default: YGO Omega lists custom cards under
    /// Card Pool → Custom and treats lower values as official cards.
    /// </summary>
    public int Ot { get; set; } = (int)CardPool.Custom;
    public List<Archetype> Archetypes { get; set; } = [];
    public List<CustomType> CustomTypes { get; set; } = [];
    public List<CardProject> Cards { get; set; } = [];
    /// <summary>Banlists made with this pack, for any cards: official ones or this pack's.</summary>
    public List<PackBanlist> Banlists { get; set; } = [];

    /// <summary>The display name of a Type, including this pack's custom Types.</summary>
    public string TypeName(CardRace race) =>
        CustomTypes.FirstOrDefault(t => t.Value == (long)race)?.Name is { Length: > 0 } n ? n : EnumNames.Display(race);

    public string ArchetypeName(int code) =>
        Archetypes.FirstOrDefault(a => a.Code == code)?.Name is { Length: > 0 } n ? n : Text.ArchetypeNames.Find(code) ?? $"0x{code:x}";
}
