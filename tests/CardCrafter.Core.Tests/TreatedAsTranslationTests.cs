using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Official scripts of "treated as" read into blocks: Special Summons treated as a Fusion/Synchro/Ritual Summon, cards
/// treated as an archetype, a Normal Monster or a name in some places, and material treatments. Each reads only into
/// the block that writes the same effect; the rest stays script.
/// </summary>
public class TreatedAsTranslationTests
{
    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, $"c{id}.lua"));

    private static string Find(long code, string loc) => $"Duel.GetFirstMatchingCard(Card.IsCode,0,{loc},0,nil,{code})";

    private static CardData Monster(long id, int level = 4, CardAttribute attribute = CardAttribute.Earth, MonsterAbility abilities = MonsterAbility.None, int[]? archetypes = null) => new()
    {
        Id = id, Name = "Translated " + id, Frame = MonsterFrame.Effect, Level = level, Atk = 1000, Def = 1000, Attribute = attribute, Abilities = abilities, Archetypes = [.. archetypes ?? []],
    };

    /// <summary>The translated effect whose DO blocks include <paramref name="block"/>; it must compile cleanly.</summary>
    private static EffectEvent Piece(long id, CardData card, string block)
    {
        var result = ScriptTranslator.Translate(Script(id), id, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(e => e.Do.Any(b => b.BlockId == block));
        Assert.True(effect is not null, string.Join("\n", result.Pieces.Select(p => $"{p.Piece?.Title}: {p.Reason}")));
        var compiled = ScriptCompiler.Compile(new CardProject { Card = card, Effects = [effect!] });
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        return effect!;
    }

    private static string Reason(long id, CardData card, string title) =>
        ScriptTranslator.Translate(Script(id), id, card).Pieces.First(p => p.Piece?.Title == title).Reason ?? "";

    // ---------------------------------------------------------------- Special Summons treated as another Summon

    [EngineFact]
    public void AbsoluteAxonKicker_BanishFromTheGY_TreatedAsAFusionSummon_Plays()
    {
        var card = Monster(100233001);
        var effect = Piece(80073414, card, "do.special_summon");
        var ss = effect.Do.Single();
        Assert.Equal("fusion", ss.Values["as"]);
        Assert.Equal((Zone.ExtraDeck, MonsterKind.Fusion, CardRace.Psychic, 10), (ss.Filters["cards"].Zones, ss.Filters["cards"].MonsterKind, ss.Filters["cards"].Race, ss.Filters["cards"].LevelMin ?? 0));
        Assert.Equal("banish", effect.Cost.Single().Values["how"]);
        var project = new CardProject { Card = card, Effects = [effect] };
        Assert.Contains("(This is treated as a Fusion Summon.)", EffectTextGenerator.Generate(project));
        var fusion = new CardProject
        {
            Card = new CardData { Id = 100233002, Name = "Psychic Fusion", Frame = MonsterFrame.Fusion, Level = 10, Race = CardRace.Psychic, Atk = 3000, Def = 2000, OnlyByOwnProcedure = true },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [Warrior, Dragon] },
        };
        new EngineScenario(project, fusion).Card(card.Id, 0, "grave").Card(fusion.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(card.Id).ExpectActivations(1)
            .Check($"{Find(fusion.Card.Id, "LOCATION_MZONE")}:IsSummonType(SUMMON_TYPE_FUSION)").Check($"{Find(fusion.Card.Id, "LOCATION_MZONE")}:IsStatus(STATUS_PROC_COMPLETE)")
            .AssertPasses();
    }

    [EngineFact]
    public void JunkWarriorExtreme_BanishThisCard_CountsTheZoneItLeaves()
    {
        var card = Monster(100233011, 8);
        var effect = Piece(16327715, card, "do.special_summon");
        var ss = effect.Do.Single();
        Assert.Equal(("synchro", 0x43), (ss.Values["as"], ss.Filters["cards"].Archetype));
        Assert.Contains("Duel.GetLocationCountFromEx(tp,tp,e:GetHandler(),c)>0", ScriptCompiler.Compile(new CardProject { Card = card, Effects = [effect] }).Lua);
    }

    [EngineFact]
    public void ConductorOfNephthys_TreatedAsARitualSummon_FromTheHandOrDeck()
    {
        var card = Monster(100233021, 2);
        var ss = Piece(8454126, card, "do.special_summon").Do.Single();
        Assert.Equal(("ritual", Zone.Hand | Zone.Deck, MonsterKind.Ritual), (ss.Values["as"], ss.Filters["cards"].Zones, ss.Filters["cards"].MonsterKind));
    }

    [EngineFact]
    public void AScriptWithoutSetMaterial_StaysScript()
    {
        // Bramble Rose Dragon Summons "Black Rose Dragon" without SetMaterial(nil), which the block always writes.
        var card = new CardData { Id = 100233031, Name = "Bramble", Frame = MonsterFrame.Synchro, Level = 7, Atk = 2400, Def = 1800 };
        Assert.Contains("aux.MustMaterialCheck", Reason(6560411, card, "Special summon"));
    }

    // ---------------------------------------------------------------- treated as something in some places

    [EngineFact]
    public void DragonSpiritOfWhite_IsANormalMonsterInTheHandAndGY()
    {
        var card = Monster(100233041, 8, CardAttribute.Light);
        var b = Piece(45467446, card, TreatedAsPassive.BlockId).Do.Single();
        Assert.Equal(("instead", "in", "true", "true", "none", MonsterKind.Normal), (b.Values["how"], b.Values["where"], b.Values["in_hand"], b.Values["in_gy"], b.Values["on_field"], b.Filters["as"].MonsterKind));
        Assert.Equal("false", b.Values["banished"]);
        var project = new CardProject { Card = card, Effects = [Piece(45467446, card, TreatedAsPassive.BlockId)] };
        Assert.Equal("This card is treated as a Normal Monster while in the hand or GY.", EffectTextGenerator.Generate(project));
        new EngineScenario(project).Card(card.Id, 0, "hand").Card(card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Check($"{Find(card.Id, "LOCATION_HAND")}:IsType(TYPE_NORMAL) and not {Find(card.Id, "LOCATION_HAND")}:IsType(TYPE_EFFECT)")
            .Check($"{Find(card.Id, "LOCATION_MZONE")}:IsType(TYPE_EFFECT)").AssertPasses();
    }

    [EngineFact]
    public void PatchworkFluffal_FrightfurOnTheField_AndAFusionSubstitute()
    {
        var card = Monster(100233051, 1);
        var treated = Piece(81481818, card, TreatedAsPassive.BlockId).Do.Single();
        Assert.Equal(("face_up", 0xad), (treated.Values["where"], treated.Filters["as"].Archetype));
        var sub = Piece(81481818, card, MaterialTreatedAsPassive.BlockId).Do.Single();
        Assert.Equal(("fusion_substitute", "field", 0xad), (sub.Values["kind"], sub.Values["where"], sub.Filters["for"].Archetype));
    }

    [EngineFact]
    public void ElementalMistressDoriado_AlsoFourAttributes()
    {
        var card = new CardData { Id = 100233061, Name = "Doriado", Frame = MonsterFrame.Ritual, Level = 3, Atk = 1200, Def = 1400, Attribute = CardAttribute.Light };
        var b = Piece(99414168, card, TreatedAsPassive.BlockId).Do.Single();
        Assert.Equal(CardAttribute.Earth | CardAttribute.Water | CardAttribute.Fire | CardAttribute.Wind, b.Filters["as"].Attribute);
        Assert.Equal("The Attribute of this card is also treated as EARTH, WATER, FIRE, and WIND while it is face-up on the field.",
            EffectTextGenerator.Generate(new CardProject { Card = card, Effects = [Piece(99414168, card, TreatedAsPassive.BlockId)] }));
    }

    [EngineTheory]
    [InlineData(74839123L, 0xad, MonsterKind.Fusion, CardAttribute.None)]
    [InlineData(14108995L, 0x182, MonsterKind.Any, CardAttribute.Earth)]
    public void FrightfurSanctuary_YourMonstersAreAlsoTreatedAsAnArchetype(long id, int archetype, MonsterKind kind, CardAttribute attribute)
    {
        var card = new CardData { Id = 100233121, Name = "Sanctuary", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous };
        var b = Piece(id, card, CardsTreatedAsPassive.BlockId).Do.Single();
        Assert.Equal((archetype, kind, attribute, CardOwner.You), (b.Filters["as"].Archetype, b.Filters["cards"].MonsterKind, b.Filters["cards"].Attribute, b.Filters["cards"].Owner));
    }

    // ---------------------------------------------------------------- "This card's name becomes ..."

    [EngineTheory]
    [InlineData(43266605L, "in", "true", "false", "none", "true")]
    [InlineData(63748694L, "in", "true", "true", "field", "true")]
    public void EnableChangeCode_InAnyPlaces(long id, string where, string hand, string deck, string field, string gy)
    {
        var card = Monster(100233071);
        var b = Piece(id, card, NameBecomesPassive.BlockId).Do.Single();
        Assert.Equal((where, hand, deck, field, gy), (b.Values["where"], b.Values["in_hand"], b.Values["in_deck"], b.Values["on_field"], b.Values["in_gy"]));
    }

    [EngineFact]
    public void EnableChangeCode_OnTheWholeField_IsNotJustTheCardsOwnZone()
    {
        // Treasures of the Kings (a Field Spell): LOCATION_ONFIELD.
        var card = new CardData { Id = 100233081, Name = "Treasures", Kind = CardKind.Spell, SpellKind = SpellKind.Field };
        var b = Piece(69299029, card, NameBecomesPassive.BlockId).Do.Single();
        Assert.Equal(("in", "onfield"), (b.Values["where"], b.Values["on_field"]));
        Assert.Contains("SetRange(LOCATION_ONFIELD)", ScriptCompiler.Compile(new CardProject { Card = card, Effects = [Piece(69299029, card, NameBecomesPassive.BlockId)] }).Lua);
    }

    // ---------------------------------------------------------------- how this card counts as material

    [EngineFact]
    public void LanternShark_XyzLevel3Or5_ForAWaterMonster()
    {
        var card = Monster(100233091, 4, CardAttribute.Water);
        var b = Piece(70156946, card, MaterialTreatedAsPassive.BlockId).Do.Single();
        Assert.Equal(("xyz_level", "3", "5", CardAttribute.Water), (b.Values["kind"], b.Values["level"], b.Values["level2"], b.Filters["for"].Attribute));
    }

    [EngineTheory]
    [InlineData(4632019L, "synchro_level", "field")]
    [InlineData(29253591L, "synchro_level", "any")]
    [InlineData(40380686L, "nontuner", null)]
    [InlineData(29888389L, "ritual_entire", "any")]
    [InlineData(51618973L, "ritual_entire", "field")]
    [InlineData(50259460L, "fusion_substitute", "any")]
    [InlineData(81096431L, "xyz_double", null)]
    public void MaterialTreatments(long id, string kind, string? where)
    {
        var card = Monster(100233101, 4, CardAttribute.Water, kind == "nontuner" ? MonsterAbility.Tuner : MonsterAbility.None);
        var effect = Piece(id, card, MaterialTreatedAsPassive.BlockId);
        var b = effect.Do.Single();
        Assert.Equal(kind, b.Values["kind"]);
        if (where is not null) Assert.Equal(where, b.Values["where"]);
        // Drake Shark's "2 materials" is once per turn, as its count limit says.
        if (kind == "xyz_double") Assert.Equal(OncePerTurn.Hard, effect.Limit);
    }

    // ---------------------------------------------------------------- "(This card is always treated as ...)"

    [Fact]
    public void AlwaysTreatedAs_TheReminderBecomesTheBlock_WhenTheCardHasThatArchetype()
    {
        var pack = new CardPack { Archetypes = [new Archetype { Code = 0xdd, Name = "Blue-Eyes" }] };
        var card = Monster(100233111, 8, CardAttribute.Light, archetypes: [0xdd]);
        card.Text = "(This card is always treated as a \"Blue-Eyes\" card.)\r\nThis card gains 100 ATK.";
        var project = new CardProject
        {
            Card = card, UseCustomLua = true,
            CustomLua = "local s,id=GetID()\nfunction s.initial_effect(c)\n\tlocal e1=Effect.CreateEffect(c)\n\te1:SetType(EFFECT_TYPE_SINGLE)\n\te1:SetProperty(EFFECT_FLAG_SINGLE_RANGE)\n\te1:SetRange(LOCATION_MZONE)\n\te1:SetCode(EFFECT_UPDATE_ATTACK)\n\te1:SetValue(100)\n\tc:RegisterEffect(e1)\nend\n",
        };
        ScriptTranslator.ApplyTo(project, pack, out var applied);
        Assert.True(applied);
        Assert.StartsWith("(This card is always treated as a \"Blue-Eyes\" card.) This card gains 100 ATK.", EffectTextGenerator.Generate(project, pack));
        // The archetype is the card's own already: nothing is added to the script or the database entry.
        Assert.Equal([0xdd], TreatedAsPassive.Setcodes(project));
    }
}
