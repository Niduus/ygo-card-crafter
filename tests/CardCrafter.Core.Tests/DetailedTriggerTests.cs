using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Activation and attack WHENs with their details, in sentences and in duels.</summary>
public class DetailedTriggerTests
{
    private const long Id = 100101001, OppSpell = 100101002, Attacker = 100101003;

    private static CardProject Watcher(BlockInstance when) => new()
    {
        Card = new CardData { Id = Id, Name = "Watcher", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [new EffectEvent { When = when, Do = [new BlockInstance("do.gain_lp").With("amount", 1000)] }],
    };

    private static CardProject Spell(int archetype = 0) => new()
    {
        Card = new CardData { Id = OppSpell, Name = "Opp Spell", Kind = CardKind.Spell, Archetypes = archetype == 0 ? [] : [archetype] },
        Effects = [new EffectEvent { When = BlockRegistry.Find("when.activate")!.CreateInstance(), Do = [new BlockInstance("do.draw")] }],
    };

    [Fact]
    public void Sentences()
    {
        var when = new BlockInstance(ActivationTrigger.BlockId).With("what", "spell_card").With("from", "hand").With("targets", "your_monster");
        Assert.StartsWith("When your opponent activates a Spell Card in their hand that targets a monster(s) you control (Quick Effect): ", EffectTextGenerator.Generate(Watcher(when)));
        var gy = new BlockInstance(ActivationTrigger.BlockId).With("mode", "trigger").With("who", "either").With("what", "monster").With("from", "gy");
        gy.Filters["card"] = new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Dark, Zones = Zone.None };
        Assert.StartsWith("If a player activates the effect of a DARK monster in the GY: ", EffectTextGenerator.Generate(Watcher(gy)));
        var attack = new BlockInstance(AttackDeclaredTrigger.BlockId).With("how", "direct");
        Assert.StartsWith("When an opponent's monster declares a direct attack (Quick Effect): ", EffectTextGenerator.Generate(Watcher(attack)));
    }

    [EngineTheory]
    [InlineData("spell_card", 0, true)]
    [InlineData("trap_card", 0, false)]
    [InlineData("monster", 0, false)]
    public void RespondsToTheRightKindOfActivation(string what, int archetypeFilter, bool responds)
    {
        var when = new BlockInstance(ActivationTrigger.BlockId).With("what", what);
        if (archetypeFilter != 0) when.Filters["card"] = new CardFilter { Archetype = archetypeFilter, Zones = Zone.None };
        new EngineScenario(Watcher(when), Spell()).Card(Id, 0, "mzone").Card(OppSpell, 1, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(OppSpell).Activate(Id).Turns(2)
            .Check(responds ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineTheory]
    [InlineData(0x123, 0x123, true)]
    [InlineData(0x123, 0x456, false)]
    public void TheActivatedCardsPicker_Counts(int spellArchetype, int wanted, bool responds)
    {
        var when = new BlockInstance(ActivationTrigger.BlockId);
        when.Filters["card"] = new CardFilter { Archetype = wanted, Zones = Zone.None };
        new EngineScenario(Watcher(when), Spell(spellArchetype)).Card(Id, 0, "mzone").Card(OppSpell, 1, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(OppSpell).Activate(Id).Turns(2)
            .Check(responds ? "Duel.GetLP(0)==9000" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineTheory]
    [InlineData("any", true)]
    [InlineData("direct", false)]
    public void AttackDeclared(string how, bool responds)
    {
        var attacker = new CardProject { Card = new CardData { Id = Attacker, Name = "Attacker", Frame = MonsterFrame.Normal, Level = 4, Atk = 2000, Def = 1000 } };
        new EngineScenario(Watcher(new BlockInstance(AttackDeclaredTrigger.BlockId).With("how", how)), attacker)
            .Card(Id, 0, "mzone").Card(Attacker, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(Attacker).Activate(Id).Turns(2)
            // The 2000-ATK attacker also destroys the 1000-ATK watcher for 1000 battle damage.
            .Check(responds ? "Duel.GetLP(0)==8000" : "Duel.GetLP(0)==7000").AssertPasses();
    }
}

/// <summary>"If you Special Summoned a monster(s) this turn", LP comparisons.</summary>
public class HistoryAndLpTests
{
    [Theory]
    [InlineData("you_special", "did", 1, "If you Special Summoned a monster(s) this turn: ")]
    [InlineData("opp_activates", "not", 1, "If it has not happened this turn that your opponent activated a card or effect: ")]
    [InlineData("banished_any", "did", 3, "If a card(s) was banished 3 or more times this turn: ")]
    public void Reads(string ev, string did, int count, string text)
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100102001, Name = "History", Frame = MonsterFrame.Effect, Level = 4, Atk = 0, Def = 0 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), If = [new BlockInstance(EventThisTurnCondition.BlockId).With("event", ev).With("did", did).With("count", count)], Do = [new BlockInstance("do.draw")] }],
        };
        Assert.StartsWith(text, EffectTextGenerator.Generate(p));
    }

    [EngineTheory]
    [InlineData("did", true)]
    [InlineData("not", false)]
    public void AfterASpecialSummon(string did, bool activates)
    {
        const long summoner = 100102011, judge = 100102012;
        var ss = BlockRegistry.Find("do.special_summon")!.CreateInstance();
        ss.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster };
        var a = new CardProject
        {
            Card = new CardData { Id = summoner, Name = "Summoner", Frame = MonsterFrame.Effect, Level = 4, Atk = 0, Def = 0 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), Do = [ss] }],
        };
        var b = new CardProject
        {
            Card = new CardData { Id = judge, Name = "Judge", Frame = MonsterFrame.Effect, Level = 4, Atk = 0, Def = 0 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), If = [new BlockInstance(EventThisTurnCondition.BlockId).With("event", "you_special").With("did", did)], Do = [new BlockInstance("do.gain_lp").With("amount", 500)] }],
        };
        new EngineScenario(a, b).Card(summoner, 0, "mzone").Card(judge, 0, "mzone").Card(Warrior, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(summoner).Activate(judge)
            .Check(activates ? "Duel.GetLP(0)==8500" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineTheory]
    [InlineData("lt", 8000, 0, false)]
    [InlineData("eq", 8000, 0, true)]
    [InlineData("between", 7000, 9000, true)]
    [InlineData("gt", 8000, 0, false)]
    public void LifePoints(string cmp, int amount, int amount2, bool activates)
    {
        const long id = 100102021;
        var p = new CardProject
        {
            Card = new CardData { Id = id, Name = "LP", Frame = MonsterFrame.Effect, Level = 4, Atk = 0, Def = 0 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), If = [new BlockInstance("if.life_points").With("cmp", cmp).With("amount", amount).With("amount2", amount2)], Do = [new BlockInstance("do.draw")] }],
        };
        new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(id).ExpectActivations(activates ? 1 : 0).AssertPasses();
    }
}

/// <summary>Any value compared with a number or another value.</summary>
public class CompareValuesTests
{
    private const long Id = 100103001;

    private static CardProject Card(BlockInstance condition) => new()
    {
        Card = new CardData { Id = Id, Name = "Compare", Frame = MonsterFrame.Effect, Level = 4, Atk = 0, Def = 0 },
        Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), If = [condition], Do = [new BlockInstance("do.draw")] }],
    };

    [Fact]
    public void Reads()
    {
        var b = new BlockInstance(CompareValuesCondition.BlockId).With("value", "your_lp").With("right", "value").With("scale", "double").With("value2", "opp_lp");
        Assert.StartsWith("If your LP is double your opponent's LP or more: ", EffectTextGenerator.Generate(Card(b)));
        var hand = new BlockInstance(CompareValuesCondition.BlockId).With("value", "count").With("op", "lt").With("right", "value").With("value2", "count");
        Assert.StartsWith("If the number of cards in your hand is less than the number of cards in your opponent's hand: ", EffectTextGenerator.Generate(Card(hand)));
        var range = new BlockInstance(CompareValuesCondition.BlockId).With("op", "between").With("amount", 1000).With("amount2", 3000);
        Assert.StartsWith("If the difference between both players' LP is between 1000 and 3000: ", EffectTextGenerator.Generate(Card(range)));
    }

    [EngineTheory]
    [InlineData(3, 1, "lt", false)]
    [InlineData(1, 3, "lt", true)]
    [InlineData(2, 2, "eq", true)]
    public void HandSizes(int mine, int theirs, string op, bool activates)
    {
        var hand = new BlockInstance(CompareValuesCondition.BlockId).With("value", "count").With("op", op).With("right", "value").With("value2", "count");
        new EngineScenario(Card(hand)).Card(Id, 0, "mzone").Card(EngineScenario.Fiend, 0, "hand", mine).Card(EngineScenario.Fiend, 1, "hand", theirs)
            .Card(EngineScenario.Warrior, 0, "deck", 4).Card(EngineScenario.Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(activates ? 1 : 0).AssertPasses();
    }
}
