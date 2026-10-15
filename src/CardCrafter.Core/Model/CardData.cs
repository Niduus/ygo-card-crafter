namespace CardCrafter.Core.Model;

/// <summary>Everything that goes into the card database row for one card.</summary>
public sealed class CardData
{
    /// <summary>The card's passcode. Custom cards should use a 9-digit number nobody else uses.</summary>
    public long Id { get; set; } = 100000001;
    public long Alias { get; set; }
    public string Name { get; set; } = "New Card";
    public string Text { get; set; } = "";
    public string PendulumText { get; set; } = "";

    public CardKind Kind { get; set; } = CardKind.Monster;

    public MonsterFrame Frame { get; set; } = MonsterFrame.Effect;
    public MonsterAbility Abilities { get; set; }
    /// <summary>The monster can only be Special Summoned by its own procedure, never by other effects.</summary>
    public bool OnlyByOwnProcedure { get; set; }
    public CardAttribute Attribute { get; set; } = CardAttribute.Dark;
    public CardRace Race { get; set; } = CardRace.Warrior;
    public int Level { get; set; } = 4;
    /// <summary>ATK value. -2 means "?".</summary>
    public int Atk { get; set; } = 1800;
    /// <summary>DEF value. -2 means "?". Ignored for Link monsters.</summary>
    public int Def { get; set; } = 1000;
    public LinkMarker LinkMarkers { get; set; }
    public int LeftScale { get; set; }
    public int RightScale { get; set; }

    public SpellKind SpellKind { get; set; }
    public TrapKind TrapKind { get; set; }

    /// <summary>Archetype codes (setcodes), up to four.</summary>
    public List<int> Archetypes { get; set; } = [];

    // Database values Card Crafter doesn't edit, kept so opening and saving a database never loses them.
    /// <summary>The "ot" value for this card when it differs from the pack's.</summary>
    public long? Ot { get; set; }
    /// <summary>The database's search categories.</summary>
    public long Category { get; set; }
    /// <summary>Card-type bits the editor has no setting for, such as Token or Trap Monster.</summary>
    public long ExtraTypeBits { get; set; }
    /// <summary>YGO Omega's card flags, support codes, release dates (Unix seconds) and in-database script, kept as read.</summary>
    public long OmegaFlags { get; set; }
    public long Support { get; set; }
    public long? OcgDate { get; set; }
    public long? TcgDate { get; set; }
    public string? DatabaseScript { get; set; }

    /// <summary>Status on every banlist in the game. Unlimited writes nothing, like official cards that aren't on a list.</summary>
    public Limitation Limitation { get; set; } = Limitation.Unlimited;
    /// <summary>Per-banlist limits read from a database (banlist id to limit), kept when <see cref="Limitation"/> is Unlimited.</summary>
    public Dictionary<long, int> BanlistLimits { get; set; } = [];

    public bool IsMonster => Kind == CardKind.Monster;
    public bool IsLink => IsMonster && Frame == MonsterFrame.Link;
    public bool IsXyz => IsMonster && Frame == MonsterFrame.Xyz;
    public bool IsPendulum => IsMonster && Abilities.HasFlag(MonsterAbility.Pendulum);
    public bool IsExtraDeck => IsMonster && Frame is MonsterFrame.Fusion or MonsterFrame.Synchro or MonsterFrame.Xyz or MonsterFrame.Link;
    public bool IsEffectMonster => IsMonster && Frame != MonsterFrame.Normal;

    /// <summary>How the monster may be Summoned. Stored as the "Special Summon" type bit plus <see cref="OnlyByOwnProcedure"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SummonRule SummonRule
    {
        get => OnlyByOwnProcedure ? SummonRule.ProcedureOnly
            : Abilities.HasFlag(MonsterAbility.SpecialSummonOnly) ? SummonRule.SpecialSummonOnly : SummonRule.Normal;
        set
        {
            OnlyByOwnProcedure = value == SummonRule.ProcedureOnly;
            Abilities = value == SummonRule.Normal
                ? Abilities & ~MonsterAbility.SpecialSummonOnly
                : Abilities | MonsterAbility.SpecialSummonOnly;
        }
    }

    /// <summary>A main-deck monster that isn't Ritual: the kind that needs its own Special Summon procedure when it can't be Normal Summoned.</summary>
    public bool IsMainDeckNonRitual => IsMonster && !IsExtraDeck && Frame != MonsterFrame.Ritual;

    /// <summary>A Link monster's rating is its number of arrows.</summary>
    public int LinkRating => System.Numerics.BitOperations.PopCount((uint)LinkMarkers);

    /// <summary>The engine's offset for "hard once per turn" codes: 1 for official-range ids, 100 for 9-digit ids.</summary>
    public long CountCodeOffset => Id < 100000000 ? 1 : 100;
}
