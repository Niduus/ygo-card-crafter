using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"This card: any of these happen": every event that can start a self trigger, ticked in any mix.</summary>
public class ThisCardEventsTests
{
    private static BlockInstance Events(params string[] keys)
    {
        var b = BlockRegistry.Find(ThisCardEventsTrigger.BlockId)!.CreateInstance();
        foreach (var e in ThisCardEventsTrigger.All) b.Values[e.Key] = keys.Contains(e.Key) ? "true" : "false";
        return b;
    }

    private static string Head(params string[] keys)
    {
        var def = BlockRegistry.Find(ThisCardEventsTrigger.BlockId)!;
        return def.Describe(new BlockArgs(def, Events(keys)));
    }

    [Theory]
    [InlineData(new[] { "normal", "special", "tributed" }, "If this card is Normal or Special Summoned, or if this card is Tributed")]
    [InlineData(new[] { "sent_gy", "banished" }, "If this card is sent to the GY or banished")]
    [InlineData(new[] { "normal", "flip_summon", "special" }, "If this card is Normal, Flip, or Special Summoned")]
    [InlineData(new[] { "destroyed_battle", "destroyed_effect" }, "If this card is destroyed by battle or card effect")]
    [InlineData(new[] { "tributed", "banished", "returned_deck" }, "If this card is Tributed, banished, or shuffled into the Deck")]
    [InlineData(new[] { "special", "leaves_field" }, "If this card is Special Summoned, or if this card leaves the field")]
    [InlineData(new[] { "synchro", "xyz" }, "If this card is Synchro or Xyz Summoned")]
    public void ReadsLikeACard(string[] keys, string text) => Assert.Equal(text, Head(keys));

    [Fact]
    public void EveryEvent_CompilesAlone_AndAllTogether()
    {
        var card = new CardData { Id = 100011000, Name = "All", Level = 4 };
        foreach (var e in ThisCardEventsTrigger.All)
        {
            var p = new CardProject { Card = card, Effects = [new EffectEvent { When = Events(e.Key), Do = [BlockRegistry.Find("do.draw")!.CreateInstance()] }] };
            var r = ScriptCompiler.Compile(p);
            Assert.False(r.HasErrors, e.Key + ": " + string.Join("; ", r.Diagnostics.Select(d => d.Message)));
            Assert.Contains(e.Code, r.Lua);
            Assert.DoesNotContain(CardCrafter.Core.Validation.ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        }
        var all = new CardProject { Card = card, Effects = [new EffectEvent { When = Events([.. ThisCardEventsTrigger.All.Select(e => e.Key)]), Do = [BlockRegistry.Find("do.draw")!.CreateInstance()] }] };
        var ra = ScriptCompiler.Compile(all);
        Assert.False(ra.HasErrors, string.Join("; ", ra.Diagnostics.Select(d => d.Message)));
        Assert.Contains("e:GetCode()==EVENT_TO_GRAVE", ra.Lua);
        Assert.DoesNotContain(CardCrafter.Core.Validation.ScriptChecker.Check(ra.Lua), d => d.Severity == Severity.Error);
    }

    [Fact]
    public void NothingTicked_IsAnError()
    {
        var p = new CardProject { Card = new CardData { Id = 100011001, Name = "None", Level = 4 }, Effects = [new EffectEvent { When = Events(), Do = [BlockRegistry.Find("do.draw")!.CreateInstance()] }] };
        Assert.True(ScriptCompiler.Compile(p).HasErrors);
    }

    [EngineFact]
    public void EachEvent_StartsTheEffect_OnlyWhenItsOwnCheckHolds()
    {
        // "If this card is Tributed, or sent from the hand to the GY": Tributed from the field, it is also sent to the GY,
        // but not from the hand, so the effect starts once.
        var mitsu = new CardProject
        {
            Card = new CardData { Id = 100011010, Name = "Tributed One", Level = 4 },
            Effects = [new EffectEvent { When = Events("tributed", "sent_gy_hand"), Do = [BlockRegistry.Find("do.draw")!.CreateInstance()] }],
        };
        var cost = BlockRegistry.Find("cost.tribute")!.CreateInstance();
        cost.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true };
        var tributer = new CardProject
        {
            Card = new CardData { Id = 100011011, Name = "Tributer", Level = 4 },
            Effects = [new EffectEvent { When = BlockRegistry.Find("when.ignition")!.CreateInstance(), Cost = [cost], Do = [BlockRegistry.Find("do.draw")!.CreateInstance()] }],
        };
        new EngineScenario(mitsu, tributer).Card(mitsu.Card.Id, 0, "mzone").Card(tributer.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 6).Card(Warrior, 1, "deck", 6)
            .Activate(tributer.Card.Id).Activate(mitsu.Card.Id, 2).ExpectActivations(2)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{mitsu.Card.Id})").AssertPasses();
    }
}
