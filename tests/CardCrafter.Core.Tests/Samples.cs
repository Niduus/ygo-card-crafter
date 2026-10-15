using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;

namespace CardCrafter.Core.Tests;

/// <summary>Example cards built the way a user would build them on the event sheet.</summary>
public static class Samples
{
    public static CardProject PotOfGreedClone() => new()
    {
        Card = new CardData { Id = 100000001, Name = "Jar of Avarice", Kind = CardKind.Spell, SpellKind = SpellKind.Normal },
        Effects =
        [
            new EffectEvent
            {
                Label = "Draw 2",
                When = new BlockInstance("when.activate"),
                Do = [new BlockInstance("do.draw").With("who", "you").With("count", 2)],
            },
        ],
    };

    /// <summary>A monster with a search on summon, a GY quick effect, and a lasting ATK boost.</summary>
    public static CardProject DragonCommander() => new()
    {
        Card = new CardData
        {
            Id = 100000002, Name = "Dragon Commander", Kind = CardKind.Monster, Frame = MonsterFrame.Effect,
            Attribute = CardAttribute.Light, Race = CardRace.Dragon, Level = 4, Atk = 1700, Def = 1200,
        },
        Effects =
        [
            new EffectEvent
            {
                Label = "Search a Dragon",
                When = new BlockInstance("when.summoned").With("how", "normal").With("optional", true),
                AlsoWhen = [new BlockInstance("when.summoned").With("how", "special").With("optional", true)],
                Do =
                [
                    new BlockInstance("do.add_to_hand").With("count", 1)
                        .With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Dragon, LevelMax = 4 }),
                ],
                Limit = OncePerTurn.Hard,
            },
            new EffectEvent
            {
                Label = "Destroy from GY",
                When = new BlockInstance("when.quick").With("zone", "graveyard"),
                If = [new BlockInstance("if.turn").With("whose", "opponent")],
                Cost = [new BlockInstance("cost.this_card").With("how", "banish")],
                Do =
                [
                    new BlockInstance("do.destroy").With("count", 1).With("target", true)
                        .With("cards", CardFilter.OpponentField(FilterCardType.Monster)),
                    new BlockInstance("do.damage").With("amount", 500).With("who", "opponent").Linked(ActionLink.IfYouDo),
                ],
                Limit = OncePerTurn.Hard,
            },
            new EffectEvent
            {
                Label = "Dragon boost",
                When = new BlockInstance("when.while_face_up"),
                If = [new BlockInstance("if.cards_exist").With("count", 2)
                    .With("cards", new CardFilter { Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Dragon, FaceUpOnly = true })],
                Do = [new BlockInstance("passive.boost_cards")
                    .With("cards", new CardFilter { Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Dragon })
                    .With("amount", 300)],
            },
        ],
    };

    public static CardPack Pack(params CardProject[] cards) => new() { Name = "Test Pack", Cards = [.. cards] };

    /// <summary>A card that suits a block: Spell for "activate", Flip monster for FLIP, monster otherwise.</summary>
    public static CardData CardFor(BlockDefinition trigger) => trigger.Id switch
    {
        "when.activate" => new CardData { Id = 100000010, Name = "Test Spell", Kind = CardKind.Spell, SpellKind = SpellKind.QuickPlay },
        "when.flip" => new CardData { Id = 100000011, Name = "Test Flip", Abilities = MonsterAbility.Flip },
        _ => new CardData { Id = 100000012, Name = "Test Monster" },
    };
}
