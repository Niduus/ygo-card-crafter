using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Zones and fields: zones that cannot be used (Kashtira Shangri-Ira), players who must banish monsters so they only
/// control 1 (Kashtira Big Bang), cards taken from a target's materials (Kashtira Big Bang), discarding any number and
/// sending the monsters in that many of the opponent's zones to the GY (The Winged Dragon of Ra, Sun God of Darkness).
/// </summary>
public class ZoneBlockTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static string Lua(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics) + "\n" + r.Lua);
        Assert.DoesNotContain(Validation.ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        Assert.Empty(Validation.ScriptChecker.UnknownNames(r.Lua));
        return r.Lua;
    }

    private static CardProject Monster(long id, string name, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Effect, Race = CardRace.Fiend, Attribute = CardAttribute.Dark, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    private static CardProject Spell(long id, string name, params BlockInstance[] dos) => new()
    {
        Card = new CardData { Id = id, Name = name, Kind = CardKind.Spell, SpellKind = SpellKind.Normal },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [.. dos] }],
    };

    private static string Text(CardProject p, int i = 0) => EffectTextGenerator.Describe(p.Effects[i], p.Card);

    [EngineFact]
    public void ChooseAnUnusedZone_ItCannotBeUsedWhileThisMonsterIsFaceUp()
    {
        var p = Monster(100490001, "Shangri", new EffectEvent { When = B("when.ignition", ("zone", "monster_zone")), Do = [B(DisableZoneAction.BlockId)] });
        Assert.Equal("You can choose 1 unused Main Monster Zone or Spell & Trap Zone; it cannot be used while this monster is face-up on the field.",
            EffectTextGenerator.Describe(p.Effects[0], p.Card));
        Assert.Contains("Duel.SelectDisableField(tp,1,LOCATION_ONFIELD,LOCATION_ONFIELD,0xe000e0)", Lua(p));
        // This card takes one zone and the chosen one can't be used: 3 of the 5 are left.
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(p.Card.Id).Check("Duel.GetLocationCount(0,LOCATION_MZONE)==3").AssertPasses();
    }

    [Fact]
    public void ChooseZonesUntilTheEndOfTheNextTurn_Wording()
    {
        var p = Spell(100490002, "Zone Lock", B(DisableZoneAction.BlockId, ("count", "2"), ("zones", "opp_mmz"), ("duration", "next")));
        Assert.Equal("Choose 2 unused Main Monster Zones your opponent controls; they cannot be used until the end of the next turn.", Text(p));
        Assert.Contains("ez:SetReset(RESET_PHASE+PHASE_END,2)", Lua(p));
    }

    [EngineFact]
    public void EachPlayerMustBanishMonstersFaceDown_SoTheyOnlyControlOne()
    {
        var p = Spell(100490010, "Big Bang", B(MustReduceAction.BlockId));
        Assert.Equal("Each player who controls 2 or more monsters must banish monsters they control, face-down, so they only control 1 monster.", Text(p));
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "mzone", 3).Card(Fiend, 1, "mzone", 2).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(p.Card.Id)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1").Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1")
            .Check("Duel.IsExistingMatchingCard(Card.IsFacedown,0,LOCATION_REMOVED,0,2,nil)").Check("Duel.IsExistingMatchingCard(Card.IsFacedown,1,LOCATION_REMOVED,0,1,nil)")
            .AssertPasses();
    }

    [Fact]
    public void YourOpponentMustSendMonstersToTheGY_Wording()
    {
        var p = Spell(100490011, "Weighbridge", B(MustReduceAction.BlockId, ("who", "opponent"), ("how", "grave")));
        Assert.Equal("Your opponent must send monsters they control to the GY so they only control 1 monster.", Text(p));
        Assert.Contains("Duel.SendtoGrave(sg,REASON_RULE,1-tp)", Lua(p));
    }

    [EngineFact]
    public void AddOneOfYourMonstersAttachedToTheTarget_ThenSpecialSummonIt()
    {
        var mat = Monster(100490021, "Kash Material");
        var xyz = new CardProject
        {
            Card = new CardData { Id = 100490022, Name = "Kash Xyz", Frame = MonsterFrame.Xyz, Level = 4, Atk = 2000, Def = 2000, Race = CardRace.Fiend },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, XyzMaterialLevel = 4 },
        };
        var summon = B("do.special_summon").With("cards", new CardFilter { That = CardRef.Affected, CardType = FilterCardType.Monster }).MayDo();
        summon.Link = ActionLink.Then;
        var spell = Spell(100490020, "Big Bang Return",
            B(TargetCardsAction.BlockId).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, MonsterKind = MonsterKind.Xyz }),
            B(FromMaterialsAction.BlockId).With("cards", new CardFilter { Zones = Zone.None, CardType = FilterCardType.Monster, Race = CardRace.Fiend }),
            summon);
        Assert.Equal("Target 1 face-up Xyz Monster you control; add 1 of your Fiend monsters attached to it to your hand, then you can Special Summon it.", Text(spell));
        Lua(spell);
        new EngineScenario(spell, mat, xyz).Card(spell.Card.Id, 0, "hand").Card(xyz.Card.Id, 0, "extra").Card(mat.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .SpSummon(xyz.Card.Id).Activate(spell.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{xyz.Card.Id}):GetOverlayCount()==1")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{mat.Card.Id})").AssertPasses();
    }

    [EngineFact]
    public void DiscardAnyNumber_ThenChooseThatManyOfYourOpponentsOccupiedZones()
    {
        var ra = Monster(100490030, "Dark Ra", new EffectEvent
        {
            When = B("when.ignition", ("zone", "monster_zone")),
            Cost = [B("cost.discard", ("mode", "any")).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, Race = CardRace.Fiend })],
            Do = [B(SendZonesAction.BlockId, ("many", "cost"))],
        });
        Assert.Equal("You can discard any number of Fiend monsters, then choose that many of your opponent's occupied Main Monster Zones; " +
                     "your opponent must send the monsters in those zones to the GY.", Text(ra));
        Assert.Contains("Duel.SelectField(tp,zct,0,LOCATION_MZONE,~zf)", Lua(ra));
        // The automatic player discards 1 Fiend, so 1 zone is chosen.
        new EngineScenario(ra).Card(ra.Card.Id, 0, "mzone").Card(Fiend, 0, "hand", 2).Card(Warrior, 1, "mzone", 2).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(ra.Card.Id)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1").Check("Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)==1").Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .AssertPasses();
    }
}
