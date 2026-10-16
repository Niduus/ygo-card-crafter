using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Ritual and Fusion Summons by effect in every way the blocks offer, and Contact Fusion, played in the engine: the
/// monster is Summoned, its materials go where the card says, and nothing can be activated without valid materials.
/// </summary>
public class RitualFusionTests
{
    private const long RitualId = 100098001, Ritual2Id = 100098002, SpellId = 100098010, FusionId = 100098020, ExtraLv4 = 100098021,
        PendId = 100098030, SelfId = 100098040, ContactId = 100098050, PolyId = 100098060;

    private static CardProject RitualMonster(long id = RitualId, int level = 8, int atk = 2500) => new()
    {
        Card = new CardData { Id = id, Name = $"Ritual {id}", Frame = MonsterFrame.Ritual, Level = level, Atk = atk, Def = 1000, Race = CardRace.Warrior },
    };

    private static BlockInstance Rit(string source = "hand_field", string how = "greater", string stat = "level", int max = 0, string after = "none", CardFilter? cards = null, CardFilter? mats = null, CardFilter? mats2 = null)
    {
        var b = BlockRegistry.Find(RitualSummonAction.BlockId)!.CreateInstance().With("source", source).With("how", how).With("stat", stat).With("max", max).With("after", after);
        if (cards is not null) b.Filters["cards"] = cards;
        if (mats is not null) b.Filters["mats"] = mats;
        if (mats2 is not null) b.Filters["mats2"] = mats2;
        return b;
    }

    private static CardProject RitualSpell(params BlockInstance[] steps) => new()
    {
        Card = new CardData { Id = SpellId, Name = "Test Ritual Spell", Kind = CardKind.Spell, SpellKind = SpellKind.Ritual },
        Effects = [new EffectEvent { When = new BlockInstance("when.activate"), Do = [.. steps] }],
    };

    private static string On(long code, int p = 0, string loc = "LOCATION_MZONE") => $"Duel.IsExistingMatchingCard(Card.IsCode,{p},{loc},0,1,nil,{code})";
    private static string Count(long code, string loc, int n, int p = 0) => $"Duel.GetMatchingGroupCount(Card.IsCode,{p},{loc},0,nil,{code})=={n}";
    private static string Card(long code, int p = 0, string loc = "LOCATION_MZONE") => $"Duel.GetFirstMatchingCard(Card.IsCode,{p},{loc},0,nil,{code})";
    private static EngineScenario Decks(EngineScenario s) => s.Card(Dragon, 0, "deck", 3).Card(Dragon, 1, "deck", 3);

    private static void Compiles(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
    }

    // ---------------------------------------------------------------- Ritual: wording

    [Theory]
    [InlineData("hand_field", "greater", "level", 0, "Ritual Summon 1 Ritual Monster from your hand, by Tributing monsters from your hand or field whose total Levels equal or exceed its Level.")]
    [InlineData("hand_field_gy", "greater", "level", 0, "Ritual Summon 1 Ritual Monster from your hand, by Tributing monsters from your hand or field, and/or banishing monsters from your GY, whose total Levels equal or exceed its Level.")]
    [InlineData("gy", "equal", "level", 0, "Ritual Summon 1 Ritual Monster from your hand, by banishing monsters from your GY whose total Levels equal its Level.")]
    [InlineData("deck", "equal", "level", 0, "Ritual Summon 1 Ritual Monster from your hand, by sending monsters from your Deck to the GY whose total Levels equal its Level.")]
    [InlineData("hand_field_extra", "greater", "level", 0, "Ritual Summon 1 Ritual Monster from your hand, by Tributing monsters from your hand or field, and/or sending monsters from your Extra Deck to the GY, whose total Levels equal or exceed its Level.")]
    [InlineData("hand_field_opponent", "equal", "level", 0, "Ritual Summon 1 Ritual Monster from your hand, by Tributing monsters from your hand or field, and/or face-up monsters your opponent controls, whose total Levels equal its Level.")]
    [InlineData("field", "greater", "atk", 0, "Ritual Summon 1 Ritual Monster from your hand, by Tributing monsters you control whose total ATK equal or exceed its ATK.")]
    [InlineData("hand_field", "greater", "level", 1, "Ritual Summon 1 Ritual Monster from your hand, by Tributing 1 monster from your hand or field whose Level equals or exceeds its Level.")]
    [InlineData("hand_field", "equal", "level", 2, "Ritual Summon 1 Ritual Monster from your hand, by Tributing up to 2 monsters from your hand or field whose total Levels equal its Level.")]
    public void Ritual_ReadsLikeACard(string source, string how, string stat, int max, string text)
    {
        var p = RitualSpell(Rit(source, how, stat, max));
        Assert.Equal(text, EffectTextGenerator.Generate(p));
        Compiles(p);
    }

    [Fact]
    public void Ritual_FixedLevel_ReadsAsTheNumber_WhenTheRitualMonsterIsInThePack()
    {
        // Black Luster Ritual: "You must also Tribute monsters from your hand or field whose total Levels equal 8 or more."
        var ritual = RitualMonster();
        var spell = RitualSpell(Rit(stat: "original", cards: new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Ritual, CardCode = RitualId }));
        var pack = new CardPack { Cards = [ritual, spell] };
        Assert.Equal($"Ritual Summon 1 \"Ritual {RitualId}\" from your hand, by Tributing monsters from your hand or field whose total Levels equal 8 or more.", EffectTextGenerator.Generate(spell, pack));
        Assert.Contains("Card.GetOriginalLevel", ScriptCompiler.Compile(spell).Lua);
    }

    [Fact]
    public void Ritual_Afterwards_ReadLikeCards()
    {
        Assert.EndsWith(", and if you do, it cannot be destroyed by card effects, and its effects cannot be negated.", EffectTextGenerator.Generate(RitualSpell(Rit(after: "indestructible_unnegatable"))));
        Assert.EndsWith(", but it cannot attack this turn.", EffectTextGenerator.Generate(RitualSpell(Rit(after: "cannot_attack"))));
        Assert.EndsWith(", but destroy it during the End Phase.", EffectTextGenerator.Generate(RitualSpell(Rit(after: "destroy_end"))));
    }

    [Fact]
    public void Ritual_ByAtk_NeedsTributesFromTheHandOrField()
    {
        var r = ScriptCompiler.Compile(RitualSpell(Rit("hand_field_gy", stat: "atk")));
        Assert.True(r.HasErrors);
    }

    [Fact]
    public void Ritual_SavedBeforeTheNewOptions_KeepsItsWordingAndWay()
    {
        // A block saved with only "cards", "mats" and "how" reads and compiles as before: Tributes from the hand or field.
        var old = new BlockInstance(RitualSummonAction.BlockId).With("how", "equal");
        var p = RitualSpell(old);
        Assert.Equal("Ritual Summon 1 Ritual Monster from your hand, by Tributing monsters from your hand or field whose total Levels equal its Level.", EffectTextGenerator.Generate(p));
        var json = PackSerializer.Clone(p);
        Assert.Equal(EffectTextGenerator.Generate(p), EffectTextGenerator.Generate(json));
        Compiles(p);
    }

    // ---------------------------------------------------------------- Ritual: in the engine

    [EngineFact]
    public void Ritual_TributesAndBanishesFromTheGY()
    {
        var ritual = RitualMonster();
        var spell = RitualSpell(Rit("hand_field_gy"));
        Decks(new EngineScenario(ritual, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 0, "grave")
            .Activate(SpellId).ExpectActivations(1)
            .Check(On(RitualId)).Check(Count(Warrior, "LOCATION_GRAVE", 1)).Check(Count(Fiend, "LOCATION_REMOVED", 1))
            .Check($"{Card(RitualId)}:IsSummonType(SUMMON_TYPE_RITUAL)").AssertPasses();
    }

    [EngineFact]
    public void Ritual_TributesAndBanishesFromTheGY_NotWithoutEnoughLevels()
    {
        var ritual = RitualMonster();
        var spell = RitualSpell(Rit("hand_field_gy"));
        Decks(new EngineScenario(ritual, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone")
            .Activate(SpellId).ExpectActivations(0).Check(On(RitualId, 0, "LOCATION_HAND")).AssertPasses();
    }

    [EngineFact]
    public void Ritual_BanishesFromTheGYOnly()
    {
        var ritual = RitualMonster();
        var spell = RitualSpell(Rit("gy", "equal"));
        Decks(new EngineScenario(ritual, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "grave", 2).Card(Fiend, 0, "mzone", 2)
            .Activate(SpellId).ExpectActivations(1)
            .Check(On(RitualId)).Check(Count(Warrior, "LOCATION_REMOVED", 2)).Check(Count(Fiend, "LOCATION_MZONE", 2)).AssertPasses();
        // The monsters on the field don't count.
        Decks(new EngineScenario(ritual, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "grave").Card(Fiend, 0, "mzone", 2)
            .Activate(SpellId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Ritual_SendsNormalMonstersFromTheDeck_AdvancedRitualArt()
    {
        var ritual = RitualMonster();
        var spell = RitualSpell(Rit("deck", "equal", mats: new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Normal }));
        new EngineScenario(ritual, spell).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "deck", 2).Card(EffectFiller, 0, "deck", 2).Card(Dragon, 1, "deck", 3)
            .Card(Fiend, 0, "mzone", 2)
            .Activate(SpellId).ExpectActivations(1)
            .Check(On(RitualId)).Check(Count(Warrior, "LOCATION_GRAVE", 2)).Check(Count(Fiend, "LOCATION_MZONE", 2)).AssertPasses();
        // Effect Monsters in the Deck aren't Normal Monsters.
        new EngineScenario(ritual, spell).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "deck").Card(EffectFiller, 0, "deck", 3).Card(Dragon, 1, "deck", 3)
            .Activate(SpellId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Ritual_SendsAMonsterFromTheExtraDeck()
    {
        var ritual = RitualMonster();
        var extra = new CardProject
        {
            Card = new CardData { Id = ExtraLv4, Name = "Level 4 Fusion", Frame = MonsterFrame.Fusion, Level = 4, Atk = 1000, Def = 1000 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [Warrior, Fiend] },
        };
        var spell = RitualSpell(Rit("hand_field_extra", "equal"));
        Decks(new EngineScenario(ritual, extra, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone").Card(ExtraLv4, 0, "extra")
            .Activate(SpellId).ExpectActivations(1)
            .Check(On(RitualId)).Check(On(ExtraLv4, 0, "LOCATION_GRAVE")).Check(On(Warrior, 0, "LOCATION_GRAVE")).AssertPasses();
    }

    [EngineFact]
    public void Ritual_TributesTheOpponentsFaceUpMonster()
    {
        var ritual = RitualMonster();
        var spell = RitualSpell(Rit("hand_field_opponent", "equal"));
        Decks(new EngineScenario(ritual, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone")
            .Activate(SpellId).ExpectActivations(1)
            .Check(On(RitualId)).Check(On(Fiend, 1, "LOCATION_GRAVE")).Check(On(Warrior, 0, "LOCATION_GRAVE")).AssertPasses();
        // Without the opponent's monster, the Levels don't add up.
        Decks(new EngineScenario(ritual, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone")
            .Activate(SpellId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Ritual_ByTotalAtk_Drytron()
    {
        // Meteonis Drytron: "Tribute monsters from your hand or field whose total ATK equal or exceed the ATK of the Ritual Monster".
        var ritual = RitualMonster(atk: 3000);
        var spell = RitualSpell(Rit(stat: "atk"));
        Decks(new EngineScenario(ritual, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone", 2)
            .Activate(SpellId).ExpectActivations(1)
            .Check(On(RitualId)).Check(Count(Warrior, "LOCATION_GRAVE", 2)).AssertPasses();
        // 1500 ATK is less than 3000; the Levels (4 + 4 = 8) don't matter.
        Decks(new EngineScenario(ritual, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 0, "grave")
            .Activate(SpellId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Ritual_ByTributingOneMonster()
    {
        var ritual = RitualMonster(level: 4);
        var big = RitualMonster(Ritual2Id, level: 8);
        var spell = RitualSpell(Rit(max: 1));
        Decks(new EngineScenario(ritual, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone", 2)
            .Activate(SpellId).ExpectActivations(1)
            .Check(On(RitualId)).Check(Count(Warrior, "LOCATION_GRAVE", 1)).Check(Count(Warrior, "LOCATION_MZONE", 1)).AssertPasses();
        // A Level 8 Ritual Monster needs two Level 4 Tributes: not with "1 monster".
        Decks(new EngineScenario(big, spell)).Card(SpellId, 0, "hand").Card(Ritual2Id, 0, "hand").Card(Warrior, 0, "mzone", 2)
            .Activate(SpellId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Ritual_FromTheGYAndBanishment()
    {
        var ritual = RitualMonster(level: 4);
        var spell = RitualSpell(Rit(cards: new CardFilter { Owner = CardOwner.You, Zones = Zone.Banished, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Ritual }));
        Assert.Contains("from your banishment", EffectTextGenerator.Generate(spell));
        Decks(new EngineScenario(ritual, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "removed").Card(Warrior, 0, "mzone")
            .Activate(SpellId).ExpectActivations(1).Check(On(RitualId)).AssertPasses();
        var fromGy = RitualSpell(Rit(cards: new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Ritual }));
        Decks(new EngineScenario(ritual, fromGy)).Card(SpellId, 0, "hand").Card(RitualId, 0, "grave").Card(Warrior, 0, "mzone")
            .Activate(SpellId).ExpectActivations(1).Check(On(RitualId)).AssertPasses();
    }

    [EngineFact]
    public void Ritual_ThisCard_FromTheHand()
    {
        var ritual = RitualMonster(level: 4);
        ritual.Effects.Add(new EffectEvent { When = new BlockInstance("when.ignition").With("zone", "hand"), Do = [Rit(cards: new CardFilter { ThisCard = true })] });
        Assert.StartsWith("You can Ritual Summon this card, by Tributing monsters from your hand or field whose total Levels equal or exceed its Level.", EffectTextGenerator.Generate(ritual));
        Decks(new EngineScenario(ritual)).Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone")
            .Activate(RitualId).ExpectActivations(1).Check(On(RitualId)).Check(On(Warrior, 0, "LOCATION_GRAVE"))
            .Check($"{Card(RitualId)}:IsSummonType(SUMMON_TYPE_RITUAL)").AssertPasses();
        Decks(new EngineScenario(ritual)).Card(RitualId, 0, "hand")
            .Activate(RitualId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Ritual_Afterwards_AppliesToTheSummonedMonster()
    {
        var ritual = RitualMonster();
        var tough = RitualSpell(Rit(after: "indestructible_unnegatable"));
        Decks(new EngineScenario(ritual, tough)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone", 2)
            .Activate(SpellId).ExpectActivations(1)
            .Check($"{Card(RitualId)}:IsHasEffect(EFFECT_INDESTRUCTABLE_EFFECT)~=nil").Check($"{Card(RitualId)}:IsHasEffect(EFFECT_CANNOT_DISABLE)~=nil").AssertPasses();
        // "It cannot attack this turn": in the second turn, the opponent's, its direct attack never comes.
        var tired = RitualSpell(Rit(after: "cannot_attack"));
        Decks(new EngineScenario(ritual, tired)).Card(SpellId, 1, "hand").Card(RitualId, 1, "hand").Card(Warrior, 1, "mzone", 2)
            .Activate(SpellId).Attack(RitualId).Turns(2).ExpectActivations(1).Check(On(RitualId, 1)).Check("Duel.GetLP(0)==8000").AssertPasses();
        Decks(new EngineScenario(ritual, RitualSpell(Rit()))).Card(SpellId, 1, "hand").Card(RitualId, 1, "hand").Card(Warrior, 1, "mzone", 2)
            .Activate(SpellId).Attack(RitualId).Turns(2).ExpectActivations(1).Check("Duel.GetLP(0)==5500").AssertPasses();
        // Forbidden Ritual Art: destroyed during the End Phase.
        var brief = RitualSpell(Rit(after: "destroy_end"));
        Decks(new EngineScenario(ritual, brief)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone", 2)
            .Activate(SpellId).ExpectActivations(1).Check(On(RitualId, 0, "LOCATION_GRAVE")).AssertPasses();
    }

    [EngineFact]
    public void Ritual_TwoRitualSummonsInOneEffect()
    {
        var first = RitualMonster(level: 4);
        var second = RitualMonster(Ritual2Id, level: 4);
        CardFilter Named(long code) => new() { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Ritual, CardCode = code };
        // Each Tributes one Normal Monster you control: neither the other Ritual Monster in the hand nor the one just Summoned.
        var normal = new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Normal };
        var then = Rit("field", cards: Named(Ritual2Id), mats: normal).Linked(ActionLink.Then);
        var spell = RitualSpell(Rit("field", cards: Named(RitualId), mats: normal), then);
        Assert.Contains(", then Ritual Summon 1 \"Ritual 100098002\" from your hand, by Tributing Normal Monsters you control", EffectTextGenerator.Generate(spell, new CardPack { Cards = [first, second, spell] }));
        Decks(new EngineScenario(first, second, spell)).Card(SpellId, 0, "hand").Card(RitualId, 0, "hand").Card(Ritual2Id, 0, "hand").Card(Warrior, 0, "mzone", 2)
            .Activate(SpellId).ExpectActivations(1).Check(On(RitualId)).Check(On(Ritual2Id)).Check(Count(Warrior, "LOCATION_GRAVE", 2)).AssertPasses();
    }

    [EngineFact]
    public void RitualSpell_AlsoUsedFromTheGY_ByBanishingIt()
    {
        // "You can banish this card from your GY; Ritual Summon 1 Ritual Monster from your hand, by Tributing ..."
        var ritual = RitualMonster(level: 4);
        var spell = RitualSpell(Rit());
        spell.Effects.Add(new EffectEvent
        {
            When = new BlockInstance("when.ignition").With("zone", "graveyard"),
            Cost = [new BlockInstance("cost.this_card").With("how", "banish")],
            Do = [Rit()],
        });
        Assert.Contains("You can banish this card from your GY; Ritual Summon 1 Ritual Monster from your hand", EffectTextGenerator.Generate(spell));
        Decks(new EngineScenario(ritual, spell)).Card(SpellId, 0, "grave").Card(RitualId, 0, "hand").Card(Warrior, 0, "mzone")
            .Activate(SpellId).ExpectActivations(1).Check(On(RitualId)).Check(On(SpellId, 0, "LOCATION_REMOVED")).AssertPasses();
    }

    // ---------------------------------------------------------------- Fusion

    private static CardProject FusionMonster(long id = FusionId, params long[] mats) => new()
    {
        Card = new CardData { Id = id, Name = $"Fusion {id}", Frame = MonsterFrame.Fusion, Level = 7, Atk = 2500, Def = 2000 },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = mats.Length > 0 ? [.. mats] : [Warrior, Fiend] },
    };

    private static CardProject Poly(string mats, bool self = false, string after = "none", params BlockInstance[] costs) => new()
    {
        Card = new CardData { Id = PolyId, Name = "Fusion Spell", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = new BlockInstance("when.activate"), Cost = [.. costs], Do = [new BlockInstance(FusionSummonAction.BlockId).With("mats", mats).With("self", self).With("after", after)] }],
    };

    [Theory]
    [InlineData("either_field", "Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters from either field as Fusion Material.")]
    [InlineData("hand_field_gy_deck", "Fusion Summon 1 Fusion Monster from your Extra Deck, by shuffling Fusion Materials from your hand, field, and/or GY into the Deck.")]
    [InlineData("field_gy_banished_deck", "Fusion Summon 1 Fusion Monster from your Extra Deck, by shuffling Fusion Materials from your field, GY, and/or banishment into the Deck.")]
    [InlineData("banished_deck", "Fusion Summon 1 Fusion Monster from your Extra Deck, by shuffling your banished Fusion Materials into the Deck.")]
    [InlineData("hand_field_pzone", "Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters from your hand, field, and/or Pendulum Zones as Fusion Material.")]
    [InlineData("hand_field_gy_banish", "Fusion Summon 1 Fusion Monster from your Extra Deck, by banishing Fusion Materials from your hand, field, and/or GY.")]
    [InlineData("deck", "Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters from your Deck as Fusion Material.")]
    public void Fusion_ReadsLikeACard(string mats, string text)
    {
        var p = Poly(mats);
        Assert.Equal(text, EffectTextGenerator.Generate(p));
        Compiles(p);
    }

    [Fact]
    public void Fusion_ArchetypeOnly_AndSuperPolymerization_ReadLikeCards()
    {
        var hero = Poly("gy_banished_deck");
        hero.Effects[0].Do[0].Filters["matcards"] = new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Spellcaster };
        Assert.Equal("Fusion Summon 1 Fusion Monster from your Extra Deck, by shuffling Spellcaster monsters from your GY and/or banishment into the Deck.", EffectTextGenerator.Generate(hero));
        var super = Poly("either_field", costs: [new BlockInstance("cost.discard"), new BlockInstance("cost.chain_limit").With("who", "both").With("what", "any")]);
        Assert.Equal("Discard 1 card from your hand; Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters from either field as Fusion Material. Neither player can activate cards or effects in response to this card's activation.",
            EffectTextGenerator.Generate(super));
        Assert.Contains("Duel.SetChainLimit(", ScriptCompiler.Compile(super).Lua);
    }

    [EngineFact]
    public void Fusion_UsingTheOpponentsMonster_SuperPolymerization()
    {
        var fusion = FusionMonster();
        var poly = Poly("either_field");
        Decks(new EngineScenario(fusion, poly)).Card(PolyId, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone").Card(FusionId, 0, "extra")
            .Activate(PolyId).ExpectActivations(1)
            .Check(On(FusionId)).Check(On(Fiend, 1, "LOCATION_GRAVE")).Check(On(Warrior, 0, "LOCATION_GRAVE")).AssertPasses();
        // Monsters in the hand aren't on the field.
        Decks(new EngineScenario(fusion, poly)).Card(PolyId, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 0, "hand").Card(FusionId, 0, "extra")
            .Activate(PolyId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Fusion_ShufflesMaterialsFromTheFieldAndGYIntoTheDeck()
    {
        var fusion = FusionMonster();
        var poly = Poly("field_gy_deck");
        new EngineScenario(fusion, poly).Card(PolyId, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 0, "grave").Card(FusionId, 0, "extra").Card(Dragon, 1, "deck", 3)
            .Activate(PolyId).ExpectActivations(1)
            .Check(On(FusionId)).Check(On(Warrior, 0, "LOCATION_DECK")).Check(On(Fiend, 0, "LOCATION_DECK")).AssertPasses();
        // Not from the hand.
        new EngineScenario(fusion, poly).Card(PolyId, 0, "hand").Card(Warrior, 0, "hand").Card(Fiend, 0, "grave").Card(FusionId, 0, "extra").Card(Dragon, 1, "deck", 3)
            .Activate(PolyId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Fusion_ShufflesMaterialsFromTheGYAndBanishmentIntoTheDeck()
    {
        var fusion = FusionMonster();
        var poly = Poly("gy_banished_deck");
        new EngineScenario(fusion, poly).Card(PolyId, 0, "hand").Card(Warrior, 0, "removed").Card(Fiend, 0, "grave").Card(FusionId, 0, "extra").Card(Dragon, 1, "deck", 3)
            .Activate(PolyId).ExpectActivations(1)
            .Check(On(FusionId)).Check(On(Warrior, 0, "LOCATION_DECK")).Check(On(Fiend, 0, "LOCATION_DECK")).AssertPasses();
        new EngineScenario(fusion, poly).Card(PolyId, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 0, "grave").Card(FusionId, 0, "extra").Card(Dragon, 1, "deck", 3)
            .Activate(PolyId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Fusion_BanishesMaterialsFromTheHandFieldAndGY()
    {
        var fusion = FusionMonster();
        var poly = Poly("hand_field_gy_banish");
        Decks(new EngineScenario(fusion, poly)).Card(PolyId, 0, "hand").Card(Warrior, 0, "hand").Card(Fiend, 0, "grave").Card(FusionId, 0, "extra")
            .Activate(PolyId).ExpectActivations(1)
            .Check(On(FusionId)).Check(On(Warrior, 0, "LOCATION_REMOVED")).Check(On(Fiend, 0, "LOCATION_REMOVED")).AssertPasses();
    }

    [EngineFact]
    public void Fusion_UsesMonstersFromTheDeck()
    {
        var fusion = FusionMonster();
        var poly = Poly("deck");
        new EngineScenario(fusion, poly).Card(PolyId, 0, "hand").Card(Warrior, 0, "deck").Card(Fiend, 0, "deck").Card(FusionId, 0, "extra").Card(Dragon, 1, "deck", 3)
            .Activate(PolyId).ExpectActivations(1)
            .Check(On(FusionId)).Check(On(Warrior, 0, "LOCATION_GRAVE")).Check(On(Fiend, 0, "LOCATION_GRAVE")).AssertPasses();
        new EngineScenario(fusion, poly).Card(PolyId, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 0, "deck").Card(FusionId, 0, "extra").Card(Dragon, 1, "deck", 3)
            .Activate(PolyId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Fusion_UsesAMonsterInThePendulumZone()
    {
        var pend = new CardProject { Card = new CardData { Id = PendId, Name = "Pendulum Warrior", Frame = MonsterFrame.Normal, Abilities = MonsterAbility.Pendulum, Level = 4, LeftScale = 1, RightScale = 1, Atk = 1200, Def = 1000 } };
        var fusion = FusionMonster(FusionId, Warrior, PendId);
        var poly = Poly("hand_field_pzone");
        var scenario = Decks(new EngineScenario(fusion, pend, poly)).Card(PolyId, 0, "hand").Card(Warrior, 0, "hand").Card(PendId, 0, "pzone").Card(FusionId, 0, "extra");
        scenario.Activate(PolyId).ExpectActivations(1)
            // A Pendulum Monster sent from the field goes face-up to the Extra Deck.
            .Check(On(FusionId)).Check(On(PendId, 0, "LOCATION_EXTRA")).Check(On(Warrior, 0, "LOCATION_GRAVE")).AssertPasses();
        Decks(new EngineScenario(fusion, pend, Poly("hand_field"))).Card(PolyId, 0, "hand").Card(Warrior, 0, "hand").Card(PendId, 0, "pzone").Card(FusionId, 0, "extra")
            .Activate(PolyId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Fusion_IncludingThisCard()
    {
        // "You can Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters you control as Fusion Material, including this card."
        var self = new CardProject
        {
            Card = new CardData { Id = SelfId, Name = "Fusion Partner", Level = 4, Atk = 1000, Def = 1000 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), Do = [new BlockInstance(FusionSummonAction.BlockId).With("mats", "field").With("self", true)] }],
        };
        Assert.StartsWith("You can Fusion Summon 1 Fusion Monster from your Extra Deck, using monsters you control as Fusion Material, including this card.", EffectTextGenerator.Generate(self));
        var withSelf = FusionMonster(FusionId, Warrior, SelfId);
        Decks(new EngineScenario(withSelf, self)).Card(SelfId, 0, "mzone").Card(Warrior, 0, "mzone").Card(FusionId, 0, "extra")
            .Activate(SelfId).ExpectActivations(1).Check(On(FusionId)).Check(On(SelfId, 0, "LOCATION_GRAVE")).AssertPasses();
        // A Fusion Monster whose materials can't include this card can't be Summoned with it.
        var without = FusionMonster(FusionId, Warrior, Fiend);
        Decks(new EngineScenario(without, self)).Card(SelfId, 0, "mzone").Card(Warrior, 0, "mzone").Card(Fiend, 0, "mzone").Card(FusionId, 0, "extra")
            .Activate(SelfId).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Fusion_Afterwards_CannotAttack()
    {
        var fusion = FusionMonster();
        var poly = Poly("hand_field", after: "cannot_attack");
        Assert.EndsWith(", but it cannot attack this turn.", EffectTextGenerator.Generate(poly));
        Decks(new EngineScenario(fusion, poly)).Card(PolyId, 1, "hand").Card(Warrior, 1, "hand").Card(Fiend, 1, "hand").Card(FusionId, 1, "extra")
            .Activate(PolyId).Attack(FusionId).Turns(2).ExpectActivations(1).Check(On(FusionId, 1)).Check("Duel.GetLP(0)==8000").AssertPasses();
        Decks(new EngineScenario(fusion, Poly("hand_field"))).Card(PolyId, 1, "hand").Card(Warrior, 1, "hand").Card(Fiend, 1, "hand").Card(FusionId, 1, "extra")
            .Activate(PolyId).Attack(FusionId).Turns(2).ExpectActivations(1).Check("Duel.GetLP(0)==5500").AssertPasses();
    }

    // ---------------------------------------------------------------- Contact Fusion

    private static CardProject Contact(ContactFusionMove move, Zone zones = Zone.MonsterZone, SummonRule rule = SummonRule.SpecialSummonOnly) => new()
    {
        Card = new CardData { Id = ContactId, Name = "Contact Fusion", Frame = MonsterFrame.Fusion, Level = 7, Atk = 2500, Def = 2000, SummonRule = rule },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [Warrior, Fiend], Contact = true, ContactMove = move, ContactZones = zones },
    };

    [Theory]
    [InlineData(ContactFusionMove.ShuffleIntoDeck, Zone.MonsterZone, SummonRule.SpecialSummonOnly, "Must first be Special Summoned (from your Extra Deck) by shuffling the above cards you control into the Deck. (You do not use \"Polymerization\".)")]
    [InlineData(ContactFusionMove.Banish, Zone.Field | Zone.Graveyard, SummonRule.SpecialSummonOnly, "Must first be Special Summoned (from your Extra Deck) by banishing the above cards you control and/or from your GY. (You do not use \"Polymerization\".)")]
    [InlineData(ContactFusionMove.Tribute, Zone.MonsterZone, SummonRule.Normal, "Must first be either Fusion Summoned, or Special Summoned from your Extra Deck by Tributing the above cards you control.")]
    [InlineData(ContactFusionMove.SendToGrave, Zone.MonsterZone, SummonRule.ProcedureOnly, "Must be either Fusion Summoned, or Special Summoned by sending the above cards you control to the GY (in which case you do not use \"Polymerization\").")]
    [InlineData(ContactFusionMove.Banish, Zone.Field | Zone.Hand | Zone.Graveyard, SummonRule.SpecialSummonOnly, "Must first be Special Summoned (from your Extra Deck) by banishing the above cards from your hand, field, and/or GY. (You do not use \"Polymerization\".)")]
    public void ContactFusion_ReadsLikeACard(ContactFusionMove move, Zone zones, SummonRule rule, string text)
    {
        var p = Contact(move, zones, rule);
        var named = new CardPack { Cards = [p, new() { Card = new CardData { Id = Warrior, Name = "Filler Warrior" } }, new() { Card = new CardData { Id = Fiend, Name = "Filler Fiend" } }] };
        Assert.Equal($"\"Filler Warrior\" + \"Filler Fiend\"\n{text}", EffectTextGenerator.Generate(p, named));
        Compiles(p);
        Assert.Contains("aux.AddContactFusionProcedure(c,", ScriptCompiler.Compile(p).Lua);
    }

    [Fact]
    public void ContactFusion_MustBeSpecialSummonedThisWay_WhenItCannotBeSpecialSummonedOtherwise()
    {
        var p = Contact(ContactFusionMove.Banish);
        p.Card.SummonRule = SummonRule.Normal;
        p.Card.CannotBeSpecialSummoned = true;
        var text = EffectTextGenerator.Generate(p);
        Assert.EndsWith("Must be Special Summoned (from your Extra Deck) by banishing the above cards you control.", text);
        Assert.DoesNotContain("Cannot be Special Summoned.", text);
    }

    [EngineFact]
    public void ContactFusion_ShufflesTheMaterialsIntoTheDeck_AndCantBeFusionSummoned()
    {
        var contact = Contact(ContactFusionMove.ShuffleIntoDeck);
        Decks(new EngineScenario(contact)).Card(Warrior, 0, "mzone").Card(Fiend, 0, "mzone").Card(ContactId, 0, "extra")
            .SpSummon(ContactId).Check(On(ContactId)).Check(On(Warrior, 0, "LOCATION_DECK")).Check(On(Fiend, 0, "LOCATION_DECK")).AssertPasses();
        // Not with only one of the materials.
        Decks(new EngineScenario(contact)).Card(Warrior, 0, "mzone").Card(Fiend, 0, "hand").Card(ContactId, 0, "extra")
            .SpSummon(ContactId).Check(On(ContactId, 0, "LOCATION_EXTRA")).AssertPasses();
        // "You do not use Polymerization": it can't be Fusion Summoned.
        var poly = Poly("hand_field");
        Decks(new EngineScenario(contact, poly)).Card(PolyId, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 0, "mzone").Card(ContactId, 0, "extra")
            .Activate(PolyId).ExpectActivations(0).Check(On(ContactId, 0, "LOCATION_EXTRA")).AssertPasses();
    }

    [EngineFact]
    public void ContactFusion_BanishesFromTheFieldAndGY()
    {
        var contact = Contact(ContactFusionMove.Banish, Zone.Field | Zone.Graveyard);
        Decks(new EngineScenario(contact)).Card(Warrior, 0, "mzone").Card(Fiend, 0, "grave").Card(ContactId, 0, "extra")
            .SpSummon(ContactId).Check(On(ContactId)).Check(On(Warrior, 0, "LOCATION_REMOVED")).Check(On(Fiend, 0, "LOCATION_REMOVED")).AssertPasses();
    }

    [EngineFact]
    public void ContactFusion_Tributes_AndCanAlsoBeFusionSummoned()
    {
        var contact = Contact(ContactFusionMove.Tribute, rule: SummonRule.Normal);
        Decks(new EngineScenario(contact)).Card(Warrior, 0, "mzone").Card(Fiend, 0, "mzone").Card(ContactId, 0, "extra")
            .SpSummon(ContactId).Check(On(ContactId)).Check(On(Warrior, 0, "LOCATION_GRAVE")).Check(On(Fiend, 0, "LOCATION_GRAVE")).AssertPasses();
        var poly = Poly("hand_field");
        Decks(new EngineScenario(contact, poly)).Card(PolyId, 0, "hand").Card(Warrior, 0, "hand").Card(Fiend, 0, "hand").Card(ContactId, 0, "extra")
            .Activate(PolyId).ExpectActivations(1).Check(On(ContactId)).Check($"{Card(ContactId)}:IsSummonType(SUMMON_TYPE_FUSION)").AssertPasses();
    }

    [EngineFact]
    public void ContactFusion_SendsToTheGY()
    {
        var contact = Contact(ContactFusionMove.SendToGrave);
        Decks(new EngineScenario(contact)).Card(Warrior, 0, "mzone").Card(Fiend, 0, "mzone").Card(ContactId, 0, "extra")
            .SpSummon(ContactId).Check(On(ContactId)).Check(On(Warrior, 0, "LOCATION_GRAVE")).Check(On(Fiend, 0, "LOCATION_GRAVE")).AssertPasses();
    }
}

/// <summary>Official Ritual Spells, Fusion Spells and Contact Fusion Monsters translated into the blocks, played in the engine.</summary>
public class RitualFusionTranslationTests
{
    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, $"c{id}.lua"));

    private static CardProject Translated(long id, CardData card)
    {
        var result = ScriptTranslator.Translate(Script(id), id, card);
        Assert.True(result.Complete, string.Join("\n", result.Pieces.Where(p => !p.Translated).Select(p => $"{p.Piece?.Title}: {p.Reason}")));
        var project = new CardProject { Card = card, Effects = result.Pieces.SelectMany(p => p.Effects).ToList() };
        var compiled = ScriptCompiler.Compile(project);
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        return project;
    }

    private static BlockInstance Only(CardProject p, string blockId) => p.Effects.SelectMany(e => e.Do).Single(b => b.BlockId == blockId);
    private static CardData Spell(long id, SpellKind kind = SpellKind.Normal) => new() { Id = id, Name = "Translated", Kind = CardKind.Spell, SpellKind = kind };
    private static string On(long code, int p = 0, string loc = "LOCATION_MZONE") => $"Duel.IsExistingMatchingCard(Card.IsCode,{p},{loc},0,1,nil,{code})";
    private static EngineScenario Decks(EngineScenario s) => s.Card(Dragon, 0, "deck", 3).Card(Dragon, 1, "deck", 3);

    [EngineFact]
    public void BlackLusterRitual_TributesLevelsEqualToEightOrMore()
    {
        // aux.AddRitualProcGreaterCode(c,5405694): Black Luster Soldier's original Level, 8, or more.
        var p = Translated(55761792, Spell(100098101, SpellKind.Ritual));
        var b = Only(p, RitualSummonAction.BlockId);
        Assert.Equal(5405694, b.Filters["cards"].CardCode);
        Assert.Equal("original", b.Values["stat"]);
        Assert.Equal("greater", b.Values["how"]);
        var bls = new CardProject { Card = new CardData { Id = 5405694, Name = "Black Luster Soldier", Frame = MonsterFrame.Ritual, Level = 8, Atk = 3000, Def = 2500 } };
        Assert.Contains("whose total Levels equal 8 or more", EffectTextGenerator.Generate(p, new CardPack { Cards = [p, bls] }));
        Decks(new EngineScenario(bls, p)).Card(100098101, 0, "hand").Card(5405694, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 0, "hand")
            .Activate(100098101).ExpectActivations(1).Check(On(5405694)).Check(On(Warrior, 0, "LOCATION_GRAVE")).Check(On(Fiend, 0, "LOCATION_GRAVE")).AssertPasses();
        Decks(new EngineScenario(bls, p)).Card(100098101, 0, "hand").Card(5405694, 0, "hand").Card(Warrior, 0, "mzone")
            .Activate(100098101).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void LightAndDarknessRitual_AlsoBanishesFromTheGY()
    {
        // "local e1=aux.AddRitualProcGreater2(c,s.rfilter,nil,s.grfilter)" with its description set afterwards: one effect.
        var result = ScriptTranslator.Translate(Script(33599853), 33599853, Spell(100098102, SpellKind.Ritual));
        var piece = result.Pieces.Single(p => p.Effect?.Do.Any(b => b.BlockId == RitualSummonAction.BlockId) == true);
        var b = piece.Effect!.Do.Single();
        Assert.Equal("hand_field_gy", b.Values["source"]);
        Assert.Equal("level", b.Values["stat"]);
        Assert.Equal([70405001], b.Filters["cards"].AltCodes);
        Assert.DoesNotContain(result.Pieces, p => p.Piece.Code.TrimStart().StartsWith("e1:", StringComparison.Ordinal));
    }

    [EngineFact]
    public void LibromancerMagigirl_RitualSummonsAsAQuickEffect()
    {
        // "local e2=aux.AddRitualProcGreater2(c,s.ritfilter,LOCATION_HAND,nil,nil,true)" then e2:SetType(EFFECT_TYPE_QUICK_O) ...
        var card = new CardData { Id = 100098103, Name = "Translated", Level = 4, Atk = 1000, Def = 1000 };
        var p = Translated(82128978, card);
        var effect = p.Effects.Single(e => e.Do.Any(b => b.BlockId == RitualSummonAction.BlockId));
        Assert.Equal("when.quick", effect.When!.BlockId);
        Assert.Equal(0x17c, effect.Do.Single().Filters["cards"].Archetype);
    }

    [EngineFact]
    public void ForbiddenRitualArt_StaysScript_ItsExtraOperationIsNotABlock()
    {
        var result = ScriptTranslator.Translate(Script(12157563), 12157563, Spell(100098104, SpellKind.Ritual));
        Assert.Contains(result.Pieces, p => !p.Translated && p.Reason!.Contains("extra operation", StringComparison.Ordinal));
    }

    [EngineFact]
    public void SuperPolymerization_UsesTheOpponentsMonsters_AndCantBeRespondedTo()
    {
        var p = Translated(48130397, Spell(100098105, SpellKind.QuickPlay));
        var effect = p.Effects.Single();
        Assert.Equal("either_field", effect.Do.Single().Values["mats"]);
        Assert.Contains(effect.Cost, c => c.BlockId == "cost.chain_limit" && c.Values["what"] == "any");
        Assert.Contains(effect.Cost, c => c.BlockId == "cost.discard");
        var fusion = new CardProject
        {
            Card = new CardData { Id = 100098106, Name = "Fusion", Frame = MonsterFrame.Fusion, Level = 7, Atk = 2500, Def = 2000 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [Warrior, Fiend] },
        };
        Decks(new EngineScenario(fusion, p)).Card(100098105, 0, "hand").Card(Dragon, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone").Card(100098106, 0, "extra")
            .Activate(100098105).ExpectActivations(1).Check(On(100098106)).Check(On(Fiend, 1, "LOCATION_GRAVE")).Check(On(Dragon, 0, "LOCATION_GRAVE")).AssertPasses();
    }

    [EngineFact]
    public void MiracleFusion_BanishesFromTheFieldOrGY()
    {
        var p = Translated(45906428, Spell(100098107));
        var b = Only(p, FusionSummonAction.BlockId);
        Assert.Equal("field_gy_banish", b.Values["mats"]);
        Assert.Equal(0x3008, b.Filters["cards"].Archetype);
    }

    [EngineFact]
    public void DDSwirlSlime_FusionSummonsIncludingItselfFromTheHand()
    {
        var slime = new CardData { Id = 100098108, Name = "Translated", Level = 2, Atk = 200, Def = 200, Archetypes = [0xaf] };
        var p = Translated(45206713, slime);
        var b = Only(p, FusionSummonAction.BlockId);
        Assert.Equal("hand", b.Values["mats"]);
        Assert.Equal("true", b.Values["self"]);
        var fusion = new CardProject
        {
            Card = new CardData { Id = 100098109, Name = "D/D/D Fusion", Frame = MonsterFrame.Fusion, Level = 7, Atk = 2500, Def = 2000, Archetypes = [0x10af] },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [100098108, Fiend] },
        };
        Decks(new EngineScenario(fusion, p)).Card(100098108, 0, "hand").Card(Fiend, 0, "hand").Card(100098109, 0, "extra")
            .Activate(100098108).ExpectActivations(1).Check(On(100098109)).Check(On(100098108, 0, "LOCATION_GRAVE")).AssertPasses();
    }

    [EngineFact]
    public void GladiatorBeastEssedarii_ContactFusion_ShufflesItsMaterialsIntoTheDeck()
    {
        var project = new CardProject
        {
            Card = new CardData { Id = 100098110, Name = "Essedarii", Frame = MonsterFrame.Fusion, Level = 5, Atk = 2500, Def = 1400, Archetypes = [0x1019] },
            UseCustomLua = true,
            CustomLua = Script(73285669),
        };
        ScriptTranslator.ApplyTo(project, null, out var applied);
        Assert.True(applied);
        Assert.True(project.Procedure.Contact);
        Assert.Equal(ContactFusionMove.ShuffleIntoDeck, project.Procedure.ContactMove);
        Assert.Equal(Zone.MonsterZone, project.Procedure.ContactZones);
        Assert.Equal(SummonRule.SpecialSummonOnly, project.Card.SummonRule);
        Assert.Contains("Must first be Special Summoned (from your Extra Deck) by shuffling the above cards you control into the Deck. (You do not use \"Polymerization\".)", EffectTextGenerator.Generate(project));
        CardProject Beast(long id) => new() { Card = new CardData { Id = id, Name = "Gladiator Beast " + id, Frame = MonsterFrame.Normal, Level = 4, Atk = 1000, Def = 1000, Archetypes = [0x1019] } };
        Decks(new EngineScenario(project, Beast(100098111), Beast(100098112))).Card(100098111, 0, "mzone").Card(100098112, 0, "mzone").Card(100098110, 0, "extra")
            .SpSummon(100098110).Check(On(100098110)).Check(On(100098111, 0, "LOCATION_DECK")).Check(On(100098112, 0, "LOCATION_DECK")).AssertPasses();
    }

    [EngineFact]
    public void GateGuardianOfThunderAndWind_MustBeSpecialSummonedByBanishing()
    {
        var project = new CardProject
        {
            Card = new CardData { Id = 34904525, Name = "Gate Guardian of Thunder and Wind", Frame = MonsterFrame.Fusion, Level = 10, Atk = 3300, Def = 2800 },
            UseCustomLua = true,
            CustomLua = Script(34904525),
        };
        ScriptTranslator.ApplyTo(project, null, out var applied);
        Assert.True(applied);
        Assert.True(project.Card.CannotBeSpecialSummoned);
        Assert.Equal(ContactFusionMove.Banish, project.Procedure.ContactMove);
        Assert.Equal(Zone.Field, project.Procedure.ContactZones);
        Assert.Contains("Must be Special Summoned (from your Extra Deck) by banishing the above cards you control.", EffectTextGenerator.Generate(project));
    }

    [EngineFact]
    public void ContactFusion_EitherFusionSummonedOrSpecialSummoned_IsTheUsualRule()
    {
        // Meizen the Battle Ninja: "not e:GetHandler():IsLocation(LOCATION_EXTRA) or aux.fuslimit(e,se,sp,st)".
        var result = ScriptTranslator.Translate(Script(11825276), 11825276, new CardData { Id = 100098114, Name = "T", Frame = MonsterFrame.Fusion, Level = 6 });
        Assert.Contains(result.Pieces, p => p.ProcedureSetting is not null);
        Assert.Contains(result.Pieces, p => p.Rule == SummonRule.Normal);
    }

    [EngineFact]
    public void ContactFusion_WithItsOwnMaterialFilter_StaysScript()
    {
        // Gladiator Beast Heraklinos's materials are also checked to be monsters by its own filter.
        var result = ScriptTranslator.Translate(Script(27346636), 27346636, new CardData { Id = 100098115, Name = "T", Frame = MonsterFrame.Fusion, Level = 8 });
        Assert.Contains(result.Pieces, p => !p.Translated && p.Reason!.StartsWith("its Contact Fusion", StringComparison.Ordinal));
    }

    [EngineFact]
    public void FusionMaterialsWithARepeatingSlot_AndNoSubstitutes()
    {
        // "aux.AddFusionProcCodeFunRep(c,68468459,aux.FilterBoolFunction(Card.IsRace,RACE_DRAGON),1,127,true,true)".
        var result = ScriptTranslator.Translate(Script(3410461), 3410461, new CardData { Id = 100098116, Name = "T", Frame = MonsterFrame.Fusion, Level = 8 });
        var proc = result.Pieces.Single(p => p.Procedure is not null).Procedure!;
        Assert.Equal(68468459, proc.Materials[0].Filter.CardCode);
        Assert.Equal(CardRace.Dragon, proc.Materials[1].Filter.Race);
        Assert.Equal((1, 0), (proc.Materials[1].Min, proc.Materials[1].Max));
        Assert.False(proc.FusionNoSubstitutes);
        // Elemental HERO Chaos Neos: "aux.AddFusionProcCode3(c,...,false,false)" can't use Fusion substitutes.
        var neos = ScriptTranslator.Translate(Script(17032740), 17032740, new CardData { Id = 100098117, Name = "T", Frame = MonsterFrame.Fusion, Level = 9 });
        var neosProc = neos.Pieces.Single(p => p.Procedure is not null).Procedure!;
        Assert.True(neosProc.FusionNoSubstitutes);
        var compiled = ScriptCompiler.Compile(new CardProject { Card = new CardData { Id = 100098117, Name = "T", Frame = MonsterFrame.Fusion, Level = 9 }, Procedure = neosProc });
        Assert.Contains("aux.AddFusionProcMix(c,false,true,89943723,43237273,17732278)", compiled.Lua);
    }

    [EngineFact]
    public void FusionMaterials_OneNamedCardRepeating_UsesTheHelperForNames()
    {
        // "2+ "Filler Warrior"": the passcode can't go to the helper for filters.
        var p = new CardProject
        {
            Card = new CardData { Id = 100098118, Name = "T", Frame = MonsterFrame.Fusion, Level = 8 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, Materials = [new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, CardCode = Warrior }, Min = 2, Max = 0 }] },
        };
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("aux.AddFusionProcCodeRep2(c,1001,2,127,true,true)", lua);
        Assert.DoesNotContain(ScriptChecker.Check(lua), d => d.Severity == Severity.Error);
        var poly = new CardProject
        {
            Card = new CardData { Id = 100098119, Name = "Fusion Spell", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = new BlockInstance("when.activate"), Do = [new BlockInstance(FusionSummonAction.BlockId)] }],
        };
        Decks(new EngineScenario(p, poly)).Card(100098119, 0, "hand").Card(Warrior, 0, "hand", 3).Card(100098118, 0, "extra")
            .Activate(100098119).ExpectActivations(1).Check(On(100098118)).AssertPasses();
    }
}
