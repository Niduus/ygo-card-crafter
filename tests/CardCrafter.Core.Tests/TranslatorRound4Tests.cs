using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The fourth round of translations: triggers on other cards ("If a Dragon monster is Special Summoned to your field"),
/// Synchro/Xyz/Link/Fusion material lines, card-wide rules and the chain checks the Negate block writes itself.
/// </summary>
public class TranslatorRound4Tests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private const string DragonDrawScript = """
        --Test
        local s,id=GetID()
        function s.initial_effect(c)
        	c:EnableReviveLimit()
        	aux.AddSynchroProcedure(c,nil,aux.NonTuner(Card.IsAttribute,ATTRIBUTE_FIRE),1)
        	c:SetUniqueOnField(1,0,id)
        	local e1=Effect.CreateEffect(c)
        	e1:SetCategory(CATEGORY_DRAW)
        	e1:SetType(EFFECT_TYPE_FIELD+EFFECT_TYPE_TRIGGER_O)
        	e1:SetCode(EVENT_SPSUMMON_SUCCESS)
        	e1:SetProperty(EFFECT_FLAG_DELAY)
        	e1:SetRange(LOCATION_MZONE)
        	e1:SetCondition(s.drcon)
        	e1:SetTarget(s.drtg)
        	e1:SetOperation(s.drop)
        	c:RegisterEffect(e1)
        end
        function s.cfilter(c,tp)
        	return c:IsFaceup() and c:IsControler(tp) and c:IsRace(RACE_DRAGON)
        end
        function s.drcon(e,tp,eg,ep,ev,re,r,rp)
        	return eg:IsExists(s.cfilter,1,nil,tp)
        end
        function s.drtg(e,tp,eg,ep,ev,re,r,rp,chk)
        	if chk==0 then return Duel.IsPlayerCanDraw(tp,1) end
        	Duel.SetTargetPlayer(tp)
        	Duel.SetTargetParam(1)
        	Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,tp,1)
        end
        function s.drop(e,tp,eg,ep,ev,re,r,rp)
        	local p,d=Duel.GetChainInfo(0,CHAININFO_TARGET_PLAYER,CHAININFO_TARGET_PARAM)
        	Duel.Draw(p,d,REASON_EFFECT)
        end
        """;

    private static CardData Synchro(long id) => new() { Id = id, Name = "Test Synchro", Frame = MonsterFrame.Synchro, Level = 6, Atk = 2400, Def = 1000 };

    [Fact]
    public void FieldTrigger_OnMatchingCards_BecomesTheCardsEventBlock()
    {
        var result = ScriptTranslator.Translate(DragonDrawScript, 100006000, Synchro(100006000));
        Assert.True(result.Complete, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var effect = result.Pieces.SelectMany(p => p.Effects).Single();
        Assert.Equal(CardsEventTrigger.BlockId, effect.When!.BlockId);
        Assert.Equal("special", effect.When.Values["event"]);
        var cards = effect.When.Filters["cards"];
        Assert.Equal(CardOwner.You, cards.Owner);
        Assert.Equal(CardRace.Dragon, cards.Race);
        Assert.True(cards.FaceUpOnly);
        var text = EffectTextGenerator.Describe(effect, Synchro(100006000));
        Assert.StartsWith("If a face-up Dragon monster(s) is Special Summoned to your field: You can draw 1 card", text);
    }

    [Fact]
    public void SynchroMaterialLine_BecomesTheSummoningConditions()
    {
        var result = ScriptTranslator.Translate(DragonDrawScript, 100006000, Synchro(100006000));
        var proc = result.Pieces.Select(p => p.Procedure).Single(p => p is not null)!;
        Assert.Equal(SummonProcedureKind.Synchro, proc.Kind);
        Assert.Equal(CardAttribute.Fire, proc.MaterialFilter.Attribute);
        Assert.Equal(1, proc.MinMaterials);
        Assert.Equal("1 Tuner + 1+ non-Tuner FIRE monsters", MaterialText.Describe(new CardProject { Card = Synchro(1), Procedure = proc }, null));
    }

    [Fact]
    public void UniqueOnField_AndSpecialSummonOnce_AreCardRules()
    {
        var project = new CardProject { Card = Synchro(100006000), UseCustomLua = true, CustomLua = DragonDrawScript };
        ScriptTranslator.ApplyTo(project, null, out var applied);
        Assert.True(applied);
        Assert.True(project.Card.OnlyOneOnField);
        var compiled = ScriptCompiler.Compile(project);
        Assert.Contains("c:SetUniqueOnField(1,0,id)", compiled.Lua);
        Assert.StartsWith("You can only control 1 \"Test Synchro\".", EffectTextGenerator.Generate(project).Split('\n').Last());

        project.Card.SpecialSummonOncePerTurn = true;
        Assert.Contains("c:SetSPSummonOnce(id)", ScriptCompiler.Compile(project).Lua);
        Assert.Contains("You can only Special Summon \"Test Synchro\" once per turn.", EffectTextGenerator.Generate(project));
    }

    [Theory]
    [InlineData("aux.AddXyzProcedure(c,aux.FilterBoolFunction(Card.IsRace,RACE_MACHINE),4,2)", SummonProcedureKind.Xyz, "2 Level 4 Machine monsters")]
    [InlineData("aux.AddLinkProcedure(c,aux.FilterBoolFunction(Card.IsLinkType,TYPE_EFFECT),2,2)", SummonProcedureKind.Link, "2 Effect Monsters")]
    [InlineData("aux.AddFusionProcFunRep(c,aux.FilterBoolFunction(Card.IsFusionAttribute,ATTRIBUTE_DARK),3,true)", SummonProcedureKind.Fusion, "3 DARK monsters")]
    public void MaterialLines_Translate(string line, SummonProcedureKind kind, string text)
    {
        var frame = kind switch { SummonProcedureKind.Xyz => MonsterFrame.Xyz, SummonProcedureKind.Link => MonsterFrame.Link, _ => MonsterFrame.Fusion };
        var card = new CardData { Id = 100006010, Name = "M", Frame = frame, Level = 4, LinkMarkers = LinkMarker.Left | LinkMarker.Right };
        var lua = $"local s,id=GetID()\nfunction s.initial_effect(c)\n\tc:EnableReviveLimit()\n\t{line}\nend\n";
        var result = ScriptTranslator.Translate(lua, 100006010, card);
        Assert.True(result.Complete, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var proc = result.Pieces.Select(p => p.Procedure).Single(p => p is not null)!;
        Assert.Equal(kind, proc.Kind);
        Assert.Equal(text, MaterialText.Describe(new CardProject { Card = card, Procedure = proc }, null));
        Assert.False(ScriptCompiler.Compile(new CardProject { Card = card, Procedure = proc }).HasErrors);
    }

    [Fact]
    public void QuickNegate_LeavesOutTheChecksTheNegateBlockWrites()
    {
        const string lua = """
            local s,id=GetID()
            function s.initial_effect(c)
            	local e1=Effect.CreateEffect(c)
            	e1:SetCategory(CATEGORY_NEGATE)
            	e1:SetType(EFFECT_TYPE_QUICK_O)
            	e1:SetCode(EVENT_CHAINING)
            	e1:SetRange(LOCATION_MZONE)
            	e1:SetCountLimit(1)
            	e1:SetCondition(s.negcon)
            	e1:SetTarget(s.negtg)
            	e1:SetOperation(s.negop)
            	c:RegisterEffect(e1)
            end
            function s.negcon(e,tp,eg,ep,ev,re,r,rp)
            	return not e:GetHandler():IsStatus(STATUS_BATTLE_DESTROYED) and rp==1-tp and re:IsActiveType(TYPE_MONSTER) and Duel.IsChainNegatable(ev)
            end
            function s.negtg(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return true end
            	Duel.SetOperationInfo(0,CATEGORY_NEGATE,eg,1,0,0)
            end
            function s.negop(e,tp,eg,ep,ev,re,r,rp)
            	Duel.NegateActivation(ev)
            end
            """;
        var card = new CardData { Id = 100006020, Name = "N", Level = 4 };
        var result = ScriptTranslator.Translate(lua, 100006020, card);
        Assert.True(result.Complete, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var effect = result.Pieces.Single().Effect!;
        Assert.Equal("when.quick", effect.When!.BlockId);
        Assert.Equal("chain_monster", effect.When.Values["timing"]);
        Assert.Empty(effect.If);
        var compiled = ScriptCompiler.Compile(new CardProject { Card = card, Effects = [effect] });
        Assert.Contains("not e:GetHandler():IsStatus(STATUS_BATTLE_DESTROYED)", compiled.Lua);
    }

    [Fact]
    public void CardsEvent_DescribesEachEvent()
    {
        string Text(string ev, CardOwner owner, bool other = false)
        {
            var when = B(CardsEventTrigger.BlockId, ("event", ev));
            when.Filters["cards"] = new CardFilter { Owner = owner, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Archetype = 0, ExcludeThisCard = other };
            return EffectTextGenerator.Describe(new EffectEvent { When = when, Do = [B("do.draw")] }, new CardData { Name = "X" });
        }
        Assert.StartsWith("If a monster(s) is Normal or Special Summoned to your opponent's field:", Text("summoned", CardOwner.Opponent));
        Assert.StartsWith("If a monster(s) you control is destroyed by battle or card effect:", Text("destroyed_any", CardOwner.You));
        Assert.StartsWith("If a monster(s) is sent to your GY:", Text("to_gy", CardOwner.You));
        Assert.StartsWith("If another monster(s) is Ritual Summoned:", Text("ritual", CardOwner.Either, other: true));
    }

    [EngineFact]
    public void CardsEvent_DrawsWhenAMonsterIsSpecialSummonedToYourField()
    {
        var watcher = new CardProject
        {
            Card = new CardData { Id = 100006030, Name = "Watcher", Level = 4, Atk = 1000 },
            Effects = [new EffectEvent { When = B(CardsEventTrigger.BlockId, ("event", "special"), ("zone", "monster_zone")), Do = [B("do.draw")] }],
        };
        var summoner = new CardProject
        {
            Card = new CardData { Id = 100006031, Name = "Summoner", Level = 4 },
            Effects = [new EffectEvent { When = B("when.summon_procedure", ("zone", "hand")) }],
        };
        new EngineScenario(watcher, summoner).Card(watcher.Card.Id, 0, "mzone").Card(summoner.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .SpSummon(summoner.Card.Id).Activate(watcher.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1")
            .AssertPasses();
    }

    [EngineFact]
    public void SpecialSummonOncePerTurn_StopsTheSecondCopy()
    {
        var summoner = new CardProject
        {
            Card = new CardData { Id = 100006040, Name = "Once", Level = 4, SpecialSummonOncePerTurn = true },
            Effects = [new EffectEvent { When = B("when.summon_procedure", ("zone", "hand")) }],
        };
        new EngineScenario(summoner).Card(summoner.Card.Id, 0, "hand", 2).Card(Warrior, 0, "deck", 5)
            .SpSummon(summoner.Card.Id).SpSummon(summoner.Card.Id)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==1")
            .AssertPasses();
    }
}
