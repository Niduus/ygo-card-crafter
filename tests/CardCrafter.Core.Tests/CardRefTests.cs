using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// "That card": DO steps acting on this effect's target, the card the step before affected, the card used for the cost or
/// the monster this card battled ("Special Summon that monster to your field"), values and requirements that read them
/// ("equal to the sent monster's ATK", "with a different Attribute"), "only if" gates on them ("then if it is a Dragon
/// monster"), "Target cards", and the Special Summon options "but negate its effects" and "both it and this card".
/// </summary>
public class CardRefTests
{
    private const long Id = 100470001, Second = 100470002;

    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    private static CardFilter That(CardRef that, FilterCardType type = FilterCardType.Any) => new() { That = that, CardType = type };

    private static CardRequirement Ref(string property, CardRef that, bool not = false) =>
        new() { Property = property, Op = not ? RequirementOp.IsNot : RequirementOp.Is, Values = [(long)that] };

    private static CardFilter Mine(Zone zone) => new() { Owner = CardOwner.You, Zones = zone, CardType = FilterCardType.Monster };
    private static CardFilter Theirs => new() { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    private static CardProject Monster(params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = Id, Name = "Ref Tester", Frame = MonsterFrame.Effect, Race = CardRace.Machine, Attribute = CardAttribute.Earth, Level = 4, Atk = 1800, Def = 1000 },
        Effects = [.. effects],
    };

    private static CardProject Spell(params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = Id, Name = "Ref Spell", Kind = CardKind.Spell },
        Effects = [.. effects],
    };

    private static EffectEvent Ignition(params BlockInstance[] dos) => new() { When = B("when.ignition"), Do = [.. dos] };

    private static string Compiles(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        Assert.Empty(ScriptChecker.Check(r.Lua));
        return r.Lua;
    }

    private static string Text(CardProject p) => EffectTextGenerator.Generate(p);

    private static EngineScenario Duel(CardProject p) => new EngineScenario(p).Card(Warrior, 1, "deck", 4);

    private static string In(int player, string location, long code, int count = 1) =>
        $"Duel.GetMatchingGroupCount(Card.IsCode,{player},{location},0,nil,{code})=={count}";

    private static string AtkOf(int player, long code, int atk) => $"Duel.GetFirstMatchingCard(Card.IsCode,{player},LOCATION_MZONE,0,nil,{code}):GetAttack()=={atk}";

    // ---------------------------------------------------------------- Target cards, and steps on the target

    /// <summary>Kashtiratheosis: "Target 1 ... monster you control; Special Summon 1 ... monster with a different Attribute from your Deck".</summary>
    private static CardProject Theosis() => Spell(new EffectEvent
    {
        When = B("when.activate"),
        Do =
        [
            B(TargetCardsAction.BlockId).With("cards", Mine(Zone.MonsterZone)),
            B("do.special_summon").With("position", "defense").With("cards", new CardFilter
            {
                Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, Requirements = [Ref("ref_attribute", CardRef.Target, not: true)],
            }),
        ],
    });

    [Fact]
    public void TargetThenADifferentAttribute_ReadsLikeKashtiratheosis()
    {
        var p = Theosis();
        Assert.Equal("Target 1 monster you control; Special Summon 1 monster with a different Attribute from your Deck in Defense Position.", Text(p));
        var lua = Compiles(p);
        // The target is chosen; as the official script does, only one with a monster of another Attribute in the Deck.
        Assert.Contains("Duel.SelectTarget(tp,", lua);
        Assert.Contains("CHAININFO_TRIGGERING_EFFECT,CHAININFO_TARGET_CARDS", lua);
        Assert.Contains("s.ccpairtarget=Group.FromCards(c)", lua);
    }

    [EngineFact]
    public void TargetThenADifferentAttribute_SummonsTheOtherAttribute()
    {
        // Your EARTH Warrior is the target: of the EARTH Warriors and the DARK Fiend in the Deck, only the Fiend can come.
        Duel(Theosis()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Fiend, 0, "deck")
            .Activate(Id).ExpectActivations(1)
            .Check(In(0, "LOCATION_MZONE", Fiend)).Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==2").AssertPasses();
    }

    [EngineFact]
    public void TargetThenADifferentAttribute_NeedsAMonsterOfAnotherAttribute()
    {
        // Only EARTH Warriors in the Deck: the EARTH Warrior can't be targeted, so it can't be activated.
        Duel(Theosis()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(0).Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1").AssertPasses();
    }

    /// <summary>Machina Citadel: "target 1 Machine monster you control; destroy it and all monsters your opponent controls with ATK less than or equal to it".</summary>
    private static CardProject Citadel() => Monster(Ignition(
        B(TargetCardsAction.BlockId).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Warrior, FaceUpOnly = true }),
        B("do.destroy").With("mode", "all").With("plus", "target").With("cards", Theirs.With(Ref("ref_atk_le", CardRef.Target)))));

    [Fact]
    public void DestroyItAndAllWithLessAtk_ReadsLikeMachinaCitadel()
    {
        var p = Citadel();
        Assert.Equal("You can target 1 face-up Warrior monster you control; destroy it and all face-up monsters with ATK less than or equal to that monster's ATK your opponent controls.", Text(p));
        Assert.Contains(":Merge(Duel.GetChainInfo(0,CHAININFO_TARGET_CARDS):Filter(Card.IsRelateToEffect,nil,e))", Compiles(p));
    }

    [EngineFact]
    public void DestroyItAndAllWithLessAtk_DestroysTheTargetAndTheWeakerOnes()
    {
        // The 1500 ATK Warrior is the target: the 1000 ATK Fiend goes with it, the 1600 ATK Dragon stays.
        Duel(Citadel()).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(1)
            .Check(In(0, "LOCATION_GRAVE", Warrior)).Check(In(1, "LOCATION_GRAVE", Fiend)).Check(In(1, "LOCATION_MZONE", Dragon)).AssertPasses();
    }

    /// <summary>Invoked Elysium: "target 1 ... monster you control; banish it and all monsters your opponent controls with the same Attribute as that monster".</summary>
    [EngineFact]
    public void BanishItAndAllWithTheSameAttribute()
    {
        var p = Monster(Ignition(
            B(TargetCardsAction.BlockId).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Warrior }),
            B("do.banish").With("mode", "all").With("plus", "target").With("cards", Theirs.With(Ref("ref_attribute", CardRef.Target)))));
        Assert.Equal("You can target 1 Warrior monster you control; banish it and all face-up monsters with the same Attribute as that monster your opponent controls.", Text(p));
        Duel(p).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 1, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(1)
            .Check(In(0, "LOCATION_REMOVED", Warrior)).Check(In(1, "LOCATION_REMOVED", Warrior)).Check(In(1, "LOCATION_MZONE", Fiend)).AssertPasses();
    }

    /// <summary>Karin: "target 1 Level 4 monster in your GY; take damage equal to its ATK, and if you do, return both it and this card to the hand".</summary>
    private static CardProject Karin() => Monster(Ignition(
        B(TargetCardsAction.BlockId).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, LevelMin = 4, LevelMax = 4 }),
        B(LifeFromValueAction.BlockId).With("who", "you").With("value", "target_atk"),
        B("do.return_to_hand").With("plus", "this").With("cards", That(CardRef.Target)).Linked(ActionLink.IfYouDo)));

    [Fact]
    public void DamageEqualToTheTarget_ThenReturnBoth_Reads()
    {
        var p = Karin();
        Assert.Equal("You can target 1 Level 4 monster in your GY; you take damage equal to the targeted monster's ATK, and if you do, return both it and this card to the hand.", Text(p));
        Compiles(p);
    }

    [EngineFact]
    public void DamageEqualToTheTarget_ThenReturnBoth()
    {
        Duel(Karin()).Card(Id, 0, "mzone").Card(Warrior, 0, "grave").Card(Warrior, 0, "deck", 4)
            .Activate(Id).ExpectActivations(1)
            .Check("Duel.GetLP(0)==6500").Check(In(0, "LOCATION_HAND", Warrior)).Check(In(0, "LOCATION_HAND", Id)).AssertPasses();
    }

    /// <summary>Sword of Mutual Destruction: "target 1 face-up monster; it gains 1500 ATK, also it cannot attack this turn".</summary>
    [Fact]
    public void ItGainsAtk_AlsoItCannotAttack_Reads()
    {
        var p = Monster(Ignition(
            B("do.change_stats").With("target", true).With("amount", 1500).With("cards", Mine(Zone.MonsterZone)),
            B("do.battle_rule").With("target", false).With("rule", "cannot_attack").With("cards", That(CardRef.Target))));
        Assert.Equal("You can target 1 monster you control; it gains 1500 ATK until the end of this turn, and it cannot attack this turn.", Text(p));
        Assert.Contains("EFFECT_CANNOT_ATTACK", Compiles(p));
    }

    /// <summary>"Target 1 monster you control; it gains 1500 ATK, also negate its effects": a second step on the same target.</summary>
    [EngineFact]
    public void TwoStepsOnTheSameTarget()
    {
        var mine = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Effect, ExcludeThisCard = true };
        var p = Monster(Ignition(
            B("do.change_stats").With("target", true).With("amount", 1500).With("duration", "always").With("cards", mine),
            B("do.negate_effects").With("duration", "always").With("cards", That(CardRef.Target))));
        Assert.Equal("You can target 1 other Effect Monster you control; it gains 1500 ATK, and negate its effects.", Text(p));
        Duel(p).Card(Id, 0, "mzone").Card(EffectFiller, 0, "mzone").Card(Warrior, 0, "deck", 4).Activate(Id).ExpectActivations(1)
            .Check($"Duel.GetMatchingGroupCount(function(c) return c:IsCode({EffectFiller}) and c:GetAttack()==2900 and c:IsDisabled() end,0,LOCATION_MZONE,0,nil)==1")
            .AssertPasses();
    }

    /// <summary>Without a block that targets, "this effect's target" is an error.</summary>
    [Fact]
    public void TheTarget_NeedsABlockThatTargets()
    {
        var p = Monster(Ignition(B("do.destroy").With("cards", That(CardRef.Target))));
        Assert.True(ScriptCompiler.Compile(p).HasErrors);
        var q = Monster(Ignition(B("do.special_summon").With("cards", Mine(Zone.Deck).With(Ref("ref_attribute", CardRef.Target)))));
        Assert.True(ScriptCompiler.Compile(q).HasErrors);
    }

    // ---------------------------------------------------------------- the card the step before affected

    /// <summary>Voloferniges: "destroy it, and if you do, this card gains ATK equal to the destroyed monster's original Level/Rank x 300".</summary>
    [EngineFact]
    public void AtkEqualToThatMonstersOriginalLevelOrRank()
    {
        var p = Monster(Ignition(
            B("do.destroy").With("target", true).With("cards", Theirs),
            B(StatsFromValueAction.BlockId).With("cards", new CardFilter { ThisCard = true }).With("how", "per").With("amount", 300).With("value", "that_orig_lvrk")
                .With("duration", "opp_turn").Linked(ActionLink.IfYouDo)));
        Assert.Equal("You can target 1 face-up monster your opponent controls; destroy it, and if you do, this card gains ATK equal to that monster's original Level/Rank x 300 until the end of your opponent's turn.", Text(p));
        Duel(p).Card(Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 4).Activate(Id).ExpectActivations(1)
            .Check(In(1, "LOCATION_GRAVE", Fiend)).Check(AtkOf(0, Id, 3000)).AssertPasses();
    }

    /// <summary>Gandora-X: "destroy all other monsters, and if you do, inflict damage equal to the highest original ATK among them; this card's ATK becomes equal to the damage inflicted".</summary>
    private static CardProject Gandora() => Monster(new EffectEvent
    {
        When = B("when.summoned"),
        Do =
        [
            B("do.destroy").With("mode", "all").With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true }),
            B(LifeFromValueAction.BlockId).With("value", "that_max_orig_atk").Linked(ActionLink.IfYouDo),
            B(StatsFromValueAction.BlockId).With("cards", new CardFilter { ThisCard = true }).With("dir", "becomes").With("value", "damage_dealt").With("duration", "always"),
        ],
    });

    [Fact]
    public void HighestOriginalAtk_AndTheDamageInflicted_Read()
    {
        Assert.Equal("If this card is Normal Summoned: You can destroy all other monsters on the field, and if you do, inflict damage to your opponent equal to the highest original ATK among those monsters, " +
                     "and this card's ATK becomes equal to the damage inflicted.", Text(Gandora()));
        Assert.Contains($"{ValueSources.DamageField}=r", Compiles(Gandora()));
    }

    [EngineFact]
    public void HighestOriginalAtk_AndTheDamageInflicted()
    {
        Duel(Gandora()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Dragon, 1, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Summon(Id).Activate(Id).ExpectActivations(1)
            .Check("Duel.GetLP(1)==6400").Check(AtkOf(0, Id, 1600)).Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,LOCATION_MZONE)==1").AssertPasses();
    }

    /// <summary>"Send 1 monster from your Deck to the GY, then if it is a Dragon monster, draw 1 card."</summary>
    private static CardProject IfItIs() => Monster(Ignition(
        B("do.send_to_gy").With("cards", Mine(Zone.Deck)),
        B(OnlyIfAction.BlockId).With("is", "it_is").With("cards", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Dragon }).Linked(ActionLink.Then),
        B("do.draw")));

    [Fact]
    public void IfItIs_Reads()
    {
        Assert.Equal("You can send 1 monster from your Deck to the GY, then if it is a Dragon monster, draw 1 card.", Text(IfItIs()));
        Compiles(IfItIs());
    }

    [EngineTheory]
    [InlineData(Dragon, 1)]
    [InlineData(EngineScenario.Warrior, 0)]
    public void IfItIs_DrawsOnlyForADragon(long deck, int drawn)
    {
        Duel(IfItIs()).Card(Id, 0, "mzone").Card(deck, 0, "deck", 4).Activate(Id).ExpectActivations(1)
            .Check($"Duel.GetFieldGroupCount(0,LOCATION_HAND,0)=={drawn}").AssertPasses();
    }

    /// <summary>Reindsurm Giriakarma: "destroy all monsters your opponent controls, also if any of them was a Dragon monster, ...".</summary>
    [EngineTheory]
    [InlineData(Dragon, 1)]
    [InlineData(Fiend, 0)]
    public void IfAnyOfThemWas(long theirs, int drawn)
    {
        var p = Monster(Ignition(
            B("do.destroy").With("mode", "all").With("cards", Theirs),
            B(OnlyIfAction.BlockId).With("is", "any_was").With("cards", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Dragon }),
            B("do.draw")));
        Assert.Equal("You can destroy all face-up monsters your opponent controls, and if any of them was a Dragon monster, draw 1 card.", Text(p));
        Duel(p).Card(Id, 0, "mzone").Card(theirs, 1, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Activate(Id).ExpectActivations(1)
            .Check($"Duel.GetFieldGroupCount(0,LOCATION_HAND,0)=={drawn}").AssertPasses();
    }

    // ---------------------------------------------------------------- the cards used for the cost

    /// <summary>Warrior's Devotion: "Send 1 Warrior monster from your hand or Deck to the GY, then target 1 face-up monster your opponent controls; it loses ATK equal to the sent monster's ATK."</summary>
    private static CardProject Devotion() => Spell(new EffectEvent
    {
        When = B("when.activate"),
        Cost = [B("cost.move_cards").With("how", "grave").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Warrior })],
        Do = [B(StatsFromValueAction.BlockId).With("target", true).With("dir", "lose").With("value", "cost_atk").With("duration", "always").With("cards", Theirs)],
    });

    [Fact]
    public void AtkOfTheSentMonster_ReadsLikeWarriorsDevotion()
    {
        Assert.Equal("Send 1 Warrior monster from your hand or Deck to the GY, then target 1 face-up monster your opponent controls; it loses ATK equal to the sent monster's ATK.",
            Text(Devotion()));
        var lua = Compiles(Devotion());
        Assert.Contains("e:SetLabelObject(cg)", lua);
    }

    [EngineFact]
    public void AtkOfTheSentMonster()
    {
        Duel(Devotion()).Card(Id, 0, "hand").Card(Dragon, 1, "mzone").Card(Warrior, 0, "deck", 4).Activate(Id).ExpectActivations(1)
            .Check(AtkOf(1, Dragon, 100)).Check(In(0, "LOCATION_GRAVE", Warrior)).AssertPasses();
    }

    /// <summary>The Book of the Law: "Tribute 1 monster; Special Summon 1 monster with a different original Attribute from that monster's from your Deck."</summary>
    [EngineFact]
    public void DifferentOriginalAttributeFromTheTributedMonster()
    {
        var p = Monster(new EffectEvent
        {
            When = B("when.ignition"),
            Cost = [B("cost.tribute").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true })],
            Do = [B("do.special_summon").With("cards", Mine(Zone.Deck).With(Ref("ref_orig_attribute", CardRef.Cost, not: true)))],
        });
        Assert.Equal("You can Tribute 1 other monster you control; Special Summon 1 monster with a different original Attribute from that monster's from your Deck.", Text(p));
        Duel(p).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 3).Card(Fiend, 0, "deck").Activate(Id).ExpectActivations(1)
            .Check(In(0, "LOCATION_MZONE", Fiend)).Check(In(0, "LOCATION_GRAVE", Warrior)).AssertPasses();
    }

    // ---------------------------------------------------------------- the monster this card battled

    /// <summary>Buster Blader, the One Who Surpasses the Limits: "When this card destroys a monster by battle: You can discard 1 card; Special Summon that monster to your field."</summary>
    private static CardProject Buster() => Monster(new EffectEvent
    {
        When = B("when.destroys_by_battle"),
        Cost = [B("cost.discard")],
        Do = [B("do.special_summon").With("cards", That(CardRef.BattleTarget, FilterCardType.Monster))],
    });

    [Fact]
    public void ThatMonsterToYourField_ReadsLikeBusterBlader()
    {
        Assert.Equal("If this card destroys an opponent's monster by battle: You can discard 1 card from your hand; Special Summon that monster to your field.", Text(Buster()));
        var lua = Compiles(Buster());
        // Related to the effect as it is activated, as Duel.SetTargetCard does in official scripts.
        Assert.Contains("e:GetHandler():GetBattleTarget():CreateEffectRelation(e)", lua);
    }

    [EngineFact]
    public void ThatMonsterToYourField()
    {
        Duel(Buster()).Card(Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "hand").Card(Warrior, 0, "deck", 4)
            .Attack(Id).Activate(Id).Turns(3).ExpectActivations(1)
            .Check(In(0, "LOCATION_MZONE", Fiend)).Check(In(0, "LOCATION_GRAVE", Warrior)).AssertPasses();
    }

    /// <summary>Infinitrack Mountain Smasher: "When this card destroys an opponent's monster by battle: You can attach that monster to this card as material."</summary>
    [EngineFact]
    public void AttachThatMonster()
    {
        var p = Monster(new EffectEvent { When = B("when.destroys_by_battle"), Do = [B("do.attach").With("cards", That(CardRef.BattleTarget))] });
        p.Card.Frame = MonsterFrame.Xyz;
        p.Card.Atk = 2400;
        p.Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2 };
        Assert.Equal("If this card destroys an opponent's monster by battle: You can attach that monster to this card as material.", Text(p).Split('\n')[^1]);
        Duel(p).Card(Id, 0, "mzone").Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 4)
            .Attack(Id).Activate(Id).Turns(3).ExpectActivations(1)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{Id}):GetOverlayCount()==1").AssertPasses();
    }

    // ---------------------------------------------------------------- Special Summon options

    /// <summary>Infinitrack Brutal Dozer: "Special Summon 1 monster from your Deck in Defense Position, but negate its effects."</summary>
    private static CardProject Negated() => Monster(Ignition(
        B("do.special_summon").With("position", "defense").With("negate", true).With("cards", Mine(Zone.Deck))));

    [Fact]
    public void ButNegateItsEffects_Reads()
    {
        Assert.Equal("You can Special Summon 1 monster from your Deck in Defense Position, but negate its effects.", Text(Negated()));
        var lua = Compiles(Negated());
        Assert.Contains("Duel.SpecialSummonStep(tc,v,tp,tp,nocheck,nolimit,pos)", lua);
        Assert.Contains("Duel.SpecialSummonComplete()", lua);
        // Targeted: "Special Summon it in Defense Position, but negate its effects".
        var t = Monster(Ignition(B("do.special_summon").With("target", true).With("position", "defense").With("negate", true).With("cards", Mine(Zone.Graveyard))));
        Assert.Equal("You can target 1 monster in your GY; Special Summon it in Defense Position, but negate its effects.", Text(t).Replace("from your GY", "in your GY"));
        var self = Monster(new EffectEvent { When = B("when.ignition").With("zone", "hand"), Do = [B("do.special_summon_self").With("negate", true)] });
        Assert.Equal("You can Special Summon this card from your hand, but negate its effects.", Text(self));
        Assert.Contains("s.spnegate(Group.FromCards(c),e,tp,SUMMON_VALUE_SELF,false,false,POS_FACEUP)", Compiles(self));
    }

    [EngineFact]
    public void ButNegateItsEffects()
    {
        Duel(Negated()).Card(Id, 0, "mzone").Card(EffectFiller, 0, "deck", 4).Activate(Id).ExpectActivations(1)
            .Check($"Duel.GetMatchingGroupCount(function(c) return c:IsCode({EffectFiller}) and c:IsDisabled() and c:IsDefensePos() end,0,LOCATION_MZONE,0,nil)==1").AssertPasses();
    }

    /// <summary>Night Train Blue Traveler: "target 1 other EARTH Machine monster in your GY; Special Summon both it and this card."</summary>
    private static CardProject NightTrain() => Monster(new EffectEvent
    {
        When = B("when.ignition").With("zone", "graveyard"),
        Do =
        [
            B(TargetCardsAction.BlockId).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, Attribute = CardAttribute.Earth, ExcludeThisCard = true }),
            B("do.special_summon_self").With("with", "target"),
        ],
    });

    [Fact]
    public void BothItAndThisCard_ReadsLikeNightTrain()
    {
        Assert.Equal("If this card is in your GY: You can target 1 other EARTH monster in your GY; Special Summon both it and this card.", Text(NightTrain()));
        var lua = Compiles(NightTrain());
        // Both, with a zone each, or neither; and not while only 1 monster can be Special Summoned at a time.
        Assert.Contains("Duel.GetLocationCount(tp,LOCATION_MZONE)>=2", lua);
        Assert.Contains("not Duel.IsPlayerAffectedByEffect(tp,59822133)", lua);
        // The same from "Special Summon monsters" on the target, with "and this card".
        var same = Monster(new EffectEvent
        {
            When = B("when.ignition").With("zone", "graveyard"),
            Do = [NightTrain().Effects[0].Do[0], B("do.special_summon").With("plus", "this").With("cards", That(CardRef.Target))],
        });
        Assert.Equal(Text(NightTrain()), Text(same));
    }

    [EngineFact]
    public void BothItAndThisCard()
    {
        new EngineScenario(NightTrain()).Card(Id, 0, "grave").Card(Warrior, 0, "grave").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(Id).ExpectActivations(1).Check(In(0, "LOCATION_MZONE", Id)).Check(In(0, "LOCATION_MZONE", Warrior)).AssertPasses();
    }

    /// <summary>Primrose the Twin Rikka Fairy: "Special Summon this card from your hand and 1 ... monster from your Deck, except "...", in Defense Position."</summary>
    private static CardProject Primrose() => Monster(new EffectEvent
    {
        When = B("when.ignition").With("zone", "hand"),
        Do = [B("do.special_summon_self").With("with", "pick").With("position", "defense").With("with_cards", Mine(Zone.Deck).Except())],
    });

    [Fact]
    public void ThisCardAndOneFromTheDeck_ReadsLikePrimrose()
    {
        Assert.Equal("You can Special Summon this card from your hand and 1 monster from your Deck, except \"Ref Tester\", in Defense Position.", Text(Primrose()));
        Compiles(Primrose());
    }

    [EngineFact]
    public void ThisCardAndOneFromTheDeck()
    {
        Duel(Primrose()).Card(Id, 0, "hand").Card(Warrior, 0, "deck", 4).Activate(Id).ExpectActivations(1)
            .Check($"Duel.GetMatchingGroupCount(Card.IsDefensePos,0,LOCATION_MZONE,0,nil)==2").Check(In(0, "LOCATION_MZONE", Id)).AssertPasses();
    }

    /// <summary>With 1 free zone, neither is Summoned: the effect can't be activated.</summary>
    [EngineFact]
    public void ThisCardAndOneFromTheDeck_NeedsTwoZones()
    {
        Duel(Primrose()).Card(Id, 0, "hand").Card(Warrior, 0, "mzone", 4).Card(Warrior, 0, "deck", 4).Activate(Id).ExpectActivations(0)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==4").AssertPasses();
    }

    // ---------------------------------------------------------------- reading official scripts

    /// <summary>Flying Pegasus Railroad Stampede's "target 1 ... in your GY; Special Summon it in Defense Position, but negate its effects".</summary>
    [EngineFact]
    public void Translates_ButNegateItsEffects()
    {
        var card = new CardData { Id = 100470050, Name = "Translated Pegasus", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000, Race = CardRace.Machine, Attribute = CardAttribute.Earth };
        var result = ScriptTranslator.Translate(File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, "c88875132.lua")), 88875132, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).First(e => e.Do.Any(b => b.BlockId == "do.special_summon"));
        var ss = effect.Do.Single(b => b.BlockId == "do.special_summon");
        Assert.Equal(("true", "true", "defense"), (ss.Values["negate"], ss.Values["target"], ss.Values["position"]));
        var p = new CardProject { Card = card, Effects = [effect] };
        Assert.Contains("Special Summon it in Defense Position, but negate its effects.", Text(p));
        Assert.Contains("Duel.SpecialSummonStep(", Compiles(p));
    }

    /// <summary>Older blocks keep their wording and script: "plus", "negate" and "with" default to nothing.</summary>
    [Fact]
    public void Defaults_ChangeNothing()
    {
        var p = Monster(Ignition(B("do.special_summon")));
        Assert.Equal("You can Special Summon 1 monster from your GY.", Text(p));
        var lua = Compiles(p);
        Assert.DoesNotContain("SpecialSummonStep", lua);
        Assert.DoesNotContain("59822133", lua);
        Assert.DoesNotContain("SetLabelObject", lua);
    }
}

internal static class CardRefTestFilters
{
    public static CardFilter With(this CardFilter f, CardRequirement r)
    {
        var copy = f.Clone();
        copy.Requirements.Add(r);
        return copy;
    }

    public static CardFilter Except(this CardFilter f)
    {
        var copy = f.Clone();
        copy.ExceptOwnName = true;
        return copy;
    }
}
