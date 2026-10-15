using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>The blocks added from the scan of official scripts, played in real duels.</summary>
public class ScanBlockEngineTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Monster(long id, EffectEvent effect, MonsterFrame frame = MonsterFrame.Effect) => new()
    {
        Card = new CardData { Id = id, Name = "Test " + id, Frame = frame, Level = 4, Atk = 1800, Def = 1000 },
        Effects = [effect],
    };

    private static EffectEvent Ignition(params BlockInstance[] actions) => new() { When = B("when.ignition"), Do = [.. actions] };

    [EngineFact]
    public void NegateSummon_DestroysTheMonster()
    {
        var p = Monster(100005001, new EffectEvent
        {
            When = B("when.quick", ("timing", "would_summon"), ("zone", "hand")),
            Cost = [B("cost.this_card", ("how", "discard"))],
            Do = [B("do.negate_summon")],
        });
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Fiend, 1, "hand").Card(Warrior, 1, "deck", 5)
            .Summon(Fiend).Activate(p.Card.Id).Turns(2).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0")
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_GRAVE,0,1,nil,1004)")
            .AssertPasses();
    }

    [EngineFact]
    public void Tokens_AreSpecialSummoned()
    {
        const long token = 100005099;
        var p = Monster(100005002, Ignition(B("do.tokens", ("count", "2"), ("token", token.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        var t = new CardProject { Card = new CardData { Id = token, Name = "Sheep Token", Frame = MonsterFrame.Token, Level = 1, Race = CardRace.Beast, Attribute = CardAttribute.Earth } };
        new EngineScenario(p, t).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5).Activate(p.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==3")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,2,nil,{token})")
            .AssertPasses();
    }

    [EngineFact]
    public void TakeControl_UntilTheEndPhase()
    {
        var p = Monster(100005003, Ignition(B("do.take_control")));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1)
            // Back to its owner after the End Phase: the duel stops at turn 2, so it is back by then.
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void Discard_OpponentDiscardsAtRandom()
    {
        var p = Monster(100005004, Ignition(B("do.discard", ("how", "random"))));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Fiend, 1, "hand", 2).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void ChangePosition_AndHalveAtk()
    {
        var p = Monster(100005005, Ignition(B("do.change_position", ("target", "true")), B("do.set_atk", ("target", "false"), ("duration", "always"))));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldCard(1,LOCATION_MZONE,0):IsDefensePos()")
            .Check("Duel.GetFieldCard(1,LOCATION_MZONE,0):GetAttack()==500")
            .AssertPasses();
    }

    [EngineFact]
    public void EquipSpell_EquipsAndBoosts_OnlyAllowedMonsters()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100005006, Name = "Warrior Blade", Kind = CardKind.Spell, SpellKind = SpellKind.Equip },
            EquipFilter = new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Warrior },
            Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.equipped", ("amount", "700"))] }],
        };
        Assert.StartsWith("Equip only to a Warrior monster. The equipped monster gains 700 ATK", CardCrafter.Core.Text.EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 0, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldCard(0,LOCATION_MZONE,0):GetAttack()==2200")
            .Check("Duel.GetFieldCard(0,LOCATION_MZONE,1):GetAttack()==1000")
            .AssertPasses();
    }

    [EngineFact]
    public void IfThisCardIsBanished_Draws()
    {
        var banisher = Monster(100005007, Ignition(B("do.banish")));
        banisher.Effects[0].Do[0].Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster };
        var p = Monster(100005008, new EffectEvent { When = B("when.this_card", ("event", "banished")), Do = [B("do.draw")] });
        new EngineScenario(banisher, p).Card(banisher.Card.Id, 0, "mzone").Card(p.Card.Id, 0, "grave").Card(Warrior, 0, "deck", 5)
            .Activate(banisher.Card.Id).Activate(p.Card.Id).ExpectActivations(2)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void IfThisCardIsLinkSummoned_Draws()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100005009, Name = "Linker", Frame = MonsterFrame.Link, Atk = 1600, LinkMarkers = LinkMarker.Left | LinkMarker.Right },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 2 },
            Effects = [new EffectEvent { When = B("when.summoned", ("how", "link")), Do = [B("do.draw")] }],
        };
        new EngineScenario(p).Card(EffectFiller, 0, "mzone", 2).Card(p.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 5)
            .SpSummon(p.Card.Id).Activate(p.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void SummonRestriction_StopsOtherSpecialSummonsThatTurn()
    {
        // Except DARK monsters: a LIGHT monster that can Special Summon itself from the hand can't, after this resolves.
        var p = Monster(100005010, Ignition(B("do.summon_restriction")));
        CardProject Summoner(long id, CardAttribute attribute) => new()
        {
            Card = new CardData { Id = id, Name = "Summoner " + id, Frame = MonsterFrame.Effect, Level = 4, Attribute = attribute },
            Effects = [new EffectEvent { When = B("when.summon_procedure", ("zone", "hand")) }],
        };
        var light = Summoner(100005011, CardAttribute.Light);
        new EngineScenario(p, light).Card(p.Card.Id, 0, "mzone").Card(light.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).SpSummon(light.Card.Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,{light.Card.Id})")
            .AssertPasses();
        var dark = Summoner(100005012, CardAttribute.Dark);
        new EngineScenario(p, dark).Card(p.Card.Id, 0, "mzone").Card(dark.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Activate(p.Card.Id).SpSummon(dark.Card.Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{dark.Card.Id})")
            .AssertPasses();
    }
}
