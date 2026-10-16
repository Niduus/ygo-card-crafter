using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Official scripts with counters, loops over every card of a group, and burn amounts worked out when activated, read
/// into the blocks and played in the engine.
/// </summary>
public class CounterTranslationTests
{
    private static string? Script(long id) =>
        EngineScripts is { } dir && File.Exists(Path.Combine(dir, $"c{id}.lua")) ? File.ReadAllText(Path.Combine(dir, $"c{id}.lua")) : null;

    private static CardData Monster(long id, int atk = 1000, int def = 1000, int level = 4, MonsterFrame frame = MonsterFrame.Effect) =>
        new() { Id = id, Name = "Counter " + id, Frame = frame, Level = level, Atk = atk, Def = def, Race = CardRace.Spellcaster, Attribute = CardAttribute.Dark };

    private static TranslationResult Translate(long official, CardData card) => ScriptTranslator.Translate(Script(official)!, official, card);

    /// <summary>The whole card, as the editor would apply it: every part translated, and the blocks compile cleanly.</summary>
    private static CardProject Applied(long official, CardData card)
    {
        var project = new CardProject { Card = card, CustomLua = Script(official)!, UseCustomLua = true };
        var result = ScriptTranslator.ApplyTo(project, null, out var applied);
        Assert.True(applied, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var compiled = ScriptCompiler.Compile(project);
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics));
        Assert.DoesNotContain(ScriptChecker.Check(compiled.Lua), d => d.Severity == Severity.Error);
        return project;
    }

    private static EffectEvent Effect(long official, CardData card, string blockId)
    {
        var result = Translate(official, card);
        var effect = result.Pieces.SelectMany(p => p.Effects).FirstOrDefault(e => e.Do.Concat(e.Cost).Concat(e.If).Any(b => b.BlockId == blockId));
        Assert.True(effect is not null, string.Join("\n", ScriptTranslator.Untranslated(result)));
        var compiled = ScriptCompiler.Compile(new CardProject { Card = card, Effects = [effect] });
        Assert.False(compiled.HasErrors, string.Join("\n", compiled.Diagnostics) + "\n" + compiled.Lua);
        Assert.DoesNotContain(ScriptChecker.Check(compiled.Lua), d => d.Severity == Severity.Error);
        return effect;
    }

    private static string Find(long code, int p = 0) => $"Duel.GetFirstMatchingCard(Card.IsCode,{p},LOCATION_MZONE,0,nil,{code})";

    [EngineFact]
    public void Breaker_TheWholeCard()
    {
        if (Script(71413901) is null) return;
        // Its Spell Counter permit and limit become the place-counter block's: the card holds 1 Spell Counter at most.
        var card = Monster(100097101, atk: 1600);
        var p = Applied(71413901, card);
        var place = p.Effects.SelectMany(e => e.Do).Single(b => b.BlockId == PlaceCounterAction.BlockId);
        Assert.Equal(("0x1", "1", "1"), (place.Values["counter"], place.Values["amount"], place.Values["max"]));
        Assert.Contains(p.Effects, e => e.Cost.Any(b => b.BlockId == RemoveCounterCost.BlockId && b.Values["from"] == "this"));
        Assert.Contains("This card gains 300 ATK for each Spell Counter on it.", EffectTextGenerator.Generate(p));
        new EngineScenario(p).Card(card.Id, 0, "hand").Card(Spell, 1, "szone").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4)
            .Summon(card.Id).Activate(card.Id)
            .Check($"{Find(card.Id)}:GetCounter(0x1)==0").Check($"{Find(card.Id)}:GetAttack()==1600")
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_SZONE,0,1,nil,{Spell})").AssertPasses();
        new EngineScenario(p).Card(card.Id, 0, "hand").Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4).Summon(card.Id)
            .Check($"{Find(card.Id)}:GetCounter(0x1)==1").Check($"{Find(card.Id)}:GetAttack()==1900").AssertPasses();
    }

    [EngineFact]
    public void BlastMagician_CollectsSpellCounters()
    {
        if (Script(21051146) is null) return;
        // Its two Continuous effects (aux.chainreg on EVENT_CHAINING, AddCounter on EVENT_CHAIN_SOLVED) are one block.
        var card = Monster(100097108, atk: 1400);
        var result = Translate(21051146, card);
        Assert.Contains(result.Pieces, p => p.Ignored && p.Piece.Code.Contains("aux.chainreg", StringComparison.Ordinal));
        var effect = result.Pieces.SelectMany(p => p.Effects).Single(e => e.Do.Any(b => b.BlockId == CounterEachTimePassive.BlockId));
        var b = effect.Do.Single();
        Assert.Equal(("any", "spell", "1", "0x1"), (b.Values["who"], b.Values["what"], b.Values["amount"], b.Values["counter"]));
        Assert.Equal("Each time a Spell Card is activated, place 1 Spell Counter on this card when that Spell resolves.", EffectTextGenerator.Describe(effect, card));
        var spell = new CardProject
        {
            Card = new CardData { Id = 100097109, Name = "Draw Spell", Kind = CardKind.Spell },
            Effects = [new EffectEvent { When = new BlockInstance("when.activate"), Do = [new BlockInstance("do.draw")] }],
        };
        new EngineScenario(new CardProject { Card = card, Effects = [effect] }, spell).Card(card.Id, 0, "mzone").Card(spell.Card.Id, 0, "hand", 2)
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4).Activate(spell.Card.Id, 2)
            .Check($"{Find(card.Id)}:GetCounter(0x1)==2").AssertPasses();
    }

    [EngineFact]
    public void SharkCaesar_PlacesACounterOnItself_WhenItFits()
    {
        if (Script(14306092) is null) return;
        // "Detach 1 material; place 1 Shark Counter on this card": an Ignition Effect, so it asks whether the counter fits.
        var effect = Effect(14306092, Monster(100097102, frame: MonsterFrame.Xyz), PlaceCounterAction.BlockId);
        var place = effect.Do.Single();
        Assert.Equal("0x2e", place.Values["counter"]);
        Assert.Contains("place 1 Shark Counter on this card", EffectTextGenerator.Describe(effect, Monster(100097102)));
    }

    /// <summary>A script with one effect: <paramref name="effect"/> lines in initial_effect, and its functions.</summary>
    private static TranslationResult Inline(string effect, string functions, CardData? card = null) =>
        ScriptTranslator.Translate($"function c100097110.initial_effect(c)\n{effect}\nend\n{functions}", 100097110, card ?? Monster(100097110));

    private const string Ignition = "\tlocal e1=Effect.CreateEffect(c)\n\te1:SetType(EFFECT_TYPE_IGNITION)\n\te1:SetRange(LOCATION_MZONE)\n"
        + "\te1:SetCondition(c100097110.con)\n\te1:SetCost(c100097110.cost)\n\te1:SetTarget(c100097110.tg)\n\te1:SetOperation(c100097110.op)\n\tc:RegisterEffect(e1)";

    private static List<BlockInstance> Blocks(TranslationResult r)
    {
        Assert.True(r.Complete, string.Join("\n", ScriptTranslator.Untranslated(r)));
        return r.Pieces.SelectMany(p => p.Effects).SelectMany(e => e.If.Concat(e.Cost).Concat(e.Do)).ToList();
    }

    [Fact]
    public void Counters_InConditionsCostsFiltersAndTargets()
    {
        var r = Inline("\tc:EnableCounterPermit(0x1)\n" + Ignition, """
            function c100097110.con(e,tp,eg,ep,ev,re,r,rp)
            	return e:GetHandler():GetCounter(0x1)>=3
            end
            function c100097110.cost(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return Duel.IsCanRemoveCounter(tp,1,0,0x37,2,REASON_COST) end
            	Duel.RemoveCounter(tp,1,0,0x37,2,REASON_COST)
            end
            function c100097110.filter(c)
            	return c:IsFaceup() and c:GetCounter(0x100e)>0
            end
            function c100097110.tg(e,tp,eg,ep,ev,re,r,rp,chk,chkc)
            	if chkc then return chkc:IsOnField() and chkc:IsCanAddCounter(0x100e,1) end
            	if chk==0 then return Duel.IsExistingTarget(Card.IsCanAddCounter,tp,0,LOCATION_MZONE,1,nil,0x100e,1) end
            	Duel.SelectTarget(tp,Card.IsCanAddCounter,tp,0,LOCATION_MZONE,1,1,nil,0x100e,1)
            end
            function c100097110.op(e,tp,eg,ep,ev,re,r,rp)
            	local tc=Duel.GetFirstTarget()
            	if tc:IsRelateToEffect(e) then
            		tc:AddCounter(0x100e,1)
            	end
            	local g=Duel.GetMatchingGroup(c100097110.filter,tp,0,LOCATION_MZONE,nil)
            	Duel.Destroy(g,REASON_EFFECT)
            end
            """);
        var blocks = Blocks(r);
        var cond = blocks.Single(b => b.BlockId == SelfCountersCondition.BlockId);
        Assert.Equal(("at_least", "3", "0x1"), (cond.Values["cmp"], cond.Values["amount"], cond.Values["counter"]));
        var cost = blocks.Single(b => b.BlockId == RemoveCounterCost.BlockId);
        Assert.Equal(("yours", "2", "0x37"), (cost.Values["from"], cost.Values["amount"], cost.Values["counter"]));
        var place = blocks.Single(b => b.BlockId == PlaceCounterAction.BlockId);
        Assert.Equal(("true", "0x100e", CardOwner.Opponent), (place.Values["target"], place.Values["counter"], place.Filters["cards"].Owner));
        var destroy = blocks.Single(b => b.BlockId == "do.destroy");
        var req = Assert.Single(destroy.Filters["cards"].Requirements);
        Assert.Equal((CardProperties.CounterCount, RequirementOp.Greater, 0, 0x100eL), (req.Property, req.Op, req.Value, req.Values.Single()));
        // The card holds Spell Counters only if the script lets it: its condition counts its own, which it can't have.
        Assert.Contains("which the script doesn't let it hold", string.Join("\n", ScriptTranslator.Untranslated(Inline(Ignition, """
            function c100097110.con(e,tp,eg,ep,ev,re,r,rp)
            	return e:GetHandler():GetCounter(0x1)>=3
            end
            function c100097110.cost(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return true end
            end
            function c100097110.tg(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return true end
            end
            function c100097110.op(e,tp,eg,ep,ev,re,r,rp)
            	Duel.Draw(tp,1,REASON_EFFECT)
            end
            """))));
    }

    [Fact]
    public void Counters_ThePermitAndLimit_GoWithTheBlocksThatPlaceThem()
    {
        const string place = """
            function c100097110.con(e,tp,eg,ep,ev,re,r,rp)
            	return Duel.IsMainPhase()
            end
            function c100097110.cost(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return true end
            end
            function c100097110.tg(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return e:GetHandler():IsCanAddCounter(0x3,1) end
            end
            function c100097110.op(e,tp,eg,ep,ev,re,r,rp)
            	local c=e:GetHandler()
            	if c:IsRelateToEffect(e) then
            		c:AddCounter(0x3,1)
            	end
            end
            """;
        var r = Inline("\tc:EnableCounterPermit(0x3)\n\tc:SetCounterLimit(0x3,4)\n" + Ignition, place);
        var b = Blocks(r).Single(x => x.BlockId == PlaceCounterAction.BlockId);
        Assert.Equal(("0x3", "4"), (b.Values["counter"], b.Values["max"]));
        // Without the permit the card couldn't hold them; with a permit no block uses, the permit stays script.
        Assert.False(Inline(Ignition, place).Complete);
        Assert.False(Inline("\tc:EnableCounterPermit(0x5)\n" + Ignition, place).Complete);
        // An activation that doesn't ask whether the counter fits isn't the block's.
        Assert.Contains("can be placed", string.Join("\n", ScriptTranslator.Untranslated(Inline("\tc:EnableCounterPermit(0x3)\n" + Ignition,
            place.Replace("return e:GetHandler():IsCanAddCounter(0x3,1)", "return true")))));
    }

    [Fact]
    public void Counters_AsValues_AndOnEveryCardOfAGroup()
    {
        var r = Inline(Ignition, """
            function c100097110.con(e,tp,eg,ep,ev,re,r,rp)
            	return Duel.GetCounter(tp,1,1,0x37)>=3
            end
            function c100097110.cost(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return true end
            end
            function c100097110.tg(e,tp,eg,ep,ev,re,r,rp,chk)
            	if chk==0 then return true end
            	Duel.SetTargetPlayer(1-tp)
            end
            function c100097110.op(e,tp,eg,ep,ev,re,r,rp)
            	local g=Duel.GetMatchingGroup(Card.IsCanAddCounter,tp,0,LOCATION_MZONE,nil,0x100e,1)
            	for tc in aux.Next(g) do
            		tc:AddCounter(0x100e,1)
            	end
            	local p=Duel.GetChainInfo(0,CHAININFO_TARGET_PLAYER)
            	Duel.Damage(p,Duel.GetCounter(tp,1,1,0x100e)*100,REASON_EFFECT)
            end
            """);
        var blocks = Blocks(r);
        var cond = blocks.Single(b => b.BlockId == CompareValuesCondition.BlockId);
        Assert.Equal(("sum_counters", "0x37", "ge", "3", CardOwner.Either), (cond.Values["value"], cond.Values["counter"], cond.Values["op"], cond.Values["amount"], cond.Filters[ValueSources.CardsKey].Owner));
        var all = blocks.Single(b => b.BlockId == PlaceCounterAction.BlockId);
        Assert.Equal(("all", "0x100e"), (all.Values["mode"], all.Values["counter"]));
        var burn = blocks.Single(b => b.BlockId == LifeFromValueAction.BlockId);
        Assert.Equal(("sum_counters", "per", "100", "0x100e", "true"), (burn.Values["value"], burn.Values["how"], burn.Values["amount"], burn.Values["counter"], burn.Values["zero_ok"]));
    }

    [EngineFact]
    public void EveryMonster_GainsAFixedAmount_OrALiveOne()
    {
        if (Script(13314457) is null || Script(1689516) is null || Script(2099841) is null || Script(43513897) is null) return;
        // Deepsea Macrotrema: "all face-up WATER monsters you control gain 500 ATK until the end of this turn".
        var macro = Effect(13314457, Monster(100097104), "do.change_stats");
        var gain = macro.Do.Single();
        Assert.Equal(("all", "500", "gain", "turn", "false"), (gain.Values["mode"], gain.Values["amount"], gain.Values["dir"], gain.Values["duration"], gain.Values["kept"]));
        Assert.Equal(CardAttribute.Water, gain.Filters["cards"].Attribute);
        // Constellar Acubens keeps the ATK it gave itself while negated (EFFECT_FLAG_CANNOT_DISABLE).
        var acubens = Effect(43513897, Monster(100097105), "do.change_stats");
        Assert.Equal("true", acubens.Do.Single().Values["kept"]);
        Assert.Contains("SetProperty(EFFECT_FLAG_CANNOT_DISABLE)", ScriptCompiler.Compile(new CardProject { Card = Monster(100097105), Effects = [acubens] }).Lua);
        // The Big March of Animals: "200 ATK for each face-up Beast monster you control", the same for each of them.
        var march = Effect(1689516, new CardData { Id = 100097106, Name = "March", Kind = CardKind.Spell }, StatsFromValueAction.BlockId);
        Assert.Equal(("all", "count", "per", "200"), (march.Do[0].Values["mode"], march.Do[0].Values["value"], march.Do[0].Values["how"], march.Do[0].Values["amount"]));
        // Smile World: "all face-up monsters on the field gain 100 ATK for each monster on the field".
        var smile = new CardData { Id = 100097107, Name = "Smile", Kind = CardKind.Spell };
        var effect = Effect(2099841, smile, StatsFromValueAction.BlockId);
        Assert.Equal(("all", "count", "per", "100"), (effect.Do[0].Values["mode"], effect.Do[0].Values["value"], effect.Do[0].Values["how"], effect.Do[0].Values["amount"]));
        Assert.Contains("face-up monsters on the field gain 100 ATK for each monster on the field until the end of this turn", EffectTextGenerator.Describe(effect, smile));
        // Player 1's Warrior (1500, the only monster: +100) attacks directly on their turn 2.
        new EngineScenario(new CardProject { Card = smile, Effects = [effect] }).Card(smile.Id, 1, "hand").Card(Warrior, 1, "mzone")
            .Card(Warrior, 0, "deck", 4).Card(Warrior, 1, "deck", 4).Activate(smile.Id).Attack(Warrior).Turns(2)
            .ExpectActivations(1).Check("Duel.GetLP(0)==6400").AssertPasses();
    }
}
