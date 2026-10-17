using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// "Illusion of the Winter Raven Witch": a Fusion Summon using Extra Deck monsters, a lock on monster effects in the hand
/// or GY, and Cyber Emergency's "If the activation of this card ... was negated by your opponent's card effect and sent
/// to your GY", built from blocks and played in the engine.
/// </summary>
public class NegatedActivationTests
{
    private const long WitchId = 100310001, FusionId = 100310002, ExtraMatId = 100310003, CounterId = 100310004, DestroyCounterId = 100310005,
        OffHandId = 100310006, IllusionHandId = 100310007, OffGyId = 100310008;

    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    public const string WitchText =
        "Fusion Summon 1 Illusion Fusion Monster from your Extra Deck, using monsters from your Extra Deck and/or field, also you cannot activate monster effects " +
        "in the hand or GY for the rest of this turn after this card resolves, except Illusion monsters'. If the activation of this card in its owner's possession " +
        "was negated by your opponent's card effect and sent to your GY: You can Set this card, and if you do, return all cards your opponent controls to the hand. " +
        "You can only activate 1 \"Illusion of the Winter Raven Witch\" per turn.";

    public static CardProject Witch() => new()
    {
        Card = new CardData { Id = WitchId, Name = "Illusion of the Winter Raven Witch", Kind = CardKind.Trap },
        Effects =
        [
            new EffectEvent
            {
                Label = "Fusion Summon", When = B("when.activate"), Limit = OncePerTurn.Hard,
                Do =
                [
                    B(FusionSummonAction.BlockId).With("mats", "extra_field")
                        .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion, Race = CardRace.Illusion }),
                    B("do.summon_restriction").With("what", "activate_monster_hand_gy").With("cards", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Illusion }),
                ],
            },
            new EffectEvent
            {
                Label = "Set and bounce", When = B(NegatedActivationTrigger.BlockId),
                Do =
                [
                    B("do.set").With("cards", new CardFilter { ThisCard = true }),
                    B("do.return_to_hand").With("mode", "all").With("cards", CardFilter.OpponentField()).Linked(ActionLink.IfYouDo),
                ],
            },
        ],
    };

    /// <summary>An Illusion Fusion Monster made from an Extra Deck monster and a Warrior.</summary>
    private static CardProject IllusionFusion() => new()
    {
        Card = new CardData { Id = FusionId, Name = "Raven Witch Fusion", Frame = MonsterFrame.Fusion, Race = CardRace.Illusion, Attribute = CardAttribute.Dark, Level = 7, Atk = 2500, Def = 2000 },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [ExtraMatId, Warrior] },
    };

    /// <summary>The material waiting in the Extra Deck: a Fusion Monster of its own.</summary>
    private static CardProject ExtraMaterial() => new()
    {
        Card = new CardData { Id = ExtraMatId, Name = "Raven Familiar", Frame = MonsterFrame.Fusion, Race = CardRace.WingedBeast, Attribute = CardAttribute.Dark, Level = 4, Atk = 1200, Def = 800 },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [Fiend, Fiend] },
    };

    /// <summary>"When a Spell/Trap Card is activated: Negate the activation" (and destroy it).</summary>
    internal static CardProject Counter(long id, string and) => new()
    {
        Card = new CardData { Id = id, Name = "Negating Counter " + id, Kind = CardKind.Trap, TrapKind = TrapKind.Counter },
        Effects =
        [
            new EffectEvent
            {
                When = B(ActivationTrigger.BlockId).With("mode", "activate").With("who", "opponent").With("what", "spelltrap_card"),
                Do = [B("do.negate").With("what", "activation").With("and", and)],
            },
        ],
    };

    /// <summary>A monster whose hand or GY effect only works once a Fusion Monster is out: after the Trap has resolved.</summary>
    private static CardProject Drawer(long id, CardRace race, string zone) => new()
    {
        Card = new CardData { Id = id, Name = "Drawer " + id, Frame = MonsterFrame.Effect, Race = race, Attribute = CardAttribute.Light, Level = 4, Atk = 1000, Def = 1000 },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.ignition").With("zone", zone), Limit = OncePerTurn.Hard,
                If = [B("if.cards_exist").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion })],
                Do = [B("do.draw")],
            },
        ],
    };

    private static EngineScenario Board(params CardProject[] more) =>
        new EngineScenario([Witch(), IllusionFusion(), ExtraMaterial(), .. more])
            .Card(WitchId, 0, "szone").Card(Warrior, 0, "mzone").Card(ExtraMatId, 0, "extra").Card(FusionId, 0, "extra")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5);

    private static string OnField(long id, int player = 0) => $"Duel.IsExistingMatchingCard(Card.IsCode,{player},LOCATION_MZONE,0,1,nil,{id})";

    [Fact]
    public void ReadsLikeTheCard()
    {
        var p = Witch();
        Assert.Equal(WitchText, EffectTextGenerator.Generate(p));
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Empty(ScriptChecker.Check(r.Lua));
        // "You can only activate 1 per turn" doesn't count negated activations; the activation watches for its negation.
        Assert.Contains("e1:SetCountLimit(1,id+EFFECT_COUNT_CODE_OATH)", r.Lua);
        Assert.Contains("e1:SetCost(s.e1cost)", r.Lua);
        Assert.Contains("e2:SetCode(EVENT_CHAIN_NEGATED)", r.Lua);
        Assert.Contains("e2b:SetCode(EVENT_CUSTOM+id)", r.Lua);
        Assert.Contains("e2:SetRange(LOCATION_GRAVE)", r.Lua);
    }

    [Fact]
    public void TheTriggerNeedsASpellOrTrapThatIsActivated()
    {
        var monster = new CardProject
        {
            Card = new CardData { Id = 100310020, Name = "Not a Trap", Frame = MonsterFrame.Effect },
            Effects = [new EffectEvent { When = B(NegatedActivationTrigger.BlockId), Do = [B("do.draw")] }],
        };
        Assert.True(ScriptCompiler.Compile(monster).HasErrors);
        var noActivation = new CardProject
        {
            Card = new CardData { Id = 100310021, Name = "No Activation", Kind = CardKind.Trap },
            Effects = [new EffectEvent { When = B(NegatedActivationTrigger.BlockId), Do = [B("do.draw")] }],
        };
        Assert.True(ScriptCompiler.Compile(noActivation).HasErrors);
        // A Continuous Trap's plain activation carries the watcher.
        noActivation.Card.TrapKind = TrapKind.Continuous;
        var r = ScriptCompiler.Compile(noActivation);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Contains("e0:SetCost(", r.Lua);
    }

    [EngineFact]
    public void FusionSummons_WithAnExtraDeckMonster()
    {
        Board().Activate(WitchId).ExpectActivations(1)
            .Check(OnField(FusionId))
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{ExtraMatId})")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{Warrior})")
            .Check($"Duel.GetFieldGroupCount(0,LOCATION_EXTRA,0)==0").AssertPasses();
    }

    [EngineFact]
    public void TheLock_StopsOtherMonsterEffectsInTheHandAndGY()
    {
        // After the Trap resolves, only the Illusion monster's hand effect can still be activated: 1 card drawn.
        Board(Drawer(OffHandId, CardRace.Warrior, "hand"), Drawer(IllusionHandId, CardRace.Illusion, "hand"), Drawer(OffGyId, CardRace.Warrior, "graveyard"))
            .Card(OffHandId, 0, "hand").Card(IllusionHandId, 0, "hand").Card(OffGyId, 0, "grave")
            .Activate(WitchId).Activate(OffHandId).Activate(IllusionHandId).Activate(OffGyId).ExpectActivations(2)
            .Check(OnField(FusionId))
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==3").AssertPasses();
    }

    [EngineFact]
    public void WithoutTheLock_TheyCouldAllBeActivated()
    {
        var unlocked = Witch();
        unlocked.Effects[0].Do.RemoveAt(1);
        new EngineScenario(unlocked, IllusionFusion(), ExtraMaterial(), Drawer(OffHandId, CardRace.Warrior, "hand"), Drawer(OffGyId, CardRace.Warrior, "graveyard"))
            .Card(WitchId, 0, "szone").Card(Warrior, 0, "mzone").Card(ExtraMatId, 0, "extra").Card(FusionId, 0, "extra")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Card(OffHandId, 0, "hand").Card(OffGyId, 0, "grave")
            .Activate(WitchId).Activate(OffHandId).Activate(OffGyId).ExpectActivations(3).AssertPasses();
    }

    [EngineTheory]
    [InlineData("nothing")]
    [InlineData("destroy")]
    public void NegatedByTheOpponent_SetsItselfAndBouncesTheirCards(string and)
    {
        // Negated only, the rules send it to the GY at the end of the Chain; negated and destroyed, it is there at once.
        var id = and == "nothing" ? CounterId : DestroyCounterId;
        Board(Counter(id, and)).Card(id, 1, "szone").Card(Fiend, 1, "mzone").Card(Spell, 1, "szone")
            .Activate(WitchId, 2).Activate(id).ExpectActivations(3)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,1,nil,{WitchId})")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,nil,{WitchId}):IsFacedown()")
            .Check("Duel.GetFieldGroupCount(1,LOCATION_ONFIELD,0)==0")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_HAND,0,1,nil,{Fiend})")
            .Check($"not {OnField(FusionId)}").AssertPasses();
    }

    [EngineFact]
    public void ResolvedNormally_TheGyEffectDoesNotTrigger()
    {
        // Sent to the GY after resolving, it was not negated: it stays there.
        Board().Card(Fiend, 1, "mzone").Activate(WitchId, 2).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{WitchId})")
            .Check(OnField(Fiend, 1)).AssertPasses();
    }

    [EngineFact]
    public void OnlyOneCanBeActivatedPerTurn()
    {
        new EngineScenario(Witch(), IllusionFusion(), ExtraMaterial())
            .Card(WitchId, 0, "szone", 2).Card(Warrior, 0, "mzone", 2).Card(ExtraMatId, 0, "extra", 2).Card(FusionId, 0, "extra", 2)
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(WitchId, 2).ExpectActivations(1)
            .Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_MZONE,0,nil,{FusionId})==1")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,1,nil,{WitchId})").AssertPasses();
    }
}
