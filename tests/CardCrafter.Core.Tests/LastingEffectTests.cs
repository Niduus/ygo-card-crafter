using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Lasting effects and turn-long rules: "this card cannot attack the turn you activate this effect", "you cannot conduct
/// your Battle Phase the turn you activate this card", the Ancient Gear lock, Pendulum Scale locks, double Tributes,
/// "but banish it when it leaves the field", and the rules an effect gives its target, played in the engine.
/// </summary>
public class LastingEffectTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static BlockInstance With(BlockInstance b, string key, CardFilter f) { b.Filters[key] = f; return b; }

    private static CardProject Monster(long id, int atk, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = "Lasting " + id, Frame = MonsterFrame.Effect, Level = 4, Atk = atk, Def = 500, Race = CardRace.Machine, Attribute = CardAttribute.Earth },
        Effects = [.. effects],
    };

    private static string Find(long code, int player, string loc) => $"Duel.GetFirstMatchingCard(Card.IsCode,{player},{loc},0,nil,{code})";

    // ---------------------------------------------------------------- costs

    [EngineTheory]
    [InlineData("attack")]
    [InlineData("declare")]
    [InlineData("direct")]
    public void ThisCardCannotAttack_TheTurnItsEffectIsActivated(string rule)
    {
        // Player 1's monster burns for 400, then can't attack (directly) that turn: no battle damage.
        var archer = Monster(100140001, 1800, new EffectEvent
        {
            When = B("when.ignition"), Cost = [B(ThisCardTurnCost.BlockId, ("rule", rule))], Do = [B("do.damage", ("amount", "400"))],
        });
        new EngineScenario(archer).Card(archer.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(archer.Card.Id).Attack(archer.Card.Id).Turns(2).ExpectActivations(1)
            .Check("Duel.GetLP(0)==7600").AssertPasses();
        var wording = rule switch { "declare" => "cannot declare an attack", "direct" => "cannot attack directly", _ => "cannot attack" };
        Assert.Equal($"You can inflict 400 damage to your opponent. This card {wording} the turn you activate this effect.", EffectTextGenerator.Generate(archer));
        Assert.Contains("EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_OATH", ScriptCompiler.Compile(archer).Lua);
    }

    [EngineFact]
    public void WithoutTheCost_TheMonsterStillAttacks()
    {
        var archer = Monster(100140002, 1800, new EffectEvent { When = B("when.ignition"), Do = [B("do.damage", ("amount", "400"))] });
        new EngineScenario(archer).Card(archer.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(archer.Card.Id).Attack(archer.Card.Id).Turns(2)
            .Check("Duel.GetLP(0)==5800").AssertPasses();
    }

    [EngineFact]
    public void ThisCardCannotAttack_AfterItAttacked_TheEffectCannotBeActivated()
    {
        // The check the cost makes: the effect can't be used once this card has declared an attack this turn.
        var archer = Monster(100140003, 1800, new EffectEvent
        {
            When = B("when.ignition"), Cost = [B(ThisCardTurnCost.BlockId)], Do = [B("do.damage", ("amount", "400"))],
        });
        Assert.Contains("e:GetHandler():GetAttackAnnouncedCount()==0", ScriptCompiler.Compile(archer).Lua);
        var direct = Monster(100140004, 1800, new EffectEvent { When = B("when.ignition"), Cost = [B(ThisCardTurnCost.BlockId, ("rule", "direct"))], Do = [B("do.draw")] });
        Assert.Contains("not e:GetHandler():IsDirectAttacked()", ScriptCompiler.Compile(direct).Lua);
    }

    [EngineFact]
    public void NoBattlePhase_TheTurnYouActivateThisCard()
    {
        var spell = new CardProject
        {
            Card = new CardData { Id = 100140010, Name = "Quiet Draw", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = B("when.activate"), Cost = [B("cost.summon_restriction", ("what", "bp"), ("when", "whole_turn"))], Do = [B("do.draw")] }],
        };
        new EngineScenario(spell).Card(spell.Card.Id, 1, "hand").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(spell.Card.Id).Attack(Warrior).Turns(2).ExpectActivations(1)
            .Check("Duel.GetLP(0)==8000").Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==2").AssertPasses();
        Assert.Equal("Draw 1 card. You cannot conduct your Battle Phase the turn you activate this card.", EffectTextGenerator.Generate(spell));
        Assert.Contains("Duel.GetActivityCount(tp,ACTIVITY_BATTLE_PHASE)==0", ScriptCompiler.Compile(spell).Lua);
    }

    [EngineTheory]
    [InlineData(Warrior, false)]
    [InlineData(Fiend, true)]
    public void NoNormalOrSpecialSummons_TheTurnYouActivateThis_ExceptDarkMonsters(long monster, bool summoned)
    {
        var spell = new CardProject
        {
            Card = new CardData { Id = 100140011, Name = "Dark Draw", Kind = CardKind.Spell },
            Effects =
            [
                new EffectEvent
                {
                    When = B("when.activate"),
                    Cost = [With(B("cost.summon_restriction", ("what", "normal_special"), ("when", "whole_turn")), "cards", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Dark })],
                    Do = [B("do.draw")],
                },
            ],
        };
        new EngineScenario(spell).Card(spell.Card.Id, 0, "hand").Card(monster, 0, "hand").Card(Warrior, 0, "deck", 4)
            .Activate(spell.Card.Id).Summon(monster).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{monster})=={(summoned ? "true" : "false")}").AssertPasses();
        Assert.Equal("Draw 1 card. You cannot Normal or Special Summon monsters the turn you activate this card, except DARK monsters.", EffectTextGenerator.Generate(spell));
        var lua = ScriptCompiler.Compile(spell).Lua;
        Assert.Contains("Duel.GetCustomActivityCount(id,tp,ACTIVITY_SUMMON)==0", lua);
        Assert.Contains("EFFECT_CANNOT_SUMMON", lua);
    }

    // ---------------------------------------------------------------- locks

    private static CardProject AttackTrap(long id) => new()
    {
        Card = new CardData { Id = id, Name = "Answer " + id, Kind = CardKind.Trap },
        Effects = [new EffectEvent { When = B("when.activate", ("timing", "attack")), Do = [B("do.gain_lp", ("amount", "1000"))] }],
    };

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AncientGear_IfThisCardAttacks_TheOpponentCannotActivateSpellTrapCards(bool locked)
    {
        var gear = Monster(100140020, 1800);
        if (locked)
            gear.Effects.Add(new EffectEvent
            {
                When = B("when.while_face_up"), If = [B(BattlingCondition.BlockId, ("how", "attacks"))],
                Do = [B("passive.lock", ("who", "opponent"), ("what", "activate_cards"))],
            });
        var trap = AttackTrap(100140021);
        new EngineScenario(gear, trap).Card(gear.Card.Id, 1, "mzone").Card(trap.Card.Id, 0, "szone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(trap.Card.Id).Attack(gear.Card.Id).Turns(2).ExpectActivations(locked ? 0 : 1)
            .Check($"Duel.GetLP(0)=={(locked ? 6200 : 7200)}").AssertPasses();
        if (locked)
            Assert.Equal("If this card attacks, your opponent cannot activate Spell/Trap Cards until the end of the Damage Step.", EffectTextGenerator.Describe(gear.Effects[0], gear.Card));
    }

    [EngineTheory]
    [InlineData("activate_trap_cards", false)]
    [InlineData("activate_spell_cards", true)]
    [InlineData("activate_gy", true)]
    public void YourOpponentCannotActivateTrapCards(string what, bool trapActivates)
    {
        var wolf = Monster(100140022, 1800, new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.lock", ("who", "opponent"), ("what", what))] });
        var trap = AttackTrap(100140023);
        new EngineScenario(wolf, trap).Card(wolf.Card.Id, 1, "mzone").Card(trap.Card.Id, 0, "szone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(trap.Card.Id).Attack(wolf.Card.Id).Turns(2).ExpectActivations(trapActivates ? 1 : 0).AssertPasses();
    }

    [EngineTheory]
    [InlineData("activate_gy", 8000)]
    [InlineData("activate_trap_cards", 8100)]
    public void NeitherPlayerCanActivateEffectsInTheGY(string what, int lp)
    {
        var dweller = Monster(100140024, 1000, new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.lock", ("who", "both"), ("what", what))] });
        var ghost = Monster(100140025, 1000, new EffectEvent { When = B("when.ignition", ("zone", "graveyard")), Do = [B("do.gain_lp", ("amount", "100"))] });
        new EngineScenario(dweller, ghost).Card(dweller.Card.Id, 0, "mzone").Card(ghost.Card.Id, 0, "grave").Card(Warrior, 0, "deck", 4)
            .Activate(ghost.Card.Id).Check($"Duel.GetLP(0)=={lp}").AssertPasses();
        if (what == "activate_gy")
            Assert.Equal("Neither player can activate the effects of cards in the GY.", EffectTextGenerator.Generate(dweller));
    }

    [EngineFact]
    public void PendulumScale_YouCannotPendulumSummon_ExceptDarkMonsters()
    {
        const long scaleId = 100140030;
        var scale = new CardProject
        {
            Card = new CardData { Id = scaleId, Name = "Scale Lock", Frame = MonsterFrame.Effect, Abilities = MonsterAbility.Pendulum, Level = 4, LeftScale = 1, RightScale = 1, Atk = 1000, Def = 1000 },
            Effects =
            [
                new EffectEvent
                {
                    When = B("when.while_face_up", ("zone", "pendulum_zone")),
                    Do = [With(B("passive.lock", ("who", "you"), ("what", "pendulum")), "cards", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Dark })],
                },
            ],
        };
        var rigger = RequirementMatrix.RiggerCard($"\tDuel.MoveToField(find({scaleId},LOCATION_HAND,tp),tp,tp,LOCATION_PZONE,POS_FACEUP,true)");
        string Can(string sumtype, long code) => $"Duel.IsPlayerCanSpecialSummon(0,{sumtype},POS_FACEUP,0,{Find(code, 0, "LOCATION_DECK")})";
        new EngineScenario(scale, rigger).Card(scaleId, 0, "hand").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "deck", 3).Card(Fiend, 0, "deck", 3)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_PZONE,0,1,nil,{scaleId})")
            .Check("not " + Can("SUMMON_TYPE_PENDULUM", Warrior)).Check(Can("SUMMON_TYPE_PENDULUM", Fiend)).Check(Can("0", Warrior))
            .AssertPasses();
        Assert.Equal("You cannot Pendulum Summon monsters, except DARK monsters. This effect cannot be negated.", EffectTextGenerator.Describe(scale.Effects[0], scale.Card));
        Assert.Contains("EFFECT_FLAG_PLAYER_TARGET+EFFECT_FLAG_CANNOT_DISABLE+EFFECT_FLAG_CAN_FORBIDDEN", ScriptCompiler.Compile(scale).Lua);
    }

    // ---------------------------------------------------------------- this card

    [EngineTheory]
    [InlineData(CardRace.Dragon, true)]
    [InlineData(CardRace.Warrior, false)]
    public void DoubleTribute_OneMonsterTributesForALevel7Dragon(CardRace race, bool summoned)
    {
        var samsara = Monster(100140040, 1000, new EffectEvent
        {
            When = B("when.while_face_up"), Do = [With(B(DoubleTributePassive.BlockId), "cards", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Dragon })],
        });
        var big = new CardProject { Card = new CardData { Id = 100140041, Name = "Big One", Frame = MonsterFrame.Normal, Level = 7, Atk = 2500, Def = 2000, Race = race, Attribute = CardAttribute.Light } };
        new EngineScenario(samsara, big).Card(samsara.Card.Id, 0, "mzone").Card(big.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 4)
            .Summon(big.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{big.Card.Id})=={(summoned ? "true" : "false")}")
            .AssertPasses();
        Assert.Equal("This card can be treated as 2 Tributes for the Tribute Summon of a Dragon monster.", EffectTextGenerator.Describe(samsara.Effects[0], samsara.Card));
    }

    [EngineTheory]
    [InlineData(Fiend, true)]
    [InlineData(Warrior, false)]
    public void CannotBeDestroyedByBattle_WithADarkMonster(long attacker, bool survives)
    {
        var reese = Monster(100140050, 500, new EffectEvent
        {
            When = B("when.while_face_up"),
            Do = [With(B("passive.indestructible", ("by", "battle_with")), "with", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Dark })],
        });
        new EngineScenario(reese).Card(reese.Card.Id, 0, "mzone").Card(attacker, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(attacker).Turns(2)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{reese.Card.Id})=={(survives ? "true" : "false")}")
            .AssertPasses();
        Assert.Equal("This card cannot be destroyed by battle with a DARK monster.", EffectTextGenerator.Describe(reese.Effects[0], reese.Card));
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void SpecialSummonThisCard_ButBanishItWhenItLeavesTheField(bool banish)
    {
        const long id = 100140060;
        var quik = Monster(id, 1000, new EffectEvent
        {
            When = B("when.ignition", ("zone", "graveyard")), Do = [B("do.special_summon_self", ("banish", banish ? "true" : "false"))],
        });
        // At the next Standby Phase the rigger destroys it.
        var rigger = RequirementMatrix.RiggerCard($"\tlocal c=find({id},LOCATION_MZONE,tp)\n\tif c then Duel.Destroy(c,REASON_EFFECT) end");
        new EngineScenario(quik, rigger).Card(id, 0, "grave").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(id).Turns(2).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,{(banish ? "LOCATION_REMOVED" : "LOCATION_GRAVE")},0,1,nil,{id})")
            .AssertPasses();
        if (banish)
            Assert.Equal("You can Special Summon this card from your GY, but banish it when it leaves the field.", EffectTextGenerator.Describe(quik.Effects[0], quik.Card));
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpToTwoAttacksOnMonsters(bool twice)
    {
        var wagon = Monster(100140080, 3000);
        if (twice) wagon.Effects.Add(new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.battle_ability", ("ability", "extra_monster"))] });
        new EngineScenario(wagon).Card(wagon.Card.Id, 1, "mzone").Card(Warrior, 0, "mzone", 2).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(wagon.Card.Id).Attack(wagon.Card.Id).Turns(2)
            .Check($"Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)=={(twice ? 0 : 1)}").Check($"Duel.GetLP(0)=={(twice ? 5000 : 6500)}").AssertPasses();
        if (twice) Assert.Equal("This card can make up to 2 attacks on monsters during each Battle Phase.", EffectTextGenerator.Generate(wagon));
    }

    [EngineFact]
    public void BattleDamage_NoneToTheOpponent_Reflected_AndThreeAttacks()
    {
        // Player 1's monster attacks directly but inflicts nothing.
        var soft = Monster(100140085, 1000, new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.self_rule", ("rule", "opp_no_battle_damage"))] });
        new EngineScenario(soft).Card(soft.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(soft.Card.Id).Turns(2).Check("Duel.GetLP(0)==8000").AssertPasses();
        // Player 0's weak monster is attacked: the 1000 damage goes to player 1 instead.
        var mirror = Monster(100140086, 500, new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.self_rule", ("rule", "reflect_battle"))] });
        new EngineScenario(mirror).Card(mirror.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(Warrior).Turns(2).Check("Duel.GetLP(0)==8000").Check("Duel.GetLP(1)==7000").AssertPasses();
        // Three direct attacks.
        var hydra = Monster(100140087, 1000, new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.battle_ability", ("ability", "extra2"))] });
        new EngineScenario(hydra).Card(hydra.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(hydra.Card.Id).Attack(hydra.Card.Id).Attack(hydra.Card.Id).Turns(2).Check("Duel.GetLP(0)==5000").AssertPasses();
    }

    [EngineTheory]
    [InlineData("substitute_battle", true)]
    [InlineData("substitute", true)]
    [InlineData("pierce", false)]
    public void EquipSpell_IsDestroyedInsteadOfTheEquippedMonster(string rule, bool survives)
    {
        var shield = new CardProject
        {
            Card = new CardData { Id = 100140088, Name = "Shield Equip", Kind = CardKind.Spell, SpellKind = SpellKind.Equip },
            Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.equipped", ("rule", rule))] }],
        };
        new EngineScenario(shield).Card(shield.Card.Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(shield.Card.Id).Attack(Dragon).Turns(2).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Warrior})=={(survives ? "true" : "false")}")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{shield.Card.Id})").AssertPasses();
        if (rule == "substitute_battle")
            Assert.Equal("If the equipped monster would be destroyed by battle, destroy this card instead.", EffectTextGenerator.Describe(shield.Effects[0], shield.Card));
    }

    [EngineTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CannotBeNormalSummoned_UnlessYouControlAWarrior(bool warrior, bool summoned)
    {
        var lantern = Monster(100140089, 1000, new EffectEvent
        {
            When = B("when.while_face_up"),
            If = [With(B("if.no_cards"), "cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Warrior, FaceUpOnly = true })],
            Do = [B(CannotBeSummonedPassive.BlockId)],
        });
        var s = new EngineScenario(lantern).Card(lantern.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 4);
        if (warrior) s.Card(Warrior, 0, "mzone");
        s.Summon(lantern.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{lantern.Card.Id})=={(summoned ? "true" : "false")}").AssertPasses();
        Assert.Equal("If you control no face-up Warrior monsters, this card cannot be Normal Summoned.", EffectTextGenerator.Generate(lantern));
    }

    [EngineTheory]
    [InlineData("defense_attack", 6000)]
    [InlineData("defense_attack_atk", 7500)]
    public void AttacksWhileInDefensePosition(string ability, int lp)
    {
        const long id = 100140081;
        var shogun = Monster(id, 500, new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.battle_ability", ("ability", ability))] });
        shogun.Card.Def = 2000;
        // Turned to Defense Position at the first Standby Phase; it attacks directly on player 1's turn.
        var rigger = RequirementMatrix.RiggerCard($"\tlocal c=find({id},LOCATION_MZONE,1)\n\tif c and c:IsAttackPos() then Duel.ChangePosition(c,POS_FACEUP_DEFENSE) end");
        new EngineScenario(shogun, rigger).Card(id, 1, "mzone").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(id).Turns(2).Check($"Duel.GetLP(0)=={lp}").AssertPasses();
    }

    [EngineTheory]
    [InlineData("grave", false)]
    [InlineData("destroy", true)]
    public void IfTheConditionHolds_SendThisCardToTheGY(string how, bool destroyed)
    {
        const long id = 100140082;
        var cat = Monster(id, 1000, new EffectEvent
        {
            When = B("when.while_face_up"), If = [B("if.hand_count", ("who", "you"), ("cmp", "le"), ("count", "0"))], Do = [B(SelfDestroyPassive.BlockId, ("how", how))],
        });
        new EngineScenario(cat).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Check($"{Find(id, 0, "LOCATION_GRAVE")}:IsReason(REASON_DESTROY)=={(destroyed ? "true" : "false")}").AssertPasses();
        if (how == "grave") Assert.Equal("If you have no cards in your hand, send this card to the GY.", EffectTextGenerator.Generate(cat));
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATrapCanBeActivatedTheTurnItWasSet(bool allowed)
    {
        const long id = 100140083;
        var trap = new CardProject
        {
            Card = new CardData { Id = id, Name = "Quick Set", Kind = CardKind.Trap },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.gain_lp", ("amount", "1000"))] }],
        };
        if (allowed)
            trap.Effects.Add(new EffectEvent { When = B("when.while_face_up"), If = [B("if.cards_exist", ("count", "1"))], Do = [B(ActivateSetTurnPassive.BlockId)] });
        // The rigger Sets the Trap from the hand at the first Standby Phase.
        var rigger = RequirementMatrix.RiggerCard($"\tlocal c=find({id},LOCATION_HAND,tp)\n\tif c then Duel.SSet(tp,c) end");
        new EngineScenario(trap, rigger).Card(id, 0, "hand").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(id).ExpectActivations(allowed ? 1 : 0).Check($"Duel.GetLP(0)=={(allowed ? 9000 : 8000)}").AssertPasses();
        if (allowed)
            Assert.Equal("If you control a face-up monster, you can activate this card the turn it was Set.", EffectTextGenerator.Describe(trap.Effects[1], trap.Card));
    }

    [EngineFact]
    public void MustAttack_AndDirectAttacks_ForMonstersOnTheField()
    {
        var crawler = Monster(100140084, 1500, new EffectEvent
        {
            When = B("when.while_face_up"),
            Do = [With(B("passive.cards_rule", ("rule", "must_attack")), "cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
        }, new EffectEvent
        {
            When = B("when.while_face_up"),
            Do = [With(B("passive.cards_rule", ("rule", "direct")), "cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Warrior })],
        });
        new EngineScenario(crawler).Card(crawler.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Check($"{Find(Fiend, 1, "LOCATION_MZONE")}:IsHasEffect(EFFECT_MUST_ATTACK)~=nil")
            .Check($"{Find(Warrior, 0, "LOCATION_MZONE")}:IsHasEffect(EFFECT_DIRECT_ATTACK)~=nil")
            .Check($"{Find(crawler.Card.Id, 0, "LOCATION_MZONE")}:IsHasEffect(EFFECT_DIRECT_ATTACK)==nil").AssertPasses();
        Assert.Equal("Monsters your opponent controls must attack, if able. Warrior monsters you control can attack directly.", EffectTextGenerator.Generate(crawler));
    }

    [EngineTheory]
    [InlineData("opp_activated_monster", true)]
    [InlineData("other_activated", true)]
    [InlineData("opp_spelltrap", false)]
    public void UnaffectedByActivatedMonsterEffects(string whose, bool survives)
    {
        var guard = Monster(100140090, 1000, new EffectEvent { When = B("when.while_face_up"), Do = [B("passive.immune", ("whose", whose))] });
        // Player 1's monster destroys every monster player 0 controls with an activated effect.
        var bomber = Monster(100140091, 1000, new EffectEvent
        {
            When = B("when.ignition"), Do = [With(B("do.destroy", ("mode", "all")), "cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
        });
        new EngineScenario(guard, bomber).Card(guard.Card.Id, 0, "mzone").Card(bomber.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(bomber.Card.Id).Turns(2).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{guard.Card.Id})=={(survives ? "true" : "false")}").AssertPasses();
    }

    [EngineFact]
    public void WhileFaceUpOnTheField_ThisCardIsAlsoDark()
    {
        var dragon = Monster(100140092, 2800, new EffectEvent
        {
            When = B("when.while_face_up", ("zone", "monster_zone")),
            Do = [With(B(TreatedAsPassive.BlockId, ("how", "also"), ("where", "face_up")), "as", new CardFilter { CardType = FilterCardType.Monster, Attribute = CardAttribute.Dark })],
        });
        dragon.Card.Attribute = CardAttribute.Light;
        new EngineScenario(dragon).Card(dragon.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Check($"{Find(dragon.Card.Id, 0, "LOCATION_MZONE")}:IsAttribute(ATTRIBUTE_DARK)")
            .Check($"{Find(dragon.Card.Id, 0, "LOCATION_MZONE")}:IsAttribute(ATTRIBUTE_LIGHT)").AssertPasses();
        Assert.Equal("While face-up on the field, this card is also DARK-Attribute.", EffectTextGenerator.Generate(dragon));
        var lua = ScriptCompiler.Compile(dragon).Lua;
        Assert.Contains("SetProperty(EFFECT_FLAG_SINGLE_RANGE)", lua);
        Assert.DoesNotContain("CANNOT_DISABLE", lua);
        // Light and Darkness Dragon reads into it.
        if (Script(47297616) is not null)
            Assert.Equal("face_up", Effect(47297616, Card(100140093, 2800, 8), TreatedAsPassive.BlockId).Do[0].Values["where"]);
    }

    // ---------------------------------------------------------------- the target

    [EngineTheory]
    [InlineData("cannot_be_destroyed", true)]
    [InlineData("cannot_be_destroyed_both", true)]
    [InlineData("pierce", false)]
    public void TheTarget_CannotBeDestroyedByBattleThisTurn(string rule, bool survives)
    {
        // Player 1's 1000 ATK monster protects itself, then attacks a 1500 ATK Warrior.
        var weak = Monster(100140070, 1000, new EffectEvent
        {
            When = B("when.ignition"),
            Do = [With(B("do.battle_rule", ("rule", rule), ("target", "true")), "cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true })],
        });
        new EngineScenario(weak).Card(weak.Card.Id, 1, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(weak.Card.Id).Attack(weak.Card.Id).Turns(2).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{weak.Card.Id})=={(survives ? "true" : "false")}")
            .Check("Duel.GetLP(1)==7500").AssertPasses();
    }

    [EngineTheory]
    [InlineData("cannot_declare", 8000)]
    [InlineData("pierce", 7000)]
    public void TheTarget_CannotDeclareAnAttackThisTurn(string rule, int lp)
    {
        var weak = Monster(100140071, 1000, new EffectEvent
        {
            When = B("when.ignition"),
            Do = [With(B("do.battle_rule", ("rule", rule), ("target", "true")), "cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true })],
        });
        new EngineScenario(weak).Card(weak.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(weak.Card.Id).Attack(weak.Card.Id).Turns(2).ExpectActivations(1).Check($"Duel.GetLP(0)=={lp}").AssertPasses();
    }

    /// <summary>
    /// The giver puts the rule on the Warrior and gains 100 LP; only then (8100 LP) can the second card try to destroy
    /// Warriors you control: all of them (unaffected), or a targeted one (untargetable).
    /// </summary>
    [EngineTheory]
    [InlineData("unaffected_other", false, true)]
    [InlineData("pierce", false, false)]
    [InlineData("cannot_be_targeted_any", true, true)]
    [InlineData("pierce", true, false)]
    public void TheTarget_IsUnaffected_OrCannotBeTargeted_ThisTurn(string rule, bool targeted, bool survives)
    {
        var mine = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, Race = CardRace.Warrior };
        var giver = Monster(100140072, 1000, new EffectEvent
        {
            When = B("when.ignition"), Do = [With(B("do.battle_rule", ("rule", rule), ("target", "true")), "cards", mine), B("do.gain_lp", ("amount", "100"))],
        });
        var warriors = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Warrior };
        var destroy = targeted ? B("do.destroy", ("target", "true")) : B("do.destroy", ("mode", "all"));
        var destroyer = Monster(100140073, 1000, new EffectEvent
        {
            When = B("when.ignition"), If = [B("if.life_points", ("who", "you"), ("cmp", "ge"), ("amount", "8100"))], Do = [With(destroy, "cards", warriors)],
        });
        new EngineScenario(giver, destroyer).Card(giver.Card.Id, 0, "mzone").Card(destroyer.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(giver.Card.Id).Activate(destroyer.Card.Id).ExpectActivations(targeted && survives ? 1 : 2)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Warrior})=={(survives ? "true" : "false")}").AssertPasses();
    }

    [EngineTheory]
    [InlineData("tuner", 9100)]
    [InlineData("pierce", 8100)]
    public void TheTarget_IsTreatedAsATunerThisTurn(string rule, int lp)
    {
        var giver = Monster(100140074, 1000, new EffectEvent
        {
            When = B("when.ignition"),
            Do = [With(B("do.battle_rule", ("rule", rule), ("target", "true")), "cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, Race = CardRace.Warrior }),
                B("do.gain_lp", ("amount", "100"))],
        });
        // Once the giver resolved (8100 LP), a Tuner on the field lets the checker gain 1000 LP.
        var checker = Monster(100140075, 1000, new EffectEvent
        {
            When = B("when.ignition"),
            If = [B("if.life_points", ("who", "you"), ("cmp", "ge"), ("amount", "8100")),
                With(B("if.cards_exist", ("count", "1")), "cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Tuner })],
            Do = [B("do.gain_lp", ("amount", "1000"))],
        });
        new EngineScenario(giver, checker).Card(giver.Card.Id, 0, "mzone").Card(checker.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(giver.Card.Id).Activate(checker.Card.Id).Check($"Duel.GetLP(0)=={lp}").AssertPasses();
    }

    // ---------------------------------------------------------------- official scripts

    private static string? Script(long id) =>
        EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{id}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{id}.lua")) : null;

    private static CardData Card(long id, int atk = 1500, int level = 4, CardKind kind = CardKind.Monster, SpellKind spell = SpellKind.Normal) =>
        new() { Id = id, Name = "Official " + id, Kind = kind, SpellKind = spell, Frame = MonsterFrame.Effect, Level = level, Atk = atk, Def = 1000, Race = CardRace.Machine, Attribute = CardAttribute.Earth };

    /// <summary>The effect of an official script that uses <paramref name="blockId"/>, translated for <paramref name="card"/> and compiling cleanly.</summary>
    private static EffectEvent Effect(long official, CardData card, string blockId)
    {
        var result = ScriptTranslator.Translate(Script(official)!, official, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(e => e.Do.Concat(e.Cost).Concat(e.If).Any(b => b.BlockId == blockId));
        Assert.True(effect is not null, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var compiled = ScriptCompiler.Compile(new CardProject { Card = card, Effects = [effect] });
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        Assert.DoesNotContain(ScriptChecker.Check(compiled.Lua), d => d.Severity == Severity.Error);
        return effect;
    }

    [EngineFact]
    public void VolcanicSlicer_And_SuperConductorTyranno_CannotAttackTheTurnTheyBurn()
    {
        if (Script(17415895) is null || Script(85520851) is null) return;
        var slicer = Card(100140100, 1800);
        var burn = Effect(17415895, slicer, ThisCardTurnCost.BlockId);
        Assert.Equal("attack", burn.Cost.Single(b => b.BlockId == ThisCardTurnCost.BlockId).Values["rule"]);
        Assert.Equal("Once per turn: You can inflict 500 damage to your opponent. This card cannot attack the turn you activate this effect.", EffectTextGenerator.Describe(burn, slicer));
        new EngineScenario(new CardProject { Card = slicer, Effects = [burn] }).Card(slicer.Id, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(slicer.Id).Attack(slicer.Id).Turns(2).ExpectActivations(1).Check("Duel.GetLP(0)==7500").AssertPasses();
        var tyranno = Effect(85520851, Card(100140101, 3300, 8), ThisCardTurnCost.BlockId);
        Assert.Equal(["cost.tribute", ThisCardTurnCost.BlockId], tyranno.Cost.Select(b => b.BlockId));
        Assert.Equal("declare", tyranno.Cost[1].Values["rule"]);
        // Fairy Archer checks nothing before it is activated: as the block always does, it stays script... unless the check is there.
        if (Script(3814632) is not null)
            Assert.DoesNotContain(ScriptTranslator.Translate(Script(3814632)!, 3814632, Card(100140102)).Pieces.SelectMany(p => p.Effects), e => e.Cost.Any(b => b.BlockId == ThisCardTurnCost.BlockId));
    }

    [EngineFact]
    public void ReinforceTruth_NoBattlePhaseTheTurnItIsActivated()
    {
        if (Script(10118318) is null) return;
        var card = Card(100140110, kind: CardKind.Spell);
        var effect = Effect(10118318, card, "cost.summon_restriction");
        var cost = effect.Cost.Single();
        Assert.Equal(("bp", "whole_turn"), (cost.Values["what"], cost.Values["when"]));
        Assert.EndsWith("You cannot conduct your Battle Phase the turn you activate this card.", EffectTextGenerator.Describe(effect, card));
    }

    [EngineFact]
    public void AncientGearGolem_LocksSpellTrapCardsWhileItAttacks()
    {
        if (Script(83104731) is null) return;
        var golem = Card(100140120, 3000, 8);
        var effect = Effect(83104731, golem, BattlingCondition.BlockId);
        Assert.Equal("attacks", effect.If.Single().Values["how"]);
        Assert.Equal(("opponent", "activate_cards"), (effect.Do.Single().Values["who"], effect.Do.Single().Values["what"]));
        var trap = AttackTrap(100140121);
        new EngineScenario(new CardProject { Card = golem, Effects = [effect] }, trap).Card(golem.Id, 1, "mzone").Card(trap.Card.Id, 0, "szone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(trap.Card.Id).Attack(golem.Id).Turns(2).ExpectActivations(0).Check("Duel.GetLP(0)==5000").AssertPasses();
        // "If this card battles" (Gem-Knight Citrine) locks every card and effect.
        if (Script(67985943) is not null)
        {
            var citrine = Effect(67985943, Card(100140122, 2200, 7), BattlingCondition.BlockId);
            Assert.Equal(("battles", "activate_any"), (citrine.If.Single().Values["how"], citrine.Do.Single().Values["what"]));
        }
    }

    [EngineFact]
    public void PendulumScales_YouCannotPendulumSummonExceptTheirArchetype()
    {
        if (Script(55106249) is null || Script(23166823) is null) return;
        var deskbot = Effect(55106249, Card(100140130), "passive.lock");
        Assert.Equal(("you", "pendulum", "pendulum_zone"), (deskbot.Do[0].Values["who"], deskbot.Do[0].Values["what"], deskbot.When!.Values["zone"]));
        Assert.Equal(0xab, deskbot.Do[0].Filters["cards"].Archetype);
        // "if c:IsSetCard(0xb5,0xc4) then return false end": either archetype is spared.
        var tamer = Effect(23166823, Card(100140131), "passive.lock");
        Assert.Equal((0xb5, 0xc4), (tamer.Do[0].Filters["cards"].Archetype, tamer.Do[0].Filters["cards"].Archetype2));
        Assert.Contains("(c:IsSetCard(0xb5) or c:IsSetCard(0xc4))", ScriptCompiler.Compile(new CardProject { Card = Card(100140131), Effects = [tamer] }).Lua);
        // Older scales that read "not forbidden" as a condition stay script.
        if (Script(42880485) is not null)
            Assert.DoesNotContain(ScriptTranslator.Translate(Script(42880485)!, 42880485, Card(100140132)).Pieces.SelectMany(p => p.Effects), e => e.Do.Any(b => b.Values.GetValueOrDefault("what") == "pendulum"));
    }

    [EngineFact]
    public void DoubleTributes_EquipDef_AndBattleSafety_ReadFromOfficialScripts()
    {
        if (Script(15090429) is null || Script(18937875) is null || Script(30276969) is null || Script(96891787) is null) return;
        var prodigy = Effect(15090429, Card(100140140), DoubleTributePassive.BlockId);
        Assert.Equal(CardAttribute.Wind, prodigy.Do[0].Filters["cards"].Attribute);
        var spear = Effect(18937875, Card(100140141, kind: CardKind.Spell, spell: SpellKind.Equip), "passive.equipped");
        Assert.True(spear.Do[0].Values["rule"] is "gain_atk" or "lose_def");
        var defPart = ScriptTranslator.Translate(Script(18937875)!, 18937875, Card(100140141, kind: CardKind.Spell, spell: SpellKind.Equip))
            .Pieces.SelectMany(p => p.Effects).Single(e => e.Do.Any(b => b.Values.GetValueOrDefault("rule") == "lose_def"));
        Assert.Equal("200", defPart.Do[0].Values["amount"]);
        var reese = Effect(30276969, Card(100140142, 100, 2), "passive.indestructible");
        Assert.Equal(("battle_with", 4), (reese.Do[0].Values["by"], reese.Do[0].Filters["with"].LevelMin));
        Assert.Equal("This card cannot be destroyed by battle with a Level 4 or higher monster.", EffectTextGenerator.Describe(reese, Card(100140142)));
        var theo = Effect(96891787, Card(100140143), "passive.indestructible");
        Assert.Equal(FilterSummoned.FromExtra, theo.Do[0].Filters["with"].Summoned);
    }

    [EngineFact]
    public void SpyralQuikFix_SpecialSummonsItselfButIsBanishedWhenItLeaves()
    {
        if (Script(78080961) is null) return;
        var card = Card(100140150);
        var effect = Effect(78080961, card, "do.special_summon_self");
        Assert.Equal("true", effect.Do.Single(b => b.BlockId == "do.special_summon_self").Values["banish"]);
        Assert.Contains("but banish it when it leaves the field", EffectTextGenerator.Describe(effect, card));
        Assert.Contains("RESET_EVENT+RESETS_REDIRECT", ScriptCompiler.Compile(new CardProject { Card = card, Effects = [effect] }).Lua);
    }

    [EngineFact]
    public void RulesForTheTarget_ReadFromOfficialScripts()
    {
        if (Script(60577362) is null || Script(74122412) is null || Script(96434581) is null || Script(14812659) is null || Script(18235577) is null) return;
        Assert.Equal("direct", Effect(60577362, Card(100140160, kind: CardKind.Spell), "do.battle_rule").Do[0].Values["rule"]);
        Assert.Equal("cannot_be_destroyed_both", Effect(74122412, Card(100140161), "do.battle_rule").Do[0].Values["rule"]);
        Assert.Equal("unaffected_other", Effect(96434581, Card(100140162, kind: CardKind.Spell), "do.battle_rule").Do[0].Values["rule"]);
        // Risebell: "its Level increases by 1" (while face-up); Silent Strider: "decreases by 1 until the end of this turn".
        var risebell = Effect(14812659, Card(100140163, 800, 1), "do.change_level").Do.Single();
        Assert.Equal(("up", "1", "always"), (risebell.Values["mode"], risebell.Values["amount"], risebell.Values["duration"]));
        var strider = Effect(18235577, Card(100140164), "do.change_level").Do.Single();
        Assert.Equal(("down", "1", "turn"), (strider.Values["mode"], strider.Values["amount"], strider.Values["duration"]));
        // Risebell in the engine: the Level 4 Warrior becomes Level 5.
        var rise = new CardProject { Card = Card(100140163, 800, 1), Effects = [Effect(14812659, Card(100140163, 800, 1), "do.change_level")] };
        new EngineScenario(rise).Card(rise.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(rise.Card.Id).ExpectActivations(1)
            .Check($"{Find(Warrior, 0, "LOCATION_MZONE")}:GetLevel()==5 or {Find(rise.Card.Id, 0, "LOCATION_MZONE")}:GetLevel()==2").AssertPasses();
    }

    [EngineFact]
    public void MoreLastingRules_ReadFromOfficialScripts()
    {
        if (Script(57964143) is null || Script(53239672) is null || Script(24731391) is null || Script(83656563) is null || Script(90587641) is null
            || Script(75372290) is null || Script(87742943) is null || Script(19963185) is null || Script(44654994) is null) return;
        // Arbitration of White: "target 1 Fish monster you control; it is treated as a Tuner this turn".
        Assert.Equal("tuner", Effect(57964143, Card(100140190, kind: CardKind.Trap), "do.battle_rule").Do[0].Values["rule"]);
        // Spirit Barrier: "While you control a monster, you take no battle damage"; Cyberse Magician halves damage.
        var barrier = Effect(53239672, Card(100140191, kind: CardKind.Trap), "passive.player_rule");
        Assert.Equal("no_battle_damage", barrier.Do[0].Values["rule"]);
        Assert.Equal("While you control a monster, you take no battle damage.", EffectTextGenerator.Describe(barrier, Card(100140191, kind: CardKind.Trap)));
        Assert.Equal("half_damage", Effect(24731391, Card(100140192), "passive.player_rule").Do[0].Values["rule"]);
        // Up to 2 attacks on monsters; attacking from Defense Position with its DEF, or with its ATK.
        Assert.Equal("extra_monster", Effect(83656563, Card(100140193), "passive.battle_ability").Do[0].Values["ability"]);
        Assert.Equal("defense_attack", Effect(90587641, Card(100140194), "passive.battle_ability").Do[0].Values["ability"]);
        Assert.Equal("defense_attack_atk", Effect(75372290, Card(100140195), "passive.battle_ability").Do[0].Values["ability"]);
        // Powered Crawler: "All monsters your opponent controls must attack, if able."
        var crawler = Effect(87742943, Card(100140196), "passive.cards_rule").Do[0];
        Assert.Equal(("must_attack", CardOwner.Opponent), (crawler.Values["rule"], crawler.Filters["cards"].Owner));
        // Amethyst Cat: "If "Advanced Dark" is not in a Field Zone, send this monster to the GY", and its flag needs no block.
        var cat = ScriptTranslator.Translate(Script(19963185)!, 19963185, Card(100140197));
        Assert.Contains(cat.Pieces.SelectMany(p => p.Effects), e => e.Do.Any(b => b.BlockId == SelfDestroyPassive.BlockId && b.Values["how"] == "grave"));
        Assert.Contains(cat.Pieces, p => p.Ignored && p.Piece.Code.Contains("GLOBALFLAG_SELF_TOGRAVE", StringComparison.Ordinal));
        // Artmage Impasto -Recapture-: "If you control an "Artmage" monster, you can activate this card the turn it was Set."
        var impasto = Effect(44654994, Card(100140198, kind: CardKind.Trap), ActivateSetTurnPassive.BlockId);
        Assert.Single(impasto.If);
        // Ghostrick Lantern: "Cannot be Normal Summoned, unless you control a "Ghostrick" monster."
        if (Script(54512827) is not null)
        {
            var lantern = Effect(54512827, Card(100140205), CannotBeSummonedPassive.BlockId);
            Assert.Equal(("normal", "if.no_cards"), (lantern.Do[0].Values["how"], lantern.If.Single().BlockId));
        }
        // Xiangsheng Magician: "Your opponent takes no battle damage from attacks involving this card"; Arahime reflects it.
        if (Script(17086528) is not null && Script(75771170) is not null && Script(37869028) is not null)
        {
            Assert.Equal("opp_no_battle_damage", Effect(17086528, Card(100140202), "passive.self_rule").Do[0].Values["rule"]);
            Assert.Equal("reflect_battle", Effect(75771170, Card(100140203), "passive.self_rule").Do[0].Values["rule"]);
            Assert.Equal("extra2", Effect(37869028, Card(100140204), "passive.battle_ability").Do[0].Values["ability"]);
        }
        // Immunities that name "e:GetHandlerPlayer()": Silent Magician LV8, Junk Warrior/Assault Mode, Cyberdark End Dragon.
        if (Script(72443568) is null || Script(23219323) is null || Script(37542782) is null) return;
        Assert.Equal("opp_spell", Effect(72443568, Card(100140199, 3500, 8), "passive.immune").Do[0].Values["whose"]);
        Assert.Equal("opp_activated_monster", Effect(23219323, Card(100140200, 3000, 10), "passive.immune").Do[0].Values["whose"]);
        Assert.Equal("opp_activated", Effect(37542782, Card(100140201, 4000, 10), "passive.immune").Do[0].Values["whose"]);
    }

    [EngineFact]
    public void NegatingEffects_WrittenAsTwoEffects_IsOneBlock()
    {
        // Black Ray Lancer creates EFFECT_DISABLE and EFFECT_DISABLE_EFFECT one after the other.
        if (Script(74416224) is null) return;
        var lancer = Effect(74416224, Card(100140170, 2100, 3), "do.negate_effects");
        Assert.Equal(("turn", "true"), (lancer.Do.Single().Values["duration"], lancer.Do.Single().Values["target"]));
        // Forbidden Chalice also gives the target 400 ATK: two blocks can't share the target, so it stays script.
        if (Script(25789292) is not null)
            Assert.False(ScriptTranslator.Translate(Script(25789292)!, 25789292, Card(100140171, kind: CardKind.Spell, spell: SpellKind.QuickPlay)).Complete);
    }

    [EngineFact]
    public void ForTheRestOfThisTurn_FieldRulesAndNoBattleDamage_ReadFromOfficialScripts()
    {
        if (Script(7935043) is null || Script(93816465) is null) return;
        var aegis = Effect(7935043, Card(100140180, kind: CardKind.Spell, spell: SpellKind.QuickPlay), TurnFieldAction.BlockId);
        var field = aegis.Do.Single();
        Assert.Equal("indestructible_both", field.Values["rule"]);
        Assert.Equal((CardAttribute.Water, 3), (field.Filters["cards"].Attribute, field.Filters["cards"].LevelMax));
        // Face-down monsters count too, as the official target range has them.
        Assert.False(field.Filters["cards"].FaceUpOnly);
        var gardna = Effect(93816465, Card(100140181, 0), TurnPlayerRuleAction.BlockId);
        Assert.Contains(gardna.Do, b => b.BlockId == TurnPlayerRuleAction.BlockId && b.Values["rule"] == "no_battle");
        // Aegis in the engine: player 1's Level 3 WATER monster attacks a stronger Warrior and isn't destroyed.
        var spell = new CardProject { Card = Card(100140180, kind: CardKind.Spell, spell: SpellKind.QuickPlay), Effects = [aegis] };
        var fish = new CardProject { Card = new CardData { Id = 100140182, Name = "Small Fish", Frame = MonsterFrame.Normal, Level = 3, Atk = 1000, Def = 1000, Race = CardRace.Fish, Attribute = CardAttribute.Water } };
        new EngineScenario(spell, fish).Card(spell.Card.Id, 1, "hand").Card(fish.Card.Id, 1, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(spell.Card.Id).Attack(fish.Card.Id).Turns(2).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{fish.Card.Id})").Check("Duel.GetLP(1)==7500").AssertPasses();
    }
}
