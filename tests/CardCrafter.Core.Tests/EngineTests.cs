using CardCrafter.Core.Blocks;
using CardCrafter.Core.Model;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Plays real duels in the ygopro engine with Card Crafter scripts.</summary>
public class EngineTests
{
    [EngineFact]
    public void PotOfGreedClone_DrawsTwo()
    {
        var pot = Samples.PotOfGreedClone();
        new EngineScenario(pot)
            .Card(pot.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 10)
            .Activate(pot.Card.Id)
            .ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void Harness_CatchesBrokenScripts()
    {
        // Same card, but the operation calls a function that doesn't exist. The duel must fail.
        var pot = Samples.PotOfGreedClone();
        var lua = CardCrafter.Core.Compiler.ScriptCompiler.Compile(pot).Lua.Replace("Duel.Draw(tp,2,REASON_EFFECT)", "Duel.DrawCards(tp,2,REASON_EFFECT)");
        pot.UseCustomLua = true;
        pot.CustomLua = lua;
        var (code, log) = new EngineScenario(pot)
            .Card(pot.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 10)
            .Activate(pot.Card.Id)
            .Run();
        Assert.True(code != 0, log);
        Assert.Contains("SCRIPT ERROR", log);
    }

    [EngineFact]
    public void DragonCommander_SearchesOnSummon_AndBoostsDragons()
    {
        var dc = Samples.DragonCommander();
        new EngineScenario(dc)
            .Card(Dragon, 0, "mzone")
            .Card(dc.Card.Id, 0, "hand").Card(Dragon, 0, "deck", 3).Card(Warrior, 0, "deck", 5)
            .Summon(dc.Card.Id).Activate(dc.Card.Id)
            .ExpectActivations(1)
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,1002)")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==7")
            // Two face-up Dragons now: each gets +300 ATK.
            .Check("Duel.GetFieldCard(0,LOCATION_MZONE,0):GetAttack()==1900")
            .AssertPasses();
    }

    [EngineFact]
    public void DragonCommander_QuickEffectFromGraveyard_OnOpponentsTurn()
    {
        var dc = Samples.DragonCommander();
        new EngineScenario(dc)
            .Card(dc.Card.Id, 0, "grave").Card(Warrior, 0, "deck", 5)
            .Card(Fiend, 1, "mzone").Card(Warrior, 1, "deck", 5)
            .Activate(dc.Card.Id)
            .Turns(2)
            .ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0")
            .Check("Duel.GetLP(1)==7500")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_REMOVED,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void QuickEffect_DoesNotActivateOnOwnTurn()
    {
        var dc = Samples.DragonCommander();
        new EngineScenario(dc)
            .Card(dc.Card.Id, 0, "grave").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(dc.Card.Id)
            .ExpectActivations(0)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1")
            .AssertPasses();
    }

    private static CardProject SummonedQuickEffectCard() => new()
    {
        Card = new CardData { Id = 100000230, Name = "Ambusher", Level = 4, Atk = 1500 },
        Effects =
        [
            new EffectEvent
            {
                Label = "Summoned this turn",
                When = new BlockInstance("when.quick"),
                If = [new BlockInstance("if.summoned_this_turn")],
                Do = [new BlockInstance("do.draw").With("count", 1)],
                Limit = OncePerTurn.Hard,
            },
        ],
    };

    [EngineFact]
    public void QuickEffect_IfSummonedThisTurn_Works()
    {
        var card = SummonedQuickEffectCard();
        new EngineScenario(card)
            .Card(card.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Summon(card.Card.Id).Activate(card.Card.Id)
            .ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void QuickEffect_IfSummonedThisTurn_NotUsableOtherwise()
    {
        var card = SummonedQuickEffectCard();
        new EngineScenario(card)
            .Card(card.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(card.Card.Id)
            .ExpectActivations(0)
            .AssertPasses();
    }

    [EngineFact]
    public void QuickEffect_WhenAnyMonsterIsSummoned()
    {
        // In the GY: when a monster is Summoned (here, by its own owner), gain 500 LP.
        var card = new CardProject
        {
            Card = new CardData { Id = 100000231, Name = "Watcher", Level = 4 },
            Effects =
            [
                new EffectEvent
                {
                    When = new BlockInstance("when.quick").With("timing", "any_summon").With("zone", "graveyard"),
                    Do = [new BlockInstance("do.gain_lp").With("amount", 500)],
                },
            ],
        };
        new EngineScenario(card)
            .Card(card.Card.Id, 0, "grave").Card(Warrior, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Summon(Warrior).Activate(card.Card.Id)
            .ExpectActivations(1)
            .Check("Duel.GetLP(0)==8500")
            .AssertPasses();
    }

    [EngineFact]
    public void Negate_StopsTheOpponentsSpell_AndDestroysIt()
    {
        var pot = Samples.PotOfGreedClone();
        var ash = new CardProject
        {
            Card = new CardData { Id = 100000240, Name = "Hand Trap", Level = 3, Atk = 0, Def = 1800 },
            Effects =
            [
                new EffectEvent
                {
                    When = new BlockInstance("when.quick").With("timing", "chain_any").With("zone", "hand"),
                    Cost = [new BlockInstance("cost.this_card").With("how", "discard")],
                    Do = [new BlockInstance("do.negate").With("what", "activation").With("and", "destroy")],
                    Limit = OncePerTurn.Hard,
                },
            ],
        };
        new EngineScenario(pot, ash)
            .Card(ash.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Card(pot.Card.Id, 1, "hand").Card(Warrior, 1, "deck", 5)
            .Activate(ash.Card.Id).Activate(pot.Card.Id)
            .Turns(2)
            .ExpectActivations(2)
            // The opponent drew 1 for turn, played the Spell, and drew nothing from it.
            .Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==1")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_GRAVE,0,1,nil,{pot.Card.Id})")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{ash.Card.Id})")
            .AssertPasses();
    }

    private static CardProject LinkCard(params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = 100000250, Name = "Arrow Keeper", Frame = MonsterFrame.Link, Atk = 1500, LinkMarkers = LinkMarker.Left | LinkMarker.Right },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 2 },
        Effects = [.. effects],
    };

    [EngineFact]
    public void LinkArrows_BoostAndProtectTheMonstersThisCardPointsTo()
    {
        var pointed = new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, PointedToByThisCard = true };
        var link = LinkCard(new EffectEvent
        {
            When = new BlockInstance("when.while_face_up"),
            Do =
            [
                new BlockInstance("passive.boost_cards").With("cards", pointed).With("amount", 500),
                new BlockInstance("passive.protect_cards").With("cards", pointed.Clone()).With("by", "both"),
            ],
        });
        // Zones 0, 1, 2: Warrior, the Link monster (arrows left and right), Warrior. Zone 3: Warrior it doesn't point to.
        new EngineScenario(link)
            .Card(Warrior, 0, "mzone").Card(link.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "mzone")
            .Card(Warrior, 0, "deck", 3)
            .Check("Duel.GetFieldCard(0,LOCATION_MZONE,0):GetAttack()==2000")
            .Check("Duel.GetFieldCard(0,LOCATION_MZONE,2):GetAttack()==2000")
            .Check("Duel.GetFieldCard(0,LOCATION_MZONE,3):GetAttack()==1500")
            .Check("Duel.GetFieldCard(0,LOCATION_MZONE,0):IsHasEffect(EFFECT_INDESTRUCTABLE_BATTLE)~=nil")
            .Check("Duel.GetFieldCard(0,LOCATION_MZONE,3):IsHasEffect(EFFECT_INDESTRUCTABLE_BATTLE)==nil")
            .AssertPasses();
    }

    [EngineFact]
    public void LinkArrows_ConditionsAndSummoningToAPointedZone()
    {
        var link = LinkCard(new EffectEvent
        {
            When = new BlockInstance("when.ignition"),
            If = [new BlockInstance("if.points_to").With("count", 1)],
            Do = [new BlockInstance("do.special_summon").With("zone", "pointed")
                .With("cards", new CardFilter { Zones = Zone.Graveyard, CardType = FilterCardType.Monster })],
        });
        // The Link monster is in zone 0 pointing left (nothing) and right (zone 1, a Warrior). Zone 2 is empty but not pointed to,
        // so the summon has nowhere to go after the Warrior moves... it must land in a pointed zone only.
        new EngineScenario(link)
            .Card(link.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone")
            .Card(Dragon, 0, "grave").Card(Warrior, 0, "deck", 3)
            .Activate(link.Card.Id)
            .ExpectActivations(0)
            .AssertPasses();

        var link2 = LinkCard(new EffectEvent
        {
            When = new BlockInstance("when.ignition"),
            If = [new BlockInstance("if.linked").With("state", "unlinked")],
            Do = [new BlockInstance("do.special_summon").With("zone", "pointed")
                .With("cards", new CardFilter { Zones = Zone.Graveyard, CardType = FilterCardType.Monster })],
        });
        // Alone in zone 0, unlinked: the Dragon must be summoned into zone 1, the only zone it points to.
        new EngineScenario(link2)
            .Card(link2.Card.Id, 0, "mzone").Card(Dragon, 0, "grave").Card(Warrior, 0, "deck", 3)
            .Activate(link2.Card.Id)
            .ExpectActivations(1)
            .Check("Duel.GetFieldCard(0,LOCATION_MZONE,1)~=nil and Duel.GetFieldCard(0,LOCATION_MZONE,1):IsCode(1002)")
            .AssertPasses();
    }

    [EngineFact]
    public void Xyz_DetachMaterialAsCost()
    {
        var xyz = new CardProject
        {
            Card = new CardData { Id = 100000260, Name = "Rank 4 Drawer", Frame = MonsterFrame.Xyz, Level = 4, Atk = 2000, Def = 2000 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2 },
            Effects =
            [
                new EffectEvent
                {
                    When = new BlockInstance("when.ignition"),
                    If = [new BlockInstance("if.xyz_materials").With("count", 2)],
                    Cost = [new BlockInstance("cost.detach").With("count", 1)],
                    Do = [new BlockInstance("do.draw").With("count", 1)],
                    Limit = OncePerTurn.Soft,
                },
            ],
        };
        new EngineScenario(xyz)
            .Card(Warrior, 0, "mzone", 2).Card(xyz.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 3)
            .SpSummon(xyz.Card.Id).Activate(xyz.Card.Id)
            .ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void UsedAsMaterialTrigger_Fires()
    {
        var mat = new CardProject
        {
            Card = new CardData { Id = 100000270, Name = "Eager Material", Level = 4, Atk = 1000 },
            Effects =
            [
                new EffectEvent
                {
                    When = new BlockInstance("when.used_as_material").With("kind", "link"),
                    Do = [new BlockInstance("do.draw").With("count", 1)],
                },
            ],
        };
        var xyz = new CardProject
        {
            Card = new CardData { Id = 100000271, Name = "Link Two", Frame = MonsterFrame.Link, Atk = 1600, LinkMarkers = LinkMarker.Left | LinkMarker.Right },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 2 },
        };
        new EngineScenario(mat, xyz)
            .Card(mat.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(xyz.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 3)
            .SpSummon(xyz.Card.Id).Activate(mat.Card.Id)
            .ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void NegateEffects_LastsWhileFaceUp()
    {
        var card = new CardProject
        {
            Card = new CardData { Id = 100000280, Name = "Silencer", Level = 4 },
            Effects =
            [
                new EffectEvent
                {
                    When = new BlockInstance("when.ignition"),
                    Do = [new BlockInstance("do.negate_effects").With("target", true).With("duration", "always")],
                },
            ],
        };
        new EngineScenario(card)
            .Card(card.Card.Id, 0, "mzone").Card(EffectFiller, 1, "mzone").Card(Warrior, 0, "deck", 3)
            .Activate(card.Card.Id)
            .ExpectActivations(1)
            .Check("Duel.GetFieldCard(1,LOCATION_MZONE,0):IsDisabled()")
            .AssertPasses();
    }

    [EngineFact]
    public void HardOncePerTurn_IsEnforced()
    {
        var pot = Samples.PotOfGreedClone();
        pot.Effects[0].Limit = OncePerTurn.Hard;
        new EngineScenario(pot)
            .Card(pot.Card.Id, 0, "hand", 2).Card(Warrior, 0, "deck", 10)
            .Activate(pot.Card.Id, 2)
            .ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==3")
            .AssertPasses();
    }

    [EngineFact]
    public void EndPhaseTrigger_AndSentToGraveyardTrigger()
    {
        // Ignition: send this card to the GY as a cost to draw 1. Trigger: if sent to the GY from the field, gain 1000.
        var card = new CardProject
        {
            Card = new CardData { Id = 100000200, Name = "Recycler", Level = 4, Atk = 1000 },
            Effects =
            [
                new EffectEvent
                {
                    Label = "Draw",
                    When = new BlockInstance("when.ignition"),
                    Cost = [new BlockInstance("cost.this_card").With("how", "grave")],
                    Do = [new BlockInstance("do.draw").With("count", 1)],
                },
                new EffectEvent
                {
                    Label = "Gain",
                    When = new BlockInstance("when.sent_to_gy").With("from", "field"),
                    Do = [new BlockInstance("do.gain_lp").With("amount", 1000)],
                },
                new EffectEvent
                {
                    Label = "End Phase burn",
                    When = new BlockInstance("when.phase").With("whose", "your").With("phase", "end").With("zone", "graveyard").With("optional", false),
                    Do = [new BlockInstance("do.damage").With("amount", 300)],
                },
            ],
        };
        new EngineScenario(card)
            .Card(card.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(card.Card.Id, 2)
            .ExpectActivations(2)
            .Check("Duel.GetLP(0)==9000")
            .Check("Duel.GetLP(1)==7700")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void DestroyedTrigger_FiresWhenDestroyedByOwnEffect()
    {
        var card = new CardProject
        {
            Card = new CardData { Id = 100000210, Name = "Martyr", Level = 4, Atk = 1000 },
            Effects =
            [
                new EffectEvent
                {
                    When = new BlockInstance("when.ignition"),
                    Do = [new BlockInstance("do.destroy").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone })],
                },
                new EffectEvent
                {
                    When = new BlockInstance("when.destroyed").With("by", "effect"),
                    Do = [new BlockInstance("do.special_summon").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster, CardCode = Dragon })],
                },
            ],
        };
        new EngineScenario(card)
            .Card(card.Card.Id, 0, "mzone").Card(Dragon, 0, "deck", 2)
            .Activate(card.Card.Id, 2)
            .ExpectActivations(2)
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,1002)")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void XyzProcedure_SummonsFromExtraDeck()
    {
        var xyz = new CardProject
        {
            Card = new CardData { Id = 100000220, Name = "Rank 4", Frame = MonsterFrame.Xyz, Level = 4, Atk = 2500, Def = 2000 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2 },
        };
        new EngineScenario(xyz)
            .Card(Warrior, 0, "mzone", 2).Card(xyz.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 3)
            .SpSummon(xyz.Card.Id)
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,100000220)")
            .Check("Duel.GetFieldCard(0,LOCATION_MZONE,0) == nil or Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1")
            .AssertPasses();
    }

    // Negation needs an opponent's activation to respond to, so it has its own duel below.
    public static IEnumerable<object[]> DoBlocks() =>
        BlockRegistry.OfKind(BlockKind.Action).Where(b => b.Id != "do.negate").Select(b => new object[] { b.Id, false })
            .Concat(BlockRegistry.OfKind(BlockKind.Action).Where(b => b.Param("target") is not null).Select(b => new object[] { b.Id, true }));

    /// <summary>Every DO block, with its default settings, activated from a monster on a well-stocked board.</summary>
    [EngineTheory]
    [MemberData(nameof(DoBlocks))]
    public void EveryActionBlock_ResolvesInTheEngine(string blockId, bool targeting)
    {
        var def = BlockRegistry.Find(blockId)!;
        var inst = def.CreateInstance();
        if (targeting)
        {
            inst.With("target", true);
            // Hidden zones can't be targeted, so aim at the GY instead.
            var f = inst.Filters["cards"];
            if ((f.Zones & (Zone.Deck | Zone.Hand | Zone.ExtraDeck)) != Zone.None) f.Zones = Zone.Graveyard;
        }
        var fromHand = blockId == "do.special_summon_self";
        var card = new CardProject
        {
            Card = new CardData { Id = 100000300, Name = "Tester", Level = 4, Atk = 1000, Def = 1000 },
            Effects = [new EffectEvent { When = new BlockInstance("when.ignition").With("zone", fromHand ? "hand" : "monster_zone"), Do = [inst] }],
        };
        new EngineScenario(card)
            .Card(card.Card.Id, 0, fromHand ? "hand" : "mzone")
            .Card(Warrior, 0, "deck", 6).Card(Trap, 0, "deck", 2).Card(Warrior, 0, "grave", 2).Card(Trap, 0, "grave", 1).Card(Spell, 0, "hand", 1)
            .Card(Fiend, 1, "mzone", 1).Card(EffectFiller, 1, "mzone", 1).Card(Warrior, 1, "deck", 3).Card(Warrior, 1, "grave", 2)
            .Activate(card.Card.Id)
            .ExpectActivations(1)
            .AssertPasses();
    }

    public static IEnumerable<object[]> CostBlocks() =>
        // Detaching needs an Xyz monster with materials; Xyz_DetachMaterialAsCost covers it.
        BlockRegistry.OfKind(BlockKind.Cost).Where(b => b.Id != "cost.detach").SelectMany(def =>
            new[] { def.CreateInstance() }
                .Concat(def.Params.Where(p => p.Type == ParamType.Choice).SelectMany(p => p.Choices.Skip(1).Select(c => def.CreateInstance().With(p.Key, c.Value))))
                .Select(inst => new object[] { def.Id, string.Join(",", inst.Values.Select(kv => $"{kv.Key}={kv.Value}")) }));

    [EngineTheory]
    [MemberData(nameof(CostBlocks))]
    public void EveryCostBlock_IsPaidInTheEngine(string blockId, string values)
    {
        var inst = BlockRegistry.Find(blockId)!.CreateInstance();
        foreach (var kv in values.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('='))) inst.With(kv[0], kv[1]);
        if (blockId == "cost.move_cards" && inst.Values["how"] == "grave") inst.Filters["cards"].Zones = Zone.Hand;
        var discardSelf = inst.Values.GetValueOrDefault("how") == "discard";
        var card = new CardProject
        {
            Card = new CardData { Id = 100000400, Name = "Payer", Level = 4, Atk = 1000 },
            Effects =
            [
                new EffectEvent
                {
                    When = new BlockInstance("when.ignition").With("zone", discardSelf ? "hand" : "monster_zone"),
                    Cost = [inst],
                    Do = [new BlockInstance("do.gain_lp").With("amount", 100)],
                },
            ],
        };
        new EngineScenario(card)
            .Card(card.Card.Id, 0, discardSelf ? "hand" : "mzone").Card(Warrior, 0, "mzone")
            .Card(Warrior, 0, "hand", 2).Card(Warrior, 0, "grave", 2).Card(Warrior, 0, "deck", 5)
            .Activate(card.Card.Id)
            .ExpectActivations(1)
            .Check("Duel.GetLP(0)>=7100")
            .AssertPasses();
    }

    public static IEnumerable<object[]> PassiveBlocks() =>
        BlockRegistry.OfKind(BlockKind.Passive).SelectMany(def =>
            new[] { def.CreateInstance() }
                .Concat(def.Params.Where(p => p.Type == ParamType.Choice).SelectMany(p => p.Choices.Skip(1).Select(c => def.CreateInstance().With(p.Key, c.Value))))
                .Select(inst => new object[] { def.Id, string.Join(",", inst.Values.Select(kv => $"{kv.Key}={kv.Value}")) }));

    [EngineTheory]
    [MemberData(nameof(PassiveBlocks))]
    public void EveryLastingBlock_RegistersInTheEngine(string blockId, string values)
    {
        var inst = BlockRegistry.Find(blockId)!.CreateInstance();
        foreach (var kv in values.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('='))) inst.With(kv[0], kv[1]);
        var card = new CardProject
        {
            Card = new CardData { Id = 100000500, Name = "Aura", Level = 4, Atk = 1000, Def = 1000 },
            Effects = [new EffectEvent { When = new BlockInstance("when.while_face_up"), Do = [inst] }],
        };
        var scenario = new EngineScenario(card).Card(card.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 3);
        if (blockId == "passive.boost_cards" && inst.Values["stat"] != "def")
            scenario.Check(inst.Values["dir"] == "gain" ? "Duel.GetFieldCard(0,LOCATION_MZONE,1):GetAttack()==2000" : "Duel.GetFieldCard(0,LOCATION_MZONE,1):GetAttack()==1000");
        if (blockId == "passive.self_stats" && inst.Values["stat"] != "def")
            scenario.Check(inst.Values["dir"] == "gain" ? "Duel.GetFieldCard(0,LOCATION_MZONE,0):GetAttack()==1500" : "Duel.GetFieldCard(0,LOCATION_MZONE,0):GetAttack()==500");
        if (blockId == "passive.indestructible" && inst.Values["by"] is "effect" or "both")
            scenario.Check("Duel.GetFieldCard(0,LOCATION_MZONE,0):IsHasEffect(EFFECT_INDESTRUCTABLE_EFFECT)~=nil");
        scenario.AssertPasses();
    }
}

/// <summary>Each template, played in the engine on a board where it can be used.</summary>
public class EngineTemplateTests
{
    public static IEnumerable<object[]> Names() => CardCrafter.Core.Templates.EffectTemplates.All.Select(t => new object[] { t.Name });

    [EngineTheory]
    [MemberData(nameof(Names))]
    public void TemplateWorksInTheEngine(string name)
    {
        var t = CardCrafter.Core.Templates.EffectTemplates.All.Single(x => x.Name == name);
        var p = TemplateTests.CardWith(t);
        var id = p.Card.Id;
        var s = new EngineScenario(p).Activate(id, 3)
            .Card(Warrior, 0, "deck", 6).Card(Dragon, 0, "grave", 6).Card(Spell, 0, "deck", 2)
            .Card(Warrior, 1, "deck", 6).Card(Warrior, 1, "grave", 2);
        switch (name)
        {
            case "Search on summon": s.Card(id, 0, "hand").Summon(id).ExpectActivations(1); break;
            case "Floater":
                // Destroyed by battle on the opponent's turn is hard to script; check it registers and loads.
                s.Card(id, 0, "mzone").ExpectActivations(0); break;
            case "Free summon from hand": s.Card(id, 0, "hand").Card(Fiend, 1, "mzone", 2).ExpectActivations(1).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})"); break;
            case "Revive itself from GY": s.Card(id, 0, "grave").ExpectActivations(1).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{id})"); break;
            case "Discard to search": s.Card(id, 0, "hand").ExpectActivations(1).Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,1003)"); break;
            case "Quick banish from GY": s.Card(id, 0, "mzone").ExpectActivations(1).Check("Duel.GetFieldGroupCount(1,LOCATION_REMOVED,0)==1"); break;
            case "Negate an activation":
                // Played for real in Negate_StopsTheOpponentsSpell_AndDestroysIt; here it must load and wait.
                s.Card(id, 0, "hand").ExpectActivations(0); break;
            case "Negate a monster's effects":
                // "Until the end of this turn" has worn off by the time checks run, so check it resolved.
                s.Card(id, 0, "hand").Card(EffectFiller, 1, "mzone").ExpectActivations(1)
                    .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{id})");
                break;
            case "Link support":
                s.Card(Warrior, 0, "mzone").Card(id, 0, "mzone").ExpectActivations(0)
                    .Check("Duel.GetFieldCard(0,LOCATION_MZONE,0):GetAttack()==2000");
                break;
            case "Detach to destroy":
                s.Card(id, 0, "mzone").Card(Fiend, 1, "mzone").ExpectActivations(0); break;
            case "Battle protection": s.Card(id, 0, "mzone").ExpectActivations(0).Check("Duel.GetFieldCard(0,LOCATION_MZONE,0):IsHasEffect(EFFECT_INDESTRUCTABLE_BATTLE)~=nil"); break;
            case "Destroy an attacker":
            case "Banish on summon":
                s.Card(id, 0, "szone").Card(Fiend, 1, "hand", 2).ExpectActivations(0); break;
            case "Type boost field": s.Card(id, 0, "hand").Card(Dragon, 0, "mzone").ExpectActivations(1).Check("Duel.GetFieldCard(0,LOCATION_MZONE,0):GetAttack()==1800"); break;
            case "Standby heal": s.Card(id, 0, "hand").Turns(3).ExpectActivations(2).Check("Duel.GetLP(0)==8500"); break;
            case "Destroy a Spell/Trap": s.Card(id, 0, "hand").Card(Spell, 1, "szone").ExpectActivations(1).Check("Duel.GetFieldGroupCount(1,LOCATION_SZONE,0)==0"); break;
            case "Revive a monster": s.Card(id, 0, "hand").ExpectActivations(1).Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1"); break;
            case "Recycle and draw": s.Card(id, 0, "hand").ExpectActivations(1).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2"); break;
            case "Burn 800": s.Card(id, 0, "hand").ExpectActivations(1).Check("Duel.GetLP(1)==7200"); break;
            default: s.Card(id, 0, "hand").ExpectActivations(1); break;
        }
        s.AssertPasses();
    }
}
