using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"This card itself" in the pickers of general blocks: stats, positions, costs, protection and replacements.</summary>
public class SelfPickerEngineTests
{
    private const long Id = 100080001;

    private static BlockInstance Self(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        b.Filters["cards"] = new CardFilter { ThisCard = true };
        if (BlockRegistry.Find(id)!.Param("target") is not null) b.Values["target"] = "false";
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Card(params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = Id, Name = "Self Test", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    [Fact]
    public void Sentences_SayThisCard()
    {
        var p = Card(
            new EffectEvent { When = new BlockInstance("when.ignition"), Do = [Self("do.change_stats", ("duration", "always")), Self("do.change_position").Linked(ActionLink.And), Self("do.battle_rule", ("rule", "direct")).Linked(ActionLink.And)] },
            new EffectEvent { When = new BlockInstance("when.ignition"), Cost = [Self("cost.tribute")], Do = [new BlockInstance("do.draw")] },
            new EffectEvent { When = new BlockInstance("when.ignition").With("zone", "graveyard"), Cost = [Self("cost.move_cards")], Do = [new BlockInstance("do.draw")] },
            new EffectEvent { When = new BlockInstance("when.while_face_up"), Do = [Self("passive.protect_cards"), Self("passive.boost_cards"), Self("passive.cards_rule", ("rule", "unaffected"))] },
            new EffectEvent { When = new BlockInstance("when.while_face_up"), Do = [Self("passive.replace_destroy", ("how", "banish"), ("by", "effect")), Self("passive.attack_targets")] });
        var text = EffectTextGenerator.Generate(p);
        Assert.Contains("You can activate this effect; this card gains 500 ATK, and change this card to face-up Defense Position, and this card can attack directly this turn.", text);
        Assert.Contains("You can Tribute this card; draw 1 card.", text);
        Assert.Contains("You can banish this card from your GY; draw 1 card.", text);
        Assert.Contains("This card cannot be destroyed by battle", text);
        Assert.Contains("this card gains 500 ATK", text);
        Assert.Contains("this card is unaffected by your opponent's card effects", text);
        Assert.Contains("If this card would be destroyed by card effect, you can banish this card instead", text);
        Assert.Contains("your opponent cannot target this card for attacks", text);
    }

    [EngineFact]
    public void Actions_ChangeThisCard()
    {
        var p = Card(new EffectEvent { When = new BlockInstance("when.ignition"), Do = [Self("do.change_stats", ("duration", "always")), Self("do.change_position").Linked(ActionLink.And)] });
        new EngineScenario(p).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetAttack()==1500")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):IsPosition(POS_FACEUP_DEFENSE)")
            .AssertPasses();
    }

    [EngineFact]
    public void Costs_PayWithThisCard()
    {
        var tribute = Card(new EffectEvent { When = new BlockInstance("when.ignition"), Cost = [Self("cost.tribute")], Do = [new BlockInstance("do.gain_lp").With("amount", 1000)] });
        new EngineScenario(tribute).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{Id})").Check("Duel.GetLP(0)==9000").AssertPasses();

        var banish = Card(new EffectEvent { When = new BlockInstance("when.ignition").With("zone", "graveyard"), Cost = [Self("cost.move_cards")], Do = [new BlockInstance("do.gain_lp").With("amount", 500)] });
        new EngineScenario(banish).Card(Id, 0, "grave").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_REMOVED,0,1,nil,{Id})").Check("Duel.GetLP(0)==8500").AssertPasses();
    }

    [EngineFact]
    public void Protection_KeepsThisCardThroughABattle()
    {
        var p = Card(new EffectEvent { When = new BlockInstance("when.while_face_up"), Do = [Self("passive.protect_cards")] });
        var attacker = new CardProject { Card = new CardData { Id = 100080002, Name = "Big Attacker", Frame = MonsterFrame.Normal, Level = 4, Atk = 2000, Def = 1000 } };
        new EngineScenario(p, attacker).Card(Id, 0, "mzone").Card(attacker.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(attacker.Card.Id).Turns(2)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Id})").Check("Duel.GetLP(0)==7000").AssertPasses();
    }

    [EngineFact]
    public void Replacement_BanishesThisCardInsteadOfDestroyingIt()
    {
        var p = Card(new EffectEvent { When = new BlockInstance("when.while_face_up"), Do = [Self("passive.replace_destroy", ("how", "banish"), ("by", "effect"))] });
        var wipe = BlockRegistry.Find("do.destroy")!.CreateInstance().With("mode", "all");
        wipe.Filters["cards"] = new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        var raigeki = new CardProject
        {
            Card = new CardData { Id = 100080003, Name = "Wipe", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = BlockRegistry.Find("when.activate")!.CreateInstance(), Do = [wipe] }],
        };
        new EngineScenario(p, raigeki).Card(Id, 0, "mzone").Card(raigeki.Card.Id, 0, "hand").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(raigeki.Card.Id).Activate(Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_REMOVED,0,1,nil,{Id})")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_GRAVE,0,1,nil,{Warrior})").AssertPasses();
    }
}
