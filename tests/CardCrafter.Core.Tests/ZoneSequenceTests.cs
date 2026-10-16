using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.Core.Tests;

/// <summary>"c:GetSequence()&lt;5": not the Extra Monster Zone in the Monster Zones, and not the Field Zone in the Spell &amp; Trap Zones.</summary>
public class ZoneSequenceTests
{
    private static TranslationResult Destroy(string location) => ScriptTranslator.Translate($$"""
        function c100097300.initial_effect(c)
        	local e1=Effect.CreateEffect(c)
        	e1:SetCategory(CATEGORY_DESTROY)
        	e1:SetType(EFFECT_TYPE_IGNITION)
        	e1:SetRange(LOCATION_MZONE)
        	e1:SetTarget(c100097300.tg)
        	e1:SetOperation(c100097300.op)
        	c:RegisterEffect(e1)
        end
        function c100097300.filter(c)
        	return c:GetSequence()<5
        end
        function c100097300.tg(e,tp,eg,ep,ev,re,r,rp,chk)
        	if chk==0 then return Duel.IsExistingMatchingCard(c100097300.filter,tp,0,{{location}},1,nil) end
        	local g=Duel.GetMatchingGroup(c100097300.filter,tp,0,{{location}},nil)
        	Duel.SetOperationInfo(0,CATEGORY_DESTROY,g,1,0,0)
        end
        function c100097300.op(e,tp,eg,ep,ev,re,r,rp)
        	Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_DESTROY)
        	local g=Duel.SelectMatchingCard(tp,c100097300.filter,tp,0,{{location}},1,1,nil)
        	if #g>0 then
        		Duel.HintSelection(g)
        		Duel.Destroy(g,REASON_EFFECT)
        	end
        end
        """, 100097300, new CardData { Id = 100097300, Name = "Zone Tester", Frame = MonsterFrame.Effect, Level = 4 });

    private static List<CardRequirement> Requirements(TranslationResult r)
    {
        Assert.True(r.Complete, string.Join("\n", ScriptTranslator.Untranslated(r)));
        return r.Pieces.SelectMany(p => p.Effects).SelectMany(e => e.Do).SelectMany(b => b.Filters.Values).Single().Requirements;
    }

    [Fact]
    public void InTheMonsterZones_ItIsNotTheExtraMonsterZone()
    {
        var req = Assert.Single(Requirements(Destroy("LOCATION_MZONE")));
        Assert.Equal(("extra_zone", RequirementOp.IsNot), (req.Property, req.Op));
    }

    [Fact]
    public void InTheSpellAndTrapZones_ItIsNotAFieldSpell()
    {
        var req = Assert.Single(Requirements(Destroy("LOCATION_SZONE")));
        Assert.Equal(("card_type", RequirementOp.IsNot), (req.Property, req.Op));
        Assert.Equal([0x80000L], req.Values);
    }

    [Fact]
    public void OnTheWholeField_ItKeepsBothOut()
    {
        var req = Assert.Single(Requirements(Destroy("LOCATION_ONFIELD")));
        Assert.Equal("main_zone", req.Property);
        Assert.Contains("not in the Extra Monster Zone or the Field Zone", FilterText.Describe(new CardFilter { Zones = Zone.Field, Requirements = [req] }, null));
    }

    [Fact]
    public void InTheGy_ItStaysScript() => Assert.False(Destroy("LOCATION_GRAVE").Complete);
}
