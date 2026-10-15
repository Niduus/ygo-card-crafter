using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters from your hand or field as Fusion Material."</summary>
public class FusionSummonTests
{
    private static CardProject FusionMonster(long id = 100012000) => new()
    {
        Card = new CardData { Id = id, Name = "Twin Warrior", Frame = MonsterFrame.Fusion, Level = 6, Atk = 2400, Def = 1000 },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [Warrior, Warrior] },
    };

    private static CardProject Poly(string mats = "hand_field") => new()
    {
        Card = new CardData { Id = 100012001, Name = "Fusion Spell", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = new BlockInstance("when.activate"), Do = [new BlockInstance(FusionSummonAction.BlockId).With("mats", mats)] }],
    };

    [Theory]
    [InlineData("hand_field", "Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters from your hand or field as Fusion Material.")]
    [InlineData("field_gy_banish", "Fusion Summon 1 Fusion Monster from your Extra Deck, by banishing Fusion Materials from your field or GY.")]
    [InlineData("gy_deck", "Fusion Summon 1 Fusion Monster from your Extra Deck, by shuffling Fusion Materials from your GY into the Deck.")]
    public void ReadsLikeACard(string mats, string text)
    {
        var p = Poly(mats);
        Assert.Equal(text, EffectTextGenerator.Generate(p));
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(CardCrafter.Core.Validation.ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        Assert.Contains("SUMMON_TYPE_FUSION", r.Lua);
    }

    [EngineFact]
    public void FusionSummons_FromTheHand()
    {
        var fusion = FusionMonster();
        var poly = Poly();
        new EngineScenario(fusion, poly).Card(poly.Card.Id, 0, "hand").Card(Warrior, 0, "hand", 2).Card(fusion.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3)
            .Activate(poly.Card.Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{fusion.Card.Id})")
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_GRAVE,0,nil,1001)==2").AssertPasses();
    }

    [EngineFact]
    public void FusionSummons_ByBanishingFromTheGY()
    {
        var fusion = FusionMonster(100012002);
        var poly = Poly("gy_banish");
        new EngineScenario(fusion, poly).Card(poly.Card.Id, 0, "hand").Card(Warrior, 0, "grave", 2).Card(fusion.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 3)
            .Activate(poly.Card.Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{fusion.Card.Id})")
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_REMOVED,0,nil,1001)==2").AssertPasses();
    }
}
