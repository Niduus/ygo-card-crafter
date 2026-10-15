using CardCrafter.Core.Compiler;
using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Tests;

public class CatalogTests
{
    [Fact]
    public void Generator_AvoidsUsedBlocks()
    {
        var used = new List<long>();
        var rng = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            var id = PasscodeGenerator.Generate(used, rng);
            Assert.InRange(id, 100_000_000, 999_990_000);
            Assert.Equal(0, id % 10_000);
            Assert.DoesNotContain(used, u => u >= id && u < id + 10_000);
            used.Add(id + 5); // something else lives in that block now
        }
    }

    [Theory]
    [InlineData(new[] { "Elemental HERO Neos", "Elemental HERO Avian", "Elemental HERO Burstinatrix" }, "Elemental HERO")]
    [InlineData(new[] { "Blue-Eyes White Dragon", "Blue-Eyes Alternative White Dragon", "Blue-Eyes Chaos MAX Dragon", "The White Stone of Ancients" }, "Blue-Eyes")]
    [InlineData(new[] { "Solo" }, "Solo")]
    public void ArchetypeNames_AreGuessedFromCardNames(string[] names, string expected) =>
        Assert.Equal(expected, OmegaCatalog.GuessName(names));

    [Fact]
    public void Catalog_ReadsPasscodesArchetypesAndCustomTypes()
    {
        using var tmp = new TempDir();
        var db = Path.Combine(tmp.Path, "db");
        CdbFile.WriteFresh(db,
        [
            Row(1, "Star Knight Alpha", 0xa50, CardRace.Warrior),
            Row(2, "Star Knight Beta", 0xa50, CardRace.Warrior),
            Row(3, "Cyber Gizmo", 0, (CardRace)0x8000000),
        ]);
        var cat = OmegaCatalog.Load([db], tmp.Path);
        Assert.Equal("Star Knight Beta (db)", cat.Clash(2));
        Assert.Null(cat.Clash(4));
        var arch = Assert.Single(cat.Archetypes);
        Assert.Equal(("Star Knight", 0xa50, 2), (arch.Name, arch.Code, arch.Cards));
        var custom = Assert.Single(cat.UnknownTypes);
        Assert.Equal(0x8000000, custom.Value);
        Assert.NotEqual(0xa50, cat.FreeArchetypeCode([]) & 0xfff);
    }

    private static CdbRow Row(long id, string name, int setcode, CardRace race) =>
        CdbEncoder.Encode(new CardData { Id = id, Name = name, Archetypes = setcode == 0 ? [] : [setcode], Race = race }, [], 3);

    [Fact]
    public void CustomType_CompilesAsHexAndReadsByName()
    {
        var pack = new CardPack { CustomTypes = [new CustomType { Value = 0x8000000, Name = "Cyborg" }] };
        var p = Samples.PotOfGreedClone();
        p.Effects[0].Do = [new BlockInstance("do.add_to_hand").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = (CardRace)0x8000000 })];
        pack.Cards.Add(p);
        var lua = ScriptCompiler.Compile(p, pack).Lua;
        Assert.Contains("c:IsRace(0x8000000)", lua);
        Assert.Contains("Cyborg monster", EffectTextGenerator.Generate(p, pack));
        Assert.Equal("[Cyborg/Effect]", CardFaceText.TypeLine(new CardData { Race = (CardRace)0x8000000 }, pack));
    }

    [Theory]
    [InlineData(MonsterFrame.Synchro, MonsterAbility.Tuner, "[Dragon/Synchro/Tuner/Effect]")]
    [InlineData(MonsterFrame.Normal, MonsterAbility.None, "[Dragon]")]
    [InlineData(MonsterFrame.Normal, MonsterAbility.Pendulum, "[Dragon/Pendulum/Normal]")]
    [InlineData(MonsterFrame.Effect, MonsterAbility.Flip, "[Dragon/Flip/Effect]")]
    public void TypeLine_MatchesRealCards(MonsterFrame frame, MonsterAbility abilities, string expected) =>
        Assert.Equal(expected, CardFaceText.TypeLine(new CardData { Race = CardRace.Dragon, Frame = frame, Abilities = abilities }, null));

    [Fact]
    public void CustomType_RoundTripsThroughPackJson()
    {
        var pack = new CardPack { CustomTypes = [new CustomType { Value = 0x8000000, Name = "Cyborg" }] };
        pack.Cards.Add(new CardProject { Card = new CardData { Race = (CardRace)0x8000000 } });
        var back = PackSerializer.FromJson(PackSerializer.ToJson(pack));
        Assert.Equal((CardRace)0x8000000, back.Cards[0].Card.Race);
        Assert.Equal("Cyborg", back.CustomTypes[0].Name);
    }
}
