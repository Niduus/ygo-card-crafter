using CardCrafter.Core.Model;

namespace CardCrafter.Core.Templates;

/// <summary>A ready-made effect modelled on a well-known card, inserted as normal editable blocks.</summary>
public sealed class EffectTemplate
{
    public required string Name { get; init; }
    public required string LikeCard { get; init; }
    public required string Description { get; init; }
    /// <summary>Which kind of card it suits. The app warns when it is added to another kind.</summary>
    public required CardKind For { get; init; }
    public required string Group { get; init; }
    public required Func<EffectEvent> Create { get; init; }
}

/// <summary>
/// The template library: the common effect shapes people otherwise copy out of official scripts.
/// Every template is built from regular blocks, so users can see how it works and change any part.
/// </summary>
public static class EffectTemplates
{
    private static BlockInstance B(string id) => Blocks.BlockRegistry.Find(id)!.CreateInstance();

    public static IReadOnlyList<EffectTemplate> All { get; } =
    [
        new()
        {
            Name = "Draw 2", LikeCard = "Pot of Greed", For = CardKind.Spell, Group = "Spells",
            Description = "Activate: draw 2 cards.",
            Create = () => new EffectEvent { Label = "Draw 2", When = B("when.activate"), Do = [B("do.draw").With("count", 2)] },
        },
        new()
        {
            Name = "Revive a monster", LikeCard = "Monster Reborn", For = CardKind.Spell, Group = "Spells",
            Description = "Target 1 monster in either GY; Special Summon it.",
            Create = () => new EffectEvent
            {
                Label = "Revive", When = B("when.activate"), Limit = OncePerTurn.Hard,
                Do = [B("do.special_summon").With("target", true)
                    .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.Graveyard, CardType = FilterCardType.Monster })],
            },
        },
        new()
        {
            Name = "Destroy a Spell/Trap", LikeCard = "Mystical Space Typhoon", For = CardKind.Spell, Group = "Spells",
            Description = "Target 1 Spell/Trap on the field; destroy it. Best on a Quick-Play Spell.",
            Create = () => new EffectEvent
            {
                Label = "Destroy S/T", When = B("when.activate"),
                Do = [B("do.destroy").With("target", true)
                    .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.SpellTrapZone, CardType = FilterCardType.Any, ExcludeThisCard = true })],
            },
        },
        new()
        {
            Name = "Burn 800", LikeCard = "Ookazi", For = CardKind.Spell, Group = "Spells",
            Description = "Inflict 800 damage to your opponent.",
            Create = () => new EffectEvent { Label = "Burn", When = B("when.activate"), Do = [B("do.damage").With("amount", 800)] },
        },
        new()
        {
            Name = "Recycle and draw", LikeCard = "Pot of Avarice", For = CardKind.Spell, Group = "Spells",
            Description = "Shuffle 5 monsters from your GY into the Deck, then draw 2 cards.",
            Create = () => new EffectEvent
            {
                Label = "Recycle", When = B("when.activate"), Limit = OncePerTurn.Hard,
                Do =
                [
                    B("do.shuffle_into_deck").With("count", 5).With("target", true)
                        .With("cards", new CardFilter { Zones = Zone.Graveyard, CardType = FilterCardType.Monster }),
                    B("do.draw").With("count", 2).Linked(ActionLink.Then),
                ],
            },
        },
        new()
        {
            Name = "Type boost field", LikeCard = "Mountain", For = CardKind.Spell, Group = "Spells",
            Description = "A Field Spell: monsters of one Type on the field gain 200 ATK.",
            Create = () => new EffectEvent
            {
                Label = "Boost", When = B("when.while_face_up"),
                Do = [B("passive.boost_cards").With("amount", 200)
                    .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Dragon })],
            },
        },
        new()
        {
            Name = "Destroy an attacker", LikeCard = "Mirror Force (single)", For = CardKind.Trap, Group = "Traps",
            Description = "When an opponent's monster declares an attack: destroy 1 monster your opponent controls.",
            Create = () => new EffectEvent
            {
                Label = "Counterattack", When = B("when.activate").With("timing", "attack"),
                Do = [B("do.destroy").With("cards", Narrow(CardFilter.OpponentField(FilterCardType.Monster)))],
            },
        },
        new()
        {
            Name = "Banish on summon", LikeCard = "Bottomless Trap Hole", For = CardKind.Trap, Group = "Traps",
            Description = "When your opponent summons a monster: banish 1 monster they control.",
            Create = () => new EffectEvent
            {
                Label = "Trap hole", When = B("when.activate").With("timing", "summon"),
                Do = [B("do.banish").With("cards", Narrow(CardFilter.OpponentField(FilterCardType.Monster)))],
            },
        },
        new()
        {
            Name = "Search on summon", LikeCard = "Elemental HERO Stratos", For = CardKind.Monster, Group = "Monsters",
            Description = "If this card is Normal or Special Summoned: add 1 monster from your Deck to your hand.",
            Create = () => new EffectEvent
            {
                Label = "Search", When = B("when.summoned").With("how", "normal_or_special"), Limit = OncePerTurn.Hard,
                Do = [B("do.add_to_hand").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster })],
            },
        },
        new()
        {
            Name = "Floater", LikeCard = "Sangan / floaters", For = CardKind.Monster, Group = "Monsters",
            Description = "If this card is destroyed: Special Summon 1 Level 4 or lower monster from your Deck.",
            Create = () => new EffectEvent
            {
                Label = "Float", When = B("when.destroyed"), Limit = OncePerTurn.Hard,
                Do = [B("do.special_summon").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster, LevelMax = 4 })],
            },
        },
        new()
        {
            Name = "Free summon from hand", LikeCard = "Cyber Dragon", For = CardKind.Monster, Group = "Monsters",
            Description = "If your opponent controls more monsters: Special Summon this card from your hand.",
            Create = () => new EffectEvent
            {
                Label = "Summon itself", When = B("when.ignition").With("zone", "hand"), Limit = OncePerTurn.Hard,
                If = [B("if.opponent_more_monsters")],
                Do = [B("do.special_summon_self")],
            },
        },
        new()
        {
            Name = "Revive itself from GY", LikeCard = "GY self-revivers", For = CardKind.Monster, Group = "Monsters",
            Description = "Banish 1 other card from your GY; Special Summon this card from your GY.",
            Create = () => new EffectEvent
            {
                Label = "Revive itself", When = B("when.ignition").With("zone", "graveyard"), Limit = OncePerTurn.Hard,
                Cost = [B("cost.move_cards").With("how", "banish").With("cards", new CardFilter { Zones = Zone.Graveyard, ExcludeThisCard = true })],
                Do = [B("do.special_summon_self").With("position", "defense")],
            },
        },
        new()
        {
            Name = "Discard to search", LikeCard = "hand-starter monsters", For = CardKind.Monster, Group = "Monsters",
            Description = "Discard this card; add 1 Spell from your Deck to your hand.",
            Create = () => new EffectEvent
            {
                Label = "Discard search", When = B("when.ignition").With("zone", "hand"), Limit = OncePerTurn.Hard,
                Cost = [B("cost.this_card").With("how", "discard")],
                Do = [B("do.add_to_hand").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Spell })],
            },
        },
        new()
        {
            Name = "Quick banish from GY", LikeCard = "Crossout-style disruption", For = CardKind.Monster, Group = "Monsters",
            Description = "(Quick Effect) Target 1 card in your opponent's GY; banish it.",
            Create = () => new EffectEvent
            {
                Label = "Banish", When = B("when.quick"), Limit = OncePerTurn.Hard,
                Do = [B("do.banish").With("target", true).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Graveyard })],
            },
        },
        new()
        {
            Name = "Battle protection", LikeCard = "Marshmallon", For = CardKind.Monster, Group = "Monsters",
            Description = "This card cannot be destroyed by battle.",
            Create = () => new EffectEvent { Label = "Protection", When = B("when.while_face_up"), Do = [B("passive.indestructible")] },
        },
        new()
        {
            Name = "Standby heal", LikeCard = "Solemn Wishes", For = CardKind.Spell, Group = "Spells",
            Description = "During your Standby Phase: gain 500 LP. Put it on a Continuous Spell.",
            Create = () => new EffectEvent
            {
                Label = "Heal", When = B("when.phase").With("whose", "your").With("phase", "standby").With("optional", false),
                Do = [B("do.gain_lp").With("amount", 500)],
            },
        },
    ];

    private static CardFilter Narrow(CardFilter f)
    {
        f.Zones = Zone.MonsterZone;
        return f;
    }
}
