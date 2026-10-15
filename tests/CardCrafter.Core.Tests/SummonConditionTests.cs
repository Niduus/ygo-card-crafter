using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Extra Deck materials: what the card says, what the script does, and the Summon in a real duel.</summary>
public class SummonConditionTests
{
    private static string FirstLine(CardProject p, CardPack? pack = null) => EffectTextGenerator.Generate(p, pack).Split('\n')[0];

    /// <summary>S:P Little Knight: 2 Effect Monsters.</summary>
    private static CardProject LittleKnight() => new()
    {
        Card = new CardData { Id = 100004001, Name = "Little Knight", Frame = MonsterFrame.Link, Atk = 1600, LinkMarkers = LinkMarker.Left | LinkMarker.Right },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 2, MaterialFilter = new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Effect } },
    };

    [Fact]
    public void Link_TwoEffectMonsters()
    {
        var p = LittleKnight();
        Assert.Equal("2 Effect Monsters", FirstLine(p));
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("c:IsLinkType(TYPE_EFFECT)", lua);
        Assert.Contains("aux.AddLinkProcedure(c,", lua);
    }

    [EngineFact]
    public void Link_TwoEffectMonsters_SummonsWithEffectMonsters_NotWithNormalOnes()
    {
        var p = LittleKnight();
        new EngineScenario(p).Card(EffectFiller, 0, "mzone", 2).Card(p.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 3)
            .SpSummon(p.Card.Id).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{p.Card.Id})").AssertPasses();
        new EngineScenario(p).Card(Warrior, 0, "mzone", 2).Card(p.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 3)
            .SpSummon(p.Card.Id).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_EXTRA,0,1,nil,{p.Card.Id})").AssertPasses();
    }

    [Fact]
    public void Link_Including()
    {
        var p = LittleKnight();
        p.Procedure.MaterialFilter = new CardFilter { CardType = FilterCardType.Monster };
        p.Procedure.Including = true;
        p.Procedure.IncludeFilter = new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Dark };
        Assert.Equal("2 monsters, including a DARK monster", FirstLine(p));
        Assert.Contains("g:IsExists(", ScriptCompiler.Compile(p).Lua);
    }

    /// <summary>TY-PHON - Sky Crisis: 2 Level 12 monsters.</summary>
    [Fact]
    public void Xyz_TwoLevel12Monsters()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100004002, Name = "Sky Crisis", Frame = MonsterFrame.Xyz, Level = 12, Atk = 2900, Def = 2900 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2 },
        };
        Assert.Equal("2 Level 12 monsters", FirstLine(p));
        Assert.Contains("aux.AddXyzProcedure(c,nil,12,2)", ScriptCompiler.Compile(p).Lua);
        p.Procedure.MaxMaterials = 3;
        p.Procedure.MaterialFilter.Attribute = CardAttribute.Light;
        Assert.Equal("2+ Level 12 LIGHT monsters", FirstLine(p));
    }

    [EngineFact]
    public void Xyz_MaterialLevelCanDifferFromTheRank()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100004003, Name = "Rank 6 from Level 4", Frame = MonsterFrame.Xyz, Level = 6, Atk = 2400, Def = 2000 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2, XyzMaterialLevel = 4 },
        };
        Assert.Equal("2 Level 4 monsters", FirstLine(p));
        new EngineScenario(p).Card(Warrior, 0, "mzone", 2).Card(p.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 3)
            .SpSummon(p.Card.Id).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{p.Card.Id})").AssertPasses();
    }

    [EngineFact]
    public void Synchro_SummonsWithATunerAndANonTuner()
    {
        var tuner = new CardProject { Card = new CardData { Id = 100004011, Name = "Plain Tuner", Frame = MonsterFrame.Normal, Level = 2, Abilities = MonsterAbility.Tuner } };
        var syn = new CardProject
        {
            Card = new CardData { Id = 100004012, Name = "Level 6", Frame = MonsterFrame.Synchro, Level = 6, Atk = 2000 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Synchro, MinMaterials = 1 },
        };
        Assert.Contains("aux.AddSynchroProcedure(c,nil,aux.NonTuner(nil),1)", ScriptCompiler.Compile(syn).Lua);
        new EngineScenario(tuner, syn).Card(tuner.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(syn.Card.Id, 0, "extra")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).SpSummon(syn.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{syn.Card.Id})").AssertPasses();
    }

    [Fact]
    public void Synchro_TunerAndNonTuners()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100004004, Name = "Wing", Frame = MonsterFrame.Synchro, Level = 7, Atk = 2500 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Synchro, MinMaterials = 1 },
        };
        Assert.Equal("1 Tuner + 1+ non-Tuner monsters", FirstLine(p));
        p.Procedure.MaterialFilter.Attribute = CardAttribute.Wind;
        p.Procedure.TunerFilter.Attribute = CardAttribute.Dark;
        Assert.Equal("1 DARK Tuner + 1+ non-Tuner WIND monsters", FirstLine(p));
    }

    /// <summary>Invoked Raidjin-style: 1 "Aleister" monster + 1 LIGHT or EARTH monster.</summary>
    private static (CardProject Card, CardPack Pack) Invoked()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100004005, Name = "Invoked", Frame = MonsterFrame.Fusion, Level = 6, Atk = 2400, Def = 1000 },
            Procedure = new SummonProcedure
            {
                Kind = SummonProcedureKind.Fusion,
                Materials =
                [
                    new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, Archetype = 0x10f4 } },
                    new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Light | CardAttribute.Earth } },
                ],
            },
        };
        var pack = new CardPack { Cards = [p], Archetypes = [new Archetype { Code = 0x10f4, Name = "Aleister" }] };
        return (p, pack);
    }

    [Fact]
    public void Fusion_MaterialsOneByOne()
    {
        var (p, pack) = Invoked();
        Assert.Equal("1 \"Aleister\" monster + 1 LIGHT or EARTH monster", FirstLine(p, pack));
        var r = ScriptCompiler.Compile(p, pack);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics));
        Assert.Contains("aux.AddFusionProcMix(c,true,true,", r.Lua);
        Assert.Contains("c:IsFusionSetCard(0x10f4)", r.Lua);
        Assert.Contains("c:IsFusionAttribute(ATTRIBUTE_EARTH+ATTRIBUTE_LIGHT)", r.Lua);
    }

    [Fact]
    public void Fusion_OneSlotCanTakeSeveral_AndExactCardsAreNamed()
    {
        var (p, pack) = Invoked();
        pack.Cards.Add(new CardProject { Card = new CardData { Id = 46986414, Name = "Dark Magician", Frame = MonsterFrame.Normal, Level = 7 } });
        p.Procedure.Materials[0] = new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, CardCode = 46986414 } };
        p.Procedure.Materials[1].Min = 1;
        p.Procedure.Materials[1].Max = 0;
        Assert.Equal("\"Dark Magician\" + 1+ LIGHT or EARTH monsters", FirstLine(p, pack));
        var r = ScriptCompiler.Compile(p, pack);
        Assert.Contains("aux.AddFusionProcMixRep(c,true,true,", r.Lua);
        Assert.Contains(",1,127,46986414)", r.Lua);

        p.Procedure.Materials[0].Max = 0;
        Assert.Contains(ScriptCompiler.Compile(p, pack).Diagnostics, d => d.Severity == Severity.Error);
    }

    [EngineFact]
    public void ExtraDeckMonsters_WithMaterials_LoadInTheEngine()
    {
        var (fusion, _) = Invoked();
        var synchro = new CardProject
        {
            Card = new CardData { Id = 100004006, Name = "Synchro", Frame = MonsterFrame.Synchro, Level = 8 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Synchro, MinMaterials = 1, MaterialFilter = new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Wind | CardAttribute.Dark } },
        };
        var link = LittleKnight();
        link.Procedure.Including = true;
        link.Procedure.IncludeFilter = new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Dragon | CardRace.Wyrm };
        new EngineScenario(fusion, synchro, link).Card(fusion.Card.Id, 0, "extra").Card(synchro.Card.Id, 0, "extra").Card(link.Card.Id, 0, "extra")
            .Card(Warrior, 0, "deck", 3).AssertPasses();
    }
}
