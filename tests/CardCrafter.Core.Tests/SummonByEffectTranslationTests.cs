using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>Official scripts of Summons by effect, Rank-Up-Magic and Pendulum effects, read into blocks and played.</summary>
public class SummonByEffectTranslationTests
{
    private static string Script(long id) => File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, $"c{id}.lua"));

    private static string Find(long code, int player, string loc) => $"Duel.GetFirstMatchingCard(Card.IsCode,{player},{loc},0,nil,{code})";
    private static string In(long code, string loc, int player = 0) => $"Duel.IsExistingMatchingCard(Card.IsCode,{player},{loc},0,1,nil,{code})";

    /// <summary>The translated effect whose DO blocks include <paramref name="block"/>; it must compile cleanly.</summary>
    private static EffectEvent Piece(string lua, long id, CardData card, string block)
    {
        var result = ScriptTranslator.Translate(lua, id, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(e => e.Do.Any(b => b.BlockId == block));
        Assert.True(effect is not null, string.Join("\n", result.Pieces.Select(p => $"{p.Piece?.Title}: {p.Reason}")));
        var compiled = ScriptCompiler.Compile(new CardProject { Card = card, Effects = [effect!] });
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        return effect!;
    }

    private static EffectEvent Piece(long id, CardData card, string block) => Piece(Script(id), id, card, block);

    private static CardProject Xyz(long id, int rank, int[]? archetypes = null) => new()
    {
        Card = new CardData { Id = id, Name = "Rank " + rank, Frame = MonsterFrame.Xyz, Level = rank, Atk = 2000, Def = 2000, Archetypes = [.. archetypes ?? []] },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2 },
    };

    private static CardData PendulumCard(long id, int scale, int[]? archetypes = null) => new()
    {
        Id = id, Name = "Translated " + id, Frame = MonsterFrame.Effect, Abilities = MonsterAbility.Pendulum, Level = 4, LeftScale = scale, RightScale = scale,
        Atk = 1000, Def = 1000, Archetypes = [.. archetypes ?? []],
    };

    private static string ToPendulumZone(long code) => $"\tDuel.MoveToField(find({code},LOCATION_HAND,tp),tp,tp,LOCATION_PZONE,POS_FACEUP,true)";

    [EngineFact]
    public void WonderXyz_IsTheExtraDeckSummonBlock_AndPlays()
    {
        var card = new CardData { Id = 100171001, Name = "Wonder", Kind = CardKind.Spell };
        var effect = Piece(73860462, card, ExtraDeckSummonAction.BlockId);
        Assert.Equal("xyz", effect.Do.Single().Values["kind"]);
        var spell = new CardProject { Card = card, Effects = [effect] };
        var rank4 = Xyz(100171002, 4);
        new EngineScenario(spell, rank4).Card(Warrior, 0, "mzone", 2).Card(rank4.Card.Id, 0, "extra").Card(card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Activate(card.Id).ExpectActivations(1).Check(In(rank4.Card.Id, "LOCATION_MZONE")).AssertPasses();
    }

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void RankUpMagicRaidForce_RanksUpIntoARaidraptor(bool raidraptor)
    {
        var card = new CardData { Id = 100171011, Name = "Raid", Kind = CardKind.Spell };
        var effect = Piece(41201386, card, RankUpAction.BlockId);
        var rum = effect.Do.Single();
        Assert.Equal(("up", "1", 0xba), (rum.Values["rank"], rum.Values["ranks"], rum.Filters["cards"].Archetype));
        var spell = new CardProject { Card = card, Effects = [effect] };
        var rank4 = Xyz(100171012, 4);
        var rank5 = Xyz(100171013, 5, raidraptor ? [0xba] : []);
        new EngineScenario(spell, rank4, rank5).Card(Warrior, 0, "mzone", 2).Card(rank4.Card.Id, 0, "extra").Card(rank5.Card.Id, 0, "extra").Card(card.Id, 0, "hand")
            .Card(Warrior, 0, "deck", 5).SpSummon(rank4.Card.Id).Activate(card.Id).ExpectActivations(raidraptor ? 1 : 0)
            .Check(raidraptor ? $"{Find(rank5.Card.Id, 0, "LOCATION_MZONE")}:GetOverlayCount()==3" : In(rank4.Card.Id, "LOCATION_MZONE")).AssertPasses();
    }

    /// <summary>Rank-Up-Magic Raptor's Force's activation, without its "destroyed this turn" condition.</summary>
    private const string RaptorsForce = """
        local s,id=GetID()
        function s.initial_effect(c)
        	local e1=Effect.CreateEffect(c)
        	e1:SetCategory(CATEGORY_SPECIAL_SUMMON)
        	e1:SetType(EFFECT_TYPE_ACTIVATE)
        	e1:SetCode(EVENT_FREE_CHAIN)
        	e1:SetProperty(EFFECT_FLAG_CARD_TARGET)
        	e1:SetTarget(s.target)
        	e1:SetOperation(s.activate)
        	c:RegisterEffect(e1)
        end
        function s.filter1(c,e,tp)
        	return c:IsSetCard(0xba) and c:IsType(TYPE_XYZ) and c:IsCanBeSpecialSummoned(e,0,tp,false,false)
        		and aux.MustMaterialCheck(c,tp,EFFECT_MUST_BE_XMATERIAL)
        		and Duel.IsExistingMatchingCard(s.filter2,tp,LOCATION_EXTRA,0,1,nil,e,tp,c,c:GetRank()+1)
        end
        function s.filter2(c,e,tp,mc,rk)
        	return c:IsRank(rk) and c:IsSetCard(0xba) and mc:IsCanBeXyzMaterial(c)
        		and c:IsCanBeSpecialSummoned(e,SUMMON_TYPE_XYZ,tp,false,false) and Duel.GetLocationCountFromEx(tp,tp,mc,c)>0
        end
        function s.target(e,tp,eg,ep,ev,re,r,rp,chk,chkc)
        	if chkc then return chkc:IsControler(tp) and chkc:IsLocation(LOCATION_GRAVE) and s.filter1(chkc,e,tp) end
        	if chk==0 then return Duel.IsPlayerCanSpecialSummonCount(tp,2)
        		and Duel.GetLocationCount(tp,LOCATION_MZONE)>0
        		and Duel.IsExistingTarget(s.filter1,tp,LOCATION_GRAVE,0,1,nil,e,tp) end
        	Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)
        	local g=Duel.SelectTarget(tp,s.filter1,tp,LOCATION_GRAVE,0,1,1,nil,e,tp)
        	Duel.SetOperationInfo(0,CATEGORY_SPECIAL_SUMMON,g,2,tp,LOCATION_EXTRA)
        end
        function s.activate(e,tp,eg,ep,ev,re,r,rp)
        	if Duel.GetLocationCount(tp,LOCATION_MZONE)<=0 then return end
        	local tc=Duel.GetFirstTarget()
        	if not tc:IsRelateToEffect(e) or tc:IsImmuneToEffect(e) then return end
        	if Duel.SpecialSummon(tc,0,tp,tp,false,false,POS_FACEUP)==0 then return end
        	if not aux.MustMaterialCheck(tc,tp,EFFECT_MUST_BE_XMATERIAL) then return end
        	Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_SPSUMMON)
        	local g=Duel.SelectMatchingCard(tp,s.filter2,tp,LOCATION_EXTRA,0,1,1,nil,e,tp,tc,tc:GetRank()+1)
        	local sc=g:GetFirst()
        	if sc then
        		Duel.BreakEffect()
        		sc:SetMaterial(Group.FromCards(tc))
        		Duel.Overlay(sc,Group.FromCards(tc))
        		Duel.SpecialSummon(sc,SUMMON_TYPE_XYZ,tp,tp,false,false,POS_FACEUP)
        		sc:CompleteProcedure()
        	end
        end
        """;

    [EngineFact]
    public void RaptorsForceShape_IsTheRankUpFromTheGY_AndPlays()
    {
        var card = new CardData { Id = 100171021, Name = "Raptor", Kind = CardKind.Spell };
        var effect = Piece(RaptorsForce, 100171021, card, RankUpAction.BlockId);
        var rum = effect.Do.Single();
        Assert.Equal((Zone.Graveyard, 0xba, 0xba), (rum.Filters["material"].Zones, rum.Filters["material"].Archetype, rum.Filters["cards"].Archetype));
        var rank4 = Xyz(100171022, 4, [0xba]);
        var rank5 = Xyz(100171023, 5, [0xba]);
        new EngineScenario(new CardProject { Card = card, Effects = [effect] }, rank4, rank5).Card(rank4.Card.Id, 0, "grave").Card(rank5.Card.Id, 0, "extra").Card(card.Id, 0, "hand")
            .Card(Warrior, 0, "deck", 5).Setup($"{Find(rank4.Card.Id, 0, "LOCATION_GRAVE")}:CompleteProcedure()").Activate(card.Id).ExpectActivations(1)
            .Check($"{Find(rank5.Card.Id, 0, "LOCATION_MZONE")}:GetOverlayCount()==1").AssertPasses();
        // Without its "Number C88" check, a Rank-Up into any "Number C" monster isn't read: the block always makes that check.
        var noC88 = RaptorsForce.Replace("c:IsRank(rk) and c:IsSetCard(0xba)", "c:IsRank(rk) and c:IsSetCard(0x1048)", StringComparison.Ordinal);
        Assert.False(ScriptTranslator.Translate(noC88, 100171021, card).Complete);
        // A Rank-Up that also does something else stays script: Rank-Up-Magic Zexal Force puts a card on top of the Deck.
        Assert.DoesNotContain(ScriptTranslator.Translate(Script(36224040), 36224040, card).Pieces.SelectMany(p => p.Effects), e => e.Do.Any(b => b.BlockId == RankUpAction.BlockId));
    }

    [EngineTheory]
    [InlineData(3, 1)]
    [InlineData(12, 0)]
    public void PerformapalTrumpanda_IncreasesItsScale(int scale, int activations)
    {
        var card = PendulumCard(100171031, scale);
        var effect = Piece(32787239, card, PendulumScaleAction.BlockId);
        var b = effect.Do.Single();
        Assert.Equal(("increase", "1", "12", "always"), (b.Values["mode"], b.Values["amount"], b.Values["limit"], b.Values["duration"]));
        Assert.Equal("Once per turn: You can increase this card's Pendulum Scale by 1 (max. 12).", EffectTextGenerator.Describe(effect, card));
        new EngineScenario(new CardProject { Card = card, Effects = [effect] }, RequirementMatrix.RiggerCard(ToPendulumZone(card.Id)))
            .Card(card.Id, 0, "hand").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "deck", 5)
            .Activate(card.Id).ExpectActivations(activations).Check($"{Find(card.Id, 0, "LOCATION_PZONE")}:GetRightScale()=={scale + activations}").AssertPasses();
    }

    [EngineFact]
    public void PerformapalParrotrio_MakesItsScaleBecome7_IfItHasAPartner()
    {
        var card = PendulumCard(100171041, 2);
        var effect = Piece(56675280, card, PendulumScaleAction.BlockId);
        var b = effect.Do.Single();
        Assert.Equal(("becomes", "7", "turn"), (b.Values["mode"], b.Values["amount"], b.Values["duration"]));
        var partner = effect.If.Single().Filters["cards"];
        Assert.Equal((Zone.PendulumZone, true), (partner.Zones, partner.ExcludeThisCard));
        Assert.StartsWith("Once per turn, if you have a ", EffectTextGenerator.Describe(effect, card), StringComparison.Ordinal);
        Assert.EndsWith("card in your other Pendulum Zone, except cards with this card's name: You can make this card's Pendulum Scale become 7 until the end of this turn.",
            EffectTextGenerator.Describe(effect, card), StringComparison.Ordinal);
    }

    [EngineTheory]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    public void StargazerMagician_ScaleBecomes4_UnlessItHasAMagicianPartner(bool partner, int scale)
    {
        var card = PendulumCard(100171051, 1);
        var effect = Piece(94415058, card, PendulumScalePassive.BlockId);
        Assert.Equal(("becomes", "4", "if.no_cards"), (effect.Do.Single().Values["mode"], effect.Do.Single().Values["amount"], effect.If.Single().BlockId));
        var magician = new CardProject { Card = PendulumCard(100171052, 8, [0x98]) };
        var rig = ToPendulumZone(card.Id) + (partner ? "\n" + ToPendulumZone(magician.Card.Id) : "");
        new EngineScenario(new CardProject { Card = card, Effects = [effect] }, magician, RequirementMatrix.RiggerCard(rig))
            .Card(card.Id, 0, "hand").Card(magician.Card.Id, 0, "hand").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "deck", 5)
            .Check($"{Find(card.Id, 0, "LOCATION_PZONE")}:GetLeftScale()=={scale}").AssertPasses();
    }

    [EngineFact]
    public void ExtraPendulum_IsTheExtraPendulumBlock_FromTheExtraDeck()
    {
        var card = new CardData { Id = 100171061, Name = "Extra", Kind = CardKind.Spell };
        var result = ScriptTranslator.Translate(Script(58308221), 58308221, card);
        Assert.True(result.Complete);
        var b = result.Pieces.Single().Effect!.Do.Single();
        Assert.Equal((ExtraPendulumAction.BlockId, "extra"), (b.BlockId, b.Values["from"]));
    }

    [EngineFact]
    public void PendulumMonsters_PlaceThemselvesInThePendulumZone()
    {
        // Supreme King Z-ARC: "If this card in the Monster Zone is destroyed"; Clear Wing Fast Dragon: "... by battle or card effect".
        foreach (var (id, by) in new[] { (13331639L, "in_mzone"), (90036274L, "in_mzone_battle_effect") })
        {
            var effect = Piece(id, PendulumCard(100171071, 1), PlaceAction.BlockId);
            Assert.Equal(("when.destroyed", by), (effect.When!.BlockId, effect.When.Values["by"]));
            Assert.True(effect.Do.Single().Filters["cards"].ThisCard);
            Assert.Equal("pzone", effect.Do.Single().Values["where"]);
        }
        // Amazoness Silver Sword Master gives up when the Pendulum Zones fill before it resolves, which the block doesn't: it stays script.
        var amazoness = ScriptTranslator.Translate(Script(24087580), 24087580, PendulumCard(100171072, 1));
        Assert.DoesNotContain(amazoness.Pieces.SelectMany(p => p.Effects), e => e.Do.Any(b => b.BlockId == PlaceAction.BlockId));
    }
}
