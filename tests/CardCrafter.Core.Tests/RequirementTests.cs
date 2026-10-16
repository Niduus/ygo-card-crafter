using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Card filter requirements: any property compared with a value, same-card vs different-card conditions, counts.</summary>
public class RequirementTests
{
    private const long Id = 100100001, DarkAtk = 100100002, DarkWeak = 100100003, LightAtk = 100100004;

    private static CardRequirement R(string property, RequirementOp op, int value = 0, int value2 = 0, params long[] values) =>
        new() { Property = property, Op = op, Value = value, Value2 = value2, Values = [.. values] };

    private static CardFilter Mine(params CardRequirement[] reqs) =>
        new() { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, Requirements = [.. reqs] };

    private static CardProject Card(BlockInstance condition) => new()
    {
        Card = new CardData { Id = Id, Name = "Req Test", Frame = MonsterFrame.Effect, Level = 4, Atk = 0, Def = 0, Attribute = CardAttribute.Earth },
        Effects = [new EffectEvent { When = new BlockInstance("when.ignition"), If = [condition], Do = [new BlockInstance("do.gain_lp").With("amount", 100)] }],
    };

    private static CardProject Monster(long id, CardAttribute attr, int atk, int def) => new()
    {
        Card = new CardData { Id = id, Name = $"M{id}", Frame = MonsterFrame.Normal, Level = 4, Atk = atk, Def = def, Attribute = attr, Race = CardRace.Fiend },
    };

    private static BlockInstance Exists(CardFilter f, int count = 1)
    {
        var b = new BlockInstance("if.cards_exist").With("count", count);
        b.Filters["cards"] = f;
        return b;
    }

    public static IEnumerable<object[]> Texts()
    {
        yield return [R("base_atk", RequirementOp.GreaterOrEqual, 2000), "with 2000 or more original ATK"];
        yield return [R("atk", RequirementOp.Between, 1500, 2500), "with 1500 to 2500 ATK"];
        yield return [R("atk", RequirementOp.NotEqual, 2000), "whose ATK is other than 2000"];
        yield return [R("level", RequirementOp.LessOrEqual, 4), "whose Level is 4 or lower"];
        yield return [R("orig_attribute", RequirementOp.IsNot, 0, 0, (long)CardAttribute.Dark), "whose original Attribute is not DARK"];
        yield return [R("attribute", RequirementOp.IsNot, 0, 0, (long)CardAttribute.Dark, (long)CardAttribute.Light), "that is neither LIGHT nor DARK"];
        yield return [R("summon", RequirementOp.Is, 0, 0, 4, 5), "that was Fusion or Synchro Summoned"];
        yield return [R("negated", RequirementOp.Is), "whose effects are negated"];
        yield return [R("materials", RequirementOp.GreaterOrEqual, 2), "with 2 or more materials attached"];
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void Reads(CardRequirement r, string phrase)
    {
        var text = FilterText.Describe(Mine(r), null, withLocation: false);
        Assert.Equal("face-up monster " + phrase, text);
        var lua = ScriptCompiler.Compile(Card(Exists(Mine(r)))).Lua;
        Assert.Empty(ScriptChecker.Check(lua));
    }

    [Fact]
    public void OldFilters_CompileTheSame()
    {
        var plain = Mine();
        Assert.DoesNotContain(FilterCompiler.Conditions(plain), c => c.Contains("GetBaseAttack", StringComparison.Ordinal));
        // Saved and loaded, a filter without requirements stays without them; with them, they survive.
        var pack = new CardPack { Cards = [Card(Exists(Mine(R("base_atk", RequirementOp.Greater, 1000))))] };
        var back = PackSerializer.FromJson(PackSerializer.ToJson(pack));
        var req = Assert.Single(back.Cards[0].Effects[0].If[0].Filters["cards"].Requirements);
        Assert.Equal(("base_atk", RequirementOp.Greater, 1000), (req.Property, req.Op, req.Value));
        var old = PackSerializer.FromJson(PackSerializer.ToJson(new CardPack { Cards = [Card(Exists(Mine()))] }).Replace("\"requirements\": [],", ""));
        Assert.Empty(old.Cards[0].Effects[0].If[0].Filters["cards"].Requirements);
    }

    // A: one monster must be DARK, have 2000 ATK and 1500 DEF. B: a DARK monster and a different 2000-ATK monster.
    // C: two monsters with ATK between 1500 and 2500.
    [EngineTheory]
    [InlineData("A", new[] { DarkAtk }, true)]
    [InlineData("A", new[] { DarkWeak, LightAtk }, false)]
    [InlineData("B", new[] { DarkWeak, LightAtk }, true)]
    [InlineData("B", new[] { DarkAtk }, false)]
    [InlineData("C", new[] { DarkAtk, LightAtk }, true)]
    [InlineData("C", new[] { DarkAtk, DarkWeak }, false)]
    public void SameVersusDifferentMonsters(string which, long[] field, bool activates)
    {
        var dark = R("attribute", RequirementOp.Is, 0, 0, (long)CardAttribute.Dark);
        var atk2000 = R("atk", RequirementOp.Equal, 2000);
        BlockInstance condition = which switch
        {
            "A" => Exists(Mine(dark, atk2000, R("def", RequirementOp.Equal, 1500))),
            "B" => TwoCards(Mine(dark), Mine(atk2000)),
            _ => Exists(Mine(R("atk", RequirementOp.Between, 1500, 2500)), 2),
        };
        var scenario = new EngineScenario(Card(condition), Monster(DarkAtk, CardAttribute.Dark, 2000, 1500), Monster(DarkWeak, CardAttribute.Dark, 500, 500), Monster(LightAtk, CardAttribute.Light, 2000, 1000))
            .Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4);
        foreach (var m in field) scenario.Card(m, 0, "mzone");
        scenario.Activate(Id).ExpectActivations(activates ? 1 : 0).AssertPasses();
    }

    private static BlockInstance TwoCards(CardFilter a, CardFilter b)
    {
        var block = new BlockInstance(TwoCardsCondition.BlockId);
        block.Filters["cards"] = a;
        block.Filters["other"] = b;
        return block;
    }

    [Fact]
    public void TwoCards_Reads()
    {
        var dark = Mine(R("attribute", RequirementOp.Is, 0, 0, (long)CardAttribute.Dark));
        var strong = Mine(R("atk", RequirementOp.Equal, 2000));
        Assert.StartsWith("If you control a face-up DARK monster and a different face-up monster with 2000 ATK: ",
            EffectTextGenerator.Generate(Card(TwoCards(dark, strong))));
    }
}

/// <summary>"This card is always treated as a "HERO" card", "also treated as a DARK Tuner".</summary>
public class TreatedAsTests
{
    [EngineFact]
    public void ArchetypeNameAttributeAndTuner_Apply()
    {
        const long id = 100100101;
        var b = new BlockInstance(TreatedAsPassive.BlockId);
        b.Filters["as"] = new CardFilter { CardType = FilterCardType.Monster, Archetype = 0x8, Attribute = CardAttribute.Dark, MonsterKind = MonsterKind.Tuner, CardCode = 46986414 };
        var p = new CardProject
        {
            Card = new CardData { Id = id, Name = "Treated", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000, Attribute = CardAttribute.Earth },
            Effects = [new EffectEvent { When = new BlockInstance("when.while_face_up"), Do = [b] }],
        };
        CardNames.Set([(46986414, "Dark Magician")]);
        try
        {
            Assert.Equal("This card is always treated as a \"HERO\" card, and this card's name is also treated as \"Dark Magician\", and this card is also treated as a DARK Tuner.",
                EffectTextGenerator.Generate(p, new CardPack { Cards = [p], Archetypes = [new Archetype { Code = 0x8, Name = "HERO" }] }));
        }
        finally { CardNames.Set([]); }
        new EngineScenario(p).Card(id, 0, "mzone").Card(id, 0, "grave").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Check($"Duel.GetMatchingGroupCount(Card.IsSetCard,0,LOCATION_MZONE+LOCATION_GRAVE,0,nil,0x8)==2")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,46986414)")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{id}):IsAttribute(ATTRIBUTE_DARK)")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{id}):IsAttribute(ATTRIBUTE_EARTH)")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{id}):IsType(TYPE_TUNER)").AssertPasses();
    }
}

/// <summary>"a DARK monster or a Dragon monster": any one requirement is enough.</summary>
public class AnyRequirementTests
{
    [Fact]
    public void Or_ReadsAndCompiles()
    {
        var f = new CardFilter
        {
            Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, AnyRequirement = true,
            Requirements =
            [
                new CardRequirement { Property = "attribute", Op = RequirementOp.Is, Values = [(long)CardAttribute.Dark] },
                new CardRequirement { Property = "race", Op = RequirementOp.Is, Values = [(long)CardRace.Dragon] },
            ],
        };
        Assert.Equal("monster that is DARK or that is a Dragon monster", FilterText.Describe(f, null, withLocation: false));
        var conditions = FilterCompiler.Conditions(f);
        Assert.Contains("((c:IsAttribute(0x20)) or (c:IsRace(0x2000)))", conditions);
    }
}
