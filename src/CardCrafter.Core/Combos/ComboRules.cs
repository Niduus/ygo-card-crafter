using CardCrafter.Core.Model;

namespace CardCrafter.Core.Combos;

/// <summary>What each kind of step usually does: where its card comes from and goes, and what happens to its materials.</summary>
public static class ComboRules
{
    private static readonly ComboZone[] FieldZones =
        [ComboZone.MonsterZone, ComboZone.ExtraMonsterZone, ComboZone.SpellTrapZone, ComboZone.PendulumZone, ComboZone.FieldZone];

    private static readonly ComboZone[] Anywhere =
        [.. FieldZones, ComboZone.Hand, ComboZone.Graveyard, ComboZone.Banished, ComboZone.ExtraDeck, ComboZone.Deck, ComboZone.XyzMaterial];

    /// <summary>Steps with no card of their own.</summary>
    public static bool NeedsCard(ComboStepKind kind) => kind is not (ComboStepKind.EndTurn or ComboStepKind.Note);

    /// <summary>Steps whose card isn't one of yours: the opponent's card is only named.</summary>
    public static bool MovesNothing(ComboStepKind kind) => kind is ComboStepKind.Interruption or ComboStepKind.EndTurn or ComboStepKind.Note;

    public static bool IsSummon(ComboStepKind kind) => kind is ComboStepKind.NormalSummon or ComboStepKind.TributeSummon or ComboStepKind.SpecialSummon
        or ComboStepKind.FusionSummon or ComboStepKind.SynchroSummon or ComboStepKind.XyzSummon or ComboStepKind.LinkSummon or ComboStepKind.RitualSummon;

    /// <summary>Summons whose materials (or Tributes) leave before the monster arrives, freeing their zones for it.</summary>
    public static bool MaterialsFirst(ComboStepKind kind) => IsSummon(kind) || kind == ComboStepKind.Set;

    /// <summary>Where the step looks for its card, in order, when it doesn't say.</summary>
    public static IReadOnlyList<ComboZone> SearchZones(ComboStepKind kind) => kind switch
    {
        ComboStepKind.NormalSummon or ComboStepKind.TributeSummon or ComboStepKind.Set => [ComboZone.Hand],
        ComboStepKind.SpecialSummon => [ComboZone.Hand, ComboZone.Graveyard, ComboZone.Banished, ComboZone.Deck, ComboZone.ExtraDeck,
            ComboZone.PendulumZone, ComboZone.SpellTrapZone, ComboZone.XyzMaterial],
        ComboStepKind.FusionSummon or ComboStepKind.SynchroSummon or ComboStepKind.XyzSummon or ComboStepKind.LinkSummon => [ComboZone.ExtraDeck],
        ComboStepKind.RitualSummon => [ComboZone.Hand, ComboZone.Deck, ComboZone.Graveyard, ComboZone.Banished],
        ComboStepKind.ActivateCard => [ComboZone.Hand, ComboZone.SpellTrapZone, ComboZone.FieldZone, ComboZone.Graveyard, ComboZone.Deck],
        ComboStepKind.AddToHand => [ComboZone.Deck, ComboZone.Graveyard, ComboZone.Banished, ComboZone.ExtraDeck],
        ComboStepKind.Draw => [ComboZone.Deck],
        ComboStepKind.SendToGraveyard => [.. FieldZones, ComboZone.Hand, ComboZone.Deck, ComboZone.ExtraDeck, ComboZone.XyzMaterial, ComboZone.Banished],
        ComboStepKind.Banish => [.. FieldZones, ComboZone.Graveyard, ComboZone.Hand, ComboZone.Deck, ComboZone.ExtraDeck, ComboZone.XyzMaterial],
        ComboStepKind.ReturnToHand or ComboStepKind.ReturnToExtraDeck => [.. FieldZones, ComboZone.Graveyard, ComboZone.Banished],
        ComboStepKind.ReturnToDeck => [.. FieldZones, ComboZone.Graveyard, ComboZone.Banished, ComboZone.Hand],
        ComboStepKind.Place => [ComboZone.Hand, ComboZone.Deck, ComboZone.Graveyard, ComboZone.Banished, ComboZone.ExtraDeck],
        ComboStepKind.Attach or ComboStepKind.Detach => [ComboZone.MonsterZone, ComboZone.ExtraMonsterZone],
        ComboStepKind.ChangePosition => [ComboZone.MonsterZone, ComboZone.ExtraMonsterZone, ComboZone.SpellTrapZone, ComboZone.FieldZone],
        _ => Anywhere,
    };

    /// <summary>Where the step's materials are looked for when they don't say.</summary>
    public static IReadOnlyList<ComboZone> MaterialSearchZones(ComboStepKind kind) => kind switch
    {
        ComboStepKind.Detach => [ComboZone.XyzMaterial],
        ComboStepKind.TributeSummon or ComboStepKind.SynchroSummon or ComboStepKind.XyzSummon or ComboStepKind.LinkSummon =>
            [ComboZone.MonsterZone, ComboZone.ExtraMonsterZone, ComboZone.Hand, ComboZone.Graveyard],
        _ => [ComboZone.MonsterZone, ComboZone.ExtraMonsterZone, ComboZone.Hand, ComboZone.SpellTrapZone, ComboZone.PendulumZone, ComboZone.FieldZone,
            ComboZone.Deck, ComboZone.Graveyard, ComboZone.Banished, ComboZone.ExtraDeck, ComboZone.XyzMaterial],
    };

    /// <summary>
    /// Where the step's card goes when it doesn't say: <see cref="ComboZone.Auto"/> means it stays where it is.
    /// A Normal Spell or Trap that is activated ends in the GY once it resolves; one that stays on the field stays.
    /// </summary>
    public static ComboZone DefaultTo(ComboStepKind kind, CardData? card) => kind switch
    {
        _ when IsSummon(kind) => ComboZone.MonsterZone,
        ComboStepKind.Set => card is null || card.IsMonster ? ComboZone.MonsterZone : IsFieldSpell(card) ? ComboZone.FieldZone : ComboZone.SpellTrapZone,
        ComboStepKind.ActivateCard => card is null ? ComboZone.Graveyard
            : IsFieldSpell(card) ? ComboZone.FieldZone
            : card.IsPendulum ? ComboZone.PendulumZone
            : StaysOnField(card) ? ComboZone.SpellTrapZone : ComboZone.Graveyard,
        ComboStepKind.Place => card is null ? ComboZone.SpellTrapZone : IsFieldSpell(card) ? ComboZone.FieldZone
            : card.IsPendulum ? ComboZone.PendulumZone : ComboZone.SpellTrapZone,
        ComboStepKind.AddToHand or ComboStepKind.Draw or ComboStepKind.ReturnToHand => ComboZone.Hand,
        ComboStepKind.SendToGraveyard => ComboZone.Graveyard,
        ComboStepKind.Banish => ComboZone.Banished,
        ComboStepKind.ReturnToDeck => ComboZone.Deck,
        ComboStepKind.ReturnToExtraDeck => ComboZone.ExtraDeck,
        _ => ComboZone.Auto,
    };

    /// <summary>Where a material goes when it doesn't say: under the Xyz Monster, or the GY.</summary>
    public static ComboZone MaterialDefaultTo(ComboStepKind kind) => kind switch
    {
        ComboStepKind.XyzSummon or ComboStepKind.Attach => ComboZone.XyzMaterial,
        _ => ComboZone.Graveyard,
    };

    public static bool IsFieldSpell(CardData card) => card.Kind == CardKind.Spell && card.SpellKind == SpellKind.Field;

    /// <summary>Continuous and Equip Spells and Continuous Traps stay on the field after they resolve.</summary>
    public static bool StaysOnField(CardData card) =>
        (card.Kind == CardKind.Spell && card.SpellKind is SpellKind.Continuous or SpellKind.Equip or SpellKind.Field)
        || (card.Kind == CardKind.Trap && card.TrapKind == TrapKind.Continuous);

    /// <summary>What the materials list is called on a step of this kind, or null when the kind has none.</summary>
    public static string? MaterialsLabel(ComboStepKind kind) => kind switch
    {
        ComboStepKind.TributeSummon => "Tributes",
        ComboStepKind.RitualSummon => "Tributes (materials)",
        ComboStepKind.FusionSummon or ComboStepKind.SynchroSummon or ComboStepKind.XyzSummon or ComboStepKind.LinkSummon => "Materials",
        ComboStepKind.Attach => "Cards to attach",
        ComboStepKind.Detach => "Materials detached",
        ComboStepKind.EndTurn or ComboStepKind.Note or ComboStepKind.Interruption or ComboStepKind.Draw => null,
        _ => "Also moves (costs, targets, what the effect moved)",
    };

    /// <summary>The frame a Summon of this kind needs, or null when any card will do.</summary>
    public static MonsterFrame? FrameFor(ComboStepKind kind) => kind switch
    {
        ComboStepKind.FusionSummon => MonsterFrame.Fusion,
        ComboStepKind.SynchroSummon => MonsterFrame.Synchro,
        ComboStepKind.XyzSummon => MonsterFrame.Xyz,
        ComboStepKind.LinkSummon => MonsterFrame.Link,
        ComboStepKind.RitualSummon => MonsterFrame.Ritual,
        _ => null,
    };

    /// <summary>The places a step of this kind can take its card from, for the step editor ("Auto" first).</summary>
    public static IReadOnlyList<ComboZone> FromChoices(ComboStepKind kind) =>
        [ComboZone.Auto, .. SearchZones(kind).Where(z => z != ComboZone.XyzMaterial || kind is ComboStepKind.SpecialSummon or ComboStepKind.SendToGraveyard or ComboStepKind.Banish or ComboStepKind.ActivateEffect),
         .. (kind == ComboStepKind.SpecialSummon ? new[] { ComboZone.Token } : Array.Empty<ComboZone>())];

    /// <summary>The places a step of this kind can put its card, for the step editor ("Auto" first).</summary>
    public static IReadOnlyList<ComboZone> ToChoices(ComboStepKind kind) => kind switch
    {
        _ when IsSummon(kind) => [ComboZone.Auto, ComboZone.MonsterZone, ComboZone.ExtraMonsterZone],
        ComboStepKind.Set => [ComboZone.Auto, ComboZone.MonsterZone, ComboZone.SpellTrapZone, ComboZone.FieldZone],
        ComboStepKind.ActivateCard => [ComboZone.Auto, ComboZone.Graveyard, ComboZone.SpellTrapZone, ComboZone.PendulumZone, ComboZone.FieldZone],
        ComboStepKind.Place => [ComboZone.Auto, ComboZone.SpellTrapZone, ComboZone.PendulumZone, ComboZone.FieldZone],
        ComboStepKind.ActivateEffect => [ComboZone.Auto, ComboZone.Graveyard, ComboZone.Banished, ComboZone.Hand, ComboZone.Deck, ComboZone.ExtraDeck,
            ComboZone.MonsterZone, ComboZone.ExtraMonsterZone, ComboZone.SpellTrapZone, ComboZone.PendulumZone],
        ComboStepKind.Banish or ComboStepKind.SendToGraveyard or ComboStepKind.AddToHand or ComboStepKind.Draw or ComboStepKind.ReturnToHand
            or ComboStepKind.ReturnToDeck or ComboStepKind.ReturnToExtraDeck => [ComboZone.Auto],
        _ => [ComboZone.Auto],
    };

    /// <summary>Places a material or moved card can go.</summary>
    public static IReadOnlyList<ComboZone> MaterialToChoices { get; } =
    [
        ComboZone.Auto, ComboZone.Graveyard, ComboZone.Banished, ComboZone.Hand, ComboZone.Deck, ComboZone.ExtraDeck, ComboZone.XyzMaterial,
        ComboZone.MonsterZone, ComboZone.ExtraMonsterZone, ComboZone.SpellTrapZone, ComboZone.PendulumZone, ComboZone.FieldZone,
    ];

    /// <summary>Places a material or moved card can come from.</summary>
    public static IReadOnlyList<ComboZone> MaterialFromChoices { get; } =
    [
        ComboZone.Auto, ComboZone.Field, ComboZone.MonsterZone, ComboZone.ExtraMonsterZone, ComboZone.SpellTrapZone, ComboZone.PendulumZone, ComboZone.FieldZone,
        ComboZone.Hand, ComboZone.Deck, ComboZone.ExtraDeck, ComboZone.Graveyard, ComboZone.Banished, ComboZone.XyzMaterial,
    ];

    /// <summary>Whether a card at <paramref name="actual"/> counts as being at <paramref name="wanted"/>.</summary>
    public static bool ZoneMatches(ComboZone actual, ComboZone wanted) => wanted switch
    {
        ComboZone.Field => ComboBoard.IsOnField(actual),
        ComboZone.MonsterZone => actual is ComboZone.MonsterZone or ComboZone.ExtraMonsterZone,
        ComboZone.SpellTrapZone => actual is ComboZone.SpellTrapZone or ComboZone.PendulumZone,
        _ => actual == wanted,
    };
}
