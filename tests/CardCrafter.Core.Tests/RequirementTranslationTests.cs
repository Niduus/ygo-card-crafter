using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Official scripts whose filters test what only a card filter's requirements say (not DARK, original ATK 2500, face-down
/// or not "Burning Abyss"), read into <see cref="CardFilter.Requirements"/>, and played in the engine.
/// </summary>
public class RequirementTranslationTests
{
    private static string? Script(long id) =>
        EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{id}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{id}.lua")) : null;

    private static CardData Monster(long id, int level = 4, int atk = 1000, int def = 1000, int[]? archetypes = null) => new()
    {
        Id = id, Name = "Req " + id, Frame = MonsterFrame.Effect, Level = level, Atk = atk, Def = def, Race = CardRace.Machine, Attribute = CardAttribute.Dark,
        Archetypes = [.. archetypes ?? []],
    };

    private static CardData SpellCard(long id) => new() { Id = id, Name = "Req " + id, Kind = CardKind.Spell };

    private static IEnumerable<CardFilter> Filters(EffectEvent e) =>
        new[] { e.When }.Concat(e.AlsoWhen).Concat(e.If).Concat(e.Cost).Concat(e.Do).OfType<BlockInstance>().SelectMany(b => b.Filters.Values);

    /// <summary>The effect of an official script one of whose filters <paramref name="picks"/>, translated for <paramref name="card"/>; it compiles cleanly.</summary>
    private static EffectEvent Effect(long official, CardData card, Func<CardFilter, bool> picks)
    {
        var result = ScriptTranslator.Translate(Script(official)!, official, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(e => Filters(e).Any(picks));
        Assert.True(effect is not null, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var compiled = ScriptCompiler.Compile(Project(card, effect));
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        Assert.DoesNotContain(ScriptChecker.Check(compiled.Lua), d => d.Severity == Severity.Error);
        return effect;
    }

    private static CardProject Project(CardData card, EffectEvent effect) => new() { Card = card, Effects = [effect] };

    private static CardProject Mon(long id, int level = 4, int atk = 1000, int def = 1000, CardRace race = CardRace.Machine, MonsterFrame frame = MonsterFrame.Normal,
        int[]? archetypes = null) => RequirementMatrix.Mon(id, "Req " + id, atk, def, level, CardAttribute.Earth, race, frame, archetypes: archetypes);

    private static string InMZone(int player, long id) => $"Duel.IsExistingMatchingCard(Card.IsCode,{player},LOCATION_MZONE,0,1,nil,{id})";

    [EngineFact]
    public void BurningAbyss_IsDestroyedBesideAMonsterThatIsFaceDownOrNotBurningAbyss()
    {
        if (Script(9342162) is null) return;
        // Cagna: "return c:IsFacedown() or not c:IsSetCard(0xb1)", either of two requirements.
        var card = Monster(100099001, level: 3, archetypes: [0xb1]);
        var effect = Effect(9342162, card, f => f.AnyRequirement);
        var f = Filters(effect).Single(x => x.AnyRequirement);
        Assert.Equal([("position", RequirementOp.Is), ("archetype", RequirementOp.IsNot)], f.Requirements.Select(r => (r.Property, r.Op)));
        Assert.Equal([0xaL, 0xb1L], f.Requirements.SelectMany(r => r.Values));
        Assert.StartsWith("If you control a monster that is face-down or that is not a \"", EffectTextGenerator.Describe(effect, card));
        // Beside a Warrior it destroys itself; beside another "Burning Abyss" monster it stays.
        new EngineScenario(Project(card, effect)).Card(card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Check($"not {InMZone(0, card.Id)}").Check(InMZone(0, Warrior)).AssertPasses();
        new EngineScenario(Project(card, effect), Mon(100099002, archetypes: [0xb1])).Card(card.Id, 0, "mzone").Card(100099002, 0, "mzone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Check(InMZone(0, card.Id)).Check(InMZone(0, 100099002)).AssertPasses();
    }

    [EngineFact]
    public void RegenesisSage_ComesOutBesideAMonsterWith2500OriginalAtkOrDef()
    {
        if (Script(22938501) is null) return;
        var card = Monster(100099010, level: 4);
        var effect = Effect(22938501, card, f => f.AnyRequirement);
        var f = Filters(effect).Single(x => x.AnyRequirement);
        Assert.Equal([("base_atk", 2500), ("base_def", 2500)], f.Requirements.Select(r => (r.Property, r.Value)));
        Assert.StartsWith("If there is a face-up monster with 2500 original ATK or with 2500 original DEF on the field, you can Special Summon this card (from your hand).",
            EffectTextGenerator.Describe(effect, card));
        foreach (var (atk, def, comes) in new[] { (2500, 1000, true), (1000, 2500, true), (2400, 2400, false) })
            new EngineScenario(Project(card, effect), Mon(100099011, atk: atk, def: def)).Card(card.Id, 0, "hand").Card(100099011, 1, "mzone")
                .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4).SpSummon(card.Id)
                .Check(comes ? InMZone(0, card.Id) : $"not {InMZone(0, card.Id)}").AssertPasses();
    }

    [EngineFact]
    public void SprightJet_ComesOutBesideALevel2OrRank2Monster()
    {
        if (Script(13533678) is null) return;
        // "(c:IsLevel(2) or c:IsRank(2)) and c:IsFaceup()": a Level and a Rank, any of them.
        var card = Monster(100099020, level: 2);
        var effect = Effect(13533678, card, f => f.AnyRequirement);
        Assert.Equal([("level", 2), ("rank", 2)], Filters(effect).Single(x => x.AnyRequirement).Requirements.Select(r => (r.Property, r.Value)));
        foreach (var (level, frame, comes) in new[] { (2, MonsterFrame.Normal, true), (2, MonsterFrame.Xyz, true), (3, MonsterFrame.Normal, false), (3, MonsterFrame.Xyz, false) })
            new EngineScenario(Project(card, effect), Mon(100099021, level: level, frame: frame)).Card(card.Id, 0, "hand").Card(100099021, 0, "mzone")
                .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4).SpSummon(card.Id)
                .Check(comes ? InMZone(0, card.Id) : $"not {InMZone(0, card.Id)}").AssertPasses();
    }

    [EngineFact]
    public void CyberDragonNachster_RevivesAMachineWith2100AtkOrDef()
    {
        if (Script(1142880) is null) return;
        var card = Monster(100099030);
        var effect = Effect(1142880, card, f => f.AnyRequirement);
        var f = Filters(effect).Single(x => x.AnyRequirement);
        Assert.Equal(CardRace.Machine, f.Race);
        Assert.Equal([("atk", RequirementOp.Equal, 2100), ("def", RequirementOp.Equal, 2100)], f.Requirements.Select(r => (r.Property, r.Op, r.Value)));
        Assert.Contains("target 1 Machine monster with 2100 ATK or with 2100 DEF from your GY", EffectTextGenerator.Describe(effect, card));
        foreach (var (atk, def, revives) in new[] { (2100, 1000, true), (1000, 2100, true), (2000, 2000, false) })
            new EngineScenario(Project(card, effect), Mon(100099031, atk: atk, def: def)).Card(card.Id, 0, "hand").Card(100099031, 0, "grave")
                .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4).Summon(card.Id).Activate(card.Id)
                .ExpectActivations(revives ? 1 : 0).Check(revives ? InMZone(0, 100099031) : $"not {InMZone(0, 100099031)}").AssertPasses();
    }

    [EngineFact]
    public void GearspringSpirit_TargetsOnlyAMonsterThatHasAtk()
    {
        if (Script(45458027) is null) return;
        // aux.nzatk: "c:IsFaceup() and c:GetAttack()>0", which is IsAttackAbove(1).
        var card = Monster(100099040);
        var effect = Effect(45458027, card, f => f.AtkMin == 1);
        var f = Filters(effect).Single(x => x.AtkMin == 1);
        Assert.True(f.FaceUpOnly);
        Assert.Contains("target 1 face-up monster with 1 or more ATK your opponent controls", EffectTextGenerator.Describe(effect, card));
        new EngineScenario(Project(card, effect)).Card(card.Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(card.Id).ExpectActivations(1).AssertPasses();
        new EngineScenario(Project(card, effect), Mon(100099041, atk: 0)).Card(card.Id, 0, "mzone").Card(100099041, 1, "mzone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(card.Id).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Materialization_ReturnsAMonsterCardThatIsASpellNow()
    {
        if (Script(24649931) is null) return;
        // "c:GetOriginalType()&TYPE_MONSTER~=0": a Pendulum Monster in the Pendulum Zone is a Spell that is originally a monster.
        var card = SpellCard(100099050);
        var effect = Effect(24649931, card, f => f.Requirements.Any(r => r.Property == "orig_card_type"));
        Assert.Equal("Target 1 face-up Spell/Trap that is originally a monster on the field; return it to the hand. You can only activate 1 \"Req 100099050\" per turn.",
            EffectTextGenerator.Describe(effect, card));
        const long pend = 100099051;
        var pendulum = RequirementMatrix.Mon(pend, "Req Pendulum", 1500, 1500, 4, abilities: MonsterAbility.Pendulum, scale: 3);
        // The opponent's Pendulum Monster is put in their Pendulum Zone at the Standby Phase, before the Spell is activated.
        var rigger = RequirementMatrix.RiggerCard($"\tDuel.MoveToField(find({pend},LOCATION_HAND,1-tp),tp,1-tp,LOCATION_PZONE,POS_FACEUP,true)");
        new EngineScenario(Project(card, effect), pendulum, rigger).Card(card.Id, 0, "hand").Card(pend, 1, "hand").Card(RequirementMatrix.Rigger, 0, "grave")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4).Activate(card.Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_HAND,0,1,nil,{pend})").Check("Duel.GetFieldGroupCount(1,LOCATION_PZONE,0)==0").AssertPasses();
        // A Spell that was always one isn't.
        new EngineScenario(Project(card, effect)).Card(card.Id, 0, "hand").Card(Spell, 1, "szone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Activate(card.Id).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void HieraticSeal_TributesAHieraticMonsterNamedByTheEnginesFilter()
    {
        if (Script(11975962) is null) return;
        // "Duel.SelectReleaseGroup(tp,Card.IsSetCard,1,1,nil,0x69)": the filter's argument comes last.
        var card = SpellCard(100099060);
        var effect = Effect(11975962, card, f => f.Archetype == 0x69);
        Assert.StartsWith("Tribute 1 \"", EffectTextGenerator.Describe(effect, card));
        new EngineScenario(Project(card, effect), Mon(100099061, archetypes: [0x69])).Card(card.Id, 0, "hand").Card(100099061, 0, "mzone").Card(Warrior, 1, "mzone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4).Activate(card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(1,LOCATION_REMOVED,0)==1").Check("Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,100099061)").AssertPasses();
        new EngineScenario(Project(card, effect)).Card(card.Id, 0, "hand").Card(Warrior, 0, "mzone").Card(Warrior, 1, "mzone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4).Activate(card.Id).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void TwoKindsOfTheSameTest_BothHold()
    {
        if (Script(21862633) is null || Script(269510) is null) return;
        // Piercing the Darkness: "c:IsType(TYPE_NORMAL) and not c:IsType(TYPE_TOKEN)". The second kind used to replace the first.
        // It checks who Summoned it (IsSummonPlayer), so the trigger reads "If you Normal or Special Summon".
        var spell = new CardData { Id = 100099070, Name = "Req 100099070", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous };
        var effect = Effect(21862633, spell, f => f.Requirements.Count > 0);
        var f = Filters(effect).First(x => x.Requirements.Count > 0);
        Assert.Equal(MonsterKind.Normal, f.MonsterKind);
        Assert.Equal(("monster_type", RequirementOp.IsNot, 0x4000L), (f.Requirements.Single().Property, f.Requirements.Single().Op, f.Requirements.Single().Values.Single()));
        Assert.StartsWith("If you Normal or Special Summon a face-up non-Token Normal Monster", EffectTextGenerator.Describe(effect, spell));
        // Fire Prison: "not c:IsType(TYPE_LINK)".
        var prison = Effect(269510, new CardData { Id = 100099071, Name = "Req 100099071", Kind = CardKind.Spell, SpellKind = SpellKind.Field }, x => x.Requirements.Count > 0);
        Assert.Equal("Face-up non-Link monsters on the field cannot attack.", EffectTextGenerator.Describe(prison, new CardData { Id = 100099071, Name = "Req", Kind = CardKind.Spell }));
    }

    [EngineFact]
    public void ANumberZeroWouldPass_IsReadOnlyWhereEveryCardHasIt()
    {
        if (Script(53550467) is null || Script(1003028) is null) return;
        // Noble Knight Drystan: "c:GetAttack()<1800" among monsters you control: in the Monster Zones every card has ATK.
        var card = Monster(100099080);
        var effect = Effect(53550467, card, f => f.Requirements.Any(r => r.Property == "atk"));
        var f = Filters(effect).First(x => x.Requirements.Any(r => r.Property == "atk"));
        Assert.Equal(Zone.MonsterZone, f.Zones);
        Assert.Equal(("atk", RequirementOp.Less, 1800), (f.Requirements.Single().Property, f.Requirements.Single().Op, f.Requirements.Single().Value));
        // "not c:IsLevel(4)" also picks Xyz and Link Monsters, which have no Level: it stays script.
        var result = ScriptTranslator.Translate(Script(1003028)!, 1003028, Monster(100099081));
        Assert.Contains(result.Pieces, p => !p.Translated && p.Reason!.Contains("no such number", StringComparison.Ordinal));
    }

    [EngineFact]
    public void ALinkMaterialThatIsNotAToken_IsCheckedInItsMaterialForm()
    {
        if (Script(89238128) is null) return;
        // Cyberse Accelerator: aux.NOT(aux.FilterBoolFunction(Card.IsLinkType,TYPE_TOKEN)).
        var card = new CardData { Id = 100099090, Name = "Req 100099090", Frame = MonsterFrame.Link, Level = 2, Atk = 2000, LinkMarkers = LinkMarker.Bottom | LinkMarker.Top };
        var result = ScriptTranslator.Translate(Script(89238128)!, 89238128, card);
        var procedure = result.Pieces.Select(p => p.Procedure).OfType<SummonProcedure>().Single();
        Assert.Equal(MonsterKind.NonToken, procedure.MaterialFilter.MonsterKind);
        var lua = ScriptCompiler.Compile(new CardProject { Card = card, Procedure = procedure }).Lua;
        Assert.Contains("not c:IsLinkType(TYPE_TOKEN)", lua);
    }

    [Fact]
    public void RequirementsOfAMaterial_AreCheckedInItsMaterialForm()
    {
        var f = new CardFilter { CardType = FilterCardType.Monster, Requirements = [RequirementMatrix.IsNot("monster_type", 0x4000000), RequirementMatrix.Is("race", (long)CardRace.Dragon)] };
        var link = FilterCompiler.Conditions(f, material: SummonProcedureKind.Link, hasEffect: false);
        Assert.Contains("not (c:IsLinkType(0x4000000))", link);
        Assert.Contains("(c:IsLinkRace(0x2000))", link);
        Assert.Contains("not (c:IsType(0x4000000))", FilterCompiler.Conditions(f));
    }
}
