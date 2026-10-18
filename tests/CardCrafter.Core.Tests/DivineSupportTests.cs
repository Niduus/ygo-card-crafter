using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The Egyptian Gods' support built from blocks and played in the engine: Slifer's "then if its ATK has been reduced to 0
/// as a result, destroy it", Thunderforce Attack (original names, a gate on the phase it was activated in, drawing for the
/// cards sent to the opponent's GY, attacking with 1 monster), Guardian Slime (during that damage calculation only),
/// Ancient Chant (the Tributed monsters' combined stats), Blaze Cannon (effects given to a monster for the turn), Dangers of
/// the Divine (choose 1 and send all others), The Breaking Ruin God (2 or more Tributed at the same time, unaffected this
/// turn), Trap Trick (only 1 Trap Card), The Revived Sky God (draw until 6) and Sun God Unification (not in the same Chain).
/// </summary>
public class DivineSupportTests
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

    private static CardPack Pack(params CardProject[] cards) => new() { Cards = [.. cards] };

    private static string In(long code, int player, string loc) => $"Duel.IsExistingMatchingCard(Card.IsCode,{player},{loc},0,1,nil,{code})";

    private static string Card(long code, int player, string loc = "LOCATION_MZONE") => $"Duel.GetFirstMatchingCard(Card.IsCode,{player},{loc},0,nil,{code})";

    private const long SlId = 100720001, RaId = 100720002, ObId = 100720003, Thunder = 100720004, Slime = 100720005, Chant = 100720006, Cannon = 100720007,
        Dangers = 100720008, Ruin = 100720009, TrapLock = 100720010, TrapGain = 100720011, SkyGod = 100720012, Unify = 100720013, Big = 100720014, Small = 100720015;

    private static CardProject God(long id, string name, int atk = 4000) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Normal, Race = CardRace.DivineBeast, Attribute = CardAttribute.Divine, Level = 10, Atk = atk, Def = atk },
    };

    private static CardProject Vanilla(long id, string name, int atk) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Normal, Race = CardRace.Warrior, Attribute = CardAttribute.Earth, Level = 4, Atk = atk, Def = 1000 },
    };

    private static CardFilter Named(long id, Zone zones = Zone.MonsterZone, CardOwner owner = CardOwner.You) =>
        new() { Owner = owner, Zones = zones, CardType = FilterCardType.Monster, CardCode = id };

    // ---------------------------------------------------------------- Slifer the Sky Dragon

    private static CardProject Slifer() => new()
    {
        Card = new CardData { Id = SlId, Name = "Slifer the Sky Dragon", Frame = MonsterFrame.Effect, Race = CardRace.DivineBeast, Attribute = CardAttribute.Divine, Level = 10, Atk = 0, Def = 0 },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.cards_detail", ("event", "normal"), ("optional", "false"))
                    .With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster,
                        Requirements = [new CardRequirement { Property = "position", Op = RequirementOp.Is, Values = [0x3] }] }),
                Do =
                [
                    B("do.change_stats", ("dir", "lose"), ("amount", "2000"), ("duration", "always"), ("destroy_zero", "true"))
                        .With("cards", new CardFilter { That = CardRef.Event, CardType = FilterCardType.Monster }),
                ],
            },
        ],
    };

    [Fact]
    public void Slifer_Wording()
    {
        var text = EffectTextGenerator.Generate(Slifer());
        Assert.Equal("If a monster(s) is Normal Summoned to your opponent's field in Attack Position: Those monsters lose 2000 ATK, then destroy those whose ATK has been " +
                     "reduced to 0 as a result.", text);
        Assert.Contains("if preatk~=0 and tc:IsAttack(0) then dg:AddCard(tc) end", Lua(Slifer()));
    }

    [EngineTheory]
    [InlineData(Small, true)]
    [InlineData(Big, false)]
    public void Slifer_DestroysWhatItReducesTo0(long summoned, bool destroyed)
    {
        // Your opponent Normal Summons a 1500 or a 2500 ATK monster during their turn.
        new EngineScenario(Slifer(), Vanilla(Small, "Small", 1500), Vanilla(Big, "Big", 2500)).Card(SlId, 0, "mzone").Card(summoned, 1, "hand")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Summon(summoned).Turns(2)
            .Check(destroyed ? In(summoned, 1, "LOCATION_GRAVE") : $"{Card(summoned, 1)}:GetAttack()==500")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- Thunderforce Attack

    /// <param name="quick">A Quick-Play Spell, as printed; otherwise a Normal Spell, which can only be activated in the Main Phase.</param>
    private static CardProject Thunderforce(bool quick = true) => new()
    {
        Card = new CardData { Id = Thunder, Name = "Thunderforce Attack", Kind = CardKind.Spell, SpellKind = quick ? SpellKind.QuickPlay : SpellKind.Normal },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"), Limit = OncePerTurn.Hard,
                If =
                [
                    B("if.cards_exist").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true,
                        Requirements = [new CardRequirement { Property = "orig_name", Op = RequirementOp.Is, Values = [SlId] }] }),
                ],
                Cost = [B(CannotBeNegatedCost.BlockId)],
                Do =
                [
                    B("do.destroy", ("mode", "all")).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true }),
                    B("if.phase", ("phase", "your_main")).Linked(ActionLink.Then),
                    B("do.draw", ("per", "per_sent_opp")).MayDo(),
                    B("do.summon_restriction", ("what", "attack_one")),
                ],
            },
        ],
    };

    [Fact]
    public void Thunderforce_Wording()
    {
        var t = Thunderforce();
        Assert.Equal("If you control a face-up monster whose original name is \"Slifer the Sky Dragon\": Destroy all face-up monsters your opponent controls, then if you activated " +
                     "this card during your Main Phase, you can draw cards equal to the number of monsters destroyed by this effect and sent to your opponent's GY, also you can " +
                     "only attack with 1 monster this turn. This card's activation and effect cannot be negated. You can only activate 1 \"Thunderforce Attack\" per turn.",
            EffectTextGenerator.Generate(t, Pack(t, Slifer())));
        var lua = Lua(t);
        Assert.Contains("c:IsOriginalCodeRule(" + SlId + ")", lua);
        Assert.Contains("Duel.GetTurnPlayer()==tp and (Duel.GetCurrentPhase()==PHASE_MAIN1", lua);
    }

    [EngineFact]
    public void Thunderforce_DestroysDrawsAndOnlyOneMonsterAttacks()
    {
        // Player 1's turn: Slifer and two 1500 ATK Warriors; player 0 has 2 face-up monsters. Only one Warrior attacks directly.
        new EngineScenario(Thunderforce(quick: false), Slifer()).Card(Thunder, 1, "hand").Card(SlId, 1, "mzone").Card(Warrior, 1, "mzone", 2).Card(Fiend, 0, "mzone", 2)
            .Card(Warrior, 0, "deck", 5).Card(Dragon, 1, "deck", 6).Activate(Thunder).Attack(Warrior).Attack(Warrior).Turns(2)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==0 and Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==2")
            // Its normal draw and the 2 cards for the destroyed monsters.
            .Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==3")
            .Check("Duel.GetLP(0)==8000-1500")
            .AssertPasses();
    }

    [EngineFact]
    public void Thunderforce_OnTheOpponentsTurn_DrawsNothing()
    {
        // Set, it is activated during player 0's turn: the monsters are destroyed, but nobody draws (player 1 only has its normal draw).
        new EngineScenario(Thunderforce(), Slifer()).Card(Thunder, 1, "szone").Card(SlId, 1, "mzone").Card(Fiend, 0, "mzone", 2)
            .Card(Warrior, 0, "deck", 5).Card(Dragon, 1, "deck", 6).Activate(Thunder)
            .ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==2 and Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==1")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- Fist of Fate

    private const long Fist = 100720016;

    private static CardProject FistOfFate(bool quick = true) => new()
    {
        Card = new CardData { Id = Fist, Name = "Fist of Fate", Kind = CardKind.Spell, SpellKind = quick ? SpellKind.QuickPlay : SpellKind.Normal },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"), Limit = OncePerTurn.Hard,
                If =
                [
                    B("if.cards_exist").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true,
                        Requirements = [new CardRequirement { Property = "orig_name", Op = RequirementOp.Is, Values = [ObId] }] }),
                ],
                Cost = [B(CannotBeNegatedCost.BlockId)],
                Do =
                [
                    B("do.negate_effects").With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, MonsterKind = MonsterKind.Effect }),
                    B("do.destroy").With("cards", new CardFilter { That = CardRef.Affected, CardType = FilterCardType.Monster }).Linked(ActionLink.IfYouDo),
                    B(NegateSameNameAction.BlockId, ("duration", "turn")).Linked(ActionLink.IfYouDo),
                    B("if.phase", ("phase", "your_main")).Linked(ActionLink.Then),
                    B("do.destroy", ("mode", "all")).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.SpellTrapZone, CardType = FilterCardType.SpellTrap }).MayDo(),
                ],
            },
        ],
    };

    [Fact]
    public void FistOfFate_Wording()
    {
        var f = FistOfFate();
        Assert.Equal("If you control a face-up monster whose original name is \"Obelisk the Tormentor\": Negate the effects of 1 face-up Effect Monster your opponent controls until the end of this turn, " +
                     "and if you do, destroy it, and if you do, until the end of this turn, its effects are negated, as well as the activated effects and effects on the field " +
                     "of monsters with the same original name, then if you activated this card during your Main Phase, you can destroy all Spells/Traps your opponent controls. " +
                     "This card's activation and effect cannot be negated. You can only activate 1 \"Fist of Fate\" per turn.", EffectTextGenerator.Generate(f, Pack(f, Obelisk())));
        Lua(f);
    }

    [EngineFact]
    public void FistOfFate_DestroysTheMonster_ThenTheirSpellsAndTraps()
    {
        new EngineScenario(FistOfFate(quick: false), Obelisk()).Card(Fist, 0, "hand").Card(ObId, 0, "mzone").Card(EffectFiller, 1, "mzone").Card(Trap, 1, "szone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(Fist)
            .Check(In(EffectFiller, 1, "LOCATION_GRAVE") + " and " + In(Trap, 1, "LOCATION_GRAVE")).AssertPasses();
    }

    // ---------------------------------------------------------------- Sun God Unification

    private static CardProject SunGodUnification() => new()
    {
        Card = new CardData { Id = Unify + 100, Name = "Sun God Unification", Kind = CardKind.Trap, TrapKind = TrapKind.Continuous },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.while_face_up"),
                If = [B("if.cards_exist").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true,
                    Requirements = [new CardRequirement { Property = "orig_name", Op = RequirementOp.Is, Values = [RaId] }] })],
                Do = [B(ActivateSetTurnPassive.BlockId)],
            },
            new EffectEvent
            {
                When = B("when.quick", ("timing", "main_phase"), ("zone", "spell_trap_zone")), SameChainGroup = 1,
                Cost = [B("cost.pay_lp", ("how", "to_one"), ("left", "100"))],
                Do =
                [
                    B(StatsFromValueAction.BlockId, ("stat", "both"), ("value", "lp_paid"), ("duration", "always")).With("cards", new CardFilter
                    {
                        Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, CardCode = RaId,
                        Requirements = [new CardRequirement { Property = "summon", Op = RequirementOp.Is, Values = [3] }],
                    }),
                ],
            },
            new EffectEvent
            {
                When = B("when.quick", ("zone", "spell_trap_zone")), Limit = OncePerTurn.Soft, SameChainGroup = 1,
                Cost = [B("cost.tribute").With("cards", Named(RaId))],
                Do = [B(LifeFromValueAction.BlockId, ("what", "gain"), ("who", "you"), ("value", "cost_atk"))],
            },
        ],
    };

    [Fact]
    public void SunGodUnification_Wording()
    {
        var u = SunGodUnification();
        var text = EffectTextGenerator.Generate(u, Pack(u, God(RaId, "The Winged Dragon of Ra")));
        Assert.StartsWith("If you control a face-up monster whose original name is \"The Winged Dragon of Ra\", you can activate this card the turn it was Set. ", text);
        Assert.Contains("You can pay LP so that you only have 100 left; ", text);
        Assert.EndsWith(" You cannot activate both of this card's effects in the same Chain.", text);
        Lua(u);
    }

    // ---------------------------------------------------------------- Guardian Slime

    private static CardProject GuardianSlime() => new()
    {
        Card = new CardData { Id = Slime, Name = "Guardian Slime", Frame = MonsterFrame.Effect, Race = CardRace.Aqua, Attribute = CardAttribute.Divine, Level = 10, Atk = 1000, Def = 1000 },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.battle", ("mode", "quick"), ("window", "calc"), ("who", "this_card"), ("versus", "opponent")), Limit = OncePerTurn.Hard,
                Do = [B(StatsFromValueAction.BlockId, ("stat", "atk"), ("value", "battle_atk"), ("duration", "damage_calc")).With("cards", new CardFilter { ThisCard = true })],
            },
        ],
    };

    [Fact]
    public void GuardianSlime_Wording()
    {
        Assert.Equal("If this card battles an opponent's monster, during damage calculation (Quick Effect): You can activate this effect; this card gains ATK equal to " +
                     "that opponent's monster's ATK during that damage calculation only. You can only use this effect of \"Guardian Slime\" once per turn.",
            EffectTextGenerator.Generate(GuardianSlime()));
        Assert.Contains("RESET_PHASE+PHASE_DAMAGE_CAL", Lua(GuardianSlime()));
    }

    [EngineFact]
    public void GuardianSlime_GainsTheAttackersAtk_ForThatDamageCalculationOnly()
    {
        // A 1600 ATK Dragon attacks it: it has 2600 ATK for the battle, then 1000 again.
        new EngineScenario(GuardianSlime()).Card(Slime, 0, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Attack(Dragon).Activate(Slime).Turns(2)
            .ExpectActivations(1)
            .Check(In(Dragon, 1, "LOCATION_GRAVE"))
            .Check($"{Card(Slime, 0)}:GetAttack()==1000")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- Ancient Chant

    private static CardProject AncientChant() => new()
    {
        Card = new CardData { Id = Chant, Name = "Ancient Chant", Kind = CardKind.Spell },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.ignition", ("zone", "graveyard")),
                Cost = [B("cost.this_card", ("how", "banish"))],
                Do = [B(TributedStatsAction.BlockId).With("cards", new CardFilter { CardType = FilterCardType.Monster, CardCode = RaId })],
            },
        ],
    };

    [Fact]
    public void AncientChant_Wording()
    {
        var c = AncientChant();
        Assert.Equal("You can banish this card from your GY; if you Tribute Summon \"The Winged Dragon of Ra\" this turn, its original ATK/DEF become the combined original ATK/DEF " +
                     "of the monsters Tributed for its Summon.", EffectTextGenerator.Generate(c, Pack(c, God(RaId, "The Winged Dragon of Ra", 0))));
        Lua(c);
    }

    [EngineFact]
    public void AncientChant_TheTributedMonstersStats()
    {
        // Ra (Level 10, 0 ATK) is Tribute Summoned with a 1500 and a 1600 ATK monster.
        new EngineScenario(AncientChant(), God(RaId, "The Winged Dragon of Ra", 0)).Card(Chant, 0, "grave").Card(RaId, 0, "hand").Card(Warrior, 0, "mzone").Card(Dragon, 0, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(Chant).Summon(RaId)
            .Check($"{Card(RaId, 0)}:GetBaseAttack()==3100 and {Card(RaId, 0)}:GetAttack()==3100 and {Card(RaId, 0)}:GetDefense()==2000")
            .Check(In(Chant, 0, "LOCATION_REMOVED"))
            .AssertPasses();
    }

    // ---------------------------------------------------------------- Blaze Cannon

    private static CardProject BlazeCannon() => new()
    {
        Card = new CardData { Id = Cannon, Name = "Blaze Cannon", Kind = CardKind.Spell, SpellKind = SpellKind.QuickPlay },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Cost = [B(CannotBeNegatedCost.BlockId)],
                Do = [B(GrantEffectsAction.BlockId).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, CardCode = RaId })],
            },
            new EffectEvent
            {
                Granted = true, When = B("when.while_face_up"),
                Do = [B("passive.immune", ("whose", "opponent")).With("cards", new CardFilter { ThisCard = true, Zones = Zone.MonsterZone })],
            },
            new EffectEvent
            {
                Granted = true,
                When = B("when.attack_declared", ("mode", "trigger"), ("how", "involving")).With("mine", new CardFilter { ThisCard = true }),
                Cost =
                [
                    B("cost.tribute", ("mode", "any")).With("cards", new CardFilter
                    {
                        Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true,
                        Requirements = [new CardRequirement { Property = "attacked_turn", Op = RequirementOp.IsNot }],
                    }),
                ],
                Do = [B(StatsFromValueAction.BlockId, ("stat", "atk"), ("value", "cost_orig_atk"), ("duration", "turn")).With("cards", new CardFilter { ThisCard = true })],
            },
            new EffectEvent
            {
                Granted = true,
                When = B("when.battle", ("mode", "trigger"), ("window", "after"), ("who", "attacks")),
                Do = [B("do.send_to_gy", ("mode", "all")).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
            },
        ],
    };

    [Fact]
    public void BlazeCannon_Wording()
    {
        var b = BlazeCannon();
        Assert.Equal("1 \"The Winged Dragon of Ra\" you control gains the following effects until the end of this turn. This card's activation and effect cannot be negated.\n" +
                     " ● This card is unaffected by your opponent's card effects.\n" +
                     " ● When an attack is declared involving this card: You can Tribute any number of other monsters that have not declared an attack this turn; " +
                     "this card gains ATK equal to the Tributed monsters' combined original ATK until the end of this turn.\n" +
                     " ● If this card attacks, after damage calculation: You can send all monsters your opponent controls to the GY.",
            EffectTextGenerator.Generate(b, Pack(b, God(RaId, "The Winged Dragon of Ra"))));
        var lua = Lua(b);
        Assert.Contains("function s.cc_grant(c,tc)", lua);
        Assert.Contains("tc:RegisterEffect(e3)", lua);
        Assert.DoesNotContain("\tc:RegisterEffect(e2)", lua);
    }

    [EngineFact]
    public void BlazeCannon_RaGainsTheEffects()
    {
        // Player 1's turn: Ra (4000) gets the effects, attacks a 1000 ATK Fiend, Tributes a 1500 Warrior for 1500 ATK, then sends
        // the opponent's other monster to the GY after damage calculation.
        new EngineScenario(BlazeCannon(), God(RaId, "The Winged Dragon of Ra")).Card(Cannon, 1, "hand").Card(RaId, 1, "mzone").Card(Warrior, 1, "mzone")
            .Card(Fiend, 0, "mzone", 2).Card(Warrior, 0, "deck", 5).Card(Dragon, 1, "deck", 5)
            .Activate(Cannon).Activate(RaId, 2).Attack(RaId).Turns(2)
            .ExpectActivations(3)
            // It attacked with 5500 ATK; once the turn is over its ATK and the effects are gone.
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==0 and Duel.GetLP(0)==8000-4500")
            .Check($"{Card(RaId, 1)}:GetAttack()==4000 and not {Card(RaId, 1)}:IsHasEffect(EFFECT_IMMUNE_EFFECT)")
            .Check(In(Warrior, 1, "LOCATION_GRAVE"))
            .AssertPasses();
    }

    // ---------------------------------------------------------------- Dangers of the Divine

    private static CardProject DangersOfTheDivine() => new()
    {
        Card = new CardData { Id = Dangers, Name = "Dangers of the Divine", Kind = CardKind.Spell },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do =
                [
                    B("do.send_to_gy", ("mode", "all"), ("spare", "true"))
                        .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })
                        .With("spare_cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, CardCode = RaId }),
                ],
            },
        ],
    };

    [Fact]
    public void Dangers_Wording()
    {
        var d = DangersOfTheDivine();
        Assert.Equal("Choose 1 \"The Winged Dragon of Ra\" you control and send all other monsters on the field to the GY.",
            EffectTextGenerator.Generate(d, Pack(d, God(RaId, "The Winged Dragon of Ra"))));
    }

    [EngineFact]
    public void Dangers_SendsAllButTheChosenRa()
    {
        new EngineScenario(DangersOfTheDivine(), God(RaId, "The Winged Dragon of Ra")).Card(Dangers, 0, "hand").Card(Warrior, 0, "mzone").Card(RaId, 0, "mzone").Card(Fiend, 1, "mzone", 2)
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(Dangers)
            .Check(In(RaId, 0, "LOCATION_MZONE") + " and Duel.GetFieldGroupCount(0,LOCATION_MZONE,LOCATION_MZONE)==1")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- The Breaking Ruin God

    private static CardProject Obelisk() => new()
    {
        Card = new CardData { Id = ObId, Name = "Obelisk the Tormentor", Frame = MonsterFrame.Effect, Race = CardRace.DivineBeast, Attribute = CardAttribute.Divine, Level = 10, Atk = 4000, Def = 4000 },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.ignition", ("zone", "monster_zone")),
                Cost = [B("cost.tribute", ("count", "2")).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true })],
                Do = [B("do.destroy", ("mode", "all")).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
            },
        ],
    };

    private static CardProject BreakingRuinGod() => new()
    {
        Card = new CardData { Id = Ruin, Name = "The Breaking Ruin God", Kind = CardKind.Spell, SpellKind = SpellKind.QuickPlay },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"), Limit = OncePerTurn.Hard,
                Do =
                [
                    B("do.special_summon").With("cards", Named(ObId, Zone.Hand | Zone.Graveyard)),
                    B("do.battle_rule", ("rule", "unaffected_opp"), ("target", "false")).With("cards", new CardFilter { That = CardRef.Affected, CardType = FilterCardType.Monster }).Linked(ActionLink.IfYouDo),
                ],
            },
            new EffectEvent
            {
                When = B("when.cards_detail", ("event", "tributed"), ("by", "cost"), ("player", "you"), ("count", "two_plus"), ("zone", "graveyard"))
                    .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
                If = [B("if.card_present", ("where", "you")).With("card", new CardFilter { CardCode = ObId })],
                Limit = OncePerTurn.Hard,
                Cost = [B("cost.this_card", ("how", "banish"))],
                Do =
                [
                    B("do.banish", ("mode", "all")).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Graveyard, CardType = FilterCardType.Monster }),
                    B("do.damage", ("amount", "500"), ("per", "per_previous")).Linked(ActionLink.IfYouDo),
                ],
            },
        ],
    };

    [Fact]
    public void BreakingRuinGod_Wording()
    {
        var r = BreakingRuinGod();
        var text = EffectTextGenerator.Generate(r, Pack(r, Obelisk()));
        Assert.StartsWith("Special Summon 1 \"Obelisk the Tormentor\" from your hand or GY, and if you do, it is unaffected by your opponent's card effects this turn. ", text);
        Assert.Contains("If you Tribute 2 or more monsters you control at the same time to activate your card or effect while you control \"Obelisk the Tormentor\": " +
                        "You can banish this card from your GY; banish all monsters from your opponent's GY, and if you do, inflict 500 damage to your opponent for each", text);
        Assert.Contains("eg:IsExists(s.evcards,2,nil,e,tp)", Lua(r));
    }

    /// <summary>Your opponent's monster in the hand: "When your opponent Special Summons a monster(s) (Quick Effect): You can destroy all monsters your opponent controls."</summary>
    private static CardProject Wiper() => new()
    {
        Card = new CardData { Id = Big, Name = "Summon Wiper", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.quick", ("timing", "summon_special"), ("zone", "hand")),
                Do = [B("do.destroy", ("mode", "all")).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
            },
        ],
    };

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void BreakingRuinGod_SummonsObelisk_UnaffectedThisTurn(bool unaffected)
    {
        var ruin = BreakingRuinGod();
        if (!unaffected) ruin.Effects[0].Do.RemoveAt(1);
        new EngineScenario(ruin, Obelisk(), Wiper()).Card(Ruin, 0, "hand").Card(ObId, 0, "grave").Card(Big, 1, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(Ruin).Activate(Big)
            .ExpectActivations(2)
            .Check(unaffected ? In(ObId, 0, "LOCATION_MZONE") : In(ObId, 0, "LOCATION_GRAVE")).AssertPasses();
    }

    [EngineFact]
    public void BreakingRuinGod_BanishesTheirGy_WhenYouTribute2ForACost()
    {
        new EngineScenario(BreakingRuinGod(), Obelisk()).Card(Ruin, 0, "grave").Card(ObId, 0, "mzone").Card(Warrior, 0, "mzone", 2).Card(Fiend, 1, "grave", 3).Card(Dragon, 1, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(ObId).Activate(Ruin)
            .ExpectActivations(2)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_REMOVED,0)>=3 and Duel.GetLP(1)==8000-500*Duel.GetFieldGroupCount(1,LOCATION_REMOVED,0)")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- Trap Trick (only 1 Trap Card)

    private static CardProject TrapLimit() => new()
    {
        Card = new CardData { Id = TrapLock, Name = "Trap Limit", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.gain_lp", ("amount", "100")), B("do.summon_restriction", ("what", "trap_one"))] }],
    };

    private static CardProject EndTrap() => new()
    {
        Card = new CardData { Id = TrapGain, Name = "End Phase Gain", Kind = CardKind.Trap },
        Effects = [new EffectEvent { When = B("when.activate", ("timing", "end_phase")), Do = [B("do.gain_lp", ("amount", "1000"))] }],
    };

    [Fact]
    public void TrapOne_Wording() =>
        Assert.Equal("Gain 100 LP, also you can only activate 1 Trap Card for the rest of this turn after this card resolves.", EffectTextGenerator.Generate(TrapLimit()));

    [EngineTheory]
    [InlineData(true, 9100)]
    [InlineData(false, 10000)]
    public void TrapOne_OnlyOneTrapCardAfterIt(bool limit, int lp)
    {
        new EngineScenario(TrapLimit(), EndTrap()).Card(TrapGain, 0, "szone", 2).Card(limit ? TrapLock : Spell, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(TrapLock).Activate(TrapGain, 2)
            .Check($"Duel.GetLP(0)=={lp}").AssertPasses();
    }

    // ---------------------------------------------------------------- The Revived Sky God (draw until 6)

    private static CardProject RevivedSkyGod() => new()
    {
        Card = new CardData { Id = SkyGod, Name = "The Revived Sky God", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.draw", ("who", "each"), ("count", "6"), ("until", "true"))] }],
    };

    [Fact]
    public void DrawUntil_Wording() =>
        Assert.Equal("Each player draws until they have 6 cards in their hand.", EffectTextGenerator.Generate(RevivedSkyGod()));

    [EngineFact]
    public void DrawUntil_EachPlayerHas6()
    {
        new EngineScenario(RevivedSkyGod()).Card(SkyGod, 0, "hand").Card(Fiend, 0, "hand", 2).Card(Fiend, 1, "hand").Card(Warrior, 0, "deck", 8).Card(Warrior, 1, "deck", 8)
            .Activate(SkyGod)
            // Player 1 also has its normal draw of the next turn.
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==6 and Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==7").AssertPasses();
    }

    // ---------------------------------------------------------------- not in the same Chain

    private static CardProject TwoEffects(bool sameChain) => new()
    {
        Card = new CardData { Id = Unify, Name = "Chain Pair", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects =
        [
            new EffectEvent { When = B("when.ignition", ("zone", "monster_zone")), Limit = OncePerTurn.Soft, SameChainGroup = sameChain ? 1 : 0, Do = [B("do.gain_lp", ("amount", "100"))] },
            new EffectEvent
            {
                When = B("when.quick", ("timing", "chain_either"), ("zone", "monster_zone")), Limit = OncePerTurn.Soft, SameChainGroup = sameChain ? 1 : 0,
                Do = [B("do.gain_lp", ("amount", "1000"))],
            },
        ],
    };

    [Fact]
    public void SameChain_Wording()
    {
        Assert.EndsWith(" You cannot activate both of this card's effects in the same Chain.", EffectTextGenerator.Generate(TwoEffects(true)));
        var named = TwoEffects(true);
        named.Effects[1].Limit = OncePerTurn.Hard;
        Assert.EndsWith(" You cannot activate these effects of \"Chain Pair\" in the same Chain.", EffectTextGenerator.Generate(named));
        Assert.Contains("e:GetHandler():RegisterFlagEffect(id+8701,RESET_CHAIN,0,1)", Lua(TwoEffects(true)));
    }

    [EngineTheory]
    [InlineData(true, 8100)]
    [InlineData(false, 9100)]
    public void SameChain_TheSecondCantRespondToTheFirst(bool sameChain, int lp)
    {
        new EngineScenario(TwoEffects(sameChain)).Card(Unify, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(Unify, 2)
            .Check($"Duel.GetLP(0)=={lp}").AssertPasses();
    }
}
