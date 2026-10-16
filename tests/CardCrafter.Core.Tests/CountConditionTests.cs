using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"All monsters you control are ...", exact and at-most counts, comparisons, different Attributes and totals.</summary>
public class CountConditionTests
{
    private const long Id = 100095001;

    private static BlockInstance If(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Card(BlockInstance condition, string zone = "monster_zone") => new()
    {
        Card = new CardData { Id = Id, Name = "Count Test", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000, Race = CardRace.Warrior, Attribute = CardAttribute.Earth },
        Effects = [new EffectEvent { When = new BlockInstance("when.ignition").With("zone", zone), If = [condition], Do = [new BlockInstance("do.gain_lp").With("amount", 500)] }],
    };

    private static BlockInstance AllWarriors(string mode)
    {
        var b = If(AllAreCondition.BlockId, ("mode", mode));
        b.Filters["kind"] = new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Warrior };
        return b;
    }

    [Theory]
    [InlineData("all", "If all monsters you control are Warrior monsters: You can gain 500 LP.")]
    [InlineData("none_or_all", "If you control no monsters, or all monsters you control are Warrior monsters: You can gain 500 LP.")]
    [InlineData("not_all", "If not all monsters you control are Warrior monsters: You can gain 500 LP.")]
    public void AllAre_Reads(string mode, string text) => Assert.Equal(text, EffectTextGenerator.Generate(Card(AllWarriors(mode))));

    [Fact]
    public void Counts_Read()
    {
        var exactly = If("if.cards_exist", ("cmp", "exactly"), ("count", "1"));
        Assert.StartsWith("If you control exactly 1 face-up monster: ", EffectTextGenerator.Generate(Card(exactly)));
        var fewer = If("if.cards_exist", ("cmp", "at_most"), ("count", "2"));
        Assert.StartsWith("If you control 2 or fewer face-up monsters: ", EffectTextGenerator.Generate(Card(fewer)));
        Assert.StartsWith("If you control more monsters than your opponent does: ", EffectTextGenerator.Generate(Card(If("if.compare_counts"))));
        Assert.StartsWith("If there are 2 or more different Attributes among the monsters in your GY: ", EffectTextGenerator.Generate(Card(If("if.different"))));
        Assert.StartsWith("If the total ATK of face-up monsters you control is 3000 or more: ", EffectTextGenerator.Generate(Card(If("if.total"))));
    }

    [EngineTheory]
    [InlineData("all", Warrior, true)]
    [InlineData("all", Fiend, false)]
    [InlineData("none_or_all", Fiend, false)]
    [InlineData("not_all", Fiend, true)]
    [InlineData("not_all", Warrior, false)]
    public void AllAre_InADuel(string mode, long other, bool activates)
    {
        new EngineScenario(Card(AllWarriors(mode))).Card(Id, 0, "mzone").Card(other, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(activates ? 1 : 0).Check(activates ? "Duel.GetLP(0)==8500" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineFact]
    public void NoneOrAll_WithNoMonsters_FromTheHand()
    {
        new EngineScenario(Card(AllWarriors("none_or_all"), "hand")).Card(Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1).Check("Duel.GetLP(0)==8500").AssertPasses();
    }

    [EngineFact]
    public void ExactCountsComparisonsDifferencesAndTotals_InADuel()
    {
        void Run(BlockInstance condition, bool activates) =>
            new EngineScenario(Card(condition)).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Dragon, 1, "mzone")
                .Card(Warrior, 0, "grave").Card(Dragon, 0, "grave").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
                .Activate(Id).ExpectActivations(activates ? 1 : 0).AssertPasses();
        Run(If("if.cards_exist", ("cmp", "exactly"), ("count", "2")), true);
        Run(If("if.cards_exist", ("cmp", "exactly"), ("count", "1")), false);
        Run(If("if.cards_exist", ("cmp", "at_most"), ("count", "1")), false);
        Run(If("if.compare_counts"), true);
        Run(If("if.compare_counts", ("cmp", "fewer")), false);
        Run(If("if.different", ("count", "2")), true);
        Run(If("if.different", ("count", "3")), false);
        Run(If("if.total", ("amount", "2500")), true);
        Run(If("if.total", ("amount", "2600")), false);
    }

    [EngineFact]
    public void DreamShark_TranslatesItsCondition()
    {
        if (Script(6180710) is not { } lua) return;
        var card = new CardData { Id = 6180710, Name = "Dream Shark", Frame = MonsterFrame.Effect, Level = 5, Atk = 1200, Def = 2600, Race = CardRace.Fish, Attribute = CardAttribute.Water };
        var result = ScriptTranslator.Translate(lua, card.Id, card);
        var first = result.Pieces.First(p => p.Effect is not null).Effect!;
        var block = Assert.Single(first.If);
        Assert.Equal(AllAreCondition.BlockId, block.BlockId);
        Assert.Equal("none_or_all", block.Values["mode"]);
        Assert.Equal(CardAttribute.Water, block.Filters["kind"].Attribute);
    }

    private static string? Script(long id) =>
        EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{id}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{id}.lua")) : null;
}
