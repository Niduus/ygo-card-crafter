using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// "Banish this card from your GY; Special Summon 1 Synchro Monster from your Extra Deck. (This is treated as a Synchro
/// Summon.)": the Special Summon blocks' "treated as" option, played in the engine. The monster is really Summoned that
/// way (IsSummonType), counts as properly Summoned (it can be revived later), and needs no materials.
/// </summary>
public class TreatedAsSummonTests
{
    private const long SourceId = 100231001, SynchroId = 100231002, FusionId = 100231003, XyzId = 100231004, LinkId = 100231005, RitualId = 100231006,
        ReviveId = 100231007;

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static string Find(long code, string loc = "LOCATION_MZONE") => $"Duel.GetFirstMatchingCard(Card.IsCode,0,{loc},0,nil,{code})";
    private static string In(long code, string loc) => $"Duel.IsExistingMatchingCard(Card.IsCode,0,{loc},0,1,nil,{code})";

    /// <summary>Extra Deck monsters that must be Summoned their own way ("Must be Synchro Summoned"), so nothing else gets them out.</summary>
    private static CardProject Extra(long id, MonsterFrame frame, SummonProcedureKind kind, bool mustBe = true) => new()
    {
        Card = new CardData
        {
            Id = id, Name = $"{frame} {id}", Frame = frame, Level = 6, Atk = 2400, Def = 1000, OnlyByOwnProcedure = mustBe,
            LinkMarkers = frame == MonsterFrame.Link ? LinkMarker.Bottom | LinkMarker.Top : LinkMarker.None,
        },
        Procedure = kind == SummonProcedureKind.Fusion
            ? new SummonProcedure { Kind = kind, FusionMaterialCodes = [Warrior, Dragon], MinMaterials = 2 }
            : new SummonProcedure { Kind = kind, MinMaterials = 2 },
    };

    private static CardProject RitualMonster() => new()
    {
        Card = new CardData { Id = RitualId, Name = "Ritual Knight", Frame = MonsterFrame.Ritual, Level = 6, Atk = 2400, Def = 1000, Race = CardRace.Warrior },
    };

    /// <summary>"You can banish this card from your GY; Special Summon 1 ... Monster from your Extra Deck."</summary>
    private static CardProject Source(string kind, MonsterKind summoned, Zone from = Zone.ExtraDeck, string zone = "graveyard", string cost = "banish") => new()
    {
        Card = new CardData { Id = SourceId, Name = "Treated Source", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.ignition", ("zone", zone)), Limit = OncePerTurn.Hard,
                Cost = [B("cost.this_card", ("how", cost))],
                Do = [B("do.special_summon", ("as", kind)).With("cards", new CardFilter { Owner = CardOwner.You, Zones = from, CardType = FilterCardType.Monster, MonsterKind = summoned })],
            },
        ],
    };

    private static void Compiles(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
    }

    [Fact]
    public void ReadsLikeOfficialCards()
    {
        Assert.Equal("You can banish this card from your GY; Special Summon 1 Synchro Monster from your Extra Deck. (This is treated as a Synchro Summon.) " +
                     "You can only use this effect of \"Treated Source\" once per turn.", EffectTextGenerator.Generate(Source("synchro", MonsterKind.Synchro)));
        Assert.Equal("You can banish this card from your GY; Special Summon 1 Xyz Monster from your Extra Deck. (This is treated as an Xyz Summon.) " +
                     "You can only use this effect of \"Treated Source\" once per turn.", EffectTextGenerator.Generate(Source("xyz", MonsterKind.Xyz)));
        Assert.Equal("You can banish this card from your hand or GY; Special Summon 1 Ritual Monster from your hand or Deck. (This is treated as a Ritual Summon.) " +
                     "You can only use this effect of \"Treated Source\" once per turn.", EffectTextGenerator.Generate(Source("ritual", MonsterKind.Ritual, Zone.Hand | Zone.Deck, "hand_or_graveyard")));
        // A plain Special Summon has no reminder.
        Assert.DoesNotContain("treated as", EffectTextGenerator.Generate(Source("none", MonsterKind.Synchro)));
    }

    [Theory]
    [InlineData("ritual", "SUMMON_TYPE_RITUAL,tp,false,true", null)]
    [InlineData("fusion", "SUMMON_TYPE_FUSION,tp,false,false", "EFFECT_MUST_BE_FMATERIAL")]
    [InlineData("synchro", "SUMMON_TYPE_SYNCHRO,tp,false,false", "EFFECT_MUST_BE_SMATERIAL")]
    [InlineData("xyz", "SUMMON_TYPE_XYZ,tp,false,false", "EFFECT_MUST_BE_XMATERIAL")]
    [InlineData("link", "SUMMON_TYPE_LINK,tp,false,false", "EFFECT_MUST_BE_LMATERIAL")]
    public void CompilesAsOfficialScripts(string kind, string summon, string? must)
    {
        var p = Source(kind, MonsterKind.Any);
        Compiles(p);
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains($"c:IsCanBeSpecialSummoned(e,{summon})", lua);
        Assert.Contains($"Duel.SpecialSummon(tc,{summon.Replace(",tp,", ",tp,tp,")},POS_FACEUP)", lua);
        Assert.Contains("tc:SetMaterial(nil)", lua);
        Assert.Contains("tc:CompleteProcedure()", lua);
        // The zone this card leaves for the cost counts, as official scripts pass the card being banished.
        Assert.Contains("Duel.GetLocationCountFromEx(tp,tp,e:GetHandler(),c)>0", lua);
        if (must is null) Assert.DoesNotContain("MustMaterialCheck", lua);
        else Assert.Contains($"aux.MustMaterialCheck(nil,tp,{must})", lua);
        Assert.Equal(kind == "fusion", lua.Contains("c:CheckFusionMaterial()", StringComparison.Ordinal));
    }

    [Fact]
    public void MoreThanOneMonster_IsAnError()
    {
        var p = Source("fusion", MonsterKind.Fusion);
        p.Effects[0].Do[0].Values["count"] = "2";
        Assert.True(ScriptCompiler.Compile(p).HasErrors);
        p.Effects[0].Do[0].Values["count"] = "1";
        p.Effects[0].Do[0].Values["position"] = "facedown";
        Assert.True(ScriptCompiler.Compile(p).HasErrors);
    }

    [EngineTheory]
    [InlineData("synchro", MonsterFrame.Synchro, SummonProcedureKind.Synchro, SynchroId)]
    [InlineData("fusion", MonsterFrame.Fusion, SummonProcedureKind.Fusion, FusionId)]
    [InlineData("xyz", MonsterFrame.Xyz, SummonProcedureKind.Xyz, XyzId)]
    [InlineData("link", MonsterFrame.Link, SummonProcedureKind.Link, LinkId)]
    public void BanishFromTheGY_SummonsFromTheExtraDeck_AsThatSummon(string kind, MonsterFrame frame, SummonProcedureKind procedure, long id)
    {
        var extra = Extra(id, frame, procedure);
        var source = Source(kind, frame switch { MonsterFrame.Synchro => MonsterKind.Synchro, MonsterFrame.Fusion => MonsterKind.Fusion, MonsterFrame.Xyz => MonsterKind.Xyz, _ => MonsterKind.Link });
        new EngineScenario(extra, source).Card(SourceId, 0, "grave").Card(id, 0, "extra").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(SourceId).ExpectActivations(1)
            .Check(In(SourceId, "LOCATION_REMOVED"))
            .Check($"{Find(id)}:IsSummonType({TreatedAsSummon.SummonType(kind)})")
            // Properly Summoned: it can come back from the GY later.
            .Check($"{Find(id)}:IsStatus(STATUS_PROC_COMPLETE)")
            .Check($"{Find(id)}:GetMaterialCount()==0")
            .AssertPasses();
    }

    [EngineFact]
    public void APlainSpecialSummon_CannotSummonAMonsterThatMustBeSynchroSummoned()
    {
        var extra = Extra(SynchroId, MonsterFrame.Synchro, SummonProcedureKind.Synchro);
        new EngineScenario(extra, Source("none", MonsterKind.Synchro)).Card(SourceId, 0, "grave").Card(SynchroId, 0, "extra").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(SourceId).ExpectActivations(0).Check(In(SynchroId, "LOCATION_EXTRA")).AssertPasses();
    }

    [EngineFact]
    public void TheSummonedMonster_CanBeRevivedAfterwards()
    {
        // Treated as a Synchro Summon, then sent to the GY by the rigger, then revived by a plain Special Summon from the GY.
        var extra = Extra(SynchroId, MonsterFrame.Synchro, SummonProcedureKind.Synchro, mustBe: false);
        var revive = new CardProject
        {
            Card = new CardData { Id = ReviveId, Name = "Revive", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.special_summon", ("target", "true")).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Synchro })] }],
        };
        var rigger = RequirementMatrix.RiggerCard($"\tif Duel.GetTurnCount()==2 then Duel.SendtoGrave(find({SynchroId},LOCATION_MZONE,0),REASON_EFFECT) end");
        new EngineScenario(extra, Source("synchro", MonsterKind.Synchro), revive, rigger)
            .Card(SourceId, 0, "grave").Card(SynchroId, 0, "extra").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Card(ReviveId, 0, "hand")
            .Activate(SourceId).Turns(3).Activate(ReviveId)
            .Check(In(SynchroId, "LOCATION_MZONE")).Check($"not {Find(SynchroId)}:IsSummonType(SUMMON_TYPE_SYNCHRO)")
            .AssertPasses();
        // Never properly Summoned, it can't be revived.
        new EngineScenario(extra, revive).Card(SynchroId, 0, "grave").Card(ReviveId, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(ReviveId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void TributeThisCard_ForARitualMonsterFromTheHand_WithAFullField()
    {
        var ritual = RitualMonster();
        var source = Source("ritual", MonsterKind.Ritual, Zone.Hand | Zone.Deck, "monster_zone", "tribute");
        Assert.StartsWith("You can Tribute this card; Special Summon 1 Ritual Monster from your hand or Deck. (This is treated as a Ritual Summon.)", EffectTextGenerator.Generate(source));
        // Five monsters, this card among them: its zone is free once it is Tributed.
        new EngineScenario(ritual, source).Card(SourceId, 0, "mzone").Card(Warrior, 0, "mzone", 4).Card(RitualId, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(SourceId).ExpectActivations(1)
            .Check($"{Find(RitualId)}:IsSummonType(SUMMON_TYPE_RITUAL)").Check($"{Find(RitualId)}:IsStatus(STATUS_PROC_COMPLETE)")
            .AssertPasses();
    }

    [EngineTheory]
    [InlineData("hand", "hand", Zone.Deck, "deck")]
    [InlineData("hand", "hand", Zone.Graveyard, "grave")]
    [InlineData("monster_zone", "mzone", Zone.Hand, "hand")]
    [InlineData("monster_zone", "mzone", Zone.Graveyard, "grave")]
    [InlineData("graveyard", "grave", Zone.Hand, "hand")]
    [InlineData("graveyard", "grave", Zone.Deck, "deck")]
    public void BanishThisCard_FromAnywhere_ARitualMonsterFromAnywhere(string trigger, string at, Zone from, string ritualAt)
    {
        // "You can banish this card from your hand; Special Summon 1 Ritual Monster from your GY. (This is treated as a Ritual Summon.)"
        var ritual = RitualMonster();
        var source = Source("ritual", MonsterKind.Ritual, from, trigger);
        new EngineScenario(ritual, source).Card(SourceId, 0, at).Card(RitualId, 0, ritualAt).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(SourceId).ExpectActivations(1)
            .Check(In(SourceId, "LOCATION_REMOVED"))
            .Check($"{Find(RitualId)}:IsSummonType(SUMMON_TYPE_RITUAL)").Check($"{Find(RitualId)}:IsStatus(STATUS_PROC_COMPLETE)")
            .AssertPasses();
    }

    [EngineFact]
    public void SpecialSummonThisCard_TreatedAsARitualSummon()
    {
        var ritual = RitualMonster();
        ritual.Effects =
        [
            new EffectEvent { When = B("when.ignition", ("zone", "hand")), Do = [B("do.special_summon_self", ("as", "ritual"))] },
        ];
        Assert.Equal("You can Special Summon this card from your hand. (This is treated as a Ritual Summon.)", EffectTextGenerator.Generate(ritual));
        new EngineScenario(ritual).Card(RitualId, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(RitualId).ExpectActivations(1)
            .Check($"{Find(RitualId)}:IsSummonType(SUMMON_TYPE_RITUAL)").Check($"{Find(RitualId)}:IsStatus(STATUS_PROC_COMPLETE)")
            .AssertPasses();
        // Without the option, a Ritual Monster can't leave the hand by a plain Special Summon.
        ritual.Effects[0].Do[0].Values["as"] = "none";
        new EngineScenario(ritual).Card(RitualId, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(RitualId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void MustBeMaterial_StopsTheSummon()
    {
        // A monster you control that must be used as Synchro Material (as "Destiny HERO" support does): no Synchro Summon without it.
        var extra = Extra(SynchroId, MonsterFrame.Synchro, SummonProcedureKind.Synchro);
        new EngineScenario(extra, Source("synchro", MonsterKind.Synchro)).Card(SourceId, 0, "grave").Card(SynchroId, 0, "extra").Card(Warrior, 0, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Setup($"local w=Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Warrior}) local e1=Effect.CreateEffect(w) e1:SetType(EFFECT_TYPE_FIELD) e1:SetCode(EFFECT_MUST_BE_SMATERIAL) e1:SetProperty(EFFECT_FLAG_PLAYER_TARGET) e1:SetTargetRange(1,0) e1:SetRange(LOCATION_MZONE) w:RegisterEffect(e1)")
            .Activate(SourceId).ExpectActivations(0).Check(In(SynchroId, "LOCATION_EXTRA")).AssertPasses();
        // The same board without it: the Summon goes ahead.
        new EngineScenario(extra, Source("synchro", MonsterKind.Synchro)).Card(SourceId, 0, "grave").Card(SynchroId, 0, "extra").Card(Warrior, 0, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(SourceId).ExpectActivations(1).Check(In(SynchroId, "LOCATION_MZONE")).AssertPasses();
    }
}
