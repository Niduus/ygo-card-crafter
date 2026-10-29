using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Turn structure, hand size and Life Point rules (Time Seal, Number 60, An Unfortunate Report, Hieroglyph Lithograph,
/// Darklord Nurse Reficule, Goblin of Greed, G.B. Hunter, Terminal World NEXT), played in real duels.
/// </summary>
public class TurnRulesTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    /// <summary>A Normal Spell that does <paramref name="dos"/>.</summary>
    private static CardProject Spell(long id, params BlockInstance[] dos) => new()
    {
        Card = new CardData { Id = id, Name = "Rule Spell " + id, Kind = CardKind.Spell, SpellKind = SpellKind.Normal },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [.. dos] }],
    };

    /// <summary>A monster whose lasting effects are <paramref name="passives"/>.</summary>
    private static CardProject Floodgate(long id, params BlockInstance[] passives) => new()
    {
        Card = new CardData { Id = id, Name = "Gate " + id, Level = 4, Atk = 0, Def = 2000 },
        Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [.. passives] }],
    };

    /// <summary>A monster with an Ignition effect that does <paramref name="dos"/> after <paramref name="costs"/>.</summary>
    private static CardProject Igniter(long id, BlockInstance[] dos, params BlockInstance[] costs) => new()
    {
        Card = new CardData { Id = id, Name = "Igniter " + id, Level = 4, Atk = 0, Def = 2000 },
        Effects = [new EffectEvent { When = B("when.ignition"), Cost = [.. costs], Do = [.. dos] }],
    };

    private static string Hand(int player, int n) => $"Duel.GetFieldGroupCount({player},LOCATION_HAND,0)=={n}";
    private static string Lp(int player, int n) => $"Duel.GetLP({player})=={n}";

    // ---------------------------------------------------------------- skipping phases and turns

    [Fact]
    public void SkipPhase_ReadsAsOfficialCards()
    {
        string Text(params (string, string)[] values) => EffectTextGenerator.Generate(Spell(100009000, B(SkipPhaseAction.BlockId, values)));
        Assert.Equal("Your opponent skips their next Draw Phase.", Text(("who", "opponent"), ("what", "draw")));
        Assert.Equal("Skip the Draw Phase of your opponent's next turn.", Text(("who", "opponent"), ("what", "draw"), ("when", "next_turn")));
        Assert.Equal("Your opponent skips their next Battle Phase.", Text(("who", "opponent"), ("what", "battle")));
        Assert.Equal("Skip the Battle Phase of your next turn.", Text(("who", "you"), ("what", "battle"), ("when", "next_turn")));
        Assert.Equal("Skip your next 2 Draw Phases.", Text(("who", "you"), ("what", "draw"), ("count", "2")));
        Assert.Equal("Your opponent skips their next Main Phase 1.", Text(("who", "opponent"), ("what", "main1")));
        Assert.Equal("Skip your next Standby Phase.", Text(("who", "you"), ("what", "standby")));
        Assert.Equal("Skip your opponent's next turn.", Text(("who", "opponent"), ("what", "turn")));
        Assert.Equal("Your opponent conducts their next Battle Phase twice.", Text(("who", "opponent"), ("what", "bp_twice")));
        Assert.Equal("You can conduct your next Battle Phase twice.", Text(("who", "you"), ("what", "bp_twice")));
    }

    [Fact]
    public void SkipPhase_CompilesToTheOfficialShapes()
    {
        string Lua(params (string, string)[] values) => ScriptCompiler.Compile(Spell(100009001, B(SkipPhaseAction.BlockId, values))).Lua;
        // Time Seal.
        var seal = Lua(("who", "opponent"), ("what", "draw"));
        Assert.Contains("if Duel.GetTurnPlayer()==1-tp and Duel.GetCurrentPhase()==PHASE_DRAW then", seal);
        Assert.Contains("sk:SetReset(RESET_PHASE+PHASE_DRAW+RESET_OPPO_TURN,2)", seal);
        Assert.Contains("sk:SetReset(RESET_PHASE+PHASE_DRAW+RESET_OPPO_TURN)\n", seal);
        // Number 60: "skip the Battle Phase of your next turn".
        var n60 = Lua(("who", "you"), ("what", "battle"), ("when", "next_turn"));
        Assert.Contains("if Duel.GetTurnPlayer()==tp then\n\t\tsk:SetLabel(Duel.GetTurnCount())", n60);
        Assert.Contains("sk:SetReset(RESET_PHASE+PHASE_END+RESET_SELF_TURN,2)", n60);
        Assert.Contains("return Duel.GetTurnCount()~=e:GetLabel()", n60);
        // An Unfortunate Report.
        var report = Lua(("who", "opponent"), ("what", "bp_twice"));
        Assert.Contains("sk:SetCode(EFFECT_BP_TWICE)", report);
        Assert.Contains("(Duel.GetCurrentPhase()>=PHASE_BATTLE_START and Duel.GetCurrentPhase()<=PHASE_BATTLE)", report);
        Assert.Contains("sk:SetReset(RESET_PHASE+PHASE_BATTLE+RESET_OPPO_TURN,1)", report);
        var r = ScriptCompiler.Compile(Spell(100009002, B(SkipPhaseAction.BlockId, ("who", "opponent"), ("what", "turn"))));
        Assert.False(r.HasErrors);
        Assert.DoesNotContain(ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
    }

    [EngineTheory]
    [InlineData("next")]
    [InlineData("next_turn")]
    public void TimeSeal_TheOpponentDrawsNothingNextTurn(string when)
    {
        var seal = Spell(100009010, B(SkipPhaseAction.BlockId, ("who", "opponent"), ("what", "draw"), ("when", when)));
        new EngineScenario(seal).Card(seal.Card.Id, 0, "hand").Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(seal.Card.Id).Turns(2).ExpectActivations(1).Check(Hand(1, 0)).AssertPasses();
    }

    [EngineFact]
    public void WithoutTimeSeal_TheOpponentDraws()
    {
        new EngineScenario().Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Turns(2).Check(Hand(1, 1)).AssertPasses();
    }

    [EngineFact]
    public void RecklessGreed_SkipsExactlyTheNextTwoDrawPhases()
    {
        var greed = Spell(100009020, B(SkipPhaseAction.BlockId, ("who", "you"), ("what", "draw"), ("count", "2")));
        // Turns 3 and 5 draw nothing; turn 7 draws again.
        new EngineScenario(greed).Card(greed.Card.Id, 0, "hand").Card(Dragon, 0, "deck", 8).Card(Warrior, 1, "deck", 8)
            .Activate(greed.Card.Id).Turns(7).Check(Hand(0, 1)).AssertPasses();
    }

    [EngineTheory]
    [InlineData("next", 8000)]
    [InlineData("next_turn", 8000)]
    public void FrozenSoul_TheOpponentCannotAttackNextTurn(string when, int lp)
    {
        var frozen = Spell(100009030, B(SkipPhaseAction.BlockId, ("who", "opponent"), ("what", "battle"), ("when", when)));
        new EngineScenario(frozen).Card(frozen.Card.Id, 0, "hand").Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "mzone").Card(Fiend, 1, "deck", 5)
            .Activate(frozen.Card.Id).Attack(Warrior).Turns(2).Check(Lp(0, lp)).AssertPasses();
    }

    [EngineFact]
    public void WithoutFrozenSoul_TheOpponentAttacks()
    {
        new EngineScenario().Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "mzone").Card(Fiend, 1, "deck", 5)
            .Attack(Warrior).Turns(2).Check(Lp(0, 6500)).AssertPasses();
    }

    /// <summary>
    /// Used in your Main Phase 1: "your next Battle Phase" is this turn's (Runick), "the Battle Phase of your next turn"
    /// is not (Number 60).
    /// </summary>
    [EngineTheory]
    [InlineData("next", 8000)]
    [InlineData("next_turn", 6500)]
    public void SkipYourOwnBattlePhase_ThisTurnOrTheNext(string when, int lp)
    {
        var skip = Spell(100009040, B(SkipPhaseAction.BlockId, ("who", "you"), ("what", "battle"), ("when", when)));
        new EngineScenario(skip).Card(Dragon, 0, "deck", 5).Card(skip.Card.Id, 1, "hand").Card(Warrior, 1, "mzone").Card(Fiend, 1, "deck", 5)
            .Activate(skip.Card.Id).Attack(Warrior).Turns(2).ExpectActivations(1).Check(Lp(0, lp)).AssertPasses();
    }

    [EngineFact]
    public void Number60_TheBattlePhaseOfYourNextTurnIsSkipped()
    {
        var skip = Spell(100009045, B(SkipPhaseAction.BlockId, ("who", "you"), ("what", "battle"), ("when", "next_turn")));
        // Attacks in turn 2, not in turn 4.
        new EngineScenario(skip).Card(Dragon, 0, "deck", 8).Card(skip.Card.Id, 1, "hand").Card(Warrior, 1, "mzone").Card(Fiend, 1, "deck", 8)
            .Activate(skip.Card.Id).Attack(Warrior).Attack(Warrior).Turns(4).Check(Lp(0, 6500)).AssertPasses();
    }

    [EngineFact]
    public void SixShinobi_TheOpponentsNextTurnIsSkipped()
    {
        var shinobi = Spell(100009050, B(SkipPhaseAction.BlockId, ("who", "opponent"), ("what", "turn")));
        new EngineScenario(shinobi).Card(shinobi.Card.Id, 0, "hand").Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "mzone").Card(Fiend, 1, "deck", 5)
            .Activate(shinobi.Card.Id).Attack(Warrior).Turns(2).Check(Hand(1, 0)).Check(Lp(0, 8000)).AssertPasses();
    }

    [EngineFact]
    public void TerminalWorld_BothPlayersSkipTheirDrawPhase()
    {
        var gate = Floodgate(100009060, B(SkipPhasePassive.BlockId, ("who", "both"), ("phase", "draw")));
        new EngineScenario(gate).Card(gate.Card.Id, 0, "mzone").Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Turns(3).Check(Hand(0, 0)).Check(Hand(1, 0)).AssertPasses();
        Assert.Equal("Both players skip their Draw Phase.", EffectTextGenerator.Generate(gate));
        var m2 = Floodgate(100009061, B(SkipPhasePassive.BlockId, ("who", "both"), ("phase", "main2")));
        Assert.Contains("SetCode(EFFECT_SKIP_M2)", ScriptCompiler.Compile(m2).Lua);
        Assert.Equal("Both players skip their Main Phase 2.", EffectTextGenerator.Generate(m2));
    }

    // ---------------------------------------------------------------- two Battle Phases

    [EngineFact]
    public void AgesOfStarsAndFrost_TwoBattlePhasesAttackTwice()
    {
        var twice = Spell(100009070, B(SkipPhaseAction.BlockId, ("who", "you"), ("what", "bp_twice")));
        new EngineScenario(twice).Card(Dragon, 0, "deck", 5).Card(twice.Card.Id, 1, "hand").Card(Warrior, 1, "mzone").Card(Fiend, 1, "deck", 5)
            .Activate(twice.Card.Id).Attack(Warrior).Attack(Warrior).Attack(Warrior).Turns(2).Check(Lp(0, 5000)).AssertPasses();
    }

    [EngineFact]
    public void WithoutASecondBattlePhase_AMonsterAttacksOnce()
    {
        new EngineScenario().Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "mzone").Card(Fiend, 1, "deck", 5)
            .Attack(Warrior).Attack(Warrior).Turns(2).Check(Lp(0, 6500)).AssertPasses();
    }

    [EngineFact]
    public void AnUnfortunateReport_TheOpponentsNextBattlePhaseIsTwice()
    {
        var report = Spell(100009075, B(SkipPhaseAction.BlockId, ("who", "opponent"), ("what", "bp_twice")));
        new EngineScenario(report).Card(report.Card.Id, 0, "hand").Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "mzone").Card(Fiend, 1, "deck", 5)
            .Activate(report.Card.Id).Attack(Warrior).Attack(Warrior).Turns(2).Check(Lp(0, 5000)).AssertPasses();
    }

    [Fact]
    public void CannotConductMainPhase2_IsALock()
    {
        var gate = Floodgate(100009078, B("passive.lock", ("who", "opponent"), ("what", "m2")));
        gate.Effects[0].Do[0].Filters["cards"] = new CardFilter { CardType = FilterCardType.Monster };
        Assert.Equal("Your opponent cannot conduct their Main Phase 2.", EffectTextGenerator.Generate(gate));
        Assert.Contains("SetCode(EFFECT_CANNOT_M2)", ScriptCompiler.Compile(gate).Lua);
    }

    // ---------------------------------------------------------------- the hand size limit

    [Fact]
    public void HandLimit_ReadsAsOfficialCards()
    {
        Assert.Equal("Your opponent's hand size limit becomes 5.", EffectTextGenerator.Generate(Floodgate(100009080, B(HandLimitPassive.BlockId, ("who", "opponent"), ("limit", "5")))));
        Assert.Equal("The hand size limit of each player becomes 3.", EffectTextGenerator.Generate(Floodgate(100009081, B(HandLimitPassive.BlockId, ("who", "both"), ("limit", "3")))));
        Assert.Equal("There is no limit to the hand size for each player.", EffectTextGenerator.Generate(Floodgate(100009082, B(HandLimitPassive.BlockId, ("who", "both"), ("mode", "none")))));
        Assert.Equal("For the rest of this Duel, your hand size limit becomes 7.", EffectTextGenerator.Generate(Spell(100009083, B(HandLimitAction.BlockId, ("limit", "7")))));
        Assert.Contains("SetValue(100)", ScriptCompiler.Compile(Floodgate(100009082, B(HandLimitPassive.BlockId, ("who", "both"), ("mode", "none")))).Lua);
    }

    [EngineTheory]
    [InlineData("duel", "limit", 7)]
    [InlineData("turn", "limit", 7)]
    [InlineData("duel", "none", 8)]
    public void HieroglyphLithograph_TheEndPhaseDiscardsDownToTheNewLimit(string duration, string mode, int kept)
    {
        var tablet = Spell(100009090, B(HandLimitAction.BlockId, ("duration", duration), ("mode", mode), ("limit", "7")));
        new EngineScenario(tablet).Card(tablet.Card.Id, 0, "hand").Card(Dragon, 0, "hand", 8).Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(tablet.Card.Id).Turns(1).Check(Hand(0, kept)).AssertPasses();
    }

    [EngineFact]
    public void SilentWobby_TheControllersHandLimitIsThree()
    {
        var wobby = Floodgate(100009095, B(HandLimitPassive.BlockId, ("who", "you"), ("limit", "3")));
        new EngineScenario(wobby).Card(wobby.Card.Id, 0, "mzone").Card(Dragon, 0, "hand", 6).Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Turns(1).Check(Hand(0, 3)).AssertPasses();
    }

    [EngineFact]
    public void WithoutAHandLimit_TheEndPhaseDiscardsDownToSix()
    {
        new EngineScenario().Card(Dragon, 0, "hand", 8).Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Turns(1).Check(Hand(0, 6)).AssertPasses();
    }

    // ---------------------------------------------------------------- Life Points

    [Fact]
    public void LifePointRules_ReadAsOfficialCards()
    {
        Assert.Equal("Any effect that would make your opponent gain LP inflicts the same amount of damage to them, instead.",
            EffectTextGenerator.Generate(Floodgate(100009100, B("passive.player_rule", ("rule", "opp_reverse_recover")))));
        Assert.Equal("Any effect that would inflict damage to you makes you gain that much LP, instead.",
            EffectTextGenerator.Generate(Floodgate(100009101, B("passive.player_rule", ("rule", "reverse_effect_damage")))));
        Assert.Equal("Until the end of this turn, each time you would take damage, gain that amount of LP instead.",
            EffectTextGenerator.Generate(Spell(100009102, B(TurnPlayerRuleAction.BlockId, ("rule", "reverse_damage")))));
        Assert.Equal("Any effect that would inflict damage to a player this turn increases their LP by the same amount, instead.",
            EffectTextGenerator.Generate(Spell(100009103, B(TurnPlayerRuleAction.BlockId, ("rule", "reverse_effect_both")))));
    }

    [EngineFact]
    public void DarklordNurseReficule_TheOpponentsGainBecomesDamage()
    {
        var nurse = Floodgate(100009110, B("passive.player_rule", ("rule", "opp_reverse_recover")));
        var giver = Igniter(100009111, [B("do.gain_lp", ("who", "opponent"), ("amount", "1000"))]);
        new EngineScenario(nurse, giver).Card(nurse.Card.Id, 0, "mzone").Card(giver.Card.Id, 0, "mzone").Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(giver.Card.Id).ExpectActivations(1).Check(Lp(1, 7000)).AssertPasses();
    }

    [EngineFact]
    public void OracleKingDArc_EffectDamageYouWouldTakeIsGained()
    {
        var king = Floodgate(100009120, B("passive.player_rule", ("rule", "reverse_effect_damage")));
        var burner = Igniter(100009121, [B("do.damage", ("who", "you"), ("amount", "1000"))]);
        new EngineScenario(king, burner).Card(king.Card.Id, 0, "mzone").Card(burner.Card.Id, 0, "mzone").Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(burner.Card.Id).ExpectActivations(1).Check(Lp(0, 9000)).AssertPasses();
    }

    [EngineFact]
    public void RainbowLife_DamageThisTurnIsGained()
    {
        var rainbow = Spell(100009130, B(TurnPlayerRuleAction.BlockId, ("rule", "reverse_damage")), B("do.damage", ("who", "you"), ("amount", "1000")));
        new EngineScenario(rainbow).Card(rainbow.Card.Id, 0, "hand").Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(rainbow.Card.Id).ExpectActivations(1).Check(Lp(0, 9000)).AssertPasses();
    }

    // ---------------------------------------------------------------- discards and the Deck

    [Fact]
    public void HandDeckLock_ReadsAsOfficialCards()
    {
        Assert.Equal("Cards on the field cannot be returned to the Deck.", EffectTextGenerator.Generate(Floodgate(100009140, B(HandDeckLockPassive.BlockId, ("what", "to_deck")))));
        var goblin = Floodgate(100009141, B(HandDeckLockPassive.BlockId, ("what", "discard_cost")));
        goblin.Effects[0].Do[0].Filters["cards"] = new CardFilter { Owner = CardOwner.Either, Zones = Zone.Hand };
        Assert.Equal("Neither player can activate an effect by discarding from their hand.", EffectTextGenerator.Generate(goblin));
        var opp = Floodgate(100009142, B(HandDeckLockPassive.BlockId, ("what", "discard")));
        opp.Effects[0].Do[0].Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Hand };
        Assert.Equal("Your opponent cannot discard cards.", EffectTextGenerator.Generate(opp));
        Assert.Contains("SetTarget(aux.TargetBoolFunction(Card.IsLocation,LOCATION_ONFIELD))", ScriptCompiler.Compile(Floodgate(100009140, B(HandDeckLockPassive.BlockId, ("what", "to_deck")))).Lua);
    }

    [EngineTheory]
    [InlineData("Either", 0)]
    [InlineData("Opponent", 1)]
    [InlineData("You", 0)]
    public void GoblinOfGreed_DiscardCostsCannotBePaid(string whose, int activations)
    {
        var goblin = Floodgate(100009150, B(HandDeckLockPassive.BlockId, ("what", "discard_cost")));
        goblin.Effects[0].Do[0].Filters["cards"] = new CardFilter { Owner = Enum.Parse<CardOwner>(whose), Zones = Zone.Hand };
        var discarder = Igniter(100009151, [B("do.draw")], B("cost.discard"));
        new EngineScenario(goblin, discarder).Card(goblin.Card.Id, 0, "mzone").Card(discarder.Card.Id, 0, "mzone").Card(Dragon, 0, "hand").Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(discarder.Card.Id).ExpectActivations(activations).AssertPasses();
    }

    [EngineFact]
    public void GBHunter_CardsOnTheFieldStay()
    {
        var hunter = Floodgate(100009160, B(HandDeckLockPassive.BlockId, ("what", "to_deck")));
        var bouncer = Igniter(100009161, [B("do.shuffle_into_deck")]);
        bouncer.Effects[0].Do[0].Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        new EngineScenario(hunter, bouncer).Card(hunter.Card.Id, 0, "mzone").Card(bouncer.Card.Id, 0, "mzone").Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "mzone").Card(Fiend, 1, "deck", 5)
            .Activate(bouncer.Card.Id).ExpectActivations(0).Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{Warrior})").AssertPasses();
    }

    // ---------------------------------------------------------------- zones

    [Fact]
    public void ZoneLimit_ReadsAsTerminalWorldNext()
    {
        var next = Floodgate(100009170, B(ZoneLimitPassive.BlockId));
        Assert.Equal("Cards cannot be placed on the field if it would give any player more than 3 monsters or more than 3 Spell/Trap Cards on their field.", EffectTextGenerator.Generate(next));
        var lua = ScriptCompiler.Compile(next).Lua;
        Assert.Contains("SetCode(EFFECT_MAX_MZONE)", lua);
        Assert.Contains("SetCode(EFFECT_MAX_SZONE)", lua);
        Assert.Contains("if r~=LOCATION_REASON_TOFIELD then return 5 end", lua);
    }

    [EngineFact]
    public void ZoneLimit_NoMoreMonstersThanTheLimit()
    {
        var gate = Floodgate(100009180, B(ZoneLimitPassive.BlockId, ("who", "you"), ("which", "monsters"), ("monsters", "1")));
        new EngineScenario(gate).Card(gate.Card.Id, 0, "mzone").Card(Warrior, 0, "hand").Card(Dragon, 0, "deck", 5).Card(Fiend, 1, "deck", 5)
            .Summon(Warrior).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,{Warrior})").AssertPasses();
    }

    // ---------------------------------------------------------------- official scripts read back

    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScripts!, $"c{id}.lua"));

    private static CardData SpellCard(long id, SpellKind kind = SpellKind.Normal) => new() { Id = id, Name = "Translated " + id, Kind = CardKind.Spell, SpellKind = kind };

    /// <summary>The block of <paramref name="blockId"/> the official script translates to.</summary>
    private static BlockInstance ReadBack(long id, CardData card, string blockId)
    {
        var result = ScriptTranslator.Translate(Script(id), id, card);
        var blocks = result.Pieces.Where(p => p.Translated).SelectMany(p => p.Effects).SelectMany(e => e.Do).ToList();
        return Assert.Single(blocks, b => b.BlockId == blockId);
    }

    [EngineTheory]
    // Time Seal, Solomon's Lawbook, An Unfortunate Report: the phase-long shape; Break the Destiny, Great Long Nose: until an End Phase.
    [InlineData(35316708, "opponent", "draw", "next", true)]
    [InlineData(23471572, "you", "standby", "next", true)]
    [InlineData(19763315, "opponent", "bp_twice", "next", false)]
    [InlineData(4008212, "opponent", "main1", "next", false)]
    [InlineData(2356994, "opponent", "battle", "next_turn", false)]
    [InlineData(61557074, "you", "battle", "next_turn", false)]
    public void OfficialSkips_ReadBack(long id, string who, string what, string when, bool unlessApplied)
    {
        var card = id is 2356994 ? new CardData { Id = id, Name = "Translated", Level = 4, Frame = MonsterFrame.Effect }
            : SpellCard(id, id == 61557074 ? SpellKind.Field : SpellKind.Normal);
        var b = ReadBack(id, card, SkipPhaseAction.BlockId);
        Assert.Equal(who, b.Values["who"]);
        Assert.Equal(what, b.Values["what"]);
        Assert.Equal(when, b.Values["when"]);
        Assert.Equal(unlessApplied ? "true" : "false", b.Values.GetValueOrDefault("unless_applied", "false"));
    }

    [EngineFact]
    public void TimeSeal_TranslatedPlaysLikeTheOriginal()
    {
        var card = SpellCard(100009200);
        var result = ScriptTranslator.Translate(Script(35316708), 35316708, card);
        Assert.True(result.Complete);
        var p = new CardProject { Card = card, Effects = result.Pieces.Where(x => x.Effect is not null).Select(x => x.Effect!).ToList() };
        Assert.Equal("Your opponent skips their next Draw Phase.", EffectTextGenerator.Generate(p));
        Assert.Contains("not Duel.IsPlayerAffectedByEffect(1-tp,EFFECT_SKIP_DP)", ScriptCompiler.Compile(p).Lua);
        new EngineScenario(p).Card(card.Id, 0, "hand").Card(Dragon, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(card.Id).Turns(2).ExpectActivations(1).Check(Hand(1, 0)).AssertPasses();
    }

    [EngineTheory]
    [InlineData(26022485, HandLimitPassive.BlockId, "who=opponent,mode=limit,limit=5")]
    [InlineData(48310593, HandLimitPassive.BlockId, "who=both,mode=limit,limit=3")]
    [InlineData(94163677, HandLimitPassive.BlockId, "who=both,mode=none")]
    [InlineData(10248192, HandLimitAction.BlockId, "duration=duel,who=you,mode=limit,limit=7")]
    [InlineData(54631834, SkipPhasePassive.BlockId, "who=both,phase=main2")]
    [InlineData(40633297, "passive.player_rule", "rule=opp_reverse_recover")]
    [InlineData(34002992, TurnPlayerRuleAction.BlockId, "rule=reverse_damage")]
    [InlineData(4130270, HandDeckLockPassive.BlockId, "what=to_deck")]
    public void OfficialRules_ReadBack(long id, string blockId, string values)
    {
        var card = id == 4130270 ? new CardData { Id = id, Name = "Translated", Level = 4, Frame = MonsterFrame.Effect }
            : SpellCard(id, id is 34002992 or 10248192 ? SpellKind.Normal : SpellKind.Continuous);
        var b = ReadBack(id, card, blockId);
        foreach (var kv in values.Split(','))
        {
            var (k, v) = (kv.Split('=')[0], kv.Split('=')[1]);
            Assert.Equal(v, b.Values[k]);
        }
    }

    [EngineFact]
    public void GoblinOfGreed_And_TerminalWorldNext_ReadBack()
    {
        var goblin = ReadBack(425934, new CardData { Id = 425934, Name = "Translated", Level = 2, Frame = MonsterFrame.Effect }, HandDeckLockPassive.BlockId);
        Assert.Equal("discard_cost", goblin.Values["what"]);
        Assert.Equal(CardOwner.Either, goblin.Filters["cards"].Owner);
        var result = ScriptTranslator.Translate(Script(48605591), 48605591, SpellCard(48605591, SpellKind.Continuous));
        var zones = result.Pieces.Where(p => p.Translated).SelectMany(p => p.Effects).SelectMany(e => e.Do).Where(b => b.BlockId == ZoneLimitPassive.BlockId).ToList();
        Assert.Equal(["monsters", "spells"], zones.Select(z => z.Values["which"]).Order());
        Assert.All(zones, z => Assert.Equal("both", z.Values["who"]));
    }

    [EngineFact]
    public void WithoutAZoneLimit_TheMonsterIsSummoned()
    {
        var gate = Floodgate(100009181, B(ZoneLimitPassive.BlockId, ("who", "opponent"), ("which", "monsters"), ("monsters", "1")));
        new EngineScenario(gate).Card(gate.Card.Id, 0, "mzone").Card(Warrior, 0, "hand").Card(Dragon, 0, "deck", 5).Card(Fiend, 1, "deck", 5)
            .Summon(Warrior).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{Warrior})").AssertPasses();
    }
}
