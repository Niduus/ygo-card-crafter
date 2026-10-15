using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Validation;

namespace CardCrafter.Core.Tests;

public class CompilerTests
{
    [Fact]
    public void PotOfGreedClone_MatchesOfficialScriptShape()
    {
        var result = ScriptCompiler.Compile(Samples.PotOfGreedClone());

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        var lua = result.Lua;
        Assert.Contains("local s,id=c100000001,100000001", lua);
        Assert.Contains("e1:SetType(EFFECT_TYPE_ACTIVATE)", lua);
        Assert.Contains("e1:SetCode(EVENT_FREE_CHAIN)", lua);
        Assert.Contains("e1:SetCategory(CATEGORY_DRAW)", lua);
        Assert.Contains("if chk==0 then return Duel.IsPlayerCanDraw(tp,2) end", lua);
        Assert.Contains("Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,tp,2)", lua);
        Assert.Contains("Duel.Draw(tp,2,REASON_EFFECT)", lua);
        Assert.Empty(ScriptChecker.Check(lua));
    }

    [Fact]
    public void DragonCommander_CompilesCleanly()
    {
        var result = ScriptCompiler.Compile(Samples.DragonCommander());

        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Empty(ScriptChecker.Check(result.Lua));
        var lua = result.Lua;
        // Summon trigger registered for both Normal and Special Summons.
        Assert.Contains("e1:SetCode(EVENT_SUMMON_SUCCESS)", lua);
        Assert.Contains("e1b:SetCode(EVENT_SPSUMMON_SUCCESS)", lua);
        Assert.Contains("e1:SetCategory(CATEGORY_TOHAND+CATEGORY_SEARCH)", lua);
        // Each hard once-per-turn effect gets its own count code.
        Assert.Contains("e1:SetCountLimit(1,id)", lua);
        Assert.Contains("e2:SetCountLimit(1,id+100)", lua);
        // Targeting destroy with an "if you do" follow-up.
        Assert.Contains("EFFECT_FLAG_CARD_TARGET", lua);
        Assert.Contains("if chkc then return", lua);
        Assert.Matches(@"if r\d+>0 then\s+local r\d+=Duel\.Damage\(1-tp,500,REASON_EFFECT\)", lua);
        // The banish-self cost.
        Assert.Contains("e:GetHandler():IsAbleToRemoveAsCost()", lua);
        // Lasting effect with its condition.
        Assert.Contains("e3:SetType(EFFECT_TYPE_FIELD)", lua);
        Assert.Contains("e3:SetCondition(s.e3con)", lua);
        Assert.Contains("local tp=e:GetHandlerPlayer()", lua);
    }

    [Fact]
    public void SharedLimitGroup_SharesOneCountCode()
    {
        var p = Samples.DragonCommander();
        p.Effects[0].SharedLimitGroup = 1;
        p.Effects[1].SharedLimitGroup = 1;
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("e1:SetCountLimit(1,id)", lua);
        Assert.Contains("e2:SetCountLimit(1,id)", lua);
        Assert.DoesNotContain("id+100", lua);
    }

    [Fact]
    public void HardLimitOnSpellActivation_UsesOathCode()
    {
        var p = Samples.PotOfGreedClone();
        p.Effects[0].Limit = OncePerTurn.Hard;
        Assert.Contains("e1:SetCountLimit(1,id+EFFECT_COUNT_CODE_OATH)", ScriptCompiler.Compile(p).Lua);
    }

    [Fact]
    public void ContinuousSpellWithoutActivateEffect_GetsOne()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100000020, Name = "Banner", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
            Effects =
            [
                new EffectEvent
                {
                    When = new BlockInstance("when.while_face_up"),
                    Do = [new BlockInstance("passive.boost_cards")],
                },
            ],
        };
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics));
        Assert.Contains("e0:SetType(EFFECT_TYPE_ACTIVATE)", r.Lua);
        Assert.Contains("e1:SetRange(LOCATION_SZONE)", r.Lua);
    }

    [Fact]
    public void ThenLink_BreaksEffectInsideCondition()
    {
        var p = Samples.PotOfGreedClone();
        p.Effects[0].Do.Add(new BlockInstance("do.gain_lp").With("amount", 1000).Linked(ActionLink.Then));
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Matches(@"if r1>0 then\s+Duel\.BreakEffect\(\)\s+local r2=Duel\.Recover\(tp,1000,REASON_EFFECT\)\s+end", lua);
    }

    [Fact]
    public void TwoTargetingBlocks_IsAnError()
    {
        var p = Samples.DragonCommander();
        p.Effects[1].Do.Add(new BlockInstance("do.banish").With("target", true));
        var r = ScriptCompiler.Compile(p);
        Assert.Contains(r.Diagnostics, d => d.Severity == Severity.Error && d.EffectIndex == 2 && d.Message.Contains("Only one block"));
    }

    [Fact]
    public void TargetingTheDeck_IsAnError()
    {
        var p = Samples.PotOfGreedClone();
        p.Effects[0].Do = [new BlockInstance("do.add_to_hand").With("target", true)];
        var r = ScriptCompiler.Compile(p);
        Assert.Contains(r.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("can't be targeted"));
    }

    [Fact]
    public void BlockInTheWrongColumn_IsAnError()
    {
        var p = Samples.PotOfGreedClone();
        p.Effects[0].If.Add(new BlockInstance("do.draw"));
        var r = ScriptCompiler.Compile(p);
        Assert.Contains(r.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("belongs in DO"));
    }

    [Fact]
    public void PassiveBlockOnActivatedEffect_IsAnError()
    {
        var p = Samples.PotOfGreedClone();
        p.Effects[0].Do.Add(new BlockInstance("passive.untargetable"));
        Assert.True(ScriptCompiler.Compile(p).HasErrors);
    }

    [Fact]
    public void MissingTrigger_IsAnError()
    {
        var p = Samples.PotOfGreedClone();
        p.Effects[0].When = null;
        Assert.True(ScriptCompiler.Compile(p).HasErrors);
    }

    [Fact]
    public void MandatoryTrigger_AlwaysPassesActivationCheck()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100000030, Name = "Burner" },
            Effects =
            [
                new EffectEvent
                {
                    When = new BlockInstance("when.summoned").With("how", "normal").With("optional", false),
                    Do = [new BlockInstance("do.draw").With("count", 1)],
                },
            ],
        };
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("EFFECT_TYPE_TRIGGER_F", lua);
        Assert.Contains("if chk==0 then return true end", lua);
    }

    [Theory]
    [InlineData(SummonProcedureKind.Synchro, MonsterFrame.Synchro, "aux.AddSynchroProcedure(c,aux.Tuner(nil),aux.NonTuner(nil),1)")]
    [InlineData(SummonProcedureKind.Xyz, MonsterFrame.Xyz, "aux.AddXyzProcedure(c,nil,4,2)")]
    [InlineData(SummonProcedureKind.Link, MonsterFrame.Link, "aux.AddLinkProcedure(c,nil,2)")]
    public void ExtraDeckProcedures(SummonProcedureKind kind, MonsterFrame frame, string expected)
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100000040, Name = "Extra", Frame = frame, Level = 4, LinkMarkers = LinkMarker.Bottom | LinkMarker.Top },
            Procedure = new SummonProcedure { Kind = kind, MinMaterials = 2 },
        };
        if (kind == SummonProcedureKind.Synchro) p.Procedure.MinMaterials = 1;
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics));
        Assert.Contains("c:EnableReviveLimit()", r.Lua);
        Assert.Contains(expected, r.Lua);
        Assert.Empty(ScriptChecker.Check(r.Lua));
    }

    [Fact]
    public void FusionByCodes_UsesProcMix()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100000041, Name = "Fused", Frame = MonsterFrame.Fusion },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Fusion, FusionMaterialCodes = [89631139, 46986414] },
        };
        Assert.Contains("aux.AddFusionProcMix(c,true,true,89631139,46986414)", ScriptCompiler.Compile(p).Lua);
    }

    [Fact]
    public void FilterWithEveryCriterion_ProducesExpectedChecks()
    {
        var p = Samples.PotOfGreedClone();
        p.Effects[0].Do =
        [
            new BlockInstance("do.add_to_hand").With("cards", new CardFilter
            {
                Zones = Zone.Deck | Zone.Graveyard, CardType = FilterCardType.Monster, Race = CardRace.Dragon,
                Attribute = CardAttribute.Light, Archetype = 0xdd, LevelMin = 1, LevelMax = 4, AtkMax = 1500,
            }),
        ];
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains("c:IsType(TYPE_MONSTER) and c:IsSetCard(0xdd) and c:IsRace(RACE_DRAGON) and c:IsAttribute(ATTRIBUTE_LIGHT) and c:IsLevelAbove(1) and c:IsLevelBelow(4) and c:IsAttackBelow(1500) and c:IsAbleToHand()", lua);
        Assert.Contains("LOCATION_DECK+LOCATION_GRAVE", lua);
        Assert.Contains("aux.NecroValleyFilter(", lua);
        Assert.Empty(ScriptChecker.Check(lua));
    }

    [Fact]
    public void CustomLua_IsPassedThrough()
    {
        var p = Samples.PotOfGreedClone();
        p.UseCustomLua = true;
        p.CustomLua = "--hand written\nfunction c100000001.initial_effect(c) end\n";
        Assert.Equal(p.CustomLua, ScriptCompiler.Compile(p).Lua);
    }
}
