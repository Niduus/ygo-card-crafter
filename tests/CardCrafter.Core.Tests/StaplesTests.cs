using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Generic Extra Deck staples like I:P Masquerena and S:P Little Knight, built from blocks.</summary>
public class StaplesTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject LinkTwo(long id, string name, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Link, Atk = 800, LinkMarkers = LinkMarker.BottomLeft | LinkMarker.BottomRight },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 2, MaxMaterials = 2 },
        Effects = [.. effects],
    };

    /// <summary>Masquerena: Link Summon using this card, and the Link Monster can't be destroyed by the opponent's effects.</summary>
    private static CardProject Masquerena(BlockInstance when, params BlockInstance[] ifs) => LinkTwo(100050000, "Masked One",
        new EffectEvent { When = when, If = [.. ifs], Do = [B(SummonUsingThisAction.BlockId, ("kind", "link"))], Limit = OncePerTurn.Hard },
        new EffectEvent { When = B("when.while_face_up"), Do = [B(MaterialGrantPassive.BlockId, ("kind", "link"), ("gets", "indes_opp_effect"))] });

    /// <summary>S:P-style: "If this card is Link Summoned using a Fusion, Synchro, Xyz, or Link Monster as material: You can draw 1 card."</summary>
    private static CardProject Knight() => LinkTwo(100050010, "Small Knight",
        new EffectEvent { When = B("when.summoned", ("how", "link")), If = [B(SummonedUsingCondition.BlockId, ("kind", "extra"))], Do = [B("do.draw")] });

    [Fact]
    public void Masquerena_ReadsLikeTheCard()
    {
        var p = Masquerena(B("when.quick", ("timing", "main_phase")), B("if.turn", ("whose", "opponent")));
        var text = EffectTextGenerator.Generate(p);
        Assert.Contains("During your opponent's Main Phase (Quick Effect): You can Link Summon 1 Link Monster using materials you control, including this card.", text);
        Assert.Contains("A Link Monster that used this card as material cannot be destroyed by your opponent's card effects.", text);
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics));
        Assert.Contains("Duel.LinkSummon(tp,", r.Lua);
        Assert.Contains("EVENT_BE_MATERIAL", r.Lua);
        Assert.Equal("If this card is Link Summoned using a Fusion, Synchro, Xyz, or Link Monster as material: You can draw 1 card.", EffectTextGenerator.Describe(Knight().Effects[0], Knight().Card));
    }

    [EngineFact]
    public void LinkSummonUsingThisCard_TheNewMonsterGainsProtection_AndKnowsItsMaterials()
    {
        var m = Masquerena(B("when.ignition"));
        var k = Knight();
        new EngineScenario(m, k).Card(m.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(k.Card.Id, 0, "extra")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(m.Card.Id).Activate(k.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{k.Card.Id})")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{m.Card.Id})")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{k.Card.Id}):IsHasEffect(EFFECT_INDESTRUCTABLE_EFFECT)~=nil")
            // Masked One is a Link Monster, so the Knight's "Summoned using a Link Monster" draw happened.
            .Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==4").AssertPasses();
    }

    [EngineFact]
    public void KnightSummonedWithoutExtraDeckMaterials_DoesNotDraw()
    {
        var k = Knight();
        new EngineScenario(k).Card(Warrior, 0, "mzone", 2).Card(k.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .SpSummon(k.Card.Id).Activate(k.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{k.Card.Id})")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==5").AssertPasses();
    }

    private static CardProject Using(long id, string kind, int level, bool tuner) => new()
    {
        Card = new CardData { Id = id, Name = "User " + kind, Level = level, Abilities = tuner ? MonsterAbility.Tuner : MonsterAbility.None },
        Effects = [new EffectEvent { When = B("when.ignition"), Do = [B(SummonUsingThisAction.BlockId, ("kind", kind))] }],
    };

    [EngineTheory]
    [InlineData("synchro", true)]
    [InlineData("synchro", false)]
    [InlineData("xyz", false)]
    public void SynchroAndXyzSummonUsingThisCard(string kind, bool tuner)
    {
        var user = Using(100050020, kind, tuner ? 2 : 4, tuner);
        var tunerCard = new CardProject { Card = new CardData { Id = 100050021, Name = "Plain Tuner", Frame = MonsterFrame.Normal, Level = 2, Abilities = MonsterAbility.Tuner } };
        var target = kind == "xyz"
            ? new CardProject { Card = new CardData { Id = 100050022, Name = "Rank 4", Frame = MonsterFrame.Xyz, Level = 4, Atk = 2000 }, Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2 } }
            : new CardProject { Card = new CardData { Id = 100050022, Name = "Level 6", Frame = MonsterFrame.Synchro, Level = 6, Atk = 2000 }, Procedure = new SummonProcedure { Kind = SummonProcedureKind.Synchro, MinMaterials = 1 } };
        var s = new EngineScenario(user, tunerCard, target).Card(user.Card.Id, 0, "mzone").Card(target.Card.Id, 0, "extra");
        // The other material: a Level 4 non-Tuner for a Tuner, a Level 2 Tuner for a non-Tuner, another Level 4 for the Xyz.
        s = kind == "synchro" && !tuner ? s.Card(tunerCard.Card.Id, 0, "mzone") : s.Card(Warrior, 0, "mzone");
        s.Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(user.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{target.Card.Id})")
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{user.Card.Id})").AssertPasses();
    }

    [EngineFact]
    public void BanishUntilTheEndPhase_ComesBack()
    {
        var banish = B("do.banish", ("until", "end_phase"), ("target", "true"));
        banish.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };
        var p = new CardProject { Card = new CardData { Id = 100050030, Name = "Banisher", Level = 4 }, Effects = [new EffectEvent { When = B("when.ignition"), Do = [banish] }] };
        Assert.Equal("You can target 1 face-up monster your opponent controls; banish it until the End Phase.", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,0,LOCATION_MZONE,1,nil,{Fiend})").AssertPasses();
        // Banished for good stays banished.
        banish.Values["until"] = "always";
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(p.Card.Id).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,0,LOCATION_REMOVED,1,nil,{Fiend})").AssertPasses();
    }

    [Fact]
    public void EveryGrant_Compiles()
    {
        foreach (var gets in BlockRegistry.Find(MaterialGrantPassive.BlockId)!.Param("gets")!.Choices)
            foreach (var kind in BlockRegistry.Find(MaterialGrantPassive.BlockId)!.Param("kind")!.Choices)
            {
                var p = LinkTwo(100050040, "Granter", new EffectEvent { When = B("when.while_face_up"), Do = [B(MaterialGrantPassive.BlockId, ("kind", kind.Value), ("gets", gets.Value))] });
                Assert.False(ScriptCompiler.Compile(p).HasErrors, $"{kind.Value} {gets.Value}");
            }
        var atk = B(MaterialGrantPassive.BlockId, ("kind", "xyz"), ("gets", "atk"), ("amount", "800"));
        Assert.Equal("An Xyz Monster that used this card as material gains 800 ATK.",
            EffectTextGenerator.Describe(new EffectEvent { When = B("when.while_face_up"), Do = [atk] }, new CardData { Name = "X" }));
    }
}
