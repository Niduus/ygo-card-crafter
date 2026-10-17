using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Monster Cards in the Spell &amp; Trap Zone (Snake-Eye, Diabellze, Wedju Temple, the Crystal Beasts, Heart of Gaia) and
/// control (Mimighoul, Sun God Domination, Number F0: Utopic Future), played in the engine.
/// </summary>
public class MonsterCardTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Monster(long id, string name, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Effect, Level = 3, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    private static EffectEvent Ignition(string zone, params BlockInstance[] dos) =>
        new() { When = B("when.ignition", ("zone", zone)), Do = [.. dos] };

    private static EffectEvent Lasting(params BlockInstance[] passives) =>
        new() { When = B("when.while_face_up", ("zone", "monster_zone")), Do = [.. passives] };

    private static string Text(CardProject p, int i = 0) => EffectTextGenerator.Describe(p.Effects[i], p.Card);

    private static readonly CardFilter FaceUpMonsters = new() { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };

    [EngineFact]
    public void PlacesAMonsterInItsOwnersSpellTrapZone()
    {
        var place = B(PlaceAction.BlockId, ("where", "owner_szone"), ("as", "continuous_spell"), ("target", "true"));
        place.Filters["cards"] = FaceUpMonsters.Clone();
        place.Filters["cards"].ExcludeThisCard = true;
        var p = Monster(100470001, "Spoils Placer", Ignition("monster_zone", place));
        Assert.Equal("You can target 1 other face-up monster on the field; place it face-up in its owner's Spell & Trap Zone as a Continuous Spell.", Text(p));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id)
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,1,LOCATION_SZONE,0,nil,1001)==1")
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_SZONE,0,nil,1001):IsType(TYPE_CONTINUOUS)")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_SZONE,0)==0").AssertPasses();
    }

    [EngineFact]
    public void PlacesAnyNumberOfMonsters_InTheirOwnersZones()
    {
        var place = B(PlaceAction.BlockId, ("where", "owner_szone"), ("as", "continuous_spell"), ("amount", "any"));
        place.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true };
        var p = Monster(100470002, "Heiress Placer", Ignition("monster_zone", place));
        Assert.Equal("You can place any number of other monsters you control face-up in their owners' Spell & Trap Zones as Continuous Spells.", Text(p));
        // The automatic player picks as few as it may: 1.
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone", 2).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id)
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_SZONE,0,nil,1001)==1")
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_MZONE,0,nil,1001)==1").AssertPasses();
    }

    /// <summary>
    /// Deep Forest of the Winter Raven and Heart of Gaia: Monster Cards in the Spell &amp; Trap Zone count, with the Level
    /// they are printed with there.
    /// </summary>
    [EngineFact]
    public void MonsterCardsInTheSpellTrapZone_AreProtectedAndCounted()
    {
        var protect = B("passive.protect_cards", ("by", "effect"));
        protect.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Field, CardType = FilterCardType.Monster, MonsterCards = true, Race = CardRace.Warrior };
        var place = B(PlaceAction.BlockId, ("as", "continuous_spell"));
        place.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster };
        var level = new CardFilter
        {
            Owner = CardOwner.Either, Zones = Zone.SpellTrapZone, CardType = FilterCardType.Monster, MonsterCards = true, FaceUpOnly = true,
            Requirements = [new CardRequirement { Property = "card_level", Op = RequirementOp.GreaterOrEqual, Value = 4 }],
        };
        var draw = new EffectEvent { When = B("when.ignition", ("zone", "monster_zone")), If = [B("if.cards_exist").With("cards", level)], Do = [B("do.draw")] };
        var p = Monster(100470003, "Forest Keeper", Lasting(protect), Ignition("monster_zone", place), draw);
        Assert.Equal("Warrior Monster Cards you control cannot be destroyed by card effects.", Text(p));
        Assert.Equal("If there is a face-up Level 4 or higher Monster Card in a Spell & Trap Zone: You can draw 1 card.", Text(p, 2));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id, 2)
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,nil,1001):IsHasEffect(EFFECT_INDESTRUCTABLE_EFFECT)")
            // The Warrior went to the Spell & Trap Zone, then the draw: 1 card in hand.
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").AssertPasses();
    }

    private static EffectEvent DestroyOne(bool own) => Ignition("monster_zone",
        B("do.destroy").With("cards", new CardFilter { Owner = own ? CardOwner.You : CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true }));

    [EngineFact]
    public void ThisCardIsPlacedInsteadOfGoingToTheGy()
    {
        var crystal = Monster(100470004, "Crystal Shard", Lasting(B(PlaceInsteadPassive.BlockId)));
        Assert.Equal("If this card is destroyed while in the Monster Zone, you can place it face-up in your Spell & Trap Zone as a Continuous Spell, instead of sending it to the GY.", Text(crystal));
        var breaker = Monster(100470005, "Shard Breaker", DestroyOne(own: true));
        new EngineScenario(crystal, breaker).Card(breaker.Card.Id, 0, "mzone").Card(crystal.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(breaker.Card.Id).Activate(crystal.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,1,nil,{crystal.Card.Id})")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,nil,{crystal.Card.Id}):IsType(TYPE_SPELL+TYPE_CONTINUOUS)").AssertPasses();
    }

    [EngineFact]
    public void ChosenMonstersArePlacedInsteadOfGoingToTheGy()
    {
        var place = B(PlaceInsteadPassive.BlockId, ("by", "both"));
        place.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, Race = CardRace.Warrior };
        var temple = Monster(100470006, "Stone Temple", Lasting(place), DestroyOne(own: true));
        Assert.Equal("If a face-up Warrior monster(s) you control is destroyed by battle or card effect, you can place it face-up in your Spell & Trap Zone as a Continuous Spell, instead of sending it to the GY.", Text(temple));
        new EngineScenario(temple).Card(temple.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(temple.Card.Id).Activate(Warrior)
            .Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,1,nil,1001)")
            .Check("not Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,1001)").AssertPasses();
    }

    [EngineFact]
    public void SummonsThisCardToTheOpponentsFieldFaceDown()
    {
        var p = Monster(100470007, "Ghoul Gift", Ignition("hand", B("do.special_summon_self", ("side", "opponent"), ("position", "defense_facedown"))));
        Assert.Equal("You can Special Summon this card from your hand to your opponent's field in face-down Defense Position.", Text(p));
        new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,0,LOCATION_MZONE,nil,{p.Card.Id}):IsFacedown()")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,0,LOCATION_MZONE,nil,{p.Card.Id}):GetOwner()==0").AssertPasses();
    }

    [EngineFact]
    public void GivesControlOfThisCard_UnlessItsControlCannotSwitch()
    {
        var p = Monster(100470008, "Ghoul Guest", Ignition("monster_zone", B("do.this_card", ("how", "control"))));
        Assert.Equal("You can give control of this card to your opponent.", Text(p));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id).Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{p.Card.Id})").AssertPasses();
        // Heart of the Blue-Eyes: "Control of this card cannot switch."
        var fixedCard = Monster(100470009, "Loyal Heart", Lasting(B("passive.self_rule", ("rule", "control_fixed"))), Ignition("monster_zone", B("do.this_card", ("how", "control"))));
        Assert.Equal("Control of this card cannot switch.", Text(fixedCard));
        new EngineScenario(fixedCard).Card(fixedCard.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(fixedCard.Card.Id).Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{fixedCard.Card.Id})").AssertPasses();
    }

    [EngineFact]
    public void ChangesAFaceDownMonsterToFaceUp_InThePositionChosen()
    {
        var down = B("do.change_position", ("to", "facedown"), ("target", "false"));
        down.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };
        var up = B("do.change_position", ("to", "faceup_choice"), ("target", "false")).Linked(ActionLink.Then);
        up.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceDownOnly = true };
        var p = Monster(100470010, "Ghoul Peeker", Ignition("monster_zone", down, up));
        Assert.EndsWith("then change 1 face-down monster your opponent controls to face-up Attack or Defense Position.", Text(p));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id)
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,1001):IsPosition(POS_FACEUP_ATTACK)").AssertPasses();
    }

    [EngineFact]
    public void RevealsTwo_TheOpponentGetsOneFaceDown_YouAddTheOther()
    {
        var reveal = B(RevealPickAction.BlockId, ("count", "2"), ("how", "random"), ("then", "summon_opponent"), ("rest", "hand"), ("names", "true"));
        reveal.Filters["cards"] = new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Normal };
        var p = Monster(100470011, "Ghoul Maker", Ignition("monster_zone", reveal));
        Assert.Equal("You can reveal 2 Normal Monsters with different names from your Deck, your opponent randomly picks 1 for you to Special Summon to their field " +
                     "in face-down Defense Position, and you add the other to your hand.", Text(p));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 2).Card(Dragon, 0, "deck", 2).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1")
            .Check("Duel.GetMatchingGroup(Card.IsFacedown,1,LOCATION_MZONE,0,nil):GetCount()==1")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").AssertPasses();
    }

    [EngineFact]
    public void TakesControlOfAllTheOpponentsMonsters()
    {
        var take = B("do.take_control", ("mode", "all"), ("target", "false"), ("duration", "always"));
        take.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        var p = Monster(100470012, "Sun Ruler", Ignition("monster_zone", take));
        Assert.Equal("You can take control of all monsters your opponent controls.", Text(p));
        new EngineScenario(p).Card(p.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone", 2).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(p.Card.Id)
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_MZONE,0,nil,1001)==2").AssertPasses();
    }

    [Fact]
    public void TakesControlUntilTheEndOfTheBattlePhase()
    {
        var p = Monster(100470013, "Future Taker", Ignition("monster_zone", B("do.take_control", ("duration", "battle"))));
        Assert.Equal("You can target 1 face-up monster your opponent controls; take control of it until the end of the Battle Phase.", Text(p));
        Assert.Contains("Duel.GetControl(g,tp,PHASE_BATTLE,1)", ScriptCompiler.Compile(p).Lua.Replace("g1", "g").Replace("g2", "g"));
    }

    /// <summary>"Monster Cards" compile as official cards check them: what the card is printed as.</summary>
    [Fact]
    public void MonsterCards_AreWhatTheCardIsPrintedAs()
    {
        var f = new CardFilter { Owner = CardOwner.You, Zones = Zone.SpellTrapZone, CardType = FilterCardType.Monster, MonsterCards = true, FaceUpOnly = true };
        Assert.Contains("c:GetOriginalType()&TYPE_MONSTER~=0", FilterCompiler.Conditions(f));
        Assert.DoesNotContain("c:IsType(TYPE_MONSTER)", FilterCompiler.Conditions(f));
        Assert.Equal("face-up Monster Cards in your Spell & Trap Zone", FilterText.Describe(f, null, plural: true, preposition: "in"));
    }
}
