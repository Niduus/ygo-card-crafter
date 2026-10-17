using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The top of the Deck, draws, counters, LP and costs: banishing the top cards of a Deck (Kashtira, Card Crawl, Omega
/// Vidolium, Number 89: Diablosis), excavating the opponent's Deck (Kashtira Ogre), the normal draw (Bunnillin Extract,
/// Time Chain), Mulcharmy Fuwalos, counters equal to a value and moved (Autumn), Cubic Counters, Wicked Canon, Ultimate Sky,
/// The Winged Dragon of Ra, Sun God of Darkness, Nibiru, and The Sun God Leading Down into Darkness.
/// </summary>
public class DeckDrawCounterLpTests
{
    private const long Id = 100480001, Other = 100480002, TokenId = 100480003;

    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    private static CardProject Monster(params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = Id, Name = "Deck Tester", Frame = MonsterFrame.Effect, Race = CardRace.Fiend, Attribute = CardAttribute.Dark, Level = 4, Atk = 1800, Def = 1000 },
        Effects = [.. effects],
    };

    private static CardProject SpellCard(SpellKind kind, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = Id, Name = "Deck Spell", Kind = CardKind.Spell, SpellKind = kind },
        Effects = [.. effects],
    };

    private static EffectEvent Ignition(params BlockInstance[] dos) => new() { When = B("when.ignition"), Do = [.. dos] };

    private static string Compiles(CardProject p, CardPack? pack = null)
    {
        var r = ScriptCompiler.Compile(p, pack);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Empty(ScriptChecker.Check(r.Lua));
        return r.Lua;
    }

    private static string Text(CardProject p, CardPack? pack = null) => EffectTextGenerator.Generate(p, pack);

    private static string In(int player, string location, long code, int count = 1) =>
        $"Duel.GetMatchingGroupCount(Card.IsCode,{player},{location},0,nil,{code})=={count}";

    private static string Count(int player, string location, int count) => $"Duel.GetFieldGroupCount({player},{location},0)=={count}";

    private static string FaceDownBanished(int player, int count) => $"Duel.GetMatchingGroupCount(Card.IsFacedown,{player},LOCATION_REMOVED,0,nil)=={count}";

    private static CardFilter Theirs => new() { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    // ---------------------------------------------------------------- banishing the top of the Deck

    /// <summary>Kashtira Riseheart: "banish the top 3 cards of your opponent's Deck face-down, and if you banish any, this card's Level becomes 7".</summary>
    private static CardProject Riseheart() => Monster(Ignition(
        B(BanishTopAction.BlockId).With("whose", "opponent").With("facedown", true),
        B("do.self_level").With("mode", "becomes").With("amount", 7).With("duration", "always").Linked(ActionLink.IfYouDo)));

    [Fact]
    public void BanishTheTop3OfTheOpponentsDeck_ReadsLikeKashtiraRiseheart()
    {
        var p = Riseheart();
        Assert.Equal("You can banish the top 3 cards of your opponent's Deck face-down, and if you banish any, this card's Level becomes 7.", Text(p));
        var lua = Compiles(p);
        Assert.Contains("Duel.GetDecktopGroup(1-tp,", lua);
        Assert.Contains("Duel.DisableShuffleCheck()", lua);
        Assert.Contains("POS_FACEDOWN,REASON_EFFECT", lua);
    }

    [EngineFact]
    public void BanishTheTop3OfTheOpponentsDeck_BanishesThemFaceDown()
    {
        new EngineScenario(Riseheart()).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 5)
            .Activate(Id).ExpectActivations(1)
            .Check(FaceDownBanished(1, 3)).Check(Count(1, "LOCATION_DECK", 1))
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetLevel()==7").AssertPasses();
    }

    [EngineFact]
    public void BanishTheTop3OfTheOpponentsDeck_NeedsAll3()
    {
        new EngineScenario(Riseheart()).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 2)
            .Activate(Id).ExpectActivations(0).Check(Count(1, "LOCATION_DECK", 1)).AssertPasses();
    }

    /// <summary>Number 89: Diablosis the Mind Hacker: "banish cards from the top of your opponent's Deck, face-down, equal to their face-down banished cards".</summary>
    private static CardProject Diablosis() => Monster(Ignition(
        B(BanishTopAction.BlockId).With("whose", "opponent").With("facedown", true).With("by", "value").With("value", "count")
            .With(ValueSources.CardsKey, new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Banished, FaceDownOnly = true })));

    [Fact]
    public void BanishFromTheTopEqualToAValue_ReadsLikeDiablosis()
    {
        Assert.Equal("You can banish cards from the top of your opponent's Deck, face-down, equal to their face-down banished cards.", Text(Diablosis()));
        Assert.Contains("s.topremovable(tp,1-tp,Duel.GetMatchingGroup(", Compiles(Diablosis()));
    }

    [EngineFact]
    public void BanishFromTheTopEqualToAValue_BanishesThatMany()
    {
        // As many as the cards in the opponent's hand (2): none when they have none.
        var p = Monster(Ignition(B(BanishTopAction.BlockId).With("whose", "opponent").With("facedown", true).With("by", "value").With("value", "count")
            .With(ValueSources.CardsKey, new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Hand })));
        new EngineScenario(p).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "hand", 2).Card(Warrior, 1, "deck", 6)
            .Activate(Id).ExpectActivations(1).Check(FaceDownBanished(1, 2)).Check(Count(1, "LOCATION_DECK", 3)).AssertPasses();
        new EngineScenario(p).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 6)
            .Activate(Id).ExpectActivations(0).AssertPasses();
    }

    /// <summary>Omega Vidolium: "You can banish (face-down) the top 3 cards of your Deck; ...".</summary>
    private static CardProject Vidolium() => Monster(new EffectEvent
    {
        When = B("when.ignition"),
        Cost = [B(BanishTopCost.BlockId).With("facedown", true)],
        Do = [B("do.draw")],
    });

    [Fact]
    public void BanishTheTop3AsACost_ReadsLikeOmegaVidolium()
    {
        Assert.Equal("You can banish (face-down) the top 3 cards of your Deck; draw 1 card.", Text(Vidolium()));
        var lua = Compiles(Vidolium());
        Assert.Contains("Duel.GetDecktopGroup(tp,3):FilterCount(Card.IsAbleToRemoveAsCost,nil,POS_FACEDOWN)==3", lua);
        Assert.Contains("Duel.Remove(Duel.GetDecktopGroup(tp,3),POS_FACEDOWN,REASON_COST)", lua);
    }

    [EngineFact]
    public void BanishTheTop3AsACost_IsPaidAndNeedsTheCards()
    {
        new EngineScenario(Vidolium()).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 5)
            .Activate(Id).ExpectActivations(1).Check(FaceDownBanished(0, 3)).Check(Count(0, "LOCATION_DECK", 1)).AssertPasses();
        new EngineScenario(Vidolium()).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 2)
            .Activate(Id).ExpectActivations(0).Check(Count(0, "LOCATION_DECK", 2)).AssertPasses();
    }

    /// <summary>Card Crawl: "You can banish (face-down) the top 5, 10, or 15 cards of your Deck ...": each number is an option with its own cost.</summary>
    private static CardProject CardCrawl() => Monster(Ignition(
        B(ChooseEffectsAction.BlockId),
        B(BanishTopCost.BlockId).With("count", 5).With("facedown", true).Linked(ActionLink.Option),
        B("do.draw"),
        B(BanishTopCost.BlockId).With("count", 10).With("facedown", true).Linked(ActionLink.Option),
        B("do.draw").With("count", 2)));

    [Fact]
    public void BanishTheTopCardsAsAnOptionsCost_ReadsAsEachOption()
    {
        Assert.Equal("You can activate 1 of these effects;\n ● Banish (face-down) the top 5 cards of your Deck; draw 1 card.\n ● Banish (face-down) the top 10 cards of your Deck; draw 2 cards.", Text(CardCrawl()));
        var lua = Compiles(CardCrawl());
        Assert.Contains("(not e:IsCostChecked() or Duel.GetDecktopGroup(tp,10):FilterCount(Card.IsAbleToRemoveAsCost,nil,POS_FACEDOWN)==10)", lua);
    }

    [EngineFact]
    public void BanishTheTopCardsAsAnOptionsCost_PaysTheChosenOne()
    {
        // 8 cards: only the first option (5) can be paid.
        new EngineScenario(CardCrawl()).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 8)
            .Activate(Id).ExpectActivations(1).Check(FaceDownBanished(0, 5)).Check(Count(0, "LOCATION_DECK", 2)).Check(Count(0, "LOCATION_HAND", 1)).AssertPasses();
        new EngineScenario(CardCrawl()).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 14).Option(1)
            .Activate(Id).ExpectActivations(1).Check(FaceDownBanished(0, 10)).Check(Count(0, "LOCATION_HAND", 2)).AssertPasses();
    }

    // ---------------------------------------------------------------- excavating the opponent's Deck

    /// <summary>Kashtira Ogre: "excavate up to 5 cards from the top of your opponent's Deck, banish 1 excavated card, face-down, and place the rest on the top of the Deck in the same order".</summary>
    private static CardProject Ogre() => Monster(Ignition(
        B(ExcavateAction.BlockId).With("upto", true).With("count", 5).With("whose", "opponent").With("take", "banish").With("rest", "top")
            .With("cards", new CardFilter())));

    [Fact]
    public void ExcavateTheOpponentsDeck_ReadsLikeKashtiraOgre()
    {
        Assert.Equal("You can excavate up to 5 cards from the top of your opponent's Deck, banish 1 excavated card, face-down, and place the rest on the top of the Deck in the same order.", Text(Ogre()));
        var lua = Compiles(Ogre());
        Assert.Contains("Duel.AnnounceNumber(tp,table.unpack(nums))", lua);
        Assert.Contains("Duel.ConfirmDecktop(1-tp,", lua);
    }

    [EngineFact]
    public void ExcavateTheOpponentsDeck_BanishesOneFaceDown()
    {
        // 3 are excavated: the first is banished face-down, the other 2 stay on top.
        new EngineScenario(Ogre()).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "deck", 6).Declare(3)
            .Activate(Id).ExpectActivations(1).Check(FaceDownBanished(1, 1)).Check(Count(1, "LOCATION_DECK", 4)).AssertPasses();
    }

    [Fact]
    public void ExcavateYourDeck_StillReadsAndCompilesAsBefore()
    {
        var p = Monster(Ignition(B(ExcavateAction.BlockId)));
        Assert.Equal("You can excavate the top 3 cards of your Deck, and if you do, you can add 1 excavated monster to your hand, also send the rest to the GY.", Text(p));
        var lua = Compiles(p);
        Assert.Contains("if Duel.GetFieldGroupCount(tp,LOCATION_DECK,0)>=3 then", lua);
        Assert.Contains("Duel.ConfirmDecktop(tp,3)", lua);
    }

    // ---------------------------------------------------------------- the normal draw

    /// <summary>Bunnillin Extract: "While you control 2 or more non-Token Normal Monsters, draw 2 cards instead of 1 for your normal draw during your Draw Phase."</summary>
    private static CardProject Extract() => SpellCard(SpellKind.Continuous, new EffectEvent
    {
        When = B("when.while_face_up").With("zone", "spell_trap_zone"),
        If = [B("if.cards_exist").With("count", 2).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Normal, FaceUpOnly = true })],
        Do = [B(DrawCountPassive.BlockId)],
    });

    [Fact]
    public void DrawTwoInsteadOfOne_ReadsLikeBunnillinExtract()
    {
        Assert.Equal("While you control 2 or more face-up Normal Monsters, draw 2 cards instead of 1 for your normal draw during your Draw Phase.", Text(Extract()));
        var lua = Compiles(Extract());
        Assert.Contains("SetCode(EFFECT_DRAW_COUNT)", lua);
        Assert.Contains("SetTargetRange(1,0)", lua);
    }

    [EngineFact]
    public void DrawTwoInsteadOfOne_WhileTheConditionHolds()
    {
        // Player 0's next Draw Phase (turn 3) draws 2; with only 1 Normal Monster it draws 1 as usual.
        new EngineScenario(Extract()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Spell, 0, "deck", 10).Card(Warrior, 1, "deck", 10)
            .Activate(Id).Turns(3).Check(Count(0, "LOCATION_DECK", 9)).AssertPasses();
        new EngineScenario(Extract()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone", 2).Card(Spell, 0, "deck", 10).Card(Warrior, 1, "deck", 10)
            .Activate(Id).Turns(3).Check(Count(0, "LOCATION_DECK", 8)).AssertPasses();
    }

    /// <summary>Freed the Matchless General, Time Chain: "During your Draw Phase, instead of conducting your normal draw: You can add 1 monster from your Deck to your hand."</summary>
    private static CardProject InsteadOfDrawing() => Monster(new EffectEvent
    {
        When = B("when.phase").With("phase", "draw").With("instead", true).With("zone", "monster_zone"),
        Do = [B("do.add_to_hand").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster })],
    });

    [Fact]
    public void InsteadOfConductingYourNormalDraw_ReadsLikeTimeChain()
    {
        Assert.Equal("During your Draw Phase, instead of conducting your normal draw: You can add 1 monster from your Deck to your hand.", Text(InsteadOfDrawing()));
        var lua = Compiles(InsteadOfDrawing());
        Assert.Contains("SetCode(EVENT_PREDRAW)", lua);
        Assert.Contains("aux.GiveUpNormalDraw(e,tp)", lua);
    }

    [EngineFact]
    public void InsteadOfConductingYourNormalDraw_AddsTheMonsterAndDrawsNothing()
    {
        // The Deck has Spells and 1 Fiend: on turn 3 the Fiend is added and no Spell is drawn.
        new EngineScenario(InsteadOfDrawing()).Card(Id, 0, "mzone").Card(Spell, 0, "deck", 5).Card(Fiend, 0, "deck").Card(Warrior, 1, "deck", 5)
            .Activate(Id).Turns(3).ExpectActivations(1).Check(In(0, "LOCATION_HAND", Fiend)).Check(Count(0, "LOCATION_DECK", 5)).AssertPasses();
    }

    // ---------------------------------------------------------------- Mulcharmy Fuwalos

    /// <summary>
    /// Mulcharmy Fuwalos: "(Quick Effect): You can discard this card; ... each time your opponent Special Summons a monster(s)
    /// from the Deck and/or Extra Deck, immediately draw 1 card ... You can only activate 1 other "Mulcharmy" monster effect,
    /// the turn you activate this effect."
    /// </summary>
    private static CardProject Fuwalos() => new()
    {
        Card = new CardData { Id = Id, Name = "Mulcharmy Tester", Frame = MonsterFrame.Effect, Race = CardRace.Fiend, Attribute = CardAttribute.Wind, Level = 1, Archetypes = [ArchetypeActivationsCondition.Mulcharmy] },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.quick").With("zone", "hand"),
                If = [B(ArchetypeActivationsCondition.BlockId).With("cards", new CardFilter { Archetype = ArchetypeActivationsCondition.Mulcharmy })],
                Cost = [B("cost.this_card").With("how", "discard")],
                Do =
                [
                    B("do.turn_watch").With("event", "opp_special_deck_extra").With("then", "draw"),
                    B(EndPhaseHandAction.BlockId),
                ],
            },
        ],
    };

    private static CardPack MulcharmyPack() => new() { Cards = [Fuwalos()], Archetypes = [new Archetype { Code = ArchetypeActivationsCondition.Mulcharmy, Name = "Mulcharmy" }] };

    /// <summary>Player 1's Spell: "Special Summon 1 monster from your Deck."</summary>
    private static CardProject Summoner() => new()
    {
        Card = new CardData { Id = Other, Name = "Deck Summoner", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.special_summon").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster })] }],
    };

    [Fact]
    public void Fuwalos_Reads()
    {
        Assert.Equal("(Quick Effect): You can discard this card; for the rest of this turn, each time your opponent Special Summons a monster(s) from the Deck and/or Extra Deck, " +
                     "immediately draw 1 card, and once, during this End Phase, if the number of cards in your hand is more than the number of cards your opponent controls +6, " +
                     "you must randomly shuffle cards from your hand into the Deck so the number in your hand equals the number your opponent controls +6. " +
                     "You can only activate 1 other \"Mulcharmy\" monster effect, the turn you activate this effect.", Text(Fuwalos(), MulcharmyPack()));
        var lua = Compiles(Fuwalos());
        Assert.Contains("aux.EnableMulcharmyGlobalCheck()", lua);
        Assert.Contains("Duel.GetFlagEffect(tp,84192580)<=1", lua);
        Assert.Contains("c:IsSummonLocation(LOCATION_DECK+LOCATION_EXTRA)", lua);
        Assert.Contains("not Duel.IsChainSolving()", lua);
    }

    [EngineFact]
    public void Fuwalos_DrawsAfterTheChainLinkThatSummoned()
    {
        // Player 0 discards it on player 1's turn; player 1's Spell Special Summons from the Deck as it resolves: 1 card is drawn after it.
        new EngineScenario(Fuwalos(), Summoner()).Card(Id, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Other, 1, "hand").Card(Warrior, 1, "deck", 5)
            .Activate(Id).Activate(Other).Turns(2).ExpectActivations(2)
            .Check(In(0, "LOCATION_GRAVE", Id)).Check(Count(1, "LOCATION_MZONE", 1)).Check(Count(0, "LOCATION_DECK", 4)).Check(Count(0, "LOCATION_HAND", 1)).AssertPasses();
    }

    [EngineFact]
    public void Fuwalos_CantBeActivatedAfter2OtherMulcharmyEffects()
    {
        new EngineScenario(Fuwalos()).Card(Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Setup("Duel.RegisterFlagEffect(0,84192580,RESET_PHASE+PHASE_END,0,1) Duel.RegisterFlagEffect(0,84192580,RESET_PHASE+PHASE_END,0,1)")
            .Activate(Id).ExpectActivations(0).Check(In(0, "LOCATION_HAND", Id)).AssertPasses();
    }

    /// <summary>The End Phase rule, down to a number: "... so the number in your hand equals 0".</summary>
    [EngineFact]
    public void DuringThisEndPhase_ShufflesTheHandDownToTheNumber()
    {
        var p = Monster(Ignition(B(EndPhaseHandAction.BlockId).With("than", "number").With("plus", 1)));
        Assert.Equal("You can activate this effect; once, during this End Phase, if the number of cards in your hand is more than 1, you must randomly shuffle cards from your hand into the Deck so the number in your hand equals 1.", Text(p));
        new EngineScenario(p).Card(Id, 0, "mzone").Card(Warrior, 0, "hand", 4).Card(Warrior, 0, "deck", 2)
            .Activate(Id).ExpectActivations(1).Check(Count(0, "LOCATION_HAND", 1)).Check(Count(0, "LOCATION_DECK", 5)).AssertPasses();
    }

    // ---------------------------------------------------------------- counters

    private const int Season = 0x6e;

    /// <summary>Autumn: "Once per turn: You can place Season Counters on this card equal to the number of cards in your opponent's hand."</summary>
    private static CardProject AutumnCounters() => SpellCard(SpellKind.Field, new EffectEvent
    {
        When = B("when.ignition").With("zone", "field_zone"),
        Limit = OncePerTurn.Soft,
        Do = [B(PlaceCounterAction.BlockId).With("by", "value").With("value", "count").WithCounter(Season)
            .With(ValueSources.CardsKey, new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Hand })],
    });

    [Fact]
    public void CountersEqualToAValue_ReadLikeAutumn()
    {
        Assert.Equal("Once per turn: You can place Season Counters on this card equal to the number of cards in your opponent's hand.", Text(AutumnCounters()));
        var lua = Compiles(AutumnCounters());
        Assert.Contains("c:EnableCounterPermit(0x6e)", lua);
        Assert.Contains("Duel.GetMatchingGroup(", lua);
    }

    [EngineFact]
    public void CountersEqualToAValue_PlacesThatMany()
    {
        new EngineScenario(AutumnCounters()).Card(Id, 0, "hand").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "hand", 3).Card(Warrior, 1, "deck", 3)
            .Activate(Id, 2).ExpectActivations(2).Check($"Duel.GetFieldCard(0,LOCATION_FZONE,0):GetCounter({Counters.Hex(Season)})==3").AssertPasses();
    }

    /// <summary>
    /// Autumn: "You can take 1 Field Spell from your hand, Deck, or GY that you can place a Season Counter on, except "Autumn", and
    /// place it face-up on your field, and if you do, place all Season Counters on this card on that card."
    /// </summary>
    private static CardProject AutumnMove() => SpellCard(SpellKind.Field, AutumnCounters().Effects[0], new EffectEvent
    {
        When = B("when.ignition").With("zone", "field_zone"),
        Do =
        [
            B(PlaceAction.BlockId).With("where", "fzone").With("cards", new CardFilter
            {
                Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck | Zone.Graveyard, CardType = FilterCardType.Spell, SpellTrapKind = SpellTrapKind.Field, ExceptOwnName = true,
                Requirements = [new CardRequirement { Property = "can_have_counter", Op = RequirementOp.Is, Values = [Season] }],
            }),
            B(PlaceCounterAction.BlockId).With("by", "here").WithCounter(Season).With("cards", new CardFilter { That = CardRef.Affected }).Linked(ActionLink.IfYouDo),
        ],
    });

    /// <summary>Another Field Spell that holds Season Counters ("Once per turn: place 1 Season Counter on this card").</summary>
    private static CardProject Winter(long id = Other) => new()
    {
        Card = new CardData { Id = id, Name = "Winter", Kind = CardKind.Spell, SpellKind = SpellKind.Field },
        Effects = [new EffectEvent { When = B("when.ignition").With("zone", "field_zone"), Do = [B(PlaceCounterAction.BlockId).WithCounter(Season)] }],
    };

    [Fact]
    public void MoveAllCountersOntoThatCard_ReadsLikeAutumn()
    {
        Assert.EndsWith(" You can place 1 Field Spell that you can place a Season Counter on from your hand, Deck, or GY face-up in your Field Zone, except \"Deck Spell\", " +
                        "and if you do, place all Season Counters on this card on that card.", Text(AutumnMove()));
        var lua = Compiles(AutumnMove());
        Assert.Contains("c:IsCanHaveCounter(0x6e)", lua);
        Assert.Contains("local here2=c:GetCounter(0x6e)", lua);
        Assert.Contains("s.ccplaced={}", lua);
    }

    [EngineFact]
    public void MoveAllCountersOntoThatCard_TheNewFieldSpellHasThem()
    {
        // Autumn gets 2 Season Counters (2 cards in the opponent's hand), then "Winter", which can hold them, takes its place and them.
        new EngineScenario(AutumnMove(), Winter()).Card(Id, 0, "hand").Card(Other, 0, "deck").Card(Warrior, 0, "deck", 3).Card(Warrior, 1, "hand", 2).Card(Warrior, 1, "deck", 3)
            .Activate(Id, 3).ExpectActivations(3)
            .Check(In(0, "LOCATION_GRAVE", Id)).Check($"Duel.GetFieldCard(0,LOCATION_FZONE,0):IsCode({Other}) and Duel.GetFieldCard(0,LOCATION_FZONE,0):GetCounter({Counters.Hex(Season)})==2").AssertPasses();
    }

    /// <summary>Cubic Bodhi: "place 1 Cubic Counter on 1 face-up monster your opponent controls (monsters with a Cubic Counter cannot attack, also negate their effects)".</summary>
    private static CardProject Bodhi() => Monster(new EffectEvent
    {
        When = B("when.ignition"),
        Cost = [B("cost.discard")],
        Do = [B(PlaceCounterAction.BlockId).WithCounter(0x1038).With("status", "no_attack_negated").With("cards", Theirs)],
    });

    [Fact]
    public void ACounterThatCarriesItsEffect_ReadsLikeCubicBodhi()
    {
        Assert.Equal("You can discard 1 card from your hand; place 1 Cubic Counter on 1 face-up monster your opponent controls (monsters with a Cubic Counter cannot attack, also negate their effects).", Text(Bodhi()));
        var lua = Compiles(Bodhi());
        Assert.Contains("return e:GetHandler():GetCounter(0x1038)>0", lua);
        Assert.Contains("ce:SetCode(EFFECT_DISABLE)", lua);
    }

    [EngineFact]
    public void ACounterThatCarriesItsEffect_NegatesWhileItIsThere()
    {
        var monster = $"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{EffectFiller})";
        new EngineScenario(Bodhi()).Card(Id, 0, "mzone").Card(Warrior, 0, "hand").Card(Warrior, 0, "deck", 3).Card(EffectFiller, 1, "mzone")
            .Activate(Id).ExpectActivations(1).Check($"{monster}:GetCounter(0x1038)==1 and {monster}:IsDisabled()").AssertPasses();
    }

    /// <summary>Wicked Canon: "Each time an opponent's monster declares an attack, place 1 ... Counter on this card."</summary>
    private static CardProject Canon() => Monster(new EffectEvent
    {
        When = B("when.while_face_up"),
        Do = [B(EachTimePassive.BlockId).With("event", "opp_attack").With("then", "counter").WithCounter(0x1)],
    });

    [Fact]
    public void EachTimePlaceACounter_ReadsLikeWickedCanon()
    {
        Assert.Equal("Each time an opponent's monster declares an attack, place 1 Spell Counter on this card.", Text(Canon()));
        Assert.Contains("e:GetHandler():AddCounter(0x1,1)", Compiles(Canon()));
    }

    [EngineFact]
    public void EachTimePlaceACounter_OnEachAttack()
    {
        new EngineScenario(Canon()).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Dragon, 1, "mzone").Card(Warrior, 1, "deck", 3)
            .Attack(Dragon).Turns(2).Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetCounter(0x1)==1").AssertPasses();
    }

    /// <summary>Wicked Canon: "add 1 DARK Fiend monster from your Deck to your hand, then discard 1 Fiend monster".</summary>
    private static CardProject AddThenDiscard() => Monster(Ignition(
        B("do.add_to_hand").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Fiend }),
        B("do.discard").With("who", "you").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, Race = CardRace.Fiend }).Linked(ActionLink.Then)));

    [Fact]
    public void DiscardAPickedCard_ReadsLikeWickedCanon()
    {
        Assert.Equal("You can add 1 Fiend monster from your Deck to your hand, then discard 1 Fiend monster.", Text(AddThenDiscard()));
        Assert.Contains("Duel.DiscardHand(tp,s.discardfilter,1,1,REASON_EFFECT+REASON_DISCARD,nil,e,tp)", Compiles(AddThenDiscard()));
    }

    [EngineFact]
    public void DiscardAPickedCard_DiscardsOnlyThatKind()
    {
        // The Warrior in the hand stays; the Fiend just added is discarded.
        new EngineScenario(AddThenDiscard()).Card(Id, 0, "mzone").Card(Warrior, 0, "hand").Card(Fiend, 0, "deck").Card(Warrior, 0, "deck", 2)
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_GRAVE", Fiend)).Check(In(0, "LOCATION_HAND", Warrior)).AssertPasses();
    }

    [Fact]
    public void DiscardWithoutAPicker_CompilesAsBefore()
    {
        var p = Monster(Ignition(B("do.discard").With("who", "you")));
        Assert.Equal("You can discard 1 card.", Text(p));
        Assert.Contains("Duel.DiscardHand(tp,nil,1,1,REASON_EFFECT+REASON_DISCARD)", Compiles(p));
    }

    // ---------------------------------------------------------------- LP and costs

    /// <summary>Ultimate Sky: "Target face-up cards on the field up to the number of face-up monsters your opponent controls and pay 800 LP for each target; negate their effects until the end of this turn."</summary>
    private static CardProject UltimateSky(string duration = "turn") => SpellCard(SpellKind.Normal, new EffectEvent
    {
        When = B("when.activate"),
        Cost = [B("cost.pay_lp").With("how", "per_target").With("amount", 800)],
        Do = [B("do.negate_effects").With("target", true).With("amount", "number_of")
            .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field, FaceUpOnly = true, ExcludeThisCard = true })
            .With("count_cards", Theirs).With("duration", duration)],
    });

    [Fact]
    public void PayLpForEachTarget_ReadsLikeUltimateSky()
    {
        Assert.Equal("Target other face-up cards on the field, up to the number of face-up monsters your opponent controls and pay 800 LP for each target; negate their effects until the end of this turn.", Text(UltimateSky()));
        var lua = Compiles(UltimateSky());
        Assert.Contains("math.min(ct1,math.floor(Duel.GetLP(tp)/800))", lua);
        Assert.Contains("if e:IsCostChecked() then Duel.PayLPCost(tp,tg", lua);
    }

    [EngineFact]
    public void PayLpForEachTarget_PaysForTheTargetsChosen()
    {
        // Negated for good, so it can be seen after the turn.
        new EngineScenario(UltimateSky("always")).Card(Id, 0, "hand").Card(Warrior, 0, "deck", 3).Card(EffectFiller, 1, "mzone", 2)
            .Activate(Id).ExpectActivations(1).Check("Duel.GetLP(0)==7200")
            .Check($"Duel.GetMatchingGroupCount(Card.IsDisabled,1,LOCATION_MZONE,0,nil)==1").AssertPasses();
        // Without 800 LP to pay it can't be activated.
        new EngineScenario(UltimateSky()).Card(Id, 0, "hand").Card(Warrior, 0, "deck", 3).Card(EffectFiller, 1, "mzone", 2).Setup("Duel.SetLP(0,500)")
            .Activate(Id).ExpectActivations(0).AssertPasses();
    }

    /// <summary>The Winged Dragon of Ra, Sun God of Darkness: "pay LP so that you only have 1 left; this card gains ATK equal to the amount of LP paid".</summary>
    private static CardProject RaPays() => Monster(new EffectEvent
    {
        When = B("when.ignition"),
        Cost = [B("cost.pay_lp").With("how", "to_one")],
        Do = [B(StatsFromValueAction.BlockId).With("cards", new CardFilter { ThisCard = true }).With("value", "lp_paid").With("duration", "always")],
    });

    [Fact]
    public void PayLpSoThatYouOnlyHave1Left_ReadsLikeRa()
    {
        Assert.Equal("You can pay LP so that you only have 1 left; this card gains that much ATK.", Text(RaPays()));
        Assert.Contains("local lpc=Duel.GetLP(tp)-1", Compiles(RaPays()));
    }

    [EngineFact]
    public void PayLpSoThatYouOnlyHave1Left_GainsTheLpPaid()
    {
        new EngineScenario(RaPays()).Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 3).Setup("Duel.SetLP(0,3000)")
            .Activate(Id).ExpectActivations(1).Check("Duel.GetLP(0)==1")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetAttack()==1800+2999").AssertPasses();
    }

    /// <summary>Ra: "discard any number of "Sun God" cards, then ... that many ...": here, send that many monsters your opponent controls to the GY.</summary>
    private static CardProject RaDiscards() => Monster(new EffectEvent
    {
        When = B("when.ignition"),
        Cost = [B("cost.discard").With("amount", "any").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, Race = CardRace.Warrior })],
        Do = [B("do.send_to_gy").With("amount", "cost").With("cards", Theirs)],
    });

    [Fact]
    public void DiscardAnyNumber_ThenThatMany()
    {
        Assert.Equal("You can discard any number of Warrior monsters from your hand; send that many face-up monsters your opponent controls to the GY.", Text(RaDiscards()));
        var lua = Compiles(RaDiscards());
        Assert.Contains("e:SetLabel(Duel.DiscardHand(tp,s.discardfilter,1,", lua);
    }

    [EngineFact]
    public void DiscardAnyNumber_SendsAsManyAsWereDiscarded()
    {
        // The harness discards 1: 1 of the 2 monsters goes.
        new EngineScenario(RaDiscards()).Card(Id, 0, "mzone").Card(Warrior, 0, "hand", 2).Card(Warrior, 0, "deck", 3).Card(Dragon, 1, "mzone", 2)
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_GRAVE", Warrior)).Check(In(1, "LOCATION_GRAVE", Dragon)).Check(In(1, "LOCATION_MZONE", Dragon)).AssertPasses();
    }

    /// <summary>Nibiru, the Primal Being: "Tribute as many face-up monsters on the field as possible, and if you do, Special Summon this card from your hand, then Special Summon 1 "Primal Being Token" ... (this Token's ATK/DEF become the combined original ATK/DEF of the Tributed monsters)".</summary>
    private static CardProject Nibiru() => Monster(new EffectEvent
    {
        When = B("when.ignition").With("zone", "hand"),
        Do =
        [
            B("do.tribute").With("mode", "all").With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true }),
            B("do.special_summon_self").Linked(ActionLink.IfYouDo),
            B("do.tokens").With("token", TokenId).With("side", "opponent").With("stats", "tributed").Linked(ActionLink.Then),
        ],
    });

    private static CardProject PrimalToken() => new()
    {
        Card = new CardData { Id = TokenId, Name = "Primal Being Token", Frame = MonsterFrame.Token, Race = CardRace.Rock, Attribute = CardAttribute.Light, Level = 11 },
    };

    private static CardPack Pack(params CardProject[] cards) => new() { Cards = [.. cards] };

    [Fact]
    public void TokenWithTheTributedMonstersStats_ReadsLikeNibiru()
    {
        var p = Nibiru();
        Assert.Equal("You can Tribute all face-up monsters on the field, and if you do, Special Summon this card from your hand, then Special Summon 1 \"Primal Being Token\" " +
                     "(Rock/LIGHT/Level 11/ATK ?/DEF ?) to your opponent's field (this Token's ATK/DEF become the combined original ATK/DEF of the Tributed monsters).", Text(p, Pack(p, PrimalToken())));
        var lua = Compiles(p, Pack(p, PrimalToken()));
        Assert.Contains("s.keeptributed(r", lua);
        Assert.Contains("e1:SetCode(EFFECT_SET_ATTACK)", lua);
    }

    [EngineFact]
    public void TokenWithTheTributedMonstersStats_HasTheirCombinedOriginalAtkDef()
    {
        // A 1500/1000 Warrior and a 1600/1000 Dragon: the Token has 3100/2000.
        var token = $"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{TokenId})";
        new EngineScenario(Nibiru(), PrimalToken()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 3)
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_MZONE", Id))
            .Check($"{token} and {token}:GetAttack()==3100 and {token}:GetDefense()==2000").AssertPasses();
    }

    /// <summary>
    /// The Sun God Leading Down into Darkness: "Activate this card by sending 1 ... monster from your hand ... to the GY." and "If a
    /// monster(s) is destroyed by battle: Its controller takes damage equal to its original ATK".
    /// </summary>
    private static CardProject LeadingDown() => SpellCard(SpellKind.Continuous,
        new EffectEvent
        {
            When = B("when.activate"),
            Cost = [B("cost.move_cards").With("how", "grave").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster })],
        },
        new EffectEvent
        {
            When = B(CardsEventTrigger.BlockId).With("event", "destroyed_battle").With("zone", "spell_trap_zone").With("optional", false)
                .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
            Do = [B(ControllerDamageAction.BlockId)],
        });

    [Fact]
    public void ActivateThisCardBy_AndItsControllerTakesDamage_ReadLikeTheSunGodLeadingDown()
    {
        Assert.Equal("Activate this card by sending 1 monster from your hand to the GY. If a monster(s) on the field is destroyed by battle: Its controller takes damage equal to its original ATK.", Text(LeadingDown()));
        var lua = Compiles(LeadingDown());
        // The activation is the effect with the cost: no plain one besides it.
        Assert.DoesNotContain("--Activate\n", lua);
        Assert.Contains("e1:SetType(EFFECT_TYPE_ACTIVATE)", lua);
    }

    [EngineFact]
    public void ActivateThisCardBy_PaysTheCost_AndBattleDestructionDamagesItsController()
    {
        // Player 1's Dragon (1600) destroys player 0's Warrior (1500): 100 battle damage, then 1500 for the Warrior's original ATK.
        new EngineScenario(LeadingDown()).Card(Id, 0, "hand").Card(Fiend, 0, "hand").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 3)
            .Card(Dragon, 1, "mzone").Card(Warrior, 1, "deck", 3)
            .Activate(Id).Attack(Dragon).Turns(2)
            .Check(In(0, "LOCATION_GRAVE", Fiend)).Check(In(0, "LOCATION_SZONE", Id)).Check("Duel.GetLP(0)==8000-100-1500").AssertPasses();
    }

    [Fact]
    public void ActivationWithoutADoBlock_StillNeedsACost()
    {
        var p = SpellCard(SpellKind.Continuous, new EffectEvent { When = B("when.activate") });
        Assert.True(ScriptCompiler.Compile(p).HasErrors);
        var normal = SpellCard(SpellKind.Normal, new EffectEvent { When = B("when.activate"), Cost = [B("cost.pay_lp")] });
        Assert.True(ScriptCompiler.Compile(normal).HasErrors);
    }

    // ---------------------------------------------------------------- official scripts read back

    private static CardProject Translated(long id, CardData card)
    {
        var result = ScriptTranslator.Translate(File.ReadAllText(Path.Combine(EngineScripts!, $"c{id}.lua")), id, card);
        Assert.True(result.Complete, string.Join("\n", result.Pieces.Where(p => !p.Translated).Select(p => $"{p.Piece?.Title}: {p.Reason}")));
        var project = new CardProject { Card = card, Effects = result.Pieces.Where(p => p.Effect is not null).Select(p => p.Effect!).ToList() };
        Assert.False(ScriptCompiler.Compile(project).HasErrors);
        return project;
    }

    /// <summary>Pot of Desires: "Banish 10 cards from the top of your Deck, face-down; draw 2 cards."</summary>
    [EngineFact]
    public void PotOfDesires_TranslatesToTheCostThatBanishesTheTop()
    {
        var p = Translated(35261759, new CardData { Id = Id, Name = "Translated", Kind = CardKind.Spell });
        Assert.Equal(BanishTopCost.BlockId, p.Effects.Single().Cost.Single().BlockId);
        new EngineScenario(p).Card(Id, 0, "hand").Card(Warrior, 0, "deck", 12).Activate(Id).ExpectActivations(1)
            .Check(FaceDownBanished(0, 10)).Check(Count(0, "LOCATION_HAND", 2)).AssertPasses();
    }

    /// <summary>Guarded Treasure: "While this card is on the field, draw 2 cards instead of 1 for your normal draw during your Draw Phase."</summary>
    [EngineFact]
    public void GuardedTreasure_TranslatesTheDrawCount()
    {
        var p = Translated(17052477, new CardData { Id = Id, Name = "Translated", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous });
        Assert.Contains(p.Effects, e => e.Do.Any(b => b.BlockId == DrawCountPassive.BlockId));
    }

    /// <summary>Lady Assailant of Flames: "Banish the top 3 cards of your Deck; inflict 800 damage to your opponent."</summary>
    [EngineFact]
    public void LadyAssailant_TranslatesTheFaceUpCost()
    {
        var p = Translated(90147755, new CardData { Id = Id, Name = "Translated", Frame = MonsterFrame.Effect, Level = 3, Atk = 1000 });
        Assert.Equal("false", p.Effects.Single().Cost.Single().Values["facedown"]);
    }
}
