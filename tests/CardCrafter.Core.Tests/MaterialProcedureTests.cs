using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The Card tab's summon procedures: materials as a group ("with different names", "with different Types and different
/// Attributes", "2+ Fusion Monsters with different Attributes"), level-free Xyz ("with the same Rank"), a Link-1 monster
/// as a Level 1 Tuner, an Extra Deck monster's own procedure, the other way to Xyz Summon with a discard or after an
/// activation, "Must be Special Summoned by a card effect" and a procedure in Attack Position.
/// </summary>
public class MaterialProcedureTests
{
    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    private static string Lua(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics) + "\n" + r.Lua);
        Assert.DoesNotContain(Validation.ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        Assert.Empty(Validation.ScriptChecker.UnknownNames(r.Lua));
        return r.Lua;
    }

    private static CardProject Plain(long id, string name, MonsterFrame frame = MonsterFrame.Normal, int level = 4, CardRace race = CardRace.Warrior,
        CardAttribute attr = CardAttribute.Earth, int archetype = 0) => new()
    {
        Card = new CardData
        {
            Id = id, Name = name, Frame = frame, Level = level, Atk = 1000, Def = 1000, Race = race, Attribute = attr,
            Archetypes = archetype == 0 ? [] : [archetype],
        },
    };

    private static CardProject LinkMonster(long id, string name, int min, int max, MaterialGroupRule rule, MonsterKind kind = MonsterKind.Any)
    {
        var p = Plain(id, name, MonsterFrame.Link);
        p.Card.LinkMarkers = LinkMarker.Bottom | LinkMarker.Top | LinkMarker.Left | LinkMarker.Right;
        p.Procedure = new SummonProcedure
        {
            Kind = SummonProcedureKind.Link, MinMaterials = min, MaxMaterials = max, MaterialRule = rule,
            MaterialFilter = new CardFilter { CardType = FilterCardType.Monster, MonsterKind = kind },
        };
        return p;
    }

    // ---------------------------------------------------------------- Link: with different names (Apollousa)

    private static CardProject Apollousa(long id)
    {
        var p = LinkMonster(id, "Bow " + id, 2, 99, MaterialGroupRule.DifferentNames, MonsterKind.NonToken);
        // Link-2, so 2 monsters are enough.
        p.Card.LinkMarkers = LinkMarker.Bottom | LinkMarker.Top;
        return p;
    }

    [Fact]
    public void DifferentNames_Link_ReadsAndCompilesAsApollousa()
    {
        var p = Apollousa(100230001);
        Assert.StartsWith("2+ monsters with different names, except Tokens", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("aux.AddLinkProcedure(c,s.linkfilter,2,99,s.linkcheck)", lua);
        Assert.Contains("return g:GetClassCount(Card.GetLinkCode)==#g", lua);
    }

    [EngineFact]
    public void DifferentNames_Link_NeedsTwoNames()
    {
        const long id = 100230002;
        new EngineScenario(Apollousa(id)).Card(id, 0, "extra").Card(Warrior, 0, "mzone", 2).SpSummon(id)
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})").AssertPasses();
        new EngineScenario(Apollousa(id)).Card(id, 0, "extra").Card(Warrior, 0, "mzone").Card(Dragon, 0, "mzone").SpSummon(id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})").AssertPasses();
    }

    // ---------------------------------------------------------------- with different Types and different Attributes (Aleister)

    [Fact]
    public void DifferentTypesAndAttributes_ReadsAndCompilesAsAleisterOfMadness()
    {
        var p = LinkMonster(100230003, "Madness", 2, 2, MaterialGroupRule.DifferentTypesAndAttributes);
        p.Card.LinkMarkers = LinkMarker.Bottom | LinkMarker.Top;
        Assert.Equal("2 monsters with different Types and different Attributes", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("aux.AddLinkProcedure(c,nil,2,2,s.linkcheck)", lua);
        Assert.Contains("return g:GetClassCount(Card.GetLinkRace)==#g and g:GetClassCount(Card.GetLinkAttribute)==#g", lua);
    }

    [EngineFact]
    public void DifferentTypesAndAttributes_NeedsBoth()
    {
        const long id = 100230004;
        CardProject P() { var p = LinkMonster(id, "Madness", 2, 2, MaterialGroupRule.DifferentTypesAndAttributes); p.Card.LinkMarkers = LinkMarker.Bottom | LinkMarker.Top; return p; }
        // A second EARTH monster of another Type: the Attributes are the same.
        var earth = Plain(100230005, "Earth Fiend", race: CardRace.Fiend, attr: CardAttribute.Earth);
        new EngineScenario(P(), earth).Card(id, 0, "extra").Card(Warrior, 0, "mzone").Card(earth.Card.Id, 0, "mzone").SpSummon(id)
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})").AssertPasses();
        new EngineScenario(P()).Card(id, 0, "extra").Card(Warrior, 0, "mzone").Card(Fiend, 0, "mzone").SpSummon(id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})").AssertPasses();
    }

    // ---------------------------------------------------------------- Fusion: 2+ Fusion Monsters with different Attributes (Aeon)

    private static CardProject Aeon(long id)
    {
        var p = Plain(id, "Aeon " + id, MonsterFrame.Fusion, 12);
        p.Procedure = new SummonProcedure
        {
            Kind = SummonProcedureKind.Fusion, MaterialRule = MaterialGroupRule.DifferentAttributes,
            Materials = [new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion }, Min = 2, Max = 0 }],
        };
        return p;
    }

    private static CardProject Polymerization(long id) => new()
    {
        Card = new CardData { Id = id, Name = "Poly " + id, Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [B(FusionSummonAction.BlockId).With("mats", "field")] }],
    };

    [Fact]
    public void FusionDifferentAttributes_ReadsAndCompilesAsHyperinvokedAeon()
    {
        var p = Aeon(100230010);
        Assert.StartsWith("2+ Fusion Monsters with different Attributes", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("fm:SetCode(EFFECT_FUSION_MATERIAL)", lua);
        Assert.Contains("return g:GetClassCount(Card.GetAttribute)==#g", lua);
        Assert.Contains("local res=mg:CheckSubGroup(s.fusgoal,2,99,c,gc,tp,chkf)", lua);
    }

    [EngineFact]
    public void FusionDifferentAttributes_NeedsDifferentAttributes()
    {
        const long aeon = 100230011, poly = 100230012;
        var light = Plain(100230013, "Light Fusion", MonsterFrame.Fusion, 6, attr: CardAttribute.Light);
        var light2 = Plain(100230014, "Light Fusion 2", MonsterFrame.Fusion, 6, attr: CardAttribute.Light);
        var dark = Plain(100230015, "Dark Fusion", MonsterFrame.Fusion, 6, attr: CardAttribute.Dark);
        new EngineScenario(Aeon(aeon), Polymerization(poly), light, light2).Card(aeon, 0, "extra").Card(poly, 0, "hand")
            .Card(light.Card.Id, 0, "mzone").Card(light2.Card.Id, 0, "mzone").Activate(poly).ExpectActivations(0).AssertPasses();
        new EngineScenario(Aeon(aeon), Polymerization(poly), light, dark).Card(aeon, 0, "extra").Card(poly, 0, "hand")
            .Card(light.Card.Id, 0, "mzone").Card(dark.Card.Id, 0, "mzone").Activate(poly).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{aeon})")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==3").AssertPasses();
    }

    // ---------------------------------------------------------------- Xyz with the same Rank (Number F0)

    private const int Number = 0x48;

    private static CardPack NumberPack(params CardProject[] cards) => new() { Archetypes = [new Archetype { Code = Number, Name = "Number" }], Cards = [.. cards] };

    private static CardProject UtopicFuture(long id, int count = 2)
    {
        var p = Plain(id, "F0 " + id, MonsterFrame.Xyz, 0);
        p.Procedure = new SummonProcedure
        {
            Kind = SummonProcedureKind.Xyz, MinMaterials = count, XyzSameRank = true,
            MaterialFilter = new CardFilter
            {
                CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz,
                Requirements = [new CardRequirement { Property = "archetype", Op = RequirementOp.IsNot, Values = [Number] }],
            },
        };
        return p;
    }

    private static CardProject XyzOf(long id, int rank, int archetype = 0) => Plain(id, "Rank " + rank + " " + id, MonsterFrame.Xyz, rank, archetype: archetype);

    [Fact]
    public void SameRank_ReadsAndCompilesAsUtopicFuture()
    {
        var p = UtopicFuture(100230020);
        Assert.Equal("2 Xyz Monsters with the same Rank, except \"Number\" monsters", EffectTextGenerator.Generate(p, NumberPack(p)));
        var lua = Lua(p);
        Assert.Contains("aux.AddXyzProcedureLevelFree(c,s.xyzfilter,s.xyzcheck,2,2)", lua);
        Assert.Contains("return g:GetClassCount(Card.GetRank)==1", lua);
        Assert.Contains("c:IsXyzType(TYPE_XYZ)", lua);
        Assert.Contains("not (c:IsSetCard(0x48))", lua);
    }

    [EngineFact]
    public void SameRank_XyzSummonsWithXyzMonstersOfOneRank()
    {
        const long id = 100230021;
        var r4a = XyzOf(100230022, 4);
        var r4b = XyzOf(100230023, 4);
        var r3 = XyzOf(100230024, 3);
        var num = XyzOf(100230025, 4, Number);
        new EngineScenario(UtopicFuture(id), r4a, r4b).Card(id, 0, "extra").Card(r4a.Card.Id, 0, "mzone").Card(r4b.Card.Id, 0, "mzone").SpSummon(id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{id}):GetOverlayCount()==2").AssertPasses();
        new EngineScenario(UtopicFuture(id), r4a, r3).Card(id, 0, "extra").Card(r4a.Card.Id, 0, "mzone").Card(r3.Card.Id, 0, "mzone").SpSummon(id)
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})").AssertPasses();
        new EngineScenario(UtopicFuture(id), r4a, num).Card(id, 0, "extra").Card(r4a.Card.Id, 0, "mzone").Card(num.Card.Id, 0, "mzone").SpSummon(id)
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})").AssertPasses();
    }

    /// <summary>Number F0: Utopic Draco Future: "3 Xyz Monsters with the same Rank", or by using "Number F0: Utopic Future" you control.</summary>
    private static CardProject DracoFuture(long id, long future)
    {
        var p = UtopicFuture(id, 3);
        p.Procedure.XyzAlternate = true;
        p.Procedure.XyzAlternateFilter = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, CardCode = future };
        return p;
    }

    [Fact]
    public void SameRank_WithTheOtherWay_ReadsAndCompilesAsDracoFuture()
    {
        var future = UtopicFuture(100230026);
        future.Card.Name = "Number F0: Utopic Future";
        var p = DracoFuture(100230027, future.Card.Id);
        var pack = NumberPack(future, p);
        var text = EffectTextGenerator.Generate(p, pack);
        Assert.True(text.StartsWith("3 Xyz Monsters", StringComparison.Ordinal), text);
        Assert.StartsWith("3 Xyz Monsters with the same Rank, except \"Number\" monsters\nYou can also Xyz Summon \"F0 100230027\" by using 1 \"Number F0: Utopic Future\" you control.", text);
        var lua = Lua(p);
        Assert.Contains("aux.AddXyzProcedureLevelFree(c,s.xyzfilter,s.xyzcheck,3,3,s.xyzaltfilter,aux.Stringid(id,0))", lua);
    }

    [EngineFact]
    public void SameRank_WithTheOtherWay_SummonsOnTopOfTheMonster()
    {
        var future = UtopicFuture(100230028);
        var p = DracoFuture(100230029, future.Card.Id);
        new EngineScenario(future, p).Card(future.Card.Id, 0, "mzone").Card(p.Card.Id, 0, "extra").SpSummon(p.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{p.Card.Id})")
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{future.Card.Id})").AssertPasses();
    }

    // ---------------------------------------------------------------- Synchro: a Link-1 monster as a Level 1 Tuner (Yummy Way)

    private static CardProject YummyWay(long id)
    {
        var p = Plain(id, "Way " + id, MonsterFrame.Synchro, 2);
        p.Procedure = new SummonProcedure { Kind = SummonProcedureKind.Synchro, MinMaterials = 1, MaxMaterials = 1, SynchroLinkAsTuner = true };
        return p;
    }

    [Fact]
    public void LinkAsTuner_ReadsAndCompilesAsLollipoYummyWay()
    {
        var p = YummyWay(100230030);
        var text = EffectTextGenerator.Generate(p);
        Assert.StartsWith("1 Tuner + 1 non-Tuner monster\nFor this card's Synchro Summon, you can treat 1 Link-1 monster you control as a Level 1 Tuner.", text);
        var lua = Lua(p);
        Assert.Contains("aux.AddSynchroMixProcedure(c,s.synmatfilter1,nil,nil,s.synmatfilter2,1,1)", lua);
        Assert.Contains("return c:IsTuner(syncard) or c:IsType(TYPE_LINK) and c:IsLink(1)", lua);
        Assert.Contains("return c:IsNotTuner(syncard) and not c:IsType(TYPE_LINK)", lua);
        Assert.Contains("slv:SetCode(EFFECT_SYNCHRO_LEVEL_EX)", lua);
    }

    [EngineFact]
    public void LinkAsTuner_SynchroSummonsWithALinkOne()
    {
        const long id = 100230031;
        var link = Plain(100230032, "Link One", MonsterFrame.Link);
        link.Card.LinkMarkers = LinkMarker.Bottom;
        var one = Plain(100230033, "Level One", level: 1);
        new EngineScenario(YummyWay(id), link, one).Card(id, 0, "extra").Card(link.Card.Id, 0, "mzone").Card(one.Card.Id, 0, "mzone").SpSummon(id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==2").AssertPasses();
    }

    // ---------------------------------------------------------------- an Extra Deck monster's own procedure (Snake-Eyes Doomed Dragon)

    private static CardProject DoomedDragon(long id)
    {
        var p = Plain(id, "Doomed " + id, MonsterFrame.Fusion, 8, CardRace.Dragon, CardAttribute.Dark);
        p.Card.SummonRule = SummonRule.ProcedureOnly;
        p.Procedure = new SummonProcedure
        {
            Kind = SummonProcedureKind.Fusion,
            Materials = [new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Warrior } }, new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Dragon } }],
        };
        var sts = new CardFilter
        {
            Owner = CardOwner.You, Zones = Zone.SpellTrapZone, FaceUpOnly = true,
            Requirements = [new CardRequirement { Property = "orig_card_type", Op = RequirementOp.Is, Values = [0x1] }],
        };
        p.Effects.Add(new EffectEvent
        {
            When = B(SummonProcedureTrigger.BlockId).With("zone", "extra"),
            Cost = [B("cost.move_cards").With("how", "grave").With("count", 2).With("cards", sts)],
        });
        return p;
    }

    [Fact]
    public void ExtraDeckProcedure_ReadsAndCompilesAsDoomedDragon()
    {
        var p = DoomedDragon(100230040);
        var text = EffectTextGenerator.Generate(p);
        Assert.Contains("Must be either Fusion Summoned, or Special Summoned by sending 2 face-up Monster Cards from your Spell & Trap Zone to the GY.", text);
        Assert.DoesNotContain("Must be Fusion Summoned.", text);
        var lua = Lua(p);
        Assert.Contains("e1:SetRange(LOCATION_EXTRA)", lua);
        Assert.Contains("Duel.GetLocationCountFromEx(tp,tp,nil,c)>0", lua);
        Assert.Contains("sc:SetValue(aux.fuslimit)", lua);
    }

    [Fact]
    public void ExtraDeckProcedure_OnlyFromTheExtraDeck()
    {
        var p = DoomedDragon(100230041);
        p.Effects[0].When!.Values["zone"] = "hand";
        Assert.True(ScriptCompiler.Compile(p).HasErrors);
        var main = Plain(100230042, "Main", MonsterFrame.Effect);
        main.Effects.Add(new EffectEvent { When = B(SummonProcedureTrigger.BlockId).With("zone", "extra") });
        Assert.True(ScriptCompiler.Compile(main).HasErrors);
    }

    [EngineFact]
    public void ExtraDeckProcedure_SummonsBySendingMonsterCardsFromTheSpellTrapZone()
    {
        const long id = 100230043;
        // The harness places cards in the Spell & Trap Zone face-down, so here they need not be face-up.
        var p = DoomedDragon(id);
        p.Effects[0].Cost[0].Filters["cards"].FaceUpOnly = false;
        new EngineScenario(p).Card(id, 0, "extra").Card(Warrior, 0, "szone").Card(Dragon, 0, "szone").SpSummon(id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==2").AssertPasses();
    }

    // ---------------------------------------------------------------- the other way to Xyz Summon: discarding, after an activation

    private static CardProject GustavRocket(long id, long max)
    {
        var p = Plain(id, "Rocket " + id, MonsterFrame.Xyz, 10, CardRace.Machine);
        p.Procedure = new SummonProcedure
        {
            Kind = SummonProcedureKind.Xyz, MinMaterials = 3, XyzAlternate = true, XyzAlternateOncePerTurn = true, XyzAlternateDiscard = 1,
            XyzAlternateFilter = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, CardCode = max },
        };
        return p;
    }

    [Fact]
    public void AlternateDiscard_ReadsAndCompilesAsGustavRocket()
    {
        var max = XyzOf(100230050, 10);
        max.Card.Name = "Superdreadnought Rail Cannon Gustav Max";
        var p = GustavRocket(100230051, max.Card.Id);
        p.Card.Name = "Superdreadnought Rail Cannon Gustav Rocket";
        var text = EffectTextGenerator.Generate(p, new CardPack { Cards = [max, p] });
        Assert.Contains("Once per turn, you can also Xyz Summon \"Superdreadnought Rail Cannon Gustav Rocket\" by discarding 1 card and using 1 \"Superdreadnought Rail Cannon Gustav Max\" you control. (Transfer its materials to this card.)", text);
        var lua = Lua(p);
        Assert.Contains("if chk==0 then return Duel.GetFlagEffect(tp,id+7200)==0 and Duel.IsExistingMatchingCard(s.xyzdiscardfilter,tp,LOCATION_HAND,0,1,nil) end", lua);
        Assert.Contains("Duel.DiscardHand(tp,s.xyzdiscardfilter,1,1,REASON_COST+REASON_DISCARD)", lua);
        Assert.Contains("return c:IsDiscardable(REASON_SPSUMMON)", lua);
    }

    [EngineFact]
    public void AlternateDiscard_DiscardsAndSummons()
    {
        var max = XyzOf(100230052, 10);
        var p = GustavRocket(100230053, max.Card.Id);
        new EngineScenario(max, p).Card(max.Card.Id, 0, "mzone").Card(p.Card.Id, 0, "extra").SpSummon(p.Card.Id)
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{p.Card.Id})").AssertPasses();
        new EngineScenario(max, p).Card(max.Card.Id, 0, "mzone").Card(p.Card.Id, 0, "extra").Card(Warrior, 0, "hand").SpSummon(p.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{p.Card.Id})")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
    }

    private static CardProject AriseHeart(long id, long shangri)
    {
        var p = Plain(id, "Arise " + id, MonsterFrame.Xyz, 7);
        p.Procedure = new SummonProcedure
        {
            Kind = SummonProcedureKind.Xyz, MinMaterials = 3, XyzAlternate = true, XyzAlternateOncePerTurn = true, XyzAlternateAfterActivationOf = shangri,
            XyzAlternateFilter = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Any },
        };
        return p;
    }

    private static CardProject DrawSpell(long id) => new()
    {
        Card = new CardData { Id = id, Name = "Shangri " + id, Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.draw").With("count", 1)] }],
    };

    [Fact]
    public void AlternateAfterActivation_ReadsAndCompilesAsAriseHeart()
    {
        var shangri = DrawSpell(100230060);
        shangri.Card.Name = "Kashtira Shangri-Ira";
        var p = AriseHeart(100230061, shangri.Card.Id);
        var text = EffectTextGenerator.Generate(p, new CardPack { Cards = [shangri, p] });
        Assert.Contains("by using 1 monster you control, if an effect of \"Kashtira Shangri-Ira\" was successfully activated this turn. (Transfer its materials to this card.)", text);
        var lua = Lua(p);
        Assert.Contains("Duel.AddCustomActivityCounter(id+1000000000,ACTIVITY_CHAIN,s.xyzchainfilter)", lua);
        Assert.Contains("return not re:GetHandler():IsCode(100230060)", lua);
        Assert.Contains("(Duel.GetCustomActivityCount(id+1000000000,tp,ACTIVITY_CHAIN)>0 or Duel.GetCustomActivityCount(id+1000000000,1-tp,ACTIVITY_CHAIN)>0)", lua);
    }

    [EngineFact]
    public void AlternateAfterActivation_OnlyAfterThatCardWasActivated()
    {
        var shangri = DrawSpell(100230062);
        var p = AriseHeart(100230063, shangri.Card.Id);
        new EngineScenario(shangri, p).Card(Warrior, 0, "mzone").Card(p.Card.Id, 0, "extra").Card(shangri.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 3).SpSummon(p.Card.Id)
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{p.Card.Id})").AssertPasses();
        new EngineScenario(shangri, p).Card(Warrior, 0, "mzone").Card(p.Card.Id, 0, "extra").Card(shangri.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 3)
            .Activate(shangri.Card.Id).SpSummon(p.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{p.Card.Id})").AssertPasses();
    }

    // ---------------------------------------------------------------- Must be Special Summoned by a card effect (Machina Citadel)

    private static CardProject Citadel(long id)
    {
        var p = Plain(id, "Citadel " + id, MonsterFrame.Effect, 4, CardRace.Machine);
        p.Card.SummonRule = SummonRule.ByCardEffect;
        p.Effects.Add(new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.indestructible")] });
        return p;
    }

    private static CardProject Reborn(long id) => new()
    {
        Card = new CardData { Id = id, Name = "Reborn " + id, Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.special_summon").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster })] }],
    };

    [Fact]
    public void ByCardEffect_ReadsAndCompilesAsMachinaCitadel()
    {
        var p = Citadel(100230070);
        Assert.StartsWith("Cannot be Normal Summoned/Set. Must be Special Summoned by a card effect.", EffectTextGenerator.Generate(p));
        var r = ScriptCompiler.Compile(p);
        Assert.DoesNotContain(r.Diagnostics, d => d.Message.Contains("procedure", StringComparison.Ordinal));
        var lua = Lua(p);
        Assert.DoesNotContain("EnableReviveLimit", lua);
        Assert.Contains("esc:SetCode(EFFECT_SPSUMMON_CONDITION)", lua);
        Assert.Contains("return se:IsHasType(EFFECT_TYPE_ACTIONS)", lua);
        Assert.True(p.Card.Abilities.HasFlag(MonsterAbility.SpecialSummonOnly));
        // The rule is kept as it is saved.
        var copy = PackSerializer.Clone(p.Card);
        Assert.Equal(SummonRule.ByCardEffect, copy.SummonRule);
    }

    [EngineFact]
    public void ByCardEffect_CannotBeNormalSummonedButAnEffectRevivesIt()
    {
        const long id = 100230071, reborn = 100230072;
        new EngineScenario(Citadel(id), Reborn(reborn)).Card(id, 0, "hand").Summon(id)
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})").AssertPasses();
        new EngineScenario(Citadel(id), Reborn(reborn)).Card(id, 0, "grave").Card(reborn, 0, "hand").Activate(reborn).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})").AssertPasses();
    }

    // ---------------------------------------------------------------- a procedure in Attack Position (Jester Confit)

    private static CardProject Confit(long id)
    {
        var p = Plain(id, "Confit " + id, MonsterFrame.Effect, 1);
        p.Effects.Add(new EffectEvent { When = B(SummonProcedureTrigger.BlockId).With("position", "attack") });
        return p;
    }

    [Fact]
    public void AttackPosition_ReadsAndCompilesAsJesterConfit()
    {
        var p = Confit(100230080);
        Assert.StartsWith("You can Special Summon this card (from your hand) in Attack Position.", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("e1:SetProperty(EFFECT_FLAG_UNCOPYABLE+EFFECT_FLAG_SPSUM_PARAM)", lua);
        Assert.Contains("e1:SetTargetRange(POS_FACEUP_ATTACK,0)", lua);
    }

    [EngineFact]
    public void AttackPosition_SummonsInAttackPosition()
    {
        const long id = 100230081;
        new EngineScenario(Confit(id)).Card(id, 0, "hand").SpSummon(id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsPosition,0,LOCATION_MZONE,0,1,nil,POS_FACEUP_ATTACK)").AssertPasses();
    }
}
