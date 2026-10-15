using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"Place 1 Continuous Trap from your Deck face-up in your Spell & Trap Zone", and "discard this card" found by name.</summary>
public class PlaceTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Placer(long id, BlockInstance place, string zone = "monster_zone") => new()
    {
        Card = new CardData { Id = id, Name = "Placer", Level = 4 },
        Effects = [new EffectEvent { When = B("when.ignition", ("zone", zone)), Do = [place] }],
    };

    private static readonly CardProject Continuous = new() { Card = new CardData { Id = 100060001, Name = "Lasting Spell", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous } };
    private static readonly CardProject Field = new() { Card = new CardData { Id = 100060002, Name = "A Field", Kind = CardKind.Spell, SpellKind = SpellKind.Field } };

    [EngineFact]
    public void PlacesAContinuousSpellFromTheDeck()
    {
        var p = Placer(100060010, B(PlaceAction.BlockId));
        Assert.Equal("You can place 1 Continuous Spell/Trap from your Deck face-up in your Spell & Trap Zone.", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        new EngineScenario(p, Continuous).Card(p.Card.Id, 0, "mzone").Card(Continuous.Card.Id, 0, "deck").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,1,nil,{Continuous.Card.Id})").AssertPasses();
    }

    [EngineFact]
    public void PlacesAFieldSpellInTheFieldZone()
    {
        var place = B(PlaceAction.BlockId, ("where", "fzone"));
        place.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Spell, SpellTrapKind = SpellTrapKind.Field };
        var p = Placer(100060020, place);
        Assert.Equal("You can place 1 Field Spell from your Deck face-up in your Field Zone.", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        new EngineScenario(p, Field).Card(p.Card.Id, 0, "mzone").Card(Field.Card.Id, 0, "deck").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id).Check($"Duel.GetFieldCard(0,LOCATION_FZONE,0) and Duel.GetFieldCard(0,LOCATION_FZONE,0):IsCode({Field.Card.Id})").AssertPasses();
    }

    [EngineFact]
    public void PlacesThisMonsterAsAContinuousSpell()
    {
        var place = B(PlaceAction.BlockId, ("as", "continuous_spell"));
        place.Filters["cards"] = new CardFilter { ThisCard = true };
        var p = Placer(100060030, place, "hand");
        Assert.Equal("You can place this card from your hand face-up in your Spell & Trap Zone as a Continuous Spell.", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,1,nil,{p.Card.Id})")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,nil,{p.Card.Id}):IsType(TYPE_CONTINUOUS)").AssertPasses();
    }

    [EngineFact]
    public void DiscardThisCard_AsAnAction()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100060040, Name = "Discarder", Level = 4 },
            Effects = [new EffectEvent { When = B("when.ignition", ("zone", "hand")), Do = [B("do.this_card", ("how", "discard")), B("do.draw").Linked(ActionLink.Then)] }],
        };
        Assert.Equal("You can discard this card, then draw 1 card.", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{p.Card.Id})")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").AssertPasses();
    }

    [Fact]
    public void DiscardThisCard_IsAMenuEntryOfItsOwn()
    {
        Assert.Contains(BlockRegistry.Find("cost.this_card")!.Shortcuts, s => s.Label == "Discard this card" && s.Values["how"] == "discard");
        Assert.Contains(BlockRegistry.Find("do.this_card")!.Shortcuts, s => s.Label == "Discard this card");
        var set = Assert.Single(BlockRegistry.Find("do.set")!.Shortcuts, s => s.Label.Contains("activated this turn"));
        Assert.Equal("true", set.Values["this_turn"]);
        Assert.Equal(Zone.Hand | Zone.Deck, set.Filters!["cards"]().Zones);
    }
}
