using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// "This card is treated as a Normal Monster while in the hand or GY", "(This card is always treated as an "Elfnote"
/// card.)", "This card's name becomes "X" while ...", and how a card counts as material (Xyz and Synchro Levels,
/// non-Tuner, the entire Ritual Tribute, Fusion substitutes, 2 Xyz materials), played in the engine.
/// </summary>
public class TreatedAsPassiveTests
{
    private const long Id = 100232001, Other = 100232002, Target = 100232003, SpellId = 100232004, Ritual = 100232005, NameId = 46986414;
    private const int Arch = 0x1f1, Arch2 = 0x1f2;

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    /// <summary>The card by its printed passcode, whatever its name is now.</summary>
    private static string Find(long code, string loc) => $"Duel.GetFirstMatchingCard(function(c) return c:GetOriginalCode()=={code} end,0,{loc},0,nil)";
    private static string In(long code, string loc) => $"Duel.IsExistingMatchingCard(Card.IsCode,0,{loc},0,1,nil,{code})";

    private static CardProject Card(BlockInstance passive, int level = 4, MonsterFrame frame = MonsterFrame.Effect, MonsterAbility abilities = MonsterAbility.None,
        CardAttribute attribute = CardAttribute.Earth, long id = Id, string name = "Treated") => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = frame, Abilities = abilities, Level = level, Atk = 1000, Def = 1000, Attribute = attribute },
        Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [passive] }],
    };

    private static CardPack Pack(params CardProject[] cards) => new() { Cards = [.. cards], Archetypes = [new Archetype { Code = Arch, Name = "Elfnote" }, new Archetype { Code = Arch2, Name = "Power Patron" }] };

    private static void Compiles(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
    }

    /// <summary>One copy of the card in each of these places: hand, Monster Zone, GY, banished and Deck.</summary>
    private static EngineScenario Everywhere(params CardProject[] cards) =>
        new EngineScenario(cards).Card(Id, 0, "hand").Card(Id, 0, "mzone").Card(Id, 0, "grave").Card(Id, 0, "removed").Card(Id, 0, "deck")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 5);

    private static EngineScenario Holds(EngineScenario s, string test, params string[] where)
    {
        foreach (var loc in new[] { "LOCATION_HAND", "LOCATION_MZONE", "LOCATION_GRAVE", "LOCATION_REMOVED", "LOCATION_DECK" })
            s.Check($"{(where.Contains(loc) ? "" : "not ")}{Find(Id, loc)}:{test}");
        return s;
    }

    // ---------------------------------------------------------------- where it is treated as something

    [Theory]
    [InlineData(true, false, "none", true, false, "in the hand or GY")]
    [InlineData(false, false, "field", true, false, "on the field or in the GY")]
    [InlineData(true, false, "field", false, false, "in the hand or on the field")]
    [InlineData(true, true, "field", true, true, "in the hand, Deck, on the field, in the GY, or banished")]
    [InlineData(false, false, "none", true, false, "in the GY")]
    [InlineData(false, false, "mzone", false, false, "in the Monster Zone")]
    [InlineData(false, false, "none", false, true, "banished")]
    public void Places_ReadAsOfficialCards(bool hand, bool deck, string field, bool gy, bool banished, string text)
    {
        var b = B(TreatedAsPassive.BlockId, ("where", "in"), ("in_hand", hand.ToString()), ("in_deck", deck.ToString()), ("on_field", field), ("in_gy", gy.ToString()), ("banished", banished.ToString()))
            .With("as", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Dark });
        Assert.Equal($"This card is also treated as a DARK monster while {text}.", EffectTextGenerator.Generate(Card(b)));
        Compiles(Card(b));
    }

    [EngineFact]
    public void HandOrGY_AttributeArchetypeAndName()
    {
        var b = B(TreatedAsPassive.BlockId, ("where", "in"), ("in_hand", "true"), ("in_gy", "true"))
            .With("as", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Dark, Archetype = Arch, CardCode = NameId });
        var card = Card(b);
        CardNames.Set([(NameId, "Dark Magician")]);
        try
        {
            Assert.Equal("This card is also treated as an \"Elfnote\" card, and this card's name is also treated as \"Dark Magician\", and this card is also treated as a DARK monster while in the hand or GY.",
                EffectTextGenerator.Generate(card, Pack(card)));
        }
        finally { CardNames.Set([]); }
        var lua = ScriptCompiler.Compile(card).Lua;
        Assert.Contains("SetRange(LOCATION_HAND+LOCATION_GRAVE)", lua);
        Assert.Contains("SetProperty(EFFECT_FLAG_SINGLE_RANGE)", lua);
        var s = Everywhere(card);
        Holds(s, "IsAttribute(ATTRIBUTE_DARK)", "LOCATION_HAND", "LOCATION_GRAVE");
        Holds(s, $"IsSetCard({Lua.Hex(Arch)})", "LOCATION_HAND", "LOCATION_GRAVE");
        Holds(s, $"IsCode({NameId})", "LOCATION_HAND", "LOCATION_GRAVE");
        // Its own Attribute stays: "also".
        Holds(s, "IsAttribute(ATTRIBUTE_EARTH)", "LOCATION_HAND", "LOCATION_MZONE", "LOCATION_GRAVE", "LOCATION_REMOVED", "LOCATION_DECK");
        s.AssertPasses();
    }

    [EngineFact]
    public void NormalMonster_WhileInTheHandOrGY()
    {
        // Dragon Spirit of White: "This card is treated as a Normal Monster while in the hand or GY."
        var b = B(TreatedAsPassive.BlockId, ("how", "instead"), ("where", "in"), ("in_hand", "true"), ("in_gy", "true"))
            .With("as", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Normal });
        var card = Card(b);
        Assert.Equal("This card is treated as a Normal Monster while in the hand or GY.", EffectTextGenerator.Generate(card));
        Assert.Contains("EFFECT_REMOVE_TYPE", ScriptCompiler.Compile(card).Lua);
        var s = Everywhere(card);
        Holds(s, "IsType(TYPE_NORMAL)", "LOCATION_HAND", "LOCATION_GRAVE");
        Holds(s, "IsType(TYPE_EFFECT)", "LOCATION_MZONE", "LOCATION_REMOVED", "LOCATION_DECK");
        s.AssertPasses();
    }

    [EngineFact]
    public void Instead_ReplacesTheAttributeAndType_OnlyThere()
    {
        var b = B(TreatedAsPassive.BlockId, ("how", "instead"), ("where", "in"), ("banished", "true"), ("in_gy", "false"))
            .With("as", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Light, Race = CardRace.Dragon });
        var card = Card(b);
        Assert.Equal("This card is treated as a LIGHT Dragon monster while banished.", EffectTextGenerator.Generate(card));
        var s = Everywhere(card);
        Holds(s, "IsAttribute(ATTRIBUTE_LIGHT)", "LOCATION_REMOVED");
        Holds(s, "IsAttribute(ATTRIBUTE_EARTH)", "LOCATION_HAND", "LOCATION_MZONE", "LOCATION_GRAVE", "LOCATION_DECK");
        Holds(s, "IsRace(RACE_DRAGON)", "LOCATION_REMOVED");
        s.AssertPasses();
    }

    // ---------------------------------------------------------------- "(This card is always treated as ... card.)"

    [EngineFact]
    public void AlwaysAnArchetype_IsTheDatabaseEntry()
    {
        var b = B(TreatedAsPassive.BlockId).With("as", new CardFilter { CardType = FilterCardType.Any, Archetype = Arch });
        var card = Card(b);
        card.Card.Archetypes = [Arch2];
        Assert.Equal("(This card is always treated as an \"Elfnote\" card.)", EffectTextGenerator.Generate(card, Pack(card)));
        // No effect: the archetype goes in the card's setcode, beside its own.
        var lua = ScriptCompiler.Compile(card).Lua;
        Assert.DoesNotContain("EFFECT_ADD_SETCODE", lua);
        Assert.Equal([Arch2, Arch], TreatedAsPassive.Setcodes(card));
        // The original setcode, as official cards have it: in every place, and even after its name changes.
        var s = Everywhere(card);
        Holds(s, $"IsOriginalSetCard({Lua.Hex(Arch)})", "LOCATION_HAND", "LOCATION_MZONE", "LOCATION_GRAVE", "LOCATION_REMOVED", "LOCATION_DECK");
        Holds(s, $"IsSetCard({Lua.Hex(Arch2)})", "LOCATION_HAND", "LOCATION_MZONE", "LOCATION_GRAVE", "LOCATION_REMOVED", "LOCATION_DECK");
        s.AssertPasses();
    }

    [Fact]
    public void AlwaysTwoArchetypes_AndUnderACondition()
    {
        var b = B(TreatedAsPassive.BlockId).With("as", new CardFilter { CardType = FilterCardType.Any, Archetype = Arch2, Archetype2 = Arch });
        var card = Card(b);
        Assert.Equal("(This card is always treated as a \"Power Patron\" and \"Elfnote\" card.)", EffectTextGenerator.Generate(card, Pack(card)));
        // With an IF block it is an effect that applies only then.
        card.Effects[0].If.Add(B("if.cards_exist"));
        Assert.Contains("EFFECT_ADD_SETCODE", ScriptCompiler.Compile(card).Lua);
        Assert.Empty(TreatedAsPassive.Setcodes(card));
    }

    [Fact]
    public void AlwaysArchetypes_CountTowardsFour()
    {
        var b = B(TreatedAsPassive.BlockId).With("as", new CardFilter { CardType = FilterCardType.Any, Archetype = Arch, Archetype2 = Arch2 });
        var card = Card(b);
        card.Card.Archetypes = [0x10, 0x11, 0x12];
        Assert.True(ScriptCompiler.Compile(card).HasErrors);
    }

    // ---------------------------------------------------------------- "This card's name becomes ..."

    [EngineFact]
    public void NameBecomes_InTheTickedPlaces()
    {
        var b = B(NameBecomesPassive.BlockId, ("where", "in"), ("in_hand", "true"), ("in_gy", "true")).With("card", new CardFilter { CardType = FilterCardType.Any, CardCode = Warrior });
        var card = Card(b);
        Assert.Equal("This card's name becomes \"Filler Warrior\" while in the hand or GY.", EffectTextGenerator.Generate(card, new CardPack { Cards = [card, new CardProject { Card = new CardData { Id = Warrior, Name = "Filler Warrior" } }] }));
        var s = Everywhere(card);
        Holds(s, $"IsCode({Warrior})", "LOCATION_HAND", "LOCATION_GRAVE");
        Holds(s, $"IsCode({Id})", "LOCATION_MZONE", "LOCATION_REMOVED", "LOCATION_DECK");
        // As aux.EnableChangeCode does, it mentions that card.
        s.Check($"aux.IsCodeListed({Find(Id, "LOCATION_MZONE")},{Warrior})");
        s.AssertPasses();
    }

    // ---------------------------------------------------------------- other monsters

    [EngineFact]
    public void YourWarriors_AreAlsoTreatedAsAnArchetype()
    {
        // Frightfur Sanctuary: "All Fusion Monsters you control are also treated as "Frightfur" monsters."
        var b = B(CardsTreatedAsPassive.BlockId)
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Warrior })
            .With("as", new CardFilter { CardType = FilterCardType.Monster, Archetype = Arch });
        var spell = new CardProject
        {
            Card = new CardData { Id = SpellId, Name = "Sanctuary", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
            Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [b] }],
        };
        Assert.Equal("All Warrior monsters you control are also treated as \"Elfnote\" monsters.", EffectTextGenerator.Generate(spell, Pack(spell)));
        new EngineScenario(spell).Card(SpellId, 0, "szone").Card(Warrior, 0, "mzone").Card(Dragon, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "grave")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(SpellId).ExpectActivations(1)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Warrior}):IsSetCard({Lua.Hex(Arch)})")
            .Check($"not Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Dragon}):IsSetCard({Lua.Hex(Arch)})")
            .Check($"not Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{Warrior}):IsSetCard({Lua.Hex(Arch)})")
            .Check($"not Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,nil,{Warrior}):IsSetCard({Lua.Hex(Arch)})")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- how this card counts as material

    private static CardProject Xyz(long id, int rank, CardAttribute attribute, int materials = 2) => new()
    {
        Card = new CardData { Id = id, Name = $"Rank {rank}", Frame = MonsterFrame.Xyz, Level = rank, Atk = 2000, Def = 2000, Attribute = attribute },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = materials },
    };

    private static CardProject Synchro(long id, int level) => new()
    {
        Card = new CardData { Id = id, Name = $"Level {level} Synchro", Frame = MonsterFrame.Synchro, Level = level, Atk = 2000, Def = 2000 },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Synchro, MinMaterials = 1 },
    };

    private static CardProject SummonSpell(string kind) => new()
    {
        Card = new CardData { Id = SpellId, Name = "Summon Spell", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [B(ExtraDeckSummonAction.BlockId, ("kind", kind))] }],
    };

    private static CardProject Monster(long id, int level, MonsterAbility abilities = MonsterAbility.None) => new()
    {
        Card = new CardData { Id = id, Name = $"Monster {id}", Frame = MonsterFrame.Effect, Abilities = abilities, Level = level, Atk = 1000, Def = 1000 },
    };

    [EngineTheory]
    [InlineData(CardAttribute.Water, true)]
    [InlineData(CardAttribute.Dark, false)]
    public void XyzLevel_ForTheXyzSummonOfAWaterMonster(CardAttribute xyzAttribute, bool summons)
    {
        // Lantern Shark: a Level 4 that can be a Level 3 or 5 for a WATER Xyz Monster.
        var b = B(MaterialTreatedAsPassive.BlockId, ("kind", "xyz_level"), ("level", "3"), ("level2", "5")).With("for", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Water });
        var card = Card(b);
        Assert.Equal("If this card is used for the Xyz Summon of a WATER monster, it can be treated as a Level 3 or 5 monster.", EffectTextGenerator.Generate(card));
        var rank3 = Xyz(Target, 3, xyzAttribute);
        new EngineScenario(card, rank3, Monster(Other, 3), SummonSpell("xyz")).Card(Id, 0, "mzone").Card(Other, 0, "mzone").Card(Target, 0, "extra").Card(SpellId, 0, "hand")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(SpellId).ExpectActivations(summons ? 1 : 0)
            .Check(summons ? In(Target, "LOCATION_MZONE") : In(Target, "LOCATION_EXTRA")).AssertPasses();
    }

    [EngineTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SynchroLevel_OnTheField(bool treated, bool summons)
    {
        // A Level 4 that can be a Level 2 for a Synchro Summon: with a Level 2 Tuner, a Level 4 Synchro Monster.
        var b = B(MaterialTreatedAsPassive.BlockId, ("kind", "synchro_level"), ("level", "2"), ("where", "field"));
        var card = treated ? Card(b) : Monster(Id, 4);
        if (treated) Assert.Equal("This card on the field can be treated as a Level 2 monster when used for a Synchro Summon.", EffectTextGenerator.Generate(card));
        new EngineScenario(card, Synchro(Target, 4), Monster(Other, 2, MonsterAbility.Tuner), SummonSpell("synchro")).Card(Id, 0, "mzone").Card(Other, 0, "mzone").Card(Target, 0, "extra")
            .Card(SpellId, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(SpellId).ExpectActivations(summons ? 1 : 0).AssertPasses();
    }

    [EngineTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void NonTuner_TwoTunersCanSynchroSummon(bool treated, bool summons)
    {
        // Baby Mudragon: "If this card you control is used as Synchro Material, you can treat it as a non-Tuner."
        var b = B(MaterialTreatedAsPassive.BlockId, ("kind", "nontuner"));
        var card = treated ? Card(b, level: 2, abilities: MonsterAbility.Tuner) : Monster(Id, 2, MonsterAbility.Tuner);
        if (treated) Assert.Equal("If this card you control is used as Synchro Material, you can treat it as a non-Tuner.", EffectTextGenerator.Generate(card));
        new EngineScenario(card, Synchro(Target, 4), Monster(Other, 2, MonsterAbility.Tuner), SummonSpell("synchro")).Card(Id, 0, "mzone").Card(Other, 0, "mzone").Card(Target, 0, "extra")
            .Card(SpellId, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(SpellId).ExpectActivations(summons ? 1 : 0).AssertPasses();
    }

    [EngineTheory]
    [InlineData(CardAttribute.Water, true)]
    [InlineData(CardAttribute.Dark, false)]
    public void RitualEntire_ForAWaterRitualMonster(CardAttribute ritualAttribute, bool summons)
    {
        // Gishki Shadow: a Level 1 that can be the entire Tribute for a WATER Ritual Monster.
        var b = B(MaterialTreatedAsPassive.BlockId, ("kind", "ritual_entire"), ("where", "any")).With("for", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Water });
        var card = Card(b, level: 1);
        Assert.Equal("If you Ritual Summon exactly 1 WATER Ritual Monster with a card effect that requires use of monsters, this card can be used as the entire Tribute.", EffectTextGenerator.Generate(card));
        var ritual = new CardProject { Card = new CardData { Id = Ritual, Name = "Ritual 8", Frame = MonsterFrame.Ritual, Level = 8, Atk = 2800, Def = 2000, Attribute = ritualAttribute } };
        var spell = new CardProject
        {
            Card = new CardData { Id = SpellId, Name = "Ritual Spell", Kind = CardKind.Spell, SpellKind = SpellKind.Ritual },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B(RitualSummonAction.BlockId, ("source", "hand_field"), ("how", "greater"))] }],
        };
        new EngineScenario(card, ritual, spell).Card(Id, 0, "hand").Card(Ritual, 0, "hand").Card(SpellId, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(SpellId).ExpectActivations(summons ? 1 : 0)
            .Check(summons ? In(Ritual, "LOCATION_MZONE") : In(Ritual, "LOCATION_HAND")).AssertPasses();
    }

    [EngineTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void FusionSubstitute_ForAnyNamedMaterial(bool treated, bool summons)
    {
        var b = B(MaterialTreatedAsPassive.BlockId, ("kind", "fusion_substitute"), ("where", "any"));
        var card = treated ? Card(b) : Monster(Id, 4);
        if (treated)
            Assert.Equal("This card can be used as a substitute for any 1 Fusion Material whose name is specifically listed on the Fusion Monster Card, but the other Fusion Material(s) must be correct.",
                EffectTextGenerator.Generate(card));
        var fusion = new CardProject
        {
            Card = new CardData { Id = Target, Name = "Fusion", Frame = MonsterFrame.Fusion, Level = 7, Atk = 2500, Def = 2000 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [Warrior, Fiend] },
        };
        var poly = new CardProject
        {
            Card = new CardData { Id = SpellId, Name = "Fusion Spell", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B(FusionSummonAction.BlockId, ("mats", "hand_field"))] }],
        };
        new EngineScenario(card, fusion, poly).Card(Id, 0, "hand").Card(Fiend, 0, "hand").Card(Target, 0, "extra").Card(SpellId, 0, "hand").Card(Dragon, 0, "deck", 5).Card(Dragon, 1, "deck", 5)
            .Activate(SpellId).ExpectActivations(summons ? 1 : 0)
            .Check(summons ? $"{In(Target, "LOCATION_MZONE")} and {In(Id, "LOCATION_GRAVE")}" : In(Target, "LOCATION_EXTRA")).AssertPasses();
    }

    [EngineTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void TwoXyzMaterials_ForAnXyzMonsterThatNeedsThree(bool treated, bool summons)
    {
        // Drake Shark: "This card can be treated as 2 materials for the Xyz Summon of a WATER Xyz Monster that requires 3 or more materials."
        var b = B(MaterialTreatedAsPassive.BlockId, ("kind", "xyz_double")).With("for", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Water });
        var card = treated ? Card(b) : Monster(Id, 4);
        if (treated) Assert.Equal("This card can be treated as 2 materials for the Xyz Summon of a WATER Xyz Monster that requires 3 or more materials.", EffectTextGenerator.Generate(card));
        new EngineScenario(card, Xyz(Target, 4, CardAttribute.Water, materials: 3), Monster(Other, 4), SummonSpell("xyz")).Card(Id, 0, "mzone").Card(Other, 0, "mzone").Card(Target, 0, "extra")
            .Card(SpellId, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(SpellId).ExpectActivations(summons ? 1 : 0).AssertPasses();
    }

    [Theory]
    [InlineData("xyz_level", "If this card is used for an Xyz Summon, it can be treated as a Level 4 monster.")]
    [InlineData("synchro_level", "This card on the field can be treated as a Level 4 monster when used for a Synchro Summon.")]
    [InlineData("nontuner", "If this card you control is used as Synchro Material, you can treat it as a non-Tuner.")]
    [InlineData("ritual_entire", "If you Ritual Summon exactly 1 Ritual Monster with a card effect that requires use of monsters, this card you control can be used as the entire Tribute.")]
    [InlineData("fusion_substitute", "This card on the field can be used as a substitute for any 1 Fusion Material whose name is specifically listed on the Fusion Monster Card, but the other Fusion Material(s) must be correct.")]
    [InlineData("xyz_double", "This card can be treated as 2 materials for the Xyz Summon of an Xyz Monster that requires 3 or more materials.")]
    public void MaterialKinds_ReadAndCompile(string kind, string text)
    {
        var card = Card(B(MaterialTreatedAsPassive.BlockId, ("kind", kind)));
        Assert.Equal(text, EffectTextGenerator.Generate(card));
        Compiles(card);
    }
}
