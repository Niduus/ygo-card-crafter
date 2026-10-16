using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"If you have less LP than your opponent: You can Special Summon this card from your GY."</summary>
public class LowLpReviveTests
{
    private const long Id = 100104001, Payer = 100104002;

    private static CardProject Revive() => new()
    {
        Card = new CardData { Id = Id, Name = "Undying", Frame = MonsterFrame.Effect, Level = 4, Atk = 0, Def = 0 },
        Effects =
        [
            new EffectEvent
            {
                When = new BlockInstance("when.ignition").With("zone", "graveyard"),
                If = [new BlockInstance("if.lp_compare").With("cmp", "less")],
                Do = [new BlockInstance("do.special_summon_self")],
            },
        ],
    };

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyWhileYourLpAreLower(bool lower)
    {
        var payer = new CardProject
        {
            Card = new CardData { Id = Payer, Name = "Payer", Frame = MonsterFrame.Effect, Level = 4, Atk = 0, Def = 0 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), Cost = [new BlockInstance("cost.pay_lp").With("amount", 1000)], Do = [new BlockInstance("do.draw")] }],
        };
        Assert.Equal("If you have less LP than your opponent: You can Special Summon this card from your GY.", EffectTextGenerator.Generate(Revive()));
        var s = new EngineScenario(Revive(), payer).Card(Id, 0, "grave").Card(Payer, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4);
        if (lower) s.Activate(Payer);
        s.Activate(Id)
            .Check(lower ? $"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Id})" : $"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{Id})")
            .AssertPasses();
    }

    [EngineTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARitualMonster_NeedsToIgnoreItsSummoningConditions(bool ignore)
    {
        var p = Revive();
        p.Card.Frame = MonsterFrame.Ritual;
        p.Effects[0].If.Clear();
        p.Effects[0].Do[0] = new BlockInstance("do.special_summon_self").With("ignore", ignore);
        var compiled = Compiler.ScriptCompiler.Compile(p);
        Assert.Equal(!ignore, compiled.Diagnostics.Any(d => d.Message.Contains("properly Summoned first", StringComparison.Ordinal)));
        if (ignore) Assert.EndsWith("Special Summon this card from your GY, ignoring its Summoning conditions.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(Id, 0, "grave").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id)
            .Check(ignore ? $"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Id})" : $"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{Id})")
            .AssertPasses();
    }
}
