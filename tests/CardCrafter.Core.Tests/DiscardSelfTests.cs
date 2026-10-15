using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"You can discard 1 "Sundering - Inferno"; ..." on Sundering - Inferno itself, and "discard this card".</summary>
public class DiscardSelfTests
{
    private static CardProject Sundering(CardFilter discarded)
    {
        var discard = BlockRegistry.Find("cost.discard")!.CreateInstance();
        discard.Filters["cards"] = discarded;
        var add = BlockRegistry.Find("do.add_to_hand")!.CreateInstance();
        add.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.SpellTrap, Archetype = 0x123 };
        return new CardProject
        {
            Card = new CardData { Id = 108270000, Name = "Sundering - Inferno", Level = 4, Archetypes = [0x123] },
            Effects = [new EffectEvent { When = new BlockInstance("when.quick").With("timing", "main_phase").With("zone", "hand"), Cost = [discard], Do = [add] }],
        };
    }

    private static readonly CardProject Spell = new() { Card = new CardData { Id = 108280000, Name = "Sundering Spell", Kind = CardKind.Spell, Archetypes = [0x123] } };

    [EngineTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithOneCopy_ItDiscardsItself_AndSearches(bool thisCard)
    {
        var p = Sundering(thisCard ? new CardFilter { ThisCard = true } : new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardCode = 108270000 });
        if (thisCard) Assert.Contains("You can discard this card; add 1", EffectTextGenerator.Generate(p));
        new EngineScenario(p, Spell).Card(p.Card.Id, 0, "hand").Card(Spell.Card.Id, 0, "deck").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,{Spell.Card.Id})")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{p.Card.Id})").AssertPasses();
    }

    [Fact]
    public void DiscardingAnyCard_StillLeavesTheActivatingCardOut()
    {
        var p = Sundering(new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand });
        Assert.Contains("LOCATION_HAND,0,1,e:GetHandler(),e,tp)", ScriptCompiler.Compile(p).Lua);
    }
}
