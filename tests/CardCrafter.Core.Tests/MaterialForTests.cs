using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"as material for the Ritual Summon of a LIGHT Reptile "Infested" monster": which monster this card was material for.</summary>
public class MaterialForTests
{
    private const int Infested = 0x1f0;

    private static CardFilter LightReptileInfested() => new()
    {
        Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Monster,
        Attribute = CardAttribute.Light, Race = CardRace.Reptile, Archetype = Infested,
    };

    private static CardProject Material(BlockInstance when, params BlockInstance[] ifs) => new()
    {
        Card = new CardData { Id = 100000370, Name = "Shedding Scale", Level = 4, Atk = 1000 },
        Effects = [new EffectEvent { When = when, If = [.. ifs], Do = [new BlockInstance("do.draw").With("count", 1)] }],
    };

    private static CardPack Pack(CardProject p) => new() { Cards = [p], Archetypes = [new Archetype { Code = Infested, Name = "Infested" }] };

    [Fact]
    public void TheWhen_SaysWhichMonster_AsOfficialCardsDo()
    {
        var when = new BlockInstance("when.used_as_material").With("kind", "ritual").With("gy", true).With("optional", true);
        when.Filters["summoner"] = LightReptileInfested();
        var p = Material(when);
        var text = EffectTextGenerator.Generate(p, Pack(p));
        Assert.Contains("If this card is sent to the GY as material for the Ritual Summon of a LIGHT Reptile \"Infested\" monster: You can draw 1 card.", text);

        // Left as "any monster", the old wording stays.
        var plain = Material(new BlockInstance("when.used_as_material").With("kind", "ritual"));
        Assert.Contains("If this card is used as material for a Ritual Summon:", EffectTextGenerator.Generate(plain, Pack(plain)));
    }

    [Fact]
    public void TheIf_ChecksTheMonsterThisCardWasMaterialFor()
    {
        var cond = new BlockInstance("if.used_as_material").With("kind", "ritual");
        cond.Filters["summoner"] = LightReptileInfested();
        var p = Material(new BlockInstance("when.sent_to_gy"), cond);
        var text = EffectTextGenerator.Generate(p, Pack(p));
        Assert.Contains("this card was used as material for the Ritual Summon of a LIGHT Reptile \"Infested\" monster", text);
        var lua = ScriptCompiler.Compile(p, Pack(p)).Lua;
        Assert.Contains("e:GetHandler():IsReason(REASON_RITUAL)", lua);
        Assert.Contains(":GetReasonCard(),e,tp)", lua);
    }

    private static CardProject Link(long id, CardAttribute attribute, CardRace race) => new()
    {
        Card = new CardData { Id = id, Name = $"Link {id}", Frame = MonsterFrame.Link, Atk = 1600, Attribute = attribute, Race = race, Archetypes = [Infested], LinkMarkers = LinkMarker.Left | LinkMarker.Right },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 2 },
    };

    [EngineTheory]
    [InlineData(CardAttribute.Light, CardRace.Reptile, 1)]
    [InlineData(CardAttribute.Dark, CardRace.Reptile, 0)]
    [InlineData(CardAttribute.Light, CardRace.Warrior, 0)]
    public void TheWhen_OnlyStartsForThatMonster(CardAttribute attribute, CardRace race, int activations)
    {
        var when = new BlockInstance("when.used_as_material").With("kind", "link");
        when.Filters["summoner"] = LightReptileInfested();
        var mat = Material(when);
        var link = Link(100000371, attribute, race);
        var scenario = new EngineScenario(mat, link)
            .Card(mat.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(link.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 3)
            .SpSummon(link.Card.Id);
        if (activations > 0) scenario.Activate(mat.Card.Id);
        scenario.ExpectActivations(activations)
            .Check($"Duel.GetFieldGroupCount(0,LOCATION_HAND,0)=={activations}")
            .AssertPasses();
    }
}
