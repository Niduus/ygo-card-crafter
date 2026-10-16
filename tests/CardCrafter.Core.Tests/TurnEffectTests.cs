using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"For the rest of this turn, ..." on the field, on the players, and "each time ...".</summary>
public class TurnEffectTests
{
    private const long Id = 100099001;

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Card(params BlockInstance[] steps) => new()
    {
        Card = new CardData { Id = Id, Name = "Turn Test", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), Do = [.. steps] }],
    };

    [Fact]
    public void Sentences()
    {
        var text = EffectTextGenerator.Generate(Card(B(TurnFieldAction.BlockId), B(TurnPlayerRuleAction.BlockId, ("rule", "half")).Linked(ActionLink.And),
            B(TurnWatchAction.BlockId, ("event", "opp_special")).Linked(ActionLink.And)));
        Assert.Contains("for the rest of this turn, monsters you control gain 500 ATK", text);
        Assert.Contains("for the rest of this turn, any damage you take is halved", text);
        Assert.Contains("for the rest of this turn, each time your opponent Special Summons a monster(s), inflict 500 damage to your opponent", text);
        foreach (var b in BlockRegistry.All.Where(d => d.Id is TurnFieldAction.BlockId or TurnPlayerRuleAction.BlockId or TurnWatchAction.BlockId))
            foreach (var choice in b.Params.First(p => p.Type == ParamType.Choice).Choices)
            {
                var r = ScriptCompiler.Compile(Card(b.CreateInstance().With(b.Params.First(p => p.Type == ParamType.Choice).Key, choice.Value)));
                Assert.False(r.HasErrors, $"{b.Id} {choice.Value}: {string.Join("; ", r.Diagnostics)}");
                Assert.Empty(ScriptChecker.Check(r.Lua));
            }
    }

    [EngineFact]
    public void TheBoost_ReachesAMonsterSummonedLater()
    {
        // The boost ends with the turn, so a second card checks it during the turn: 1500 + 2000 ATK only if the
        // Warrior Normal Summoned after the boost gained 500 too.
        var judge = new CardProject
        {
            Card = new CardData { Id = Id + 1, Name = "Judge", Frame = MonsterFrame.Effect, Level = 4, Atk = 0, Def = 0 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), If = [B("if.total", ("amount", "3500"))], Do = [B("do.gain_lp", ("amount", "1000"))] }],
        };
        new EngineScenario(Card(B(TurnFieldAction.BlockId)), judge).Card(Id, 0, "mzone").Card(judge.Card.Id, 0, "mzone").Card(Warrior, 0, "hand")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Summon(Warrior).Activate(judge.Card.Id)
            .Check("Duel.GetLP(0)==9000").AssertPasses();
    }

    [EngineFact]
    public void NoDamage_ThenEachTimeTheOpponentTakesDamage()
    {
        var p = Card(
            B(TurnPlayerRuleAction.BlockId, ("rule", "no_damage")),
            B(TurnWatchAction.BlockId, ("event", "opp_damage"), ("then", "gain"), ("amount", "300")).Linked(ActionLink.And),
            B("do.damage", ("who", "you"), ("amount", "800")).Linked(ActionLink.Then),
            B("do.damage", ("who", "opponent"), ("amount", "500")).Linked(ActionLink.And));
        new EngineScenario(p).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Check("Duel.GetLP(0)==8300").Check("Duel.GetLP(1)==7500").AssertPasses();
    }
}

/// <summary>"If this card's ATK is higher than its original ATK" and the other comparisons on this card's numbers.</summary>
public class SelfStatConditionTests
{
    [EngineTheory]
    [InlineData("above_orig", 0, true)]
    [InlineData("below_orig", 0, false)]
    [InlineData("at_least", 1500, true)]
    [InlineData("at_least", 1600, false)]
    [InlineData("exactly", 1500, true)]
    public void AfterGainingAtk(string cmp, int amount, bool activates)
    {
        const long id = 100099101;
        var gain = new BlockInstance("do.change_self_stats").With("duration", "always");
        var check = new BlockInstance(SelfStatCondition.BlockId).With("cmp", cmp).With("amount", amount);
        var p = new CardProject
        {
            Card = new CardData { Id = id, Name = "Stat Test", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
            Effects =
            [
                new EffectEvent { When = new BlockInstance("when.ignition"), Limit = OncePerTurn.Soft, Do = [gain] },
                new EffectEvent { When = new BlockInstance("when.ignition"), If = [check], Do = [new BlockInstance("do.gain_lp").With("amount", 100)] },
            ],
        };
        Assert.StartsWith("Once per turn: You can activate this effect; this card gains 500 ATK.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(id, 2).Check(activates ? "Duel.GetLP(0)==8100" : "Duel.GetLP(0)==8000").AssertPasses();
    }
}

/// <summary>"Each time your opponent takes damage, you gain 300 LP" while this card is face-up.</summary>
public class EachTimeTests
{
    [EngineFact]
    public void AnswersEachTime_WithoutAChain()
    {
        const long id = 100099201, burner = 100099202;
        var watcher = new CardProject
        {
            Card = new CardData { Id = id, Name = "Watcher", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
            Effects = [new EffectEvent { When = new BlockInstance("when.while_face_up"), Do = [new BlockInstance(EachTimePassive.BlockId).With("event", "opp_damage").With("then", "gain").With("amount", 300)] }],
        };
        Assert.Equal("Each time your opponent takes damage, you gain 300 LP.", EffectTextGenerator.Generate(watcher));
        var burn = new CardProject
        {
            Card = new CardData { Id = burner, Name = "Burner", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), Do = [new BlockInstance("do.damage").With("amount", 500), new BlockInstance("do.damage").With("amount", 500).Linked(ActionLink.Then)] }],
        };
        new EngineScenario(watcher, burn).Card(id, 0, "mzone").Card(burner, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(burner).ExpectActivations(1).Check("Duel.GetLP(0)==8600").Check("Duel.GetLP(1)==7000").AssertPasses();
    }
}
