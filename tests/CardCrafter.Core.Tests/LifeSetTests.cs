using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"Pay half your LP", "your opponent's LP become half", and an Equip Card's live value.</summary>
public class LifeSetTests
{
    private const long Id = 100098001;

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    [EngineFact]
    public void PayHalf_ThenHalveTheOpponent()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = Id, Name = "Halver", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), Cost = [B("cost.pay_lp", ("how", "half"))], Do = [B(SetLifeAction.BlockId)] }],
        };
        Assert.Contains("You can pay half your LP; halve your opponent's LP.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1).Check("Duel.GetLP(0)==4000").Check("Duel.GetLP(1)==4000").AssertPasses();
    }

    [EngineFact]
    public void LpBecomeANumber_AndTheOthersLp()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = Id, Name = "Setter", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), Do = [B(SetLifeAction.BlockId, ("who", "you"), ("how", "value"), ("amount", "1000")), B(SetLifeAction.BlockId, ("how", "other")).Linked(ActionLink.Then)] }],
        };
        Assert.Contains("your LP become 1000, then your opponent's LP become equal to yours", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Check("Duel.GetLP(0)==1000").Check("Duel.GetLP(1)==1000").AssertPasses();
    }

    [Fact]
    public void EquipValue_Reads()
    {
        var b = B(EquipValuePassive.BlockId, ("how", "per"), ("amount", "300"), ("value", "count"));
        var p = new CardProject
        {
            Card = new CardData { Id = Id, Name = "Gear", Kind = CardKind.Spell, SpellKind = SpellKind.Equip },
            Effects = [new EffectEvent { When = new BlockInstance("when.while_face_up"), Do = [b] }],
        };
        Assert.Contains("The equipped monster gains 300 ATK for each card in your hand.", EffectTextGenerator.Generate(p));
    }

    [EngineFact]
    public void EquipValue_InADuel()
    {
        var b = B(EquipValuePassive.BlockId, ("how", "per"), ("amount", "300"), ("value", "count"));
        var gear = new CardProject
        {
            Card = new CardData { Id = Id, Name = "Gear", Kind = CardKind.Spell, SpellKind = SpellKind.Equip },
            EquipFilter = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster },
            Effects = [new EffectEvent { When = new BlockInstance("when.while_face_up"), Do = [b] }],
        };
        // 2 Fiends left in the hand after the Equip Spell is activated: +600.
        new EngineScenario(gear).Card(Id, 0, "hand").Card(Fiend, 0, "hand", 2).Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Warrior}):GetAttack()==2100").AssertPasses();
    }
}
