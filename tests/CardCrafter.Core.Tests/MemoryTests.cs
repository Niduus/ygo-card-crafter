using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Remembering cards and numbers (Memory A-E) for later effects, the LP paid in multiples, banishing until a Standby
/// Phase, and delayed steps on remembered cards: their card text, their Lua, and how long they last in the real engine.
/// </summary>
public class MemoryTests
{
    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();
    private static BlockInstance Remember(string what, string duration = "turn", string slot = "A") =>
        B(RememberAction.BlockId).With("what", what).With("duration", duration).With(Memory.SlotKey, slot);

    private static readonly CardFilter OppMonster = new() { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };

    private static CardFilter Remembered(CardFilter f, string slot = "A", RequirementOp op = RequirementOp.Is)
    {
        f = f.Clone();
        f.Requirements = [new CardRequirement { Property = "remembered", Op = op, Values = [Memory.Index(slot)] }];
        return f;
    }

    private static CardProject Monster(long id, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = "Memory " + id, Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    private static EffectEvent Ignition(params BlockInstance[] dos) => new() { When = B("when.ignition"), Limit = OncePerTurn.Soft, Do = [.. dos] };

    private static string Lua(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics) + "\n" + r.Lua);
        Assert.DoesNotContain(Validation.ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        Assert.Empty(Validation.ScriptChecker.UnknownNames(r.Lua));
        return r.Lua;
    }

    /// <summary>
    /// Records <paramref name="lua"/> at the start of each turn's Draw Phase (before anything resets in it) into the global
    /// table ccw, keyed by turn: ccw[2] is what was left after turn 1 ended.
    /// </summary>
    private static EngineScenario Watch(EngineScenario s, string lua) =>
        s.Setup("ccw={} local we=Effect.GlobalEffect() we:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS) we:SetCode(EVENT_PHASE_START+PHASE_DRAW) " +
                $"we:SetOperation(function() ccw[Duel.GetTurnCount()]=({lua}) end) Duel.RegisterEffect(we,0)");

    /// <summary>How many cards on the field or in a GY carry Memory A's flag of card <paramref name="id"/>.</summary>
    private static string Flagged(long id, string slot = "A") =>
        $"Duel.GetMatchingGroupCount(function(c) return c:GetFlagEffect({id + 9100 + Memory.Index(slot)})>0 end,0,0x7f,0x7f,nil)";

    private static BlockInstance TargetOpp() =>
        B("do.change_stats").With("target", true).With("dir", "lose").With("amount", 100).With("cards", OppMonster);

    // ---------------------------------------------------------------- remembered cards

    /// <summary>"Target 1 monster your opponent controls; it loses 100 ATK, and remember the target ...".</summary>
    private static CardProject RememberTarget(long id, string duration) => Monster(id,
        Ignition(TargetOpp(), Remember("target", duration),
            // Still remembered later in the same effect, whatever the time.
            B(MemoryCondition.BlockId).With("test", "holds"), B("do.gain_lp").With("amount", 1000)));

    [Fact]
    public void RememberingTheTarget_ReadsAsCardText()
    {
        Assert.Equal("Once per turn: You can target 1 monster your opponent controls; it loses 100 ATK until the end of this turn, and remember the target until the end of this turn, " +
                     "and if a remembered card is still there, gain 1000 LP.", EffectTextGenerator.Generate(RememberTarget(100099700, "turn")));
        var lua = Lua(RememberTarget(100099700, "next"));
        Assert.Contains("tc:RegisterFlagEffect(id+9100,RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,0,2)", lua);
        Assert.Contains("RESET_EVENT+RESETS_STANDARD+RESET_CHAIN", Lua(RememberTarget(100099700, "chain")));
    }

    /// <summary>How long a remembered card stays remembered: ccw[2] is after turn 1 ended, ccw[3] after turn 2.</summary>
    [EngineTheory]
    [InlineData("chain", "ccw[2]==0 and ccw[3]==0")]
    [InlineData("turn", "ccw[2]==0 and ccw[3]==0")]
    [InlineData("next", "ccw[2]==1 and ccw[3]==0")]
    [InlineData("duel", "ccw[2]==1 and ccw[3]==1")]
    public void RememberedCards_LastAsLongAsTheyShould(string duration, string check)
    {
        const long id = 100099701;
        var s = new EngineScenario(RememberTarget(id, duration)).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(id).Turns(3).ExpectActivations(1)
            // The gate after "remember" held while the effect resolved.
            .Check("Duel.GetLP(0)==9000").Check(check);
        Watch(s, Flagged(id)).AssertPasses();
    }

    /// <summary>"Return all monsters your opponent controls that were remembered to the hand": the reader, and a remembered card that left is forgotten.</summary>
    [EngineFact]
    public void RememberedCards_AreFoundByThePicker_AndForgottenWhenTheyLeave()
    {
        const long id = 100099702;
        var p = Monster(id,
            Ignition(TargetOpp(), Remember("target", "duel")),
            Ignition(B("do.return_to_hand").With("mode", "all").With("cards", Remembered(OppMonster))));
        Assert.Equal("Once per turn: You can return all monsters your opponent controls that were remembered to the hand.", EffectTextGenerator.Describe(p.Effects[1], p.Card));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(id, 2).ExpectActivations(2)
            // Only the remembered (targeted) Fiend went back; back in the hand it is no longer remembered.
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_HAND,0,1,nil,{Fiend})")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{Dragon})")
            .Check($"{Flagged(id)}==0")
            .AssertPasses();
    }

    /// <summary>"Destroy 1 monster ... remember the destroyed monster", then "Special Summon 1 monster from either GY that was remembered".</summary>
    [EngineFact]
    public void TheDestroyedMonster_CanBeSummonedByAnotherEffect()
    {
        const long id = 100099703;
        var p = Monster(id,
            Ignition(B("do.destroy").With("cards", OppMonster), Remember("affected").Linked(ActionLink.IfYouDo)),
            Ignition(B("do.special_summon").With("cards", Remembered(new CardFilter { Owner = CardOwner.Either, Zones = Zone.Graveyard, CardType = FilterCardType.Monster }))));
        Assert.Equal("Once per turn: You can destroy 1 monster your opponent controls, and if you do, remember the destroyed monster until the end of this turn.",
            EffectTextGenerator.Describe(p.Effects[0], p.Card));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "grave").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(id, 2).ExpectActivations(2)
            // The destroyed Fiend, not the Warrior already in the GY.
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Fiend})")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{Warrior})")
            .AssertPasses();
    }

    /// <summary>"This card gains ATK equal to the remembered monster's ATK": a lasting effect reading what another effect remembered.</summary>
    [EngineFact]
    public void TheRememberedMonstersAtk_IsALiveValue()
    {
        const long id = 100099704;
        var gains = B(StatsFromValuePassive.BlockId).With("value", "mem_atk");
        gains.Filters["cards"] = new CardFilter { ThisCard = true };
        var p = Monster(id, Ignition(TargetOpp(), Remember("target", "duel")), new EffectEvent { When = B("when.while_face_up"), Do = [gains] });
        Assert.Equal("This card gains ATK equal to the remembered monster's ATK.", EffectTextGenerator.Describe(p.Effects[1], p.Card));
        // The Dragon (1600) lost 100 ATK until the end of turn 1.
        new EngineScenario(p).Card(id, 0, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(id).Turns(3).ExpectActivations(1)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{id}):GetAttack()==2600")
            .AssertPasses();
    }

    /// <summary>"If this card destroys a monster by battle: remember the monster this card battled": the destroyed monster, in the GY.</summary>
    [EngineFact]
    public void TheMonsterThisCardBattled_IsRemembered()
    {
        const long id = 100099716;
        var p = Monster(id, new EffectEvent { When = B("when.destroys_by_battle"), Do = [Remember("battle", "duel")] });
        p.Card.Atk = 2000;
        Assert.Equal("If this card destroys an opponent's monster by battle: You can remember the monster this card battled for the rest of this Duel.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Attack(id).Activate(id).Turns(3).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(function(c) return c:IsCode({Fiend}) and c:GetFlagEffect({id + 9100})>0 end,1,LOCATION_GRAVE,0,1,nil)")
            .Check($"{Flagged(id)}==1")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- numbers

    /// <summary>"Pay LP in multiples of 100 (max. 2000); this card gains that much ATK."</summary>
    private static CardProject PayAndGain(long id) => Monster(id, new EffectEvent
    {
        When = B("when.ignition"),
        Limit = OncePerTurn.Soft,
        Cost = [B("cost.pay_lp").With("how", "multiple").With("max", 2000)],
        Do = [B(StatsFromValueAction.BlockId).With("value", "lp_paid").With("duration", "always").With("cards", new CardFilter { ThisCard = true })],
    });

    [Fact]
    public void TheLpPaid_ReadsAsCardText()
    {
        var p = PayAndGain(100099705);
        Assert.Equal("Once per turn: You can pay LP in multiples of 100 (max. 2000); this card gains that much ATK.", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("Duel.CheckLPCost(tp,100,true)", lua);
        Assert.Contains("local lpm=math.floor(math.min(Duel.GetLP(tp),2000)/100)", lua);
        Assert.Contains("Duel.PayLPCost(tp,lpc,true)", lua);
        Assert.Contains("e:SetLabel(lpc)", lua);
    }

    [EngineFact]
    public void TheLpPaid_IsTheAmountDeclared()
    {
        const long id = 100099706;
        new EngineScenario(PayAndGain(id)).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Declare(700).Activate(id).ExpectActivations(1)
            .Check("Duel.GetLP(0)==7300")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{id}):GetAttack()==1700")
            .AssertPasses();
    }

    [Fact]
    public void TheLpPaid_NeedsTheCost()
    {
        var p = PayAndGain(100099707);
        p.Effects[0].Cost.Clear();
        Assert.Contains(ScriptCompiler.Compile(p).Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("Pay LP in multiples"));
    }

    /// <summary>
    /// Psychic Blade's way: "pay LP in multiples of 100; remember the LP paid while this card is face-up", and "this card
    /// gains ATK equal to the remembered number" as a lasting effect.
    /// </summary>
    [EngineFact]
    public void ARememberedNumber_LastsWhileThisCardIsFaceUp()
    {
        const long id = 100099708;
        var gains = B(StatsFromValuePassive.BlockId).With("value", "mem");
        gains.Filters["cards"] = new CardFilter { ThisCard = true };
        var remember = Remember("value", "face_up").With("value", "lp_paid");
        var p = Monster(id,
            new EffectEvent { When = B("when.ignition"), Limit = OncePerTurn.Soft, Cost = [B("cost.pay_lp").With("how", "multiple").With("step", "500")], Do = [remember] },
            new EffectEvent { When = B("when.while_face_up"), Do = [gains] });
        Assert.Equal("Once per turn: You can pay LP in multiples of 500; remember the LP paid while this card is face-up.", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        Assert.Equal("This card gains ATK equal to the remembered number.", EffectTextGenerator.Describe(p.Effects[1], p.Card));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Declare(1500).Activate(id).Turns(3).ExpectActivations(1)
            .Check("Duel.GetLP(0)==6500")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{id}):GetAttack()==2500")
            .AssertPasses();
    }

    /// <summary>
    /// "Remember your LP": a number on this card. "While face-up" ends when it leaves the field; "for the rest of this
    /// Duel" stays with it, even in the hand.
    /// </summary>
    [EngineTheory]
    [InlineData("face_up", 0)]
    [InlineData("turn", 0)]
    [InlineData("duel", 1)]
    public void ARememberedNumber_StaysWithThisCardAsLongAsItShould(string duration, int left)
    {
        const long id = 100099717;
        var p = Monster(id, Ignition(Remember("value", duration).With("value", "your_lp")));
        Assert.Equal($"Once per turn: You can remember your LP {Memory.Durations.First(d => d.Value == duration).Label}.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            // At the start of turn 2, this card goes back to the hand.
            .Setup("local we=Effect.GlobalEffect() we:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS) we:SetCode(EVENT_PHASE_START+PHASE_DRAW) " +
                   $"we:SetOperation(function() if Duel.GetTurnCount()==2 then Duel.SendtoHand(Duel.GetMatchingGroup(Card.IsCode,0,LOCATION_MZONE,0,nil,{id}),nil,REASON_RULE) end end) Duel.RegisterEffect(we,0)")
            .Activate(id).Turns(2).ExpectActivations(1)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_HAND,0,nil,{id}):GetFlagEffect({id + 9100})=={left}")
            .Check(left == 1 ? $"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_HAND,0,nil,{id}):GetFlagEffectLabel({id + 9100})==8000" : "true")
            .AssertPasses();
    }

    /// <summary>"... then if this is the second time this effect has resolved this turn / this Duel, gain 1000 LP."</summary>
    private static CardProject Counting(long id, string duration, string test = "eq", int n = 2) => Monster(id, Ignition(
        B("do.gain_lp").With("amount", 500), Remember("add", duration),
        B(MemoryCondition.BlockId).With("test", test).With("n", n), B("do.gain_lp").With("amount", 1000)));

    [Fact]
    public void ACount_ReadsAsTheTimesThisEffectResolved()
    {
        Assert.Equal("Once per turn: You can gain 500 LP, and if this is the third time this effect has resolved this Duel, gain 1000 LP.",
            EffectTextGenerator.Generate(Counting(100099709, "duel", n: 3)));
        Assert.Equal("Once per turn: You can gain 500 LP, and if this effect has resolved 2 or more times this turn, gain 1000 LP.",
            EffectTextGenerator.Generate(Counting(100099709, "turn", "ge", 2)));
        // Counted with flags on this card, one per resolution, as official cards count.
        Assert.Contains("c:RegisterFlagEffect(id+9100,0,0,1,1)", Lua(Counting(100099709, "duel", n: 3)));
    }

    /// <summary>Activated in turns 1 and 3: counted for the Duel it is the second time in turn 3; counted for the turn it is never.</summary>
    [EngineTheory]
    [InlineData("duel", 10000)]
    [InlineData("turn", 9000)]
    // Counted in turn 1 until the end of turn 2: gone by turn 3.
    [InlineData("next", 9000)]
    public void ACount_KeepsCountingAsLongAsItShould(string duration, int lp)
    {
        const long id = 100099710;
        new EngineScenario(Counting(id, duration)).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(id, 2).Turns(3).ExpectActivations(2)
            .Check($"Duel.GetLP(0)=={lp}")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- later

    /// <summary>"Destroy 1 monster your opponent controls, and if you do, Special Summon it during your next Standby Phase."</summary>
    private static CardProject DestroyThenSummon(long id, string when) => Monster(id, Ignition(
        B("do.destroy").With("cards", OppMonster),
        B(LaterAction.BlockId).With("how", "special").With("when", when).Linked(ActionLink.IfYouDo)));

    [Fact]
    public void Later_ReadsAsCardText()
    {
        Assert.Equal("Once per turn: You can destroy 1 monster your opponent controls, and if you do, Special Summon it during your next Standby Phase.",
            EffectTextGenerator.Generate(DestroyThenSummon(100099711, "standby_your")));
    }

    [EngineTheory]
    // Your next Standby Phase is turn 3's; the next Standby Phase is turn 2's (your opponent's).
    [InlineData("standby_your", 2, false)]
    [InlineData("standby_your", 3, true)]
    [InlineData("standby_next", 2, true)]
    [InlineData("standby_opp", 2, true)]
    [InlineData("end_next", 1, false)]
    [InlineData("end_next", 2, true)]
    [InlineData("end", 1, true)]
    public void Later_HappensAtItsPhase(string when, int turns, bool summoned)
    {
        const long id = 100099712;
        new EngineScenario(DestroyThenSummon(id, when)).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(id).Turns(turns).ExpectActivations(1)
            .Check(summoned ? $"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Fiend})" : $"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_GRAVE,0,1,nil,{Fiend})")
            .AssertPasses();
    }

    /// <summary>A card that leaves the GY before then is no longer "it": nothing is Summoned, even when it is back in the GY.</summary>
    [EngineFact]
    public void Later_ForgetsACardThatLeft()
    {
        const long id = 100099713;
        new EngineScenario(DestroyThenSummon(id, "standby_your")).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            // At the start of turn 2 the Fiend is shuffled into the Deck and sent back to the GY: a new card there.
            .Setup("local we=Effect.GlobalEffect() we:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS) we:SetCode(EVENT_PHASE_START+PHASE_DRAW) " +
                   $"we:SetOperation(function() if Duel.GetTurnCount()==2 then local g=Duel.GetMatchingGroup(Card.IsCode,1,LOCATION_GRAVE,0,nil,{Fiend}) Duel.SendtoHand(g,nil,REASON_RULE) Duel.SendtoGrave(g,REASON_RULE) end end) Duel.RegisterEffect(we,0)")
            .Activate(id).Turns(3).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_GRAVE,0,1,nil,{Fiend})")
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Fiend})")
            .AssertPasses();
    }

    /// <summary>"Special Summon 1 monster from your GY, and return it to the hand at the end of the Battle Phase" (a Quick Effect during the Battle Phase).</summary>
    [EngineFact]
    public void Later_AtTheEndOfTheBattlePhase()
    {
        const long id = 100099718;
        var p = Monster(id, new EffectEvent
        {
            When = B("when.quick"),
            If = [B("if.phase").With("phase", "battle")],
            Do = [B("do.special_summon").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster }),
                  B(LaterAction.BlockId).With("how", "hand").With("when", "battle_end").Linked(ActionLink.IfYouDo)],
        });
        Assert.EndsWith("Special Summon 1 monster from your GY, and if you do, return it to the hand at the end of the Battle Phase.", EffectTextGenerator.Generate(p));
        // The opponent's turn goes to the Battle Phase (the scenario has an attacker), where this card's Quick Effect is used.
        new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "grave").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Attack(id).Activate(id).Turns(2).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,{Warrior})")
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Warrior})")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- banished until a Standby Phase

    private static CardProject BanishUntil(long id, string until) => Monster(id, Ignition(
        B("do.banish").With("target", true).With("until", until).With("cards", OppMonster)));

    [Fact]
    public void BanishUntil_ReadsAsCardText()
    {
        Assert.Equal("Once per turn: You can target 1 monster your opponent controls; banish it until your next Standby Phase.",
            EffectTextGenerator.Generate(BanishUntil(100099714, "standby_your")));
        Assert.Contains("Duel.Remove(g,0,REASON_EFFECT+REASON_TEMPORARY)", Lua(BanishUntil(100099714, "standby_next")));
    }

    [EngineTheory]
    [InlineData("standby_your", 2, false)]
    [InlineData("standby_your", 3, true)]
    [InlineData("standby_next", 1, false)]
    [InlineData("standby_next", 2, true)]
    [InlineData("standby_opp", 1, false)]
    [InlineData("standby_opp", 2, true)]
    public void BanishUntil_ReturnsAtThatStandbyPhase(string until, int turns, bool back)
    {
        const long id = 100099715;
        new EngineScenario(BanishUntil(id, until)).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(id).Turns(turns).ExpectActivations(1)
            .Check(back ? $"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{Fiend})" : $"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_REMOVED,0,1,nil,{Fiend})")
            .AssertPasses();
    }

    /// <summary>
    /// A face-down monster is banished face-down and comes back face-down, as official temporary banishes (position 0) do it:
    /// "until the End Phase" (S:P Little Knight) and "until the next Standby Phase".
    /// </summary>
    [EngineTheory]
    [InlineData("end_phase", 1, false)]
    [InlineData("end_phase", 0, true)]
    [InlineData("standby_next", 1, true)]
    [InlineData("standby_next", 2, false)]
    public void BanishUntil_KeepsAFaceDownCardFaceDown(string until, int turns, bool banished)
    {
        const long id = 100099720;
        var p = BanishUntil(id, until);
        p.Effects[0].Do[0].Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone };
        var s = new EngineScenario(p);
        // Turns 0: what the banished card was like when turn 1's End Phase started.
        if (turns == 0)
        {
            s.Setup("ccfd=nil local we=Effect.GlobalEffect() we:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS) we:SetCode(EVENT_PHASE_START+PHASE_END) " +
                    $"we:SetOperation(function() local c=Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_REMOVED,0,nil,{Fiend}) if ccfd==nil then ccfd=c and c:IsFacedown() end end) Duel.RegisterEffect(we,0)");
            turns = 1;
        }
        s.Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            // Set face-down at the start of turn 1 (setup lines can't move cards themselves).
            .Setup("local we=Effect.GlobalEffect() we:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS) we:SetCode(EVENT_PHASE_START+PHASE_DRAW) " +
                   $"we:SetOperation(function() if Duel.GetTurnCount()==1 then Duel.ChangePosition(Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{Fiend}),POS_FACEDOWN_DEFENSE) end end) Duel.RegisterEffect(we,0)")
            .Activate(id).Turns(turns).ExpectActivations(1)
            .Check(banished && until == "end_phase" ? "ccfd==true"
                : banished ? $"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_REMOVED,0,nil,{Fiend}):IsFacedown()"
                : $"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{Fiend}):IsPosition(POS_FACEDOWN_DEFENSE)")
            .AssertPasses();
    }

    /// <summary>A banished card that left banishment (and was banished again) before then is not returned.</summary>
    [EngineFact]
    public void BanishUntil_DoesNotReturnACardThatLeft()
    {
        const long id = 100099719;
        new EngineScenario(BanishUntil(id, "standby_your")).Card(id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Setup("local we=Effect.GlobalEffect() we:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_CONTINUOUS) we:SetCode(EVENT_PHASE_START+PHASE_DRAW) " +
                   $"we:SetOperation(function() if Duel.GetTurnCount()==2 then local g=Duel.GetMatchingGroup(Card.IsCode,1,LOCATION_REMOVED,0,nil,{Fiend}) Duel.SendtoGrave(g,REASON_RULE) Duel.Remove(g,POS_FACEUP,REASON_RULE+REASON_TEMPORARY) end end) Duel.RegisterEffect(we,0)")
            .Activate(id).Turns(3).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_REMOVED,0,1,nil,{Fiend})")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- official scripts read into blocks

    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, $"c{id}.lua"));

    private static CardData Card(long id) => new() { Id = id, Name = "Translated " + id, Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 };

    /// <summary>The translated effect of <paramref name="script"/> whose blocks include <paramref name="block"/>; it compiles cleanly.</summary>
    private static EffectEvent Translated(long script, CardData card, string block)
    {
        var result = ScriptTranslator.Translate(Script(script), script, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(e => e.Do.Concat(e.Cost).Any(b => b.BlockId == block));
        Assert.True(effect is not null, string.Join("\n", result.Pieces.Select(p => $"{p.Piece?.Title}: {p.Reason}")));
        Lua(new CardProject { Card = card, Effects = [effect!] });
        return effect!;
    }

    private static string? Reason(long script, string title) =>
        ScriptTranslator.Translate(Script(script), script, Card(100099790)).Pieces.First(p => p.Piece?.Title == title).Reason;

    /// <summary>Cosmic Tree Irmistil: "pay LP in multiples of 1000 (max. 3000); this card gains that much ATK".</summary>
    [EngineFact]
    public void CosmicTreeIrmistil_TheLpPaid_ReadsIntoBlocks_AndPlays()
    {
        var card = Card(100099791);
        var effect = Translated(74139959, card, "cost.pay_lp");
        var cost = effect.Cost.Single();
        Assert.Equal(("multiple", "1000", "3000"), (cost.Values["how"], cost.Values["step"], cost.Values["max"]));
        Assert.Equal("lp_paid", effect.Do.Single().Values["value"]);
        var project = new CardProject { Card = card, Effects = [effect] };
        Assert.EndsWith("You can pay LP in multiples of 1000 (max. 3000); this card gains that much ATK.", EffectTextGenerator.Generate(project));
        new EngineScenario(project).Card(card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Declare(2000).Activate(card.Id).ExpectActivations(1)
            .Check("Duel.GetLP(0)==6000")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{card.Id}):GetAttack()==3000")
            .AssertPasses();
    }

    /// <summary>
    /// Number 70: Malevolent Sin: "target 1 monster your opponent controls; banish it until your opponent's next Standby
    /// Phase" (a flag that marks the same card, a Standby Phase effect of the opponent's turn). Played without its cost.
    /// </summary>
    [EngineTheory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void MalevolentSin_BanishUntilYourOpponentsNextStandbyPhase_ReadsIntoBlocks_AndPlays(int turns, bool back)
    {
        var card = Card(100099792);
        var effect = Translated(80796456, card, "do.banish");
        var banish = effect.Do.Single();
        Assert.Equal(("standby_opp", "true"), (banish.Values["until"], banish.Values["target"]));
        Assert.EndsWith("target 1 monster your opponent controls; banish it until your opponent's next Standby Phase.",
            EffectTextGenerator.Generate(new CardProject { Card = card, Effects = [effect] }));
        effect.Cost.Clear();
        new EngineScenario(new CardProject { Card = card, Effects = [effect] }).Card(card.Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(card.Id).Turns(turns).ExpectActivations(1)
            .Check(back ? $"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{Fiend})" : $"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_REMOVED,0,1,nil,{Fiend})")
            .AssertPasses();
    }

    /// <summary>
    /// Shapes that behave differently stay script: Past Image (a Trap, used at any time, returning the card without checking
    /// it is the same), Junk Archer (an End Phase return without that check), Psychic Blade (the LP paid kept in a flag by
    /// the cost itself), Vampire Fraulein (the LP paid with a flag of its own).
    /// </summary>
    [EngineTheory]
    [InlineData(50292967, "Remove")]
    [InlineData(42810973, "Remove")]
    [InlineData(75539614, "Activate")]
    [InlineData(6039967, null)]
    public void OtherShapes_StayScript(long script, string? title)
    {
        var pieces = ScriptTranslator.Translate(Script(script), script, Card(100099793)).Pieces;
        if (title is not null) Assert.NotNull(Reason(script, title));
        Assert.DoesNotContain(pieces.SelectMany(p => p.Effects).SelectMany(e => e.Do.Concat(e.Cost)),
            b => b.Values.GetValueOrDefault("how") == "multiple" || b.Values.GetValueOrDefault("until") is "standby_your" or "standby_next" or "standby_opp");
    }
}
