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
                Label = "Trap hole", When = B("when.activate").With("timing", "summon_normal"), AlsoWhen = [B("when.activate").With("timing", "summon_special")],
                Do = [B("do.banish").With("cards", Narrow(CardFilter.OpponentField(FilterCardType.Monster)))],
            },
        },
        new()
        {
            Name = "Search on summon", LikeCard = "Elemental HERO Stratos", For = CardKind.Monster, Group = "Monsters",
            Description = "If this card is Normal or Special Summoned: add 1 monster from your Deck to your hand.",
            Create = () => new EffectEvent
            {
                Label = "Search", When = B("when.summoned").With("how", "normal"), AlsoWhen = [B("when.summoned").With("how", "special")], Limit = OncePerTurn.Hard,
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
                Label = "Trap hole", When = B("when.activate").With("timing", "summon_normal"), AlsoWhen = [B("when.activate").With("timing", "summon_special")],
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
                Label = "Send", When = B("when.summoned").With("how", "normal"), AlsoWhen = [B("when.summoned").With("how", "special")], Limit = OncePerTurn.Hard,
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
        // Floodgates and lock-outs
        new()
        {
            Name = "Banish instead of the GY", LikeCard = "Macro Cosmos", For = CardKind.Trap, Group = "Floodgates & locks",
            Description = "A Continuous Trap: while it is face-up, any card sent to the GY is banished instead.",
            Create = () => new EffectEvent { Label = "Banished instead", When = B("when.while_face_up"), Do = [B("passive.banish_instead")] },
        },
        new()
        {
            Name = "Monsters banished instead", LikeCard = "Dimensional Fissure", For = CardKind.Spell, Group = "Floodgates & locks",
            Description = "A Continuous Spell: any monster sent to the GY is banished instead.",
            Create = () => new EffectEvent { Label = "Banished instead", When = B("when.while_face_up"), Do = [B("passive.banish_instead").With("what", "monster")] },
        },
        new()
        {
            Name = "Banish instead, from the hand", LikeCard = "Dimension Shifter", For = CardKind.Monster, Group = "Floodgates & locks",
            Description = "If you have no cards in your GY (Quick Effect): send this card from your hand to the GY; until the end of the next turn, any card sent to the GY is banished instead.",
            Create = () => new EffectEvent
            {
                Label = "Shift", When = B("when.quick").With("zone", "hand"),
                If = [B("if.no_cards").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Any })],
                Cost = [B("cost.this_card").With("how", "grave")],
                Do = [B("do.banish_instead").With("duration", "next")],
            },
        },
        new()
        {
            Name = "Spells can't be activated", LikeCard = "Spell Canceller", For = CardKind.Monster, Group = "Floodgates & locks",
            Description = "Spell Cards, and their effects on the field, cannot be activated. Negate all Spell effects on the field.",
            Create = () => new EffectEvent
            {
                Label = "Spell lock", When = B("when.while_face_up"),
                Do = [B("passive.lock").With("what", "activate_spell").With("cards", new CardFilter { CardType = FilterCardType.Monster }), B("passive.negate_all").With("what", "spell")],
            },
        },
        new()
        {
            Name = "Traps can't be activated", LikeCard = "Jinzo", For = CardKind.Monster, Group = "Floodgates & locks",
            Description = "Trap Cards, and their effects on the field, cannot be activated. Negate all Trap effects on the field.",
            Create = () => new EffectEvent
            {
                Label = "Trap lock", When = B("when.while_face_up"),
                Do = [B("passive.lock").With("what", "activate_trap").With("cards", new CardFilter { CardType = FilterCardType.Monster }), B("passive.negate_all").With("what", "trap")],
            },
        },
        new()
        {
            Name = "No Special Summons", LikeCard = "Vanity's Emptiness", For = CardKind.Trap, Group = "Floodgates & locks",
            Description = "A Continuous Trap: neither player can Special Summon monsters. Pick an exception, like WIND monsters, for a Barrier Statue.",
            Create = () => new EffectEvent
            {
                Label = "Summon lock", When = B("when.while_face_up"),
                Do = [B("passive.lock").With("what", "special").With("cards", new CardFilter { CardType = FilterCardType.Monster })],
            },
        },
        new()
        {
            Name = "No monster effects", LikeCard = "Majesty's Fiend", For = CardKind.Monster, Group = "Floodgates & locks",
            Description = "Neither player can activate monster effects.",
            Create = () => new EffectEvent
            {
                Label = "Effect lock", When = B("when.while_face_up"),
                Do = [B("passive.lock").With("what", "activate_monster").With("cards", new CardFilter { CardType = FilterCardType.Monster })],
            },
        },
        new()
        {
            Name = "No searching", LikeCard = "Mistake / Thunder King Rai-Oh", For = CardKind.Trap, Group = "Floodgates & locks",
            Description = "A Continuous Trap: neither player can add cards from their Deck to their hand except by drawing them.",
            Create = () => new EffectEvent { Label = "Search lock", When = B("when.while_face_up"), Do = [B("passive.lock").With("what", "search")] },
        },
        new()
        {
            Name = "Negate monster effects", LikeCard = "Skill Drain", For = CardKind.Trap, Group = "Floodgates & locks",
            Description = "A Continuous Trap: negate the effects of all face-up monsters on the field.",
            Create = () => new EffectEvent { Label = "Drain", When = B("when.while_face_up"), Do = [B("passive.negate_all").With("what", "monster")] },
        },
        new()
        {
            Name = "Summon itself, lock the Extra Deck", LikeCard = "Ice Ryzeal", For = CardKind.Monster, Group = "Floodgates & locks",
            Description = "Special Summon this card from your hand by sending 1 card from your hand or field to the GY. If Summoned this way, you cannot Special Summon from the Extra Deck for the rest of this turn, except Rank 4 Xyz Monsters.",
            Create = () => new EffectEvent
            {
                Label = "Summon", When = B("when.summon_procedure").With("zone", "hand"), Limit = OncePerTurn.Hard,
                Cost =
                [
                    B("cost.move_cards").With("how", "grave").With("count", 1)
                        .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Field, CardType = FilterCardType.Any, ExcludeThisCard = true }),
                    B("cost.summon_restriction").With("what", "special_extra").With("when", "rest")
                        .With("cards", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz, LevelMin = 4, LevelMax = 4 }),
                ],
            },
        },
        new()
        {
            Name = "Search, the turn you activate it", LikeCard = "modern searchers", For = CardKind.Monster, Group = "Floodgates & locks",
            Description = "If this card is Normal Summoned: add 1 monster from your Deck to your hand. You cannot Special Summon from the Extra Deck the turn you activate this effect, except Xyz Monsters.",
            Create = () => new EffectEvent
            {
                Label = "Search", When = B("when.summoned").With("how", "normal"), Limit = OncePerTurn.Hard,
                Cost = [B("cost.summon_restriction").With("what", "special_extra").With("when", "whole_turn")
                    .With("cards", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz })],
                Do = [B("do.add_to_hand").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster })],
            },
        },
        // Branching
        new()
        {
            Name = "Negate a Summon, then Summon this card", LikeCard = "hand-trap Summon negators", For = CardKind.Monster, Group = "Hand traps",
            Description = "When your opponent would Normal or Special Summon a monster(s) (Quick Effect): you can negate the Summon, and if you do, you can Special Summon this card from your hand.",
            Create = () => new EffectEvent
            {
                Label = "Negate Summon", When = B("when.quick").With("timing", "would_normal").With("zone", "hand"), AlsoWhen = [B("when.quick").With("timing", "would_special").With("zone", "hand")], Limit = OncePerTurn.Hard,
                Do = [B("do.negate_summon").With("and", "nothing"), B("do.special_summon_self").Linked(ActionLink.IfYouDo).MayDo()],
            },
        },
        new()
        {
            Name = "Activate 1 of these effects", LikeCard = "Mitsurugi Magatama / Faceless Mage", For = CardKind.Monster, Group = "Monsters",
            Description = "Activate 1 of these effects; ● Target 1 monster your opponent controls; destroy it. ● Draw 2 cards. Add more options with the \"● or instead\" link.",
            Create = () => new EffectEvent
            {
                Label = "Options", When = B("when.ignition").With("zone", "monster_zone"), Limit = OncePerTurn.Hard,
                Do =
                [
                    B("do.destroy").With("target", true).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
                    B("do.draw").With("count", 2).Linked(ActionLink.Option),
                ],
            },
        },
        // Archetype mechanics
        new()
        {
            Name = "Tribute this card instead", LikeCard = "Mitsurugi no Mikoto, Saji", For = CardKind.Monster, Group = "Archetype mechanics",
            Description = "If another Reptile monster(s) you control would be destroyed by battle or card effect, you can Tribute this card instead.",
            Create = () => new EffectEvent
            {
                Label = "Protect", When = B("when.while_face_up"), Limit = OncePerTurn.Hard,
                Do = [B(ReplaceDestroyPassive.BlockId).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Reptile, FaceUpOnly = true, ExcludeThisCard = true })],
            },
        },
        new()
        {
            Name = "Ritual Summon", LikeCard = "Mitsurugi Ritual", For = CardKind.Spell, Group = "Archetype mechanics",
            Description = "A Ritual Spell: Ritual Summon 1 Ritual Monster from your hand by Tributing monsters whose total Levels equal its Level.",
            Create = () => new EffectEvent { Label = "Ritual Summon", When = B("when.activate"), Do = [B("do.ritual_summon")] },
        },
        new()
        {
            Name = "Fusion Summon using Extra Deck monsters", LikeCard = "Odd-Eyes Fusion", For = CardKind.Spell, Group = "Archetype mechanics",
            Description = "Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters from your Extra Deck and/or field, also you cannot activate monster effects " +
                          "in the hand or GY for the rest of this turn after this card resolves, except Fusion Monsters'. Works on a Trap too, with \"If the activation " +
                          "of this card ... was negated\" for its GY effect.",
            Create = () => new EffectEvent
            {
                Label = "Fusion Summon", When = B("when.activate"), Limit = OncePerTurn.Hard,
                Do =
                [
                    B(FusionSummonAction.BlockId).With("mats", "extra_field"),
                    B("do.summon_restriction").With("what", "activate_monster_hand_gy").With("cards", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion }),
                ],
            },
        },
        new()
        {
            Name = "Opponent discards, or it's negated", LikeCard = "Ame no Murakumo no Mitsurugi", For = CardKind.Monster, Group = "Archetype mechanics",
            Description = "When your opponent activates a card or effect (Quick Effect): your opponent can discard 1 card, or that effect is negated.",
            Create = () => new EffectEvent
            {
                Label = "Toll", When = B("when.quick").With("timing", "chain_any").With("zone", "monster_zone"), Limit = OncePerTurn.Hard,
                Do = [B("do.negate_unless")],
            },
        },
        new()
        {
            Name = "Negate a summoning effect", LikeCard = "Dominus Impulse", For = CardKind.Trap, Group = "Archetype mechanics",
            Description = "When a card or effect is activated that includes an effect that Special Summons: negate that effect, then if you have a Trap in your GY, destroy that card. If activated from the hand, you cannot activate the effects of LIGHT, EARTH and WIND monsters for the rest of this Duel.",
            Create = () => new EffectEvent
            {
                Label = "Impulse", When = B("when.activate").With("timing", "chain_summons"), Limit = OncePerTurn.Hard,
                Do =
                [
                    B("do.negate").With("what", "effect"),
                    B(OnlyIfAction.BlockId).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Trap }).Linked(ActionLink.Then),
                    B("do.that_card"),
                    B("do.summon_restriction").With("duration", "duel").With("what", "activate_monster_of").With("from_hand", true)
                        .With("cards", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Light | CardAttribute.Earth | CardAttribute.Wind }),
                ],
            },
        },
        new()
        {
            Name = "Activate from the hand", LikeCard = "Infinite Impermanence / Dominus", For = CardKind.Trap, Group = "Archetype mechanics",
            Description = "If you control no cards, you can activate this card from your hand. Add it next to the Trap's own effect.",
            Create = () => new EffectEvent
            {
                Label = "From the hand", When = B("when.while_face_up"),
                If = [B("if.no_cards").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Field, CardType = FilterCardType.Any })],
                Do = [B(ActivateFromHandPassive.BlockId)],
            },
        },
        new()
        {
            Name = "Summon to the center zone", LikeCard = "Elfnote Lucina", For = CardKind.Monster, Group = "Archetype mechanics",
            Description = "You can Special Summon this card (from your hand) to your center Main Monster Zone. You can only do this once per turn.",
            Create = () => new EffectEvent { Label = "To the center", When = B(SummonProcedureTrigger.BlockId).With("to", "center"), Limit = OncePerTurn.Hard },
        },
        new()
        {
            Name = "Freeze a monster", LikeCard = "Forbidden Crown", For = CardKind.Spell, Group = "Archetype mechanics",
            Description = "Apply these effects to 1 face-up monster on the field until the end of this turn: negate its effects, it cannot attack, be destroyed, be Tributed or be used as material, and is unaffected by activated effects. Neither player can respond with monster effects. Untick any you don't want.",
            Create = () => new EffectEvent
            {
                Label = "Freeze", When = B("when.activate"), Limit = OncePerTurn.Hard,
                Cost = [B("cost.chain_limit")],
                Do = [B("do.apply_effects")],
            },
        },
        new()
        {
            Name = "Reveal the opponent's hand", LikeCard = "Mind Scan / Mind on Air", For = CardKind.Spell, Group = "Archetype mechanics",
            Description = "A Continuous Spell: your opponent must keep their hand revealed.",
            Create = () => new EffectEvent { Label = "Reveal", When = B("when.while_face_up"), Do = [B("passive.reveal_hand")] },
        },
        new()
        {
            Name = "Link Summon on the opponent's turn", LikeCard = "I:P Masquerena", For = CardKind.Monster, Group = "Extra Deck",
            Description = "During your opponent's Main Phase (Quick Effect): You can Link Summon 1 Link Monster using materials you control, including this card. Change Link to Synchro or Xyz for the same with other Summons.",
            Create = () => new EffectEvent
            {
                Label = "Quick Link", When = B("when.quick").With("timing", "main_phase").With("zone", "monster_zone"), Limit = OncePerTurn.Hard,
                If = [B("if.turn").With("whose", "opponent")],
                Do = [B(SummonUsingThisAction.BlockId).With("kind", "link")],
            },
        },
        new()
        {
            Name = "Protect what it becomes", LikeCard = "I:P Masquerena", For = CardKind.Monster, Group = "Extra Deck",
            Description = "A Link Monster that used this card as material cannot be destroyed by your opponent's card effects. Pick another Summon or ability.",
            Create = () => new EffectEvent { Label = "Material bonus", When = B("when.while_face_up"), Do = [B(MaterialGrantPassive.BlockId)] },
        },
        new()
        {
            Name = "Banish on Link Summon with Extra Deck materials", LikeCard = "S:P Little Knight", For = CardKind.Monster, Group = "Extra Deck",
            Description = "If this card is Link Summoned using a Fusion, Synchro, Xyz, or Link Monster as material: You can target 1 card on the field or in either GY; banish it.",
            Create = () =>
            {
                var banish = B("do.banish").With("target", true);
                banish.Filters["cards"] = new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field | Zone.Graveyard, CardType = FilterCardType.Any };
                return new EffectEvent
                {
                    Label = "Banish", When = B("when.summoned").With("how", "link"), Limit = OncePerTurn.Hard,
                    If = [B(SummonedUsingCondition.BlockId)],
                    Do = [banish],
                };
            },
        },
        new()
        {
            Name = "Banish 2 monsters until the End Phase", LikeCard = "S:P Little Knight", For = CardKind.Monster, Group = "Extra Deck",
            Description = "When your opponent activates a card or effect (Quick Effect): You can target 2 face-up monsters on the field; banish them until the End Phase.",
            Create = () =>
            {
                var banish = B("do.banish").With("count", 2).With("target", true).With("until", "end_phase");
                banish.Filters["cards"] = new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };
                return new EffectEvent
                {
                    Label = "Banish 2", When = B("when.quick").With("timing", "chain_any").With("zone", "monster_zone"), Limit = OncePerTurn.Hard, SharedLimitGroup = 0,
                    Do = [banish],
                };
            },
        },
        new()
        {
            Name = "Synchro Summon on the opponent's turn", LikeCard = "Formula Synchron", For = CardKind.Monster, Group = "Extra Deck",
            Description = "During your opponent's Main Phase (Quick Effect): You can Synchro Summon using this card you control. Works best on a Tuner.",
            Create = () => new EffectEvent
            {
                Label = "Quick Synchro", When = B("when.quick").With("timing", "main_phase").With("zone", "monster_zone"), Limit = OncePerTurn.Hard,
                If = [B("if.turn").With("whose", "opponent")],
                Do = [B(SummonUsingThisAction.BlockId).With("kind", "synchro")],
            },
        },
        // Summons by effect, Rank-Ups and Pendulums (SummonMechanics.Extra.cs).
        new()
        {
            Name = "Xyz Summon by effect", LikeCard = "Wonder Xyz", For = CardKind.Spell, Group = "Extra Deck",
            Description = "Immediately after this effect resolves, Xyz Summon 1 Xyz Monster using monsters you control as material. Change Xyz to Synchro or Link for the same with other Summons.",
            Create = () => new EffectEvent { Label = "Xyz Summon", When = B("when.activate"), Do = [B(ExtraDeckSummonAction.BlockId).With("kind", "xyz")] },
        },
        new()
        {
            Name = "Rank-Up-Magic", LikeCard = "Rank-Up-Magic Limited Barian's Force", For = CardKind.Spell, Group = "Extra Deck",
            Description = "Target 1 Xyz Monster you control; Special Summon from your Extra Deck, 1 Xyz Monster that is 1 Rank higher than that monster you control, by using it as material.",
            Create = () => new EffectEvent { Label = "Rank-Up", When = B("when.activate"), Do = [B(RankUpAction.BlockId)] },
        },
        new()
        {
            Name = "Place in the Pendulum Zone when destroyed", LikeCard = "Supreme King Z-ARC", For = CardKind.Monster, Group = "Pendulum",
            Description = "If this card in the Monster Zone is destroyed: You can place this card in your Pendulum Zone. For a Pendulum Monster.",
            Create = () =>
            {
                var place = B(PlaceAction.BlockId).With("where", "pzone");
                place.Filters["cards"] = new CardFilter { ThisCard = true };
                return new EffectEvent { Label = "Pendulum", When = B("when.destroyed").With("by", "in_mzone"), Do = [place] };
            },
        },
        new()
        {
            Name = "Change this card's Pendulum Scale", LikeCard = "Performapal Parrotrio", For = CardKind.Monster, Group = "Pendulum",
            Description = "Once per turn: You can make this card's Pendulum Scale become 7 until the end of this turn. A Pendulum Effect, used in the Pendulum Zone.",
            Create = () => new EffectEvent
            {
                Label = "Scale", When = B("when.ignition").With("zone", "pendulum_zone"), Limit = OncePerTurn.Soft,
                Do = [B(PendulumScaleAction.BlockId).With("amount", 7)],
            },
        },
        new()
        {
            Name = "Special Summon from the Pendulum Zone", LikeCard = "Abyss Actor - Extras", For = CardKind.Monster, Group = "Pendulum",
            Description = "If your opponent controls a monster: You can Special Summon this card from your Pendulum Zone. A Pendulum Effect, used in the Pendulum Zone.",
            Create = () =>
            {
                var theirs = B("if.cards_exist");
                theirs.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
                return new EffectEvent
                {
                    Label = "Special Summon", When = B("when.ignition").With("zone", "pendulum_zone"), Limit = OncePerTurn.Hard,
                    If = [theirs], Do = [B("do.special_summon_self")],
                };
            },
        },
        // ---------------------------------------------------------------- treated as (TreatedAs.cs)
        new()
        {
            Name = "Banish from the GY: Fusion Summon treated as", LikeCard = "Absolute Axon Kicker", For = CardKind.Monster, Group = "Extra Deck",
            Description = "You can banish this card from your GY; Special Summon 1 Fusion Monster from your Extra Deck. (This is treated as a Fusion Summon.) Change Fusion to Synchro, Xyz or Link for the others.",
            Create = () => new EffectEvent
            {
                Label = "Fusion", When = B("when.ignition").With("zone", "graveyard"), Limit = OncePerTurn.Hard,
                Cost = [B("cost.this_card").With("how", "banish")],
                Do = [B("do.special_summon").With("as", "fusion").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion })],
            },
        },
        new()
        {
            Name = "Tribute this card: Synchro Summon treated as", LikeCard = "Bramble Rose Dragon", For = CardKind.Monster, Group = "Extra Deck",
            Description = "During the Main Phase (Quick Effect): You can Tribute this card; Special Summon 1 Synchro Monster from your Extra Deck. (This is treated as a Synchro Summon.)",
            Create = () => new EffectEvent
            {
                Label = "Synchro", When = B("when.quick").With("timing", "main_phase").With("zone", "monster_zone"), Limit = OncePerTurn.Hard,
                Cost = [B("cost.this_card").With("how", "tribute")],
                Do = [B("do.special_summon").With("as", "synchro").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Synchro })],
            },
        },
        new()
        {
            Name = "Banish from the hand or GY: Ritual Summon treated as", LikeCard = "Infernoble Knight Ricciardetto", For = CardKind.Monster, Group = "Monsters",
            Description = "You can banish this card from your hand or GY; Special Summon 1 Ritual Monster from your hand or Deck. (This is treated as a Ritual Summon.)",
            Create = () => new EffectEvent
            {
                Label = "Ritual", When = B("when.ignition").With("zone", "hand_or_graveyard"), Limit = OncePerTurn.Hard,
                Cost = [B("cost.this_card").With("how", "banish")],
                Do = [B("do.special_summon").With("as", "ritual").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Ritual })],
            },
        },
        new()
        {
            Name = "Another Ritual Monster treated as a Ritual Summon", LikeCard = "Conductor of Nephthys", For = CardKind.Monster, Group = "Monsters",
            Description = "If this card is Ritual Summoned: You can Special Summon 1 Ritual Monster from your hand or Deck, except one with this card's name. (This is treated as a Ritual Summon.) For a Ritual Monster.",
            Create = () => new EffectEvent
            {
                Label = "Ritual", When = B("when.summoned").With("how", "ritual"), Limit = OncePerTurn.Hard,
                Do = [B("do.special_summon").With("as", "ritual").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Ritual, ExceptOwnName = true })],
            },
        },
        new()
        {
            Name = "Always treated as an archetype", LikeCard = "Junoldo the Shadespirit Power Patron", For = CardKind.Monster, Group = "Archetype mechanics",
            Description = "(This card is always treated as a \"...\" card.) Pick the archetype in the card picker: it goes in the card's database entry, as official cards have it.",
            Create = () => new EffectEvent { Label = "Treated as", When = B("when.while_face_up"), Do = [B(TreatedAsPassive.BlockId).With("as", new CardFilter { CardType = FilterCardType.Any, Archetype = 0x8 })] },
        },
        new()
        {
            Name = "Treated as a Normal Monster in the hand and GY", LikeCard = "Dragon Spirit of White", For = CardKind.Monster, Group = "Monsters",
            Description = "This card is treated as a Normal Monster while in the hand or GY. Tick other places on the block for others.",
            Create = () => new EffectEvent
            {
                Label = "Normal Monster", When = B("when.while_face_up"),
                Do = [B(TreatedAsPassive.BlockId).With("how", "instead").With("where", TreatedAsPlaces.Choice).With("in_hand", true).With("in_gy", true)
                    .With("as", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Normal })],
            },
        },
        new()
        {
            Name = "Xyz Level 3 or 5", LikeCard = "Lantern Shark", For = CardKind.Monster, Group = "Extra Deck",
            Description = "If this card is used for the Xyz Summon of a WATER monster, it can be treated as a Level 3 or 5 monster.",
            Create = () => new EffectEvent
            {
                Label = "Xyz Level", When = B("when.while_face_up"),
                Do = [B(MaterialTreatedAsPassive.BlockId).With("kind", "xyz_level").With("level", 3).With("level2", 5).With("for", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Water })],
            },
        },
        new()
        {
            Name = "Fusion substitute", LikeCard = "King of the Swamp", For = CardKind.Monster, Group = "Extra Deck",
            Description = "This card can be used as a substitute for any 1 Fusion Material whose name is specifically listed on the Fusion Monster Card, but the other Fusion Material(s) must be correct.",
            Create = () => new EffectEvent { Label = "Substitute", When = B("when.while_face_up"), Do = [B(MaterialTreatedAsPassive.BlockId).With("kind", "fusion_substitute").With("where", "any")] },
        },
    ];

    private static CardFilter Narrow(CardFilter f)
    {
        f.Zones = Zone.MonsterZone;
        return f;
    }
}
