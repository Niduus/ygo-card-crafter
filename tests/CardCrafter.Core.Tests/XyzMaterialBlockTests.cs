using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Xyz Materials: attaching this card to an Xyz Monster (Infinitrack Goliath), effects an Xyz Monster gains while this
/// card is its material (EFFECT_TYPE_XMATERIAL), detaching as a step (Gustav Rocket), "detach any number" with "target
/// that many" (Flying Launcher) and "for each material detached" (Promenade Thrush), "if this card has a Dragon monster
/// as material" (Voloferniges), "up to 1 card each from your GY and your opponent's GY" (Tantrum Toddler) and attacks
/// on monsters up to the number of materials +1 (Juggernaut Liebe).
/// </summary>
public class XyzMaterialBlockTests
{
    private static BlockInstance B(string id) => BlockRegistry.Find(id)!.CreateInstance();

    private static string Lua(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics) + "\n" + r.Lua);
        Assert.DoesNotContain(Validation.ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        Assert.Empty(Validation.ScriptChecker.UnknownNames(r.Lua));
        return r.Lua;
    }

    private static CardProject Monster(long id, MonsterFrame frame = MonsterFrame.Effect, CardRace race = CardRace.Warrior, int level = 4, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = "Mat " + id, Frame = frame, Level = level, Atk = 1000, Def = 1000, Race = race, Attribute = CardAttribute.Earth },
        Effects = [.. effects],
        // Xyz Monsters are Xyz Summoned with 2 Level 4 monsters in the duels below.
        Procedure = frame == MonsterFrame.Xyz ? new SummonProcedure { Kind = SummonProcedureKind.Xyz, XyzMaterialLevel = 4 } : new SummonProcedure(),
    };

    /// <summary>The card text after the materials line.</summary>
    private static string Effects(CardProject p) => EffectTextGenerator.Generate(p).Split('\n', 2)[^1];

    private static EffectEvent Ignition(params BlockInstance[] blocks) => new() { When = B("when.ignition"), Do = [.. blocks] };

    /// <summary>The Xyz Monster in the Extra Deck and its 2 materials on the field, for it to be Xyz Summoned first.</summary>
    private static EngineScenario WithMaterials(EngineScenario s, long xyz, long mat1, long mat2) =>
        s.Card(xyz, 0, "extra").Card(mat1, 0, "mzone").Card(mat2, 0, "mzone").SpSummon(xyz);

    private static string Overlays(long xyz, int n) => $"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{xyz}):GetOverlayCount()=={n}";

    // ---------------------------------------------------------------- attach this card to the targeted Xyz Monster

    private static CardProject Goliath(long id) => Monster(id, effects: new EffectEvent
    {
        When = B("when.ignition").With("zone", "hand"),
        Do = [B(AttachSelfAction.BlockId).With("target", true)],
    });

    [Fact]
    public void AttachSelf_ReadsAndCompilesAsInfinitrackGoliath()
    {
        var p = Goliath(100250001);
        Assert.EndsWith("You can target 1 Xyz Monster you control; attach this card to it as material.", EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("Duel.Overlay(tc,Group.FromCards(c))", lua);
        Assert.Contains("e:GetHandler():IsCanOverlay()", lua);
        Assert.Contains("CATEGORY_LEAVE_GRAVE", lua);
    }

    [EngineFact]
    public void AttachSelf_GoesUnderTheXyzMonster()
    {
        const long id = 100250002, xyz = 100250003;
        new EngineScenario(Goliath(id), Monster(xyz, MonsterFrame.Xyz)).Card(id, 0, "hand").Card(xyz, 0, "mzone").Activate(id).ExpectActivations(1)
            .Check(Overlays(xyz, 1)).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
    }

    // ---------------------------------------------------------------- an Xyz Monster that has this card as material gains this effect

    private static CardFilter MachineXyz() => new()
    {
        Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz,
        Requirements = [new CardRequirement { Property = "orig_race", Op = RequirementOp.Is, Values = [(long)CardRace.Machine] }],
    };

    private static CardProject GoliathMaterial(long id) => Monster(id, effects: new EffectEvent
    {
        When = B("when.while_face_up").With("as", "xyz").With("xyz", MachineXyz()),
        Do = [B("passive.indestructible").With("by", "effect")],
    });

    private static CardProject RoadRoller(long id) => Monster(id, effects: new EffectEvent
    {
        When = B("when.while_face_up").With("as", "xyz").With("xyz", MachineXyz()),
        Do = [B("passive.boost_cards").With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true })
            .With("dir", "lose").With("amount", 1000).With("stat", "def")],
    });

    [Fact]
    public void XyzGains_ReadsAndCompilesAsInfinitrack()
    {
        var p = GoliathMaterial(100250010);
        Assert.Equal("An Xyz Monster whose original Type is Machine and has this card as material gains this effect.\n ● This card cannot be destroyed by card effects.",
            EffectTextGenerator.Generate(p));
        var lua = Lua(p);
        Assert.Contains("e1:SetType(EFFECT_TYPE_XMATERIAL)", lua);
        Assert.Contains("e1:SetCode(EFFECT_INDESTRUCTABLE_EFFECT)", lua);
        Assert.Contains("return s.xmatfilter(e:GetHandler())", lua);
        Assert.Contains("c:GetOriginalRace()&0x20~=0", lua);
        var roller = RoadRoller(100250011);
        Assert.Contains("gains this effect.\n ● Face-up monsters your opponent controls lose 1000 DEF.", EffectTextGenerator.Generate(roller));
        Assert.Contains("SetType(EFFECT_TYPE_XMATERIAL+EFFECT_TYPE_FIELD)", Lua(roller));
    }

    [EngineFact]
    public void XyzGains_OnlyAMachineXyzMonsterGainsIt()
    {
        const long mat = 100250012, machine = 100250013, warrior = 100250014, roller = 100250015;
        WithMaterials(new EngineScenario(GoliathMaterial(mat), Monster(machine, MonsterFrame.Xyz, CardRace.Machine)), machine, mat, Warrior)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{machine}):IsHasEffect(EFFECT_INDESTRUCTABLE_EFFECT)~=nil").AssertPasses();
        WithMaterials(new EngineScenario(GoliathMaterial(mat), Monster(warrior, MonsterFrame.Xyz, CardRace.Warrior)), warrior, mat, Warrior)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{warrior}):IsHasEffect(EFFECT_INDESTRUCTABLE_EFFECT)==nil").AssertPasses();
        WithMaterials(new EngineScenario(RoadRoller(roller), Monster(machine, MonsterFrame.Xyz, CardRace.Machine)), machine, roller, Warrior).Card(Warrior, 1, "mzone")
            .Check("Duel.GetFirstMatchingCard(Card.IsCode,1,LOCATION_MZONE,0,nil,1001):GetDefense()==0").AssertPasses();
    }

    // ---------------------------------------------------------------- detach as a step, or destroy this card

    private static CardProject GustavRocket(long id) => Monster(id, MonsterFrame.Xyz, CardRace.Machine, 10, new EffectEvent
    {
        When = B("when.phase").With("whose", "your").With("phase", "end").With("optional", false),
        Limit = OncePerTurn.Soft,
        Do = [B(DetachAction.BlockId).With("otherwise", "destroy")],
    });

    [Fact]
    public void DetachOrDestroy_ReadsAsGustavRocket()
    {
        var p = GustavRocket(100250020);
        Assert.Equal("Once per turn, during your End Phase: Detach 1 material from this card or destroy this card.", Effects(p));
        var lua = Lua(p);
        Assert.Contains("if c:CheckRemoveOverlayCard(tp,1,REASON_EFFECT) and Duel.SelectYesNo(tp,aux.Stringid(id,0)) then", lua);
        Assert.Contains("Duel.Destroy(c,REASON_EFFECT)", lua);
    }

    [EngineFact]
    public void DetachOrDestroy_DetachesWhenItCan()
    {
        const long id = 100250021;
        WithMaterials(new EngineScenario(GustavRocket(id)), id, Warrior, Warrior).Check(Overlays(id, 1)).AssertPasses();
        new EngineScenario(GustavRocket(id)).Card(id, 0, "mzone")
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_GRAVE,0,1,nil,{id})").AssertPasses();
    }

    // ---------------------------------------------------------------- detach any number, then target that many

    private static CardProject FlyingLauncher(long id) => Monster(id, MonsterFrame.Xyz, CardRace.Machine, 10, new EffectEvent
    {
        When = B("when.ignition"),
        Cost = [B("cost.detach").With("amount", "any")],
        Do = [B("do.destroy").With("amount", "cost").With("target", true).With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field, CardType = FilterCardType.SpellTrap })],
    });

    [Fact]
    public void ThatMany_ReadsAndCompilesAsFlyingLauncher()
    {
        var p = FlyingLauncher(100250030);
        Assert.Equal("You can detach any number of materials from this card, then target that many Spells/Traps on the field; destroy them.", Effects(p));
        var lua = Lua(p);
        Assert.Contains("e:SetLabel(e:GetHandler():RemoveOverlayCard(tp,1,Duel.GetTargetCount(s.filter,tp,LOCATION_ONFIELD,LOCATION_ONFIELD,nil,e,tp),REASON_COST))", lua);
        Assert.Contains("local ct1=e:GetLabel()", lua);
        Assert.Contains("Duel.SelectTarget(tp,s.filter,tp,LOCATION_ONFIELD,LOCATION_ONFIELD,ct1,ct1,nil,e,tp)", lua);
        // Without a cost that counts, "that many" has nothing to count.
        p.Effects[0].Cost.Clear();
        Assert.True(ScriptCompiler.Compile(p).HasErrors);
    }

    [EngineFact]
    public void ThatMany_DestroysAsManyAsWereDetached()
    {
        const long id = 100250031;
        WithMaterials(new EngineScenario(FlyingLauncher(id)), id, Warrior, Warrior).Card(Spell, 1, "szone", 3).Activate(id).ExpectActivations(1)
            .Check(Overlays(id, 1)).Check("Duel.GetFieldGroupCount(1,LOCATION_SZONE,0)==2").AssertPasses();
    }

    // ---------------------------------------------------------------- 300 ATK for each material detached

    private static CardProject Thrush(long id) => Monster(id, MonsterFrame.Xyz, CardRace.WingedBeast, 1, new EffectEvent
    {
        When = B("when.ignition"),
        Cost = [B("cost.detach").With("amount", "any")],
        Do = [B("do.stats_value").With("cards", new CardFilter { ThisCard = true }).With("dir", "gain").With("stat", "atk").With("how", "per").With("amount", 300).With("value", "cost_count").With("duration", "always")],
    });

    [Fact]
    public void ForEachDetached_ReadsAndCompilesAsPromenadeThrush()
    {
        var p = Thrush(100250040);
        Assert.StartsWith("You can detach any number of materials from this card; this card gains 300 ATK for each material detached", Effects(p));
        var lua = Lua(p);
        Assert.Contains("e:SetLabel(e:GetHandler():RemoveOverlayCard(tp,1,e:GetHandler():GetOverlayCount(),REASON_COST))", lua);
        Assert.Contains("e:GetLabel()", lua);
    }

    [EngineFact]
    public void ForEachDetached_GainsForEachMaterial()
    {
        const long id = 100250041;
        WithMaterials(new EngineScenario(Thrush(id)), id, Warrior, Warrior).Activate(id).ExpectActivations(1)
            .Check($"Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{id}):GetAttack()==1300").AssertPasses();
    }

    // ---------------------------------------------------------------- if this card has a Dragon monster as material

    private static CardProject Voloferniges(long id) => Monster(id, MonsterFrame.Xyz, CardRace.Dragon, 7, new EffectEvent
    {
        When = B("when.ignition"),
        If = [B(HasMaterialCondition.BlockId).With("cards", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Dragon })],
        Do = [B("do.draw").With("count", 1)],
    });

    [Fact]
    public void HasMaterial_ReadsAndCompilesAsVoloferniges()
    {
        var p = Voloferniges(100250050);
        Assert.Equal("If this card has a Dragon monster as material: You can draw 1 card.", Effects(p));
        Assert.Contains("e:GetHandler():GetOverlayGroup():IsExists(s.matfilter,1,nil)", Lua(p));
        p.Effects[0].If[0].Values["has"] = "not";
        Assert.Equal("If this card does not have a Dragon monster as material: You can draw 1 card.", Effects(p));
        Assert.Contains("not e:GetHandler():GetOverlayGroup():IsExists(s.matfilter,1,nil)", Lua(p));
    }

    [EngineFact]
    public void HasMaterial_OnlyWithADragonUnderIt()
    {
        const long id = 100250051;
        WithMaterials(new EngineScenario(Voloferniges(id)), id, Dragon, Warrior).Card(Warrior, 0, "deck", 2).Activate(id).ExpectActivations(1).AssertPasses();
        WithMaterials(new EngineScenario(Voloferniges(id)), id, Warrior, Warrior).Card(Warrior, 0, "deck", 2).Activate(id).ExpectActivations(0).AssertPasses();
    }

    // ---------------------------------------------------------------- attach up to 1 card each from your GY and your opponent's GY

    private static BlockInstance Toddler(int count, string mode) => B("do.attach").With("count", count).With("mode", mode).With("each_zone", true)
        .With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.Graveyard });

    [Fact]
    public void OneEach_ReadsAsTantrumToddler()
    {
        var p = Monster(100250060, MonsterFrame.Xyz, CardRace.Fiend, 1, Ignition(Toddler(2, "up_to")));
        Assert.Equal("You can attach up to 1 card each from your GY and your opponent's GY to this card as material.", Effects(p));
        Assert.Contains("g:FilterCount(Card.IsControler,nil,tp)<=1 and g:FilterCount(Card.IsControler,nil,1-tp)<=1", Lua(p));
    }

    [EngineFact]
    public void OneEach_AttachesOneOfEachPlayers()
    {
        const long id = 100250061;
        new EngineScenario(Monster(id, MonsterFrame.Xyz, CardRace.Fiend, 1, Ignition(Toddler(2, "exact")))).Card(id, 0, "mzone").Card(Warrior, 0, "grave", 2).Card(Fiend, 1, "grave")
            .Activate(id).ExpectActivations(1)
            .Check(Overlays(id, 2)).Check("Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)==0").Check("Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==1").AssertPasses();
    }

    // ---------------------------------------------------------------- attacks on monsters up to the number of materials +1

    private static CardProject Liebe(long id) => Monster(id, MonsterFrame.Xyz, CardRace.Machine, 11, new EffectEvent
    {
        When = B("when.while_face_up"),
        Do = [B("passive.battle_ability").With("ability", "extra_monster_materials")],
    });

    [Fact]
    public void AttacksPerMaterial_ReadsAndCompilesAsJuggernautLiebe()
    {
        var p = Liebe(100250070);
        Assert.Equal("During each Battle Phase, this card can make attacks on monsters up to the number of materials it has +1.", Effects(p));
        var lua = Lua(p);
        Assert.Contains("e1:SetCode(EFFECT_EXTRA_ATTACK_MONSTER)", lua);
        Assert.Contains("return e:GetHandler():GetOverlayCount()", lua);
    }

    [EngineFact]
    public void AttacksPerMaterial_CountsTheMaterials()
    {
        const long id = 100250071;
        WithMaterials(new EngineScenario(Liebe(id)), id, Warrior, Warrior)
            .Check($"(function() local c=Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,{id}) local ae=c:IsHasEffect(EFFECT_EXTRA_ATTACK_MONSTER) return ae and ae:GetValue()(ae,c)==2 end)()")
            .AssertPasses();
    }
}
