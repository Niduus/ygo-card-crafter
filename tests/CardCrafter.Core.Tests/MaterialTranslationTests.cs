using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Official scripts read back into the Card tab's procedures and the Xyz blocks: materials checked as a group
/// (Apollousa, Aleister the Invoker of Madness), "with the same Rank" (Number F0), a Link-1 monster as a Level 1 Tuner
/// (Yummy Way), the other way to Xyz Summon by discarding (Gustav Rocket) or after an activation (Kashtira Arise-Heart),
/// "Must be Special Summoned by a card effect" (Machina Citadel), a procedure in Attack Position (Jester Confit) and
/// attacks up to the number of materials +1 (Juggernaut Liebe).
/// </summary>
public class MaterialTranslationTests
{
    private static string? Script(long id) =>
        EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{id}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{id}.lua")) : null;

    private static TranslationResult? Translate(long id, CardData card) => Script(id) is { } lua ? ScriptTranslator.Translate(lua, id, card) : null;

    private static CardData Card(long id, MonsterFrame frame, int level, CardRace race = CardRace.Warrior, LinkMarker markers = LinkMarker.None, MonsterAbility abilities = MonsterAbility.None) =>
        new() { Id = id, Name = "Card " + id, Frame = frame, Level = level, Atk = 2000, Def = 2000, Race = race, LinkMarkers = markers, Abilities = abilities };

    private static SummonProcedure ProcedureOf(TranslationResult r)
    {
        var piece = Assert.Single(r.Pieces, p => p.Procedure is not null);
        return piece.Procedure!;
    }

    [EngineFact]
    public void Apollousa_DifferentNamesExceptTokens()
    {
        var r = Translate(4280258, Card(4280258, MonsterFrame.Link, 0, CardRace.Fairy, LinkMarker.Top | LinkMarker.Bottom | LinkMarker.Left | LinkMarker.Right));
        if (r is null) return;
        var proc = ProcedureOf(r);
        Assert.Equal(MaterialGroupRule.DifferentNames, proc.MaterialRule);
        Assert.Equal(MonsterKind.NonToken, proc.MaterialFilter.MonsterKind);
        Assert.Equal((2, 99), (proc.MinMaterials, proc.MaxMaterials));
        var p = new CardProject { Card = Card(4280258, MonsterFrame.Link, 0, markers: LinkMarker.Top | LinkMarker.Bottom | LinkMarker.Left | LinkMarker.Right), Procedure = proc };
        Assert.Equal("2+ monsters with different names, except Tokens", MaterialText.Describe(p));
    }

    [EngineFact]
    public void AleisterOfMadness_DifferentTypesAndAttributes()
    {
        var r = Translate(97973962, Card(97973962, MonsterFrame.Link, 0, CardRace.Spellcaster, LinkMarker.Top | LinkMarker.Bottom));
        if (r is null) return;
        var proc = ProcedureOf(r);
        Assert.Equal(MaterialGroupRule.DifferentTypesAndAttributes, proc.MaterialRule);
        Assert.Equal((2, 2), (proc.MinMaterials, proc.MaxMaterials));
    }

    [EngineFact]
    public void UtopicFuture_SameRankExceptNumbers_AndDracoFutureOnTopOfIt()
    {
        var r = Translate(65305468, Card(65305468, MonsterFrame.Xyz, 0));
        if (r is null) return;
        var proc = ProcedureOf(r);
        Assert.True(proc.XyzSameRank);
        Assert.Equal(MonsterKind.Xyz, proc.MaterialFilter.MonsterKind);
        Assert.Contains(proc.MaterialFilter.Requirements, q => q is { Property: "archetype", Op: RequirementOp.IsNot } && q.Values.SequenceEqual([0x48L]));
        Assert.Equal(2, proc.MinMaterials);

        var draco = ProcedureOf(Translate(26973555, Card(26973555, MonsterFrame.Xyz, 0))!);
        Assert.True(draco is { XyzSameRank: true, XyzAlternate: true, MinMaterials: 3 });
        Assert.Equal(65305468, draco.XyzAlternateFilter.CardCode);
    }

    [EngineFact]
    public void YummyWay_LinkOneAsALevelOneTuner()
    {
        var r = Translate(93192592, Card(93192592, MonsterFrame.Synchro, 2));
        if (r is null) return;
        var proc = ProcedureOf(r);
        Assert.True(proc is { SynchroLinkAsTuner: true, MinMaterials: 1, MaxMaterials: 1 });
        // The Level the Link-1 monster has is the procedure's own effect.
        Assert.Contains(r.Pieces, p => p.Ignored && p.Piece is not null && p.Piece.Code.Contains("EFFECT_SYNCHRO_LEVEL_EX", StringComparison.Ordinal));
    }

    [EngineFact]
    public void GustavRocket_DiscardsForTheOtherWay()
    {
        var r = Translate(92359409, Card(92359409, MonsterFrame.Xyz, 10, CardRace.Machine));
        if (r is null) return;
        var proc = ProcedureOf(r);
        Assert.True(proc is { XyzAlternate: true, XyzAlternateOncePerTurn: true, XyzAlternateDiscard: 1, MinMaterials: 3 });
        Assert.Equal(56910167, proc.XyzAlternateFilter.CardCode);
    }

    [EngineFact]
    public void AriseHeart_AfterShangriIraWasActivated()
    {
        var r = Translate(48626373, Card(48626373, MonsterFrame.Xyz, 7));
        if (r is null) return;
        var proc = ProcedureOf(r);
        Assert.True(proc is { XyzAlternate: true, XyzAlternateOncePerTurn: true, XyzAlternateAfterActivationOf: 73542331 });
    }

    [EngineFact]
    public void MachinaCitadel_MustBeSpecialSummonedByACardEffect()
    {
        var card = Card(87074380, MonsterFrame.Effect, 10, CardRace.Machine, abilities: MonsterAbility.SpecialSummonOnly);
        var r = Translate(87074380, card);
        if (r is null) return;
        Assert.Contains(r.Pieces, p => p.Rule == SummonRule.ByCardEffect);
    }

    [EngineFact]
    public void JesterConfit_ProcedureInAttackPosition()
    {
        var card = Card(8487449, MonsterFrame.Effect, 1, CardRace.Spellcaster);
        var r = Translate(8487449, card);
        if (r is null) return;
        var proc = Assert.Single(r.Pieces, p => p.Effect?.When?.BlockId == Blocks.SummonProcedureTrigger.BlockId).Effect!;
        Assert.Equal("attack", proc.When!.Values["position"]);
        var p = new CardProject { Card = card, Effects = [proc] };
        Assert.Equal("You can Special Summon this card (from your hand) in Attack Position.", EffectTextGenerator.Describe(proc, card));
        Assert.Contains("e1:SetTargetRange(POS_FACEUP_ATTACK,0)", ScriptCompiler.Compile(p).Lua);
    }

    [EngineFact]
    public void InfinitrackGoliath_TheEffectAnXyzMonsterGains()
    {
        var card = Card(23689428, MonsterFrame.Link, 0, CardRace.Machine, LinkMarker.Bottom);
        var r = Translate(23689428, card);
        if (r is null) return;
        var gained = Assert.Single(r.Pieces, p => Blocks.WhileFaceUpTrigger.ForXyz(p.Effect?.When)).Effect!;
        Assert.Equal("passive.indestructible", Assert.Single(gained.Do).BlockId);
        Assert.Equal("An Xyz Monster whose original Type is Machine and has this card as material gains this effect.\n ● This card cannot be destroyed by card effects.",
            EffectTextGenerator.Describe(gained, card));
        var lua = ScriptCompiler.Compile(new CardProject { Card = card, Effects = [gained] }).Lua;
        Assert.Contains("SetType(EFFECT_TYPE_XMATERIAL)", lua);
    }

    [EngineFact]
    public void JuggernautLiebe_AttacksUpToTheMaterialsPlusOne()
    {
        var r = Translate(26096328, Card(26096328, MonsterFrame.Xyz, 11, CardRace.Machine));
        if (r is null) return;
        Assert.Contains(r.Pieces, p => p.Effect?.Do.Any(b => b.BlockId == "passive.battle_ability" && b.Values.GetValueOrDefault("ability") == "extra_monster_materials") == true);
    }
}
