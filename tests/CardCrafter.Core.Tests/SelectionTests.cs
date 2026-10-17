using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// How cards are picked (with different names, at random, by the opponent, 1 each from several places, by total Level),
/// revealing cards for the opponent to pick, targets of two kinds, and how "●" options are chosen.
/// </summary>
public class SelectionTests
{
    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    private static CardProject SpellCard(long id, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = "Pick " + id, Kind = CardKind.Spell },
        Effects = [.. effects],
    };

    private static CardProject Monster(long id, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = "Pick " + id, Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    private static EffectEvent Activate(params BlockInstance[] blocks) => new() { When = B("when.activate"), Do = [.. blocks] };

    private static string Lua(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics) + "\n" + r.Lua);
        Assert.DoesNotContain(Validation.ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        Assert.Empty(Validation.ScriptChecker.UnknownNames(r.Lua));
        return r.Lua;
    }

    private static CardFilter Deck(FilterCardType type = FilterCardType.Monster) => new() { Owner = CardOwner.You, Zones = Zone.Deck, CardType = type };

    // ---------------------------------------------------------------- with different names

    private static CardProject Rokket(long id) => SpellCard(id, Activate(B("do.add_to_hand").With("count", 2).With("pick", "names").With("cards", Deck())));

    [Fact]
    public void DifferentNames_ReadAndCompileAsOfficialCards()
    {
        var p = Rokket(100110001);
        Assert.Equal("Add 2 monsters with different names from your Deck to your hand.", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains(":GetClassCount(Card.GetCode)>=2", lua);
        Assert.Contains(":SelectSubGroup(tp,aux.dncheck,false,2,2)", lua);
    }

    [EngineFact]
    public void DifferentNames_AddsOneOfEachName()
    {
        const long id = 100110002;
        new EngineScenario(Rokket(id)).Card(id, 0, "hand").Card(Warrior, 0, "deck", 3).Card(Dragon, 0, "deck").Activate(id).ExpectActivations(1)
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_HAND,0,nil,1001)==1")
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_HAND,0,nil,1002)==1").AssertPasses();
    }

    [EngineFact]
    public void DifferentNames_CannotBeActivatedWithOneName()
    {
        const long id = 100110003;
        new EngineScenario(Rokket(id)).Card(id, 0, "hand").Card(Warrior, 0, "deck", 3).Activate(id).ExpectActivations(0)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").AssertPasses();
    }

    /// <summary>Prank-Kids Dodo-Doodle-Doo: "target 2 cards with different names in your GY; add them to your hand".</summary>
    private static CardProject DodoDoodle(long id) => SpellCard(id, Activate(B("do.add_to_hand").With("count", 2).With("pick", "names").With("target", true)
        .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster })));

    [Fact]
    public void DifferentNames_Targeted_ReadsAsATarget()
    {
        var p = DodoDoodle(100110004);
        Assert.Equal("Target 2 monsters with different names from your GY; add them to your hand.", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("if chkc then return false end", lua);
        Assert.Contains(":Filter(Card.IsCanBeEffectTarget,nil,e)", lua);
        Assert.Contains("Duel.SetTargetCard(", lua);
    }

    [EngineFact]
    public void DifferentNames_Targeted_AddsOneOfEachName()
    {
        const long id = 100110005;
        new EngineScenario(DodoDoodle(id)).Card(id, 0, "hand").Card(Warrior, 0, "grave", 2).Card(Dragon, 0, "grave").Activate(id).ExpectActivations(1)
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_HAND,0,nil,1001)==1")
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_HAND,0,nil,1002)==1").AssertPasses();
    }

    [Fact]
    public void OtherGroupRules_Read()
    {
        string Text(BlockInstance b) => EffectTextGenerator.Generate(SpellCard(100110006, Activate(b)));
        var gy = new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster };
        Assert.Equal("Special Summon 2 monsters with the same Level from your GY.", Text(B("do.special_summon").With("count", 2).With("pick", "same_level").With("cards", gy)));
        Assert.Equal("Send 3 monsters with different Attributes from your Deck to the GY.", Text(B("do.send_to_gy").With("count", 3).With("pick", "attributes").With("cards", Deck())));
        Assert.Equal("Special Summon up to 3 monsters from your GY whose total Levels equal 8 or less.",
            Text(B("do.special_summon").With("count", 3).With("mode", "up_to").With("pick", "level_max").With("sum", 8).With("cards", gy)));
        Assert.Equal("Banish any number of monsters from your GY whose total Levels equal 8.", Text(B("do.banish").With("amount", "any").With("pick", "level_sum").With("sum", 8).With("cards", gy)));
        var both = new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck, CardType = FilterCardType.Monster };
        Assert.Equal("Send 2 monsters to the GY, 1 each from your hand and Deck.", Text(B("do.send_to_gy").With("count", 2).With("each_zone", true).With("cards", both)));
        // With 1 card there is no group: the rule says nothing.
        Assert.Equal("Add 1 monster from your Deck to your hand.", Text(B("do.add_to_hand").With("pick", "names").With("cards", Deck())));
    }

    [Fact]
    public void GroupRules_CompileToTheEnginesChecks()
    {
        var gy = new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster };
        var sum = Lua(SpellCard(100110007, Activate(B("do.banish").With("amount", "any").With("pick", "level_sum").With("sum", 8).With("cards", gy))));
        Assert.Contains(":CheckWithSumEqual(Card.GetLevel,8,1,99)", sum);
        Assert.Contains(":SelectWithSumEqual(tp,Card.GetLevel,8,1,99)", sum);
        var same = Lua(SpellCard(100110008, Activate(B("do.special_summon").With("count", 2).With("pick", "same_attribute").With("cards", gy))));
        Assert.Contains("aux.SameValueCheck(g,Card.GetAttribute)", same);
        Assert.Contains(":CheckSubGroup(s.gcheck,2,2)", same);
        var each = Lua(SpellCard(100110009, Activate(B("do.send_to_gy").With("count", 2).With("each_zone", true).With("pick", "names")
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck }))));
        Assert.Contains("return aux.dncheck(g) and g:FilterCount(Card.IsLocation,nil,LOCATION_HAND)<=1 and g:FilterCount(Card.IsLocation,nil,LOCATION_DECK)<=1", each);
    }

    [EngineFact]
    public void EachFromHandAndDeck_SendsOneFromEach()
    {
        const long id = 100110010;
        var p = SpellCard(id, Activate(B("do.send_to_gy").With("count", 2).With("each_zone", true)
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck, CardType = FilterCardType.Monster })));
        new EngineScenario(p).Card(id, 0, "hand").Card(Warrior, 0, "hand", 2).Card(Dragon, 0, "deck", 3).Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==2")
            .Check("Duel.GetMatchingGroupCount(Card.IsType,0,LOCATION_GRAVE,0,nil,TYPE_MONSTER)==2").AssertPasses();
    }

    [EngineFact]
    public void TotalLevels_SummonsMonstersThatAddUp()
    {
        const long id = 100110011;
        var p = SpellCard(id, Activate(B("do.special_summon").With("count", 3).With("mode", "up_to").With("pick", "level_sum").With("sum", 8)
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster })));
        Assert.Equal("Special Summon up to 3 monsters from your GY whose total Levels equal 8.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(id, 0, "hand").Card(Warrior, 0, "grave", 3).Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==2").AssertPasses();
    }

    // ---------------------------------------------------------------- who picks

    private static CardProject RandomBanish(long id) => SpellCard(id, Activate(B("do.banish").With("chooser", "random")
        .With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Hand })));

    [Fact]
    public void Random_ReadsAsARandomCard()
    {
        var p = RandomBanish(100110020);
        Assert.Equal("Banish 1 random card from your opponent's hand.", EffectTextGenerator.Generate(p));
        Assert.Contains(":RandomSelect(tp,1)", Lua(p));
    }

    [EngineFact]
    public void Random_BanishesOneCardFromTheHand()
    {
        const long id = 100110021;
        new EngineScenario(RandomBanish(id)).Card(id, 0, "hand").Card(Warrior, 1, "hand", 3).Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==2").Check("Duel.GetFieldGroupCount(1,LOCATION_REMOVED,0)==1").AssertPasses();
    }

    [Fact]
    public void Random_OnlyForAnExactNumberNotTargeted()
    {
        var targeted = ScriptCompiler.Compile(SpellCard(100110022, Activate(B("do.destroy").With("chooser", "random").With("target", true))));
        Assert.Contains(targeted.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("at random"));
        var upTo = ScriptCompiler.Compile(SpellCard(100110023, Activate(B("do.destroy").With("chooser", "random").With("count", 2).With("mode", "up_to"))));
        Assert.Contains(upTo.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("exact number"));
    }

    /// <summary>Painful Return, Rare Value: "your opponent chooses 1 monster in your GY, and you Special Summon it".</summary>
    private static CardProject OpponentPicks(long id) => SpellCard(id, Activate(B("do.special_summon").With("chooser", "opponent")
        .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster })));

    [Fact]
    public void OpponentPicks_ReadsAndCompiles()
    {
        var p = OpponentPicks(100110024);
        Assert.Equal("Your opponent chooses 1 monster in your GY, and you Special Summon it.", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("Duel.Hint(HINT_SELECTMSG,1-tp,HINTMSG_SPSUMMON)", lua);
        Assert.Contains("Duel.SelectMatchingCard(1-tp,aux.NecroValleyFilter(s.filter),tp,LOCATION_GRAVE,0,1,1,nil,e,tp)", lua);
    }

    [EngineFact]
    public void OpponentPicks_TheCardYouSummon()
    {
        const long id = 100110025;
        new EngineScenario(OpponentPicks(id)).Card(id, 0, "hand").Card(Warrior, 0, "grave", 2).Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1").Check("Duel.GetFieldGroupCount(0,0,LOCATION_MZONE)==0").AssertPasses();
    }

    [EngineFact]
    public void AnyNumber_DestroysWhatIsPicked()
    {
        const long id = 100110026;
        var p = SpellCard(id, Activate(B("do.destroy").With("amount", "any").With("cards", CardFilter.OpponentField(FilterCardType.Monster))));
        Assert.Equal("Destroy any number of monsters your opponent controls.", EffectTextGenerator.Generate(p));
        Assert.Contains("1,99", Lua(p));
        // The automatic player picks as few as it may: 1.
        new EngineScenario(p).Card(id, 0, "hand").Card(Fiend, 1, "mzone", 3).Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==2").AssertPasses();
    }

    // ---------------------------------------------------------------- reveal, the opponent picks

    private static CardProject Pantheism(long id, string how = "random") => SpellCard(id, Activate(B(RevealPickAction.BlockId).With("how", how).With("names", true)));

    [Fact]
    public void RevealPick_ReadsAsOfficialCards()
    {
        Assert.Equal("Reveal 3 monsters with different names from your Deck, your opponent randomly picks 1 for you to add to your hand, and you shuffle the rest into the Deck.",
            EffectTextGenerator.Generate(Pantheism(100110030)));
        var chooses = SpellCard(100110031, Activate(B(RevealPickAction.BlockId).With("then", "summon").With("rest", "grave")));
        Assert.Equal("Reveal 3 monsters from your Deck, your opponent chooses 1 for you to Special Summon, and you send the rest to the GY.", EffectTextGenerator.Generate(chooses));
        var lua = Lua(Pantheism(100110032));
        Assert.Contains(":SelectSubGroup(tp,aux.dncheck,false,3,3)", lua);
        Assert.Contains(":RandomSelect(1-tp,1)", lua);
        Assert.Contains("SetStatus(STATUS_TO_HAND_WITHOUT_CONFIRM,true)", lua);
    }

    [EngineFact]
    public void RevealPick_AddsOneAndTheRestStay()
    {
        const long id = 100110033;
        var named = new CardProject { Card = new CardData { Id = 100110039, Name = "Third Name", Frame = MonsterFrame.Normal, Level = 4, Atk = 100, Def = 100 } };
        new EngineScenario(Pantheism(id), named).Card(id, 0, "hand").Card(Warrior, 0, "deck", 2).Card(Dragon, 0, "deck").Card(100110039, 0, "deck")
            .Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==3").AssertPasses();
    }

    [EngineFact]
    public void RevealPick_SendsTheRestToTheGY()
    {
        const long id = 100110034;
        var p = SpellCard(id, Activate(B(RevealPickAction.BlockId).With("how", "chooses").With("then", "summon").With("rest", "grave")));
        new EngineScenario(p).Card(id, 0, "hand").Card(Warrior, 0, "deck", 4).Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1").Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==1")
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_GRAVE,0,nil,1001)==2").AssertPasses();
    }

    // ---------------------------------------------------------------- targets of two kinds

    private static CardProject Impact(long id) => SpellCard(id, Activate(B(PairTargetAction.BlockId)));

    [Fact]
    public void PairTargets_ReadAsOneTarget()
    {
        Assert.Equal("Target 1 monster you control and 1 card your opponent controls; destroy them.", EffectTextGenerator.Generate(Impact(100110040)));
        var lua = Lua(Impact(100110041));
        Assert.Contains("if chkc then return false end", lua);
        Assert.Contains(":Merge(", lua);
        var overlap = ScriptCompiler.Compile(SpellCard(100110042, Activate(B(PairTargetAction.BlockId).With("cards2", CardFilter.OpponentField().Clone().With(f => f.Owner = CardOwner.Either)))));
        Assert.Contains(overlap.Diagnostics, d => d.Severity == Severity.Error);
    }

    [EngineFact]
    public void PairTargets_DestroysBoth()
    {
        const long id = 100110043;
        new EngineScenario(Impact(id)).Card(id, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone").Card(Spell, 1, "szone").Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==0").Check("Duel.GetFieldGroupCount(1,LOCATION_ONFIELD,0)==1").AssertPasses();
    }

    // ---------------------------------------------------------------- how "●" options are chosen

    private static EffectEvent Options(BlockInstance? header, params BlockInstance[] options)
    {
        var e = new EffectEvent { When = B("when.ignition") };
        if (header is not null) e.Do.Add(header);
        for (var i = 0; i < options.Length; i++) e.Do.Add(i == 0 ? options[i] : options[i].Linked(ActionLink.Option));
        return e;
    }

    private static BlockInstance Header(string who = "you", string when = "activate", bool once = false) =>
        B(ChooseEffectsAction.BlockId).With("who", who).With("when", when).With("once_each", once);

    [Fact]
    public void ChooseHeader_Reads()
    {
        string Text(BlockInstance h) => EffectTextGenerator.Generate(Monster(100110050, Options(h, B("do.draw"), B("do.gain_lp"))));
        Assert.Equal("You can apply 1 of these effects.\n ● Draw 1 card.\n ● Gain 1000 LP.", Text(Header(when: "apply")));
        Assert.Equal("You can activate this effect; your opponent chooses 1 of these effects for you to apply.\n ● Draw 1 card.\n ● Gain 1000 LP.", Text(Header("opponent")));
        Assert.Equal("You can activate 1 of these effects (but you can only use each effect of \"Pick 100110050\" once per turn);\n ● Draw 1 card.\n ● Gain 1000 LP.", Text(Header(once: true)));
        // Without a header block, or with its defaults, it reads as before.
        Assert.Equal("You can activate 1 of these effects;\n ● Draw 1 card.\n ● Gain 1000 LP.", Text(Header()));
        Assert.Equal(EffectTextGenerator.Generate(Monster(100110050, Options(null, B("do.draw"), B("do.gain_lp")))), Text(Header()));
        // Options are still listed for the game's choice.
        Assert.Equal(["Draw 1 card.", "Gain 1000 LP."], EffectTextGenerator.OptionTexts(Options(Header("opponent"), B("do.draw"), B("do.gain_lp")), new CardData { Name = "X" }));
    }

    [Fact]
    public void ChooseHeader_DefaultsCompileAsWithout()
    {
        var with = ScriptCompiler.Compile(Monster(100110051, Options(Header(), B("do.draw"), B("do.gain_lp")))).Lua;
        var without = ScriptCompiler.Compile(Monster(100110051, Options(null, B("do.draw"), B("do.gain_lp")))).Lua;
        Assert.Equal(without, with);
    }

    [Fact]
    public void ChooseHeader_OnResolution_ChoosesInTheOperation()
    {
        var lua = Lua(Monster(100110052, Options(Header("opponent"), B("do.draw"), B("do.gain_lp"))));
        var op = lua[lua.IndexOf("function s.e1op", StringComparison.Ordinal)..];
        Assert.Contains("aux.SelectFromOptions(1-tp,", op);
        Assert.DoesNotContain("SelectFromOptions", lua[lua.IndexOf("function s.e1tg", StringComparison.Ordinal)..lua.IndexOf("function s.e1op", StringComparison.Ordinal)]);
        var targeting = ScriptCompiler.Compile(Monster(100110053, Options(Header(when: "apply"), B("do.destroy").With("target", true), B("do.gain_lp"))));
        Assert.Contains(targeting.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("can't target"));
    }

    [EngineTheory]
    [InlineData("you", "apply")]
    [InlineData("opponent", "activate")]
    public void ChooseHeader_OnResolution_AppliesTheChosenOption(string who, string when)
    {
        const long id = 100110054;
        var p = Monster(id, Options(Header(who, when), B("do.draw"), B("do.gain_lp")));
        new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 5).Option(1).Activate(id).ExpectActivations(1)
            .Check("Duel.GetLP(0)==9000").Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
    }

    [EngineFact]
    public void ChooseHeader_EachOncePerTurn_OffersTheOtherOptionNext()
    {
        const long id = 100110055;
        var p = Monster(id, Options(Header(once: true), B("do.draw"), B("do.gain_lp")));
        // The automatic player takes the first option offered both times: the draw is used up after the first.
        new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 5).Activate(id, 3).ExpectActivations(2)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").Check("Duel.GetLP(0)==9000").AssertPasses();
    }

    [Fact]
    public void OptionCondition_ReadsBeforeAColon()
    {
        var p = Monster(100110056, Options(Header(), B("if.no_cards").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }), B("do.draw"), B("do.gain_lp")));
        // The IF block and the draw are one option.
        p.Effects[0].Do[2].Link = ActionLink.And;
        Assert.Equal("You can activate 1 of these effects;\n ● If you control no monsters: Draw 1 card.\n ● Gain 1000 LP.", EffectTextGenerator.Generate(p));
    }

    [EngineFact]
    public void OptionCondition_LeavesOutTheOptionThatCantBeChosen()
    {
        const long id = 100110057;
        var p = SpellCard(id, new EffectEvent
        {
            When = B("when.activate"),
            Do =
            [
                Header(),
                B("if.no_cards").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }),
                B("do.draw"),
                B("do.gain_lp").Linked(ActionLink.Option),
            ],
        });
        // You control a monster: only the LP gain can be chosen, though the automatic player asks for the first option.
        new EngineScenario(p).Card(id, 0, "hand").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 5).Activate(id).ExpectActivations(1)
            .Check("Duel.GetLP(0)==9000").Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
    }

    // ---------------------------------------------------------------- "1 or both", an option's own cost

    private static BlockInstance Both() => Header().With("count", "both");

    [Fact]
    public void ChooseBoth_ReadsAndListsBoth()
    {
        var p = Monster(100110060, Options(Both(), B("do.draw"), B("do.gain_lp")));
        Assert.Equal("You can activate 1 or both of these effects;\n ● Draw 1 card.\n ● Gain 1000 LP.", EffectTextGenerator.Generate(p));
        Assert.Equal(["Draw 1 card.", "Gain 1000 LP.", ChooseEffectsAction.BothText], EffectTextGenerator.OptionTexts(p.Effects[0], p.Card));
        var lua = Lua(p);
        Assert.Contains("local b3=Duel.IsPlayerCanDraw(tp,1)", lua);
        Assert.Contains(",{b3,aux.Stringid(id,3),3})", lua);
        // Only with 2 options, and only one of them may target.
        var three = ScriptCompiler.Compile(Monster(100110061, Options(Both(), B("do.draw"), B("do.gain_lp"), B("do.draw"))));
        Assert.Contains(three.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("exactly 2 options"));
        var targets = ScriptCompiler.Compile(Monster(100110062, Options(Both(), B("do.destroy").With("target", true), B("do.banish").With("target", true))));
        Assert.Contains(targets.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("only one of the 2 options can target"));
    }

    [EngineTheory]
    [InlineData(0, 1, 8000)]
    [InlineData(1, 0, 9000)]
    [InlineData(2, 1, 9000)]
    public void ChooseBoth_AppliesWhatIsChosen(int option, int hand, int lp)
    {
        const long id = 100110063;
        new EngineScenario(Monster(id, Options(Both(), B("do.draw"), B("do.gain_lp")))).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 5).Option(option)
            .Activate(id).ExpectActivations(1)
            .Check($"Duel.GetFieldGroupCount(0,LOCATION_HAND,0)=={hand}").Check($"Duel.GetLP(0)=={lp}").AssertPasses();
    }

    /// <summary>Firestorms Over Atlantis, Blessed Winds: "● Discard 1 card; draw 2 cards."</summary>
    private static CardProject OptionCost(long id) => Monster(id, Options(Header(), B("cost.discard"), B("do.draw").With("count", 2), B("do.gain_lp")));

    [Fact]
    public void OptionCost_ReadsAndCompiles()
    {
        var p = OptionCost(100110064);
        // The cost and the draw are one option.
        p.Effects[0].Do[2].Link = ActionLink.And;
        Assert.Equal("You can activate 1 of these effects;\n ● Discard 1 card from your hand; draw 2 cards.\n ● Gain 1000 LP.", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("and (not e:IsCostChecked() or Duel.IsExistingMatchingCard(s.discardfilter,tp,LOCATION_HAND,0,1,e:GetHandler(),e,tp))", lua);
        Assert.Contains("\t\tif e:IsCostChecked() then\n\t\t\tDuel.DiscardHand(", lua);
        Assert.DoesNotContain("SetCost(", lua);
        // With a cost and a target: "Discard 1 card, then target ...".
        var targeted = Monster(100110065, Options(Header(), B("cost.discard"), B("do.destroy").With("target", true), B("do.gain_lp")));
        targeted.Effects[0].Do[2].Link = ActionLink.And;
        Assert.StartsWith("You can activate 1 of these effects;\n ● Discard 1 card from your hand, then target 1 card your opponent controls; destroy it.", EffectTextGenerator.Generate(targeted));
        Lua(targeted);
    }

    [Fact]
    public void OptionCost_OnlyAtTheStartOfAnOptionChosenOnActivation()
    {
        var noHeader = ScriptCompiler.Compile(Monster(100110066, new EffectEvent { When = B("when.ignition"), Do = [B("do.draw"), B("cost.discard")] }));
        Assert.Contains(noHeader.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("an option's own cost"));
        var late = Monster(100110067, Options(Header(), B("do.draw"), B("do.gain_lp")));
        late.Effects[0].Do.Insert(2, B("cost.discard"));
        Assert.Contains(ScriptCompiler.Compile(late).Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("at the start of its option"));
        var resolving = Monster(100110068, Options(Header(when: "apply"), B("cost.discard"), B("do.gain_lp")));
        Assert.Contains(ScriptCompiler.Compile(resolving).Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("paid as the effect is activated"));
    }

    [EngineFact]
    public void OptionCost_IsPaidWhenTheOptionIsChosen()
    {
        const long id = 100110069;
        var p = OptionCost(id);
        p.Effects[0].Do[2].Link = ActionLink.And;
        new EngineScenario(p).Card(id, 0, "mzone").Card(Fiend, 0, "hand").Card(Warrior, 0, "deck", 5).Option(0).Activate(id).ExpectActivations(1)
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_GRAVE,0,nil,1004)==1")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2").Check("Duel.GetLP(0)==8000").AssertPasses();
    }

    [EngineFact]
    public void OptionCost_OptionIsLeftOutWhenItCantBePaid()
    {
        const long id = 100110070;
        var p = OptionCost(id);
        p.Effects[0].Do[2].Link = ActionLink.And;
        // Nothing to discard: only the LP gain is offered, though the automatic player asks for the first option.
        new EngineScenario(p).Card(id, 0, "mzone").Card(Warrior, 0, "deck", 5).Option(0).Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").Check("Duel.GetLP(0)==9000").AssertPasses();
    }

    // ---------------------------------------------------------------- 1 each from both sides, up to the number of

    private static CardProject EachGy(long id) => SpellCard(id, Activate(B("do.banish").With("count", 2).With("each_zone", true)
        .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.Graveyard })));

    [Fact]
    public void EachPlayer_ReadsAndCompiles()
    {
        Assert.Equal("Banish 2 cards, 1 each from your GY and your opponent's GY.", EffectTextGenerator.Generate(EachGy(100110071)));
        var lua = Lua(EachGy(100110072));
        Assert.Contains("function s.gcheck(g,tp)", lua);
        Assert.Contains("g:FilterCount(Card.IsControler,nil,tp)<=1 and g:FilterCount(Card.IsControler,nil,1-tp)<=1", lua);
        Assert.Contains(":CheckSubGroup(s.gcheck,2,2,tp)", lua);
    }

    [EngineFact]
    public void EachPlayer_BanishesOneFromEachGY()
    {
        const long id = 100110073;
        new EngineScenario(EachGy(id)).Card(id, 0, "hand").Card(Warrior, 0, "grave", 2).Card(Fiend, 1, "grave", 2).Activate(id).ExpectActivations(1)
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_REMOVED,0,nil,1001)==1")
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,1,LOCATION_REMOVED,0,nil,1004)==1").AssertPasses();
    }

    [EngineFact]
    public void EachPlayer_NeedsACardOnEachSide()
    {
        const long id = 100110074;
        new EngineScenario(EachGy(id)).Card(id, 0, "hand").Card(Warrior, 0, "grave", 3).Activate(id).ExpectActivations(0).AssertPasses();
    }

    /// <summary>Ritual Beast Steeds: "Destroy monsters on the field up to the number of "Ritual Beast" monsters you currently control."</summary>
    private static CardProject Steeds(long id, bool target = false) => SpellCard(id, Activate(B("do.destroy").With("amount", "number_of").With("target", target)
        .With("cards", CardFilter.OpponentField(FilterCardType.Monster))
        .With("count_cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, Race = CardRace.Dragon })));

    [Fact]
    public void NumberOf_ReadsAndCompiles()
    {
        Assert.Equal("Destroy monsters your opponent controls, up to the number of face-up Dragon monsters you control.", EffectTextGenerator.Generate(Steeds(100110075)));
        Assert.Equal("Target monsters your opponent controls, up to the number of face-up Dragon monsters you control; destroy them.", EffectTextGenerator.Generate(Steeds(100110076, true)));
        var lua = Lua(Steeds(100110077));
        Assert.Contains("Duel.GetMatchingGroupCount(s.ctfilter,tp,LOCATION_MZONE,0,nil,e,tp)>0", lua);
        Assert.Matches(@"Duel\.SelectMatchingCard\(tp,s\.filter,tp,0,LOCATION_\w+,1,ct\d+,nil,e,tp\)", lua);
        Assert.Matches(@"Duel\.SelectTarget\(tp,s\.filter,tp,0,LOCATION_\w+,1,ct\d+,nil,e,tp\)", Lua(Steeds(100110078, true)));
    }

    [EngineTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void NumberOf_NeedsOneToCount(bool target)
    {
        const long id = 100110079;
        new EngineScenario(Steeds(id, target)).Card(id, 0, "hand").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone", 3).Activate(id).ExpectActivations(0).AssertPasses();
        new EngineScenario(Steeds(id, target)).Card(id, 0, "hand").Card(Dragon, 0, "mzone").Card(Fiend, 1, "mzone", 3).Activate(id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==2").AssertPasses();
    }
}

internal static class FilterTestExtensions
{
    public static CardFilter With(this CardFilter f, Action<CardFilter> change)
    {
        change(f);
        return f;
    }
}
