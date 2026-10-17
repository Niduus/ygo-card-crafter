using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// One WHEN per event, joined with "Or when": older combined choices split into them, they read as one sentence,
/// and any mix of triggers compiles into one effect (shared object, per-event checks, or clones).
/// </summary>
public class WhenSplitTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static EffectEvent Draw(BlockInstance when, params BlockInstance[] also) =>
        new() { When = when, AlsoWhen = [.. also], Do = [B("do.draw")] };

    private static CardData Card(long id = 100040000) => new() { Id = id, Name = "Tester", Level = 4 };

    private static string Text(EffectEvent e) => EffectTextGenerator.Describe(e, Card());

    private static string Keys(EffectEvent e) => string.Join(" | ", new[] { e.When! }.Concat(e.AlsoWhen)
        .Select(w => w.BlockId + ":" + string.Join(",", w.Values.Where(kv => kv.Key != "optional").OrderBy(kv => kv.Key).Select(kv => kv.Value))));

    [Theory]
    [InlineData("when.summoned", "how", "normal_or_special", "when.summoned:any,normal | when.summoned:any,special")]
    [InlineData("when.summoned", "how", "any", "when.summoned:any,normal | when.summoned:any,flip | when.summoned:any,special")]
    [InlineData("when.summoned", "how", "normal_special_tributed", "when.summoned:any,normal | when.summoned:any,special | when.this_card:tributed")]
    [InlineData("when.event", "event", "opp_summon", "when.event:no,opp_normal,if,auto | when.event:no,opp_special,if,auto")]
    [InlineData("when.quick", "timing", "summon", "when.quick:false,summon_normal,auto | when.quick:false,summon_special,auto")]
    [InlineData("when.activate", "timing", "would_summon", "when.activate:false,would_normal | when.activate:false,would_special")]
    [InlineData("when.sent_to_gy", "from", "hand_or_field", "when.sent_to_gy:any,hand | when.sent_to_gy:any,field")]
    [InlineData("when.used_as_material", "kind", "any", "when.used_as_material:false,fusion | when.used_as_material:false,synchro | when.used_as_material:false,link")]
    public void OlderCombinedChoices_SplitIntoOneWhenPerEvent(string block, string key, string value, string expected)
    {
        var e = Draw(B(block, (key, value)));
        Assert.True(TriggerSplit.Normalize(e));
        Assert.Equal(expected, Keys(e));
        Assert.False(TriggerSplit.NeedsSplit(e));
    }

    [Fact]
    public void TheTickSeveralBlock_SplitsToo_AndOlderPacksLoadSplit()
    {
        var ticks = BlockRegistry.Find(ThisCardEventsTrigger.BlockId)!.CreateInstance();
        foreach (var ev in ThisCardEventsTrigger.All) ticks.Values[ev.Key] = ev.Key is "normal" or "special" or "sent_gy_field" or "banished" ? "true" : "false";
        var pack = new CardPack { Cards = [new CardProject { Card = Card(), Effects = [Draw(ticks)] }] };
        var back = PackSerializer.FromJson(PackSerializer.ToJson(pack));
        var e = back.Cards[0].Effects[0];
        Assert.Equal("when.summoned:any,normal | when.summoned:any,special | when.sent_to_gy:any,field | when.this_card:banished", Keys(e));
        Assert.Equal("If this card is Normal or Special Summoned, or if this card is sent to the GY from the field or banished: You can draw 1 card.", Text(e));
        Assert.DoesNotContain(BlockRegistry.Offered(BlockKind.Trigger), d => d.Id == ThisCardEventsTrigger.BlockId);
    }

    [Fact]
    public void TheMenus_OfferOnlySingleEvents()
    {
        Assert.DoesNotContain(BlockRegistry.Find("when.summoned")!.Param("how")!.Choices, c => c.Value.Contains("_or_") || c.Value == "any");
        Assert.DoesNotContain(BlockRegistry.Find("when.event")!.Param("event")!.Choices, c => c.Label.Contains(" or Special"));
        Assert.DoesNotContain(BlockRegistry.Find("when.quick")!.Param("timing")!.Choices, c => c.Label.Contains(" or Special"));
        // "destroyed by battle or an opponent's card effect" is one printed condition (Sky Thunder), not two events.
        Assert.DoesNotContain(BlockRegistry.Find(CardsEventTrigger.BlockId)!.Param("event")!.Choices, c => c.Label.Contains(" or ") && c.Value != "destroyed_battle_opp");
    }

    [Theory]
    [InlineData("If this card is Normal or Special Summoned: You can draw 1 card.", "when.summoned:how=normal", "when.summoned:how=special")]
    [InlineData("If this card is Normal or Special Summoned, or if this card is Tributed: You can draw 1 card.", "when.summoned:how=normal", "when.summoned:how=special", "when.this_card:event=tributed")]
    [InlineData("If this card is sent to the GY or banished: You can draw 1 card.", "when.sent_to_gy:from=any", "when.this_card:event=banished")]
    [InlineData("If this card is destroyed by battle or card effect: You can draw 1 card.", "when.destroyed:by=battle", "when.destroyed:by=effect")]
    [InlineData("If this card is Special Summoned from the hand or GY: You can draw 1 card.", "when.summoned:how=special,from=hand", "when.summoned:how=special,from=gy")]
    [InlineData("If your opponent Normal or Special Summons a monster(s): You can draw 1 card.", "when.event:event=opp_normal", "when.event:event=opp_special")]
    [InlineData("If this card is Normal Summoned, or if your opponent Special Summons a monster(s): You can draw 1 card.", "when.summoned:how=normal", "when.event:event=opp_special,zone=monster_zone")]
    [InlineData("If this card is used as material for a Fusion or Synchro Summon: You can draw 1 card.", "when.used_as_material:kind=fusion", "when.used_as_material:kind=synchro")]
    [InlineData("When your opponent Normal or Special Summons a monster(s) (Quick Effect): You can draw 1 card.", "when.quick:timing=summon_normal", "when.quick:timing=summon_special")]
    public void SeveralWhens_ReadAsOneSentence(string expected, params string[] whens)
    {
        var blocks = whens.Select(w =>
        {
            var (id, rest) = (w[..w.IndexOf(':')], w[(w.IndexOf(':') + 1)..]);
            return B(id, [.. rest.Split(',').Select(kv => (kv[..kv.IndexOf('=')], kv[(kv.IndexOf('=') + 1)..]))]);
        }).ToList();
        var e = Draw(blocks[0], [.. blocks.Skip(1)]);
        Assert.Equal(expected, Text(e));
        var r = ScriptCompiler.Compile(new CardProject { Card = Card(), Effects = [e] });
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics));
    }

    [Fact]
    public void SameKindOfTrigger_SharesTheEffect_AndChecksWhichEventStartedIt()
    {
        var plain = ScriptCompiler.Compile(new CardProject { Card = Card(), Effects = [Draw(B("when.summoned", ("how", "normal")), B("when.summoned", ("how", "special")))] }).Lua;
        Assert.Contains("e1b:SetCode(EVENT_SPSUMMON_SUCCESS)", plain);
        Assert.DoesNotContain("e:GetCode()", plain);

        // Normal Summoned, or Ritual Summoned: the Special Summon must be a Ritual Summon, the Normal Summon needs nothing.
        var ritual = ScriptCompiler.Compile(new CardProject { Card = Card(), Effects = [Draw(B("when.summoned", ("how", "normal")), B("when.summoned", ("how", "ritual")))] }).Lua;
        Assert.Contains("e:GetCode()==EVENT_SUMMON_SUCCESS or (e:GetCode()==EVENT_SPSUMMON_SUCCESS and (e:GetHandler():IsSummonType(SUMMON_TYPE_RITUAL)))", ritual);

        // Special Summoned from the hand or from the GY: one event, either check.
        var from = ScriptCompiler.Compile(new CardProject { Card = Card(), Effects = [Draw(B("when.summoned", ("how", "special"), ("from", "hand")), B("when.summoned", ("how", "special"), ("from", "gy")))] }).Lua;
        Assert.Contains("(e:GetHandler():IsSummonLocation(LOCATION_HAND)) or (e:GetHandler():IsSummonLocation(LOCATION_GRAVE))", from);
        Assert.DoesNotContain("e1b", from);
    }

    [Fact]
    public void OtherKindsOfTrigger_BecomeClones_WithTheirOwnTypeZoneAndChecks()
    {
        var e = Draw(B("when.summoned", ("how", "normal")), B(CardsEventTrigger.BlockId, ("event", "destroyed"), ("zone", "monster_zone")));
        var lua = ScriptCompiler.Compile(new CardProject { Card = Card(), Effects = [e] }).Lua;
        Assert.Contains("e1b:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_TRIGGER_O)", lua);
        Assert.Contains("e1b:SetCode(EVENT_DESTROYED)", lua);
        Assert.Contains("e1b:SetRange(LOCATION_MZONE)", lua);
        // The other trigger's card filter is part of the script.
        Assert.Contains("function s.evfilter(", lua);
        Assert.Contains("e1b:SetCondition(s.e1con", lua);
    }

    [Fact]
    public void OptionalAndMandatoryWhens_CantShareAnEffect()
    {
        var e = Draw(B("when.summoned", ("how", "normal")), B("when.summoned", ("how", "special"), ("optional", "false")));
        var r = ScriptCompiler.Compile(new CardProject { Card = Card(), Effects = [e] });
        Assert.Contains(r.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("optional"));
    }

    [Fact]
    public void SoftOncePerTurn_CountsOnceForAllItsEvents()
    {
        var e = Draw(B("when.summoned", ("how", "normal")), B("when.summoned", ("how", "special")));
        e.Limit = OncePerTurn.Soft;
        var lua = ScriptCompiler.Compile(new CardProject { Card = Card(), Effects = [e] }).Lua;
        Assert.Contains("e1:SetCountLimit(1,EFFECT_COUNT_CODE_SINGLE)", lua);
    }

    [EngineFact]
    public void ACloneWithAnotherType_TriggersInTheDuel()
    {
        // "You can Special Summon this card from your hand." and
        // "If this card is Special Summoned, or if you Normal Summon a monster: You can draw 1 card."
        var p = new CardProject
        {
            Card = new CardData { Id = 100040010, Name = "Two Ways", Level = 4 },
            Effects =
            [
                new EffectEvent { When = B("when.ignition", ("zone", "hand")), Do = [B("do.special_summon_self")] },
                Draw(B("when.summoned", ("how", "special")), B("when.event", ("event", "you_normal"), ("zone", "monster_zone"))),
            ],
        };
        Assert.Equal("If this card is Special Summoned, or if you Normal Summon a monster: You can draw 1 card.", EffectTextGenerator.Describe(p.Effects[1], p.Card));
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(p.Card.Id, 3).Summon(Warrior)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==3").AssertPasses();
    }

    [EngineFact]
    public void NormalOrSpecial_TriggersOnBoth()
    {
        var p = new CardProject { Card = new CardData { Id = 100040020, Name = "Both", Level = 4 }, Effects = [Draw(B("when.summoned", ("how", "normal")), B("when.summoned", ("how", "special")))] };
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Summon(p.Card.Id).Activate(p.Card.Id).Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==4").AssertPasses();
    }

    [EngineFact]
    public void EveryNewEvent_LoadsInTheEngine()
    {
        var cards = new List<CardProject>();
        long id = 100040100;
        foreach (var ev in ThisCardEventTrigger.Events)
            cards.Add(new CardProject { Card = new CardData { Id = id++, Name = "T " + ev.Key, Level = 4 }, Effects = [Draw(B(ThisCardEventTrigger.BlockId, ("event", ev.Key)))] });
        foreach (var ev in BlockRegistry.Find(CardsEventTrigger.BlockId)!.Param("event")!.Choices)
            cards.Add(new CardProject { Card = new CardData { Id = id++, Name = "C " + ev.Value, Level = 4 }, Effects = [Draw(B(CardsEventTrigger.BlockId, ("event", ev.Value), ("zone", "monster_zone")))] });
        foreach (var how in BlockRegistry.Find("when.summoned")!.Param("how")!.Choices)
            foreach (var from in new[] { "any", "gy" })
                cards.Add(new CardProject { Card = new CardData { Id = id++, Name = $"S {how.Value} {from}", Level = 4 }, Effects = [Draw(B("when.summoned", ("how", how.Value), ("from", from)))] });
        foreach (var by in BlockRegistry.Find("when.sent_to_gy")!.Param("by")!.Choices)
            cards.Add(new CardProject { Card = new CardData { Id = id++, Name = "G " + by.Value, Level = 4 }, Effects = [Draw(B("when.sent_to_gy", ("by", by.Value)))] });
        foreach (var c in cards)
        {
            var r = ScriptCompiler.Compile(c);
            Assert.False(r.HasErrors, c.Card.Name + "\n" + string.Join("\n", r.Diagnostics));
        }
        foreach (var batch in cards.Chunk(25))
        {
            var s = new EngineScenario([.. batch]);
            foreach (var c in batch) s.Card(c.Card.Id, 0, "mzone");
            s.Card(Warrior, 0, "deck", 5).Card(Fiend, 1, "hand").Card(Warrior, 1, "deck", 5).Summon(Fiend).Turns(2).AssertPasses();
        }
    }

    [Fact]
    public void OlderScriptSets_GetTheNewerConstantsTheScriptUses()
    {
        var p = new CardProject
        {
            Card = Card(),
            Effects = [new EffectEvent { When = B("when.quick", ("timing", "main_phase"), ("zone", "hand")), Do = [B("do.special_summon_self")] }],
        };
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("if not SUMMON_VALUE_SELF then SUMMON_VALUE_SELF=0x1 end", lua);
        Assert.True(lua.IndexOf("SUMMON_VALUE_SELF=0x1", StringComparison.Ordinal) < lua.IndexOf("function s.initial_effect", StringComparison.Ordinal));
        Assert.Equal("During the Main Phase (Quick Effect): You can Special Summon this card from your hand.", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        Assert.DoesNotContain("SUMMON_VALUE_SELF=0x1", ScriptCompiler.Compile(new CardProject { Card = Card(), Effects = [Draw(B("when.summoned"))] }).Lua);
    }
}
