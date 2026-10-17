using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Official shapes of the negated activation, the flicker and the hand/GY lock, read into blocks: Cyber Emergency,
/// Interdimensional Matter Forwarder and Speedroid Clear Wing Wonder.
/// </summary>
public class NegatedTranslationTests
{
    private const long CyberEmergency = 60600126, MatterForwarder = 60238002, ClearWingWonder = 66002986;

    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScripts!, $"c{id}.lua"));

    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    [EngineFact]
    public void CyberEmergency_TheGyEffectIsTheNegatedTrigger()
    {
        var card = new CardData { Id = CyberEmergency, Name = "Cyber Emergency", Kind = CardKind.Spell };
        var result = ScriptTranslator.Translate(Script(CyberEmergency), CyberEmergency, card);
        var gy = result.Pieces.SelectMany(p => p.Effects).Single(e => e.When?.BlockId == NegatedActivationTrigger.BlockId);
        Assert.Equal(["cost.discard"], gy.Cost.Select(b => b.BlockId));
        var add = Assert.Single(gy.Do);
        Assert.Equal("do.add_to_hand", add.BlockId);
        Assert.True(add.Filters["cards"].ThisCard);
        // The activation's watcher is part of the trigger, not a cost: only its search (a filter of two kinds of monster) stays script.
        var activation = result.Pieces.Single(p => p.Piece.Code.Contains("EFFECT_TYPE_ACTIVATE", StringComparison.Ordinal));
        Assert.DoesNotContain("IsHasType", activation.Reason ?? "");
        Assert.DoesNotContain("cost", activation.Reason ?? "");
    }

    [EngineFact]
    public void CyberEmergency_TheTranslatedGyEffectPlays()
    {
        var card = new CardData { Id = CyberEmergency, Name = "Cyber Emergency", Kind = CardKind.Spell };
        var gy = ScriptTranslator.Translate(Script(CyberEmergency), CyberEmergency, card).Pieces.SelectMany(p => p.Effects)
            .Single(e => e.When?.BlockId == NegatedActivationTrigger.BlockId);
        var own = PackSerializer.Clone(card);
        own.Id = 100330001;
        var project = new CardProject
        {
            Card = own,
            Effects = [new EffectEvent { When = B("when.activate"), Limit = OncePerTurn.Hard, Do = [B("do.draw")] }, gy],
        };
        var r = ScriptCompiler.Compile(project);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Empty(ScriptChecker.Check(r.Lua));
        Assert.EndsWith("If the activation of this card in its owner's possession was negated by your opponent's card effect and sent to your GY: You can discard 1 card from your hand; add this card to your hand. You can only activate 1 \"Cyber Emergency\" per turn.",
            EffectTextGenerator.Generate(project));
        // Negated by the opponent's Counter Trap: discard 1 card, and it comes back to the hand.
        new EngineScenario(project, NegatedActivationTests.Counter(100330002, "nothing")).Card(100330001, 0, "hand").Card(Warrior, 0, "hand").Card(100330002, 1, "szone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(100330001, 2).Activate(100330002).ExpectActivations(3)
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,100330001)")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{Warrior})").AssertPasses();
    }

    [EngineTheory]
    [InlineData(MatterForwarder, 2)]
    [InlineData(ClearWingWonder, 1)]
    public void BanishThenReturn_ReadsAsTheBanishBlock(long id, int count)
    {
        var card = id == MatterForwarder
            ? new CardData { Id = id, Name = "Interdimensional Matter Forwarder", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous }
            : new CardData { Id = id, Name = "Speedroid Clear Wing Wonder", Frame = MonsterFrame.Synchro, Level = 7, Atk = 2500, Def = 2000, Attribute = CardAttribute.Wind, Race = CardRace.Machine };
        var effects = ScriptTranslator.Translate(Script(id), id, card).Pieces.SelectMany(p => p.Effects)
            .Where(e => e.Do.Any(b => b.BlockId == "do.banish" && b.Values.GetValueOrDefault("until") == "return")).ToList();
        Assert.Equal(count, effects.Count);
        foreach (var e in effects)
        {
            var r = ScriptCompiler.Compile(new CardProject { Card = card, Effects = [e] });
            Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
            Assert.Contains("banish it, then return it to the field", EffectTextGenerator.Describe(e, card));
        }
    }

    [Theory]
    [InlineData("(re:GetActivateLocation()==LOCATION_HAND or re:GetActivateLocation()==LOCATION_GRAVE)")]
    [InlineData("re:GetActivateLocation()&(LOCATION_HAND|LOCATION_GRAVE)~=0")]
    public void TheHandOrGyLock_ReadsBack(string where)
    {
        // A Spell written the way official scripts lock activations for the rest of the turn.
        var lua = string.Join("\n",
            "local s,id=GetID()", "function s.initial_effect(c)", "\tlocal e1=Effect.CreateEffect(c)", "\te1:SetType(EFFECT_TYPE_ACTIVATE)",
            "\te1:SetCode(EVENT_FREE_CHAIN)", "\te1:SetOperation(s.activate)", "\tc:RegisterEffect(e1)", "end",
            "function s.activate(e,tp,eg,ep,ev,re,r,rp)", "\tlocal e1=Effect.CreateEffect(e:GetHandler())", "\te1:SetType(EFFECT_TYPE_FIELD)",
            "\te1:SetProperty(EFFECT_FLAG_PLAYER_TARGET)", "\te1:SetCode(EFFECT_CANNOT_ACTIVATE)", "\te1:SetTargetRange(1,0)", "\te1:SetValue(s.aclimit)",
            "\te1:SetReset(RESET_PHASE+PHASE_END)", "\tDuel.RegisterEffect(e1,tp)", "end",
            "function s.aclimit(e,re,tp)", $"\treturn re:IsActiveType(TYPE_MONSTER) and {where} and not re:GetHandler():IsRace(RACE_ILLUSION)", "end") + "\n";
        var card = new CardData { Id = 100330003, Name = "Lock Test", Kind = CardKind.Spell };
        var result = ScriptTranslator.Translate(lua, card.Id, card);
        var lockBlock = result.Pieces.SelectMany(p => p.Effects).SelectMany(e => e.Do).SingleOrDefault(b => b.BlockId == "do.summon_restriction");
        Assert.True(lockBlock is not null, string.Join("\n", ScriptTranslator.Untranslated(result)));
        Assert.Equal("activate_monster_hand_gy", lockBlock.Values["what"]);
        Assert.Equal(CardRace.Illusion, lockBlock.Filters["cards"].Race);
        var text = EffectTextGenerator.Generate(new CardProject { Card = card, Effects = result.Pieces.SelectMany(p => p.Effects).ToList() });
        Assert.Equal("You cannot activate monster effects in the hand or GY for the rest of this turn after this card resolves, except Illusion monsters'.", text);
    }
}
