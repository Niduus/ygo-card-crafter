using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"For the rest of this turn, you cannot Normal or Special Summon monsters, except Level 4 Warrior monsters" and the other new locks.</summary>
public class SummonLockTests
{
    private const long Id = 100090001;

    private static CardProject Heart(string what)
    {
        var lockBlock = BlockRegistry.Find("do.summon_restriction")!.CreateInstance().With("who", "you").With("what", what);
        lockBlock.Filters["cards"] = new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Warrior, LevelMin = 4, LevelMax = 4 };
        return new CardProject
        {
            Card = new CardData { Id = Id, Name = "Guiding Test", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), Do = [new BlockInstance("do.gain_lp").With("amount", 500), lockBlock.Linked(ActionLink.Then)] }],
        };
    }

    [Theory]
    [InlineData("normal_special", "you cannot Normal or Special Summon monsters, except Level 4 Warrior monsters", 2)]
    [InlineData("summon_any", "you cannot Summon monsters, except Level 4 Warrior monsters", 3)]
    [InlineData("normal", "you cannot Normal Summon/Set monsters, except Level 4 Warrior monsters", 2)]
    [InlineData("special_gy", "you cannot Special Summon monsters from the GY, except Level 4 Warrior monsters", 1)]
    [InlineData("sset", "you cannot Set Spells/Traps", 1)]
    [InlineData("bp", "you cannot conduct your Battle Phase", 1)]
    public void ReadsAndCompiles(string what, string sentence, int effects)
    {
        var p = Heart(what);
        if (what == "sset") p.Effects[0].Do[1].Filters["cards"] = new CardFilter { CardType = FilterCardType.Monster };
        if (what == "bp") p.Effects[0].Do[1].Filters["cards"] = new CardFilter { CardType = FilterCardType.Monster };
        Assert.Contains("for the rest of this turn, " + sentence + ".", EffectTextGenerator.Generate(p));
        var result = ScriptCompiler.Compile(p);
        Assert.False(result.HasErrors, string.Join("; ", result.Diagnostics));
        Assert.Empty(ScriptChecker.Check(result.Lua));
        Assert.Equal(effects, result.Lua.Split("Duel.RegisterEffect(").Length - 1);
    }

    [EngineFact]
    public void AfterTheEffect_OnlyLevel4WarriorsCanBeNormalSummoned()
    {
        new EngineScenario(Heart("normal_special")).Card(Id, 0, "mzone").Card(Fiend, 0, "hand").Card(Warrior, 0, "hand")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Summon(Fiend).Summon(Warrior)
            .Check("Duel.GetLP(0)==8500")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,{Fiend})")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Warrior})").AssertPasses();
    }
}
