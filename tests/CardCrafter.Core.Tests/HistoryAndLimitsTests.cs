using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.ConditionCards;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Summon and duel history, trigger details, activation checks and limits, built from blocks: what the cards read, that
/// they compile cleanly, and that they play in the engine as their official cards do.
/// </summary>
public class HistoryAndLimitsTests
{
    private static string Text(CardProject p) => EffectTextGenerator.Generate(p);

    private static string Lua(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Empty(ScriptChecker.Check(r.Lua));
        return r.Lua;
    }

    private static EngineScenario Duel(params CardProject[] cards) => new EngineScenario(cards).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5);

    private static string In(long id, int player, string location) => $"Duel.IsExistingMatchingCard(Card.IsCode,{player},{location},0,1,nil,{id})";

    private static string Hand(int player, int n) => $"Duel.GetFieldGroupCount({player},LOCATION_HAND,0)=={n}";

    // ---------------------------------------------------------------- card text

    [Fact]
    public void EveryCard_CompilesCleanly()
    {
        foreach (var p in All()) Lua(p);
    }

    [Fact]
    public void SummonHistory_ReadsLikeTheCards()
    {
        Assert.Equal("If this card was Normal or Special Summoned this turn: You can draw 1 card. You can only use this effect of \"Kashtira Riseheart\" once per turn.", Text(Riseheart()));
        Assert.Equal("When your opponent activates a monster effect, or when an opponent's monster declares an attack, except during the turn this card was Special Summoned (Quick Effect): " +
                     "You can banish this card; gain 1000 LP. You can only use this effect of \"Invoked Magistus Heruvim\" once per turn.", Text(Heruvim()));
        Assert.Contains("this card was Special Summoned by the effect of a Synchro Monster", Text(Yummy()));
        Assert.Equal("Face-up monsters that were not Normal or Special Summoned this turn you control gain 500 ATK.", Text(Mimighoul()));
        Assert.Contains("IsStatus(STATUS_SUMMON_TURN+STATUS_SPSUMMON_TURN)", Lua(Riseheart()));
        Assert.Contains("not e:GetHandler():IsStatus(STATUS_SPSUMMON_TURN)", Lua(Heruvim()));
        // The Yummy monsters' watcher: a Synchro Monster's monster effect leaves a flag on the card it Special Summons.
        var yummy = Lua(Yummy());
        Assert.Contains("re:IsActiveType(TYPE_MONSTER) and re:GetHandler():IsType(TYPE_SYNCHRO)", yummy);
        Assert.Contains("RESET_EVENT+RESETS_STANDARD-RESET_TOFIELD-RESET_TEMP_REMOVE", yummy);
    }

    [Fact]
    public void DuelHistory_ReadsLikeTheCards()
    {
        var pack = new CardPack { Cards = [Immortal(), Ankh()] };
        Assert.Equal("During the End Phase, if \"The Immortal Sun God\" was sent to your GY this turn: You can add this card from your GY to your hand. " +
                     "You can only use this effect of \"Gil Garth, Exterminator of the Sun God\" once per turn.", EffectTextGenerator.Generate(GilGarth(), pack));
        Assert.StartsWith("If this card is in your hand and you have successfully activated \"Millennium Ankh\" this Duel: ", EffectTextGenerator.Generate(Heart(), pack));
        Assert.StartsWith("If this card was activated this turn: You can draw 1 card.", Text(Leading()));
        Assert.StartsWith("If your opponent has activated a monster effect during your Main Phase this turn: Draw 2 cards.", Text(Tactics()));
        Assert.Equal("Once per turn, at the end of the Battle Phase, if this card destroyed a monster by battle: You can gain 500 LP.", Text(Diablosis()));
        Assert.StartsWith("If your opponent activates a Spell/Trap Card or effect while this card is already face-up in your Spell & Trap Zone: ", Text(Preparations()));
        Assert.StartsWith("During the Main Phase, if this card is in your hand and your opponent Normal or Special Summoned 5 or more monsters this turn (Quick Effect): ", Text(Nibiru()));
        Assert.StartsWith("During the Main Phase, if this card is in your hand and your opponent Normal or Special Summoned a monster(s) 2 or more times this turn (Quick Effect): ",
            Text(Nibiru(2, "times")));
        Assert.Equal("While you activated a monster effect this turn, you cannot activate monster effects. While your opponent activated a monster effect this turn, your opponent cannot activate monster effects.",
            Text(Caliga()));
        Assert.StartsWith("If a card(s) is added from the Main Deck to your opponent's hand, except during the Draw Phase (Quick Effect): ", Text(Droll()));
    }

    [Fact]
    public void DuelHistory_Watchers()
    {
        // The activation leaves its flag: the plain activation of a Continuous Spell gets a cost that sets it.
        var leading = Lua(Leading());
        Assert.Contains("e0:SetCost(s.e0cost)", leading);
        Assert.Contains("RegisterFlagEffect(id+8210,RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,EFFECT_FLAG_OATH,1)", leading);
        // A card sent to the GY marks its controller; a resolved activation marks who activated it, for the whole Duel.
        Assert.Contains("hw:SetCode(EVENT_TO_GRAVE)", Lua(GilGarth()));
        var heart = Lua(Heart());
        Assert.Contains("hw:SetCode(EVENT_CHAIN_SOLVED)", heart);
        Assert.Contains("Duel.RegisterFlagEffect(rp,id+8230,0,0,1)", heart);
        // Triple Tactics Talent: the activity counter only counts monster effects activated during your Main Phase.
        Assert.Contains("return not (re:IsActiveType(TYPE_MONSTER) and (ph==PHASE_MAIN1 or ph==PHASE_MAIN2) and Duel.GetTurnPlayer()~=tp)", Lua(Tactics()));
        Assert.Contains("SetCode(EVENT_BATTLE_DESTROYING)", Lua(Diablosis()));
        Assert.Contains("e:GetHandler():IsStatus(STATUS_EFFECT_ENABLED)", Lua(Preparations()));
        // Nibiru counts the monsters, from both kinds of Summon.
        var nibiru = Lua(Nibiru());
        Assert.Contains("for i=1,eg:FilterCount(Card.IsSummonPlayer,nil,1-tp) do", nibiru);
        Assert.Contains("SetCode(EVENT_SUMMON_SUCCESS)", nibiru);
        Assert.Contains("SetCode(EVENT_SPSUMMON_SUCCESS)", nibiru);
        // Invoked Caliga counts on itself, only while it is on the field.
        var caliga = Lua(Caliga());
        Assert.Contains("fw0:SetRange(LOCATION_MZONE)", caliga);
        Assert.Contains("e:GetHandler():GetFlagEffect(id+", caliga);
        // The activation's flag needs a card that is activated.
        var monster = Leading();
        monster.Card = new CardData { Id = LeadingId, Name = "Not a Spell", Frame = MonsterFrame.Effect };
        Assert.True(ScriptCompiler.Compile(monster).HasErrors);
    }

    [Fact]
    public void TriggerDetails_ReadLikeTheCards()
    {
        Assert.Contains("If this card is sent to the GY, except by battle: You can gain 1000 LP.", Text(Reindsurm()));
        Assert.StartsWith("If this card is added from the Deck or GY to your hand, except by drawing it: ", Text(GearBox()));
        Assert.EndsWith("When 2 monsters are Special Summoned at the same time to the zones this card points to: You can gain 1000 LP.", Text(Genius()));
        Assert.StartsWith("When you draw for your normal draw in your Draw Phase: You can banish this card from your GY; ", Text(CounterSpell()));
        Assert.EndsWith("When this card or your monster this card points to inflicts battle damage to your opponent: You can gain 500 LP. " +
                        "If a player gains LP, even during the Damage Step: You can inflict 1000 damage to your opponent. You can only use each effect of \"Sorcerer of Sebek\" once per turn.", Text(Sebek()));
        Assert.StartsWith("If a card or effect activation is negated (except during the Damage Step): ", Text(Toddler()));
        Assert.Equal("You can Special Summon this card (from your hand). If Summoned this way, during your opponent's next End Phase: Return this card to the hand.", Text(Jester()));
        Assert.StartsWith("If another face-up Fusion Monster(s) you control is sent to the GY, except by battle: ", Text(Aiwass()));
        Assert.Contains("eg:FilterCount(", Lua(Genius()));
        Assert.Contains("EFFECT_FLAG_DAMAGE_STEP+EFFECT_FLAG_DAMAGE_CAL", Lua(Sebek()));
        Assert.Contains("aux.AddThisCardInGraveAlreadyCheck(c)", Lua(Roller()));
        Assert.Contains("EFFECT_TYPE_QUICK_O", Lua(Droll()));
        Assert.Contains("RESET_PHASE+PHASE_END+RESET_OPPO_TURN", Lua(Jester()));
    }

    [Fact]
    public void ActivationChecks_ReadLikeTheCards()
    {
        Assert.Equal("When a card or effect is activated that includes any of these effects (Quick Effect): You can discard this card; negate that effect." +
                     $"{EffectTextGenerator.OptionLine}Add a card from the Deck to the hand.{EffectTextGenerator.OptionLine}Special Summon from the Deck." +
                     $"{EffectTextGenerator.OptionLine}Send a card from the Deck to the GY. You can only use this effect of \"Ash Blossom & Joyous Spring\" once per turn.", Text(Ash()));
        Assert.StartsWith("When your opponent activates a card or effect that includes an effect that Special Summons a monster(s) from the Deck (Quick Effect): ", Text(Karin()));
        Assert.StartsWith("When your opponent activates a card or effect in response to your Fusion Monster's effect activation (Quick Effect): You can banish this card; ", Text(Virakam()));
        Assert.Equal("Once per turn, when a Spell/Trap Card, or monster effect, is activated (Quick Effect): You can send the same type of card (Monster, Spell, or Trap) from your hand to the GY; " +
                     "negate the activation, and if you do, banish that card.", Text(Mechaba()));
        Assert.Contains("Duel.GetChainInfo(ev-1,CHAININFO_TRIGGERING_EFFECT,CHAININFO_TRIGGERING_PLAYER)", Lua(Virakam()));
        Assert.Contains("bit.band(re:GetActiveType(),0x7)", Lua(Mechaba()));
        // "When this card is activated: You can add ...": the search doesn't stop the activation; it is asked if there is something to add.
        var lagoon = Lua(Lagoon());
        Assert.DoesNotContain("if chk==0 then return Duel.IsExistingMatchingCard", lagoon);
        Assert.Contains("if Duel.IsExistingMatchingCard(", lagoon);
    }

    [Fact]
    public void Limits_ReadLikeTheCards()
    {
        Assert.Equal("Once per turn: You can draw 1 card. You can only use this effect of \"Toon World the Perfect World\" thrice per turn.", Text(ToonWorld()));
        Assert.Equal("Once while face-up on the field: You can draw 1 card. You can only use this effect of \"Baronne de Fleur\" once per turn.", Text(Baronne()));
        Assert.Equal("Once while face-up on the field: You can draw 1 card.", Text(Baronne(OncePerTurn.FaceUp)));
        var twice = Baronne(OncePerTurn.Soft);
        twice.Effects[0].Times = 2;
        Assert.Equal("Up to twice per turn: You can draw 1 card.", Text(twice));
        var hard = Baronne(OncePerTurn.Hard);
        hard.Effects[0].Times = 2;
        Assert.Equal("You can draw 1 card. You can only use this effect of \"Baronne de Fleur\" twice per turn.", Text(hard));
        Assert.Contains("e1:SetCountLimit(2,id)", Lua(hard));
        Assert.Contains("e1:SetCountLimit(2)", Lua(twice));
        var toon = Lua(ToonWorld());
        Assert.Contains("e1:SetCountLimit(3,id)", toon);
        Assert.Contains("e:GetHandler():GetFlagEffect(id+8251)==0", toon);
        var baronne = Lua(Baronne());
        Assert.Contains("e1:SetCountLimit(1,id)", baronne);
        Assert.Contains("EFFECT_FLAG_NO_TURN_RESET", baronne);
        // Voloferniges' two effects share the card's one use.
        var volo = Lua(Volo());
        Assert.Contains("e1:SetCountLimit(1,EFFECT_COUNT_CODE_SINGLE)", volo);
        Assert.Contains("e2:SetCountLimit(1,EFFECT_COUNT_CODE_SINGLE)", volo);
        // A lone "once per turn" effect keeps a group number it had as a hard limit, and compiles as before.
        var lone = Baronne(OncePerTurn.Soft);
        lone.Effects[0].SharedLimitGroup = 1;
        Assert.Contains("e1:SetCountLimit(1)\n", Lua(lone));
    }

    // ---------------------------------------------------------------- summon history in the engine

    private const long SynchroId = 100320050, NotSynchroId = 100320051, ProcId = 100320052;

    /// <summary>A monster on the field whose effect Special Summons the Yummy monster from the hand.</summary>
    private static CardProject Summoner(long id, MonsterFrame frame) => new()
    {
        Card = new CardData { Id = id, Name = "Summoner " + id, Frame = frame, Level = 2, Atk = 1000, Def = 1000, Race = CardRace.Beast, Attribute = CardAttribute.Light },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.ignition"),
                Do = [B("do.special_summon").With("cards", new CardFilter { CardCode = YummyId, Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster })],
            },
        ],
    };

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Yummy_OnlyWhenASynchroMonstersEffectSummonedIt(bool synchro)
    {
        var id = synchro ? SynchroId : NotSynchroId;
        Duel(Yummy(), Summoner(id, synchro ? MonsterFrame.Synchro : MonsterFrame.Effect)).Card(id, 0, "mzone").Card(YummyId, 0, "hand")
            .Activate(id).Activate(YummyId).ExpectActivations(synchro ? 2 : 1)
            .Check(In(YummyId, 0, "LOCATION_MZONE")).Check(Hand(0, synchro ? 1 : 0)).AssertPasses();
    }

    [EngineTheory]
    [InlineData("hand", true)]
    [InlineData("mzone", false)]
    public void Riseheart_OnlyTheTurnItWasSummoned(string from, bool summoned)
    {
        var scenario = Duel(Riseheart()).Card(RiseheartId, 0, from);
        if (summoned) scenario.Summon(RiseheartId);
        scenario.Activate(RiseheartId).ExpectActivations(summoned ? 1 : 0).Check(Hand(0, summoned ? 1 : 0)).AssertPasses();
    }

    /// <summary>"Except during the turn this card was Special Summoned": a monster with its own Summon procedure.</summary>
    private static CardProject NotThisTurn() => new()
    {
        Card = new CardData { Id = ProcId, Name = "Not This Turn", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects =
        [
            new EffectEvent { When = B(SummonProcedureTrigger.BlockId) },
            new EffectEvent { When = B("when.ignition"), If = [B("if.summoned_this_turn").With("how", "special").With("was", "not")], Do = [Draw()] },
        ],
    };

    private static BlockInstance Draw() => B("do.draw");

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExceptTheTurnItWasSpecialSummoned(bool summoned)
    {
        var scenario = Duel(NotThisTurn()).Card(ProcId, 0, summoned ? "hand" : "mzone");
        if (summoned) scenario.SpSummon(ProcId);
        scenario.Activate(ProcId).ExpectActivations(summoned ? 0 : 1).Check(In(ProcId, 0, "LOCATION_MZONE")).AssertPasses();
    }

    // ---------------------------------------------------------------- duel history in the engine

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void GilGarth_ReturnsIfTheImmortalSunGodWentToTheGY(bool sent)
    {
        var scenario = Duel(GilGarth(), Immortal(), Sender()).Card(GilGarthId, 0, "grave").Card(ImmortalId, 0, "deck").Card(SenderId, 0, "szone");
        if (sent) scenario.Activate(SenderId);
        scenario.Activate(GilGarthId).ExpectActivations(sent ? 2 : 0)
            .Check(In(ImmortalId, 0, sent ? "LOCATION_GRAVE" : "LOCATION_DECK"))
            .Check(In(GilGarthId, 0, sent ? "LOCATION_HAND" : "LOCATION_GRAVE")).AssertPasses();
    }

    [EngineFact]
    public void Leading_ItsEffectWorksTheTurnItIsActivated()
    {
        Duel(Leading()).Card(LeadingId, 0, "szone").Activate(LeadingId, 2).ExpectActivations(2)
            .Check(In(LeadingId, 0, "LOCATION_SZONE")).Check(Hand(0, 1)).AssertPasses();
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Heart_AfterMillenniumAnkhWasActivated(bool activated)
    {
        var scenario = Duel(Heart(), Ankh()).Card(HeartId, 0, "hand").Card(AnkhId, 0, "szone");
        if (activated) scenario.Activate(AnkhId);
        scenario.Activate(HeartId).ExpectActivations(activated ? 2 : 0).Check(Hand(0, activated ? 2 : 1)).AssertPasses();
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Heart_ANegatedAnkhIsNotSuccessfullyActivated(bool negated)
    {
        const long counter = 100320053;
        var scenario = Duel(Heart(), Ankh(), NegatedActivationTests.Counter(counter, "nothing")).Card(HeartId, 0, "hand").Card(AnkhId, 0, "szone").Card(counter, 1, "szone")
            .Activate(AnkhId);
        if (negated) scenario.Activate(counter);
        // Negated, the Ankh never resolved: the Heart can't be used (2 activations either way).
        scenario.Activate(HeartId).ExpectActivations(2).Check(Hand(0, negated ? 1 : 2)).AssertPasses();
    }

    private const long OppQuickId = 100320054;

    /// <summary>The opponent's monster with a Quick Effect it uses during the Main Phase.</summary>
    private static CardProject OppQuick() => new()
    {
        Card = new CardData { Id = OppQuickId, Name = "Opp Quick", Frame = MonsterFrame.Effect, Level = 4, Atk = 500, Def = 500 },
        Effects = [new EffectEvent { When = B("when.quick"), Limit = OncePerTurn.Soft, If = [B("if.phase").With("phase", "main")], Do = [B("do.gain_lp").With("amount", 100)] }],
    };

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tactics_AfterTheOpponentActivatedAMonsterEffectInYourMainPhase(bool activated)
    {
        var scenario = Duel(Tactics(), OppQuick()).Card(TacticsId, 0, "szone").Card(OppQuickId, 1, "mzone").Card(Warrior, 0, "hand").Summon(Warrior);
        if (activated) scenario.Activate(OppQuickId);
        scenario.Activate(TacticsId).ExpectActivations(activated ? 2 : 0).Check(Hand(0, activated ? 2 : 0)).AssertPasses();
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Diablosis_AfterDestroyingAMonsterByBattle(bool destroys)
    {
        // The player going first can't attack: it attacks in its controller's first turn, turn 2.
        var scenario = Duel(Diablosis()).Card(DiablosisId, 1, "mzone");
        if (destroys) scenario.Card(Warrior, 0, "mzone");
        scenario.Attack(DiablosisId).Activate(DiablosisId).Turns(2).ExpectActivations(destroys ? 1 : 0)
            .Check(destroys ? "Duel.GetLP(1)==8500" : "Duel.GetLP(1)==8000").Check(destroys ? "Duel.GetLP(0)==6700" : "Duel.GetLP(0)==5200").AssertPasses();
    }

    private const long TwoSummonerId = 100320055;

    /// <summary>A Spell that Special Summons 2 monsters from the Deck at once.</summary>
    private static CardProject TwoSummoner() => new()
    {
        Card = new CardData { Id = TwoSummonerId, Name = "Two Summoner", Kind = CardKind.Spell },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.activate"),
                Do = [B("do.special_summon").With("count", 2).With("cards", new CardFilter { CardCode = Warrior, Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster })],
            },
        ],
    };

    [EngineTheory]
    [InlineData("cards", true)]
    [InlineData("times", false)]
    public void Nibiru_CountsTheMonstersSummoned(string counting, bool responds)
    {
        Duel(Nibiru(2, counting), TwoSummoner()).Card(NibiruId, 1, "hand").Card(TwoSummonerId, 0, "szone")
            .Activate(TwoSummonerId).Activate(NibiruId).ExpectActivations(responds ? 2 : 1)
            .Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_MZONE,0,nil,{Warrior})==2")
            .Check(responds ? "Duel.GetLP(1)==9000" : "Duel.GetLP(1)==8000").AssertPasses();
    }

    private const long DrawerA = 100320056, DrawerB = 100320057;

    private static CardProject Drawer(long id) => new()
    {
        Card = new CardData { Id = id, Name = "Drawer " + id, Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [new EffectEvent { When = B("when.ignition"), Do = [Draw()] }],
    };

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Caliga_EachPlayerActivatesOneMonsterEffectPerTurn(bool caliga)
    {
        var scenario = caliga ? Duel(Caliga(), Drawer(DrawerA), Drawer(DrawerB)).Card(CaligaId, 0, "mzone") : Duel(Drawer(DrawerA), Drawer(DrawerB));
        scenario.Card(DrawerA, 0, "mzone").Card(DrawerB, 0, "mzone").Activate(DrawerA).Activate(DrawerB)
            .ExpectActivations(caliga ? 1 : 2).Check(Hand(0, caliga ? 1 : 2)).AssertPasses();
    }

    [EngineTheory]
    [InlineData("face_up", 2)]
    [InlineData("duel", 1)]
    public void Caliga_CountsOnlyWhileItIsFaceUp(string watch, int uses)
    {
        // The second drawer can only be used once Caliga is out: counted only while Caliga is face-up, the first drawer's
        // effect (before it was Summoned) doesn't count; counted for the whole turn, it does.
        var caliga = Caliga();
        caliga.Card.Level = 4;
        caliga.Effects[0].If[0].Values["watch"] = watch;
        var second = Drawer(DrawerB);
        second.Effects[0].If.Add(B("if.card_present").With("where", "you").With("card", new CardFilter { CardCode = CaligaId, Owner = CardOwner.Either, Zones = Zone.Field, CardType = FilterCardType.Any, FaceUpOnly = true }));
        Duel(caliga, Drawer(DrawerA), second).Card(CaligaId, 0, "hand").Card(DrawerA, 0, "mzone").Card(DrawerB, 0, "mzone")
            .Activate(DrawerA).Summon(CaligaId).Activate(DrawerB).ExpectActivations(uses).Check(In(CaligaId, 0, "LOCATION_MZONE")).Check(Hand(0, uses)).AssertPasses();
    }

    // ---------------------------------------------------------------- trigger details in the engine

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Droll_RespondsToASearch(bool search)
    {
        var scenario = Duel(Droll(), Searcher(Warrior)).Card(DrollId, 1, "hand").Card(SearcherId, 0, "szone");
        if (search) scenario.Activate(SearcherId);
        scenario.Activate(DrollId).ExpectActivations(search ? 2 : 0)
            .Check(search ? "Duel.GetLP(1)==9000" : "Duel.GetLP(1)==8000").Check(In(DrollId, 1, search ? "LOCATION_GRAVE" : "LOCATION_HAND")).AssertPasses();
    }

    [EngineFact]
    public void Droll_NotForTheNormalDraw()
    {
        // The opponent's normal draw in their Draw Phase adds a card from their Deck to the hand too.
        Duel(Droll()).Card(DrollId, 0, "hand").Activate(DrollId).Turns(2).ExpectActivations(0).Check("Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineFact]
    public void Reindsurm_SentToTheGyByACost()
    {
        Duel(Reindsurm()).Card(ReindsurmId, 0, "hand").Activate(ReindsurmId, 2).ExpectActivations(2).Check("Duel.GetLP(0)==9100").AssertPasses();
    }

    [EngineFact]
    public void Reindsurm_NotWhenDestroyedByBattle()
    {
        var weak = Reindsurm();
        weak.Card.Atk = 100;
        Duel(weak).Card(ReindsurmId, 1, "mzone").Card(Dragon, 0, "mzone").Attack(ReindsurmId).Activate(ReindsurmId).Turns(2)
            .ExpectActivations(0).Check(In(ReindsurmId, 1, "LOCATION_GRAVE")).AssertPasses();
    }

    [EngineFact]
    public void GearBox_AddedByAnEffect()
    {
        Duel(GearBox(), Searcher(GearBoxId)).Card(GearBoxId, 0, "deck").Card(SearcherId, 0, "szone").Activate(SearcherId).Activate(GearBoxId)
            .ExpectActivations(2).Check(In(GearBoxId, 0, "LOCATION_HAND")).Check("Duel.GetLP(0)==9000").AssertPasses();
    }

    [EngineFact]
    public void CounterSpell_OnTheNormalDraw()
    {
        // The opponent's Draw Phase of turn 2: their normal draw.
        Duel(CounterSpell()).Card(CounterSpellId, 1, "grave").Activate(CounterSpellId).Turns(2).ExpectActivations(1)
            .Check("Duel.GetLP(1)==9000").Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_REMOVED,0,1,nil,{CounterSpellId})").AssertPasses();
    }

    [EngineFact]
    public void Roller_ATributedEarthMonsterBringsItBack()
    {
        Duel(Roller(), Tributer()).Card(RollerId, 0, "grave").Card(TributerId, 0, "szone").Card(Warrior, 0, "mzone")
            .Activate(TributerId).Activate(RollerId).ExpectActivations(2).Check(In(RollerId, 0, "LOCATION_MZONE")).AssertPasses();
    }

    private const long ToddlerSpellId = 100320058, ToddlerCounterId = 100320059;

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Toddler_WhenAnActivationIsNegated(bool negated)
    {
        var spell = new CardProject
        {
            Card = new CardData { Id = ToddlerSpellId, Name = "Toddler Spell", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [Draw()] }],
        };
        var scenario = Duel(Toddler(), spell, NegatedActivationTests.Counter(ToddlerCounterId, "nothing"))
            .Card(ToddlerId, 0, "mzone").Card(ToddlerSpellId, 0, "szone").Card(ToddlerCounterId, 1, "szone").Activate(ToddlerSpellId);
        if (negated) scenario.Activate(ToddlerCounterId);
        scenario.Activate(ToddlerId).ExpectActivations(negated ? 3 : 1).Check(negated ? "Duel.GetLP(0)==8700" : "Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineTheory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void Jester_ReturnsDuringTheOpponentsNextEndPhase(int turns, bool stillThere)
    {
        Duel(Jester()).Card(JesterId, 0, "hand").SpSummon(JesterId).Turns(turns)
            .Check(In(JesterId, 0, stillThere ? "LOCATION_MZONE" : "LOCATION_HAND")).AssertPasses();
    }

    [EngineFact]
    public void Jester_NotSummonedThisWay_Stays()
    {
        Duel(Jester()).Card(JesterId, 0, "mzone").Turns(2).Check(In(JesterId, 0, "LOCATION_MZONE")).AssertPasses();
    }

    // ---------------------------------------------------------------- activation checks in the engine

    private const long CyberseId = 100320060;

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Lagoon_CanBeActivatedWithNothingToAdd(bool cyberse)
    {
        var lagoon = Lagoon();
        lagoon.Card.SpellKind = SpellKind.Continuous;
        var target = new CardProject { Card = new CardData { Id = CyberseId, Name = "Water Cyberse", Frame = MonsterFrame.Normal, Race = CardRace.Cyberse, Attribute = CardAttribute.Water, Level = 4, Atk = 1000, Def = 1000 } };
        var scenario = Duel(lagoon, target).Card(LagoonId, 0, "hand");
        if (cyberse) scenario.Card(CyberseId, 0, "deck");
        scenario.Activate(LagoonId).ExpectActivations(1).Check(In(LagoonId, 0, "LOCATION_SZONE"))
            .Check(cyberse ? In(CyberseId, 0, "LOCATION_HAND") : Hand(0, 0)).AssertPasses();
    }

    private const long LpSpellId = 100320061;

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ash_NegatesASearchButNotOtherEffects(bool search)
    {
        var lp = new CardProject
        {
            Card = new CardData { Id = LpSpellId, Name = "LP Spell", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.gain_lp").With("amount", 1000)] }],
        };
        var spell = search ? SearcherId : LpSpellId;
        Duel(Ash(), Searcher(Warrior), lp).Card(AshId, 1, "hand").Card(spell, 0, "szone").Activate(spell).Activate(AshId)
            .ExpectActivations(search ? 2 : 1).Check(search ? Hand(0, 0) : "Duel.GetLP(0)==9000")
            .Check(In(AshId, 1, search ? "LOCATION_GRAVE" : "LOCATION_HAND")).AssertPasses();
    }

    private const long MechabaSpellId = 100320062;

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mechaba_SendsTheSameTypeOfCard(bool spellInHand)
    {
        var oppSpell = new CardProject
        {
            Card = new CardData { Id = MechabaSpellId, Name = "Mechaba Target", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.gain_lp").With("amount", 1000)] }],
        };
        Duel(Mechaba(), oppSpell).Card(MechabaId, 0, "mzone").Card(spellInHand ? Spell : Fiend, 0, "hand").Card(MechabaSpellId, 1, "hand")
            .Activate(MechabaSpellId).Activate(MechabaId).Turns(2).ExpectActivations(spellInHand ? 2 : 1)
            .Check(spellInHand ? "Duel.GetLP(1)==8000" : "Duel.GetLP(1)==9000").AssertPasses();
    }

    // ---------------------------------------------------------------- limits in the engine

    [EngineTheory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public void ToonWorld_EachCopyOnceAndTwiceByName(int copies, int uses)
    {
        Duel(ToonWorld(2)).Card(ToonWorldId, 0, "mzone", copies).Activate(ToonWorldId, 5).ExpectActivations(uses).Check(Hand(0, uses)).AssertPasses();
    }

    [EngineTheory]
    [InlineData(OncePerTurn.FaceUp, 1)]
    [InlineData(OncePerTurn.Soft, 2)]
    public void Baronne_OnceWhileFaceUp(OncePerTurn limit, int uses)
    {
        // Turn 1 and turn 3 are this player's: once while face-up is not given back on the next turn.
        Duel(Baronne(limit)).Card(BaronneId, 0, "mzone").Activate(BaronneId, 4).Turns(3).ExpectActivations(uses).AssertPasses();
    }

    [EngineFact]
    public void Baronne_FaceUpAndOncePerTurnByName()
    {
        Duel(Baronne()).Card(BaronneId, 0, "mzone", 2).Activate(BaronneId, 4).ExpectActivations(1).Check(Hand(0, 1)).AssertPasses();
    }

    [EngineFact]
    public void Volo_TwoEffectsShareOneUse()
    {
        Duel(Volo()).Card(VoloId, 0, "mzone").Activate(VoloId, 3).ExpectActivations(1).Check(Hand(0, 1)).AssertPasses();
    }

    [EngineFact]
    public void UpToTwicePerTurn()
    {
        var twice = Baronne(OncePerTurn.Soft);
        twice.Effects[0].Times = 2;
        Duel(twice).Card(BaronneId, 0, "mzone").Activate(BaronneId, 4).ExpectActivations(2).Check(Hand(0, 2)).AssertPasses();
    }
}
