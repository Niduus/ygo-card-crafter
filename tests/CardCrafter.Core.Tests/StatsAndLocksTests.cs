using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Levels, ATK/DEF, Attributes and LP changed by effects (Salamangreat Bastella, Convex Knight, Infinitrack, Card Crawl,
/// Apollousa, Hyperinvoked Aeon, Exodia Incarnate, Galaxy Tomahawk), locks and rules (Droll &amp; Lock Bird, Yummy,
/// Lethargisaurus, Herald of the Arc Light), immunities and the Extra Deck's wording.
/// </summary>
public class StatsAndLocksTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Monster(long id, string name, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Effect, Level = 3, Atk = 2000, Def = 1600 },
        Effects = [.. effects],
    };

    private static EffectEvent Ignition(string zone, params BlockInstance[] dos) =>
        new() { When = B("when.ignition", ("zone", zone)), Do = [.. dos] };

    private static EffectEvent Lasting(params BlockInstance[] passives) =>
        new() { When = B("when.while_face_up", ("zone", "monster_zone")), Do = [.. passives] };

    private static string Text(CardProject p, int i = 0) => EffectTextGenerator.Describe(p.Effects[i], p.Card);

    private static CardFilter Yours(bool other = false) => new() { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, ExcludeThisCard = other };
    private static CardFilter Theirs() => new() { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    private static EngineScenario Board(params CardProject[] cards) => new EngineScenario(cards).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4);

    // ---------------------------------------------------------------- Levels

    [EngineFact]
    public void IncreasesOrDecreasesALevel()
    {
        var p = Monster(100471001, "Level Tuner", Ignition("monster_zone", B("do.change_level", ("mode", "updown"), ("duration", "always")).With("cards", Yours(other: true))));
        Assert.Equal("You can target 1 other face-up monster you control; increase or decrease its Level by 1.", Text(p));
        // The automatic player declares the first Level offered: 4 + 1.
        Board(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Activate(p.Card.Id)
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,1001):GetLevel()==5").AssertPasses();
    }

    [EngineFact]
    public void TakesTheLevelOfAnotherMonster()
    {
        var change = B("do.change_level", ("mode", "of_card"), ("target", "false"), ("duration", "always"))
            .With("cards", new CardFilter { ThisCard = true })
            .With("source", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, Race = CardRace.Dragon });
        var p = Monster(100471002, "Convex Copier", Ignition("monster_zone", change));
        Assert.Equal("You can activate this effect; this card's Level becomes the Level/Rank of 1 face-up Dragon monster on the field.", Text(p));
        Board(p).Card(p.Card.Id, 0, "mzone").Card(Dragon, 1, "mzone").Activate(p.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{p.Card.Id}):GetLevel()==4").AssertPasses();
    }

    [EngineFact]
    public void OneOfTwoMonstersTakesTheOthersLevel()
    {
        var p = Monster(100471003, "Railroad Pegasus", Ignition("monster_zone", B("do.change_level", ("mode", "one_other"), ("duration", "always")).With("cards", Yours(other: true))));
        Assert.Equal("You can target 1 other face-up monster you control; the Level of that monster or this card becomes the Level of the other.", Text(p));
        Board(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Activate(p.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{p.Card.Id}):GetLevel()==Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,1001):GetLevel()")
            .AssertPasses();
    }

    [EngineFact]
    public void BothMonstersTakeTheirCombinedOriginalLevels()
    {
        var p = Monster(100471004, "Anchor Driller", Ignition("monster_zone", B("do.change_level", ("mode", "combined"), ("duration", "always")).With("cards", Yours(other: true))));
        Assert.Equal("You can target 1 other face-up monster you control; the Levels of both that monster and this card become the combined original Levels of those 2 monsters.", Text(p));
        var turn = Monster(100471005, "Anchor Driller II", Ignition("monster_zone", B("do.change_level", ("mode", "combined")).With("cards", Yours(other: true))));
        Assert.EndsWith("become the combined original Levels of those 2 monsters, until the end of this turn.", Text(turn));
        Board(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Activate(p.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{p.Card.Id}):GetLevel()==7")
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,1001):GetLevel()==7").AssertPasses();
    }

    // ---------------------------------------------------------------- ATK and DEF

    [EngineFact]
    public void AtkAndDefBecomeZero()
    {
        var p = Monster(100471006, "Card Crawler", Ignition("monster_zone", B("do.set_atk", ("stat", "both"), ("mode", "zero"), ("duration", "always")).With("cards", Theirs())));
        Assert.Equal("You can target 1 face-up monster your opponent controls; its ATK/DEF become 0.", Text(p));
        Board(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Activate(p.Card.Id)
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,1001):GetAttack()==0")
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,1001):GetDefense()==0").AssertPasses();
    }

    [EngineFact]
    public void AtkBecomesTheAtkOfAMonsterInTheGy()
    {
        var set = B("do.set_atk", ("mode", "of_card"), ("target", "false"), ("duration", "always")).With("cards", Theirs())
            .With("source", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster });
        var p = Monster(100471007, "Beast King", Ignition("monster_zone", set));
        Assert.Equal("You can activate this effect; the ATK of 1 face-up monster your opponent controls becomes the ATK of 1 monster from your GY.", Text(p));
        Board(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Dragon, 0, "grave").Activate(p.Card.Id)
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,1001):GetAttack()==1600").AssertPasses();
    }

    /// <summary>Apollousa: the original ATK is set, so a later loss still counts.</summary>
    [EngineFact]
    public void OriginalAtkBecomesAValue_AndLaterLossesStillApply()
    {
        var p = Monster(100471008, "Goddess Bow", Ignition("monster_zone",
            B("do.set_atk", ("stat", "orig_atk"), ("mode", "value"), ("amount", "2400"), ("target", "false"), ("duration", "always")).With("cards", new CardFilter { ThisCard = true }),
            B("do.change_self_stats", ("dir", "lose"), ("amount", "800"), ("duration", "always")).Linked(ActionLink.Then)));
        Assert.Equal("You can activate this effect; this card's original ATK becomes 2400, then this card loses 800 ATK.", Text(p));
        Board(p).Card(p.Card.Id, 0, "mzone").Activate(p.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{p.Card.Id}):GetBaseAttack()==2400")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{p.Card.Id}):GetAttack()==1600").AssertPasses();
    }

    [Fact]
    public void LiveOriginalAtk_IsTheBaseAttack()
    {
        var passive = B(StatsFromValuePassive.BlockId, ("dir", "becomes"), ("stat", "orig_atk"), ("how", "per"), ("amount", "800"), ("value", "its_used"))
            .With("cards", new CardFilter { ThisCard = true });
        var p = Monster(100471009, "Bow Goddess", Lasting(passive));
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("EFFECT_SET_BASE_ATTACK", lua);
        Assert.StartsWith("This card's original ATK becomes", Text(p));
    }

    [EngineFact]
    public void SpecialSummonsThisCard_WithItsOriginalAtkDefHalved()
    {
        var p = Monster(100471010, "Freight Crane", Ignition("hand", B("do.special_summon_self", ("halved", "true"))));
        Assert.Equal("You can Special Summon this card from your hand, but its original ATK/DEF become halved.", Text(p));
        Board(p).Card(p.Card.Id, 0, "hand").Activate(p.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{p.Card.Id}):GetBaseAttack()==1000")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{p.Card.Id}):GetDefense()==800").AssertPasses();
    }

    [EngineFact]
    public void GainLastsUntilTheEndOfTheOpponentsTurn()
    {
        var p = Monster(100471011, "Long Boost", Ignition("monster_zone", B("do.change_stats", ("dir", "gain"), ("amount", "500"), ("duration", "opp_turn"), ("target", "true")).With("cards", Yours())));
        Assert.Equal("You can target 1 face-up monster you control; it gains 500 ATK until the end of your opponent's turn.", Text(p));
        // Still there in the opponent's turn, gone in the turn after.
        Board(p).Card(p.Card.Id, 0, "mzone").Activate(p.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{p.Card.Id}):GetAttack()==2500").AssertPasses();
        Board(p).Card(p.Card.Id, 0, "mzone").Activate(p.Card.Id).Turns(2)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{p.Card.Id}):GetAttack()==2000").AssertPasses();
    }

    [Fact]
    public void NextTurnDuration_LastsTwoEndPhases()
    {
        var p = Monster(100471012, "Next Boost", Ignition("monster_zone", B("do.change_level", ("mode", "up"), ("duration", "next")).With("cards", Yours())));
        Assert.EndsWith("its Level increases by 1 until the end of the next turn.", Text(p));
        Assert.Contains("RESET_EVENT+RESETS_STANDARD+RESET_PHASE+PHASE_END,2", ScriptCompiler.Compile(p).Lua);
    }

    // ---------------------------------------------------------------- Attributes and LP

    [EngineFact]
    public void BecomesAnAttribute()
    {
        var p = Monster(100471013, "Verte Snake", Ignition("monster_zone", B(BecomeAction.BlockId, ("attribute", "Dark"), ("duration", "always")).With("cards", Theirs())));
        Assert.Equal("You can target 1 face-up monster your opponent controls; it becomes DARK.", Text(p));
        Board(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Activate(p.Card.Id)
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,1001):IsAttribute(ATTRIBUTE_DARK)").AssertPasses();
    }

    [EngineFact]
    public void AllOtherMonstersBecomeTheDeclaredAttribute()
    {
        var others = new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true };
        var p = Monster(100471014, "Aeon Caller", Ignition("monster_zone",
            B(DeclareAction.BlockId, ("what", "attribute")),
            B(BecomeAction.BlockId, ("what", "declared_attribute"), ("mode", "all"), ("target", "false")).With("cards", others)));
        Assert.Equal("You can declare 1 Attribute; all other monsters on the field become that Attribute until the end of this turn.", Text(p));
        // The duel stops at the next turn's first prompt, after the change ended: it is checked as it applies.
        var always = Monster(100471015, "Aeon Keeper", Ignition("monster_zone",
            B(DeclareAction.BlockId, ("what", "attribute")),
            B(BecomeAction.BlockId, ("what", "declared_attribute"), ("mode", "all"), ("target", "false"), ("duration", "always")).With("cards", others)));
        always.Card.Attribute = CardAttribute.Light;
        Board(always).Card(always.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Declare(0x20).Activate(always.Card.Id)
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,1001):IsAttribute(ATTRIBUTE_DARK)")
            .Check($"not Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{always.Card.Id}):IsAttribute(ATTRIBUTE_DARK)").AssertPasses();
    }

    [EngineFact]
    public void LosesLp()
    {
        var p = Monster(100471016, "Incarnate", Ignition("monster_zone", B(SetLifeAction.BlockId, ("who", "you"), ("how", "lose"), ("amount", "1000"))));
        Assert.Equal("You can lose 1000 LP.", Text(p));
        Board(p).Card(p.Card.Id, 0, "mzone").Activate(p.Card.Id).Check("Duel.GetLP(0)==7000").AssertPasses();
    }

    [EngineFact]
    public void SummonsAsManyTokensAsPossible()
    {
        var token = new CardProject { Card = new CardData { Id = 100471017, Name = "Battle Eagle Token", Frame = MonsterFrame.Token, Level = 6, Race = CardRace.Machine, Attribute = CardAttribute.Wind, Atk = 2000, Def = 0 } };
        var p = Monster(100471018, "Tomahawk", Ignition("monster_zone", B("do.tokens", ("max", "true"), ("token", "100471017")), B(TurnPlayerRuleAction.BlockId, ("rule", "opp_no_battle"))));
        var pack = new CardPack { Cards = [p, token] };
        Assert.Equal("You can Special Summon as many \"Battle Eagle Tokens\" (Machine/WIND/Level 6/ATK 2000/DEF 0) as possible, and for the rest of this turn, your opponent takes no battle damage.",
            EffectTextGenerator.Describe(p.Effects[0], p.Card, pack));
        Board(p, token).Card(p.Card.Id, 0, "mzone").Activate(p.Card.Id)
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_MZONE,0,nil,100471017)==4").AssertPasses();
    }

    // ---------------------------------------------------------------- locks and rules

    [EngineFact]
    public void NothingIsAddedFromTheMainDeck_DrawsIncluded()
    {
        var p = Monster(100471019, "Lock Bird", Ignition("monster_zone",
            B("do.summon_restriction", ("who", "both"), ("what", "search_all")),
            B("do.draw").Linked(ActionLink.AlsoAfterThat)));
        Assert.StartsWith("You can activate this effect; for the rest of this turn, cards cannot be added from either player's Main Deck to the hand".Replace("You can activate this effect; for", "For"), SentenceCase.Upper(Text(p).Replace("You can ", "")));
        Board(p).Card(p.Card.Id, 0, "mzone").Activate(p.Card.Id).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
    }

    [Fact]
    public void LinkSummonLock_NamesTheLinkMonsters()
    {
        var links = new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Link, Requirements = [new CardRequirement { Property = "link", Op = RequirementOp.GreaterOrEqual, Value = 3 }] };
        var p = Monster(100471020, "Snatcher", Ignition("monster_zone", B("do.summon_restriction", ("who", "you"), ("what", "link_of")).With("cards", links)));
        Assert.Contains("for the rest of this turn, you cannot Link Summon Link Monsters whose Link Rating is 3 or higher", Text(p));
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("SUMMON_TYPE_LINK", lua);
        var normal = Monster(100471021, "Dungeon", Lasting(B("passive.lock", ("who", "both"), ("what", "normal_only"))));
        Assert.Equal("Neither player can Normal Summon monsters (Setting them is still allowed).".Replace(" (Setting them is still allowed)", ""), Text(normal).Replace(" (Setting them is still allowed)", ""));
        Assert.DoesNotContain("EFFECT_CANNOT_MSET", ScriptCompiler.Compile(normal).Lua);
    }

    [Fact]
    public void BattleRules_CannotAttackDirectly_AndOpponentsEffects()
    {
        var p = Monster(100471022, "Little Knight", Ignition("monster_zone", B(TurnFieldAction.BlockId, ("rule", "cannot_direct"))));
        Assert.Contains("for the rest of this turn, monsters you control cannot attack directly.", Text(p));
        Assert.Contains("EFFECT_CANNOT_DIRECT_ATTACK", ScriptCompiler.Compile(p).Lua);
        var keep = Monster(100471023, "Almiraj", Ignition("monster_zone", B("do.battle_rule", ("rule", "cannot_be_destroyed_opp")).With("cards", Yours())));
        Assert.Equal("You can target 1 face-up monster you control; it cannot be destroyed by your opponent's card effects this turn.", Text(keep));
        Assert.Contains("aux.indoval", ScriptCompiler.Compile(keep).Lua);
    }

    [EngineFact]
    public void FaceUpMonstersAreChangedToDefensePosition()
    {
        var p = Monster(100471024, "Lethargic", Lasting(B("passive.cards_rule", ("rule", "to_defense")).With("cards",
            new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true })));
        Assert.Equal("Face-up monsters on the field are changed to Defense Position.", Text(p));
        Board(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone")
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,1001):IsPosition(POS_FACEUP_DEFENSE)").AssertPasses();
    }

    [Fact]
    public void NeitherPlayerRespondsToTheirOwnActivations()
    {
        var p = Monster(100471025, "Lethargic Rule", Lasting(B("passive.player_rule", ("rule", "no_own_response"))));
        Assert.Equal("Neither player can activate cards or effects in response to the activation of their own cards or effects.", Text(p));
        Assert.Contains("Duel.SetChainLimit(function(ce,cp,ctp) return cp~=rp end)", ScriptCompiler.Compile(p).Lua);
    }

    [EngineFact]
    public void MonstersSentFromTheHand_AreBanishedInstead()
    {
        var p = Monster(100471026, "Arc Herald", Lasting(B("passive.banish_instead", ("what", "monster"), ("from", "hand_deck"))),
            Ignition("monster_zone", B("do.discard", ("who", "you"))));
        Assert.Equal("Any monster sent from the hand or Main Deck to the GY is banished instead.", Text(p));
        Board(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "hand").Activate(p.Card.Id)
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_REMOVED,0,1,nil,1001)").AssertPasses();
    }

    // ---------------------------------------------------------------- immunity and wording

    [Fact]
    public void Immunities()
    {
        string Of(BlockInstance b) => Text(Monster(100471027, "Immune", Lasting(b)));
        Assert.Equal("Unaffected by the activated effects of monsters with ATK less than this card.", Of(B("passive.immune", ("whose", "less_atk"))));
        Assert.Equal("Unaffected by the activated effects of other Link Monsters.", Of(B("passive.immune", ("whose", "other_link"))));
        var patron = B("passive.immune", ("whose", "except_cards")).With("except", new CardFilter { Zones = Zone.None, Archetype = 0x1aa });
        Assert.Matches("^Unaffected by card effects, except \"[^\"]+\" cards'\\.$", Of(patron));
        Assert.Contains("not ", ScriptCompiler.Compile(Monster(100471028, "Patron", Lasting(patron))).Lua);
        var shrine = B("passive.immune", ("whose", "opp_activated_attr"), ("attribute", "Fire"))
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Pyro });
        Assert.Equal("Pyro monsters you control are unaffected by your opponent's activated FIRE monsters' effects.", Of(shrine));
        var lua = ScriptCompiler.Compile(Monster(100471029, "Shrine", Lasting(shrine))).Lua;
        Assert.Contains("EFFECT_TYPE_FIELD", lua);
        Assert.Contains("IsAttribute(ATTRIBUTE_FIRE)", lua);
    }

    [EngineFact]
    public void UnaffectedByWeakerMonstersActivatedEffects()
    {
        var strong = Monster(100471030, "Vagrada", Lasting(B("passive.immune", ("whose", "less_atk"))));
        var weak = Monster(100471031, "Weak Breaker", Ignition("monster_zone", B("do.destroy", ("target", "false"))
            .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, CardCode = 100471030 })));
        weak.Card.Atk = 1000;
        new EngineScenario(strong, weak).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4).Card(strong.Card.Id, 1, "mzone").Card(weak.Card.Id, 0, "mzone")
            .Activate(weak.Card.Id).Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{strong.Card.Id})").AssertPasses();
    }

    [Fact]
    public void ExtraDeckMonsters_ReturnToTheExtraDeck()
    {
        var fusion = new CardProject
        {
            Card = new CardData { Id = 100471032, Name = "Cubic Lord", Frame = MonsterFrame.Fusion, Level = 8 },
            Effects = [new EffectEvent { When = B("when.quick"), Do = [B("do.this_card", ("how", "deck"))] }],
        };
        Assert.Equal("(Quick Effect): You can return this card to the Extra Deck.", Text(fusion));
        var all = B("do.shuffle_into_deck", ("mode", "all")).With("cards", new CardFilter
        {
            Owner = CardOwner.Opponent, Zones = Zone.MonsterZone | Zone.Graveyard, CardType = FilterCardType.Monster,
            Requirements = [new CardRequirement { Property = "monster_type", Op = RequirementOp.Is, Values = [0x40, 0x2000, 0x800000, 0x4000000] }],
        });
        var magistus = Monster(100471033, "Magistus", Ignition("monster_zone", all));
        Assert.Equal("You can return all Fusion, Synchro, Xyz, or Link Monsters from your opponent's GY or field to the Extra Deck.", Text(magistus));
        // A Main Deck card still shuffles into the Deck.
        Assert.Equal("You can shuffle this card into the Deck.", Text(Monster(100471034, "Main", Ignition("monster_zone", B("do.this_card", ("how", "deck"))))));
    }
}
