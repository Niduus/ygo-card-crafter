using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Rare mechanics: winning the Duel (Exodia the Forbidden One), banishing a monster until the resolving effect has
/// resolved (Perfect Toon World), banishing this card until the End Phase and Link Summoning with an opponent's monster
/// (W:P Fancy Ball), a monster tied to the Continuous Spell that Summoned it (Castle of Vampire), an effect that becomes a
/// Spell's (Predaplant Verte Anaconda) and Contact Fusion to the opponent's field (Lava Golem, Steelcage of the Sun God).
/// </summary>
public class RareMechanicsTests
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

    private static CardProject Monster(long id, string name, CardAttribute attribute = CardAttribute.Light, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Effect, Race = CardRace.Warrior, Attribute = attribute, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    private static CardProject Vanilla(long id, string name) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Normal, Race = CardRace.Spellcaster, Attribute = CardAttribute.Dark, Level = 1, Atk = 200, Def = 300 },
    };

    private static string Text(CardProject p, CardPack? pack = null, int i = 0) => EffectTextGenerator.Describe(p.Effects[i], p.Card, pack);

    private static string In(long code, int player, string loc) => $"Duel.IsExistingMatchingCard(Card.IsCode,{player},{loc},0,1,nil,{code})";

    /// <summary>Player 1's monster that destroys 1 monster player 0 controls (not targeting).</summary>
    private static CardProject Destroyer(long id, CardAttribute attribute) => Monster(id, "Destroyer", attribute, new EffectEvent
    {
        When = B("when.ignition", ("zone", "monster_zone")),
        Do = [B("do.destroy").With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
    });

    // ---------------------------------------------------------------- you win the Duel

    private static (CardProject Head, CardProject[] Limbs) Exodia()
    {
        var limbs = new[] { Vanilla(100500001, "Right Leg"), Vanilla(100500002, "Left Leg"), Vanilla(100500003, "Right Arm"), Vanilla(100500004, "Left Arm") };
        var names = new CardFilter { Zones = Zone.None, CardCode = limbs[0].Card.Id, AltCodes = limbs.Skip(1).Select(l => l.Card.Id).ToArray() };
        var head = Monster(100500000, "Forbidden Head", CardAttribute.Dark, new EffectEvent
        {
            When = B("when.while_face_up", ("zone", "hand")),
            If = [B(HaveNamesCondition.BlockId).With("cards", names)],
            Do = [B(WinPassive.BlockId)],
        });
        return (head, limbs);
    }

    [Fact]
    public void Exodia_Wording()
    {
        var (head, limbs) = Exodia();
        var pack = new CardPack { Cards = [head, .. limbs] };
        Assert.Equal("If you have \"Right Leg\", \"Left Leg\", \"Right Arm\" and \"Left Arm\" in addition to this card in your hand, you win the Duel.", Text(head, pack));
        var lua = Lua(head);
        Assert.Contains("EVENT_ADJUST", lua);
        Assert.Contains("Duel.Win(tp,0x10)", lua);
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Exodia_WinsTheDuelWithAllFivePiecesInHand(bool allFive)
    {
        var (head, limbs) = Exodia();
        var s = new EngineScenario([head, .. limbs]).Card(head.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5);
        foreach (var limb in allFive ? limbs : limbs.Skip(1)) s.Card(limb.Card.Id, 0, "hand");
        // The harness plays on after a win, so the win is noted as it is declared.
        s.Setup("local win=Duel.Win Duel.Win=function(p,r) cc_won=p win(p,r) end")
            .Check(allFive ? "cc_won==0" : "cc_won==nil").AssertPasses();
    }

    [Fact]
    public void YouWinTheDuel_AsAStep()
    {
        var leo = Monster(100500010, "Disaster Puppet", CardAttribute.Dark, new EffectEvent
        {
            When = B("when.phase", ("phase", "end"), ("whose", "you")),
            If = [B("if.life_points", ("who", "opponent"), ("cmp", "at_most"), ("amount", "2000"))],
            Do = [B(WinAction.BlockId)],
        });
        Assert.EndsWith("you win the Duel.", Text(leo));
        Assert.Contains("Duel.Win(tp,0x10)", Lua(leo));
    }

    // ---------------------------------------------------------------- Perfect Toon World

    [EngineFact]
    public void BeforeAnEffectResolves_BanishAMonsterUntilItHasResolved()
    {
        var world = new CardProject
        {
            Card = new CardData { Id = 100500020, Name = "Perfect World", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
            Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [B(DodgePassive.BlockId).With("cards", new CardFilter { Zones = Zone.None, CardType = FilterCardType.Monster, Race = CardRace.Warrior })] }],
        };
        Assert.Equal("Before resolving another activated card or effect, you can banish 1 Warrior monster you control until immediately after that card/effect resolves, " +
                     "also you cannot banish monsters with that same original name with this effect of \"Perfect World\" for the rest of this turn.", Text(world));
        var destroyer = Destroyer(100500021, CardAttribute.Light);
        // Activated on turn 1; on turn 2 the Warrior dodges the opponent's destruction and comes back.
        new EngineScenario(world, destroyer).Card(world.Card.Id, 0, "hand").Card(Warrior, 0, "mzone").Card(destroyer.Card.Id, 1, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(world.Card.Id, 2).Activate(destroyer.Card.Id).Turns(2)
            .Check(In(Warrior, 0, "LOCATION_MZONE")).Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==0").AssertPasses();
    }

    // ---------------------------------------------------------------- W:P Fancy Ball

    [EngineFact]
    public void BanishThisCardUntilTheEndPhase_ItComesBack()
    {
        var ball = Monster(100500030, "Fancy Ball", effects: new EffectEvent
        {
            When = B("when.ignition", ("zone", "monster_zone")),
            Cost = [B("cost.this_card", ("how", "banish_until_end"))],
            Do = [B("do.draw")],
        });
        Assert.Equal("You can banish this card until the End Phase; draw 1 card.", Text(ball));
        new EngineScenario(ball).Card(ball.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(ball.Card.Id).Check(In(ball.Card.Id, 0, "LOCATION_MZONE")).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").AssertPasses();
    }

    private static CardProject Link(long id, string name, int rating, int minMaterials, params EffectEvent[] effects) => new()
    {
        Card = new CardData
        {
            Id = id, Name = name, Frame = MonsterFrame.Link, Race = CardRace.Cyberse, Attribute = CardAttribute.Light, Atk = 1000 * rating,
            LinkMarkers = rating switch { 1 => LinkMarker.Bottom, 2 => LinkMarker.Left | LinkMarker.Right, _ => LinkMarker.Left | LinkMarker.Right | LinkMarker.Bottom },
        },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = minMaterials },
        Effects = [.. effects],
    };

    [EngineFact]
    public void LinkSummon_AlsoUsingALinkTwoOrLowerMonsterYourOpponentControls()
    {
        var summon = B("do.extra_deck_summon", ("kind", "link"), ("also_opp", "true"));
        var ball = Link(100500031, "Fancy Linker", 2, 2, new EffectEvent
        {
            When = B("when.ignition", ("zone", "monster_zone")),
            Cost = [B("cost.chain_limit", ("who", "both"), ("what", "link"))],
            Do = [summon],
        });
        Assert.Equal("You can immediately after this effect resolves, Link Summon 1 Link Monster, and if this card you control would be used as material for that Link Summon, you can also use 1 Link Monster " +
                     "whose Link Rating is 2 or lower your opponent controls. Neither player can activate the effects of Link Monsters in response to this effect's activation.", Text(ball));
        var lua = Lua(ball);
        Assert.Contains("EFFECT_EXTRA_LINK_MATERIAL", lua);
        Assert.Contains("IsAllTypes(TYPE_LINK+TYPE_MONSTER)", lua);
        var theirs = Link(100500032, "Their Link", 1, 1);
        var big = Link(100500033, "Big Link", 3, 2);
        // Only this card and the opponent's Link-1 can be its materials.
        new EngineScenario(ball, theirs, big).Card(ball.Card.Id, 0, "mzone").Card(theirs.Card.Id, 1, "mzone").Card(big.Card.Id, 0, "extra")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(ball.Card.Id).Check(In(big.Card.Id, 0, "LOCATION_MZONE")).Check(In(theirs.Card.Id, 1, "LOCATION_GRAVE")).AssertPasses();
    }

    // ---------------------------------------------------------------- Castle of Vampire

    private static CardProject Castle(long id) => new()
    {
        Card = new CardData { Id = id, Name = "Vampire Castle", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do =
                [
                    B("do.special_summon").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Fiend }),
                    B(BindAction.BlockId, ("gets", "unaffected_non_attr"), ("attribute", "Dark")),
                ],
            },
        ],
    };

    [EngineTheory]
    [InlineData(CardAttribute.Light, true)]
    [InlineData(CardAttribute.Dark, false)]
    public void ThatMonster_IsUnaffectedByNonDarkMonsters_AndThisCardGoesWhenItLeaves(CardAttribute destroyerAttribute, bool survives)
    {
        var castle = Castle(100500040);
        Assert.Equal("Special Summon 1 Fiend monster from your Deck, and when that monster leaves the field, destroy this card. " +
                     "That monster is unaffected by the activated effects of other non-DARK monsters.", Text(castle));
        var destroyer = Destroyer(100500041, destroyerAttribute);
        new EngineScenario(castle, destroyer).Card(castle.Card.Id, 0, "hand").Card(Fiend, 0, "deck").Card(destroyer.Card.Id, 1, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(castle.Card.Id).Activate(destroyer.Card.Id).Turns(2)
            .Check($"{In(Fiend, 0, "LOCATION_MZONE")}=={(survives ? "true" : "false")}")
            .Check($"{In(castle.Card.Id, 0, "LOCATION_SZONE")}=={(survives ? "true" : "false")}").AssertPasses();
    }

    // ---------------------------------------------------------------- Predaplant Verte Anaconda

    [EngineFact]
    public void ThisEffectBecomesThatSpellsEffect()
    {
        var pot = new CardProject
        {
            Card = new CardData { Id = 100500051, Name = "Fusion Pot", Kind = CardKind.Spell, SpellKind = SpellKind.Normal },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.draw", ("count", "2"))] }],
        };
        var verte = Monster(100500050, "Verte", CardAttribute.Dark, new EffectEvent
        {
            When = B("when.ignition", ("zone", "monster_zone")),
            Cost =
            [
                B("cost.pay_lp", ("amount", "2000")),
                B("cost.move_cards", ("how", "grave")).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Spell, SpellTrapKind = SpellTrapKind.Normal }),
            ],
            Do = [B(CopyEffectAction.BlockId)],
        });
        Assert.Equal("You can pay 2000 LP and send 1 Normal Spell from your Deck to the GY; this effect becomes that Spell's effect when that card is activated.", Text(verte));
        Assert.Contains("CheckActivateEffect(true,true,true)", Lua(verte));
        new EngineScenario(verte, pot).Card(verte.Card.Id, 0, "mzone").Card(pot.Card.Id, 0, "deck").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(verte.Card.Id).Check("Duel.GetLP(0)==6000").Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2").Check(In(pot.Card.Id, 0, "LOCATION_GRAVE")).AssertPasses();
    }

    // ---------------------------------------------------------------- Time Chain's timer, instead of the normal draw

    [EngineTheory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void DestroyThisCardDuringYourNextStandbyPhaseAfterActivation(int turns, bool stillThere)
    {
        var chain = new CardProject
        {
            Card = new CardData { Id = 100500070, Name = "Time Lock", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
            Effects = [new EffectEvent { When = B("when.activate"), Cost = [B(DestroyLaterCost.BlockId, ("count", "1"))], Do = [B("do.draw")] }],
        };
        Assert.Equal("Draw 1 card. Destroy this card during your 1st Standby Phase after activation.", Text(chain));
        // Activated in the Main Phase of turn 1; player 0's next Standby Phase is in turn 3.
        new EngineScenario(chain).Card(chain.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(chain.Card.Id).Turns(turns).Check($"{In(chain.Card.Id, 0, "LOCATION_SZONE")}=={(stillThere ? "true" : "false")}").AssertPasses();
    }

    [Fact]
    public void DestroyThisCardDuringYourOpponentsThirdEndPhase_Wording()
    {
        var amulet = new CardProject
        {
            Card = new CardData { Id = 100500071, Name = "Amulet", Kind = CardKind.Trap, TrapKind = TrapKind.Continuous },
            Effects = [new EffectEvent { When = B("when.activate"), Cost = [B(DestroyLaterCost.BlockId, ("whose", "opponent"), ("count", "3"), ("phase", "end"))], Do = [B("do.draw")] }],
        };
        Assert.EndsWith("Destroy this card during your opponent's 3rd End Phase after activation.", Text(amulet));
        Assert.Contains("le:SetReset(RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END+RESET_OPPO_TURN,3)", Lua(amulet));
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuringYourDrawPhase_InsteadOfYourNormalDraw_AddAMonster(bool uses)
    {
        var freed = Monster(100500075, "Freed", effects: new EffectEvent
        {
            When = B("when.phase", ("whose", "your"), ("phase", "draw_instead"), ("zone", "monster_zone")),
            Do = [B("do.add_to_hand").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Warrior })],
        });
        Assert.Equal("During your Draw Phase, instead of conducting your normal draw: You can add 1 Warrior monster from your Deck to your hand.", Text(freed));
        // Player 1's turn 2: the normal draw (a Fiend) is given up for the Warrior.
        var s = new EngineScenario(freed).Card(freed.Card.Id, 1, "mzone").Card(Warrior, 1, "deck").Card(Fiend, 1, "deck", 4).Card(Warrior, 0, "deck", 5).Turns(2);
        if (uses) s.Activate(freed.Card.Id);
        s.Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==1")
            .Check($"{In(Warrior, 1, "LOCATION_HAND")}=={(uses ? "true" : "false")}").AssertPasses();
    }

    // ---------------------------------------------------------------- Lava Golem, Steelcage of the Sun God

    private static CardProject Steelcage(long id, bool oneMore) => new()
    {
        Card = new CardData { Id = id, Name = "Steelcage", Frame = MonsterFrame.Fusion, Race = CardRace.Fiend, Attribute = CardAttribute.Fire, Level = 8, Atk = 3000, Def = 2500 },
        Procedure = new SummonProcedure
        {
            Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [Warrior, Fiend], Contact = true, ContactMove = ContactFusionMove.SendToGrave,
            ContactOpponent = true, ContactToOpponent = true, ContactOneMoreSummon = oneMore,
        },
    };

    [Fact]
    public void ContactFusionToTheOpponentsField_Wording()
    {
        var cage = Steelcage(100500060, true);
        cage.Card.SummonRule = SummonRule.ProcedureOnly;
        Assert.Equal("Must be either Fusion Summoned, or Special Summoned (from your Extra Deck) to your opponent's Main Monster Zone by sending the above cards from either field " +
                     "to the GY, but if you Special Summon this card with the second method, you can only Special Summon once for the rest of this turn.", ContactFusion.Text(cage));
        var lua = Lua(cage);
        Assert.Contains("cp:SetTargetRange(POS_FACEUP,1)", lua);
        Assert.Contains("Duel.GetMZoneCount(1-tp,mg,tp,LOCATION_REASON_TOFIELD,0x1f)>0", lua);
    }

    [EngineTheory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void ContactFusionToTheOpponentsField_ThenOnlyOneMoreSpecialSummon(bool oneMore, int summoners)
    {
        var cage = Steelcage(100500061, oneMore);
        // Monsters that Special Summon themselves from the hand once the opponent controls a Fusion Monster.
        var summoner = Monster(100500062, "Late Summoner", effects: new EffectEvent
        {
            When = B("when.summon_procedure", ("zone", "hand")),
            If = [B("if.cards_exist").With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Fusion })],
        });
        new EngineScenario(cage, summoner).Card(cage.Card.Id, 0, "extra").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone").Card(summoner.Card.Id, 0, "hand", 2)
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .SpSummon(cage.Card.Id).SpSummon(summoner.Card.Id)
            .Check(In(cage.Card.Id, 1, "LOCATION_MZONE")).Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==1").Check("Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)==1")
            .Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_MZONE,0,nil,{summoner.Card.Id})=={summoners}").AssertPasses();
    }
}
