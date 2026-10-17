using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Card pickers and group rules: "1 "Ancient Gear" monster or 1 "Geartown"", "or" groups of requirements, "a Normal or
/// Quick-Play Spell", "whose ATK equals its own DEF", "in this card's column", "cannot be Normal Summoned/Set",
/// "including an Insect monster", "with different Types, Attributes, and Levels", "Link Summoned using ...", "Synchro
/// Summon ..., including a "Yummy" monster", "place this card, and them, on the bottom of the Deck in any order", targets
/// of two kinds with a different action each, and "up to the number of monsters used as material for its Summon".
/// </summary>
public class PickerRoundTests
{
    private const long Id = 100480001, Second = 100480002, Third = 100480003, Gear = 100480010, Town = 100480011, Insect = 100480012, Machine = 100480013;

    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    private static string Text(CardProject p, CardPack? pack = null) => EffectTextGenerator.Generate(p, pack);

    private static string Lua(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Empty(ScriptChecker.Check(r.Lua));
        return r.Lua;
    }

    private static string In(int player, string location, long code, int count = 1) =>
        $"Duel.GetMatchingGroupCount(Card.IsCode,{player},{location},0,nil,{code})=={count}";

    /// <summary>
    /// What holds right after a chain link resolves, kept in a global the check reads: effects that last until the end of
    /// the turn are gone by the time the harness checks.
    /// </summary>
    public static string SeenAfterResolving(string lua) =>
        "local fe=Effect.GlobalEffect() fe:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS) fe:SetCode(EVENT_CHAIN_SOLVED) " +
        $"fe:SetOperation(function() if {lua} then CCSEEN=true end end) Duel.RegisterEffect(fe,0)";

    private static CardProject Monster(long id, string name, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Effect, Race = CardRace.Machine, Attribute = CardAttribute.Earth, Level = 4, Atk = 1800, Def = 1000 },
        Effects = [.. effects],
    };

    private static CardProject Vanilla(long id, string name, CardRace race, CardAttribute attr, int level, int atk, int def, int[]? archetypes = null) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Normal, Race = race, Attribute = attr, Level = level, Atk = atk, Def = def, Archetypes = [.. archetypes ?? []] },
    };

    private static EffectEvent Ignition(params BlockInstance[] dos) => new() { When = B("when.ignition"), Do = [.. dos] };

    private static CardPack Pack(params CardProject[] cards) => new()
    {
        Cards = [.. cards],
        Archetypes = [new Archetype { Code = 0x7, Name = "Ancient Gear" }, new Archetype { Code = 0x7b1, Name = "Yummy" }],
    };

    // ---------------------------------------------------------------- "X or Y"

    /// <summary>Ancient Gear Ballista: "add 1 "Ancient Gear" monster or 1 "Geartown" from your Deck to your hand".</summary>
    private static CardProject Ballista() => Monster(Id, "Gear Ballista", Ignition(
        B("do.add_to_hand").With("cards", new CardFilter
        {
            Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Archetype = 0x7,
            OrCards = new CardFilter { CardCode = Town },
        })));

    private static CardProject GearTown() => new() { Card = new CardData { Id = Town, Name = "Geartown", Kind = CardKind.Spell, SpellKind = SpellKind.Field } };

    private static CardProject GearMonster() => Vanilla(Gear, "Ancient Gear Golem", CardRace.Machine, CardAttribute.Earth, 4, 1000, 1000, [0x7]);

    [Fact]
    public void OrAnotherKind_ReadsAndCompiles()
    {
        var pack = Pack(Ballista(), GearTown(), GearMonster());
        Assert.Equal("You can add 1 \"Ancient Gear\" monster or 1 \"Geartown\" from your Deck to your hand.", Text(Ballista(), pack));
        Assert.Contains("((c:IsType(TYPE_MONSTER) and c:IsSetCard(0x7)) or (c:IsCode(" + Town + ")))", Lua(Ballista()));
    }

    /// <summary>Flying Launcher: "add 1 EARTH Machine monster or 1 "Barrage Blast" from your Deck to your hand".</summary>
    [Fact]
    public void OrAnotherKind_WithTypeAndAttribute()
    {
        var f = new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Machine, Attribute = CardAttribute.Earth, OrCards = new CardFilter { CardCode = Town } };
        Assert.Equal("1 EARTH Machine monster or 1 \"Geartown\" from your Deck", "1 " + FilterText.Describe(f, Pack(GearTown())));
    }

    [EngineFact]
    public void OrAnotherKind_AddsEitherKind()
    {
        // Only the Field Spell can be added: the Deck has no "Ancient Gear" monster.
        new EngineScenario(Ballista(), GearTown(), GearMonster()).Card(Id, 0, "mzone").Card(Town, 0, "deck").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_HAND", Town)).AssertPasses();
        new EngineScenario(Ballista(), GearTown(), GearMonster()).Card(Id, 0, "mzone").Card(Gear, 0, "deck").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_HAND", Gear)).AssertPasses();
        // Neither: a Warrior isn't either kind.
        new EngineScenario(Ballista(), GearTown(), GearMonster()).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(0).AssertPasses();
    }

    // ---------------------------------------------------------------- "or" groups, Spell kinds, states

    [Fact]
    public void OrGroups_ReadAndCompile()
    {
        // "a Level 4 or lower monster or a Tuner, that is DARK".
        var f = new CardFilter
        {
            Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster,
            Requirements =
            [
                new CardRequirement { Property = "level", Op = RequirementOp.LessOrEqual, Value = 4, Group = 1 },
                new CardRequirement { Property = "monster_type", Op = RequirementOp.Is, Values = [0x1000], Group = 1 },
                new CardRequirement { Property = "attribute", Op = RequirementOp.Is, Values = [(long)CardAttribute.Dark] },
            ],
        };
        Assert.Equal("DARK monster whose Level is 4 or lower or that is a Tuner from your Deck", FilterText.Describe(f, null));
        var conds = FilterCompiler.Conditions(f);
        Assert.Contains("((c:GetLevel()>0 and c:GetLevel()<=4) or (c:IsType(0x1000)))", conds);
        Assert.Contains("(c:IsAttribute(0x20))", conds);
    }

    [Fact]
    public void TwoSpellKinds_ReadAndCompile()
    {
        var f = new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Spell, SpellTrapKind = SpellTrapKind.Normal, SpellTrapKind2 = SpellTrapKind.QuickPlay };
        Assert.Equal("Normal or Quick-Play Spell from your Deck", FilterText.Describe(f, null));
        Assert.Contains("((c:GetType()&(TYPE_CONTINUOUS+TYPE_QUICKPLAY+TYPE_FIELD+TYPE_EQUIP+TYPE_RITUAL+TYPE_COUNTER+TYPE_MONSTER)==0) or (c:IsType(TYPE_QUICKPLAY)))", FilterCompiler.Conditions(f));
    }

    /// <summary>Gizmek Taniguku: "take 1 Machine monster whose ATK equals its own DEF from your Deck and place it on top of your Deck".</summary>
    private static CardProject Taniguku() => Monster(Id, "Gizmek Tester", Ignition(
        B(PlaceOnDeckAction.BlockId).With("cards", new CardFilter
        {
            Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Machine,
            Requirements = [new CardRequirement { Property = "atk_eq_def", Op = RequirementOp.Is }],
        })));

    [Fact]
    public void AtkEqualsDef_Reads() =>
        Assert.Equal("You can take 1 Machine monster whose ATK equals its own DEF from your Deck and place it on top of your Deck.", Text(Taniguku()));

    [EngineFact]
    public void AtkEqualsDef_TakesOnlyTheMachineWithEqualStats()
    {
        var even = Vanilla(Machine, "Even Machine", CardRace.Machine, CardAttribute.Earth, 4, 1200, 1200);
        new EngineScenario(Taniguku(), even).Card(Id, 0, "mzone").Card(Machine, 0, "deck").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(1).Check($"Duel.GetDecktopGroup(0,1):GetFirst():IsCode({Machine})").AssertPasses();
        // A Machine whose ATK isn't its DEF isn't one.
        var odd = Vanilla(Machine, "Odd Machine", CardRace.Machine, CardAttribute.Earth, 4, 1200, 1000);
        new EngineScenario(Taniguku(), odd).Card(Id, 0, "mzone").Card(Machine, 0, "deck").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(0).AssertPasses();
    }

    /// <summary>Javelin Beetle of the Alliance: "target 1 other card in this card's column; destroy it".</summary>
    private static CardProject Javelin() => Monster(Id, "Column Beetle", Ignition(
        B("do.destroy").With("target", true).With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field, ExcludeThisCard = true, InThisColumn = true })));

    [Fact]
    public void ThisCardsColumn_Reads()
    {
        Assert.Equal("You can target 1 other card in this card's column; destroy it.", Text(Javelin()));
        Assert.Contains("e:GetHandler():GetColumnGroup():IsContains(c)", Lua(Javelin()));
    }

    [EngineFact]
    public void ThisCardsColumn_DestroysOnlyTheCardInIt()
    {
        // This card in your first zone, the opponent's Fiend facing it (their last zone); their Dragon is elsewhere.
        new EngineScenario(Javelin()).Card(Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Setup("Duel.MoveSequence(Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,1004),4)")
            .Activate(Id).ExpectActivations(1).Check(In(1, "LOCATION_GRAVE", Fiend)).Check(In(1, "LOCATION_MZONE", Dragon)).AssertPasses();
    }

    /// <summary>Heretic Eradicator: "your opponent has 4 or more monsters that cannot be Normal Summoned/Set in their ... GY".</summary>
    [Fact]
    public void CannotBeNormalSummoned_Reads()
    {
        var b = B("if.cards_exist").With("count", 2).With("cards", new CardFilter
        {
            Owner = CardOwner.Opponent, Zones = Zone.Graveyard | Zone.Banished, CardType = FilterCardType.Monster,
            Requirements = [new CardRequirement { Property = "not_summonable", Op = RequirementOp.Is }],
        });
        var p = Monster(Id, "Eradicator Tester", new EffectEvent { When = B("when.ignition"), If = [b], Do = [B("do.draw")] });
        Assert.Contains("monsters that cannot be Normal Summoned/Set in your opponent's GY or banished cards", Text(p));
        Assert.Contains("not c:IsSummonableCard()", Lua(p));
    }

    // ---------------------------------------------------------------- group rules

    /// <summary>Reindsurm Butterfly: "Send 2 monsters from your Deck to the GY, including an Insect monster".</summary>
    private static CardProject Butterfly() => new()
    {
        Card = new CardData { Id = Id, Name = "Butterfly Tester", Kind = CardKind.Spell },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do = [B("do.send_to_gy").With("count", 2).With("including", true).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster })
                    .With("inc_cards", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Insect })],
            },
        ],
    };

    [Fact]
    public void Including_Reads()
    {
        Assert.Equal("Send 2 monsters from your Deck to the GY, including an Insect monster.", Text(Butterfly()));
        Assert.Contains("g:IsExists(", Lua(Butterfly()));
    }

    [EngineFact]
    public void Including_NeedsOneOfThem()
    {
        var insect = Vanilla(Insect, "Matrix Insect", CardRace.Insect, CardAttribute.Wind, 3, 1000, 1000);
        new EngineScenario(Butterfly(), insect).Card(Id, 0, "hand").Card(Warrior, 0, "deck", 3).Card(Insect, 0, "deck").Card(Warrior, 0, "deck", 2)
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_GRAVE", Insect)).Check(In(0, "LOCATION_GRAVE", Warrior)).AssertPasses();
        // No Insect in the Deck: it can't be activated.
        new EngineScenario(Butterfly(), insect).Card(Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Activate(Id).ExpectActivations(0).AssertPasses();
    }

    /// <summary>Bunnillin Soft: "target 2 Normal Monsters in your GY with different Types, Attributes, and Levels; Special Summon them".</summary>
    private static CardProject Bunnillin() => Monster(Id, "Bunny Tester", Ignition(
        B("do.special_summon").With("count", 2).With("target", true).With("pick", "types").With("pick2", "attributes").With("pick3", "levels")
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Normal })));

    [Fact]
    public void SeveralGroupRules_Read()
    {
        Assert.Equal("You can target 2 Normal Monsters with different Types, Attributes, and Levels from your GY; Special Summon them.", Text(Bunnillin()));
        var lua = Lua(Bunnillin());
        Assert.Contains("aux.drccheck(g) and aux.dabcheck(g) and aux.dlvcheck(g)", lua);
    }

    [EngineFact]
    public void SeveralGroupRules_NeedAllDifferent()
    {
        // The Warrior (EARTH, Level 4) and the Dragon (LIGHT, Level 4) share a Level: no pair fits.
        new EngineScenario(Bunnillin()).Card(Id, 0, "mzone").Card(Warrior, 0, "grave").Card(Dragon, 0, "grave").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(0).AssertPasses();
        var low = Vanilla(Insect, "Low Insect", CardRace.Insect, CardAttribute.Wind, 3, 1000, 1000);
        new EngineScenario(Bunnillin(), low).Card(Id, 0, "mzone").Card(Warrior, 0, "grave").Card(Insect, 0, "grave").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_MZONE", Insect)).Check(In(0, "LOCATION_MZONE", Warrior)).AssertPasses();
    }

    /// <summary>S:P Little Knight: "target 2 face-up monsters on the field, including a monster you control; banish them until the End Phase".</summary>
    private static CardProject LittleKnight() => Monster(Id, "Knight Tester", Ignition(
        B("do.banish").With("count", 2).With("target", true).With("including", true).With("until", "end_phase")
            .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true })
            .With("inc_cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })));

    [Fact]
    public void IncludingAMonsterYouControl_Reads() =>
        Assert.StartsWith("You can target 2 face-up monsters on the field, including a monster you control; banish them", Text(LittleKnight()));

    [EngineFact]
    public void IncludingAMonsterYouControl_TakesOneOfYours()
    {
        // Two of the opponent's and none of yours but this card: this card and 1 of theirs.
        new EngineScenario(LittleKnight()).Card(Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Setup(SeenAfterResolving("Duel.GetFieldGroupCount(0,LOCATION_REMOVED,0)==1 and Duel.GetFieldGroupCount(1,LOCATION_REMOVED,0)==1"))
            .Activate(Id).ExpectActivations(1).Check("CCSEEN").Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1").AssertPasses();
    }

    // ---------------------------------------------------------------- Summoned using

    /// <summary>Marincess Truth Heart: "If this card is Link Summoned using a Link Monster with 0 original ATK".</summary>
    [Fact]
    public void SummonedUsingAPickedMaterial_Reads()
    {
        var used = B(SummonedUsingCondition.BlockId).With("kind", "picked").With("mat_cards", new CardFilter
        {
            CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Link,
            Requirements = [new CardRequirement { Property = "base_atk", Op = RequirementOp.Equal, Value = 0 }],
        });
        var p = new CardProject
        {
            Card = new CardData { Id = Id, Name = "Heart Tester", Frame = MonsterFrame.Link, Race = CardRace.Aqua, Attribute = CardAttribute.Water, Atk = 2300, LinkMarkers = LinkMarker.Left | LinkMarker.Right | LinkMarker.Bottom },
            Effects = [new EffectEvent { When = B(SummonedTrigger.BlockId).With("how", "link"), If = [used], Do = [B("do.draw")] }],
        };
        Assert.StartsWith("If this card is Link Summoned using a Link Monster with 0 original ATK as material: You can draw 1 card.", Text(p));
        Assert.Contains("e:GetHandler():GetMaterial():FilterCount(", Lua(p));
    }

    /// <summary>King of Beasts, Risen from the Valley of Flames: "if this card was Link Summoned using "Salamangreat Heatleo"".</summary>
    [Fact]
    public void LinkSummonedUsingANamedMonster_Reads()
    {
        var heatleo = Vanilla(Second, "Salamangreat Heatleo", CardRace.Beast, CardAttribute.Fire, 4, 2400, 0);
        var used = B(SummonedUsingCondition.BlockId).With("kind", "picked").With("mat_cards", new CardFilter { CardType = FilterCardType.Monster, CardCode = Second });
        var p = Monster(Id, "Beast King Tester", new EffectEvent { When = B("when.quick"), If = [B("if.summoned_by").With("how", "link"), used], Do = [B("do.draw")] });
        Assert.Contains("this card was Link Summoned using \"Salamangreat Heatleo\" as material", Text(p, Pack(heatleo)));
        Lua(p);
    }

    // ---------------------------------------------------------------- Summons by effect, the Deck, targets of two kinds

    /// <summary>Yummy★Snatchy: "Synchro Summon ... using monsters you control as material, including a "Yummy" monster".</summary>
    [Fact]
    public void SynchroIncluding_Reads()
    {
        var p = Monster(Id, "Snatchy Tester", Ignition(B(ExtraDeckSummonAction.BlockId).With("using", "including")
            .With("mats", new CardFilter { CardType = FilterCardType.Monster, Archetype = 0x7b1 })));
        Assert.Equal("You can Synchro Summon 1 Synchro Monster using monsters you control as material, including a \"Yummy\" monster.", Text(p, Pack()));
        var lua = Lua(p);
        Assert.Contains("aux.SynGroupCheckLevelAddition", lua);
        Assert.Contains("Duel.SynchroSummon(tp,", lua);
    }

    [EngineFact]
    public void XyzIncluding_SummonsWithTheIncludedMonster()
    {
        var xyz = new CardProject
        {
            Card = new CardData { Id = Third, Name = "Rank 4 Tester", Frame = MonsterFrame.Xyz, Race = CardRace.Warrior, Attribute = CardAttribute.Earth, Level = 4, Atk = 2000, Def = 2000 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2 },
        };
        var p = Monster(Id, "Including Tester", Ignition(B(ExtraDeckSummonAction.BlockId).With("kind", "xyz").With("using", "including")
            .With("mats", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Machine })));
        // This Machine and a Warrior: the Xyz Monster comes, with this card under it.
        new EngineScenario(p, xyz).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Third, 0, "extra").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_MZONE", Third)).AssertPasses();
        // Two Warriors and no Machine of the kind: nothing is Summoned.
        var other = Monster(Id, "Including Tester", Ignition(B(ExtraDeckSummonAction.BlockId).With("kind", "xyz").With("using", "including")
            .With("mats", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Dragon })));
        new EngineScenario(other, xyz).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Third, 0, "extra").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(0).AssertPasses();
    }

    /// <summary>Yummyusment☆Mignon: "target 2 ... in your GY and/or banishment; place this card, and them, on the bottom of the Deck in any order".</summary>
    private static CardProject Mignon() => new()
    {
        Card = new CardData { Id = Id, Name = "Mignon Tester", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.ignition").With("zone", "graveyard"),
                Do = [B(PlaceOnDeckAction.BlockId).With("count", 2).With("target", true).With("where", "bottom").With("plus", "this")
                    .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard | Zone.Banished, CardType = FilterCardType.Monster })],
            },
        ],
    };

    [Fact]
    public void ThisCardAndThemOnTheBottom_Reads()
    {
        Assert.Equal("If this card is in your GY: You can target 2 monsters from your GY or banished cards; place this card, and them, on the bottom of the Deck in any order.", Text(Mignon()));
        Assert.Contains("aux.PlaceCardsOnDeckBottom(tp,", Lua(Mignon()));
    }

    [EngineFact]
    public void ThisCardAndThemOnTheBottom_GoesToTheDeck()
    {
        new EngineScenario(Mignon()).Card(Id, 0, "grave").Card(Warrior, 0, "grave", 2).Card(Fiend, 0, "deck", 4)
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_DECK", Warrior, 2)).Check(In(0, "LOCATION_DECK", Id))
            .Check($"Duel.GetFieldCard(0,LOCATION_DECK,0):IsCode({Warrior},{Id})").AssertPasses();
    }

    /// <summary>Ancient Gear Ballista: "target 1 Spell/Trap you control and 1 face-up monster your opponent controls; destroy that card you control, and if you do, ...".</summary>
    private static CardProject BallistaPair() => Monster(Id, "Pair Tester", Ignition(
        B(PairTargetAction.BlockId).With("action2", "atk0")
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Field, CardType = FilterCardType.SpellTrap })
            .With("cards2", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true })));

    [Fact]
    public void PairWithASecondAction_Reads() =>
        Assert.Equal("You can target 1 Spell/Trap you control and 1 face-up monster your opponent controls; destroy that card you control, and if you do, " +
                     "change that opponent's monster's ATK/DEF to 0 until the end of the turn.", Text(BallistaPair()));

    [EngineFact]
    public void PairWithASecondAction_DestroysOneAndZeroesTheOther()
    {
        new EngineScenario(BallistaPair()).Card(Id, 0, "mzone").Card(Spell, 0, "szone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Setup(SeenAfterResolving("Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,1002):GetAttack()==0"))
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_GRAVE", Spell)).Check(In(1, "LOCATION_MZONE", Dragon)).Check("CCSEEN").AssertPasses();
    }

    /// <summary>Qliphort Genius: "target 1 face-up card on each player's field, except this card; negate their effects until the end of this turn".</summary>
    private static CardProject Genius() => Monster(Id, "Genius Tester", Ignition(
        B(PairTargetAction.BlockId).With("action", "negate")
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Field, FaceUpOnly = true, ExcludeThisCard = true })
            .With("cards2", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Field, FaceUpOnly = true })));

    [Fact]
    public void PairNegate_ReadsOnEachPlayersField()
    {
        Assert.Equal("You can target 1 face-up card on each player's field, except this card; negate their effects until the end of this turn.", Text(Genius()));
        Assert.Contains("EFFECT_DISABLE_EFFECT", Lua(Genius()));
    }

    [EngineFact]
    public void PairNegate_NegatesBoth()
    {
        new EngineScenario(Genius()).Card(Id, 0, "mzone").Card(EffectFiller, 0, "mzone").Card(EffectFiller, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Setup(SeenAfterResolving("Duel.GetMatchingGroupCount(Card.IsDisabled,0,LOCATION_MZONE,LOCATION_MZONE,nil)==2"))
            .Activate(Id).ExpectActivations(1).Check("CCSEEN").AssertPasses();
    }

    /// <summary>Hyperinvoked Aeon: "banish cards from the field and/or GYs, up to the number of monsters used as material for its Summon, then if 3 or more ...".</summary>
    [Fact]
    public void UpToTheMaterials_Reads()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = Id, Name = "Aeon Tester", Frame = MonsterFrame.Fusion, Race = CardRace.Fiend, Attribute = CardAttribute.Dark, Level = 10, Atk = 3500, Def = 3000 },
            Effects =
            [
                new EffectEvent
                {
                    When = B(SummonedTrigger.BlockId).With("how", "fusion"),
                    Do =
                    [
                        B("do.banish").With("amount", "materials").With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field | Zone.Graveyard }),
                        B(SummonedUsingCondition.BlockId).With("count", 3).With("kind", "monster").Linked(ActionLink.Then),
                        B("do.banish").With("count", 3).With("mode", "up_to").With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.ExtraDeck }).MayDo(),
                    ],
                },
            ],
        };
        var text = Text(p);
        Assert.Contains("up to the number of monsters used as material for its Summon", text);
        Assert.Contains("if this card was Summoned using 3 or more monsters as material", text);
        Assert.Contains("e:GetHandler():GetMaterialCount()", Lua(p));
    }
}
