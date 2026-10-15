namespace CardCrafter.Core.Model;

public enum CardKind { Monster, Spell, Trap }

/// <summary>The frame of a monster card. Decides its card-type bits and which deck it lives in.</summary>
public enum MonsterFrame { Normal, Effect, Ritual, Fusion, Synchro, Xyz, Link }

public enum SpellKind { Normal, QuickPlay, Continuous, Equip, Field, Ritual }

public enum TrapKind { Normal, Continuous, Counter }

/// <summary>
/// The card pool, stored as "ot". YGO Omega's deck editor filters by it: custom cards belong in Custom,
/// and the game treats values below 4 as official OCG/TCG cards.
/// </summary>
public enum CardPool { Ocg = 1, Tcg = 2, OcgTcg = 3, Custom = 4 }

/// <summary>A card's status on a banlist. Cards on no banlist are Unlimited.</summary>
public enum Limitation { Unlimited = 3, SemiLimited = 2, Limited = 1, Forbidden = 0 }

/// <summary>How a monster may be Summoned, as its card text states it.</summary>
public enum SummonRule
{
    /// <summary>Normal Summon and Set as usual. Extra Deck and Ritual monsters use their usual procedure.</summary>
    Normal,
    /// <summary>"Cannot be Normal Summoned/Set. Must first be Special Summoned..." It can be revived once properly Summoned.</summary>
    SpecialSummonOnly,
    /// <summary>"Must be Special Summoned by its own procedure, and cannot be Special Summoned by other ways."</summary>
    ProcedureOnly,
}

/// <summary>Extra monster abilities that map to card-type bits.</summary>
[Flags]
public enum MonsterAbility
{
    None = 0,
    Tuner = 1 << 0,
    Flip = 1 << 1,
    Spirit = 1 << 2,
    Toon = 1 << 3,
    Union = 1 << 4,
    Gemini = 1 << 5,
    Pendulum = 1 << 6,
    SpecialSummonOnly = 1 << 7,
}

/// <summary>Monster attributes, using the engine's bit values.</summary>
public enum CardAttribute
{
    None = 0,
    Earth = 0x01,
    Water = 0x02,
    Fire = 0x04,
    Wind = 0x08,
    Light = 0x10,
    Dark = 0x20,
    Divine = 0x40,
}

/// <summary>
/// Monster Types ("races" in the engine), using the engine's bit values.
/// Packs can add their own Types with other bit values; those are stored as undefined values of this enum.
/// </summary>
public enum CardRace : long
{
    None = 0,
    Warrior = 0x1,
    Spellcaster = 0x2,
    Fairy = 0x4,
    Fiend = 0x8,
    Zombie = 0x10,
    Machine = 0x20,
    Aqua = 0x40,
    Pyro = 0x80,
    Rock = 0x100,
    WingedBeast = 0x200,
    Plant = 0x400,
    Insect = 0x800,
    Thunder = 0x1000,
    Dragon = 0x2000,
    Beast = 0x4000,
    BeastWarrior = 0x8000,
    Dinosaur = 0x10000,
    Fish = 0x20000,
    SeaSerpent = 0x40000,
    Reptile = 0x80000,
    Psychic = 0x100000,
    DivineBeast = 0x200000,
    CreatorGod = 0x400000,
    Wyrm = 0x800000,
    Cyberse = 0x1000000,
    Illusion = 0x2000000,
}

/// <summary>Link arrows, using the engine's bit values.</summary>
[Flags]
public enum LinkMarker
{
    None = 0,
    BottomLeft = 0x001,
    Bottom = 0x002,
    BottomRight = 0x004,
    Left = 0x008,
    Right = 0x020,
    TopLeft = 0x040,
    Top = 0x080,
    TopRight = 0x100,
}

public static class EnumNames
{
    private static readonly Dictionary<CardRace, string> RaceNames = new()
    {
        [CardRace.WingedBeast] = "Winged Beast",
        [CardRace.BeastWarrior] = "Beast-Warrior",
        [CardRace.SeaSerpent] = "Sea Serpent",
        [CardRace.DivineBeast] = "Divine-Beast",
        [CardRace.CreatorGod] = "Creator God",
    };

    public static bool IsStandard(CardRace race) => Enum.IsDefined(race);

    /// <summary>Every standard Type bit OR'd together; any other bit is a custom Type.</summary>
    public static readonly long StandardRaceMask = Enum.GetValues<CardRace>().Aggregate(0L, (m, r) => m | (long)r);

    public static string Display(CardRace race) =>
        RaceNames.TryGetValue(race, out var n) ? n : IsStandard(race) ? race.ToString() : $"Type 0x{(long)race:x}";

    public static string Display(CardAttribute attribute) => attribute.ToString().ToUpperInvariant();

    /// <summary>The Lua constant name for a race, e.g. RACE_WINDBEAST.</summary>
    public static string LuaConstant(CardRace race) => !IsStandard(race) ? "0x" + ((long)race).ToString("x") : race switch
    {
        CardRace.WingedBeast => "RACE_WINDBEAST",
        CardRace.BeastWarrior => "RACE_BEASTWARRIOR",
        CardRace.SeaSerpent => "RACE_SEASERPENT",
        CardRace.Psychic => "RACE_PSYCHO",
        CardRace.DivineBeast => "RACE_DIVINE",
        CardRace.CreatorGod => "RACE_CREATORGOD",
        _ => "RACE_" + race.ToString().ToUpperInvariant(),
    };

    public static string LuaConstant(CardAttribute attribute) => "ATTRIBUTE_" + attribute.ToString().ToUpperInvariant();
}
