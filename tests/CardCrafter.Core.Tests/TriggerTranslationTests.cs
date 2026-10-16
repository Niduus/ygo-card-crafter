using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Official scripts whose WHEN is an activation, a Summon about to happen, an attack, a battle window, or a card moving
/// with details, read into the WHEN blocks and played in the engine.
/// </summary>
public class TriggerTranslationTests
{
    private const long OppCard = 100097090;

    private static string? Script(long id) =>
        EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{id}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{id}.lua")) : null;

    private static CardData Monster(long id, int atk = 1000, int def = 1000, int level = 4, params int[] archetypes) =>
        new() { Id = id, Name = "Trigger " + id, Frame = MonsterFrame.Effect, Level = level, Atk = atk, Def = def, Race = CardRace.Fairy, Attribute = CardAttribute.Light, Archetypes = [.. archetypes] };

    private static CardData TrapCard(long id) => new() { Id = id, Name = "Trigger " + id, Kind = CardKind.Trap };

    /// <summary>The effect of an official script whose WHEN is <paramref name="whenId"/>, translated for <paramref name="card"/>.</summary>
    private static EffectEvent Effect(long official, CardData card, string whenId)
    {
        var result = ScriptTranslator.Translate(Script(official)!, official, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(e => e.When?.BlockId == whenId);
        Assert.True(effect is not null, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var compiled = ScriptCompiler.Compile(Project(card, effect));
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        Assert.DoesNotContain(ScriptChecker.Check(compiled.Lua), d => d.Severity == Severity.Error);
        return effect;
    }

    private static CardProject Project(CardData card, EffectEvent effect) => new() { Card = card, Effects = [effect] };

    /// <summary>The opponent's Spell: they gain 1000 LP.</summary>
    private static CardProject OppSpell() => new()
    {
        Card = new CardData { Id = OppCard, Name = "Opp Spell", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = BlockRegistry.Find("when.activate")!.CreateInstance(), Do = [new BlockInstance("do.gain_lp").With("amount", 1000)] }],
    };

    /// <summary>The opponent's Spell that destroys 1 monster its target picker finds on your field.</summary>
    private static CardProject OppDestroyer()
    {
        var destroy = BlockRegistry.Find("do.destroy")!.CreateInstance().With("target", true).With("count", 1);
        destroy.Filters["cards"] = new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        return new()
        {
            Card = new CardData { Id = OppCard, Name = "Opp Destroyer", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = BlockRegistry.Find("when.activate")!.CreateInstance(), Do = [destroy] }],
        };
    }

    private static CardProject Normal(long id, int atk, params int[] archetypes) => new()
    {
        Card = new CardData { Id = id, Name = "Normal " + id, Frame = MonsterFrame.Normal, Level = 4, Atk = atk, Def = 1000, Race = CardRace.Beast, Archetypes = [.. archetypes] },
    };

    private static EngineScenario Duel(params CardProject[] cards) => new EngineScenario(cards).Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4);

    [EngineTheory]
    [InlineData(0x108, true)]
    [InlineData(0, false)]
    public void MagicalMusketLastStand_WhileYouControlAMagicalMusketMonster(int archetype, bool negates)
    {
        if (Script(29628180) is null) return;
        const long musket = 100097016;
        var card = TrapCard(100097001);
        var effect = Effect(29628180, card, ActivationTrigger.BlockId);
        Assert.Equal(("activate", "opponent", "spelltrap_card"), (effect.When!.Values["mode"], effect.When.Values["who"], effect.When.Values["what"]));
        Assert.StartsWith("When your opponent activates a Spell/Trap Card while you control a face-up \"", EffectTextGenerator.Describe(effect, card));
        Assert.Contains("Negate the activation, and if you do, destroy that card.", EffectTextGenerator.Describe(effect, card));
        Duel(Project(card, effect), OppSpell(), Normal(musket, 1000, archetype == 0 ? [] : [archetype])).Card(card.Id, 0, "szone").Card(musket, 0, "mzone").Card(OppCard, 1, "hand")
            .Activate(OppCard).Activate(card.Id).Turns(2)
            .Check(negates ? "Duel.GetLP(1)==8000" : "Duel.GetLP(1)==9000").AssertPasses();
    }

    [EngineFact]
    public void HeraldOfTheArcLight_NegatesASpellCardOrAMonsterEffect()
    {
        if (Script(79606837) is null) return;
        var card = Monster(100097002);
        var effect = Effect(79606837, card, ActivationTrigger.BlockId);
        Assert.Equal(("quick", "either", "card_or_monster", "yes"),
            (effect.When!.Values["mode"], effect.When.Values["who"], effect.When.Values["what"], effect.When.Values["damage_step"]));
        Assert.StartsWith("When a Spell/Trap Card, or monster effect, is activated (Quick Effect): ", EffectTextGenerator.Describe(effect, card));
        // The official check that it wasn't destroyed by battle is the one the block writes.
        Assert.Contains("STATUS_BATTLE_DESTROYED", ScriptCompiler.Compile(Project(card, effect)).Lua);
        Duel(Project(card, effect), OppSpell()).Card(card.Id, 0, "mzone").Card(OppCard, 1, "hand")
            .Activate(OppCard).Activate(card.Id).Turns(2)
            .Check("Duel.GetLP(1)==8000").Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==0").AssertPasses();
    }

    [EngineFact]
    public void SolemnStrike_And_BlackHornOfHeaven_NegateSummons()
    {
        if (Script(40605147) is null || Script(50323155) is null) return;
        var strike = TrapCard(100097003);
        var summon = Effect(40605147, strike, SummonAttemptTrigger.BlockId);
        Assert.Equal(("activate", "either", "special"), (summon.When!.Values["mode"], summon.When.Values["who"], summon.When.Values["how"]));
        Assert.StartsWith("When a monster(s) would be Special Summoned: Pay 1500 LP; negate the Summon, and if you do, destroy that monster(s).", EffectTextGenerator.Describe(summon, strike));

        var horn = TrapCard(100097004);
        var one = Effect(50323155, horn, SummonAttemptTrigger.BlockId);
        Assert.Equal(("opponent", "one"), (one.When!.Values["who"], one.When.Values["count"]));
        Assert.Contains("eg:GetCount()==1", ScriptCompiler.Compile(Project(horn, one)).Lua);
        Assert.StartsWith("When your opponent would Special Summon exactly 1 monster: Negate the Summon", EffectTextGenerator.Describe(one, horn));
    }

    [EngineTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DrowningMirrorForce_WhenAnOpponentsMonsterDeclaresADirectAttack(bool youHaveAMonster, bool activates)
    {
        if (Script(47475363) is null) return;
        const long attacker = 100097005;
        var card = TrapCard(100097006);
        var effect = Effect(47475363, card, AttackDeclaredTrigger.BlockId);
        Assert.Equal(("activate", "direct"), (effect.When!.Values["mode"], effect.When.Values["how"]));
        Assert.StartsWith("When an opponent's monster declares a direct attack: ", EffectTextGenerator.Describe(effect, card));
        var duel = Duel(Project(card, effect), Normal(attacker, 2000)).Card(card.Id, 0, "szone").Card(attacker, 1, "mzone");
        // With a 1500-ATK Warrior to attack, it isn't a direct attack: the Warrior is destroyed for 500 damage.
        if (youHaveAMonster) duel.Card(Warrior, 0, "mzone");
        duel.Attack(attacker).Activate(card.Id).Turns(2)
            .Check(activates ? "Duel.GetLP(0)==8000 and Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0" : "Duel.GetLP(0)==7500 and Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1").AssertPasses();
    }

    [EngineTheory]
    [InlineData(1500, true)]
    [InlineData(2500, false)]
    public void AncientCrimsonApe_WhenAMonsterYouControlIsDestroyedAndSentToTheGy(int wallAtk, bool destroyed)
    {
        if (Script(50781944) is null) return;
        const long attacker = 100097007, wall = 100097008;
        var card = Monster(100097009, atk: 2600, def: 1800, level: 7);
        var effect = Effect(50781944, card, CardsDetailTrigger.BlockId);
        Assert.StartsWith("When a monster(s) you control is destroyed and sent to the GY: Gain 1000 LP.", EffectTextGenerator.Describe(effect, card));
        var lua = ScriptCompiler.Compile(Project(card, effect)).Lua;
        Assert.Contains("c:IsPreviousControler(tp) and c:IsPreviousLocation(LOCATION_MZONE) and c:IsReason(REASON_DESTROY)", lua);
        // The wall placed before the Ape is the first target offered: the 2000-ATK attacker destroys it for 500 damage,
        // or loses to it and is destroyed itself (not a monster you control).
        Duel(Project(card, effect), Normal(attacker, 2000), Normal(wall, wallAtk)).Card(wall, 0, "mzone").Card(card.Id, 0, "mzone").Card(attacker, 1, "mzone")
            .Attack(attacker).Activate(card.Id).Turns(2)
            .Check(destroyed ? "Duel.GetLP(0)==8500" : "Duel.GetLP(0)==8000 and Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)==1").AssertPasses();
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dotedotengu_IfDestroyedByAnOpponentsCardEffect(bool byEffect)
    {
        if (Script(82050203) is null) return;
        const long opponents = 100097010;
        var card = Monster(100097011, atk: 1000);
        var effect = Effect(82050203, card, ThisCardDetailTrigger.BlockId);
        Assert.StartsWith("If this card in your possession is destroyed by an opponent's card effect and sent to the GY: You can target 1 card your opponent controls; return it to the hand.",
            EffectTextGenerator.Describe(effect, card));
        // Destroyed by the opponent's Spell, it returns their monster to the hand; destroyed by battle, it doesn't.
        var duel = Duel(Project(card, effect), OppDestroyer(), Normal(opponents, 2000)).Card(card.Id, 0, "mzone").Card(opponents, 1, "mzone");
        if (byEffect) duel.Card(OppCard, 1, "hand").Activate(OppCard);
        else duel.Attack(opponents);
        duel.Activate(card.Id).Turns(2)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==0")
            .Check(byEffect ? "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==0" : "Duel.GetFieldGroupCount(1,LOCATION_MZONE,0)==1").AssertPasses();
    }

    [EngineTheory]
    [InlineData(0xb2, true)]
    [InlineData(0, false)]
    public void UAPlayerManager_IfYouNormalOrSpecialSummonAUAMonster(int archetype, bool summons)
    {
        if (Script(47021196) is null) return;
        const long player = 100097012;
        var card = Monster(100097013, archetypes: 0xb2);
        var effect = Effect(47021196, card, CardsDetailTrigger.BlockId);
        Assert.Equal(CardsDetailTrigger.BlockId, effect.AlsoWhen.Single().BlockId);
        Assert.StartsWith("If you Normal or Special Summon a face-up \"", EffectTextGenerator.Describe(effect, card));
        Duel(Project(card, effect), Normal(player, 1000, archetype == 0 ? [] : [archetype])).Card(card.Id, 0, "hand").Card(player, 0, "hand")
            .Summon(player).Activate(card.Id)
            .Check(summons ? "Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==2" : "Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1").AssertPasses();
    }

    [Fact]
    public void TenpaiDragonChundra_AtTheStartOfTheDamageStep()
    {
        if (Script(91810826) is null) return;
        var card = Monster(100097014);
        var effect = Effect(91810826, card, BattleWindowTrigger.BlockId);
        Assert.Equal(("trigger", "start", "any"), (effect.When!.Values["mode"], effect.When.Values["window"], effect.When.Values["who"]));
        Assert.Contains("EVENT_BATTLE_START", ScriptCompiler.Compile(Project(card, effect)).Lua);
    }

    [Fact]
    public void ANegationThatChecksLessThanTheBlock_StaysScript()
    {
        // D/D/D Doom King Armageddon's negation can be used in the Damage Step without checking that it wasn't destroyed
        // by battle; the negate block checks it, so the piece stays script.
        if (Script(16006416) is null) return;
        var result = ScriptTranslator.Translate(Script(16006416)!, 16006416, Monster(100097015, level: 8));
        var piece = result.Pieces.Single(p => p.Piece.Code.Contains("EVENT_CHAINING", StringComparison.Ordinal));
        Assert.False(piece.Translated);
        Assert.Contains("negation", piece.Reason);
    }
}
