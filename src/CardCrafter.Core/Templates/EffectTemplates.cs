using CardCrafter.Core.Blocks;
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
            Name = "Free summon from hand", LikeCard = "Cyber Dragon", For = CardKind.Monster, Group = "Summon procedures",
            Description = "If your opponent controls more monsters, you can Special Summon this card from your hand. It doesn't start a chain.",
            Create = () => new EffectEvent
            {
                Label = "Special Summon", When = B(SummonProcedureTrigger.BlockId),
                If = [B("if.opponent_more_monsters")],
            },
        },
        new()
        {
            Name = "Banish from GY to summon", LikeCard = "Chaos Sorcerer", For = CardKind.Monster, Group = "Summon procedures",
            Description = "Special Summon this card from your hand by banishing 2 monsters from your GY. Set the card to \"Can't be Normal Summoned\".",
            Create = () => new EffectEvent
            {
                Label = "Special Summon", When = B(SummonProcedureTrigger.BlockId),
                Cost = [B("cost.move_cards").With("how", "banish").With("count", 2)
                    .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster })],
            },
        },
        new()
        {
            Name = "Tribute to summon", LikeCard = "Gate Guardian", For = CardKind.Monster, Group = "Summon procedures",
            Description = "Special Summon this card from your hand by Tributing 1 monster, once per turn.",
            Create = () => new EffectEvent
            {
                Label = "Special Summon", When = B(SummonProcedureTrigger.BlockId), Limit = OncePerTurn.Hard,
                Cost = [B("cost.tribute")],
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
            Name = "Negate an activation", LikeCard = "Ash Blossom & Joyous Spring", For = CardKind.Monster, Group = "Hand traps",
            Description = "When your opponent activates a card or effect: discard this card; negate the activation, and if you do, destroy that card.",
            Create = () => new EffectEvent
            {
                Label = "Negate", When = B("when.quick").With("timing", "chain_any").With("zone", "hand"), Limit = OncePerTurn.Hard,
                Cost = [B("cost.this_card").With("how", "discard")],
                Do = [B("do.negate").With("what", "activation").With("and", "destroy")],
            },
        },
        new()
        {
            Name = "Negate a monster's effects", LikeCard = "Effect Veiler", For = CardKind.Monster, Group = "Hand traps",
            Description = "(Quick Effect) Discard this card, then target 1 face-up monster your opponent controls; negate its effects until the end of this turn.",
            Create = () => new EffectEvent
            {
                Label = "Veil", When = B("when.quick").With("zone", "hand"),
                Cost = [B("cost.this_card").With("how", "discard")],
                Do = [B("do.negate_effects").With("target", true)],
            },
        },
        new()
        {
            Name = "Link support", LikeCard = "Link Arrow boosters", For = CardKind.Monster, Group = "Extra Deck",
            Description = "A Link monster: monsters this card points to gain 500 ATK and can't be destroyed by battle.",
            Create = () => new EffectEvent
            {
                Label = "Support", When = B("when.while_face_up"),
                Do =
                [
                    B("passive.boost_cards").With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, PointedToByThisCard = true }),
                    B("passive.protect_cards").With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, PointedToByThisCard = true }),
                ],
            },
        },
        new()
        {
            Name = "Detach to destroy", LikeCard = "Rank 4 Xyz removal", For = CardKind.Monster, Group = "Extra Deck",
            Description = "An Xyz monster: detach 1 material, then target 1 card your opponent controls; destroy it.",
            Create = () => new EffectEvent
            {
                Label = "Detach", When = B("when.ignition"), Limit = OncePerTurn.Soft,
                Cost = [B("cost.detach")],
                Do = [B("do.destroy").With("target", true)],
            },
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
        // ---- more Spells
        new()
        {
            Name = "Heal 1000", LikeCard = "Dian Keto the Cure Master", For = CardKind.Spell, Group = "Spells",
            Description = "Gain 1000 LP.",
            Create = () => new EffectEvent { Label = "Heal", When = B("when.activate"), Do = [B("do.gain_lp").With("amount", 1000)] },
        },
        new()
        {
            Name = "Draw 1, opponent gains LP", LikeCard = "Upstart Goblin", For = CardKind.Spell, Group = "Spells",
            Description = "Draw 1 card, then your opponent gains 1000 LP.",
            Create = () => new EffectEvent
            {
                Label = "Draw", When = B("when.activate"),
                Do = [B("do.draw").With("count", 1), B("do.gain_lp").With("who", "opponent").With("amount", 1000).Linked(ActionLink.Then)],
            },
        },
        new()
        {
            Name = "Search a Warrior", LikeCard = "Reinforcement of the Army", For = CardKind.Spell, Group = "Spells",
            Description = "Add 1 Level 4 or lower Warrior monster from your Deck to your hand.",
            Create = () => new EffectEvent
            {
                Label = "Search", When = B("when.activate"), Limit = OncePerTurn.Hard,
                Do = [B("do.add_to_hand").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Warrior, LevelMax = 4 })],
            },
        },
        new()
        {
            Name = "Search a Spell", LikeCard = "Terraforming-style searchers", For = CardKind.Spell, Group = "Spells",
            Description = "Add 1 Spell from your Deck to your hand.",
            Create = () => new EffectEvent
            {
                Label = "Search", When = B("when.activate"), Limit = OncePerTurn.Hard,
                Do = [B("do.add_to_hand").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Spell })],
            },
        },
        new()
        {
            Name = "Send to GY from Deck", LikeCard = "Foolish Burial", For = CardKind.Spell, Group = "Spells",
            Description = "Send 1 monster from your Deck to the GY.",
            Create = () => new EffectEvent
            {
                Label = "Send", When = B("when.activate"), Limit = OncePerTurn.Hard,
                Do = [B("do.send_to_gy").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster })],
            },
        },
        new()
        {
            Name = "Weaken a monster", LikeCard = "Forbidden Lance (ATK part)", For = CardKind.Spell, Group = "Spells",
            Description = "Target 1 face-up monster your opponent controls; it loses 800 ATK until the end of this turn. Best on a Quick-Play Spell.",
            Create = () => new EffectEvent
            {
                Label = "Weaken", When = B("when.activate"),
                Do = [B("do.change_stats").With("target", true).With("dir", "lose").With("amount", 800)
                    .With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true })],
            },
        },
        new()
        {
            Name = "Destroy an opponent's card", LikeCard = "single-target removal", For = CardKind.Spell, Group = "Spells",
            Description = "Target 1 card your opponent controls; destroy it.",
            Create = () => new EffectEvent
            {
                Label = "Destroy", When = B("when.activate"), Limit = OncePerTurn.Hard,
                Do = [B("do.destroy").With("target", true).With("cards", CardFilter.OpponentField())],
            },
        },
        new()
        {
            Name = "Mill 3", LikeCard = "Deck-to-GY engines", For = CardKind.Spell, Group = "Spells",
            Description = "Send the top 3 cards of your Deck to the GY.",
            Create = () => new EffectEvent { Label = "Mill", When = B("when.activate"), Do = [B("do.mill").With("count", 3)] },
        },
        // ---- more Traps
        new()
        {
            Name = "Destroy a summoned monster", LikeCard = "Trap Hole", For = CardKind.Trap, Group = "Traps",
            Description = "When your opponent summons a monster: destroy 1 monster with 1000 or more ATK they control.",
            Create = () => new EffectEvent
            {
                Label = "Trap hole", When = B("when.activate").With("timing", "summon"),
                Do = [B("do.destroy").With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, AtkMin = 1000 })],
            },
        },
        new()
        {
            Name = "Bounce a monster", LikeCard = "Compulsory Evacuation Device", For = CardKind.Trap, Group = "Traps",
            Description = "Target 1 monster on the field; return it to the hand.",
            Create = () => new EffectEvent
            {
                Label = "Bounce", When = B("when.activate"),
                Do = [B("do.return_to_hand").With("target", true).With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
            },
        },
        new()
        {
            Name = "Revive from your GY", LikeCard = "Call of the Haunted (simplified)", For = CardKind.Trap, Group = "Traps",
            Description = "Target 1 monster in your GY; Special Summon it in Attack Position.",
            Create = () => new EffectEvent
            {
                Label = "Revive", When = B("when.activate"),
                Do = [B("do.special_summon").With("target", true).With("position", "attack").With("cards", new CardFilter { Zones = Zone.Graveyard, CardType = FilterCardType.Monster })],
            },
        },
        new()
        {
            Name = "Discard to destroy", LikeCard = "Raigeki Break", For = CardKind.Trap, Group = "Traps",
            Description = "Discard 1 card, then target 1 card on the field; destroy it.",
            Create = () => new EffectEvent
            {
                Label = "Destroy", When = B("when.activate"),
                Cost = [B("cost.discard")],
                Do = [B("do.destroy").With("target", true).With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field, ExcludeThisCard = true })],
            },
        },
        new()
        {
            Name = "Punish an attack", LikeCard = "attack-burn Traps", For = CardKind.Trap, Group = "Traps",
            Description = "When an opponent's monster declares an attack: inflict 1000 damage to your opponent.",
            Create = () => new EffectEvent
            {
                Label = "Burn", When = B("when.activate").With("timing", "attack"),
                Do = [B("do.damage").With("amount", 1000)],
            },
        },
        // ---- more Monsters
        new()
        {
            Name = "Search when sent to the GY", LikeCard = "Sangan", For = CardKind.Monster, Group = "Monsters",
            Description = "If this card is sent from the field to the GY: add 1 monster with 1500 or less ATK from your Deck to your hand.",
            Create = () => new EffectEvent
            {
                Label = "Search", When = B("when.sent_to_gy").With("from", "field"), Limit = OncePerTurn.Hard,
                Do = [B("do.add_to_hand").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster, AtkMax = 1500 })],
            },
        },
        new()
        {
            Name = "Send to GY on summon", LikeCard = "Armageddon Knight", For = CardKind.Monster, Group = "Monsters",
            Description = "If this card is Summoned: send 1 monster from your Deck to the GY.",
            Create = () => new EffectEvent
            {
                Label = "Send", When = B("when.summoned").With("how", "normal_or_special"), Limit = OncePerTurn.Hard,
                Do = [B("do.send_to_gy").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster })],
            },
        },
        new()
        {
            Name = "Recruiter", LikeCard = "Mystic Tomato", For = CardKind.Monster, Group = "Monsters",
            Description = "If this card is destroyed by battle: Special Summon 1 monster with 1500 or less ATK from your Deck.",
            Create = () => new EffectEvent
            {
                Label = "Recruit", When = B("when.destroyed").With("by", "battle"),
                Do = [B("do.special_summon").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster, AtkMax = 1500 })],
            },
        },
        new()
        {
            Name = "Tribute to destroy", LikeCard = "Exiled Force", For = CardKind.Monster, Group = "Monsters",
            Description = "Tribute this card, then target 1 monster on the field; destroy it.",
            Create = () => new EffectEvent
            {
                Label = "Destroy", When = B("when.ignition").With("zone", "monster_zone"),
                Cost = [B("cost.this_card").With("how", "tribute")],
                Do = [B("do.destroy").With("target", true).With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
            },
        },
        new()
        {
            Name = "Pay LP for ATK", LikeCard = "LP-for-power monsters", For = CardKind.Monster, Group = "Monsters",
            Description = "Pay 1000 LP; this card gains 1000 ATK until the end of this turn.",
            Create = () => new EffectEvent
            {
                Label = "Power up", When = B("when.ignition").With("zone", "monster_zone"), Limit = OncePerTurn.Soft,
                Cost = [B("cost.pay_lp").With("amount", 1000)],
                Do = [B("do.change_self_stats").With("amount", 1000)],
            },
        },
        new()
        {
            Name = "Burn after battle", LikeCard = "battle-burn monsters", For = CardKind.Monster, Group = "Monsters",
            Description = "If this card destroys an opponent's monster by battle: inflict 500 damage to your opponent.",
            Create = () => new EffectEvent { Label = "Burn", When = B("when.destroys_by_battle"), Do = [B("do.damage").With("amount", 500)] },
        },
        new()
        {
            Name = "Piercing damage", LikeCard = "Spear Dragon", For = CardKind.Monster, Group = "Monsters",
            Description = "If this card attacks a Defense Position monster, it inflicts piercing battle damage.",
            Create = () => new EffectEvent { Label = "Pierce", When = B("when.while_face_up"), Do = [B("passive.battle_ability").With("ability", "pierce")] },
        },
        new()
        {
            Name = "Direct attacker", LikeCard = "Jinzo #7", For = CardKind.Monster, Group = "Monsters",
            Description = "This card can attack directly.",
            Create = () => new EffectEvent { Label = "Direct attack", When = B("when.while_face_up"), Do = [B("passive.battle_ability").With("ability", "direct")] },
        },
        new()
        {
            Name = "Untargetable", LikeCard = "untargetable bosses", For = CardKind.Monster, Group = "Monsters",
            Description = "Your opponent cannot target this card with card effects.",
            Create = () => new EffectEvent { Label = "Can't be targeted", When = B("when.while_face_up"), Do = [B("passive.untargetable")] },
        },
        new()
        {
            Name = "FLIP: destroy", LikeCard = "Man-Eater Bug", For = CardKind.Monster, Group = "Monsters",
            Description = "FLIP: Target 1 monster on the field; destroy it. Tick the Flip ability on the Card tab.",
            Create = () => new EffectEvent
            {
                Label = "Flip", When = B("when.flip"),
                Do = [B("do.destroy").With("target", true).With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
            },
        },
        new()
        {
            Name = "Mill for ATK", LikeCard = "Card Trooper", For = CardKind.Monster, Group = "Monsters",
            Description = "Once per turn: send the top 3 cards of your Deck to the GY, and if you do, this card gains 1500 ATK until the end of this turn.",
            Create = () => new EffectEvent
            {
                Label = "Mill", When = B("when.ignition").With("zone", "monster_zone"), Limit = OncePerTurn.Soft,
                Do = [B("do.mill").With("count", 3), B("do.change_self_stats").With("amount", 1500).Linked(ActionLink.IfYouDo)],
            },
        },
    ];

    private static CardFilter Narrow(CardFilter f)
    {
        f.Zones = Zone.MonsterZone;
        return f;
    }
}
