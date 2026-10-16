using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Coin tosses and die rolls with a step for each result, declarations, and the Toon monster rules.</summary>
public class ChanceAndDeclarationTests
{
    private static CardProject Monster(long id, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = "Chance " + id, Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    private static CardProject Spell(long id, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = "Chance " + id, Kind = CardKind.Spell },
        Effects = [.. effects],
    };

    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();
    private static BlockInstance Coin(string result, int n = 2) => B(CoinResultCondition.BlockId).With("result", result).With("n", n);
    private static BlockInstance Die(params int[] results)
    {
        var b = B(DieResultCondition.BlockId);
        foreach (var r in results) b.With("r" + r, true);
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

    /// <summary>Cup of Ace: "Toss a coin. ● Heads: Draw 2 cards. ● Tails: Your opponent draws 2 cards."</summary>
    private static CardProject CupOfAce(long id) => Spell(id, new EffectEvent
    {
        When = B("when.activate"),
        Do =
        [
            B(TossCoinAction.BlockId),
            Coin("heads").Linked(ActionLink.Option), B("do.draw").With("count", 2),
            Coin("tails").Linked(ActionLink.Option), B("do.draw").With("who", "opponent").With("count", 2),
        ],
    });

    [Fact]
    public void HeadsAndTails_ReadAsLinesOfTheirOwn()
    {
        var p = CupOfAce(100099500);
        Assert.Equal("Toss a coin.\n ● Heads: Draw 2 cards.\n ● Tails: Your opponent draws 2 cards.", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("CATEGORY_COIN", lua);
        Assert.Contains("Duel.TossCoin(tp,1)", lua);
        // Each result is its own step, not an option the player picks; the effect can be activated when either could draw.
        Assert.DoesNotContain("SelectFromOptions", lua);
        Assert.Contains("((Duel.IsPlayerCanDraw(tp,2)) or (Duel.IsPlayerCanDraw(1-tp,2)))", lua);
        Assert.Empty(ScriptCompiler.StringSlots(p).Skip(1));
    }

    [EngineTheory]
    // The opponent has also drawn for their turn when the duel is checked.
    [InlineData(1, "Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2 and Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==1")]
    [InlineData(0, "Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0 and Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==3")]
    public void HeadsAndTails_EachHappenOnTheirResult(int coin, string check)
    {
        const long id = 100099501;
        new EngineScenario(CupOfAce(id)).Card(id, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Coins(coin).Activate(id).ExpectActivations(1).Check(check).AssertPasses();
    }

    /// <summary>Abare Ushioni: "Toss a coin and call it. If you call it right, inflict 1000 damage ... If you call it wrong, you take 1000 damage."</summary>
    private static CardProject Ushioni(long id) => Monster(id, new EffectEvent
    {
        When = B("when.ignition"),
        Limit = OncePerTurn.Soft,
        Do =
        [
            B(TossCoinAction.BlockId).With("call", true),
            Coin("right"), B("do.damage").With("amount", 1000),
            Coin("wrong"), B("do.damage").With("amount", 1000).With("who", "you"),
        ],
    });

    [Fact]
    public void CallingTheCoin_ReadsAsSentences()
    {
        var p = Ushioni(100099502);
        Assert.Equal("Once per turn: You can toss a coin and call it. If you call it right, inflict 1000 damage to your opponent. If you call it wrong, you take 1000 damage.",
            EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("Duel.AnnounceCoin(tp)", lua);
    }

    [EngineTheory]
    // The harness calls "Heads": a coin landing heads is called right.
    [InlineData(1, "Duel.GetLP(1)==7000 and Duel.GetLP(0)==8000")]
    [InlineData(0, "Duel.GetLP(1)==8000 and Duel.GetLP(0)==7000")]
    public void CallingTheCoin_RightOrWrong(int coin, string check)
    {
        const long id = 100099503;
        new EngineScenario(Ushioni(id)).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Coins(coin).Activate(id).ExpectActivations(1).Check(check).AssertPasses();
    }

    /// <summary>Barrel Dragon: "Target 1 monster your opponent controls; toss a coin 3 times and if at least 2 of the results are heads, destroy it."</summary>
    private static CardProject Barrel(long id) => Monster(id, new EffectEvent
    {
        When = B("when.ignition"),
        Limit = OncePerTurn.Soft,
        Do =
        [
            B(TossCoinAction.BlockId).With("count", 3),
            Coin("heads_min", 2),
            B("do.destroy").With("target", true).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
        ],
    });

    [Fact]
    public void SeveralCoins_CountTheirHeads()
    {
        var p = Barrel(100099504);
        Assert.Equal("Once per turn: You can target 1 monster your opponent controls; toss a coin 3 times and if at least 2 of the results are heads, destroy it.",
            EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("Duel.TossCoin(tp,3)", lua);
        Assert.Contains("Duel.IsExistingTarget(", lua);
    }

    [EngineTheory]
    [InlineData(new[] { 1, 0, 1 }, "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0")]
    [InlineData(new[] { 1, 0, 0 }, "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1")]
    public void SeveralCoins_DestroyOnTwoHeads(int[] coins, string check)
    {
        const long id = 100099505;
        new EngineScenario(Barrel(id)).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Coins(coins).Activate(id).ExpectActivations(1).Check(check).AssertPasses();
    }

    [Fact]
    public void Coins_ResultBlocksNeedATossBefore()
    {
        var p = Monster(100099506, new EffectEvent { When = B("when.ignition"), If = [Coin("heads")], Do = [B("do.draw")] });
        Assert.Contains(ScriptCompiler.Compile(p).Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("Toss a coin"));
        var two = Monster(100099507, new EffectEvent { When = B("when.ignition"), Do = [B(TossCoinAction.BlockId).With("count", 2), Coin("heads"), B("do.draw")] });
        Assert.Contains(ScriptCompiler.Compile(two).Diagnostics, d => d.Severity == Severity.Error);
    }

    /// <summary>Dicephoon-like: "Roll a six-sided die and apply the result. ● 1 or 6: You take 1000 damage. ● 2, 3, or 4: Draw 1 card. ● 5: Draw 2 cards."</summary>
    private static CardProject DiceBranches(long id) => Spell(id, new EffectEvent
    {
        When = B("when.activate"),
        Do =
        [
            B(RollDieAction.BlockId),
            Die(1, 6).Linked(ActionLink.Option), B("do.damage").With("amount", 1000).With("who", "you"),
            Die(2, 3, 4).Linked(ActionLink.Option), B("do.draw"),
            Die(5).Linked(ActionLink.Option), B("do.draw").With("count", 2),
        ],
    });

    [Fact]
    public void DieResults_ReadAsLinesOfTheirOwn()
    {
        var p = DiceBranches(100099510);
        Assert.Equal("Roll a six-sided die and apply the result.\n ● 1 or 6: You take 1000 damage.\n ● 2, 3, or 4: Draw 1 card.\n ● 5: Draw 2 cards.",
            EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("CATEGORY_DICE", lua);
        Assert.Contains("Duel.TossDice(tp,1)", lua);
    }

    [EngineTheory]
    [InlineData(6, "Duel.GetLP(0)==7000 and Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0")]
    [InlineData(3, "Duel.GetLP(0)==8000 and Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")]
    [InlineData(5, "Duel.GetLP(0)==8000 and Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2")]
    public void DieResults_EachHappenOnTheirResult(int die, string check)
    {
        const long id = 100099511;
        new EngineScenario(DiceBranches(id)).Card(id, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Dice(die).Activate(id).ExpectActivations(1).Check(check).AssertPasses();
    }

    /// <summary>Psychic Rover-like inline text: "roll a six-sided die, and if you roll a 1 or 6, draw 1 card".</summary>
    [Fact]
    public void DieResult_InlineText()
    {
        var p = Monster(100099512, new EffectEvent { When = B("when.ignition"), Do = [B(RollDieAction.BlockId), Die(1, 6), B("do.draw")] });
        Assert.Equal("You can roll a six-sided die, and if you roll a 1 or 6, draw 1 card.", EffectTextGenerator.Generate(p));
        Lua(p);
    }

    /// <summary>"toss a coin and if the result is heads, draw 1 card, or if the result is tails, your opponent draws 1 card".</summary>
    [Fact]
    public void CoinResults_InlineText()
    {
        var p = Monster(100099513, new EffectEvent
        {
            When = B("when.ignition"),
            Do = [B(TossCoinAction.BlockId), Coin("heads"), B("do.draw"), Coin("tails"), B("do.draw").With("who", "opponent")],
        });
        Assert.Equal("You can toss a coin and if the result is heads, draw 1 card, or if the result is tails, your opponent draws 1 card.", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        // The tails step is not inside the heads one.
        var heads = lua.IndexOf("==1) then", StringComparison.Ordinal);
        var tails = lua.IndexOf("==0) then", StringComparison.Ordinal);
        Assert.True(heads > 0 && tails > heads);
        Assert.Matches(@"end\n\t+if \(heads\d+==0\) then", lua);
    }

    /// <summary>Skull Dice: "Roll a six-sided die. All monsters your opponent currently controls lose ATK/DEF equal to the result x 100".</summary>
    private static CardProject SkullDice(long id, string duration = "turn") => Spell(id, new EffectEvent
    {
        When = B("when.activate"),
        Do =
        [
            B(RollDieAction.BlockId),
            B(StatsFromValueAction.BlockId).With("mode", "all").With("dir", "lose").With("stat", "both").With("how", "per").With("amount", 100).With("value", "roll").With("duration", duration)
                .With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true }),
        ],
    });

    [Fact]
    public void TheResult_IsAnAmount()
    {
        var p = SkullDice(100099514);
        Assert.Contains("lose ATK/DEF equal to the result x 100", EffectTextGenerator.Generate(p));
        Lua(p);
    }

    [EngineFact]
    public void TheResult_IsAnAmount_InTheEngine()
    {
        const long id = 100099515;
        new EngineScenario(SkullDice(id, "always")).Card(id, 0, "hand").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Dice(4).Activate(id).ExpectActivations(1).Check("Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,1004):GetAttack()==600").AssertPasses();
    }

    [EngineFact]
    public void TheResult_InflictsDamage()
    {
        const long id = 100099516;
        var p = Monster(id, new EffectEvent
        {
            When = B("when.ignition"),
            Do = [B(RollDieAction.BlockId).With("count", 2), B(LifeFromValueAction.BlockId).With("how", "per").With("amount", 100).With("value", "roll")],
        });
        Assert.Equal("You can roll a six-sided die twice, and inflict damage to your opponent equal to the result x 100.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Dice(3, 5).Activate(id).ExpectActivations(1).Check("Duel.GetLP(1)==7200").AssertPasses();
    }

    [EngineTheory]
    [InlineData(RequirementOp.Is, 100099517L)]
    [InlineData(RequirementOp.IsNot, Warrior)]
    public void CoinTossingCards_CanBePicked(RequirementOp op, long added)
    {
        // "Add 1 card from your Deck to your hand that has a coin tossing effect": a card whose effect tosses a coin.
        const long id = 100099518;
        var cup = CupOfAce(100099517);
        var p = Monster(id, new EffectEvent
        {
            When = B("when.ignition"),
            Do = [B("do.add_to_hand").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, Requirements = [new CardRequirement { Property = "coin_effect", Op = op }] })],
        });
        new EngineScenario(p, cup).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 2).Card(cup.Card.Id, 0, "deck").Card(Warrior, 1, "deck", 5)
            .Activate(id).ExpectActivations(1).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,{added})").AssertPasses();
    }

    // ---------------------------------------------------------------- declarations

    /// <summary>Tribe-Infecting Virus: "Discard 1 card, then declare 1 Monster Type; destroy all face-up monsters on the field with the declared Type."</summary>
    private static CardProject Virus(long id) => Monster(id, new EffectEvent
    {
        When = B("when.ignition"),
        Cost = [B("cost.discard")],
        Do =
        [
            B(DeclareAction.BlockId).With("what", "race"),
            B("do.destroy").With("mode", "all").With("cards", new CardFilter
            {
                Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true,
                Requirements = [new CardRequirement { Property = "declared_race", Op = RequirementOp.Is }],
            }),
        ],
    });

    [Fact]
    public void DeclaredType_TextAndScript()
    {
        var p = Virus(100099520);
        Assert.Equal("You can discard 1 card from your hand, then declare 1 Monster Type; destroy all face-up monsters on the field with the declared Type.", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("Duel.AnnounceRace(tp,1,RACE_ALL)", lua);
        Assert.Contains("Duel.SetTargetParam(", lua);
    }

    [EngineFact]
    public void DeclaredType_DestroysOnlyThatType()
    {
        const long id = 100099521;
        // The declared Type is Fiend (0x8): the Fiend goes, the Warrior stays.
        new EngineScenario(Virus(id)).Card(id, 0, "mzone").Card(Warrior, 0, "hand").Card(Fiend, 1, "mzone").Card(Warrior, 1, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Declare((long)CardRace.Fiend).Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1").Check("Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,1001)").AssertPasses();
    }

    [EngineFact]
    public void DeclaredName_PicksThatCard()
    {
        // "Declare 1 card name; add 1 card with the declared name from your Deck to your hand."
        const long id = 100099522;
        var p = Monster(id, new EffectEvent
        {
            When = B("when.ignition"),
            Do =
            [
                B(DeclareAction.BlockId),
                B("do.add_to_hand").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, Requirements = [new CardRequirement { Property = "declared_name", Op = RequirementOp.Is }] }),
            ],
        });
        Assert.Equal("You can declare 1 card name; add 1 card with the declared name from your Deck to your hand.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Dragon, 0, "deck", 3).Card(Warrior, 1, "deck", 5)
            .Announce(Dragon).Activate(id).ExpectActivations(1)
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,1002)").Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").AssertPasses();
    }

    [EngineTheory]
    [InlineData("attribute", (long)CardAttribute.Dark, "declared_attribute")]
    [InlineData("level", 4L, "declared_level")]
    [InlineData("type", 0L, "declared_type")]
    public void DeclaredAttributeLevelOrType_PickThoseCards(string what, long declare, string property)
    {
        // "Declare 1 Attribute; send all monsters your opponent controls with the declared Attribute to the GY."
        const long id = 100099523;
        var p = Monster(id, new EffectEvent
        {
            When = B("when.ignition"),
            Do =
            [
                B(DeclareAction.BlockId).With("what", what),
                B("do.send_to_gy").With("mode", "all").With("cards", new CardFilter
                    { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, Requirements = [new CardRequirement { Property = property, Op = RequirementOp.Is }] }),
            ],
        });
        Lua(p);
        // The Fiend is DARK, Level 4 and a monster: it goes. A face-down Trap stays unless the card type is Trap.
        new EngineScenario(p).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Declare(declare).Activate(id).ExpectActivations(1).Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0").AssertPasses();
    }

    [EngineFact]
    public void WithoutTheDeclaredType_SparesThatType()
    {
        // "Declare 1 Monster Type; destroy all monsters your opponent controls without the declared Type": the Warrior stays.
        const long id = 100099524;
        var p = Monster(id, new EffectEvent
        {
            When = B("when.ignition"),
            Do =
            [
                B(DeclareAction.BlockId).With("what", "race"),
                B("do.destroy").With("mode", "all").With("cards", new CardFilter
                    { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Requirements = [new CardRequirement { Property = "declared_race", Op = RequirementOp.IsNot }] }),
            ],
        });
        Assert.Equal("You can declare 1 Monster Type; destroy all monsters your opponent controls without the declared Type.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Declare((long)CardRace.Warrior).Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1").Check("Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,1001)").AssertPasses();
    }

    /// <summary>Psi-Blocker: "Once per turn: You can declare 1 card name; cards with that name, and their effects, cannot be used until the end of your opponent's next turn."</summary>
    private static CardProject PsiBlocker(long id) => Monster(id, new EffectEvent
    {
        When = B("when.ignition"),
        Limit = OncePerTurn.Soft,
        Do = [B(DeclareAction.BlockId), B(DeclaredLockAction.BlockId).With("duration", "opp_next")],
    });

    [Fact]
    public void DeclaredName_Forbidden_Text()
    {
        var p = PsiBlocker(100099530);
        Assert.Equal("Once per turn: You can declare 1 card name; cards with that name, and their effects, cannot be used until the end of your opponent's next turn.",
            EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("EFFECT_FORBIDDEN", lua);
        Assert.Contains("RESET_PHASE+PHASE_END+RESET_OPPO_TURN", lua);
    }

    [EngineFact]
    public void DeclaredName_CannotBeUsed()
    {
        const long blocker = 100099531, pot = 100099532;
        var draw = Spell(pot, new EffectEvent { When = B("when.activate"), Do = [B("do.draw").With("count", 2)] });
        // The opponent's draw Spell has the declared name: it can't be activated on their turn.
        new EngineScenario(PsiBlocker(blocker), draw).Card(blocker, 0, "mzone").Card(pot, 1, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Announce(pot).Activate(blocker).Activate(pot).Turns(2).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==2").AssertPasses();
    }

    [EngineFact]
    public void DeclaredName_CannotBeActivated_ForTheRestOfThisTurn()
    {
        const long ban = 100099533, pot = 100099534;
        var sales = Spell(ban, new EffectEvent
        {
            When = B("when.activate"),
            Do = [B(DeclareAction.BlockId), B(DeclaredLockAction.BlockId).With("what", "activate").With("who", "you")],
        });
        Assert.Equal("Declare 1 card name; for the rest of this turn, you cannot activate cards, or the effects of cards, with that original name.", EffectTextGenerator.Generate(sales));
        var draw = Spell(pot, new EffectEvent { When = B("when.activate"), Do = [B("do.draw").With("count", 2)] });
        new EngineScenario(sales, draw).Card(ban, 0, "hand").Card(pot, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Announce(pot).Activate(ban).Activate(pot).ExpectActivations(1).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").AssertPasses();
    }

    [Fact]
    public void Prohibition_ActivatesByDeclaring()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100099535, Name = "Ban", Kind = CardKind.Trap, TrapKind = TrapKind.Continuous },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B(DeclareAction.BlockId), B(DeclaredLockAction.BlockId).With("duration", "face_up")] }],
        };
        Assert.Equal("Activate this card by declaring 1 card name. Cards with that name, and their effects, cannot be used. Cards already on the field are not affected (including face-down cards).",
            EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("c:GetRealFieldID()>e:GetFieldID()", lua);
        Assert.Contains("Duel.AdjustInstantly(c)", lua);
    }

    [Fact]
    public void DeclaredLock_NeedsADeclaredName()
    {
        var p = Monster(100099536, new EffectEvent { When = B("when.ignition"), Do = [B(DeclareAction.BlockId).With("what", "race"), B(DeclaredLockAction.BlockId)] });
        Assert.Contains(ScriptCompiler.Compile(p).Diagnostics, d => d.Severity == Severity.Error);
    }

    // ---------------------------------------------------------------- Toon monsters

    private static CardProject Toon(long id, params string[] rules) => new()
    {
        Card = new CardData { Id = id, Name = "Toon " + id, Frame = MonsterFrame.Effect, Abilities = MonsterAbility.Toon, Level = 4, Atk = 1400, Def = 1000 },
        Effects = rules.Select(r => new EffectEvent { When = B("when.while_face_up"), Do = [B(ToonRulesPassive.BlockId).With("rule", r)] }).ToList(),
    };

    [Fact]
    public void ToonRules_ReadAsTheCardsPrintThem()
    {
        var p = Toon(100099540, "summon_turn", "world_destroyed", "direct_world");
        Assert.Equal("Cannot attack the turn it is Summoned. If \"Toon World\" on the field is destroyed, destroy this card. " +
                     "While you control \"Toon World\" and your opponent controls no Toon monsters, this card can attack your opponent directly.",
            EffectTextGenerator.Generate(p));
        Lua(p);
        Lua(Toon(100099541, "spsummon_turn", "direct_unless", "attack_cost"));
    }

    // Attacks are on the second turn: the opponent's (player 1) monsters attack player 0.
    [EngineFact]
    public void ToonRules_CannotAttackTheTurnItIsSummoned()
    {
        const long id = 100099542;
        new EngineScenario(Toon(id, "summon_turn")).Card(id, 1, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Summon(id).Attack(id).Turns(2).Check("Duel.GetLP(0)==8000").AssertPasses();
        new EngineScenario(Toon(id, "direct_world")).Card(id, 1, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Summon(id).Attack(id).Turns(2).Check("Duel.GetLP(0)==6600").AssertPasses();
    }

    [EngineFact]
    public void ToonRules_AttackDirectlyWithToonWorld()
    {
        const long id = 100099543;
        var world = new CardProject { Card = new CardData { Id = ToonRulesPassive.ToonWorld, Name = "Toon World", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous } };
        // A Toon already on the field attacks directly past the opponent's monster while its controller has "Toon World".
        new EngineScenario(Toon(id, "summon_turn", "direct_world"), world).Card(id, 1, "mzone").Card(ToonRulesPassive.ToonWorld, 1, "hand").Card(Fiend, 0, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(ToonRulesPassive.ToonWorld).Attack(id).Turns(2).Check("Duel.GetLP(0)==6600").AssertPasses();
        // Without "Toon World" it attacks the monster.
        new EngineScenario(Toon(id, "summon_turn", "direct_world"), world).Card(id, 1, "mzone").Card(Fiend, 0, "mzone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Attack(id).Turns(2).Check("Duel.GetLP(0)==7600").AssertPasses();
        // Nor while the opponent controls a Toon monster.
        new EngineScenario(Toon(id, "summon_turn", "direct_world"), Toon(id + 10, "summon_turn"), world).Card(id, 1, "mzone").Card(ToonRulesPassive.ToonWorld, 1, "hand")
            .Card(id + 10, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(ToonRulesPassive.ToonWorld).Attack(id).Turns(2)
            .Check("Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineFact]
    public void ToonRules_DestroyedWithToonWorld()
    {
        const long id = 100099544, mst = 100099545;
        var world = new CardProject { Card = new CardData { Id = ToonRulesPassive.ToonWorld, Name = "Toon World", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous } };
        var typhoon = Spell(mst, new EffectEvent
        {
            When = B("when.activate"),
            Do = [B("do.destroy").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.SpellTrapZone, CardType = FilterCardType.Spell })],
        });
        new EngineScenario(Toon(id, "world_destroyed"), world, typhoon).Card(id, 0, "mzone").Card(ToonRulesPassive.ToonWorld, 0, "hand").Card(mst, 0, "hand")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Activate(ToonRulesPassive.ToonWorld).Activate(mst).ExpectActivations(2)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==0").AssertPasses();
    }
}

/// <summary>Official coin toss, die roll, declaration and Toon scripts read back as blocks, played in the engine.</summary>
public class ChanceTranslationTests
{
    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, $"c{id}.lua"));

    private static CardProject Translated(long id, CardData card)
    {
        var result = ScriptTranslator.Translate(Script(id), id, card);
        Assert.True(result.Complete, string.Join("\n", result.Pieces.Where(p => !p.Translated).Select(p => $"{p.Piece.Title}: {p.Reason}")));
        var project = new CardProject { Card = card, Effects = result.Pieces.SelectMany(p => p.Effects).ToList() };
        var compiled = ScriptCompiler.Compile(project);
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        return project;
    }

    private static CardData Monster(long id, int level, int atk, int def) => new() { Id = id, Name = "Translated " + id, Frame = MonsterFrame.Effect, Level = level, Atk = atk, Def = def };

    [EngineTheory]
    [InlineData(1, "Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2 and Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==1")]
    [InlineData(0, "Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0 and Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==3")]
    public void CupOfAce_HeadsAndTails(int coin, string check)
    {
        const long id = 100099600;
        var card = new CardData { Id = id, Name = "Translated", Kind = CardKind.Spell,
            Text = "Toss a coin and apply this effect.\n● Heads: Draw 2 cards.\n● Tails: Your opponent draws 2 cards." };
        var p = Translated(37812118, card);
        Assert.Equal([TossCoinAction.BlockId, CoinResultCondition.BlockId, "do.draw", CoinResultCondition.BlockId, "do.draw"], p.Effects.Single().Do.Select(b => b.BlockId));
        Assert.Equal("Toss a coin.\n ● Heads: Draw 2 cards.\n ● Tails: Your opponent draws 2 cards.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(id, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Coins(coin).Activate(id).ExpectActivations(1).Check(check).AssertPasses();
    }

    [EngineTheory]
    [InlineData(new[] { 1, 1, 0 }, "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0")]
    [InlineData(new[] { 0, 0, 1 }, "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1")]
    public void BarrelDragon_DestroysOnTwoHeads(int[] coins, string check)
    {
        const long id = 100099601;
        var p = Translated(81480460, Monster(id, 7, 2600, 2200));
        Assert.Equal("Once per turn: You can target 1 monster your opponent controls; toss a coin 3 times and if at least 2 of the results are heads, destroy it.",
            EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Coins(coins).Activate(id).ExpectActivations(1).Check(check).AssertPasses();
    }

    [EngineTheory]
    [InlineData(1, "Duel.GetLP(1)==7000 and Duel.GetLP(0)==8000")]
    [InlineData(0, "Duel.GetLP(1)==8000 and Duel.GetLP(0)==7000")]
    public void AbareUshioni_CallsTheCoin(int coin, string check)
    {
        const long id = 100099602;
        var p = Translated(89718302, Monster(id, 4, 1200, 1200));
        Assert.Contains("toss a coin and call it. If you call it right, inflict 1000 damage to your opponent. If you call it wrong,", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Coins(coin).Activate(id).ExpectActivations(1).Check(check).AssertPasses();
    }

    [EngineTheory]
    [InlineData(new[] { 1, 1, 1 }, "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0 and Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1")]
    [InlineData(new[] { 0, 0, 0 }, "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1 and Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==0")]
    [InlineData(new[] { 1, 0, 0 }, "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1 and Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1")]
    public void SandGambler_AllHeadsOrAllTails(int[] coins, string check)
    {
        const long id = 100099603;
        var p = Translated(50593156, Monster(id, 3, 300, 1600));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Coins(coins).Activate(id).ExpectActivations(1).Check(check).AssertPasses();
    }

    [EngineTheory]
    // The target is the first card offered on the field: one of the two monsters goes.
    [InlineData(3, "Duel.GetFieldGroupCount(0,LOCATION_MZONE,LOCATION_MZONE)==1")]
    [InlineData(6, "Duel.GetFieldGroupCount(0,LOCATION_MZONE,LOCATION_MZONE)==2")]
    public void SnipeHunter_DestroysUnlessOneOrSix(int die, string check)
    {
        const long id = 100099604;
        var p = Translated(84290642, Monster(id, 4, 1500, 600));
        Assert.Contains("roll a six-sided die, and if you roll a 2, 3, 4, or 5, destroy it", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Dice(die).Activate(id).ExpectActivations(1).Check(check).AssertPasses();
    }

    [EngineFact]
    public void FireDarts_TheTotalAsDamage()
    {
        var p = Translated(43061293, new CardData { Id = 100099605, Name = "Translated", Kind = CardKind.Trap });
        var effect = p.Effects.Single();
        Assert.Equal([RollDieAction.BlockId, LifeFromValueAction.BlockId], effect.Do.Select(b => b.BlockId));
        Assert.Equal("3", effect.Do[0].Values["count"]);
        Assert.Contains("Roll a six-sided die 3 times, and inflict damage to your opponent equal to the result x 100", EffectTextGenerator.Generate(p));
    }

    [EngineFact]
    public void TimeWizard_StaysScript()
    {
        // Destroying, then taking damage equal to half the destroyed monsters' ATK: more than the blocks do.
        var result = ScriptTranslator.Translate(Script(71625222), 71625222, Monster(100099606, 2, 500, 400));
        Assert.False(result.Complete);
    }

    [EngineFact]
    public void PsiBlocker_ForbidsTheDeclaredName()
    {
        const long blocker = 100099607, pot = 100099608;
        var p = Translated(29417188, Monster(blocker, 4, 1200, 300));
        Assert.Equal([DeclareAction.BlockId, DeclaredLockAction.BlockId], p.Effects.Single().Do.Select(b => b.BlockId));
        var draw = new CardProject
        {
            Card = new CardData { Id = pot, Name = "Pot", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = new BlockInstance("when.activate"), Do = [BlockRegistry.Find("do.draw")!.CreateInstance().With("count", 2)] }],
        };
        new EngineScenario(p, draw).Card(blocker, 0, "mzone").Card(pot, 1, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Announce(pot).Activate(blocker).Activate(pot).Turns(2).ExpectActivations(1).Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==2").AssertPasses();
    }

    [EngineFact]
    public void Prohibition_ReadsAsDeclaredLock()
    {
        var p = Translated(43711255, new CardData { Id = 100099609, Name = "Translated", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous });
        var lockBlock = p.Effects.Single().Do[1];
        Assert.Equal(DeclaredLockAction.BlockId, lockBlock.BlockId);
        Assert.Equal("face_up", lockBlock.Values["duration"]);
        Assert.StartsWith("Activate this card by declaring 1 card name. Cards with that name, and their effects, cannot be used.", EffectTextGenerator.Generate(p));
    }

    [EngineFact]
    public void ToonGeminiElf_RulesAsBlocks()
    {
        var result = ScriptTranslator.Translate(Script(42386471), 42386471, Monster(100099610, 4, 1900, 900));
        var rules = result.Pieces.SelectMany(x => x.Effects).SelectMany(e => e.Do).Where(b => b.BlockId == ToonRulesPassive.BlockId).Select(b => b.Values["rule"]).ToList();
        Assert.Equal(["summon_turn", "world_destroyed", "direct_world"], rules);
    }

    [EngineTheory]
    [InlineData(false, "Duel.GetLP(0)==6600 and Duel.GetLP(1)==7500")]
    [InlineData(true, "Duel.GetLP(0)==8000 and Duel.GetLP(1)==7500")]
    public void ToonMermaid_AttacksDirectlyUnlessTheyHaveAToon(bool theyHaveAToon, string check)
    {
        // Toon Mermaid is player 1's, on their turn: paying 500 LP, it attacks player 0 directly, or their Toon (both 1400 ATK).
        const long id = 100099611, other = 100099612;
        var card = Monster(id, 4, 1400, 1500);
        card.Abilities = MonsterAbility.Toon;
        var p = Translated(65458948, card);
        var rules = p.Effects.SelectMany(e => e.Do).Where(b => b.BlockId == ToonRulesPassive.BlockId).Select(b => b.Values["rule"]).ToList();
        Assert.Equal(["world_destroyed", "direct_unless", "spsummon_turn", "attack_cost"], rules);
        var toon = new CardProject { Card = new CardData { Id = other, Name = "Other Toon", Frame = MonsterFrame.Normal, Abilities = MonsterAbility.Toon, Level = 4, Atk = 1400, Def = 1000 } };
        var s = new EngineScenario(p, toon).Card(id, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Attack(id).Turns(2).Check(check);
        if (theyHaveAToon) s.Card(other, 0, "mzone");
        s.AssertPasses();
    }
}
