using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Official scripts that pick cards as a group, at random, by the opponent, up to a number they count, as targets of two
/// kinds or by revealing them, and that choose among "●" options, read into the selection blocks; and the shapes that
/// stay as script because the blocks would pick differently.
/// </summary>
public class SelectionTranslationTests
{
    private static string? Script(long id) =>
        EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{id}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{id}.lua")) : null;

    private static CardData SpellCard(long id) => new() { Id = id, Name = "Select " + id, Kind = CardKind.Spell };

    private static CardData Monster(long id) => new() { Id = id, Name = "Select " + id, Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 };

    /// <summary>The translated effect of <paramref name="lua"/> that uses <paramref name="blockId"/>; it compiles cleanly.</summary>
    private static EffectEvent Effect(string lua, long official, CardData card, string blockId)
    {
        var result = ScriptTranslator.Translate(lua, official, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(e => e.Do.Any(b => b.BlockId == blockId));
        Assert.True(effect is not null, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var compiled = ScriptCompiler.Compile(new CardProject { Card = card, Effects = [effect] });
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        Assert.DoesNotContain(ScriptChecker.Check(compiled.Lua), d => d.Severity == Severity.Error);
        return effect;
    }

    private static BlockInstance Do(EffectEvent effect, string blockId) => effect.Do.First(b => b.BlockId == blockId);

    /// <summary>Why the effect of <paramref name="lua"/> stays as script.</summary>
    private static string Reasons(string lua, long id, CardData card) => string.Join("\n", ScriptTranslator.Untranslated(ScriptTranslator.Translate(lua, id, card)));

    // ---------------------------------------------------------------- groups, random picks, the opponent's picks

    [EngineFact]
    public void SalamangreatRecureance_TargetsTwoWithDifferentNames()
    {
        if (Script(83554231) is not { } lua) return;
        var card = SpellCard(100111001);
        var b = Do(Effect(lua, 83554231, card, "do.add_to_hand"), "do.add_to_hand");
        Assert.Equal(("2", "names", "true"), (b.Values["count"], b.Values["pick"], b.Values["target"]));
        Assert.StartsWith("Target 2 \"0x119\" monsters with different names from your GY; add them to your hand.", EffectTextGenerator.Generate(new CardProject { Card = card, Effects = [Effect(lua, 83554231, card, "do.add_to_hand")] }));
    }

    [EngineFact]
    public void BlockDragon_AddsMonstersWhoseTotalLevelsEqual8()
    {
        if (Script(94689206) is not { } lua) return;
        var b = Do(Effect(lua, 94689206, Monster(100111002), "do.add_to_hand"), "do.add_to_hand");
        Assert.Equal(("level_sum", "8", "3", "up_to"), (b.Values["pick"], b.Values["sum"], b.Values["count"], b.Values["mode"]));
    }

    [EngineFact]
    public void ArsenalAudit_SendsUpTo2WithDifferentNames()
    {
        if (Script(49652661) is not { } lua) return;
        var b = Do(Effect(lua, 49652661, SpellCard(100111003), "do.send_to_gy"), "do.send_to_gy");
        Assert.Equal(("names", "2", "up_to"), (b.Values["pick"], b.Values["count"], b.Values["mode"]));
    }

    [EngineFact]
    public void KewlTuneClip_BanishesARandomCardFromTheExtraDeck()
    {
        if (Script(43904702) is not { } lua) return;
        var b = Do(Effect(lua, 43904702, Monster(100111004), "do.banish"), "do.banish");
        Assert.Equal("random", b.Values["chooser"]);
        Assert.Equal((CardOwner.Opponent, Zone.ExtraDeck), (b.Filters["cards"].Owner, b.Filters["cards"].Zones));
    }

    [EngineFact]
    public void RyuOkami_TheOpponentPicksFromTheirExtraDeck()
    {
        if (Script(63737050) is not { } lua) return;
        var card = Monster(100111005);
        var effect = Effect(lua, 63737050, card, "do.send_to_gy");
        Assert.Equal("opponent", Do(effect, "do.send_to_gy").Values["chooser"]);
        Assert.EndsWith("Your opponent chooses 1 card in their Extra Deck, and you send it to the GY.", EffectTextGenerator.Describe(effect, card));
    }

    /// <summary>A random pick among every card in the hand isn't the blocks' pick among the cards that can be banished.</summary>
    [Fact]
    public void RandomPickAmongEveryCard_StaysAsScriptUnlessItDestroys()
    {
        static string Lua(string action) => $$"""
            local s,id=GetID()
            function s.initial_effect(c)
            	local e1=Effect.CreateEffect(c)
            	e1:SetType(EFFECT_TYPE_ACTIVATE)
            	e1:SetCode(EVENT_FREE_CHAIN)
            	e1:SetTarget(s.target)
            	e1:SetOperation(s.activate)
            	c:RegisterEffect(e1)
            end
            function s.target(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return Duel.GetFieldGroupCount(tp,0,LOCATION_HAND)>0 end
            end
            function s.activate(e,tp,eg,ep,ev,re,r,rp)
            	local g=Duel.GetFieldGroup(tp,0,LOCATION_HAND):RandomSelect(tp,1)
            	{{action}}
            end
            """;
        Assert.Contains("at random among cards that might not be moved", Reasons(Lua("Duel.Remove(g,POS_FACEUP,REASON_EFFECT)"), 100111006, SpellCard(100111006)));
        var b = Do(Effect(Lua("Duel.Destroy(g,REASON_EFFECT)"), 100111007, SpellCard(100111007), "do.destroy"), "do.destroy");
        Assert.Equal("random", b.Values["chooser"]);
    }

    private static string GroupCheck(string check, string zones = "LOCATION_HAND+LOCATION_DECK") => $$"""
        local s,id=GetID()
        function s.initial_effect(c)
        	local e1=Effect.CreateEffect(c)
        	e1:SetCategory(CATEGORY_TOGRAVE)
        	e1:SetType(EFFECT_TYPE_ACTIVATE)
        	e1:SetCode(EVENT_FREE_CHAIN)
        	e1:SetTarget(s.target)
        	e1:SetOperation(s.activate)
        	c:RegisterEffect(e1)
        end
        function s.filter(c)
        	return c:IsType(TYPE_MONSTER) and c:IsAbleToGrave()
        end
        function s.fselect(g)
        	return {{check}}
        end
        function s.target(e,tp,eg,ep,ev,re,r,rp,chk)
        	local g=Duel.GetMatchingGroup(s.filter,tp,{{zones}},0,nil)
        	if chk==0 then return g:CheckSubGroup(s.fselect,2,2) end
        	Duel.SetOperationInfo(0,CATEGORY_TOGRAVE,nil,2,tp,{{zones}})
        end
        function s.activate(e,tp,eg,ep,ev,re,r,rp)
        	local g=Duel.GetMatchingGroup(s.filter,tp,{{zones}},0,nil)
        	Duel.Hint(HINT_SELECTMSG,tp,HINTMSG_TOGRAVE)
        	local sg=g:SelectSubGroup(tp,s.fselect,false,2,2)
        	if sg then
        		Duel.SendtoGrave(sg,REASON_EFFECT)
        	end
        end
        """;

    [Fact]
    public void AGroupCheckOfTheScriptsOwn_ReadsAsTheGroupBlanks()
    {
        var each = Do(Effect(GroupCheck("g:FilterCount(Card.IsLocation,nil,LOCATION_HAND)<=1 and g:FilterCount(Card.IsLocation,nil,LOCATION_DECK)<=1"),
            100111030, SpellCard(100111030), "do.send_to_gy"), "do.send_to_gy");
        Assert.Equal(("true", "2"), (each.Values["each_zone"], each.Values["count"]));
        var same = Do(Effect(GroupCheck("g:GetClassCount(Card.GetCode)==1", "LOCATION_DECK"), 100111031, SpellCard(100111031), "do.send_to_gy"), "do.send_to_gy");
        Assert.Equal("same_name", same.Values["pick"]);
        var sum = Do(Effect(GroupCheck("g:GetSum(Card.GetLevel)==8", "LOCATION_DECK"), 100111032, SpellCard(100111032), "do.send_to_gy"), "do.send_to_gy");
        Assert.Equal(("level_sum", "8"), (sum.Values["pick"], sum.Values["sum"]));
        // Two rules at once ("the same Type but different Levels"), or places other than the cards': as script.
        Assert.NotEmpty(Reasons(GroupCheck("aux.dlvcheck(g) and aux.SameValueCheck(g,Card.GetRace)", "LOCATION_DECK"), 100111033, SpellCard(100111033)));
        Assert.NotEmpty(Reasons(GroupCheck("g:FilterCount(Card.IsLocation,nil,LOCATION_HAND)<=1"), 100111034, SpellCard(100111034)));
    }

    // ---------------------------------------------------------------- several targets, two kinds, revealing

    [EngineFact]
    public void BlackRoseMoonlightDragon_StyleUpTo3Targets()
    {
        if (Script(2203790) is not { } lua) return;
        var effect = Effect(lua, 2203790, Monster(100111008), "do.destroy");
        var b = Do(effect, "do.destroy");
        Assert.Equal(("3", "up_to", "true"), (b.Values["count"], b.Values["mode"], b.Values["target"]));
        // "Up to 3" may be 1 card: the target is checked again as official scripts do.
        var compiled = ScriptCompiler.Compile(new CardProject { Card = Monster(100111008), Effects = [effect] }).Lua;
        Assert.DoesNotContain("if chkc then return false end", compiled);
    }

    [EngineFact]
    public void GraydleImpact_TargetsOneOfYoursAndOneOfTheirs()
    {
        if (Script(2759860) is not { } lua) return;
        var card = SpellCard(100111009);
        var effect = Effect(lua, 2759860, card, PairTargetAction.BlockId);
        var b = Do(effect, PairTargetAction.BlockId);
        Assert.Equal(("1", "1", "destroy"), (b.Values["count"], b.Values["count2"], b.Values["action"]));
        Assert.Equal((CardOwner.You, CardOwner.Opponent), (b.Filters["cards"].Owner, b.Filters["cards2"].Owner));
        Assert.StartsWith("You can target 1 other face-up \"0xd1\" card you control and 1 card your opponent controls; destroy them.", EffectTextGenerator.Describe(effect, card));
    }

    [EngineTheory]
    [InlineData(22842126L, "chooses")]
    [InlineData(2403771L, "random")]
    public void PantheismAndPowerToolDragon_RevealForTheOpponentToPick(long official, string how)
    {
        if (Script(official) is not { } lua) return;
        var b = Do(Effect(lua, official, Monster(100111010), RevealPickAction.BlockId), RevealPickAction.BlockId);
        Assert.Equal(("3", how, "hand", "deck"), (b.Values["count"], b.Values["how"], b.Values["then"], b.Values["rest"]));
    }

    // ---------------------------------------------------------------- up to the number of

    [EngineFact]
    public void RitualBeastSteeds_DestroysUpToTheNumberOfItsMonsters()
    {
        if (Script(11556339) is not { } lua) return;
        var card = SpellCard(100111011);
        var effect = Effect(lua, 11556339, card, "do.destroy");
        var b = Do(effect, "do.destroy");
        Assert.Equal("number_of", b.Values["amount"]);
        Assert.Equal((CardOwner.You, Zone.MonsterZone, true), (b.Filters["count_cards"].Owner, b.Filters["count_cards"].Zones, b.Filters["count_cards"].FaceUpOnly));
        Assert.Contains("Destroy monsters on the field, up to the number of face-up \"0xb5\" monsters you control.", EffectTextGenerator.Describe(effect, card));
    }

    [EngineFact]
    public void AssaultBlackwingRaikiri_TargetsUpToTheNumberOfOtherBlackwings()
    {
        if (Script(16051717) is not { } lua) return;
        var card = Monster(100111012);
        var b = Do(Effect(lua, 16051717, card, "do.destroy"), "do.destroy");
        Assert.Equal(("number_of", "true"), (b.Values["amount"], b.Values["target"]));
        Assert.True(b.Filters["count_cards"].ExcludeThisCard);
    }

    /// <summary>Counting without asking that there is 1 to count: the blocks couldn't be activated where the card can.</summary>
    [Fact]
    public void UpToTheNumberOf_NotAskedAtActivation_StaysAsScript()
    {
        const string lua = """
            local s,id=GetID()
            function s.initial_effect(c)
            	local e1=Effect.CreateEffect(c)
            	e1:SetCategory(CATEGORY_DESTROY)
            	e1:SetType(EFFECT_TYPE_ACTIVATE)
            	e1:SetCode(EVENT_FREE_CHAIN)
            	e1:SetTarget(s.target)
            	e1:SetOperation(s.activate)
            	c:RegisterEffect(e1)
            end
            function s.cfilter(c)
            	return c:IsFaceup() and c:IsRace(RACE_DRAGON)
            end
            function s.target(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return Duel.IsExistingMatchingCard(aux.TRUE,tp,0,LOCATION_MZONE,1,nil) end
            end
            function s.activate(e,tp,eg,ep,ev,re,r,rp)
            	local ct=Duel.GetMatchingGroupCount(s.cfilter,tp,LOCATION_MZONE,0,nil)
            	if ct==0 then return end
            	local g=Duel.SelectMatchingCard(tp,aux.TRUE,tp,0,LOCATION_MZONE,1,ct,nil)
            	Duel.Destroy(g,REASON_EFFECT)
            end
            """;
        Assert.Contains("without asking that there is 1 to count", Reasons(lua, 100111013, SpellCard(100111013)));
        // Asked: it reads as the block.
        var asked = lua.Replace("return Duel.IsExistingMatchingCard(aux.TRUE", "return Duel.IsExistingMatchingCard(s.cfilter,tp,LOCATION_MZONE,0,1,nil) and Duel.IsExistingMatchingCard(aux.TRUE", StringComparison.Ordinal);
        Assert.Equal("number_of", Do(Effect(asked, 100111014, SpellCard(100111014), "do.destroy"), "do.destroy").Values["amount"]);
    }

    // ---------------------------------------------------------------- "●" options

    [EngineFact]
    public void BattleguardEchoes_TwoOptionsChosenOnActivation()
    {
        if (Script(74213995) is not { } lua) return;
        var effect = Effect(lua, 74213995, Monster(100111015), "do.add_to_hand");
        Assert.Null(ChooseEffectsAction.Header(effect.Do));
        Assert.Equal(2, effect.Do.Count(b => b.Link == ActionLink.Option) + 1);
    }

    [EngineFact]
    public void StarsOfRaiseMoon_AnOptionWithItsOwnCondition()
    {
        if (Script(46983930) is not { } lua) return;
        var card = SpellCard(100111016);
        var effect = Effect(lua, 46983930, card, "do.draw");
        Assert.NotNull(ChooseEffectsAction.Header(effect.Do));
        Assert.Contains("\n ● If you control a face-up Spellcaster \"0x1ec\" Xyz Monster: Draw 1 card.", EffectTextGenerator.Describe(effect, card));
    }

    private static string Options(string target, string operation, string filters = "") => $$"""
        local s,id=GetID()
        function s.initial_effect(c)
        	local e1=Effect.CreateEffect(c)
        	e1:SetType(EFFECT_TYPE_IGNITION)
        	e1:SetRange(LOCATION_MZONE)
        	e1:SetTarget(s.target)
        	e1:SetOperation(s.operation)
        	c:RegisterEffect(e1)
        end
        {{filters}}
        function s.target(e,tp,eg,ep,ev,re,r,rp,chk)
        {{target}}
        end
        function s.operation(e,tp,eg,ep,ev,re,r,rp)
        {{operation}}
        end
        """;

    [Fact]
    public void OpponentChoosesAsItResolves_ReadsAsTheHeader()
    {
        var lua = Options(
            """
            	local b1=Duel.IsPlayerCanDraw(tp,1)
            	if chk==0 then return b1 or true end
            """,
            """
            	local b1=Duel.IsPlayerCanDraw(tp,1)
            	local op=aux.SelectFromOptions(1-tp,{b1,aux.Stringid(id,1),1},{true,aux.Stringid(id,2),2})
            	if op==1 then
            		Duel.Draw(tp,1,REASON_EFFECT)
            	elseif op==2 then
            		Duel.Recover(tp,1000,REASON_EFFECT)
            	end
            """);
        var card = Monster(100111017);
        var effect = Effect(lua, 100111017, card, "do.draw");
        var header = ChooseEffectsAction.Header(effect.Do)!;
        Assert.Equal(("opponent", "apply"), (header.Values["who"], header.Values["when"]));
        Assert.StartsWith("You can activate this effect; your opponent chooses 1 of these effects for you to apply.", EffectTextGenerator.Describe(effect, card));
    }

    /// <summary>"● Draw 1 card" offered even with an empty Deck: the blocks would only offer it when you can draw.</summary>
    [Fact]
    public void OptionOfferedWithoutItsChecks_StaysAsScript()
    {
        var lua = Options(
            "\tif chk==0 then return true end",
            """
            	local op=Duel.SelectOption(1-tp,aux.Stringid(id,1),aux.Stringid(id,2))
            	if op==0 then
            		Duel.Draw(tp,1,REASON_EFFECT)
            	else
            		Duel.Recover(tp,1000,REASON_EFFECT)
            	end
            """);
        Assert.Contains("offered without the checks its blocks make", Reasons(lua, 100111018, Monster(100111018)));
    }

    [Fact]
    public void EachEffectOncePerTurn_ReadsAsTheHeader()
    {
        var lua = Options(
            """
            	local b1=Duel.IsPlayerCanDraw(tp,1) and (not e:IsCostChecked() or Duel.GetFlagEffect(tp,id)==0)
            	local b2=Duel.IsPlayerCanDraw(tp,2) and (not e:IsCostChecked() or Duel.GetFlagEffect(tp,id+1)==0)
            	if chk==0 then return b1 or b2 end
            	local op=aux.SelectFromOptions(tp,{b1,aux.Stringid(id,1),1},{b2,aux.Stringid(id,2),2})
            	e:SetLabel(op)
            	if op==1 then
            		if e:IsCostChecked() then
            			Duel.RegisterFlagEffect(tp,id,RESET_PHASE+PHASE_END,0,1)
            		end
            	elseif op==2 then
            		if e:IsCostChecked() then
            			Duel.RegisterFlagEffect(tp,id+1,RESET_PHASE+PHASE_END,0,1)
            		end
            	end
            """,
            """
            	local op=e:GetLabel()
            	if op==1 then
            		Duel.Draw(tp,1,REASON_EFFECT)
            	elseif op==2 then
            		Duel.Draw(tp,2,REASON_EFFECT)
            	end
            """);
        var card = Monster(100111019);
        var effect = Effect(lua, 100111019, card, "do.draw");
        Assert.Equal("true", ChooseEffectsAction.Header(effect.Do)!.Values["once_each"]);
        Assert.StartsWith($"You can activate 1 of these effects (but you can only use each effect of \"{card.Name}\" once per turn);", EffectTextGenerator.Describe(effect, card));
    }

    [Fact]
    public void AnOptionsOwnCost_ReadsAsACostBlockInTheOption()
    {
        var lua = Options(
            """
            	local b1=Duel.IsPlayerCanDraw(tp,2)
            		and (not e:IsCostChecked() or Duel.IsExistingMatchingCard(Card.IsDiscardable,tp,LOCATION_HAND,0,1,nil))
            	local b2=true
            	if chk==0 then return b1 or b2 end
            	local op=aux.SelectFromOptions(tp,{b1,aux.Stringid(id,1),1},{b2,aux.Stringid(id,2),2})
            	e:SetLabel(op)
            	if op==1 then
            		e:SetCategory(CATEGORY_DRAW)
            		if e:IsCostChecked() then
            			Duel.DiscardHand(tp,Card.IsDiscardable,1,1,REASON_COST+REASON_DISCARD)
            		end
            	else
            		e:SetCategory(CATEGORY_RECOVER)
            	end
            """,
            """
            	local op=e:GetLabel()
            	if op==1 then
            		Duel.Draw(tp,2,REASON_EFFECT)
            	else
            		Duel.Recover(tp,1000,REASON_EFFECT)
            	end
            """);
        var card = Monster(100111020);
        var effect = Effect(lua, 100111020, card, "do.draw");
        Assert.NotNull(ChooseEffectsAction.Header(effect.Do));
        Assert.Contains(effect.Do, b => BlockRegistry.Find(b.BlockId) is { Kind: BlockKind.Cost });
        Assert.Contains("\n ● Discard 1 card", EffectTextGenerator.Describe(effect, card));
        // Without its check, the cost isn't the blocks' cost.
        var unchecked_ = lua.Replace("\n\t\tand (not e:IsCostChecked() or Duel.IsExistingMatchingCard(Card.IsDiscardable,tp,LOCATION_HAND,0,1,nil))", "", StringComparison.Ordinal);
        Assert.Contains("isn't checked as the blocks check it", Reasons(unchecked_, 100111021, Monster(100111021)));
    }
}
