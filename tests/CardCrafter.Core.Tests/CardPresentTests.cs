using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>"If "Dark Magician" is on the field": a condition on a certain card, picked by name.</summary>
public class CardPresentTests
{
    private const long Key = 100009001;

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static BlockInstance Present(string where, string isOrNot = "is", long code = Key)
    {
        var b = B("if.card_present", ("where", where), ("is", isOrNot));
        b.Filters["card"] = new CardFilter { CardCode = code, Owner = CardOwner.Either, Zones = Zone.Field, FaceUpOnly = true };
        return b;
    }

    private static CardPack Pack() => new() { Cards = { new CardProject { Card = new CardData { Id = Key, Name = "Key Card", Level = 4 } } } };

    /// <summary>"During the Main Phase, if "Key Card" is on the field (Quick Effect): You can Special Summon this card from your hand."</summary>
    private static CardProject HandSummon(string where) => new()
    {
        Card = new CardData { Id = 100009000, Name = "Answer", Level = 4, Atk = 1800, Def = 1000 },
        Effects = [new EffectEvent
        {
            When = B("when.quick", ("timing", "any"), ("zone", "hand")),
            If = [B("if.phase", ("phase", "main")), Present(where)],
            Do = [B("do.special_summon_self")],
        }],
    };

    [Theory]
    [InlineData("field", "is", "\"Key Card\" is on the field")]
    [InlineData("field", "not", "\"Key Card\" is not on the field")]
    [InlineData("you", "is", "you control \"Key Card\"")]
    [InlineData("opponent", "not", "your opponent does not control \"Key Card\"")]
    [InlineData("field_zone", "is", "\"Key Card\" is in a Field Zone")]
    [InlineData("gy", "is", "\"Key Card\" is in your GY")]
    [InlineData("banished", "is", "\"Key Card\" is banished")]
    public void ReadsLikeACard(string where, string isOrNot, string text)
    {
        var b = Present(where, isOrNot);
        Assert.Equal(text, BlockRegistry.Find("if.card_present")!.Describe(new BlockArgs(BlockRegistry.Find("if.card_present")!, b, Pack())));
    }

    [Fact]
    public void AnyMonster_ReadsWithAnArticle()
    {
        var b = B("if.card_present", ("where", "you"));
        b.Filters["card"] = new CardFilter { Race = CardRace.Dragon, CardType = FilterCardType.Monster };
        Assert.Equal("you control a Dragon monster", BlockRegistry.Find("if.card_present")!.Describe(new BlockArgs(BlockRegistry.Find("if.card_present")!, b)));
    }

    [Fact]
    public void FieldZone_UsesIsEnvironment()
    {
        var p = HandSummon("field_zone");
        var lua = ScriptCompiler.Compile(p).Lua;
        Assert.Contains($"Duel.IsEnvironment({Key})", lua);
    }

    [EngineFact]
    public void HandQuickEffect_WorksOnlyWhileTheCardIsOnTheField()
    {
        var p = HandSummon("field");
        var key = new CardProject { Card = new CardData { Id = Key, Name = "Key Card", Level = 4, Atk = 100 } };
        // On your own turn's Main Phase, with "Key Card" on your opponent's field.
        new EngineScenario(p, key).Card(p.Card.Id, 0, "hand").Card(Key, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(1)
            .Check($"Duel.IsExistingMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,1,nil,{p.Card.Id})").AssertPasses();
        // Without it, the effect can't be used.
        new EngineScenario(p, key).Card(p.Card.Id, 0, "hand").Card(Warrior, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void YourOpponentControls_IgnoresYourOwnCopy()
    {
        var p = HandSummon("opponent");
        var key = new CardProject { Card = new CardData { Id = Key, Name = "Key Card", Level = 4, Atk = 100 } };
        new EngineScenario(p, key).Card(p.Card.Id, 0, "hand").Card(Key, 0, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(p.Card.Id).ExpectActivations(0).AssertPasses();
    }

    [Theory]
    [InlineData("return Duel.IsEnvironment(22702055)", "field_zone", "is")]
    [InlineData("return not Duel.IsEnvironment(22702055)", "field_zone", "not")]
    [InlineData("return Duel.IsEnvironment(22702055,PLAYER_ALL,LOCATION_FZONE)", "field_zone", "is")]
    [InlineData("return Duel.IsExistingMatchingCard(s.cfilter,tp,LOCATION_ONFIELD,0,1,nil)", "you", "is")]
    [InlineData("return Duel.IsExistingMatchingCard(s.cfilter,tp,LOCATION_ONFIELD,LOCATION_ONFIELD,1,nil)", "field", "is")]
    [InlineData("return not Duel.IsExistingMatchingCard(s.cfilter,tp,0,LOCATION_ONFIELD,1,nil)", "opponent", "not")]
    public void Translates(string condition, string where, string isOrNot)
    {
        var lua = $$"""
            local s,id,o=GetID()
            function s.initial_effect(c)
            	local e1=Effect.CreateEffect(c)
            	e1:SetCategory(CATEGORY_DRAW)
            	e1:SetType(EFFECT_TYPE_IGNITION)
            	e1:SetRange(LOCATION_MZONE)
            	e1:SetCondition(s.con)
            	e1:SetTarget(s.tg)
            	e1:SetOperation(s.op)
            	c:RegisterEffect(e1)
            end
            function s.cfilter(c)
            	return c:IsFaceup() and c:IsCode(22702055)
            end
            function s.con(e,tp,eg,ep,ev,re,r,rp)
            	{{condition}}
            end
            function s.tg(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return Duel.IsPlayerCanDraw(tp,1) end
            	Duel.SetTargetPlayer(tp)
            	Duel.SetTargetParam(1)
            	Duel.SetOperationInfo(0,CATEGORY_DRAW,nil,0,tp,1)
            end
            function s.op(e,tp,eg,ep,ev,re,r,rp)
            	local p,d=Duel.GetChainInfo(0,CHAININFO_TARGET_PLAYER,CHAININFO_TARGET_PARAM)
            	Duel.Draw(p,d,REASON_EFFECT)
            end
            """;
        var card = new CardData { Id = 100009010, Name = "T", Level = 4 };
        var result = ScriptTranslator.Translate(lua, card.Id, card);
        Assert.True(result.Complete, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var cond = Assert.Single(result.Pieces.Single().Effect!.If);
        Assert.Equal("if.card_present", cond.BlockId);
        Assert.Equal(where, cond.Values["where"]);
        Assert.Equal(isOrNot, cond.Values["is"]);
        Assert.Equal(22702055, cond.Filters["card"].CardCode);
    }
}
