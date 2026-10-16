using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>ATK/DEF/Level, damage and LP equal to live values: LP difference, counts, totals, the monster's own Level...</summary>
public class ValueBlocksTests
{
    private const long Id = 100097001;

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Card(EffectEvent effect, int atk = 1000) => new()
    {
        Card = new CardData { Id = Id, Name = "Value Test", Frame = MonsterFrame.Effect, Level = 4, Atk = atk, Def = 1000 },
        Effects = [effect],
    };

    private static EffectEvent Lasting(BlockInstance b) => new() { When = new BlockInstance("when.while_face_up"), Do = [b] };
    private static EffectEvent Ignition(BlockInstance b) => new() { When = new BlockInstance("when.ignition"), Do = [b] };

    public static IEnumerable<object[]> Sentences()
    {
        yield return [B(StatsFromValuePassive.BlockId), "This card gains ATK equal to the difference between both players' LP."];
        yield return [B(StatsFromValuePassive.BlockId, ("how", "per"), ("value", "count")), "This card gains 300 ATK for each card in your hand."];
        yield return [B(StatsFromValuePassive.BlockId, ("dir", "becomes"), ("value", "your_lp"), ("how", "half")), "This card's ATK becomes equal to half your LP."];
        yield return [B(StatsFromValuePassive.BlockId, ("stat", "level"), ("value", "its_materials")), "This card's Level increases by the number of materials attached to it."];
        yield return [B(StatsFromValuePassive.BlockId, ("dir", "lose"), ("stat", "both"), ("how", "per"), ("amount", "100"), ("value", "its_counters")), "This card loses 100 ATK/DEF for each counter on it."];
    }

    [Theory]
    [MemberData(nameof(Sentences))]
    public void Lasting_Reads(BlockInstance b, string text)
    {
        var p = Card(Lasting(b));
        Assert.Equal(text, EffectTextGenerator.Generate(p));
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics));
        Assert.Empty(ScriptChecker.Check(r.Lua));
    }

    [Fact]
    public void Actions_Read()
    {
        var gy = B(LifeFromValueAction.BlockId, ("how", "per"), ("amount", "200"), ("value", "count"));
        gy.Filters[ValueSources.CardsKey] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Graveyard };
        Assert.Contains("inflict 200 damage to your opponent for each card in your opponent's GY", EffectTextGenerator.Generate(Card(Ignition(gy))));
        var heal = B(LifeFromValueAction.BlockId, ("what", "gain"), ("who", "you"), ("value", "lp_diff"), ("how", "half"));
        Assert.Contains("gain LP equal to half the difference between both players' LP", EffectTextGenerator.Generate(Card(Ignition(heal))));
        var boost = B(StatsFromValueAction.BlockId, ("value", "its_orig_atk"), ("how", "half"), ("target", "true"));
        Assert.Contains("target 1 face-up monster you control; it gains ATK equal to half its original ATK until the end of this turn", EffectTextGenerator.Generate(Card(Ignition(boost))));
    }

    [EngineFact]
    public void LpDifference_KeepsTheAtkUpToDate()
    {
        // The opponent burns themselves? No: this card's controller pays 2000 LP, so the difference becomes 2000.
        var passive = Lasting(B(StatsFromValuePassive.BlockId));
        var pay = new EffectEvent { When = new BlockInstance("when.ignition"), Cost = [B("cost.pay_lp", ("amount", "2000"))], Do = [new BlockInstance("do.draw")] };
        var p = Card(passive);
        p.Effects.Add(pay);
        new EngineScenario(p).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetAttack()==3000").AssertPasses();
    }

    [EngineFact]
    public void BurnForEachCard_AndHalfOriginalAtk()
    {
        var burn = B(LifeFromValueAction.BlockId, ("how", "per"), ("amount", "500"), ("value", "count"));
        burn.Filters[ValueSources.CardsKey] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard };
        new EngineScenario(Card(Ignition(burn))).Card(Id, 0, "mzone").Card(Warrior, 0, "grave", 3).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1).Check("Duel.GetLP(1)==6500").AssertPasses();

        var boost = B(StatsFromValueAction.BlockId, ("value", "its_orig_atk"), ("how", "half"), ("duration", "always"));
        boost.Filters["cards"] = new CardFilter { ThisCard = true };
        new EngineScenario(Card(Ignition(boost), atk: 1800)).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetAttack()==2700").AssertPasses();
    }

    [EngineFact]
    public void FieldMonstersGainForEachCardInHand_AndLevelBecomes()
    {
        var field = B(StatsFromValuePassive.BlockId, ("how", "per"), ("amount", "100"), ("value", "count"));
        field.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        var level = B(StatsFromValuePassive.BlockId, ("stat", "level"), ("dir", "becomes"), ("value", "count"));
        level.Filters[ValueSources.CardsKey] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard };
        var p = Card(Lasting(field));
        p.Effects.Add(Lasting(level));
        new EngineScenario(p).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Fiend, 0, "hand", 3).Card(Warrior, 0, "grave", 7).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Warrior}):GetAttack()==1800")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetLevel()==7").AssertPasses();
    }
}
