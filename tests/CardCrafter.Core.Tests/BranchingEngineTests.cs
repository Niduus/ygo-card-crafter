using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Optional steps, "if you don't", IF blocks among the DO blocks and "Activate 1 of these effects", in real duels.</summary>
public class BranchingEngineTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static BlockInstance F(BlockInstance b, CardFilter f, string key = "cards") { b.Filters[key] = f; return b; }

    private static CardProject Monster(long id, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = "Test " + id, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    private static string OnField(int player, long id) => $"Duel.IsExistingMatchingCard(Card.IsCode,{player},LOCATION_MZONE,0,1,nil,{id})";

    [EngineFact]
    public void NegateTheSummon_AndIfYouDo_YouCanSpecialSummonThisCard()
    {
        // "When your opponent would Normal or Special Summon a monster(s) while this card is in your hand (Quick Effect):
        // You can negate the Summon, and if you do, you can Special Summon this card."
        var p = Monster(100009000, new EffectEvent
        {
            When = B("when.quick", ("timing", "would_summon"), ("zone", "hand")), Limit = OncePerTurn.Hard,
            Do =
            [
                B("do.negate_summon", ("and", "nothing")),
                B("do.special_summon_self").Linked(ActionLink.IfYouDo).MayDo(),
            ],
        });
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Fiend, 1, "hand").Card(Warrior, 1, "deck", 5)
            .Summon(Fiend).Activate(p.Card.Id).Turns(2).ExpectActivations(1)
            .Check(OnField(0, p.Card.Id)).Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0").AssertPasses();
        var text = EffectTextGenerator.Generate(p);
        Assert.True(text.StartsWith("When your opponent would Normal or Special Summon a monster(s) (Quick Effect): You can negate the Summon, and if you do, you can Special Summon this card from your hand.", StringComparison.Ordinal), text);
        Assert.Contains("Duel.SelectYesNo(tp,aux.Stringid(id,0))", ScriptCompiler.Compile(p).Lua);
    }

    [EngineFact]
    public void IfYouDont_HappensOnlyWhenThePreviousStepDidNot()
    {
        // Nothing to destroy, so it draws instead.
        var p = Monster(100009010, new EffectEvent
        {
            When = B("when.ignition"),
            Do = [F(B("do.destroy"), new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.SpellTrapZone, CardType = FilterCardType.Any }), B("do.draw").Linked(ActionLink.IfYouDont)],
        });
        p.Effects[0].Do[0].Values["mode"] = "all";
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Fiend, 1, "mzone")
            .Activate(p.Card.Id).ExpectActivations(0).AssertPasses();
        // With a card to destroy it doesn't draw.
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Spell, 1, "szone")
            .Activate(p.Card.Id).ExpectActivations(1).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
        Assert.Contains(", and if you don't, draw 1 card", EffectTextGenerator.Generate(p));
    }

    [EngineFact]
    public void IfBlockAmongTheDoBlocks_GatesTheStepsAfterIt()
    {
        // "Draw 1 card, then if you control 2 or more monsters, gain 1000 LP."
        var gate = F(B("if.cards_exist", ("count", "2")), new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }).Linked(ActionLink.Then);
        var p = Monster(100009020, new EffectEvent { When = B("when.ignition"), Do = [B("do.draw"), gate, B("do.gain_lp", ("amount", "1000"))] });
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1).Check("Duel.GetLP(0)==8000").Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").AssertPasses();
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1).Check("Duel.GetLP(0)==9000").AssertPasses();
        Assert.Contains("You can draw 1 card, then if you control 2 or more monsters, gain 1000 LP.", EffectTextGenerator.Generate(p));
        Assert.DoesNotContain(ScriptCompiler.Compile(p).Diagnostics, d => d.Severity == Severity.Error);
    }

    [EngineFact]
    public void ActivateOneOfTheseEffects_UsesAnOptionThatCanBeUsed()
    {
        // "Activate 1 of these effects; ● Target 1 monster your opponent controls; destroy it. ● Draw 2 cards."
        // The opponent controls nothing, so only the draw can be picked.
        var p = Monster(100009030, new EffectEvent
        {
            When = B("when.ignition"), Limit = OncePerTurn.Hard,
            Do =
            [
                F(B("do.destroy", ("target", "true")), new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
                B("do.draw", ("count", "2")).Linked(ActionLink.Option),
            ],
        });
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2").AssertPasses();
        // With an opponent's monster the first option is picked: it is destroyed and nothing is drawn.
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0").Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
        var text = EffectTextGenerator.Generate(p);
        Assert.StartsWith("You can activate 1 of these effects; ● Target 1 monster your opponent controls; destroy it. ● Draw 2 cards.", text);
        var compiled = ScriptCompiler.Compile(p);
        Assert.Contains("aux.SelectFromOptions(tp,", compiled.Lua);
        Assert.Equal(3, compiled.Strings.Count);
        Assert.Equal("Draw 2 cards.", compiled.Strings[2]);
    }

    [EngineFact]
    public void OptionalStep_ThenYouCan()
    {
        // "Draw 1 card, then you can gain 500 LP": the harness answers yes.
        var p = Monster(100009040, new EffectEvent { When = B("when.ignition"), Do = [B("do.draw"), B("do.gain_lp", ("amount", "500")).Linked(ActionLink.Then).MayDo()] });
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1).Check("Duel.GetLP(0)==8500").AssertPasses();
        Assert.Contains("You can draw 1 card, then you can gain 500 LP.", EffectTextGenerator.Generate(p));
    }
}
