using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Five cards built from blocks, played in the engine: Millennium Ankh (showing cards from several places, "except" kinds,
/// shuffling itself into the Deck after activation), Lava Golem, Steelcage of the Sun God (materials you control or on the
/// field, once per turn no matter which method), The Winged Dragon of Ra, Sun God of Darkness (two values added together),
/// Omega Vidolium the Power Patron of Heaven's End (the same Type as the activated monster, banishing it face-down) and
/// Time Chain (neither battling monster destroyed by that battle, banished until their controller's 2nd Standby Phase, and
/// the activated effects of monsters with their original name negated meanwhile).
/// </summary>
public class SunGodsAnkhTimeChainTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static string Lua(CardProject p, CardPack? pack = null)
    {
        var r = ScriptCompiler.Compile(p, pack);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics) + "\n" + r.Lua);
        Assert.DoesNotContain(Validation.ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        Assert.Empty(Validation.ScriptChecker.UnknownNames(r.Lua));
        return r.Lua;
    }

    private const int SunGod = 0x1a0, Forbidden = 0x40, Exodia = 0xde, Millennium = 0x1ae, PowerPatron = 0x1b0;

    private static CardPack Pack(params CardProject[] cards) => new()
    {
        Cards = [.. cards],
        Archetypes =
        [
            new() { Code = SunGod, Name = "Sun God" }, new() { Code = Forbidden, Name = "Forbidden One" }, new() { Code = Exodia, Name = "Exodia" },
            new() { Code = Millennium, Name = "Millennium" }, new() { Code = PowerPatron, Name = "Power Patron" },
        ],
    };

    private static CardProject Vanilla(long id, string name, int archetype = 0, int atk = 1200, int level = 4, CardRace race = CardRace.Fiend) => new()
    {
        Card = new CardData
        {
            Id = id, Name = name, Frame = MonsterFrame.Normal, Race = race, Attribute = CardAttribute.Light, Level = level, Atk = atk, Def = 1000,
            Archetypes = archetype == 0 ? [] : [archetype],
        },
    };

    private static string In(long code, int player, string loc) => $"Duel.IsExistingMatchingCard(Card.IsCode,{player},{loc},0,1,nil,{code})";

    private static string Count(long code, int player, string loc) => $"Duel.GetMatchingGroupCount(Card.IsCode,{player},{loc},0,nil,{code})";

    // ---------------------------------------------------------------- Lava Golem, Steelcage of the Sun God

    private const long GolemId = 100700010, SunServant = 100700001;

    private static CardProject Servant() => Vanilla(SunServant, "Sun God Servant", SunGod);

    private static CardProject LavaGolem(bool once = true) => new()
    {
        Card = new CardData
        {
            Id = GolemId, Name = "Lava Golem, Steelcage of the Sun God", Frame = MonsterFrame.Fusion, Race = CardRace.Fiend, Attribute = CardAttribute.Fire,
            Level = 8, Atk = 3000, Def = 2500, SummonRule = SummonRule.ProcedureOnly,
        },
        Procedure = new SummonProcedure
        {
            Kind = SummonProcedureKind.Fusion, Contact = true, ContactMove = ContactFusionMove.SendToGrave, ContactOpponent = true, ContactToOpponent = true,
            ContactOneMoreSummon = true, ContactOncePerTurn = once,
            Materials =
            [
                new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, Archetype = SunGod }, Place = MaterialPlace.YouControl },
                new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, FaceUpOnly = true }, Min = 2, Max = 2, Place = MaterialPlace.OnField },
            ],
        },
        Effects =
        [
            new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.material_ban", ("kind", "fusion"))] },
            new EffectEvent { When = B("when.phase", ("whose", "your"), ("phase", "standby"), ("optional", "false")), Limit = OncePerTurn.Soft, Do = [B("do.damage", ("amount", "1000"), ("who", "you"))] },
        ],
    };

    [Fact]
    public void LavaGolem_Wording()
    {
        var golem = LavaGolem();
        Assert.Equal("1 \"Sun God\" monster you control + 2 face-up monsters on the field\n" +
                     "Must be either Fusion Summoned, or Special Summoned (from your Extra Deck) to your opponent's Main Monster Zone by sending the above cards from either field to the GY, " +
                     "but if you Special Summon this card with the second method, you can only Special Summon once for the rest of this turn. You can only Special Summon " +
                     "\"Lava Golem, Steelcage of the Sun God\" once per turn this way, no matter which method you use. Cannot be used as Fusion Material. " +
                     "Once per turn, during your Standby Phase: You take 1000 damage.", EffectTextGenerator.Generate(golem, Pack(golem)));
        var lua = Lua(golem);
        Assert.Contains("c:IsOnField() and c:IsControler(fc:GetControler()) and s.fusfilter1k(c)", lua);
        Assert.Contains("c:IsOnField() and c:IsFaceup()", lua);
        Assert.Contains("Duel.GetFlagEffect(tp,id+8300)==0", lua);
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void LavaGolem_TheSunGodMonsterMustBeYours(bool yours)
    {
        var golem = LavaGolem();
        // Two face-up monsters on the field and a "Sun God" monster, yours or your opponent's.
        new EngineScenario(golem, Servant()).Card(GolemId, 0, "extra").Card(SunServant, yours ? 0 : 1, "mzone").Card(Fiend, 1, "mzone", 2).Card(Warrior, 0, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).SpSummon(GolemId)
            .Check($"{In(GolemId, 1, "LOCATION_MZONE")}=={(yours ? "true" : "false")}").AssertPasses();
    }

    [EngineTheory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void LavaGolem_OncePerTurnThisWay(bool once, int golems)
    {
        // Materials for two of them; "only one more Special Summon" alone would allow the second.
        new EngineScenario(LavaGolem(once), Servant()).Card(GolemId, 0, "extra", 2).Card(SunServant, 0, "mzone", 4).Card(Fiend, 1, "mzone", 2)
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).SpSummon(GolemId)
            .Check($"{Count(GolemId, 1, "LOCATION_MZONE")}=={golems}").AssertPasses();
    }

    // ---------------------------------------------------------------- The Winged Dragon of Ra, Sun God of Darkness

    private const long RaId = 100700020, RaMaterial = 100700019;

    private static CardProject Ra(int atk = -2) => new()
    {
        Card = new CardData
        {
            Id = RaId, Name = "The Winged Dragon of Ra, Sun God of Darkness", Frame = MonsterFrame.Fusion, Race = CardRace.DivineBeast, Attribute = CardAttribute.Divine,
            Level = 10, Atk = atk, Def = atk,
        },
        Procedure = new SummonProcedure
        {
            Kind = SummonProcedureKind.Fusion,
            Materials =
            [
                new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, CardCode = RaMaterial } },
                new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, Archetype = SunGod }, Min = 3, Max = 3 },
            ],
        },
        Effects =
        [
            new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.immune", ("whose", "other")).With("cards", new CardFilter { ThisCard = true, Zones = Zone.MonsterZone })] },
            new EffectEvent
            {
                When = B("when.quick", ("timing", "main_phase")), Limit = OncePerTurn.Soft,
                Cost = [B("cost.discard", ("mode", "any")).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, Archetype = SunGod })],
                Do = [B("do.send_zones", ("many", "cost"))],
            },
            new EffectEvent
            {
                When = B("when.attack_declared", ("mode", "trigger"), ("how", "involving")).With("mine", new CardFilter { ThisCard = true }),
                Cost = [B("cost.pay_lp", ("how", "to_one"))],
                Do =
                [
                    B(StatsFromValueAction.BlockId, ("stat", "both"), ("value", "lp_paid"), ("duration", "always"), ("plus", "value"), ("value2", "sum_atk"))
                        .With("cards", new CardFilter { ThisCard = true })
                        .With("of2", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, Archetype = SunGod }),
                ],
            },
        ],
    };

    [Fact]
    public void Ra_Wording()
    {
        var ra = Ra();
        Assert.Equal("\"The Winged Dragon of Ra\" + 3 \"Sun God\" monsters\n" +
                     "Unaffected by other cards' effects. Once per turn, during the Main Phase (Quick Effect): You can discard any number of \"Sun God\" cards, then choose that many of " +
                     "your opponent's occupied Main Monster Zones; your opponent must send the monsters in those zones to the GY. When an attack is declared involving this card: " +
                     "You can pay LP so that you only have 1 left; this card gains ATK/DEF equal to the amount of LP paid and the total ATK of all \"Sun God\" monsters currently in your GY.",
            EffectTextGenerator.Generate(ra, Pack(ra, Vanilla(RaMaterial, "The Winged Dragon of Ra"))));
        Assert.Contains("local v=(e:GetLabel())+(Duel.GetMatchingGroup(", Lua(ra));
    }

    [EngineFact]
    public void Ra_GainsTheLpPaidAndTheTotalAtkInTheGy()
    {
        // 3000 LP: 2999 paid; two 1200 ATK "Sun God" monsters in the GY. It attacks directly on its controller's turn.
        new EngineScenario(Ra(1000), Servant()).Card(RaId, 1, "mzone").Card(SunServant, 1, "grave", 2).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Setup("Duel.SetLP(1,3000)").Attack(RaId).Activate(RaId).Turns(2).ExpectActivations(1)
            .Check("Duel.GetLP(1)==1")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{RaId}):GetAttack()==1000+2999+2400")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{RaId}):GetDefense()==1000+2999+2400")
            .Check("Duel.GetLP(0)==8000-6399").AssertPasses();
    }

    // ---------------------------------------------------------------- Omega Vidolium the Power Patron of Heaven's End

    private const long VidoliumId = 100700030, Activator = 100700031;

    private static EffectEvent VidoliumNegates() => new()
    {
        When = B("when.activation", ("mode", "quick"), ("who", "opponent"), ("what", "monster"), ("damage_step", "yes")),
        If =
        [
            B("if.cards_exist", ("cmp", "at_least"), ("count", "2")).With("cards", new CardFilter
            {
                Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster,
                Requirements = [new CardRequirement { Property = "ref_race", Op = RequirementOp.Is, Values = [(long)CardRef.Activated] }],
            }),
        ],
        Cost = [B("cost.banish_top", ("count", "3"), ("facedown", "true"))],
        Do = [B("do.negate", ("what", "activation"), ("and", "banish_facedown"))],
    };

    private static CardProject Vidolium(bool full = true) => new()
    {
        Card = new CardData
        {
            Id = VidoliumId, Name = "Omega Vidolium the Power Patron of Heaven's End", Frame = MonsterFrame.Fusion, Abilities = MonsterAbility.Pendulum | MonsterAbility.SpecialSummonOnly,
            Race = CardRace.Machine, Attribute = CardAttribute.Dark, Level = 12, Atk = 4000, Def = 4000, Archetypes = [PowerPatron],
        },
        Procedure = new SummonProcedure
        {
            Kind = SummonProcedureKind.Fusion,
            Materials = [new MaterialSlot { Filter = new CardFilter { CardType = FilterCardType.Monster, AtkMin = 3000, Summoned = FilterSummoned.FromExtra }, Min = 3, Max = 3 }],
        },
        Effects = full
            ?
            [
                new EffectEvent
                {
                    Label = "Pendulum",
                    When = B("when.cards_detail", ("event", "special"), ("summon_from", "extra"), ("zone", "pendulum_zone"))
                        .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
                    Do =
                    [
                        B("do.this_card", ("how", "deck")),
                        B("do.fusion_summon", ("mats", "either_field"), ("including", "true"))
                            .With("inccards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Archetype = PowerPatron }).Linked(ActionLink.Then).MayDo(),
                    ],
                },
                new EffectEvent
                {
                    When = B("when.while_face_up"),
                    Do = [B("passive.immune", ("whose", "except_cards")).With("cards", new CardFilter { ThisCard = true, Zones = Zone.MonsterZone }).With("except", new CardFilter { Archetype = PowerPatron })],
                },
                VidoliumNegates(),
                new EffectEvent
                {
                    When = B("when.phase", ("whose", "your"), ("phase", "standby"), ("optional", "false")), Limit = OncePerTurn.Soft,
                    Do = [B("do.place", ("where", "pzone")).With("cards", new CardFilter { ThisCard = true })],
                },
            ]
            : [VidoliumNegates()],
    };

    [Fact]
    public void Vidolium_Wording()
    {
        var v = Vidolium();
        Assert.Equal("[ Pendulum Effect ]\nIf a monster(s) is Special Summoned from the Extra Deck: You can return this card to the Extra Deck, then you can Fusion Summon 1 Fusion Monster " +
                     "from your Extra Deck, using monsters from either field as Fusion Material, including a \"Power Patron\" monster you control.\n" +
                     "[ Monster Effect ]\n3 monsters Special Summoned from the Extra Deck with 3000 or more ATK\n" +
                     "Must first be Fusion Summoned. Unaffected by card effects, except \"Power Patron\" cards'. When your opponent activates a monster effect while they control 2 or more " +
                     "monsters with the same Type as that monster (Quick Effect): You can banish (face-down) the top 3 cards of your Deck; negate the activation, and if you do, banish " +
                     "that monster face-down. Once per turn, during your Standby Phase: Place this card in your Pendulum Zone.", EffectTextGenerator.GenerateWithSections(v, Pack(v)));
        var lua = Lua(v);
        Assert.Contains("c:IsSummonLocation(LOCATION_EXTRA)", lua);
        Assert.Contains("Duel.Remove(eg,POS_FACEDOWN,REASON_EFFECT)", lua);
        Assert.Contains("(not c:IsOnField() or c:IsFaceup()) and rg:IsExists(function(rc) return c:IsRace(rc:GetRace()) end,1,nil)", lua);
    }

    /// <summary>A Warrior that inflicts 500 damage to its controller's opponent.</summary>
    private static CardProject Burner() => new()
    {
        Card = new CardData { Id = Activator, Name = "Warrior Burner", Frame = MonsterFrame.Effect, Race = CardRace.Warrior, Attribute = CardAttribute.Earth, Level = 4, Atk = 1400, Def = 1000 },
        Effects = [new EffectEvent { When = B("when.ignition", ("zone", "monster_zone")), Limit = OncePerTurn.Hard, Do = [B("do.damage", ("amount", "500"))] }],
    };

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Vidolium_NegatesWhileTheyControlTwoOfThatType(bool twoWarriors)
    {
        new EngineScenario(Vidolium(full: false), Burner()).Card(VidoliumId, 0, "mzone").Card(Activator, 1, "mzone").Card(twoWarriors ? Warrior : Dragon, 1, "mzone")
            .Card(Warrior, 0, "deck", 6).Card(Warrior, 1, "deck", 5).Activate(Activator).Activate(VidoliumId).Turns(2)
            .ExpectActivations(twoWarriors ? 2 : 1)
            .Check($"Duel.GetLP(0)=={(twoWarriors ? 8000 : 7500)}")
            .Check(twoWarriors
                ? $"Duel.IsExistingMatchingCard(function(c) return c:IsCode({Activator}) and c:IsFacedown() end,1,LOCATION_REMOVED,0,1,nil) and Duel.GetFieldGroupCount(0,LOCATION_REMOVED,0)==3"
                : In(Activator, 1, "LOCATION_MZONE")).AssertPasses();
    }

    // ---------------------------------------------------------------- Millennium Ankh

    private const long AnkhId = 100700040, Piece = 100700041, Incarnate = 100700042, MillenniumGuard = 100700043, LowExodia = 100700044;

    private static CardProject Ankh(bool ignore = false) => new()
    {
        Card = new CardData { Id = AnkhId, Name = "Millennium Ankh", Kind = CardKind.Spell },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Cost = [B(AfterActivationCost.BlockId, ("how", "deck"))],
                Do =
                [
                    B(RevealAction.BlockId, ("verb", "show"), ("count", "5")).With("cards", new CardFilter
                        { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck | Zone.Field, CardType = FilterCardType.Monster, MonsterCards = true, FaceUpOnly = true, Archetype = Forbidden }),
                    B("do.special_summon", ("ignore", ignore ? "true" : "false")).With("cards", new CardFilter { Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, CardCode = Incarnate })
                        .Linked(ActionLink.Then),
                    B("do.shuffle_into_deck", ("mode", "all")).With("cards", new CardFilter
                    {
                        Owner = CardOwner.You, Zones = Zone.Field, CardType = FilterCardType.Monster, MonsterCards = true, FaceUpOnly = true,
                        Except =
                        [
                            new CardFilter { CardType = FilterCardType.Monster, MonsterCards = true, Archetype = Exodia, Requirements = [new CardRequirement { Property = "orig_level", Op = RequirementOp.GreaterOrEqual, Value = 10 }] },
                            new CardFilter { CardType = FilterCardType.Monster, MonsterCards = true, Archetype = Millennium },
                        ],
                    }).Linked(ActionLink.Then),
                    B("do.summon_restriction", ("what", "summon_any")).With("cards", new CardFilter { CardType = FilterCardType.Monster }).Linked(ActionLink.AlsoAfterThat),
                ],
            },
        ],
    };

    private static CardProject IncarnateCard() => new()
    {
        Card = new CardData { Id = Incarnate, Name = "The Unstoppable Exodia Incarnate", Frame = MonsterFrame.Fusion, Race = CardRace.Spellcaster, Attribute = CardAttribute.Dark, Level = 10, Atk = 0, Def = 0, Archetypes = [Exodia] },
    };

    private static CardProject[] AnkhCards(bool ignore) =>
    [
        Ankh(ignore), IncarnateCard(), Vanilla(Piece, "Forbidden Piece", Forbidden, 200, 1), Vanilla(MillenniumGuard, "Millennium Guard", Millennium),
        Vanilla(LowExodia, "Exodia Pawn", Exodia, 1000, 3),
    ];

    [Fact]
    public void Ankh_Wording()
    {
        var cards = AnkhCards(false);
        Assert.Equal("Show 5 \"Forbidden One\" Monster Cards from your hand, Deck, and/or face-up field, then Special Summon 1 \"The Unstoppable Exodia Incarnate\" from your Extra Deck, " +
                     "then shuffle all face-up Monster Cards you control into the Deck, except \"Exodia\" Monster Cards whose original Levels are 10 or higher, and \"Millennium\" " +
                     "Monster Cards, also you cannot Summon monsters for the rest of this turn. After activation, shuffle this card into the Deck instead of sending it to the GY.",
            EffectTextGenerator.Generate(cards[0], Pack(cards)));
        var lua = Lua(cards[0]);
        Assert.Contains("Duel.IsExistingMatchingCard(s.filter,tp,LOCATION_ONFIELD+LOCATION_DECK+LOCATION_HAND,0,5,nil,e,tp)", lua);
        Assert.Contains("not (c:GetOriginalType()&TYPE_MONSTER~=0 and c:IsSetCard(0x1ae))", lua);
        Assert.Contains("ac:IsRelateToEffect(e) and e:IsHasType(EFFECT_TYPE_ACTIVATE)", lua);
    }

    [EngineTheory]
    [InlineData(5)]
    [InlineData(4)]
    public void Ankh_ShowsFivePiecesThenSummonsAndShufflesTheRest(int pieces)
    {
        // Pieces in the hand, the Deck and face-up on the field; a "Millennium" monster and a Level 3 "Exodia" monster on the field.
        var s = new EngineScenario(AnkhCards(true)).Card(AnkhId, 0, "hand").Card(Incarnate, 0, "extra").Card(Piece, 0, "mzone").Card(Piece, 0, "hand", 2)
            .Card(Piece, 0, "deck", pieces - 3).Card(MillenniumGuard, 0, "mzone").Card(LowExodia, 0, "mzone").Card(Warrior, 0, "hand").Card(Warrior, 0, "deck", 3)
            .Card(Warrior, 1, "deck", 5).Activate(AnkhId).Summon(Warrior).ExpectActivations(pieces == 5 ? 1 : 0);
        if (pieces == 5)
            // The Incarnate and the "Millennium" monster stay; the piece and the Level 3 "Exodia" monster go back; Ankh goes to the
            // Deck; nothing more can be Summoned this turn.
            s.Check(In(Incarnate, 0, "LOCATION_MZONE")).Check(In(MillenniumGuard, 0, "LOCATION_MZONE")).Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==2")
                .Check(In(AnkhId, 0, "LOCATION_DECK")).Check(In(LowExodia, 0, "LOCATION_DECK")).Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==0");
        else
            s.Check(In(AnkhId, 0, "LOCATION_HAND")).Check(In(Warrior, 0, "LOCATION_MZONE"));
        s.AssertPasses();
    }

    // ---------------------------------------------------------------- Time Chain

    private const long ChainId = 100700050, Striker = 100700051;

    private static CardProject TimeChain() => new()
    {
        Card = new CardData { Id = ChainId, Name = "Time Chain", Kind = CardKind.Trap, TrapKind = TrapKind.Continuous },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.attack_declared", ("mode", "activate"), ("how", "targeted")).With("target", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
                Cost = [B(DestroyLaterCost.BlockId, ("whose", "opponent"), ("count", "2"), ("phase", "standby"), ("said", "first"))],
                Do =
                [
                    B(BattleSafeAction.BlockId),
                    B(LaterAction.BlockId, ("how", "banish_until"), ("what", "battling"), ("when", "battled"), ("until", "standby_ctrl2")),
                ],
            },
            new EffectEvent
            {
                When = B("when.while_face_up"),
                Do =
                [
                    B(NegateActivatedPassive.BlockId).With("cards", new CardFilter
                    {
                        Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Monster,
                        Requirements = [new CardRequirement { Property = "ref_orig_name", Op = RequirementOp.Is, Values = [(long)CardRef.BanishedByThis] }],
                    }),
                ],
            },
            new EffectEvent
            {
                When = B("when.phase", ("whose", "your"), ("phase", "draw"), ("instead", "true")),
                Do = [B("do.add_to_hand").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster })],
            },
        ],
    };

    [Fact]
    public void TimeChain_Wording()
    {
        var chain = TimeChain();
        Assert.Equal("Destroy this card during your opponent's 2nd Standby Phase after activation. When a monster you control is targeted for an attack: Neither battling monster " +
                     "can be destroyed by that battle, also banish them after damage calculation until their controller's 2nd Standby Phase. Negate the activated effects of " +
                     "monsters with the same original name as monsters that are currently banished by this card's effect. During your Draw Phase, instead of conducting your " +
                     "normal draw: You can add 1 monster from your Deck to your hand.", EffectTextGenerator.Generate(chain));
        var lua = Lua(chain);
        Assert.Contains("se:SetCode(EFFECT_INDESTRUCTABLE_BATTLE)", lua);
        Assert.Contains("de:SetCode(EVENT_BATTLED)", lua);
        Assert.Contains("tc:CreateRelation(e:GetHandler(),RESET_EVENT+RESETS_STANDARD)", lua);
    }

    /// <summary>A Warrior that inflicts 500 damage to its controller's opponent, once per turn by name.</summary>
    private static CardProject StrikerCard() => new()
    {
        Card = new CardData { Id = Striker, Name = "Chain Striker", Frame = MonsterFrame.Effect, Race = CardRace.Warrior, Attribute = CardAttribute.Earth, Level = 4, Atk = 1400, Def = 1000 },
        Effects = [new EffectEvent { When = B("when.ignition", ("zone", "monster_zone")), Limit = OncePerTurn.Hard, Do = [B("do.damage", ("amount", "500"))] }],
    };

    [EngineTheory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void TimeChain_BanishesBothUntilTheirControllersSecondStandbyPhase(int turns)
    {
        // Turn 2: the opponent burns 500, then attacks; both monsters survive the battle and are banished after it. Turn 4: the
        // other "Chain Striker"'s burn is negated while its namesake is banished. Turn 5: your monster is back; turn 6: theirs,
        // and Time Chain is destroyed.
        var s = new EngineScenario(TimeChain(), StrikerCard()).Card(ChainId, 0, "szone").Card(Warrior, 0, "mzone").Card(Striker, 1, "mzone", 2)
            .Card(Warrior, 0, "deck", 10).Card(Warrior, 1, "deck", 10).Activate(ChainId).Activate(Striker, 2).Attack(Striker).Turns(turns)
            .Check("Duel.GetLP(0)==7500").Check("Duel.GetLP(1)==7900");
        s.Check($"{In(Warrior, 0, "LOCATION_MZONE")}=={(turns >= 5 ? "true" : "false")}")
            .Check($"{Count(Striker, 1, "LOCATION_MZONE")}=={(turns >= 6 ? 2 : 1)}")
            .Check($"{In(ChainId, 0, "LOCATION_SZONE")}=={(turns >= 6 ? "false" : "true")}");
        s.AssertPasses();
    }

    // ---------------------------------------------------------------- the same pieces on other cards

    private const long Keeper = 100700060, Breaker = 100700061, Gardna = 100700062;

    /// <summary>Dimension Gate's shape: "... banish 1 monster your opponent controls." and "If this card is sent to the GY: Special Summon the monster banished by this card's effect."</summary>
    private static CardProject Gatekeeper() => new()
    {
        Card = new CardData { Id = Keeper, Name = "Gatekeeper", Frame = MonsterFrame.Effect, Race = CardRace.Warrior, Attribute = CardAttribute.Light, Level = 4, Atk = 1000, Def = 1000 },
        Effects =
        [
            new EffectEvent { When = B("when.ignition", ("zone", "monster_zone")), Do = [B("do.banish").With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })] },
            new EffectEvent
            {
                When = B("when.sent_to_gy", ("optional", "false")),
                Do = [B("do.special_summon").With("cards", new CardFilter { That = CardRef.BanishedByThis, CardType = FilterCardType.Monster })],
            },
        ],
    };

    /// <summary>Player 1's monster that destroys 1 monster player 0 controls.</summary>
    private static CardProject BreakerCard() => new()
    {
        Card = new CardData { Id = Breaker, Name = "Breaker", Frame = MonsterFrame.Effect, Race = CardRace.Fiend, Attribute = CardAttribute.Dark, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [new EffectEvent { When = B("when.ignition", ("zone", "monster_zone")), Do = [B("do.destroy").With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })] }],
    };

    [Fact]
    public void TheMonsterBanishedByThisCardsEffect_Wording()
    {
        Assert.Equal("If this card is sent to the GY: Special Summon the monster banished by this card's effect.", EffectTextGenerator.Describe(Gatekeeper().Effects[1], Gatekeeper().Card));
        Assert.Contains("c:IsLocation(LOCATION_REMOVED) and c:IsRelateToCard(h)", Lua(Gatekeeper()));
    }

    [EngineFact]
    public void TheMonsterBanishedByThisCardsEffect_ComesBackAfterThisCardLeft()
    {
        // Turn 1: the opponent's Fiend is banished. Turn 2: Gatekeeper is destroyed, and the Fiend is Special Summoned to your field.
        new EngineScenario(Gatekeeper(), BreakerCard()).Card(Keeper, 0, "mzone").Card(Fiend, 1, "mzone").Card(Breaker, 1, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(Keeper).Summon(Breaker).Activate(Breaker).Turns(2)
            .Check(In(Keeper, 0, "LOCATION_GRAVE")).Check(In(Fiend, 0, "LOCATION_MZONE")).AssertPasses();
    }

    [Fact]
    public void NegateTheActivatedEffectsOf_Wording()
    {
        // Brave-Eyes Pendulum Dragon, Predaplant Dragostapelia.
        string Of(CardFilter f) => EffectTextGenerator.Describe(new EffectEvent { When = B("when.while_face_up"), Do = [B(NegateActivatedPassive.BlockId).With("cards", f)] }, new CardData { Name = "Negator" });
        Assert.Equal("Negate the activated effects of monsters with 0 ATK.", Of(new CardFilter { Owner = CardOwner.Either, Zones = Zone.None, CardType = FilterCardType.Monster, AtkMin = 0, AtkMax = 0 }));
        Assert.Equal("Negate the activated effects of your opponent's Fiend monsters.", Of(new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.None, CardType = FilterCardType.Monster, Race = CardRace.Fiend }));
    }

    [EngineTheory]
    [InlineData(CardRace.Warrior, 8000)]
    [InlineData(CardRace.Fiend, 7500)]
    public void NegateTheActivatedEffectsOf_WarriorMonsters(CardRace race, int lp)
    {
        var negator = new CardProject
        {
            Card = new CardData { Id = 100700063, Name = "Negator", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
            Effects =
            [
                new EffectEvent { When = B("when.while_face_up"), Do = [B(NegateActivatedPassive.BlockId).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.None, CardType = FilterCardType.Monster, Race = race })] },
            ],
        };
        new EngineScenario(negator, Burner()).Card(negator.Card.Id, 0, "hand").Card(Activator, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(negator.Card.Id).Activate(Activator).Turns(2).Check($"Duel.GetLP(0)=={lp}").AssertPasses();
    }

    /// <summary>Gagaga Gardna: "When this card is targeted for an attack: You can discard 1 card; this card cannot be destroyed by that battle."</summary>
    private static CardProject GardnaCard() => new()
    {
        Card = new CardData { Id = Gardna, Name = "Gardna", Frame = MonsterFrame.Effect, Race = CardRace.Warrior, Attribute = CardAttribute.Earth, Level = 4, Atk = 1000, Def = 1000 },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.this_card", ("event", "attacked")),
                Cost = [B("cost.discard")],
                Do = [B(BattleSafeAction.BlockId).With("cards", new CardFilter { ThisCard = true })],
            },
        ],
    };

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThisCardCannotBeDestroyedByThatBattle(bool uses)
    {
        Assert.Equal("When this card is targeted for an attack: You can discard 1 card from your hand; this card cannot be destroyed by that battle.", EffectTextGenerator.Describe(GardnaCard().Effects[0], GardnaCard().Card));
        var s = new EngineScenario(GardnaCard()).Card(Gardna, 0, "mzone").Card(Warrior, 0, "hand").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Attack(Dragon).Turns(2);
        if (uses) s.Activate(Gardna);
        s.Check($"{In(Gardna, 0, "LOCATION_MZONE")}=={(uses ? "true" : "false")}").AssertPasses();
    }

    [Fact]
    public void RevealCardsInTheHand_Wording()
    {
        var reveal = new EffectEvent
        {
            When = B("when.ignition"),
            Do = [B(RevealAction.BlockId, ("verb", "reveal")).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Spell }), B("do.draw").Linked(ActionLink.IfYouDo)],
        };
        Assert.Equal("You can reveal 1 Spell in your hand, and if you do, draw 1 card.", EffectTextGenerator.Describe(reveal, new CardData { Name = "Revealer" }));
    }
}
