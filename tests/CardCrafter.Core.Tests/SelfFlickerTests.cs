using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// "You can banish this card; Special Summon this banished card" (Special Summoning this card from where its cost put
/// it, as Union Pilot does) and "banish it, then return it to the field" (Interdimensional Matter Forwarder).
/// </summary>
public class SelfFlickerTests
{
    private const long Id = 100320001, OppSpell = 100320002, Floodgate = 100320003, ForwarderId = 100320004;

    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    private static CardProject Flicker(string how = "banish") => new()
    {
        Card = new CardData { Id = Id, Name = "Raven Mirage", Frame = MonsterFrame.Effect, Race = CardRace.Illusion, Attribute = CardAttribute.Dark, Level = 4, Atk = 1500, Def = 1200 },
        Effects =
        [
            new EffectEvent
            {
                When = B(ActivationTrigger.BlockId), Limit = OncePerTurn.Hard,
                Cost = [B("cost.this_card").With("how", how)],
                Do = [B("do.special_summon_self").With("from", "cost")],
            },
        ],
    };

    /// <summary>The opponent's Spell that draws a card, for the monster to respond to.</summary>
    private static CardProject Spell() => new()
    {
        Card = new CardData { Id = OppSpell, Name = "Opp Spell", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.draw")] }],
    };

    private static EngineScenario Respond(params CardProject[] more) =>
        new EngineScenario([Flicker(), Spell(), .. more]).Card(Id, 0, "mzone").Card(OppSpell, 1, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(OppSpell).Activate(Id).Turns(2);

    private const string Back = "Duel.IsExistingMatchingCard(function(c) return c:IsCode(100320001) and c:IsFaceup() and c:IsSummonType(SUMMON_TYPE_SPECIAL) end,0,LOCATION_MZONE,0,1,nil)";

    [Theory]
    [InlineData("banish", "When your opponent activates a card or effect (Quick Effect): You can banish this card; Special Summon this banished card.")]
    [InlineData("grave", "When your opponent activates a card or effect (Quick Effect): You can send this card to the GY; Special Summon this card from your GY.")]
    public void ReadsLikeACard(string how, string text)
    {
        var p = Flicker(how);
        Assert.StartsWith(text, EffectTextGenerator.Generate(p));
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Empty(ScriptChecker.Check(r.Lua));
        // The zone this card pays the cost from counts as free, and the card is related to the effect again after the cost.
        Assert.Contains("Duel.GetMZoneCount(tp,e:GetHandler())>0", r.Lua);
        Assert.Contains("Duel.IsPlayerCanSpecialSummonMonster(tp,id,", r.Lua);
        Assert.Contains("e:GetHandler():CreateEffectRelation(e)", r.Lua);
        Assert.Contains(how == "banish" ? "c:IsLocation(LOCATION_REMOVED) and c:IsFaceup()" : "c:IsLocation(LOCATION_GRAVE)", r.Lua);
    }

    [Fact]
    public void WithoutACostThatMovesThisCard_ItIsAnError()
    {
        var p = Flicker();
        p.Effects[0].Cost.Clear();
        Assert.True(ScriptCompiler.Compile(p).HasErrors);
    }

    [EngineTheory]
    [InlineData("banish")]
    [InlineData("grave")]
    public void LeavesAndComesBack(string how)
    {
        new EngineScenario(Flicker(how), Spell()).Card(Id, 0, "mzone").Card(OppSpell, 1, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(OppSpell).Activate(Id).Turns(2).ExpectActivations(2)
            .Check(Back).Check("Duel.GetFieldGroupCount(0,LOCATION_REMOVED+LOCATION_GRAVE,0)==0").AssertPasses();
    }

    [EngineFact]
    public void WithAFullField_ItsOwnZoneIsFreed()
    {
        Respond().Card(Warrior, 0, "mzone", 4).ExpectActivations(2).Check(Back).Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==5").AssertPasses();
    }

    [EngineFact]
    public void WhenItCouldNotBeSpecialSummoned_ItIsNotActivated()
    {
        // "Neither player can Special Summon monsters": the cost would banish it for nothing.
        var gate = new CardProject
        {
            Card = new CardData { Id = Floodgate, Name = "Floodgate", Frame = MonsterFrame.Effect, Level = 4, Atk = 0, Def = 0 },
            Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.lock").With("who", "both").With("what", "special")] }],
        };
        Respond(gate).Card(Floodgate, 1, "mzone").ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Id})")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_REMOVED,0)==0").AssertPasses();
    }

    [Fact]
    public void BanishThenReturn_ReadsLikeMatterForwarder()
    {
        var p = Forwarder();
        Assert.Equal("Target 1 monster on the field; banish it, then return it to the field.", EffectTextGenerator.Generate(p));
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Empty(ScriptChecker.Check(r.Lua));
        Assert.Contains("REASON_EFFECT+REASON_TEMPORARY", r.Lua);
        Assert.Contains("Duel.ReturnToField(tc)", r.Lua);
        var self = new EffectEvent { When = B("when.ignition"), Do = [B("do.banish").With("cards", new CardFilter { ThisCard = true }).With("until", "return")] };
        Assert.Equal("You can banish this card, then return it to the field.", EffectTextGenerator.Describe(self, new CardData { Name = "X" }));
    }

    private static CardProject Forwarder() => new()
    {
        Card = new CardData { Id = ForwarderId, Name = "Matter Forwarder Test", Kind = CardKind.Spell },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do = [B("do.banish").With("target", true).With("until", "return").With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
            },
        ],
    };

    [EngineFact]
    public void BanishThenReturn_PutsTheMonsterBack()
    {
        new EngineScenario(Forwarder()).Card(ForwarderId, 0, "hand").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(ForwarderId).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{Fiend})")
            .Check("Duel.GetFieldGroupCount(1,LOCATION_REMOVED,0)==0").AssertPasses();
    }
}
