using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Names for a while ("its name becomes "Buster Blader""), names as Fusion Material, Called by the Grave, declaring a name
/// of the cards there are, looking at random face-down cards; Rituals that shuffle materials from the GY into the Deck,
/// several Ritual Monsters at once, a replacement from the GY; and the small pieces: "double this card's ATK", the
/// attacking monster's Rank, gaining control of the monster whose activation was negated, the Secret Crane Princess,
/// Salamangreat Shrine, "not with the same Link Rating again this turn", Kashtira Birth and Gandora-X.
/// </summary>
public class NameRitualRoundTests
{
    private const long Id = 100490001, Second = 100490002, Third = 100490003, FusionId = 100490010, LinkId = 100490011, RitualId = 100490012, RitualId2 = 100490013,
        XyzId = 100490014, BusterBlader = 78193831, Aleister = 86120751;

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

    private static string Seen(string lua) => PickerRoundTests.SeenAfterResolving(lua);

    private static CardProject Monster(long id, string name, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Effect, Race = CardRace.Warrior, Attribute = CardAttribute.Light, Level = 4, Atk = 1800, Def = 1000 },
        Effects = [.. effects],
    };

    private static EffectEvent Ignition(params BlockInstance[] dos) => new() { When = B("when.ignition"), Do = [.. dos] };

    private static EngineScenario Duel(params CardProject[] cards) => new EngineScenario(cards).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5);

    // ---------------------------------------------------------------- names

    /// <summary>Buster Blader, the One Who Surpasses the Limits: "Special Summon this card, and if you do, its name becomes "Buster Blader"".</summary>
    private static CardProject Surpasses() => Monster(Id, "Surpassing Tester", new EffectEvent
    {
        When = B("when.ignition").With("zone", "hand"),
        Cost = [B("cost.discard").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Spell })],
        Do =
        [
            B("do.special_summon_self").With("banish", true),
            B(NameBecomesAction.BlockId).With("duration", "always").Linked(ActionLink.IfYouDo).With("cards", new CardFilter { CardCode = BusterBlader }),
        ],
    });

    [Fact]
    public void NameBecomes_Reads()
    {
        var pack = new CardPack { Cards = [Surpasses(), new CardProject { Card = new CardData { Id = BusterBlader, Name = "Buster Blader", Frame = MonsterFrame.Normal, Level = 7 } }] };
        Assert.Equal("You can discard 1 Spell from your hand; Special Summon this card from your hand, but banish it when it leaves the field, and if you do, its name becomes \"Buster Blader\".",
            Text(Surpasses(), pack));
        Assert.Contains("EFFECT_CHANGE_CODE", Lua(Surpasses()));
    }

    [EngineFact]
    public void NameBecomes_TakesTheName()
    {
        Duel(Surpasses()).Card(Id, 0, "hand").Card(Spell, 0, "hand").Activate(Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{BusterBlader})").AssertPasses();
    }

    /// <summary>
    /// Necromancer of Dragon Magic: "reveal 1 Fusion Monster in your Extra Deck, choose 1 non-Fusion Monster in your GY whose
    /// name is mentioned on the revealed monster, and this card's name becomes that monster's (until the End Phase), then you
    /// can Fusion Summon the revealed Fusion Monster, by banishing its materials from your hand, field, and/or GY".
    /// </summary>
    private static CardProject Necromancer() => Monster(Id, "Necro Tester", new EffectEvent
    {
        When = B("when.ignition"),
        Cost = [B(RevealCost.BlockId).With("from", "extra").With("cards", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion })],
        Do =
        [
            B(NameBecomesAction.BlockId).With("how", "chosen").With("cards", new CardFilter
            {
                Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster,
                Requirements = [new CardRequirement { Property = "monster_type", Op = RequirementOp.IsNot, Values = [0x40] }, new CardRequirement { Property = "ref_mentioned", Op = RequirementOp.Is, Values = [(long)CardRef.Cost] }],
            }),
            B(FusionSummonAction.BlockId).With("mats", "hand_field_gy_banish").With("cards", new CardFilter { That = CardRef.Cost, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion })
                .Linked(ActionLink.Then).MayDo(),
        ],
    });

    private static CardProject NecroFusion() => new()
    {
        Card = new CardData { Id = FusionId, Name = "Necro Fusion", Frame = MonsterFrame.Fusion, Race = CardRace.Dragon, Attribute = CardAttribute.Dark, Level = 8, Atk = 2800, Def = 2000 },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [Dragon, Warrior] },
    };

    [Fact]
    public void ChooseAndTakeItsName_Reads()
    {
        var text = Text(Necromancer());
        Assert.Contains("You can reveal 1 Fusion Monster in your Extra Deck; choose 1 non-Fusion monster in your GY whose name is mentioned on that monster, and this card's name becomes that monster's (until the End Phase), then you can Fusion Summon the revealed Fusion Monster", text);
        var lua = Lua(Necromancer());
        Assert.Contains("aux.IsMaterialListCode(rc,c:GetCode())", lua);
        Assert.Contains("e:SetLabelObject(", lua);
    }

    [EngineFact]
    public void ChooseAndTakeItsName_ThenFusionSummonsTheRevealedMonster()
    {
        // The Dragon in the GY is named on the Fusion Monster: this card becomes "Filler Dragon", then it and the Warrior in
        // the hand are banished to Fusion Summon it.
        Duel(Necromancer(), NecroFusion()).Card(Id, 0, "mzone").Card(FusionId, 0, "extra").Card(Dragon, 0, "grave").Card(Warrior, 0, "hand")
            .Setup(Seen($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Dragon})"))
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_MZONE", FusionId)).AssertPasses();
    }

    /// <summary>Crowley, the Gifted Magistus: "If this face-up card is used as Fusion Material, its name can be treated as "Aleister the Invoker"".</summary>
    [Fact]
    public void FusionCode_Reads()
    {
        var p = Monster(Id, "Crowley Tester", new EffectEvent { When = B("when.while_face_up"), Do = [B(MaterialTreatedAsPassive.BlockId).With("kind", "fusion_code")] });
        var pack = new CardPack { Cards = [p, new CardProject { Card = new CardData { Id = Aleister, Name = "Aleister the Invoker", Frame = MonsterFrame.Effect, Level = 4 } }] };
        Assert.Equal("If this face-up card is used as Fusion Material, its name can be treated as \"Aleister the Invoker\".", Text(p, pack));
        var lua = Lua(p);
        Assert.Contains("EFFECT_ADD_FUSION_CODE", lua);
        Assert.Contains($"SetValue({Aleister})", lua);
    }

    /// <summary>Called by the Grave: "Target 1 monster in your opponent's GY; banish it, and if you do, until the end of the next turn, its effects are negated, as well as ...".</summary>
    private static CardProject CalledByTheGrave() => new()
    {
        Card = new CardData { Id = Id, Name = "Called Tester", Kind = CardKind.Spell, SpellKind = SpellKind.QuickPlay },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do =
                [
                    B("do.banish").With("target", true).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Graveyard, CardType = FilterCardType.Monster }),
                    B(NegateSameNameAction.BlockId).Linked(ActionLink.IfYouDo),
                ],
            },
        ],
    };

    [Fact]
    public void CalledByTheGrave_Reads() =>
        Assert.Equal("Target 1 monster from your opponent's GY; banish it, and if you do, until the end of the next turn, its effects are negated, " +
                     "as well as the activated effects and effects on the field of monsters with the same original name.", Text(CalledByTheGrave()));

    [EngineFact]
    public void CalledByTheGrave_NegatesTheSameNameOnTheField()
    {
        Duel(CalledByTheGrave()).Card(Id, 0, "hand").Card(EffectFiller, 1, "grave").Card(EffectFiller, 1, "mzone")
            .Activate(Id).ExpectActivations(1).Check(In(1, "LOCATION_REMOVED", EffectFiller))
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{EffectFiller}):IsDisabled()").AssertPasses();
    }

    /// <summary>Ars Magna Academic Arcane Apparition Acropolis: "declare 1 Link Monster Card name that has not been declared ... this turn; Special Summon that monster from your Extra Deck, but it cannot be used as Link Material".</summary>
    private static CardProject Acropolis() => Monster(Id, "Acropolis Tester", new EffectEvent
    {
        When = B("when.ignition"),
        Do =
        [
            B(DeclareAction.BlockId).With("limited", true).With("fresh", true)
                .With("names", new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Link }),
            B("do.special_summon").With("no_link", true).With("cards", new CardFilter
            {
                Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, Requirements = [new CardRequirement { Property = "declared_name", Op = RequirementOp.Is }],
            }),
        ],
    });

    private static CardProject LinkMonster(long id = LinkId) => new()
    {
        Card = new CardData { Id = id, Name = $"Link {id}", Frame = MonsterFrame.Link, Race = CardRace.Cyberse, Attribute = CardAttribute.Dark, Atk = 1000, LinkMarkers = LinkMarker.Bottom | LinkMarker.Top },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 2, MaxMaterials = 2 },
    };

    [Fact]
    public void DeclareOfTheCardsThereAre_Reads()
    {
        Assert.Equal("You can declare 1 Link Monster Card name that has not been declared for \"Acropolis Tester\" this turn; Special Summon 1 monster with the declared name from your Extra Deck, but it cannot be used as Link Material.",
            Text(Acropolis()));
        var lua = Lua(Acropolis());
        Assert.Contains("OPCODE_ISCODE", lua);
        Assert.Contains("EFFECT_CANNOT_BE_LINK_MATERIAL", lua);
    }

    [EngineFact]
    public void DeclareOfTheCardsThereAre_SummonsItOnceATurn()
    {
        // Two copies of this card: the second can't declare the same name again this turn.
        Duel(Acropolis(), LinkMonster()).Card(Id, 0, "mzone", 2).Card(LinkId, 0, "extra", 2).Announce(LinkId)
            .Activate(Id, 2).ExpectActivations(1)
            .Check(In(0, "LOCATION_MZONE", LinkId)).Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{LinkId}):IsHasEffect(EFFECT_CANNOT_BE_LINK_MATERIAL)").AssertPasses();
    }

    /// <summary>Sacred Spirit Sword Aiwass: "Look at 3 random face-down cards in your opponent's Extra Deck, and if you do, banish 1 of them".</summary>
    private static CardProject Aiwass() => new()
    {
        Card = new CardData { Id = Id, Name = "Aiwass Tester", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [B(LookRandomAction.BlockId)] }],
    };

    [Fact]
    public void LookAtRandomCards_Reads() =>
        Assert.Equal("Look at 3 random face-down cards in your opponent's Extra Deck, and if you do, banish 1 of them.", Text(Aiwass()));

    [EngineFact]
    public void LookAtRandomCards_BanishesOne()
    {
        Duel(Aiwass(), NecroFusion()).Card(Id, 0, "hand").Card(FusionId, 1, "extra", 4).Activate(Id).ExpectActivations(1)
            .Check(In(1, "LOCATION_REMOVED", FusionId)).Check(In(1, "LOCATION_EXTRA", FusionId, 3)).AssertPasses();
        // Only 2 cards there: it can't be activated.
        Duel(Aiwass(), NecroFusion()).Card(Id, 0, "hand").Card(FusionId, 1, "extra", 2).Activate(Id).ExpectActivations(0).AssertPasses();
    }

    // ---------------------------------------------------------------- Rituals

    private static CardProject Ritual(long id, int level = 4) => new()
    {
        Card = new CardData { Id = id, Name = $"Ritual Insect {id}", Frame = MonsterFrame.Ritual, Race = CardRace.Insect, Attribute = CardAttribute.Wind, Level = level, Atk = 2000, Def = 2000 },
    };

    private static CardFilter InsectOrDragon() => new()
    {
        CardType = FilterCardType.Monster, Requirements = [new CardRequirement { Property = "race", Op = RequirementOp.Is, Values = [(long)CardRace.Insect, (long)CardRace.Dragon] }],
    };

    /// <summary>Reindsurm Amurusa: "Ritual Summon 1 Insect Ritual Monster from your hand, by Tributing monsters from your hand or field, and/or shuffling Insect and/or Dragon monsters from your GY into the Deck, ...".</summary>
    private static CardProject Amurusa() => new()
    {
        Card = new CardData { Id = Id, Name = "Amurusa Tester", Kind = CardKind.Spell, SpellKind = SpellKind.Ritual },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do = [B(RitualSummonAction.BlockId).With("source", "hand_field_gy_deck").With("how", "greater").With("mats2", InsectOrDragon())
                    .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Ritual, Race = CardRace.Insect })],
            },
        ],
    };

    [Fact]
    public void RitualShufflingFromTheGy_Reads()
    {
        Assert.Equal("Ritual Summon 1 Insect Ritual Monster from your hand, by Tributing monsters from your hand or field, and/or shuffling Insect or Dragon monsters from your GY into the Deck, whose total Levels equal or exceed its Level.",
            Text(Amurusa()));
        Assert.Contains("SEQ_DECKSHUFFLE,REASON_EFFECT+REASON_MATERIAL+REASON_RITUAL", Lua(Amurusa()));
    }

    [EngineFact]
    public void RitualShufflingFromTheGy_ShufflesTheDragonIn()
    {
        Duel(Amurusa(), Ritual(RitualId)).Card(Id, 0, "hand").Card(RitualId, 0, "hand").Card(Dragon, 0, "grave").Activate(Id).ExpectActivations(1)
            .Check(In(0, "LOCATION_MZONE", RitualId)).Check(In(0, "LOCATION_DECK", Dragon)).AssertPasses();
    }

    /// <summary>Reindsurm Conquest: "Ritual Summon any number of Insect Ritual Monsters, by shuffling Insect and/or Dragon monsters from the GY(s) into the Deck ...".</summary>
    private static CardProject Conquest() => new()
    {
        Card = new CardData { Id = Id, Name = "Conquest Tester", Kind = CardKind.Spell, SpellKind = SpellKind.Ritual },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do = [B(RitualSummonAction.BlockId).With("source", "gys_deck").With("number", "any").With("how", "greater").With("mats", InsectOrDragon())
                    .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Ritual, Race = CardRace.Insect })],
            },
        ],
    };

    [Fact]
    public void AnyNumberOfRituals_Reads() =>
        Assert.Equal("Ritual Summon any number of Insect Ritual Monsters from your hand, by shuffling Insect or Dragon monsters from the GY(s) into the Deck whose total Levels equal or exceed the total Levels of the Ritual Monsters.",
            Text(Conquest()));

    [EngineFact]
    public void AnyNumberOfRituals_SummonsFromTheGys()
    {
        // The opponent's Dragon pays for the Level 4 Ritual Monster (the harness picks as few as it may).
        Duel(Conquest(), Ritual(RitualId), Ritual(RitualId2)).Card(Id, 0, "hand").Card(RitualId, 0, "hand").Card(RitualId2, 0, "hand").Card(Dragon, 1, "grave")
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_MZONE", RitualId)).Check(In(1, "LOCATION_DECK", Dragon)).AssertPasses();
        // Levels the GYs can't pay: a Level 8 Ritual Monster and only 4 Levels of material.
        Duel(Conquest(), Ritual(RitualId, 8)).Card(Id, 0, "hand").Card(RitualId, 0, "hand").Card(Dragon, 0, "grave").Activate(Id).ExpectActivations(0).AssertPasses();
    }

    /// <summary>Reindsurm Encounter: "If a "Reindsurm" monster(s) you control would be destroyed ..., you can place this card from your GY on the bottom of the Deck instead".</summary>
    private static CardProject Encounter() => new()
    {
        Card = new CardData { Id = Id, Name = "Encounter Tester", Kind = CardKind.Trap },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.while_face_up").With("zone", "graveyard"),
                Do = [B(ReplaceDestroyPassive.BlockId).With("how", "deck_bottom").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Warrior })],
            },
        ],
    };

    private static CardProject Destroyer() => Monster(Second, "Destroyer", Ignition(B("do.destroy").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Warrior, ExcludeThisCard = true })));

    [Fact]
    public void PlaceOnTheBottomInstead_Reads() =>
        Assert.Contains("If a Warrior monster(s) you control would be destroyed by battle or card effect, you can place this card from your GY on the bottom of the Deck instead.", Text(Encounter()));

    [EngineFact]
    public void PlaceOnTheBottomInstead_SavesTheMonster()
    {
        Duel(Encounter(), Destroyer()).Card(Id, 0, "grave").Card(Second, 0, "mzone").Card(Warrior, 0, "mzone").Activate(Second).Activate(Id)
            .Check(In(0, "LOCATION_MZONE", Warrior)).Check(In(0, "LOCATION_DECK", Id)).AssertPasses();
    }

    // ---------------------------------------------------------------- small pieces

    /// <summary>Reindsurm Giriakarma: "double this card's ATK".</summary>
    private static CardProject Giriakarma() => Monster(Id, "Double Tester", Ignition(
        B(StatsFromValueAction.BlockId).With("dir", "becomes").With("how", "double").With("value", "its_atk").With("duration", "always").With("cards", new CardFilter { ThisCard = true })));

    [Fact]
    public void DoubleThisCardsAtk_Reads() => Assert.Equal("You can double this card's ATK.", Text(Giriakarma()));

    [EngineFact]
    public void DoubleThisCardsAtk_Doubles() =>
        Duel(Giriakarma()).Card(Id, 0, "mzone").Activate(Id).ExpectActivations(1).Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetAttack()==3600").AssertPasses();

    /// <summary>Number 39: Utopia Roots: "negate the attack, and if you negated an Xyz Monster's attack, this card gains ATK equal to that monster's Rank x 500".</summary>
    private static CardProject Roots() => Monster(Id, "Roots Tester", new EffectEvent
    {
        When = B("when.quick").With("timing", "attack"),
        Do =
        [
            B("do.negate_attack"),
            B(OnlyIfAction.BlockId).With("is", "some").With("cards", new CardFilter { That = CardRef.Attacker, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz }).Linked(ActionLink.IfYouDo),
            B(StatsFromValueAction.BlockId).With("how", "per").With("amount", 500).With("value", "attacker_rank").With("duration", "always").With("cards", new CardFilter { ThisCard = true }),
        ],
    });

    private static CardProject Rank4() => new()
    {
        Card = new CardData { Id = XyzId, Name = "Rank Four", Frame = MonsterFrame.Xyz, Race = CardRace.Warrior, Attribute = CardAttribute.Earth, Level = 4, Atk = 2000, Def = 2000 },
    };

    [Fact]
    public void AttackersRank_Reads() =>
        Assert.Equal("When an opponent's monster declares an attack (Quick Effect): You can negate the attack, and if you do, if the attacking monster is an Xyz Monster, this card gains ATK equal to the attacking monster's Rank x 500.",
            Text(Roots()));

    [EngineFact]
    public void AttackersRank_GainsItsRank()
    {
        new EngineScenario(Roots(), Rank4()).Card(Id, 0, "mzone").Card(XyzId, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Attack(XyzId).Activate(Id).Turns(2).ExpectActivations(1)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetAttack()==3800").AssertPasses();
    }

    /// <summary>Number F0: Utopic Draco Future: "negate the activation, then, if that monster is on the field, gain control of it".</summary>
    private static CardProject DracoFuture() => Monster(Id, "Draco Tester", new EffectEvent
    {
        When = B("when.quick").With("timing", "chain_monster"),
        Do = [B("do.negate"), B("do.that_card").With("how", "control").Linked(ActionLink.Then)],
    });

    private static CardProject Drawer() => Monster(Second, "Drawer", Ignition(B("do.draw")));

    [Fact]
    public void GainControlOfThatMonster_Reads() =>
        Assert.Equal("When your opponent activates a monster effect (Quick Effect): You can negate the activation, then if that monster is on the field, gain control of it.", Text(DracoFuture()));

    [EngineFact]
    public void GainControlOfThatMonster_TakesIt()
    {
        new EngineScenario(DracoFuture(), Drawer()).Card(Id, 0, "mzone").Card(Second, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(Second).Activate(Id).Turns(2).Check(In(0, "LOCATION_MZONE", Second)).AssertPasses();
    }

    /// <summary>The Secret Crane Princess Who Returns A Favor: "each player draws 1 card, then your opponent can draw 1 card, and if they do, you must keep your hand revealed during this Main Phase".</summary>
    private static CardProject Crane() => Monster(Id, "Crane Tester",
        Ignition(
            B("do.draw").With("who", "each"),
            B("do.draw").With("who", "opponent_may").Linked(ActionLink.Then),
            B(TurnPlayerRuleAction.BlockId).With("rule", "reveal_hand_main").Linked(ActionLink.IfYouDo)),
        new EffectEvent { When = B("when.quick").With("zone", "hand"), If = [B(RevealedCondition.BlockId)], Cost = [B("cost.this_card").With("how", "banish")], Do = [B("do.draw")] });

    [Fact]
    public void CranePrincess_Reads()
    {
        var text = Text(Crane());
        Assert.Contains("You can activate this effect; each player draws 1 card, then your opponent can draw 1 card, and if they do, you must keep your hand revealed during this Main Phase.", text);
        Assert.Contains("this card is revealed in your hand", text);
        var lua = Lua(Crane());
        Assert.Contains("EFFECT_PUBLIC", lua);
        Assert.Contains("e:GetHandler():IsPublic()", lua);
    }

    [EngineFact]
    public void CranePrincess_RevealsTheHand()
    {
        // The opponent draws (the harness says yes): your hand is revealed, so this card's own revealed effect can be used.
        Duel(Crane()).Card(Id, 0, "mzone").Card(Id, 0, "hand").Activate(Id, 2).ExpectActivations(2).AssertPasses();
    }

    /// <summary>Salamangreat Shrine: "If you Link Summon a Link Monster, you can use 1 Link Monster you control with its same name as the entire material".</summary>
    private static CardProject Shrine() => new()
    {
        Card = new CardData { Id = Id, Name = "Shrine Tester", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
        Effects = [new EffectEvent { When = B("when.while_face_up").With("zone", "spell_trap_zone"), Do = [B(SameNameLinkPassive.BlockId)] }],
    };

    [Fact]
    public void SameNameLink_Reads()
    {
        Assert.Equal("If you Link Summon a Link Monster, you can use 1 Link Monster you control with its same name as the entire material.", Text(Shrine()));
        var lua = Lua(Shrine());
        Assert.Contains("EFFECT_TYPE_FIELD+EFFECT_TYPE_GRANT", lua);
        Assert.Contains("c:IsLinkCode(lc:GetCode())", lua);
    }

    [EngineFact]
    public void SameNameLink_SummonsFromItsCopy()
    {
        Duel(Shrine(), LinkMonster()).Card(Id, 0, "hand").Card(LinkId, 0, "mzone").Card(LinkId, 0, "extra").Activate(Id).SpSummon(LinkId)
            .Check(In(0, "LOCATION_MZONE", LinkId)).Check(In(0, "LOCATION_GRAVE", LinkId)).AssertPasses();
    }

    /// <summary>Marincess Truth Heart: "banish 1 Link Monster Card from your field or GY; ..., also for the rest of this turn, you cannot banish monsters with that same Link Rating to activate this effect".</summary>
    private static CardProject TruthHeart() => Monster(Id, "Heart Tester", new EffectEvent
    {
        When = B("when.ignition"),
        Cost = [B("cost.move_cards").With("no_repeat", "link").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Link })],
        Do = [B("do.draw")],
    });

    [Fact]
    public void NotTheSameLinkRatingAgain_Reads() =>
        Assert.Contains("For the rest of this turn, you cannot banish monsters with that same Link Rating to activate this effect of \"Heart Tester\".", Text(TruthHeart()));

    [EngineFact]
    public void NotTheSameLinkRatingAgain_StopsTheSecond()
    {
        // Two Link Monsters of the same Link Rating in the GY: only one activation.
        Duel(TruthHeart(), LinkMonster(), LinkMonster(LinkId + 100)).Card(Id, 0, "mzone").Card(LinkId, 0, "grave").Card(LinkId + 100, 0, "grave")
            .Activate(Id, 2).ExpectActivations(1).AssertPasses();
    }

    /// <summary>Kashtira Birth: "You can Normal Summon Level 7 monsters without Tributing".</summary>
    private static CardProject Birth() => new()
    {
        Card = new CardData { Id = Id, Name = "Birth Tester", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
        Effects = [new EffectEvent { When = B("when.while_face_up").With("zone", "spell_trap_zone"), Do = [B(NoTributeSummonPassive.BlockId).With("whose", "picked")] }],
    };

    private static CardProject Seven() => new()
    {
        Card = new CardData { Id = Third, Name = "Level Seven", Frame = MonsterFrame.Normal, Race = CardRace.Fiend, Attribute = CardAttribute.Dark, Level = 7, Atk = 2500, Def = 2000 },
    };

    [Fact]
    public void NoTributeForPickedMonsters_Reads() => Assert.Equal("You can Normal Summon Level 7 monsters without Tributing.", Text(Birth()));

    [EngineFact]
    public void NoTributeForPickedMonsters_SummonsTheSeven()
    {
        Duel(Birth(), Seven()).Card(Id, 0, "hand").Card(Third, 0, "hand").Activate(Id).Summon(Third).Check(In(0, "LOCATION_MZONE", Third)).AssertPasses();
    }

    /// <summary>
    /// Gandora-X the Blackmetal Rampaging Dragon: "destroy as many other monsters on the field as possible, and if you do,
    /// inflict damage to your opponent equal to the highest ATK on the field among the destroyed face-up monsters they own,
    /// and if you do that, this card gains that much ATK".
    /// </summary>
    private static CardProject Gandora() => Monster(Id, "Gandora Tester", Ignition(
        B("do.destroy").With("mode", "all").With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true }),
        B(LifeFromValueAction.BlockId).With("value", "that_max_atk_opp").Linked(ActionLink.IfYouDo),
        B(StatsFromValueAction.BlockId).With("value", "damage_dealt").With("duration", "always").With("cards", new CardFilter { ThisCard = true }).Linked(ActionLink.IfYouDo)));

    [Fact]
    public void Gandora_Reads() =>
        Assert.Equal("You can destroy all other monsters on the field, and if you do, inflict damage to your opponent equal to the highest ATK on the field among the destroyed face-up monsters they own, " +
                     "and if you do, this card gains that much ATK.", Text(Gandora()));

    [EngineFact]
    public void Gandora_DealsTheHighestOfTheirsAndGainsIt()
    {
        // Your Warrior (1500) doesn't count; of theirs, the Dragon (1600) is the highest.
        Duel(Gandora()).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Dragon, 1, "mzone").Card(Fiend, 1, "mzone").Activate(Id).ExpectActivations(1)
            .Check("Duel.GetLP(1)==6400").Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetAttack()==3400").AssertPasses();
    }
}
