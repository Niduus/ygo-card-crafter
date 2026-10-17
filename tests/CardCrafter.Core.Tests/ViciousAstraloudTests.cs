using System.Text.Json;
using CardCrafter.Core.Assistant;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Vicious Astraloud's effect from blocks: "target 1 other monster on the field; destroy it, and if you do, this card gains
/// ATK equal to half of that monster's original ATK or DEF (whichever is higher)".
/// </summary>
public class ViciousAstraloudTests
{
    private const long Id = 100099600, Victim = 100099601;

    private static CardProject Card()
    {
        var (effect, errors) = AssistantTools.ReadEffect(JsonDocument.Parse("""
            {"label":"Destroy","when":{"blockId":"when.summoned","values":{"how":"normal"}},
             "do":[{"blockId":"do.destroy","values":{"count":1,"target":"true"},"filters":{"cards":{"owner":"either","zones":"monsterZone","excludeThisCard":true}}},
                   {"blockId":"do.stats_value","link":"ifYouDo","values":{"stat":"atk","dir":"gain","value":"that_orig_higher","how":"half","duration":"always"},"filters":{"cards":{"thisCard":true}}}]}
            """).RootElement);
        Assert.True(effect is not null, string.Join("\n", errors));
        return new CardProject { Card = new CardData { Id = Id, Name = "Vicious Test", Frame = MonsterFrame.Effect, Level = 4, Atk = 1800, Def = 1000 }, Effects = [effect!] };
    }

    [Fact]
    public void ReadsLikeTheCard()
    {
        var card = Card();
        Assert.Equal("If this card is Normal Summoned: You can target 1 other monster on the field; destroy it, and if you do, this card gains ATK equal to half of that monster's original ATK or DEF (whichever is higher).",
            EffectTextGenerator.Generate(card, null));
        Assert.False(ScriptCompiler.Compile(card).HasErrors, string.Join("\n", ScriptCompiler.Compile(card).Diagnostics));
    }

    [EngineFact]
    public void GainsHalfTheHigherOfTheDestroyedMonstersOriginalAtkOrDef()
    {
        // The victim: original ATK 1000, DEF 2400. Half of the higher is 1200: 1800 + 1200 = 3000.
        var victim = new CardProject { Card = new CardData { Id = Victim, Name = "Victim", Frame = MonsterFrame.Normal, Level = 4, Atk = 1000, Def = 2400 } };
        new EngineScenario(Card(), victim)
            .Card(Id, 0, "hand").Card(Victim, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Summon(Id).Activate(Id)
            .Check($"Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetAttack()==3000")
            .AssertPasses();
    }
}
