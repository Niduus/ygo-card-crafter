using System.Text.Json.Serialization;

namespace CardCrafter.Core.Combos;

/// <summary>
/// A combo written down play by play: the deck it uses, the hand it starts from, and each step. The board is never
/// stored; it is worked out by replaying the steps (<see cref="ComboReplay"/>), so changing an early step updates the rest.
/// </summary>
public sealed class Combo
{
    public string Name { get; set; } = "New combo";
    /// <summary>What the combo is for, what it needs, what it plays around.</summary>
    public string Notes { get; set; } = "";
    public ComboDeck Deck { get; set; } = new();
    /// <summary>The passcodes of the cards in the opening hand, each one taken from the Main Deck.</summary>
    public List<long> StartingHand { get; set; } = [];
    /// <summary>
    /// Practice: the hand is <see cref="HandSize"/> cards drawn at random from the Main Deck (with <see cref="Seed"/>, so the
    /// same hand comes back until it is drawn again) instead of <see cref="StartingHand"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RandomHand { get; set; }
    public int HandSize { get; set; } = 5;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Seed { get; set; }
    public List<ComboStep> Steps { get; set; } = [];
    /// <summary>
    /// The names of the cards it uses, as they were when the deck was made, so the combo still reads right where those
    /// cards aren't installed (a shared file, or custom cards from another pack).
    /// </summary>
    public Dictionary<long, string> Names { get; set; } = [];
}

/// <summary>A deck as passcodes, one entry per copy, like a .ydk file.</summary>
public sealed class ComboDeck
{
    public List<long> Main { get; set; } = [];
    public List<long> Extra { get; set; } = [];
    public List<long> Side { get; set; } = [];

    [JsonIgnore] public bool IsEmpty => Main.Count == 0 && Extra.Count == 0 && Side.Count == 0;
    /// <summary>Main and Extra Deck: the cards that can be played.</summary>
    [JsonIgnore] public IEnumerable<long> Playable => Main.Concat(Extra);
}

/// <summary>What a step does.</summary>
public enum ComboStepKind
{
    NormalSummon, TributeSummon, Set, SpecialSummon,
    FusionSummon, SynchroSummon, XyzSummon, LinkSummon, RitualSummon,
    ActivateCard, ActivateEffect,
    AddToHand, Draw, SendToGraveyard, Banish, ReturnToHand, ReturnToDeck, ReturnToExtraDeck,
    Place, Attach, Detach, ChangePosition,
    Interruption, EndTurn, Note,
}

/// <summary>
/// Where a card is or goes. <see cref="Auto"/> means "where it is" for a card's starting place and "where this kind of step
/// puts it" for its destination. <see cref="Field"/> is any zone on your field, <see cref="Token"/> a Token made by the step.
/// </summary>
public enum ComboZone
{
    Auto, Hand, Deck, ExtraDeck, MonsterZone, ExtraMonsterZone, SpellTrapZone, PendulumZone, FieldZone,
    Graveyard, Banished, XyzMaterial, Field, Token,
}

/// <summary>How a card sits. <see cref="Auto"/> is what the step usually does (Summons in Attack Position, Set face-down...).</summary>
public enum ComboPosition { Auto, Attack, Defense, FaceDownDefense, FaceUp, FaceDown }

/// <summary>One card a step moves along with its main card: a material, a Tribute, a cost or a card an effect moved.</summary>
public sealed class ComboMove
{
    public long Code { get; set; }
    /// <summary>Which copy of the card (1 = the first in the deck list); 0 takes any copy that is in <see cref="From"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Copy { get; set; }
    public ComboZone From { get; set; }
    public ComboZone To { get; set; }
    /// <summary>The zone for a card going to the field, 1 to 5 from the left (1 or 2 for the Extra Monster Zones); 0 picks the first free one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Slot { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ComboPosition Position { get; set; }
}

/// <summary>One play of a combo.</summary>
public sealed class ComboStep
{
    public ComboStepKind Kind { get; set; }
    /// <summary>The card the step is about (0 for an end of turn or a note).</summary>
    public long Code { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Copy { get; set; }
    public ComboZone From { get; set; }
    public ComboZone To { get; set; }
    /// <summary>The zone it goes to, 1 to 5 from the left (1 or 2 for the Extra Monster Zones); 0 picks the first free one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Slot { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ComboPosition Position { get; set; }
    /// <summary>
    /// The cards moved with it: materials and Tributes (to the GY, or under an Xyz Monster), and for an activated effect its
    /// costs and what it moved. For Attach and Detach, the materials put under or taken from the card.
    /// </summary>
    public List<ComboMove> Materials { get; set; } = [];
    /// <summary>Which effect of the card's text was used (1-based, as <see cref="CardEffects.Split"/> numbers them); 0 for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Effect { get; set; }
    /// <summary>The player's explanation: why this play, what it sets up.</summary>
    public string Note { get; set; } = "";
}
