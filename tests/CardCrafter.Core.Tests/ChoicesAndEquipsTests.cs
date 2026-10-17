using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Choices inside effects and equipping: "add to your hand, or Special Summon, 1 ..." (the card is picked, then what
/// happens to it is chosen), "X, or, if ..., you can Y instead", "●" options with a shared target, chosen by the
/// opponent, applied in sequence, taken by where this card was sent from, or each with its own timing; and equipping a
/// card to the monster the step before Summoned, to the target or to one of the cards that started the effect,
/// "the equipped monster" in DO steps, "a "Therion" monster equipped with this card gains 700 ATK", giving one of this
/// card's effects to the equipped monster, and ", except Equip Spells".
/// </summary>
public class ChoicesAndEquipsTests
{
    private const long Id = 100480001, Second = 100480002, Third = 100480003;

    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    private static CardFilter That(CardRef that, FilterCardType type = FilterCardType.Any) => new() { That = that, CardType = type };
    private static CardFilter Mine(Zone zone, FilterCardType type = FilterCardType.Monster) => new() { Owner = CardOwner.You, Zones = zone, CardType = type };
    private static CardFilter Theirs(bool faceUp = true) => new() { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = faceUp };

    private static CardProject Monster(params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = Id, Name = "Choice Tester", Frame = MonsterFrame.Effect, Race = CardRace.Spellcaster, Attribute = CardAttribute.Dark, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    private static CardProject Spell(SpellKind kind, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = Id, Name = "Choice Spell", Kind = CardKind.Spell, SpellKind = kind },
        Effects = [.. effects],
    };

    private static CardProject Trap(params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = Id, Name = "Choice Trap", Kind = CardKind.Trap },
        Effects = [.. effects],
    };

    private static EffectEvent Ignition(params BlockInstance[] dos) => new() { When = B("when.ignition"), Do = [.. dos] };
    private static EffectEvent Activate(params BlockInstance[] dos) => new() { When = B("when.activate"), Do = [.. dos] };

    private static string Compiles(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)) + "\n" + r.Lua);
        Assert.Empty(ScriptChecker.Check(r.Lua));
        return r.Lua;
    }

    private static IEnumerable<string> Errors(CardProject p) => ScriptCompiler.Compile(p).Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.Message);

    private static string Text(CardProject p) => EffectTextGenerator.Generate(p);

    private static string In(int player, string location, long code, int count = 1) =>
        $"Duel.GetMatchingGroupCount(Card.IsCode,{player},{location},0,nil,{code})=={count}";

    private static string AtkOf(int player, long code, int atk) => $"Duel.GetFirstMatchingCard(Card.IsCode,{player},LOCATION_MZONE,0,nil,{code}):GetAttack()=={atk}";

    // ---------------------------------------------------------------- either / or on the picked cards

    private static BlockInstance Either(string first, string second, CardFilter cards, string wording = "verbs") =>
        B(EitherAction.BlockId).With("first", first).With("second", second).With("cards", cards).With("wording", wording);

    /// <summary>Ars Magna Artmage: "You can add to your hand, or Special Summon, 1 ... monster from your Deck, then ...".</summary>
    private static CardProject Artmage() => Monster(Ignition(
        Either("hand", "summon", Mine(Zone.Deck)),
        B("do.draw").Linked(ActionLink.Then)));

    [Fact]
    public void Either_ReadsLikeArsMagnaArtmage()
    {
        Assert.Equal("You can add to your hand, or Special Summon, 1 monster from your Deck, then draw 1 card.", Text(Artmage()));
        var lua = Compiles(Artmage());
        Assert.Contains("aux.SelectFromOptions(tp,{b1,1190,1},{b2,1152,2})", lua);
        Assert.Contains("CATEGORY_SPECIAL_SUMMON", lua);
        Assert.Contains("CATEGORY_SEARCH", lua);
    }

    [Fact]
    public void Either_TakeWording_ReadsLikeAiwassAndRiverStormer()
    {
        var aiwass = Monster(Ignition(Either("grave", "banish", Mine(Zone.Deck), "take")));
        Assert.Equal("You can take 1 monster from your Deck, and either send it to the GY or banish it.", Text(aiwass));
        var stormer = Monster(Ignition(Either("hand", "grave", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Machine, Attribute = CardAttribute.Earth }, "take")));
        Assert.Equal("You can take 1 EARTH Machine monster from your Deck, and either add it to your hand or send it to the GY.", Text(stormer));
        Compiles(stormer);
    }

    [Fact]
    public void Either_OnCardsYouControl_ReadsLikeDoomZAndElfnote()
    {
        var doomz = Monster(Ignition(Either("destroy", "banish", new CardFilter { Owner = CardOwner.You, Zones = Zone.Field })));
        Assert.Equal("You can destroy, or banish, 1 card you control.", Text(doomz));
        var elfnote = Monster(Ignition(Either("grave", "banish", new CardFilter { Owner = CardOwner.You, Zones = Zone.Field })));
        Assert.Equal("You can send to the GY, or banish, 1 card you control.", Text(elfnote));
        Assert.Contains("Duel.HintSelection", Compiles(doomz));
    }

    [Fact]
    public void Either_TheSameThingTwice_IsAWarning() =>
        Assert.Contains(ScriptCompiler.Compile(Monster(Ignition(Either("hand", "hand", Mine(Zone.Deck))))).Diagnostics,
            d => d.Severity == Severity.Warning && d.Message.Contains("two different things"));

    [EngineFact]
    public void Either_AddsToTheHand_ThenTheNextStepChains()
    {
        var p = Artmage();
        new EngineScenario(p)
            .Card(Id, 0, "mzone").Card(Warrior, 0, "deck").Card(Fiend, 0, "deck", 3).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1)
            // The first card the harness picks from the Deck is added to the hand (option 0), then 1 card is drawn.
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void Either_SpecialSummons_WhenThatIsChosen()
    {
        var p = Artmage();
        new EngineScenario(p)
            .Card(Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Option(1).ExpectActivations(1)
            .Check(In(0, "LOCATION_MZONE", Warrior))
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void Either_BanishesACardYouControl()
    {
        var p = Monster(Ignition(Either("destroy", "banish", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true })));
        new EngineScenario(p)
            .Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Option(1).ExpectActivations(1)
            .Check(In(0, "LOCATION_REMOVED", Warrior))
            .AssertPasses();
    }

    // ---------------------------------------------------------------- "X, or, if ..., you can Y instead"

    private static BlockInstance Header(string style = "list", string who = "you", string when = "activate") =>
        B(ChooseEffectsAction.BlockId).With("style", style).With("who", who).With("when", when);

    /// <summary>Cooky☆Yummy: "You can target 1 face-up monster your opponent controls; it loses 1000 ATK, or, if ..., you can destroy that monster instead."</summary>
    private static CardProject Cooky() => Monster(new EffectEvent
    {
        When = B("when.summoned").With("how", "normal"),
        AlsoWhen = [B("when.summoned").With("how", "special")],
        Limit = OncePerTurn.Hard,
        Do =
        [
            B(TargetCardsAction.BlockId).With("cards", Theirs()),
            Header("instead", when: "apply"),
            B("do.change_stats").With("cards", That(CardRef.Target)).With("dir", "lose").With("amount", 1000).With("duration", "always"),
            B("if.summoned_by").With("how", "by_synchro_effect").Linked(ActionLink.Option),
            B("do.destroy").With("cards", That(CardRef.Target)),
        ],
    });

    [Fact]
    public void Instead_ReadsLikeCookyYummy()
    {
        Assert.Equal(
            "If this card is Normal or Special Summoned: You can target 1 face-up monster your opponent controls; it loses 1000 ATK, or, if this card was " +
            "Special Summoned by the effect of a Synchro Monster, you can destroy that monster instead. You can only use this effect of \"Choice Tester\" once per turn.",
            Text(Cooky()));
        var lua = Compiles(Cooky());
        Assert.Contains("if chkc then return", lua);
        Assert.Contains("local op=aux.SelectFromOptions(tp,", lua);
    }

    [EngineFact]
    public void Instead_ShareTheTarget_AndOnlyTheFirstApplies_WithoutTheCondition()
    {
        var p = Cooky();
        new EngineScenario(p)
            .Card(Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Fiend, 1, "mzone").Card(Warrior, 1, "deck", 4)
            .Summon(Id).Activate(Id).ExpectActivations(1)
            .Check(AtkOf(1, Fiend, 0))
            .AssertPasses();
    }

    /// <summary>Mimighoul Slime: "... to your opponent's field in face-down Defense Position, or, if your opponent controls more monsters than you do, you can Special Summon it face-up on your field instead."</summary>
    private static CardProject Mimighoul() => Monster(new EffectEvent
    {
        When = B("when.ignition").With("zone", "hand"),
        Limit = OncePerTurn.Hard,
        Do =
        [
            Header("instead", when: "apply"),
            B("do.special_summon_self").With("side", "opponent").With("position", "defense_facedown"),
            B("if.opponent_more_monsters").Linked(ActionLink.Option),
            B("do.special_summon_self"),
        ],
    });

    [Fact]
    public void Instead_ReadsLikeMimighoul()
    {
        Assert.Equal(
            "You can Special Summon this card from your hand to your opponent's field in face-down Defense Position, or, if your opponent controls more monsters " +
            "than you do, you can Special Summon it face-up on your field instead. You can only use this effect of \"Choice Tester\" once per turn.",
            Text(Mimighoul()));
        Compiles(Mimighoul());
    }

    [EngineFact]
    public void Instead_Mimighoul_GoesToTheOpponentsField_OrYoursWhenTheyControlMore()
    {
        var p = Mimighoul();
        new EngineScenario(p)
            .Card(Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1)
            .Check(In(1, "LOCATION_MZONE", Id))
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{Id}):IsFacedown()")
            .AssertPasses();
        new EngineScenario(p)
            .Card(Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Fiend, 1, "mzone", 2).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Option(1).ExpectActivations(1)
            .Check(In(0, "LOCATION_MZONE", Id))
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):IsFaceup()")
            .AssertPasses();
    }

    /// <summary>Spright Elf: "You can target 1 Level 2 monster in your GY, or, if your opponent controls a monster, you can target 1 Rank/Link 2 monster instead; Special Summon it."</summary>
    private static CardProject Elf() => Monster(new EffectEvent
    {
        When = B("when.quick").With("timing", "main"),
        Do =
        [
            Header("instead"),
            B("do.special_summon").With("target", true).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, LevelMin = 2, LevelMax = 2 }),
            B("if.cards_exist").With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster }).Linked(ActionLink.Option),
            B("do.special_summon").With("target", true).With("cards", new CardFilter
            {
                Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, AnyRequirement = true,
                Requirements = [new() { Property = "rank", Op = RequirementOp.Equal, Value = 2 }, new() { Property = "link", Op = RequirementOp.Equal, Value = 2 }],
            }),
        ],
    });

    [Fact]
    public void Instead_TargetsOfTheSameAction_ReadLikeSprightElf()
    {
        Assert.EndsWith(
            "You can target 1 Level 2 monster from your GY, or, if your opponent controls a monster, you can target 1 monster whose Rank is 2 or whose Link Rating is 2 " +
            "from your GY instead; Special Summon it.", Text(Elf()));
        Compiles(Elf());
    }

    // ---------------------------------------------------------------- a shared target, options chosen by the opponent

    /// <summary>Mimighoul Fork: "Target 1 face-down monster your opponent controls; your opponent chooses 1 of these effects for you to apply. ● ... ● Send it to the GY, then its owner draws 2 cards."</summary>
    private static CardProject Fork() => Spell(SpellKind.Normal, Activate(
        B(TargetCardsAction.BlockId).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceDownOnly = true }),
        Header(who: "opponent"),
        B("do.change_position").With("cards", That(CardRef.Target)).With("to", "faceup_choice"),
        B("do.send_to_gy").With("cards", That(CardRef.Target)).Linked(ActionLink.Option),
        B("do.draw").With("who", "owner").With("count", 2).Linked(ActionLink.Then)));

    [Fact]
    public void SharedTarget_ReadsLikeMimighoulFork()
    {
        Assert.Equal(
            "Target 1 face-down monster your opponent controls; your opponent chooses 1 of these effects for you to apply." +
            "\n ● Change it to face-up Attack or Defense Position.\n ● Send it to the GY, then its owner draws 2 cards.",
            Text(Fork()));
        var lua = Compiles(Fork());
        Assert.Contains("aux.SelectFromOptions(1-tp,", lua);
        Assert.Contains("Duel.Draw(", lua);
        Assert.Contains(":GetOwner(),2", lua);
    }

    [EngineFact]
    public void SharedTarget_TheOpponentsChoice_SendsItAndItsOwnerDraws()
    {
        var p = Fork();
        // The harness places monsters face-up: the target here is any monster the opponent controls.
        p.Effects[0].Do[0].Filters["cards"].FaceDownOnly = false;
        new EngineScenario(p)
            .Card(Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Fiend, 1, "mzone").Card(Warrior, 1, "deck", 6)
            .Activate(Id).Option(1).ExpectActivations(1)
            .Check(In(1, "LOCATION_GRAVE", Fiend))
            // The 2 cards its owner drew, and their next normal draw.
            .Check("Duel.GetFieldGroupCount(1,LOCATION_HAND,0)==3")
            .AssertPasses();
    }

    [Fact]
    public void SharedTarget_WithATargetingOption_IsAnError()
    {
        var p = Fork();
        p.Effects[0].Do[2] = B("do.destroy").With("target", true).With("cards", Theirs());
        Assert.Contains(Errors(p), m => m.Contains("share a target"));
    }

    // ---------------------------------------------------------------- in sequence

    /// <summary>Sun God Domination: "Apply these effects in sequence. ● ... ● If ..., take control of ...": each part that can be applied.</summary>
    private static CardProject Sequence() => Spell(SpellKind.Normal, Activate(
        Header("sequence"),
        B("do.destroy").With("mode", "all").With("cards", Theirs()),
        B("if.no_cards").With("cards", Mine(Zone.MonsterZone)).Linked(ActionLink.Option),
        B("do.draw")));

    [Fact]
    public void Sequence_ReadsLikeSunGodDomination()
    {
        Assert.Equal(
            "Apply these effects in sequence.\n ● Destroy all face-up monsters your opponent controls.\n ● If you control no monsters, draw 1 card.",
            Text(Sequence()));
        var lua = Compiles(Sequence());
        Assert.Contains("if applied then Duel.BreakEffect() end", lua);
        Assert.DoesNotContain("SelectFromOptions", lua);
    }

    [EngineFact]
    public void Sequence_AppliesEachPartThatCanBe()
    {
        var p = Sequence();
        // The opponent has no monsters: only the draw applies, and the card can still be activated.
        new EngineScenario(p)
            .Card(Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .AssertPasses();
        new EngineScenario(p)
            .Card(Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Fiend, 1, "mzone", 2).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1)
            .Check(In(1, "LOCATION_GRAVE", Fiend, 2))
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- based on where it was sent from

    /// <summary>Aitvaras: "If this card is sent to the GY: You can activate this effect based on where it was sent from; ● Field: ... ● Hand: ...".</summary>
    private static CardProject SentFrom() => Monster(new EffectEvent
    {
        When = B("when.sent_to_gy"),
        Do =
        [
            Header("sent_from"),
            B(SentFromCondition.BlockId).With("from", "field"),
            B("do.gain_lp").With("amount", 1000),
            B(SentFromCondition.BlockId).With("from", "hand").Linked(ActionLink.Option),
            B("do.draw"),
        ],
    });

    [Fact]
    public void SentFrom_ReadsLikeAitvaras()
    {
        Assert.Equal(
            "If this card is sent to the GY: You can activate this effect based on where it was sent from;\n ● Field: Gain 1000 LP.\n ● Hand: Draw 1 card.",
            Text(SentFrom()));
        var lua = Compiles(SentFrom());
        Assert.Contains("local op=b1 and 1 or b2 and 2 or nil", lua);
        Assert.Contains("IsPreviousLocation(LOCATION_HAND)", lua);
    }

    [Fact]
    public void SentFrom_AnOptionWithoutWhereItWasSentFrom_IsAnError()
    {
        var p = SentFrom();
        p.Effects[0].Do[3] = B("do.draw").Linked(ActionLink.Option);
        Assert.Contains(Errors(p), m => m.Contains("where it was sent from"));
    }

    [EngineFact]
    public void SentFrom_TakesTheOptionForTheHand()
    {
        var p = SentFrom();
        // A Spell that discards: this card goes from the hand to the GY.
        var discard = new CardProject
        {
            Card = new CardData { Id = Second, Name = "Discarder", Kind = CardKind.Spell },
            Effects = [Activate(B("do.send_to_gy").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardCode = Id }))],
        };
        new EngineScenario(p, discard)
            .Card(Second, 0, "hand").Card(Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Second).Activate(Id).ExpectActivations(2)
            .Check(In(0, "LOCATION_GRAVE", Id))
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .Check("Duel.GetLP(0)==8000")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- an option's own timing

    /// <summary>Counter Spell "Negate Attack": "During the Battle Phase: Activate 1 of these effects; ● When an opponent's monster declares an attack while they control more monsters than you do: ... ● When your opponent activates a monster effect: ...".</summary>
    private static CardProject NegateAttack() => Trap(new EffectEvent
    {
        When = B("when.activate"),
        If = [B("if.phase").With("phase", "battle")],
        Do =
        [
            Header(),
            B(AttackDeclaredTrigger.BlockId).With("mode", "activate"),
            B("if.opponent_more_monsters"),
            B("do.negate_attack").With("then", "end_bp"),
            B(ActivationTrigger.BlockId).With("mode", "activate").With("what", "monster").Linked(ActionLink.Option),
            B("do.negate").With("what", "effect").With("and", "destroy"),
        ],
    });

    [Fact]
    public void OptionTimings_ReadLikeNegateAttack()
    {
        var text = Text(NegateAttack());
        Assert.Contains("Activate 1 of these effects;\n ● When an opponent's monster declares an attack while they control more monsters than you do: Negate the attack, then end the Battle Phase.", text);
        Assert.Contains("\n ● When your opponent activates a monster effect: Negate that effect, and if you do, destroy that card.", text);
        var lua = Compiles(NegateAttack());
        Assert.Contains("SetCode(EVENT_ATTACK_ANNOUNCE)", lua);
        Assert.Contains("SetCode(EVENT_CHAINING)", lua);
        Assert.Contains("e:GetCode()==EVENT_CHAINING", lua);
        Assert.Contains("Duel.IsChainDisablable(ev)", lua);
    }

    [Fact]
    public void OptionTiming_ThatActivatesAnotherWay_IsAnError()
    {
        var p = NegateAttack();
        p.Effects[0].Do[1] = B(AttackDeclaredTrigger.BlockId).With("mode", "quick");
        Assert.Contains(Errors(p), m => m.Contains("same way"));
    }

    [EngineFact]
    public void OptionTiming_NegatesTheAttack_WhenTheOpponentControlsMore()
    {
        var p = NegateAttack();
        new EngineScenario(p)
            .Card(Id, 0, "szone").Card(Warrior, 0, "deck", 4).Card(Fiend, 1, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 1, "deck", 4)
            .Attack(Fiend).Activate(Id).Turns(2).ExpectActivations(1)
            .Check("Duel.GetLP(0)==8000")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- equipping

    private static BlockInstance EquipThis(CardRef to) => B(EquipAction.BlockId).With("to", new CardFilter { That = to, CardType = FilterCardType.Monster });

    /// <summary>Salamangreat Gearblade: "Special Summon 1 ... monster from your hand or GY, and if you do, equip this card to it." / "...; the equipped monster gains ATK equal to the sent monster's Level x 300."</summary>
    private static CardProject Gearblade() => new()
    {
        Card = new CardData { Id = Id, Name = "Gear Spell", Kind = CardKind.Spell, SpellKind = SpellKind.Equip },
        Effects =
        [
            Activate(B("do.special_summon").With("cards", Mine(Zone.Hand | Zone.Graveyard)), EquipThis(CardRef.Affected).Linked(ActionLink.IfYouDo)),
            new EffectEvent
            {
                When = B("when.ignition").With("zone", "spell_trap_zone"),
                Cost = [B("cost.move_cards").With("how", "grave").With("cards", Mine(Zone.Deck))],
                Do = [B("do.stats_value").With("cards", That(CardRef.Equipped, FilterCardType.Monster)).With("value", "cost_level").With("how", "per").With("amount", 300).With("duration", "always")],
            },
        ],
    };

    [Fact]
    public void EquipThisCard_ReadsLikeGearblade()
    {
        var text = Text(Gearblade());
        Assert.Contains("Special Summon 1 monster from your hand or GY, and if you do, equip this card to it.", text);
        Assert.Contains("the equipped monster gains ATK equal to the sent monster's Level x 300.", text);
        var lua = Compiles(Gearblade());
        Assert.Contains("Duel.Equip(tp,", lua);
        Assert.Contains("e:GetHandler():GetEquipTarget()~=nil", lua);
    }

    [EngineFact]
    public void EquipThisCard_ToTheSummonedMonster_ThenTheEquippedMonsterGainsAtk()
    {
        var p = Gearblade();
        new EngineScenario(p)
            .Card(Id, 0, "hand").Card(Warrior, 0, "grave").Card(Fiend, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id, 2).ExpectActivations(2)
            .Check(In(0, "LOCATION_SZONE", Id))
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Warrior}):GetEquipCount()==1")
            .Check(AtkOf(0, Warrior, 1500 + 4 * 300))
            .AssertPasses();
    }

    /// <summary>Invoked Magistus Omega: "Special Summon 1 ... monster from your Deck, then you can equip this card to it as an Equip Spell that gives it 1000 ATK."</summary>
    private static CardProject Magistus() => Monster(new EffectEvent
    {
        When = B("when.ignition").With("zone", "graveyard"),
        Do =
        [
            B("do.special_summon").With("cards", Mine(Zone.Deck)),
            EquipThis(CardRef.Affected).With("gives", "atk").With("amount", 1000).Linked(ActionLink.Then).MayDo(),
        ],
    });

    [Fact]
    public void EquipAsAnEquipSpell_ReadsLikeMagistusOmega()
    {
        Assert.Equal("If this card is in your GY: You can Special Summon 1 monster from your Deck, then you can equip this card to it as an Equip Spell that gives it 1000 ATK.", Text(Magistus()));
        Assert.Contains("EFFECT_TYPE_EQUIP", Compiles(Magistus()));
    }

    [EngineFact]
    public void EquipAsAnEquipSpell_GivesItAtk()
    {
        var p = Magistus();
        new EngineScenario(p)
            .Card(Id, 0, "grave").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1)
            .Check(In(0, "LOCATION_SZONE", Id))
            .Check(AtkOf(0, Warrior, 2500))
            .AssertPasses();
    }

    /// <summary>Marincess Azuri: "If ... monster(s) is Special Summoned to your field, while this card is in your GY: You can banish this card; equip 1 monster from your GY to 1 of those monsters as an Equip Spell that gives it 600 ATK."</summary>
    private static CardProject Azuri() => Monster(new EffectEvent
    {
        When = B("when.cards_event").With("event", "special").With("zone", "graveyard").With("cards", Mine(Zone.MonsterZone)),
        Cost = [B("cost.this_card").With("how", "banish")],
        Do =
        [
            B(EquipAction.BlockId).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster })
                .With("to", new CardFilter { That = CardRef.Event, CardType = FilterCardType.Monster }).With("gives", "atk").With("amount", 600),
        ],
    });

    [Fact]
    public void EquipToOneOfThoseMonsters_ReadsLikeMarincessAzuri()
    {
        Assert.Contains("equip 1 monster from your GY to 1 of those monsters as an Equip Spell that gives it 600 ATK.", Text(Azuri()));
        Assert.Contains("CreateEffectRelation(e)", Compiles(Azuri()));
    }

    [EngineFact]
    public void EquipToOneOfThoseMonsters_EquipsAMonsterFromTheGy()
    {
        var p = Azuri();
        var summoner = new CardProject
        {
            Card = new CardData { Id = Second, Name = "Summoner", Kind = CardKind.Spell },
            Effects = [Activate(B("do.special_summon").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster, CardCode = Warrior }))],
        };
        new EngineScenario(p, summoner)
            .Card(Second, 0, "hand").Card(Warrior, 0, "hand").Card(Id, 0, "grave").Card(Fiend, 0, "grave").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Second).Activate(Id).ExpectActivations(2)
            .Check(In(0, "LOCATION_REMOVED", Id))
            .Check(In(0, "LOCATION_SZONE", Fiend))
            .Check(AtkOf(0, Warrior, 2100))
            .AssertPasses();
    }

    /// <summary>
    /// Therion "King" Regulus: "A "Therion" monster equipped with this card gains 700 ATK, also it can activate the 2nd effect
    /// listed above as if it were ..." — a monster that equips itself to a Warrior, then gives it its draw.
    /// </summary>
    private static CardProject Regulus() => Monster(
        Ignition(B(EquipAction.BlockId).With("to", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true })),
        new EffectEvent { When = B("when.ignition"), Limit = OncePerTurn.Hard, Do = [B("do.draw")] },
        new EffectEvent
        {
            When = B("when.while_face_up").With("zone", "spell_trap_zone"),
            Do =
            [
                B(EquippedPassive.BlockId).With("only", "cards").With("equipped", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Warrior }).With("amount", 700),
                B(GrantEffectPassive.BlockId).With("only", "cards").With("equipped", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Warrior }).With("effect", 2),
            ],
        });

    [Fact]
    public void GrantedEffect_ReadsLikeRegulus()
    {
        var text = Text(Regulus());
        Assert.Contains("A Warrior monster equipped with this card gains 700 ATK, also it can activate the 2nd effect listed above as if it were \"Choice Tester\".", text);
        var lua = Compiles(Regulus());
        Assert.Contains("EFFECT_TYPE_FIELD+EFFECT_TYPE_GRANT", lua);
        Assert.Contains("SetLabelObject(e2)", lua);
    }

    [Fact]
    public void GrantedEffect_ListedAfterIt_IsAnError()
    {
        var p = Regulus();
        p.Effects[2].Do[1].Values["effect"] = "3";
        Assert.Contains(Errors(p), m => m.Contains("listed before"));
    }

    [EngineFact]
    public void GrantedEffect_TheEquippedWarriorGainsAtkAndDraws()
    {
        var p = Regulus();
        new EngineScenario(p)
            .Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Fiend, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Activate(Warrior).ExpectActivations(2)
            .Check(In(0, "LOCATION_SZONE", Id))
            .Check(AtkOf(0, Warrior, 2200))
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- reading official scripts

    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScripts!, $"c{id}.lua"));

    /// <summary>The DO blocks of the effect of <paramref name="id"/>'s script that became a "do.either" block.</summary>
    private static (EffectEvent Effect, BlockInstance Either) Translated(long id, CardData card)
    {
        var result = ScriptTranslator.Translate(Script(id), id, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).Single(e => e.Do.Any(b => b.BlockId == EitherAction.BlockId));
        return (effect, effect.Do.Single(b => b.BlockId == EitherAction.BlockId));
    }

    [EngineFact]
    public void Translator_ReadsSendToTheGyOrBanish_LikeOrcustratedEinsatz()
    {
        var (effect, either) = Translated(62834295, new CardData { Id = Id, Name = "Einsatz", Kind = CardKind.Trap, TrapKind = TrapKind.Continuous });
        Assert.Equal("grave", either.Values["first"]);
        Assert.Equal("banish", either.Values["second"]);
        Assert.Equal(Zone.Hand | Zone.Deck, either.Filters["cards"].Zones);
        Assert.Contains("You can send to the GY, or banish, 1 ", EffectTextGenerator.Describe(effect, new CardData { Name = "Einsatz", Kind = CardKind.Trap }));
    }

    [EngineFact]
    public void Translator_ReadsAddOrSendToTheGy_AsTakeAndEither()
    {
        var (effect, either) = Translated(44335251, new CardData { Id = Id, Name = "Oviraptor", Level = 4, Atk = 1800, Def = 500 });
        Assert.Equal("take", either.Values["wording"]);
        Assert.Equal(CardRace.Dinosaur, either.Filters["cards"].Race);
        Assert.Contains("take 1 Dinosaur monster from your Deck, and either add it to your hand or send it to the GY", EffectTextGenerator.Describe(effect, new CardData { Name = "Oviraptor" }));
    }

    [EngineFact]
    public void Translator_ReadsTheTargetAddedOrSpecialSummoned_LikeZexalEntrust()
    {
        var card = new CardData { Id = Id, Name = "Entrust", Kind = CardKind.Spell };
        var (effect, either) = Translated(4017398, card);
        Assert.Equal("true", either.Values["target"]);
        Assert.Equal("summon", either.Values["second"]);
        // The translation plays: the targeted monster in the GY is Special Summoned when that is chosen.
        var p = new CardProject { Card = card, Effects = [effect] };
        var monster = new CardProject { Card = new CardData { Id = Second, Name = "Zexal Monster", Frame = MonsterFrame.Normal, Level = 4, Atk = 1000, Def = 1000, Archetypes = [0x107e] } };
        new EngineScenario(p, monster)
            .Card(Id, 0, "hand").Card(Second, 0, "grave").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).Option(1).ExpectActivations(1)
            .Check(In(0, "LOCATION_MZONE", Second))
            .AssertPasses();
    }

    // ---------------------------------------------------------------- ", except Equip Spells"

    [Fact]
    public void ExceptEquipSpells_ReadsAtTheEnd_AndCompiles()
    {
        var cards = new CardFilter
        {
            Owner = CardOwner.You, Zones = Zone.Graveyard, Archetype = 0x119,
            Requirements = [new() { Property = CardProperties.SpellTrapKind, Op = RequirementOp.IsNot, Values = [3] }],
        };
        var p = Monster(Ignition(B("do.shuffle_into_deck").With("count", 3).With("cards", cards), B("do.draw").Linked(ActionLink.IfYouDo)));
        Assert.Contains("cards from your GY into the Deck, except Equip Spells, and if you do, draw 1 card.", Text(p));
        Assert.Contains("not (c:IsType(TYPE_EQUIP))", Compiles(p));
    }
}
