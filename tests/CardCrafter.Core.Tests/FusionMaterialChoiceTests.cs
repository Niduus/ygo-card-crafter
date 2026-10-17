using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// "Fusion Summon" with materials counted per place (Invocation - "Chalice"), more materials for some Fusion Monsters
/// (Invocation, Invocation - "Sword"), "including a ... monster you control" (Omega Vidolium), "up to 1 each from"
/// (Splendor of the Winter Raven Witch) and the Fusion Monster "that mentions a "HERO" monster as material".
/// </summary>
public class FusionMaterialChoiceTests
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

    private const int Invoked = 0x1f4, Patron = 0x1f5, Hero = 0x1f6;

    private static CardPack Pack(params CardProject[] cards) => new()
    {
        Archetypes = [new Archetype { Code = Invoked, Name = "Invoked" }, new Archetype { Code = Patron, Name = "Power Patron" }, new Archetype { Code = Hero, Name = "HERO" }],
        Cards = [.. cards],
    };

    private static CardProject Spell(long id, BlockInstance fusion) => new()
    {
        Card = new CardData { Id = id, Name = "Fuse " + id, Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [fusion] }],
    };

    /// <summary>A Fusion Monster for "any 2 monsters", of an archetype or none.</summary>
    private static CardProject FusionOf(long id, int archetype = 0, MaterialSlot[]? slots = null) => new()
    {
        Card = new CardData
        {
            Id = id, Name = "Fusion " + id, Frame = MonsterFrame.Fusion, Level = 6, Atk = 2400, Def = 2000, Race = CardRace.Fiend, Attribute = CardAttribute.Dark,
            Archetypes = archetype == 0 ? [] : [archetype],
        },
        Procedure = new SummonProcedure
        {
            Kind = SummonProcedureKind.Fusion,
            Materials = slots is null ? [new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster }, Min = 2, Max = 2 }] : [.. slots],
        },
    };

    private static CardFilter FusionPick(int archetype = 0) => new()
    {
        Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion, Archetype = archetype,
    };

    private static string OnField(long id, int player = 0) => $"Duel.IsExistingMatchingCard(Card.IsCode,{player},LOCATION_MZONE,0,1,nil,{id})";

    // ---------------------------------------------------------------- Invocation - "Chalice": 1 from each place

    private static CardProject Chalice(long id, string mats) => Spell(id, B(FusionSummonAction.BlockId).With("mats", mats).With("cards", FusionPick(Invoked)));

    [Fact]
    public void PerPlace_ReadsAndCompilesAsChalice()
    {
        var hand = Chalice(100240001, "hand_field_and_deck_banish");
        Assert.Equal("Fusion Summon 1 \"Invoked\" Fusion Monster from your Extra Deck, by banishing 1 monster from your hand or field and 1 monster from your Deck.",
            EffectTextGenerator.Generate(hand, Pack(hand)));
        var lua = Lua(hand);
        // A part of the materials may have at most 1 from each place; all of them, exactly 1.
        Assert.Contains("return sg:FilterCount(Card.IsLocation,nil,LOCATION_HAND+LOCATION_ONFIELD)<=1 and sg:FilterCount(Card.IsLocation,nil,LOCATION_DECK)<=1", lua);
        Assert.Contains("return sg:FilterCount(Card.IsLocation,nil,LOCATION_HAND+LOCATION_ONFIELD)==1 and sg:FilterCount(Card.IsLocation,nil,LOCATION_DECK)==1", lua);
        Assert.Contains("aux.FCheckAdditional=s.fuscheck", lua);
        Assert.Contains("aux.FGoalCheckAdditional=s.fusgoalcheck", lua);
        Assert.Contains("Duel.Remove(mat1,POS_FACEUP,REASON_EFFECT+REASON_MATERIAL+REASON_FUSION)", lua);
        var opp = Chalice(100240002, "field_and_opponent_banish");
        Assert.Equal("Fusion Summon 1 \"Invoked\" Fusion Monster from your Extra Deck, by banishing 1 monster you control and 1 face-up monster your opponent controls.",
            EffectTextGenerator.Generate(opp, Pack(opp)));
        Assert.Contains("return sg:FilterCount(Card.IsControler,nil,tp)==1 and sg:FilterCount(Card.IsControler,nil,1-tp)==1", Lua(opp));
        Assert.Contains("return sg:FilterCount(Card.IsControler,nil,tp)<=1 and sg:FilterCount(Card.IsControler,nil,1-tp)<=1", Lua(opp));
    }

    [EngineFact]
    public void PerPlace_BanishesOneFromTheHandAndOneFromTheDeck()
    {
        const long spell = 100240003, fusion = 100240004;
        new EngineScenario(Chalice(spell, "hand_field_and_deck_banish"), FusionOf(fusion, Invoked)).Card(spell, 0, "hand").Card(fusion, 0, "extra")
            .Card(Warrior, 0, "hand", 2).Card(Dragon, 0, "deck").Activate(spell).ExpectActivations(1)
            .Check(OnField(fusion))
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_HAND,0,nil,1001)==1")
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_REMOVED,0,nil,1001)==1")
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_REMOVED,0,nil,1002)==1").AssertPasses();
        // Two from the hand don't do: one must come from the Deck.
        new EngineScenario(Chalice(spell, "hand_field_and_deck_banish"), FusionOf(fusion, Invoked)).Card(spell, 0, "hand").Card(fusion, 0, "extra")
            .Card(Warrior, 0, "hand", 2).Activate(spell).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void PerPlace_BanishesYourMonsterAndTheOpponents()
    {
        const long spell = 100240005, fusion = 100240006;
        new EngineScenario(Chalice(spell, "field_and_opponent_banish"), FusionOf(fusion, Invoked)).Card(spell, 0, "hand").Card(fusion, 0, "extra")
            .Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone").Activate(spell).ExpectActivations(1)
            .Check(OnField(fusion))
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_REMOVED,0,nil,1001)==1")
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,1,LOCATION_REMOVED,0,nil,1004)==1").AssertPasses();
        new EngineScenario(Chalice(spell, "field_and_opponent_banish"), FusionOf(fusion, Invoked)).Card(spell, 0, "hand").Card(fusion, 0, "extra")
            .Card(Warrior, 0, "mzone", 2).Activate(spell).ExpectActivations(0).AssertPasses();
    }

    // ---------------------------------------------------------------- Invocation and "Sword": more materials for some monsters

    private static CardProject Invocation(long id, string mats, string also) => Spell(id, B(FusionSummonAction.BlockId).With("mats", mats).With("also", also)
        .With("alsocards", FusionPick(Invoked)));

    [Fact]
    public void AlsoMaterials_ReadAndCompileAsInvocation()
    {
        var p = Invocation(100240010, "hand", "banish_field_gy");
        Assert.Equal("Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters from your hand as Fusion Material. If Summoning an \"Invoked\" Fusion Monster " +
                     "this way, you can also banish monsters from your field and/or either player's GY as material.", EffectTextGenerator.Generate(p, Pack(p)));
        var lua = Lua(p);
        Assert.Contains("mg:Merge(Duel.GetMatchingGroup(aux.NecroValleyFilter(s.fusalsogyfilter),tp,LOCATION_GRAVE,LOCATION_GRAVE,nil,e,tp,chk))", lua);
        Assert.Contains("if #xmat>0 then Duel.Remove(xmat,POS_FACEUP,REASON_EFFECT+REASON_MATERIAL+REASON_FUSION) end", lua);
        var sword = Invocation(100240011, "field", "return_banished");
        Assert.Contains("If Summoning an \"Invoked\" Fusion Monster this way, you can also return monsters from any banishment to the GY as material.",
            EffectTextGenerator.Generate(sword, Pack(sword)));
        Assert.Contains("Duel.SendtoGrave(xmat,REASON_EFFECT+REASON_RETURN+REASON_MATERIAL+REASON_FUSION)", Lua(sword));
    }

    [EngineFact]
    public void AlsoMaterials_InvokedMonsterBanishesFromTheOpponentsGY()
    {
        const long spell = 100240012, invoked = 100240013, other = 100240014;
        // The "Invoked" one may use the opponent's GY; the other only the hand, where there is 1 monster.
        new EngineScenario(Invocation(spell, "hand", "banish_field_gy"), FusionOf(invoked, Invoked)).Card(spell, 0, "hand").Card(invoked, 0, "extra")
            .Card(Warrior, 0, "hand").Card(Fiend, 1, "grave").Activate(spell).ExpectActivations(1)
            .Check(OnField(invoked))
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_GRAVE,0,nil,1001)==1")
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,1,LOCATION_REMOVED,0,nil,1004)==1").AssertPasses();
        new EngineScenario(Invocation(spell, "hand", "banish_field_gy"), FusionOf(other)).Card(spell, 0, "hand").Card(other, 0, "extra")
            .Card(Warrior, 0, "hand").Card(Fiend, 1, "grave").Activate(spell).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void AlsoMaterials_SwordReturnsBanishedMonstersToTheGY()
    {
        const long spell = 100240015, invoked = 100240016;
        new EngineScenario(Invocation(spell, "field", "return_banished"), FusionOf(invoked, Invoked)).Card(spell, 0, "hand").Card(invoked, 0, "extra")
            .Card(Warrior, 0, "mzone").Card(Fiend, 1, "removed").Activate(spell).ExpectActivations(1)
            .Check(OnField(invoked))
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,1,LOCATION_GRAVE,0,nil,1004)==1").AssertPasses();
    }

    // ---------------------------------------------------------------- including a "Power Patron" monster you control

    private static CardProject Vidolium(long id) => Spell(id, B(FusionSummonAction.BlockId).With("mats", "either_field").With("including", true)
        .With("inccards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Archetype = Patron }));

    private static CardProject PatronMonster(long id) => new()
    {
        Card = new CardData { Id = id, Name = "Patron " + id, Frame = MonsterFrame.Normal, Level = 4, Atk = 1000, Def = 1000, Archetypes = [Patron] },
    };

    [Fact]
    public void Including_ReadsAndCompilesAsOmegaVidolium()
    {
        var p = Vidolium(100240020);
        Assert.Equal("Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters from either field as Fusion Material, including a \"Power Patron\" monster you control.",
            EffectTextGenerator.Generate(p, Pack(p)));
        var lua = Lua(p);
        Assert.Contains("return c:IsLocation(LOCATION_MZONE) and c:IsControler(tp) and c:IsSetCard(0x1f5)", lua);
        Assert.Contains("return sg:IsExists(s.fusincfilter,1,nil,tp)", lua);
        Assert.Contains("aux.FGoalCheckAdditional=s.fusgoalcheck", lua);
    }

    [EngineFact]
    public void Including_NeedsThatMonster()
    {
        const long spell = 100240021, fusion = 100240022, patron = 100240023;
        new EngineScenario(Vidolium(spell), FusionOf(fusion), PatronMonster(patron)).Card(spell, 0, "hand").Card(fusion, 0, "extra")
            .Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone").Activate(spell).ExpectActivations(0).AssertPasses();
        new EngineScenario(Vidolium(spell), FusionOf(fusion), PatronMonster(patron)).Card(spell, 0, "hand").Card(fusion, 0, "extra")
            .Card(patron, 0, "mzone").Card(Fiend, 1, "mzone").Activate(spell).ExpectActivations(1)
            .Check(OnField(fusion)).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{patron})").AssertPasses();
    }

    // ---------------------------------------------------------------- up to 1 each from your hand, field, and/or Extra Deck

    private static CardProject Splendor(long id) => Spell(id, B(FusionSummonAction.BlockId).With("mats", "hand_field_extra").With("each", true));

    [Fact]
    public void UpToOneEach_ReadsAndCompilesAsSplendor()
    {
        var p = Splendor(100240030);
        Assert.Equal("Fusion Summon 1 Fusion Monster from your Extra Deck, using up to 1 monster each from your hand, field, and/or Extra Deck.", EffectTextGenerator.Generate(p));
        Assert.Contains("return sg:FilterCount(Card.IsLocation,nil,LOCATION_HAND)<=1 and sg:FilterCount(Card.IsLocation,nil,LOCATION_ONFIELD)<=1 and sg:FilterCount(Card.IsLocation,nil,LOCATION_EXTRA)<=1",
            Lua(p));
    }

    [EngineFact]
    public void UpToOneEach_TakesOneFromEachPlace()
    {
        const long spell = 100240031, fusion = 100240032;
        new EngineScenario(Splendor(spell), FusionOf(fusion)).Card(spell, 0, "hand").Card(fusion, 0, "extra")
            .Card(Warrior, 0, "hand", 2).Activate(spell).ExpectActivations(0).AssertPasses();
        new EngineScenario(Splendor(spell), FusionOf(fusion)).Card(spell, 0, "hand").Card(fusion, 0, "extra")
            .Card(Warrior, 0, "hand", 2).Card(Dragon, 0, "mzone").Activate(spell).ExpectActivations(1)
            .Check(OnField(fusion)).Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_HAND,0,nil,1001)==1").AssertPasses();
    }

    // ---------------------------------------------------------------- the Fusion Monster that mentions a "HERO" monster as material

    private static CardFilter MentionsHero() => new()
    {
        Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion,
        Requirements = [new CardRequirement { Property = "material_archetype", Op = RequirementOp.Is, Values = [Hero] }],
    };

    [Fact]
    public void MentionsAsMaterial_ReadsAndCompilesAsHopeForTheFuture()
    {
        var p = Spell(100240040, B(FusionSummonAction.BlockId).With("mats", "hand_field_deck").With("cards", MentionsHero()));
        Assert.Equal("Fusion Summon 1 Fusion Monster that mentions a \"HERO\" monster as material from your Extra Deck, using monsters from your hand, field, or Deck as Fusion Material.",
            EffectTextGenerator.Generate(p, Pack(p)));
        Assert.Contains("aux.IsMaterialListSetCard(c,0x1f6)", Lua(p));
        // A Fusion Monster whose materials name an archetype says so for such cards.
        var hero = FusionOf(100240041, slots: [new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, Archetype = Hero } }, new MaterialSlot()]);
        Assert.Contains("s.material_setcode=0x1f6", Lua(hero));
    }

    [EngineFact]
    public void MentionsAsMaterial_PicksOnlyThatFusionMonster()
    {
        const long spell = 100240042, hero = 100240043, other = 100240044, heroMon = 100240045;
        var heroFusion = FusionOf(hero, slots: [new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, Archetype = Hero } }, new MaterialSlot()]);
        var otherFusion = FusionOf(other);
        var heroCard = new CardProject { Card = new CardData { Id = heroMon, Name = "Hero " + heroMon, Frame = MonsterFrame.Normal, Level = 4, Atk = 1000, Def = 1000, Archetypes = [Hero] } };
        var p = Spell(spell, B(FusionSummonAction.BlockId).With("mats", "hand_field_deck").With("cards", MentionsHero()));
        new EngineScenario(p, heroFusion, otherFusion, heroCard).Card(spell, 0, "hand").Card(other, 0, "extra").Card(heroMon, 0, "hand").Card(Warrior, 0, "hand")
            .Activate(spell).ExpectActivations(0).AssertPasses();
        new EngineScenario(p, heroFusion, otherFusion, heroCard).Card(spell, 0, "hand").Card(other, 0, "extra").Card(hero, 0, "extra").Card(heroMon, 0, "hand").Card(Warrior, 0, "hand")
            .Activate(spell).ExpectActivations(1).Check(OnField(hero)).AssertPasses();
    }
}
