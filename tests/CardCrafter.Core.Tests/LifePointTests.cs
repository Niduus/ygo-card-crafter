using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"If you took damage this turn" and the rest of the damage and LP family, in sentences and in duels.</summary>
public class LifePointTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Monster(long id, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = $"LP Test {id}", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    [Theory]
    [InlineData("you", "did", "damage", 0, "you took damage this turn")]
    [InlineData("you", "not", "damage", 0, "you have not taken damage this turn")]
    [InlineData("opponent", "did", "battle", 0, "your opponent took battle damage this turn")]
    [InlineData("either", "not", "effect", 0, "neither player has taken effect damage this turn")]
    [InlineData("you", "did", "paid", 2000, "you paid 2000 or more LP this turn")]
    [InlineData("both", "did", "gained", 0, "both players gained LP this turn")]
    [InlineData("you", "did", "opp_effect", 0, "you took damage from an opponent's card effect this turn")]
    [InlineData("opponent", "did", "lost", 1000, "your opponent lost 1000 or more LP this turn")]
    public void Sentences(string who, string did, string what, int amount, string text)
    {
        var p = Monster(100096001, new EffectEvent
        {
            When = new BlockInstance("when.ignition"),
            If = [B(LifeThisTurnCondition.BlockId, ("who", who), ("did", did), ("what", what), ("amount", amount.ToString()))],
            Do = [new BlockInstance("do.draw")],
        });
        Assert.StartsWith("If " + text + ": ", EffectTextGenerator.Generate(p));
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics));
        Assert.Empty(ScriptChecker.Check(r.Lua));
    }

    [Fact]
    public void LpDifference_Reads() =>
        Assert.StartsWith("If the difference between your LP and your opponent's LP is 2000 or more: ",
            EffectTextGenerator.Generate(Monster(100096002, new EffectEvent { When = new BlockInstance("when.ignition"), If = [B("if.lp_difference")], Do = [new BlockInstance("do.draw")] })));

    [EngineFact]
    public void AfterPayingAndTakingDamage_TheConditionsHold()
    {
        // A pays 1000 LP, then burns its controller for 800. B gains 300 only if LP were paid; C gains 200 only after effect damage.
        var a = Monster(100096011, new EffectEvent
        {
            When = new BlockInstance("when.ignition"), Cost = [B("cost.pay_lp", ("amount", "1000"))], Do = [B("do.damage", ("who", "you"), ("amount", "800"))],
        });
        var b = Monster(100096012, new EffectEvent
        {
            When = new BlockInstance("when.ignition"), If = [B(LifeThisTurnCondition.BlockId, ("what", "paid"), ("amount", "1000"))], Do = [B("do.gain_lp", ("amount", "300"))],
        });
        var c = Monster(100096013, new EffectEvent
        {
            When = new BlockInstance("when.ignition"), If = [B(LifeThisTurnCondition.BlockId, ("what", "effect"))], Do = [B("do.gain_lp", ("amount", "200"))],
        });
        var never = Monster(100096014, new EffectEvent
        {
            When = new BlockInstance("when.ignition"), If = [B(LifeThisTurnCondition.BlockId, ("what", "battle"))], Do = [B("do.gain_lp", ("amount", "5000"))],
        });
        new EngineScenario(a, b, c, never).Card(a.Card.Id, 0, "mzone").Card(b.Card.Id, 0, "mzone").Card(c.Card.Id, 0, "mzone").Card(never.Card.Id, 0, "mzone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(a.Card.Id).Activate(b.Card.Id).Activate(c.Card.Id).Activate(never.Card.Id)
            .Check("Duel.GetLP(0)==6700").AssertPasses();
    }

    [EngineFact]
    public void BattleDamageCounts_AndATrapAnswersIt()
    {
        var attacker = new CardProject { Card = new CardData { Id = 100096021, Name = "Big Attacker", Frame = MonsterFrame.Normal, Level = 4, Atk = 2000, Def = 1000 } };
        var trap = new CardProject
        {
            Card = new CardData { Id = 100096022, Name = "Pay Back", Kind = CardKind.Trap },
            Effects = [new EffectEvent { When = BlockRegistry.Find("when.activate")!.CreateInstance(), If = [B(LifeThisTurnCondition.BlockId, ("what", "battle"))], Do = [B("do.gain_lp", ("amount", "1000"))] }],
        };
        new EngineScenario(attacker, trap).Card(attacker.Card.Id, 1, "mzone").Card(trap.Card.Id, 0, "szone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(attacker.Card.Id).Activate(trap.Card.Id).Turns(2)
            .Check("Duel.GetLP(0)==7000").AssertPasses();
    }

    [EngineFact]
    public void PayingLp_TriggersAGainOfThatAmount_AndHalvedDamage()
    {
        var payer = Monster(100096031, new EffectEvent { When = new BlockInstance("when.ignition"), Cost = [B("cost.pay_lp", ("amount", "1000"))], Do = [B("do.damage", ("who", "you"), ("amount", "800"))] });
        var refund = Monster(100096032, new EffectEvent
        {
            When = new BlockInstance("when.event").With("event", "you_pay_lp"), Do = [B("do.gain_lp", ("equal", "event"))],
        });
        var shield = Monster(100096033, new EffectEvent { When = new BlockInstance("when.while_face_up"), Do = [B("passive.player_rule", ("rule", "half_damage"))] });
        Assert.Contains("If you pay LP: You can gain LP equal to that amount.", EffectTextGenerator.Generate(refund));
        new EngineScenario(payer, refund, shield).Card(payer.Card.Id, 0, "mzone").Card(refund.Card.Id, 0, "mzone").Card(shield.Card.Id, 0, "mzone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(payer.Card.Id).Activate(refund.Card.Id)
            // -1000 paid, +1000 refunded, 800 damage halved to 400.
            .Check("Duel.GetLP(0)==7600").AssertPasses();
    }
}
