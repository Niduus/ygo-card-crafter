using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Divine Arsenal AA-ZEUS - Sky Thunder, translated from its official script into blocks and played: its other way to
/// Xyz Summon ("if an Xyz Monster battled this turn ... by using 1 Xyz Monster you control"), and the same Card tab
/// setting built by hand.
/// </summary>
public class SkyThunderTests
{
    private const long ZeusId = 90448279, PlainXyz = 100070101;

    private static string? Script() =>
        EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{ZeusId}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{ZeusId}.lua")) : null;

    private static CardData ZeusCard() => new()
    {
        Id = ZeusId, Name = "Divine Arsenal AA-ZEUS - Sky Thunder", Frame = MonsterFrame.Xyz, Level = 12, Atk = 3000, Def = 3000,
        Race = CardRace.Machine, Attribute = CardAttribute.Light,
    };

    private static CardProject Translated()
    {
        var card = ZeusCard();
        var project = new CardProject { Card = card, UseCustomLua = true, CustomLua = Script()! };
        ScriptTranslator.ApplyTo(project, null, out var applied);
        Assert.True(applied, string.Join("\n", ScriptTranslator.Untranslated(ScriptTranslator.Translate(project.CustomLua, ZeusId, card))));
        return project;
    }

    private static CardProject Xyz() => new()
    {
        Card = new CardData { Id = PlainXyz, Name = "Plain Xyz", Frame = MonsterFrame.Xyz, Level = 4, Atk = 2000, Def = 1000, Race = CardRace.Warrior, Attribute = CardAttribute.Earth },
    };

    [EngineFact]
    public void TranslatesCompletely_AndReadsLikeTheCard()
    {
        if (Script() is null) return;
        var p = Translated();
        Assert.True(p.Procedure is { XyzAlternate: true, XyzAlternateOncePerTurn: true, XyzAlternateAfterXyzBattle: true, XyzAlternateDetach: 0 });
        Assert.Equal(MonsterKind.Xyz, p.Procedure.XyzAlternateFilter.MonsterKind);
        var text = EffectTextGenerator.Generate(p);
        Assert.StartsWith("2 Level 12 monsters\nOnce per turn, if an Xyz Monster battled this turn, you can also Xyz Summon \"Divine Arsenal AA-ZEUS - Sky Thunder\" by using 1 Xyz Monster you control. (Transfer its materials to this card.)", text);
        Assert.Contains("send all other cards on the field to the GY.", text);
        Assert.Contains("if another card(s) you control is destroyed by battle or an opponent's card effect: You can attach 1 card from your hand, Deck, or Extra Deck to this card as material.", text);
        // The prompt asking which way to Xyz Summon has a text of its own.
        Assert.Contains(ScriptCompiler.StringSlots(p), s => s.Use == ScriptCompiler.XyzAlternateUse && s.Text == "Xyz Summon by using 1 Xyz Monster you control");
    }

    [EngineFact]
    public void AfterAnXyzMonsterBattles_ItIsXyzSummonedUsingThatMonster()
    {
        if (Script() is null) return;
        new EngineScenario(Translated(), Xyz())
            .Card(PlainXyz, 1, "mzone").Card(ZeusId, 1, "extra").Card(Warrior, 0, "mzone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(PlainXyz).SpSummon(ZeusId).Turns(2)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{ZeusId})")
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{PlainXyz})")
            .AssertPasses();
    }

    [EngineFact]
    public void WithoutABattle_ItCantBeSummonedThatWay()
    {
        if (Script() is null) return;
        new EngineScenario(Translated(), Xyz())
            .Card(PlainXyz, 1, "mzone").Card(ZeusId, 1, "extra").Card(Warrior, 0, "mzone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .SpSummon(ZeusId).Turns(2)
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{ZeusId})")
            .AssertPasses();
    }

    [EngineFact]
    public void BuiltByHand_OncePerTurnAndDetaching()
    {
        var card = ZeusCard();
        card.Id = 100070102;
        card.Name = "Hand-made Rank 12";
        var p = new CardProject { Card = card };
        p.Procedure.Kind = SummonProcedureKind.Xyz;
        p.Procedure.XyzAlternate = true;
        p.Procedure.XyzAlternateOncePerTurn = true;
        var text = EffectTextGenerator.Generate(p);
        Assert.Contains("Once per turn, you can also Xyz Summon \"Hand-made Rank 12\" by using 1 Xyz Monster you control. (Transfer its materials to this card.)", text);
        p.Procedure.XyzAlternateDetach = 2;
        Assert.Contains("by using 1 Xyz Monster you control and detaching 2 materials from it.", EffectTextGenerator.Generate(p));
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("aux.AddXyzProcedure(c,nil,12,2,s.xyzaltfilter,aux.Stringid(id,0),2,s.xyzaltop)", lua);
        Assert.Contains("mc:RemoveOverlayCard(tp,2,2,REASON_COST)", lua);
        Assert.Empty(ScriptChecker.Check(lua));

        // Once per turn, with no battle needed: Summoned in the first turn.
        p.Procedure.XyzAlternateDetach = 0;
        new EngineScenario(p, Xyz())
            .Card(PlainXyz, 0, "mzone").Card(card.Id, 0, "extra").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .SpSummon(card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{card.Id})")
            .AssertPasses();
    }
}
