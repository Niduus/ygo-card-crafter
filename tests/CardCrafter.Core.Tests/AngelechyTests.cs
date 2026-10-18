using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The Angelechy pieces: Synchro Monsters placed in the Spell &amp; Trap Zone as Continuous Spells (their own trigger, and
/// what they do while treated as Continuous Spells), columns, control given to the opponent, Summons to the opponent's
/// field or the Extra Monster Zone or by each player, and options chosen by how many cards there are. Played in the engine.
/// </summary>
public class AngelechyTests
{
    private const int Angelechy = 0x1e2;
    private static readonly CardPack Pack = new() { Archetypes = [new Archetype { Code = Angelechy, Name = "Angelechy" }] };

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Synchro(long id, string name, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Synchro, Level = 7, Atk = 2500, Def = 2000, Race = CardRace.Fairy, Attribute = CardAttribute.Light, Archetypes = [Angelechy] },
        Effects = [.. effects],
    };

    private static CardProject Monster(long id, string name, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Effect, Level = 4, Atk = 1500, Def = 1000, Race = CardRace.Fairy, Attribute = CardAttribute.Light, Archetypes = [Angelechy] },
        Effects = [.. effects],
    };

    private static CardProject Spell(long id, string name, SpellKind kind, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Kind = CardKind.Spell, SpellKind = kind, Archetypes = [Angelechy] },
        Effects = [.. effects],
    };

    private static CardProject Trap(long id, string name, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Kind = CardKind.Trap, Archetypes = [Angelechy] },
        Effects = [.. effects],
    };

    private static EffectEvent Ignition(params BlockInstance[] dos) => new() { When = B("when.ignition", ("zone", "monster_zone")), Do = [.. dos] };
    private static EffectEvent Activate(params BlockInstance[] dos) => new() { When = B("when.activate"), Do = [.. dos] };

    private static string Text(CardProject p, int i = 0) => EffectTextGenerator.Describe(p.Effects[i], p.Card, Pack);

    private static CardFilter AngelechyIn(Zone zones, bool monsterCards = false) =>
        new() { Owner = CardOwner.You, Zones = zones, CardType = FilterCardType.Monster, MonsterCards = monsterCards, Archetype = Angelechy };

    /// <summary>"Place 1 "Angelechy" monster (that one, by passcode) from your Extra Deck in your Spell &amp; Trap Zone as a face-up Continuous Spell."</summary>
    private static BlockInstance PlaceFromExtra(long code = 0)
    {
        var f = AngelechyIn(Zone.ExtraDeck);
        f.CardCode = code;
        return B(PlaceAction.BlockId, ("as", "continuous_spell")).With("cards", f);
    }

    private static EffectEvent PlacedTrigger(params BlockInstance[] dos) =>
        new() { When = B(ThisCardEventTrigger.BlockId, ("event", ThisCardEventTrigger.PlacedAsSpellKey)), Do = [.. dos] };

    private static EffectEvent WhileSpell(params BlockInstance[] passives) => new() { When = B("when.while_face_up", ("as", "spell")), Do = [.. passives] };

    // ---------------------------------------------------------------- placed as a Continuous Spell

    [Fact]
    public void PlacedAsAContinuousSpell_ReadsAndCompilesAsTheOfficialScripts()
    {
        var p = Synchro(100510001, "Angel Rook", PlacedTrigger(B("do.draw")));
        Assert.Equal("If this card is placed in the Spell & Trap Zone as a Continuous Spell: You can draw 1 card.", Text(p));
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("SetCode(EVENT_MOVE)", lua);
        Assert.Contains("SetCode(EVENT_CHAIN_SOLVED)", lua);
        Assert.Contains("SetCode(EVENT_CUSTOM+id+8480)", lua);
        Assert.Contains("e:GetHandler():GetType()==TYPE_SPELL+TYPE_CONTINUOUS", lua);
        // Only monsters are placed there as Continuous Spells.
        var spell = Spell(100510002, "Not A Monster", SpellKind.Continuous, PlacedTrigger(B("do.draw")));
        Assert.Contains(ScriptCompiler.Compile(spell).Diagnostics, d => d.Severity == Severity.Error);
    }

    [EngineFact]
    public void PlacedAsAContinuousSpell_ByAChainLink_TriggersAfterIt()
    {
        var rook = Synchro(100510003, "Angel Rook", PlacedTrigger(B("do.draw")));
        var placer = Spell(100510004, "Angel Opening", SpellKind.Normal, Activate(PlaceFromExtra()));
        new EngineScenario(rook, placer).Card(rook.Card.Id, 0, "extra").Card(placer.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(placer.Card.Id).Activate(rook.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,nil,{rook.Card.Id}):GetType()==TYPE_SPELL+TYPE_CONTINUOUS")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .ExpectActivations(2).AssertPasses();
    }

    [EngineFact]
    public void PlacedOutsideTheSpellTrapZoneOrAsATrap_DoesNotTrigger()
    {
        // Summoned to the Monster Zone instead: no trigger.
        var rook = Synchro(100510005, "Angel Rook", PlacedTrigger(B("do.draw")));
        var summoner = Spell(100510006, "Angel Call", SpellKind.Normal, Activate(B("do.special_summon", ("ignore", "true")).With("cards", AngelechyIn(Zone.ExtraDeck))));
        new EngineScenario(rook, summoner).Card(rook.Card.Id, 0, "extra").Card(summoner.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(summoner.Card.Id).Activate(rook.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{rook.Card.Id})")
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0")
            .ExpectActivations(1).AssertPasses();
    }

    [EngineFact]
    public void WhileTreatedAsAContinuousSpell_ItProtectsTheOthers()
    {
        // Angelechy Bastion: "While this card is treated as a Continuous Spell, other "Angelechy" cards on the field cannot be
        // destroyed by your opponent's card effects."
        var protect = B("passive.protect_cards", ("by", "opponent_effect"))
            .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field, Archetype = Angelechy, ExcludeThisCard = true });
        var bastion = Synchro(100510007, "Angel Bastion", WhileSpell(protect));
        Assert.Equal("While this card is treated as a Continuous Spell, other \"Angelechy\" cards on the field cannot be destroyed by your opponent's card effects.", Text(bastion));
        var placer = Spell(100510008, "Angel Opening", SpellKind.Normal, Activate(PlaceFromExtra()));
        var pawn = Monster(100510009, "Angel Pawn");
        new EngineScenario(bastion, placer, pawn).Card(bastion.Card.Id, 0, "extra").Card(placer.Card.Id, 0, "hand").Card(pawn.Card.Id, 0, "mzone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(placer.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{pawn.Card.Id}):IsHasEffect(EFFECT_INDESTRUCTABLE_EFFECT)")
            .Check($"not Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_SZONE,0,nil,{bastion.Card.Id}):IsHasEffect(EFFECT_INDESTRUCTABLE_EFFECT)").AssertPasses();
        // As a monster, it protects nothing.
        new EngineScenario(bastion, pawn).Card(bastion.Card.Id, 0, "mzone").Card(pawn.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Check($"not Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{pawn.Card.Id}):IsHasEffect(EFFECT_INDESTRUCTABLE_EFFECT)").AssertPasses();
    }

    // ---------------------------------------------------------------- one card or effect per Chain, N monster effects per turn

    [EngineFact]
    public void EachPlayerCanOnlyActivateOneCardOrEffectPerChain()
    {
        // Angelechy Castellan.
        var castellan = Synchro(100510010, "Angel Castellan", WhileSpell(B("passive.player_rule", ("rule", "one_per_chain"))));
        Assert.Equal("While this card is treated as a Continuous Spell, each player can only activate 1 card or effect per Chain.", Text(castellan));
        var placer = Monster(100510011, "Angel Placer", Ignition(PlaceFromExtra(100510010)));
        var draw = Spell(100510012, "Angel Draw", SpellKind.Normal, Activate(B("do.draw")));
        // It can only respond to a Spell Card: in the same Chain as the Spell, which is the player's first activation there.
        var responder = Monster(100510013, "Angel Answer", new EffectEvent { When = B("when.quick", ("timing", "chain_spell_any"), ("zone", "monster_zone")), Do = [B("do.draw")] });
        new EngineScenario(castellan, placer, draw, responder).Card(castellan.Card.Id, 0, "extra").Card(placer.Card.Id, 0, "mzone").Card(responder.Card.Id, 0, "mzone")
            .Card(draw.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 6).Card(Warrior, 1, "deck", 4)
            .Activate(placer.Card.Id).Activate(draw.Card.Id).Activate(responder.Card.Id)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .ExpectActivations(2).AssertPasses();
        // Without it, the answer chains to the Spell.
        new EngineScenario(draw, responder).Card(responder.Card.Id, 0, "mzone").Card(draw.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 6).Card(Warrior, 1, "deck", 4)
            .Activate(draw.Card.Id).Activate(responder.Card.Id)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2")
            .ExpectActivations(2).AssertPasses();
    }

    [Fact]
    public void MonsterEffectsPerTurn_SayWhoseAndHowMany()
    {
        var shatranga = Synchro(100510014, "Angel Shatranga", WhileSpell(B("passive.player_rule", ("rule", "one_monster_effect"), ("whose", "opponent"), ("count", "5"))));
        Assert.Equal("While this card is treated as a Continuous Spell, your opponent can only attempt to activate up to 5 monster effects per turn.", Text(shatranga));
        var lua = ScriptCompiler.Compile(shatranga).Lua;
        Assert.Contains("GetFlagEffect(id+8440)>=5", lua);
        Assert.DoesNotContain("id+8420", lua);
        // Saved before: each player, 1 monster effect.
        var caliga = Monster(100510015, "Old Caliga", new EffectEvent { When = B("when.while_face_up", ("zone", "monster_zone")), Do = [B("passive.player_rule", ("rule", "one_monster_effect"))] });
        Assert.Equal("Each player can only attempt to activate 1 monster effect per turn.", Text(caliga));
        var old = ScriptCompiler.Compile(caliga).Lua;
        Assert.Contains("GetFlagEffect(id+8420)~=0", old);
        Assert.Contains("GetFlagEffect(id+8440)~=0", old);
    }

    [EngineFact]
    public void YouCanOnlyActivateUpToTwoMonsterEffectsPerTurn()
    {
        var rule = Monster(100510016, "Angel Rule", new EffectEvent { When = B("when.while_face_up", ("zone", "monster_zone")), Do = [B("passive.player_rule", ("rule", "one_monster_effect"), ("whose", "you"), ("count", "2"))] });
        var drawer = Monster(100510017, "Angel Drawer", new EffectEvent { When = B("when.ignition", ("zone", "monster_zone")), Limit = OncePerTurn.Soft, Do = [B("do.draw")] });
        new EngineScenario(rule, drawer).Card(rule.Card.Id, 0, "mzone").Card(drawer.Card.Id, 0, "mzone", 3).Card(Warrior, 0, "deck", 6).Card(Warrior, 1, "deck", 4)
            .Activate(drawer.Card.Id, 3)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2")
            .ExpectActivations(2).AssertPasses();
    }

    // ---------------------------------------------------------------- a Trap the turn it was Set

    [EngineFact]
    public void OncePerTurn_OneTrapCanBeActivatedTheTurnItWasSet()
    {
        // Angelechy Endgame Problem: "Once per turn, you can activate 1 "Angelechy" Trap Card the turn it was Set."
        var traps = B(ActivateSetTurnPassive.BlockId, ("which", "cards"))
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.SpellTrapZone, CardType = FilterCardType.Trap, Archetype = Angelechy });
        var problem = Spell(100510018, "Angel Problem", SpellKind.Continuous,
            new EffectEvent { When = B("when.while_face_up", ("zone", "spell_trap_zone")), Limit = OncePerTurn.Soft, Do = [traps] });
        Assert.Equal("Once per turn, you can activate 1 \"Angelechy\" Trap Card the turn it was Set.", Text(problem));
        var trap = Trap(100510019, "Angel Gambit", Activate(B("do.draw")));
        var setter = Monster(100510020, "Angel Setter", Ignition(B("do.set", ("count", "2"))
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Deck, CardType = FilterCardType.Trap, Archetype = Angelechy })));
        new EngineScenario(problem, trap, setter).Card(problem.Card.Id, 0, "hand").Card(setter.Card.Id, 0, "mzone").Card(trap.Card.Id, 0, "deck", 2)
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(problem.Card.Id).Activate(setter.Card.Id).Activate(trap.Card.Id, 2)
            .Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_GRAVE,0,nil,{trap.Card.Id})==1")
            .Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_SZONE,0,nil,{trap.Card.Id})==1").AssertPasses();
    }

    // ---------------------------------------------------------------- columns

    private static CardRequirement Columns(RequirementOp op, int n) => new() { Property = CardProperties.ColumnsAway, Op = op, Value = n };

    [Fact]
    public void ColumnRequirements_ReadAsTheCardsSayThem()
    {
        var destrier = Synchro(100510021, "Angel Destrier", Ignition(B("do.banish", ("target", "true"))
            .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Requirements = [Columns(RequirementOp.NotEqual, 0)] })));
        Assert.Equal("You can target 1 monster in another column; banish it.", Text(destrier));
        var adjacent = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Requirements = [Columns(RequirementOp.Equal, 1)] };
        var castellan = Synchro(100510022, "Angel Castellan", Ignition(B("do.banish", ("target", "true")).With("cards", adjacent)));
        Assert.Equal("You can target 1 monster your opponent controls in this card's adjacent column; banish it.", Text(castellan));
        var away = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.SpellTrapZone, Requirements = [Columns(RequirementOp.Equal, 2)] };
        Assert.Equal("Spell/Trap your opponent controls that is 2 columns away from this card's", FilterText.Describe(away, Pack));
        // Angelechy Castellan: either kind, each where it is.
        var either = adjacent.Clone();
        either.Zones = Zone.Field;
        either.OrCards = new CardFilter { Requirements = [new CardRequirement { Property = "location", Op = RequirementOp.Is, Values = [0x8] }, Columns(RequirementOp.Equal, 2)] };
        Assert.Equal("monster your opponent controls in this card's adjacent column, or 1 Spell/Trap your opponent controls that is 2 columns away from this card's", FilterText.Describe(either, Pack));
        var lua = ScriptCompiler.Compile(destrier).Lua;
        Assert.Contains("(aux.GetColumn(c)>=0 and aux.GetColumn(e:GetHandler())>=0 and (math.abs(aux.GetColumn(c)-aux.GetColumn(e:GetHandler())))~=0)", lua);
        // Older script sets get the helper.
        Assert.Contains("if not aux.GetColumn then", lua);
    }

    [EngineFact]
    public void InAnotherColumn_LeavesOutTheMonstersInThisCardsColumn()
    {
        var any = new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Requirements = [Columns(RequirementOp.NotEqual, 0)] };
        var destrier = Synchro(100510023, "Angel Destrier", Ignition(B("do.banish", ("target", "true")).With("cards", any)));
        // This card in your leftmost zone, the opponent's monster in their rightmost: the same column.
        new EngineScenario(destrier).Card(destrier.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Setup("Duel.MoveSequence(Duel.GetFieldCard(1,LOCATION_MZONE,0),4)")
            .Activate(destrier.Card.Id)
            .Check("Duel.GetFieldCard(1,LOCATION_MZONE,4)~=nil")
            .ExpectActivations(0).AssertPasses();
        // In the opponent's leftmost zone: another column.
        new EngineScenario(destrier).Card(destrier.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(destrier.Card.Id)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0")
            .ExpectActivations(1).AssertPasses();
    }

    [EngineFact]
    public void InTheSameColumnAsThePickedCards()
    {
        // Junora the Power Patron of Tuning: "monsters your opponent controls in the same column as the "Elfnote" monster(s) you
        // control cannot be used as material for a Fusion, Synchro, Xyz, or Link Summon, until the end of this turn".
        var same = new CardRequirement
        {
            Property = CardProperties.SameColumn, Op = RequirementOp.Is,
            Cards = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Archetype = Angelechy },
        };
        var theirs = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Requirements = [same] };
        // While face-up, so it is still there when the turn is over.
        var apply = B("do.apply_effects", ("mode", "all"), ("duration", "always"), ("negate", "false"), ("no_attack", "false"), ("indestructible", "false"), ("unaffected", "false"), ("no_tribute", "false"))
            .With("cards", theirs);
        var junora = Monster(100510024, "Angel Junora", Ignition(apply));
        Assert.Equal("monsters your opponent controls in the same column as the \"Angelechy\" monster(s) you control", FilterText.Describe(theirs, Pack, plural: true));
        // This card in your leftmost zone; the opponent's monsters in their rightmost (the same column) and the next one.
        new EngineScenario(junora).Card(junora.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone", 2).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Setup("Duel.MoveSequence(Duel.GetFieldCard(1,LOCATION_MZONE,0),4)")
            .Activate(junora.Card.Id)
            .Check("Duel.GetFieldCard(1,LOCATION_MZONE,4):IsHasEffect(EFFECT_CANNOT_BE_SYNCHRO_MATERIAL)")
            .Check("not Duel.GetFieldCard(1,LOCATION_MZONE,1):IsHasEffect(EFFECT_CANNOT_BE_SYNCHRO_MATERIAL)").AssertPasses();
    }

    // ---------------------------------------------------------------- control

    [EngineFact]
    public void GivesControlOfAMonster_ThenNegatesTheCardsBesideIt()
    {
        // Angelechy Disturbance: "Give control of 1 "Angelechy" monster you control to your opponent (until the End Phase), then
        // ... face-up card(s) in its adjacent Monster Zones and/or Spell & Trap Zones, their effects are negated."
        var give = B("do.take_control", ("to", "opponent"), ("target", "false"))
            .With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Archetype = Angelechy });
        var beside = new CardFilter
        {
            Owner = CardOwner.Opponent, Zones = Zone.Field, FaceUpOnly = true,
            Requirements = [new CardRequirement { Property = "ref_adjacent", Op = RequirementOp.Is, Values = [(long)CardRef.Affected] }],
        };
        var negate = B("do.negate_effects", ("mode", "all"), ("duration", "always")).With("cards", beside);
        negate.Link = ActionLink.Then;
        var disturbance = Spell(100510025, "Angel Disturbance", SpellKind.QuickPlay, Activate(give, negate));
        Assert.Equal("Give control of 1 \"Angelechy\" monster you control to your opponent (until the End Phase), then negate the effects of all face-up cards your opponent controls in its adjacent Monster Zones and/or Spell & Trap Zones.", Text(disturbance));
        var pawn = Monster(100510026, "Angel Pawn");
        // The opponent's monsters in their 2 leftmost zones: the pawn goes to the next one, beside the 2nd only.
        new EngineScenario(disturbance, pawn).Card(disturbance.Card.Id, 0, "hand").Card(pawn.Card.Id, 0, "mzone").Card(EffectFiller, 1, "mzone", 2)
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(disturbance.Card.Id).Turns(1)
            .Check("Duel.GetFieldCard(1,LOCATION_MZONE,1):IsDisabled()")
            .Check("not Duel.GetFieldCard(1,LOCATION_MZONE,0):IsDisabled()")
            // Back to you as the End Phase came.
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{pawn.Card.Id})").AssertPasses();
    }

    [EngineFact]
    public void GivesControlInTheAdjacentColumns_ThenTheOwnerSummonsAnother()
    {
        // Angelechy Enlisted: "target 1 opponent's monster in this card's adjacent column; banish it, then give control of this
        // card to your opponent by moving it to one of their Main Monster Zones in this card's adjacent columns. If the control
        // of this face-up card changes: Return this card to the Extra Deck, then the owner of this card Special Summons 1
        // "Angelechy" monster from their Extra Deck."
        var banish = B("do.banish", ("target", "true"))
            .With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Requirements = [Columns(RequirementOp.Equal, 1)] });
        var move = B("do.this_card", ("how", "control_adjacent"));
        move.Link = ActionLink.Then;
        var back = B("do.this_card", ("how", "deck"));
        // (The owner chooses which; the automatic player would take this card again, back in the Extra Deck.)
        var others = AngelechyIn(Zone.ExtraDeck);
        others.ExceptOwnName = true;
        var summon = B("do.special_summon", ("who", "owner"), ("ignore", "true")).With("cards", others);
        summon.Link = ActionLink.Then;
        var changed = new EffectEvent { When = B(ThisCardEventTrigger.BlockId, ("event", "control_changed"), ("optional", "false")), Do = [back, summon] };
        var enlisted = Synchro(100510027, "Angel Enlisted", Ignition(banish, move), changed);
        Assert.Equal("You can target 1 monster your opponent controls in this card's adjacent column; banish it, then give control of this card to your opponent by moving it to one of their Main Monster Zones in this card's adjacent columns.", Text(enlisted));
        Assert.Equal("If control of this card changes: Return this card to the Extra Deck, then the owner of this card Special Summons 1 \"Angelechy\" monster from their Extra Deck, except \"Angel Enlisted\", ignoring its Summoning conditions.", Text(enlisted, 1));
        var knight = Synchro(100510028, "Angel Knight");
        // This card in your leftmost zone (column 0): the opponent's 4th zone from their left is its adjacent column.
        new EngineScenario(enlisted, knight).Card(enlisted.Card.Id, 0, "mzone").Card(knight.Card.Id, 0, "extra").Card(Warrior, 1, "mzone", 4)
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(enlisted.Card.Id)
            .Check("Duel.GetFieldCard(1,LOCATION_MZONE,3)==nil")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_EXTRA,0,1,nil,{enlisted.Card.Id})")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{knight.Card.Id})").AssertPasses();
    }

    // ---------------------------------------------------------------- Special Summons

    [EngineFact]
    public void SummonsToTheOpponentsMainMonsterZone_WithADifferentNameFromTheSpellTrapZone()
    {
        // Angelechy Opposition: "... "Angelechy" monsters in your Extra Deck, with different names than the cards in your Spell &
        // Trap Zone, Special Summon 1 of them in your opponent's Main Monster Zone ...".
        var unlike = new CardRequirement { Property = "unlike_names", Op = RequirementOp.Is, Values = [0] };
        var pick = AngelechyIn(Zone.ExtraDeck);
        pick.Requirements = [unlike];
        var opposition = Spell(100510029, "Angel Opposition", SpellKind.Normal, Activate(B("do.special_summon", ("zone", "opp_main"), ("ignore", "true")).With("cards", pick)));
        Assert.Equal("Special Summon 1 \"Angelechy\" monster with a different name from the cards in your Spell & Trap Zone, from your Extra Deck in your opponent's Main Monster Zone, ignoring its Summoning conditions.", Text(opposition));
        var rook = Synchro(100510030, "Angel Rook");
        var knight = Synchro(100510031, "Angel Knight");
        var placer = Monster(100510032, "Angel Placer", Ignition(PlaceFromExtra(rook.Card.Id)));
        new EngineScenario(opposition, rook, knight, placer).Card(rook.Card.Id, 0, "extra", 2).Card(knight.Card.Id, 0, "extra").Card(placer.Card.Id, 0, "mzone")
            .Card(opposition.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(placer.Card.Id).Activate(opposition.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,0,LOCATION_MZONE,1,nil,{knight.Card.Id})")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,0,LOCATION_MZONE,nil,{knight.Card.Id}):GetSequence()<5")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,0,LOCATION_MZONE,nil,{knight.Card.Id}):GetOwner()==0")
            .Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_EXTRA,0,nil,{rook.Card.Id})==1").AssertPasses();
    }

    [EngineFact]
    public void SummonsToTheExtraMonsterZone()
    {
        var call = Monster(100510033, "Angel Call", Ignition(B("do.special_summon", ("zone", "extra_zone"), ("ignore", "true")).With("cards", AngelechyIn(Zone.ExtraDeck))));
        Assert.Equal("You can Special Summon 1 \"Angelechy\" monster from your Extra Deck to the Extra Monster Zone, ignoring its Summoning conditions.", Text(call));
        var rook = Synchro(100510034, "Angel Rook");
        new EngineScenario(call, rook).Card(call.Card.Id, 0, "mzone").Card(rook.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(call.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{rook.Card.Id}):GetSequence()>=5").AssertPasses();
    }

    [Fact]
    public void EachPlayerCanSummon_IsActivatedWhenMandatory()
    {
        var fairy = new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Synchro, Race = CardRace.Fairy };
        var each = B("do.special_summon", ("who", "each"), ("negate", "true")).With("cards", fairy);
        var seneschal = Synchro(100510051, "Angel Seneschal", new EffectEvent { When = B("when.summoned", ("how", "special"), ("optional", "false")), Do = [each] });
        Assert.Equal("If this card is Special Summoned: Activate this effect; each player can Special Summon 1 Fairy Synchro Monster from their Extra Deck (but negate its effects).", Text(seneschal));
        var lua = ScriptCompiler.Compile(seneschal).Lua;
        Assert.Contains("Duel.SelectYesNo(tp,aux.Stringid(id,0))", lua);
        Assert.Contains("Duel.SpecialSummonComplete()", lua);
    }

    [EngineFact]
    public void EachPlayerCanSummon_WithTheirMonstersAndThisCardBarredAndReturned()
    {
        // Angelechy Seneschal.
        var fairy = new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Synchro, Race = CardRace.Fairy };
        var each = B("do.special_summon", ("who", "each"), ("ignore", "true"), ("negate", "true"), ("no_xyz_link", "true"), ("return_extra", "true"), ("self_too", "true"))
            .With("cards", fairy);
        var seneschal = Synchro(100510035, "Angel Seneschal", Ignition(each));
        Assert.Equal("You can activate this effect; each player can Special Summon 1 Fairy Synchro Monster from their Extra Deck, ignoring its Summoning conditions (but negate its effects), "
                     + "also this card, and monsters Special Summoned by this effect, cannot be used as material for an Xyz or Link Summon, and return them to the Extra Deck when they leave the field.", Text(seneschal));
        var rook = Synchro(100510036, "Angel Rook");
        var knight = Synchro(100510037, "Angel Knight");
        new EngineScenario(seneschal, rook, knight).Card(seneschal.Card.Id, 0, "mzone").Card(rook.Card.Id, 0, "extra").Card(knight.Card.Id, 1, "extra")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(seneschal.Card.Id)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{rook.Card.Id}):IsDisabled()")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{knight.Card.Id}):IsDisabled()")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{knight.Card.Id}):IsHasEffect(EFFECT_CANNOT_BE_XYZ_MATERIAL)")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{rook.Card.Id}):IsHasEffect(EFFECT_LEAVE_FIELD_REDIRECT)")
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{seneschal.Card.Id}):IsHasEffect(EFFECT_CANNOT_BE_LINK_MATERIAL)")
            .Check($"not Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{seneschal.Card.Id}):IsDisabled()").AssertPasses();
    }

    [EngineFact]
    public void WhenAMonsterOfAnArchetypeTargetsThisCardForAnAttack()
    {
        // Angelechy Seneschal: "If an "Angelechy" monster targets this card for an attack: Negate that attack, and if you do,
        // give control of this card to your opponent."
        var when = B("when.attack_declared", ("mode", "trigger"), ("how", "targeted"), ("zone", "monster_zone"), ("optional", "false"))
            .With("attacker", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Archetype = Angelechy })
            .With("target", new CardFilter { ThisCard = true });
        var give = B("do.this_card", ("how", "control"));
        give.Link = ActionLink.IfYouDo;
        var seneschal = Synchro(100510052, "Angel Seneschal", new EffectEvent { When = when, Do = [B("do.negate_attack"), give] });
        Assert.Equal("When an \"Angelechy\" monster targets this card for an attack: Negate the attack, and if you do, give control of this card to your opponent.", Text(seneschal));
        var pawn = Monster(100510053, "Angel Pawn");
        new EngineScenario(seneschal, pawn).Card(seneschal.Card.Id, 0, "mzone").Card(pawn.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(pawn.Card.Id).Turns(2)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,1,nil,{seneschal.Card.Id})")
            .Check("Duel.GetLP(0)==8000 and Duel.GetLP(1)==8000").AssertPasses();
        // Another monster's attack: nothing happens.
        new EngineScenario(seneschal).Card(seneschal.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Attack(Warrior).Turns(2)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{seneschal.Card.Id})").AssertPasses();
    }

    // ---------------------------------------------------------------- the first turn, your next turn

    [Fact]
    public void FirstTurnAndYourNextTurn_Read()
    {
        var lock_ = B("do.summon_restriction", ("duration", "your_next"), ("who", "you"), ("what", "special_extra"))
            .With("cards", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Synchro });
        var opening = Trap(100510038, "Angel Opening", Activate(B("do.draw"), lock_));
        Assert.Equal("Draw 1 card, also you cannot Special Summon from the Extra Deck until the end of your next turn after this card resolves, except Synchro Monsters.", Text(opening));
        // On its own: Angelechy Opening to e4's last sentence.
        var alone = Trap(100510050, "Angel Opening", Activate(lock_));
        Assert.Equal("Until the end of your next turn after this card resolves, you cannot Special Summon from the Extra Deck, except Synchro Monsters.", Text(alone));
        Assert.Contains("RESET_PHASE+PHASE_END+RESET_SELF_TURN,Duel.GetTurnPlayer()==tp and 2 or 1", ScriptCompiler.Compile(opening).Lua);
        var hand = new EffectEvent
        {
            When = B("when.while_face_up"),
            If = [B("if.turn", ("whose", "opponent")), B("if.phase", ("phase", "standby")), B("if.turn", ("whose", "first"))],
            Do = [B(ActivateFromHandPassive.BlockId)],
        };
        var early = Trap(100510039, "Angel Early", Activate(B("do.draw")), hand);
        Assert.Equal("During your opponent's Standby Phase, if it is the first turn of the Duel, you can activate this card from your hand.", Text(early, 1));
        Assert.Contains("Duel.GetTurnCount()==1", ScriptCompiler.Compile(early).Lua);
    }

    [EngineFact]
    public void ActivatedFromTheHand_DuringTheStandbyPhaseOfTheOpponentsFirstTurn()
    {
        var hand = new EffectEvent
        {
            When = B("when.while_face_up"),
            If = [B("if.turn", ("whose", "opponent")), B("if.phase", ("phase", "standby")), B("if.turn", ("whose", "first"))],
            Do = [B(ActivateFromHandPassive.BlockId)],
        };
        var early = Trap(100510040, "Angel Early", Activate(B("do.draw")), hand);
        // Player 0 goes first: player 1 activates it from their hand in the Standby Phase.
        new EngineScenario(early).Card(early.Card.Id, 1, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(early.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_GRAVE,0,1,nil,{early.Card.Id})")
            .ExpectActivations(1).AssertPasses();
    }

    [EngineFact]
    public void UntilTheEndOfYourNextTurn_LastsThroughYourNextTurn()
    {
        var lock_ = B("do.summon_restriction", ("duration", "your_next"), ("who", "you"), ("what", "special_extra"))
            .With("cards", new CardFilter { CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Synchro });
        var opening = Spell(100510041, "Angel Opening", SpellKind.Normal, Activate(lock_));
        // Your turn 1, their turn 2, your turn 3: still locked at the start of turn 3, no longer in turn 4.
        new EngineScenario(opening).Card(opening.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 8).Card(Warrior, 1, "deck", 8)
            .Activate(opening.Card.Id).Turns(2)
            .Check("Duel.IsPlayerAffectedByEffect(0,EFFECT_CANNOT_SPECIAL_SUMMON)~=nil").AssertPasses();
        new EngineScenario(opening).Card(opening.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 8).Card(Warrior, 1, "deck", 8)
            .Activate(opening.Card.Id).Turns(3)
            .Check("Duel.IsPlayerAffectedByEffect(0,EFFECT_CANNOT_SPECIAL_SUMMON)==nil").AssertPasses();
    }

    // ---------------------------------------------------------------- based on the number of ...

    private static CardProject Verdict(long id)
    {
        var counted = new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field, CardType = FilterCardType.Monster, MonsterCards = true, FaceUpOnly = true, Archetype = Angelechy };
        var few = B(CompareValuesCondition.BlockId, ("value", "count"), ("op", "between"), ("amount", "1"), ("amount2", "4")).With(ValueSources.CardsKey, counted);
        var negate = B("do.negate_effects", ("duration", "always")).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.Field, FaceUpOnly = true });
        var many = B(CompareValuesCondition.BlockId, ("value", "count"), ("op", "ge"), ("amount", "5")).With(ValueSources.CardsKey, counted);
        many.Link = ActionLink.Option;
        var banish = B("do.banish", ("mode", "all")).With("cards", CardFilter.OpponentField());
        return Spell(id, "Angel Verdict", SpellKind.Normal, Activate(B(ChooseEffectsAction.BlockId, ("style", "count")), few, negate, many, banish));
    }

    [Fact]
    public void BasedOnTheNumberOf_ReadsAsAHeaderAndCounts()
    {
        var verdict = Verdict(100510042);
        Assert.Equal("Apply this effect based on the number of face-up \"Angelechy\" Monster Cards on the field."
                     + EffectTextGenerator.OptionLine + "1 to 4: Negate the effects of 1 face-up card your opponent controls."
                     + EffectTextGenerator.OptionLine + "5+: Banish all cards your opponent controls.", Text(verdict));
        Assert.DoesNotContain(ScriptCompiler.Compile(verdict).Diagnostics, d => d.Severity == Severity.Error);
    }

    [EngineTheory]
    [InlineData(1, false)]
    [InlineData(5, true)]
    public void BasedOnTheNumberOf_AppliesTheOptionForThatMany(int angels, bool banished)
    {
        var verdict = Verdict(100510043);
        var pawn = Monster(100510044, "Angel Pawn");
        var check = banished ? "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0" : "Duel.GetFieldCard(1,LOCATION_MZONE,0):IsDisabled()";
        new EngineScenario(verdict, pawn).Card(verdict.Card.Id, 0, "hand").Card(pawn.Card.Id, 0, "mzone", angels).Card(EffectFiller, 1, "mzone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(verdict.Card.Id).Check(check).AssertPasses();
    }

    // ---------------------------------------------------------------- back to the Extra Deck

    [Fact]
    public void MonsterCardsBackToTheExtraDeck_ReadSo()
    {
        var owned = new CardRequirement { Property = "owner", Op = RequirementOp.Is, Values = [0] };
        var cards = new CardFilter { Owner = CardOwner.You, Zones = Zone.SpellTrapZone, CardType = FilterCardType.Monster, MonsterCards = true, Archetype = Angelechy, Requirements = [owned] };
        var back = B("do.shuffle_into_deck", ("extra", "true")).With("cards", cards);
        var p = Spell(100510045, "Angel Move", SpellKind.Normal, Activate(back));
        Assert.Equal("Return 1 \"Angelechy\" Monster Card that you own in your Spell & Trap Zone to the Extra Deck.", Text(p));
        Assert.Contains("c:IsAbleToExtra()", ScriptCompiler.Compile(p).Lua);
        // Printed as Synchro Monsters: they go back there too.
        var printed = cards.Clone();
        printed.Requirements = [owned, new CardRequirement { Property = "orig_card_type", Op = RequirementOp.Is, Values = [0x2000] }];
        Assert.StartsWith("Return 1 ", Text(Spell(100510046, "Angel Move", SpellKind.Normal, Activate(B("do.shuffle_into_deck").With("cards", printed)))));
    }

    [EngineFact]
    public void ReturnsAMonsterCardToTheExtraDeck_ThenSummonsIt()
    {
        // Angelechy Problem: "return 1 "Angelechy" Monster Card you own from your Spell & Trap Zone to the Extra Deck, then you
        // can Special Summon it."
        var cards = new CardFilter { Owner = CardOwner.You, Zones = Zone.SpellTrapZone, CardType = FilterCardType.Monster, MonsterCards = true, Archetype = Angelechy };
        var back = B("do.shuffle_into_deck", ("extra", "true")).With("cards", cards);
        var summon = B("do.special_summon", ("ignore", "true")).With("cards", new CardFilter { That = CardRef.Affected, CardType = FilterCardType.Monster });
        summon.Link = ActionLink.Then;
        summon.Optional = true;
        var move = Monster(100510047, "Angel Mover", Ignition(back, summon));
        var placer = Monster(100510048, "Angel Placer", Ignition(PlaceFromExtra()));
        var rook = Synchro(100510049, "Angel Rook");
        new EngineScenario(move, placer, rook).Card(move.Card.Id, 0, "mzone").Card(placer.Card.Id, 0, "mzone").Card(rook.Card.Id, 0, "extra")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(placer.Card.Id).Activate(move.Card.Id)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{rook.Card.Id})").AssertPasses();
    }

    // ---------------------------------------------------------------- reading the official scripts

    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, $"c{id}.lua"));

    private static CardData Official(long id, string name) =>
        new() { Id = id, Name = name, Frame = MonsterFrame.Synchro, Level = 7, Atk = 2500, Def = 2000, Race = CardRace.Fairy, Attribute = CardAttribute.Light, Archetypes = [Angelechy] };

    /// <summary>
    /// Angelechy Destrier and Shatranga: "If this card is placed in the Spell &amp; Trap Zone as a Continuous Spell" is read
    /// back from its watchers (EVENT_MOVE, EVENT_CHAIN_SOLVED) and the trigger on their event. (Angelechy Bastion's trigger
    /// reads too, but what it does, placing a monster with Duel.MoveToField and a type change, stays script.)
    /// </summary>
    [EngineTheory]
    [InlineData(55393975L, "Angelechy Destrier")]
    [InlineData(42410161L, "Angelechy Shatranga")]
    public void Translates_PlacedAsAContinuousSpell(long id, string name)
    {
        var card = Official(id, name);
        var result = ScriptTranslator.Translate(Script(id), id, card);
        var found = result.Pieces.SelectMany(p => p.Effects).Where(e => e.When?.BlockId == ThisCardEventTrigger.BlockId).ToList();
        Assert.True(found.Count == 1, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var placed = found[0];
        Assert.Equal(ThisCardEventTrigger.PlacedAsSpellKey, placed.When!.Values["event"]);
        Assert.Empty(placed.If);
        Assert.Equal(OncePerTurn.Hard, placed.Limit);
        // The watchers are part of the trigger: no piece is left for them.
        Assert.DoesNotContain(result.Pieces, p => p.Piece.Code.Contains("EVENT_MOVE", StringComparison.Ordinal));
        Assert.StartsWith("If this card is placed in the Spell & Trap Zone as a Continuous Spell: You can ", EffectTextGenerator.Describe(placed, card, Pack));
        Assert.False(ScriptCompiler.Compile(new CardProject { Card = card, Effects = [placed] }).HasErrors);
    }

    /// <summary>Angelechy Opening to e4: "If your opponent goes first, you can also activate this card from your hand during the Standby Phase of their first turn."</summary>
    [EngineFact]
    public void Translates_TheFirstTurnOfTheDuel()
    {
        var card = new CardData { Id = 80565021, Name = "Angelechy Opening to e4", Kind = CardKind.Trap, Archetypes = [Angelechy] };
        var result = ScriptTranslator.Translate(Script(80565021), 80565021, card);
        var hand = result.Pieces.SelectMany(p => p.Effects).Single(e => e.Do.Any(b => b.BlockId == ActivateFromHandPassive.BlockId));
        Assert.Contains(hand.If, b => b.BlockId == "if.turn" && b.Values["whose"] == "first");
        Assert.Contains(hand.If, b => b.BlockId == "if.turn" && b.Values["whose"] == "opponent");
        Assert.Contains(hand.If, b => b.BlockId == "if.phase" && b.Values["phase"] == "standby");
        Assert.Equal("During your opponent's Standby Phase, if it is the first turn of the Duel, you can activate this card from your hand.", EffectTextGenerator.Describe(hand, card, Pack));
    }

    /// <summary>Angelechy Bastion: "While this card is treated as a Continuous Spell, other "Angelechy" cards on the field cannot be destroyed by your opponent's card effects."</summary>
    [EngineFact]
    public void Translates_WhileTreatedAsAContinuousSpell()
    {
        var card = Official(28904860, "Angelechy Bastion");
        var result = ScriptTranslator.Translate(Script(28904860), 28904860, card);
        var lasting = result.Pieces.SelectMany(p => p.Effects).Single(e => e.When?.BlockId == "when.while_face_up");
        Assert.Equal("spell", lasting.When!.Values["as"]);
        Assert.Empty(lasting.If);
        Assert.Equal("While this card is treated as a Continuous Spell, other face-up \"Angelechy\" cards on the field cannot be destroyed by your opponent's card effects.",
            EffectTextGenerator.Describe(lasting, card, Pack));
    }

    // ---------------------------------------------------------------- the zone your opponent's monsters go to

    /// <summary>
    /// Angelechy Endgame Problem's "When your opponent would Special Summon a monster(s), you choose the zone(s) to place
    /// it" is EFFECT_OPPO_CHOOSES_SPSUMMON_ZONE in EDOPro; ygopro's core has no such effect (the player who Summons picks the
    /// zone), so the checker reports it.
    /// </summary>
    [Fact]
    public void OpponentChoosesTheZone_IsNotInTheEngine()
    {
        Assert.Contains("EFFECT_OPPO_CHOOSES_SPSUMMON_ZONE", Validation.ScriptChecker.UnknownNames("local e2=Effect.CreateEffect(c)\ne2:SetCode(EFFECT_OPPO_CHOOSES_SPSUMMON_ZONE)"));
    }
}
