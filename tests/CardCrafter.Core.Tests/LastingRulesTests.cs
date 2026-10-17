using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Lasting rules: activations and effects that cannot be negated (Reindsurm Carnation, Golem that Guards the Millennium
/// Treasures, Magical Meltdown, Heretic Eradicator), IF blocks joined by "or", locks on any player who controls something
/// (Mimighoul Dungeon), Invoked Caliga's limits, Heart of Gaia, battle destruction that banishes (Ars Magna DoomZ) and
/// "immediately after it resolves" (Red-Eyes Flare Metal Dragon).
/// </summary>
public class LastingRulesTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Monster(long id, string name, CardRace race, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = name, Frame = MonsterFrame.Effect, Race = race, Attribute = CardAttribute.Light, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    private static EffectEvent Lasting(params BlockInstance[] passives) =>
        new() { When = B("when.while_face_up", ("zone", "monster_zone")), Do = [.. passives] };

    private static EffectEvent Draw(string zone = "monster_zone") => new() { When = B("when.ignition", ("zone", zone)), Do = [B("do.draw")] };

    private static string Text(CardProject p, int i = 0) => EffectTextGenerator.Describe(p.Effects[i], p.Card);

    private static CardFilter Kind(CardRace race) => new() { Zones = Zone.None, CardType = FilterCardType.Monster, Race = race };

    /// <summary>Player 1's monster that negates the effect of the opponent's monster effect (or Spell's activation) as it is activated.</summary>
    private static CardProject Negator(long id, string timing = "chain_monster", string what = "effect") =>
        Monster(id, "Negator", CardRace.Spellcaster, new EffectEvent { When = B("when.quick", ("timing", timing), ("zone", "monster_zone")), Do = [B("do.negate", ("what", what))] });

    private static string Hand(int player, int n) => $"Duel.GetFieldGroupCount({player},LOCATION_HAND,0)=={n}";

    /// <summary>A card in the GY that changes a monster's position at the first Standby Phase (setup Lua can't).</summary>
    private static CardProject Rigger(long code, int player, string position) =>
        RequirementMatrix.RiggerCard($"\tlocal c=find({code},LOCATION_MZONE,{player})\n\tif c and not c:IsPosition({position}) then Duel.ChangePosition(c,{position}) end");

    // ---------------------------------------------------------------- cannot be negated

    [Fact]
    public void ActivatedEffectsCannotBeNegated_WhileOneOfTwoThingsHolds()
    {
        var own = new CardFilter
        {
            Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, Race = CardRace.Thunder,
            Requirements = [new CardRequirement { Property = "orig_level", Op = RequirementOp.GreaterOrEqual, Value = 8 }],
        };
        var theirs = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone | Zone.Graveyard, CardType = FilterCardType.Monster, Race = CardRace.Dragon };
        var field = new CardProject
        {
            Card = new CardData { Id = 100480001, Name = "Thunder Carnation", Kind = CardKind.Spell, SpellKind = SpellKind.Field },
            Effects =
            [
                new EffectEvent
                {
                    When = B("when.while_face_up"),
                    If = [B("if.cards_exist").With("cards", own), B("if.cards_exist").With("cards", theirs).OrElse()],
                    Do = [B(CannotNegatePassive.BlockId, ("what", "effects")).With("cards", Kind(CardRace.Thunder))],
                },
            ],
        };
        Assert.Equal("While you control a face-up Thunder monster whose original Level is 8 or higher or there is a Dragon monster in your opponent's GY or field, " +
                     "the activated effects of your Thunder monsters cannot be negated.", Text(field));
        var lua = ScriptCompiler.Compile(field).Lua;
        Assert.Contains("EFFECT_CANNOT_DISEFFECT", lua);
        Assert.DoesNotContain("EFFECT_CANNOT_INACTIVATE", lua);
        // Either check is enough: they are joined with "or" in one condition.
        Assert.Matches(@"return \(\(Duel\.IsExistingMatchingCard\([^\n]*\)\) or \(Duel\.IsExistingMatchingCard\([^\n]*\)\)\)", lua);
    }

    [Theory]
    [InlineData("activation", "The activation of your Thunder monsters' effects cannot be negated.")]
    [InlineData("both", "The activation and the activated effects of your Thunder monsters cannot be negated.")]
    public void ActivationOfMonsterEffects_Wording(string what, string expected)
    {
        var p = Monster(100480002, "Discharger", CardRace.Thunder, Lasting(B(CannotNegatePassive.BlockId, ("what", what)).With("cards", Kind(CardRace.Thunder))));
        Assert.Equal(expected, Text(p));
    }

    [Fact]
    public void FusionSummoningActivations_CannotBeNegated_AndTheOpponentCannotRespond()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100480003, Name = "Meltdown", Kind = CardKind.Spell, SpellKind = SpellKind.Field },
            Effects = [new EffectEvent { When = B("when.while_face_up"), Do = [B(CannotNegatePassive.BlockId, ("what", "fusion"), ("fusion_lock", "true"))] }],
        };
        Assert.Equal("The activation of your cards and effects that include an effect that Fusion Summons a Fusion Monster cannot be negated, " +
                     "also your opponent's cards and effects cannot activate when a monster is Fusion Summoned this way.", Text(p));
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("te:IsHasCategory(CATEGORY_FUSION_SUMMON)", lua);
        Assert.Contains("Duel.SetChainLimitTillChainEnd", lua);
        Assert.Contains("EVENT_CHAIN_END", lua);
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void YourThunderMonstersEffects_CannotBeNegated(bool protectedByGate)
    {
        var thunder = Monster(100480010, "Thunder Drawer", CardRace.Thunder, Draw());
        var gate = Monster(100480011, "Thunder Gate", CardRace.Warrior, Lasting(B(CannotNegatePassive.BlockId, ("what", "effects")).With("cards", Kind(CardRace.Thunder))));
        var negator = Negator(100480012);
        var s = new EngineScenario(thunder, gate, negator).Card(thunder.Card.Id, 0, "mzone").Card(negator.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5);
        if (protectedByGate) s.Card(gate.Card.Id, 0, "mzone");
        s.Activate(thunder.Card.Id).Activate(negator.Card.Id).Check(Hand(0, protectedByGate ? 1 : 0)).AssertPasses();
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheActivationOfYourNamedSpell_CannotBeNegated(bool protectedByGate)
    {
        var spell = new CardProject
        {
            Card = new CardData { Id = 100480020, Name = "Millennium Draw", Kind = CardKind.Spell, SpellKind = SpellKind.Normal },
            Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.draw")] }],
        };
        var golem = Monster(100480021, "Golem", CardRace.Rock, Lasting(B(CannotNegatePassive.BlockId, ("what", "activation"))
            .With("cards", new CardFilter { Zones = Zone.None, CardCode = spell.Card.Id })));
        Assert.Equal("The activation of your \"Millennium Draw\" cannot be negated.", EffectTextGenerator.Describe(golem.Effects[0], golem.Card, new CardPack { Cards = [spell, golem] }));
        var negator = Negator(100480022, "chain_spell", "activation");
        var s = new EngineScenario(spell, golem, negator).Card(spell.Card.Id, 0, "hand").Card(negator.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5);
        if (protectedByGate) s.Card(golem.Card.Id, 0, "mzone");
        // The Spell drew a card only if its activation was not negated.
        s.Activate(spell.Card.Id).Activate(negator.Card.Id).Check(Hand(0, protectedByGate ? 1 : 0)).AssertPasses();
    }

    [EngineTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThisEffectCannotBeNegated_WhileYouControlNoMonsters(bool youControlAMonster)
    {
        var spell = new CardProject
        {
            Card = new CardData { Id = 100480030, Name = "Eradicating Draw", Kind = CardKind.Spell, SpellKind = SpellKind.Normal },
            Effects = [new EffectEvent { When = B("when.activate"), Cost = [B(CannotBeNegatedCost.BlockId, ("while", "no_monsters"))], Do = [B("do.draw")] }],
        };
        Assert.Equal("Draw 1 card (while you control no monsters, this effect and its activation cannot be negated).", Text(spell));
        var negator = Negator(100480031, "chain_spell", "activation");
        var s = new EngineScenario(spell, negator).Card(spell.Card.Id, 0, "hand").Card(negator.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5);
        if (youControlAMonster) s.Card(Fiend, 0, "mzone");
        s.Activate(spell.Card.Id).Activate(negator.Card.Id).Check(Hand(0, youControlAMonster ? 0 : 1)).AssertPasses();
    }

    // ---------------------------------------------------------------- "or" between IF blocks

    [EngineTheory]
    [InlineData(false, false, 1000)]
    [InlineData(true, false, 1500)]
    [InlineData(false, true, 1500)]
    public void EitherIfBlockIsEnough(bool youHaveADragon, bool theyHaveAFiend, int atk)
    {
        var gate = Monster(100480040, "Either Gate", CardRace.Warrior, new EffectEvent
        {
            When = B("when.while_face_up", ("zone", "monster_zone")),
            If =
            [
                B("if.cards_exist").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, Race = CardRace.Dragon }),
                B("if.cards_exist").With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true, Race = CardRace.Fiend }).OrElse(),
            ],
            Do = [B("passive.self_stats", ("amount", "500"))],
        });
        Assert.Equal("While you control a face-up Dragon monster or your opponent controls a face-up Fiend monster, this card gains 500 ATK.", Text(gate));
        var s = new EngineScenario(gate).Card(gate.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5);
        if (youHaveADragon) s.Card(EngineScenario.Dragon, 0, "mzone");
        if (theyHaveAFiend) s.Card(Fiend, 1, "mzone");
        s.Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{gate.Card.Id}):GetAttack()=={atk}").AssertPasses();
    }

    [Fact]
    public void OrSurvivesSavingAndOldPacksLoadWithoutIt()
    {
        var b = B("if.cards_exist").OrElse();
        var json = System.Text.Json.JsonSerializer.Serialize(b, PackSerializer.Options);
        Assert.Contains("\"or\": true", json);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<BlockInstance>(json, PackSerializer.Options)!.Or);
        Assert.DoesNotContain("\"or\"", System.Text.Json.JsonSerializer.Serialize(B("if.cards_exist"), PackSerializer.Options));
    }

    // ---------------------------------------------------------------- any player who controls ...

    private static CardProject Dungeon(long id)
    {
        var faceDown = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceDownOnly = true };
        var spsummoned = new CardFilter { CardType = FilterCardType.Monster, Requirements = [new CardRequirement { Property = "spsummoned_turn", Op = RequirementOp.Is }] };
        return new CardProject
        {
            Card = new CardData { Id = id, Name = "Mimic Dungeon", Kind = CardKind.Spell, SpellKind = SpellKind.Field },
            Effects =
            [
                new EffectEvent
                {
                    When = B("when.while_face_up"),
                    Do =
                    [
                        B("passive.lock", ("who", "holder"), ("what", "normal_only")).With("holder", faceDown),
                        B("passive.lock", ("who", "holder"), ("what", "attack_of")).With("holder", faceDown).With("cards", spsummoned),
                    ],
                },
            ],
        };
    }

    [Fact]
    public void AnyPlayerWhoControlsAFaceDownMonster_Wording()
    {
        var p = Dungeon(100480050);
        Assert.Equal("Any player who controls a face-down monster cannot Normal Summon monsters, nor declare an attack with monsters that were Special Summoned this turn.", Text(p));
        var lua = ScriptCompiler.Compile(p).Lua;
        // One Normal Summon lock per player, each while that player controls a face-down monster.
        Assert.Contains("SetTargetRange(1,0)", lua);
        Assert.Contains("SetTargetRange(0,1)", lua);
        Assert.Contains("c:GetControler()", lua);
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnyPlayerWhoControlsAFaceDownMonster_CannotNormalSummon(bool faceDown)
    {
        var p = Dungeon(100480051);
        // The Field Spell is activated first; the face-down monster is Set by the setup.
        var s = new EngineScenario(p, Rigger(Fiend, 0, "POS_FACEDOWN_DEFENSE")).Card(p.Card.Id, 0, "hand").Card(Fiend, 0, "mzone").Card(Warrior, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(p.Card.Id).Summon(Warrior);
        if (faceDown) s.Card(RequirementMatrix.Rigger, 0, "grave");
        s.Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_HAND,0,1,nil,{Warrior})=={(faceDown ? "true" : "false")}").AssertPasses();
    }

    // ---------------------------------------------------------------- Invoked Caliga

    [EngineFact]
    public void EachPlayerCanOnlyActivateOneMonsterEffectPerTurn()
    {
        var gate = Monster(100480060, "Caliga", CardRace.Fiend, Lasting(B("passive.player_rule", ("rule", "one_monster_effect"))));
        Assert.Equal("Each player can only attempt to activate 1 monster effect per turn.", Text(gate));
        var a = Monster(100480061, "Drawer A", CardRace.Warrior, Draw());
        var b = Monster(100480062, "Drawer B", CardRace.Warrior, Draw());
        new EngineScenario(gate, a, b).Card(gate.Card.Id, 0, "mzone").Card(a.Card.Id, 0, "mzone").Card(b.Card.Id, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(a.Card.Id).Activate(b.Card.Id).ExpectActivations(1).Check(Hand(0, 1)).AssertPasses();
    }

    [EngineTheory]
    [InlineData(true, 6500)]
    [InlineData(false, 5000)]
    public void EachPlayerCanOnlyAttackWithOneMonsterEachBattlePhase(bool withRule, int lp)
    {
        var rules = withRule ? B("passive.player_rule", ("rule", "one_attacker")) : B("passive.self_rule", ("rule", "cannot_attack"));
        var gate = Monster(100480065, "Caliga Wall", CardRace.Fiend, Lasting(rules));
        gate.Card.Atk = 0;
        gate.Card.Def = 3000;
        if (withRule) Assert.Equal("Each player can only attack with 1 monster during each Battle Phase.", Text(gate));
        // Player 1's two 1500 ATK Warriors attack the 3000 DEF wall on their turn; each attack costs them 1500 LP.
        var s = new EngineScenario(gate, Rigger(gate.Card.Id, 0, "POS_FACEUP_DEFENSE")).Card(gate.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone", 2).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Card(RequirementMatrix.Rigger, 0, "grave");
        s.Turns(2).Attack(Warrior).Attack(Warrior).Check($"Duel.GetLP(1)=={lp}").AssertPasses();
    }

    // ---------------------------------------------------------------- Heart of Gaia, DoomZ, Flare Metal

    [EngineFact]
    public void MonstersYourOpponentSummonsInAttackPosition_GainLevels()
    {
        var gaia = Monster(100480070, "Heart", CardRace.Warrior, Lasting(B(SummonedChangePassive.BlockId, ("position", "attack"), ("amount", "7"))));
        Assert.Equal("The Level of any monster your opponent Normal or Special Summons in Attack Position, while this card is in your Monster Zone, is increased by 7.", Text(gaia));
        new EngineScenario(gaia).Card(gaia.Card.Id, 0, "mzone").Card(Warrior, 1, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Turns(2).Summon(Warrior)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,{Warrior}):GetLevel()==11").AssertPasses();
    }

    [EngineFact]
    public void MonstersDestroyedByBattleWithThisCard_AreBanished()
    {
        var doom = Monster(100480075, "Doom", CardRace.Machine, Lasting(B(BattleRedirectPassive.BlockId)));
        doom.Card.Atk = 2000;
        Assert.Equal("Monsters destroyed by battle with this card are banished.", Text(doom));
        new EngineScenario(doom).Card(doom.Card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Turns(2).Attack(Warrior)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_REMOVED,0)==1").Check("Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)==0").AssertPasses();
    }

    [Fact]
    public void MonstersDestroyedByBattleWithYourLinkedMonster_Wording()
    {
        var linked = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Requirements = [new CardRequirement { Property = "is_linked", Op = RequirementOp.Is }] };
        var p = Monster(100480076, "DoomZ", CardRace.Machine, Lasting(B(BattleRedirectPassive.BlockId).With("cards", linked)));
        Assert.Equal("Any monster destroyed by battle with your monster that is linked is banished.", Text(p));
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("EFFECT_BATTLE_DESTROY_REDIRECT", lua);
        Assert.Contains("c:IsLinkState()", lua);
    }

    [EngineFact]
    public void EachTimeYourOpponentActivates_DamageImmediatelyAfterItResolves()
    {
        var flare = Monster(100480080, "Flare", CardRace.Dragon, Lasting(B(EachTimePassive.BlockId, ("event", "opp_activates"), ("then", "damage"), ("amount", "500"), ("timing", "resolved"))));
        Assert.Equal("Each time your opponent activates a card or effect, inflict 500 damage to your opponent immediately after it resolves.", Text(flare));
        var drawer = Monster(100480081, "Their Drawer", CardRace.Warrior, Draw());
        var lua = ScriptCompiler.Compile(flare).Lua;
        Assert.Contains("EVENT_CHAIN_SOLVED", lua);
        new EngineScenario(flare, drawer).Card(flare.Card.Id, 0, "mzone").Card(drawer.Card.Id, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Turns(2).Activate(drawer.Card.Id).Check("Duel.GetLP(1)==7500").AssertPasses();
    }
}
