using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Counters: placing, removing and counting them by kind (Spell Counters, A-Counters, any counter), the card holding
/// them, and their wording; played in the engine. Card.GetCounter(0) is the number of kinds of counters on a card.
/// </summary>
public class CounterTests
{
    private const long Id = 100097001;
    private const long Other = 100097002;

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static CardProject Mon(long id, params EffectEvent[] effects) => new()
    {
        Card = new CardData { Id = id, Name = $"Counter Test {id}", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [.. effects],
    };

    private static EffectEvent Lasting(BlockInstance b) => new() { When = new BlockInstance("when.while_face_up"), Do = [b] };
    private static EffectEvent Ignition(params BlockInstance[] steps) => new() { When = new BlockInstance("when.ignition"), Do = [.. steps] };

    private static string Find(long code, int p = 0, string loc = "LOCATION_MZONE") => $"Duel.GetFirstMatchingCard(Card.IsCode,{p},{loc},0,nil,{code})";
    private static string Counters(long code, int id, int n, int p = 0) => $"{Find(code, p)}:GetCounter(0x{id:x})=={n}";
    private static EngineScenario Decks(EngineScenario s) => s.Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5);

    /// <summary>Breaker the Magical Warrior, from blocks: a Spell Counter when Summoned, 300 ATK for each, removed to destroy a Spell/Trap.</summary>
    private static CardProject Breaker() => Mon(Id,
        new EffectEvent
        {
            When = B("when.summoned", ("how", "normal"), ("optional", "false")),
            Do = [B(PlaceCounterAction.BlockId, ("max", "1")).With("cards", new CardFilter { ThisCard = true })],
        },
        Lasting(B("passive.self_stats_per", ("per", "counters"), ("counter", "0x1"), ("amount", "300"))),
        new EffectEvent
        {
            When = new BlockInstance("when.ignition"),
            Cost = [B(RemoveCounterCost.BlockId)],
            Do = [B("do.destroy", ("target", "true")).With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.Field, CardType = FilterCardType.SpellTrap })],
        });

    [Fact]
    public void Breaker_ReadsLikeTheCard_AndHoldsItsSpellCounter()
    {
        var p = Breaker();
        var text = EffectTextGenerator.Generate(p);
        Assert.Contains("If this card is Normal Summoned: Place 1 Spell Counter on this card (max. 1).", text);
        Assert.Contains("This card gains 300 ATK for each Spell Counter on it.", text);
        Assert.Contains("You can remove 1 Spell Counter from this card", text);
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics));
        Assert.DoesNotContain(ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        // The card that places Spell Counters on itself can hold them, one at most; once in initial_effect.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(r.Lua, @"c:EnableCounterPermit\(0x1\)"));
        Assert.Contains("c:SetCounterLimit(0x1,1)", r.Lua);
        // The mandatory trigger places it if it can; it doesn't ask first.
        Assert.DoesNotContain("IsCanAddCounter", r.Lua);
    }

    [EngineFact]
    public void Breaker_GetsACounter_GainsAtk_AndRemovesItToDestroy()
    {
        var p = Breaker();
        Decks(new EngineScenario(p)).Card(Id, 0, "hand").Summon(Id).Check(Counters(Id, 0x1, 1)).Check($"{Find(Id)}:GetAttack()==1300").AssertPasses();
        // The Spell Counter goes as the cost; the opponent's Set card is destroyed; the ATK is back to 1000.
        Decks(new EngineScenario(p)).Card(Id, 0, "hand").Card(Spell, 1, "szone").Summon(Id).Activate(Id)
            .Check(Counters(Id, 0x1, 0)).Check($"{Find(Id)}:GetAttack()==1000")
            .Check($"not Duel.IsExistingMatchingCard(Card.IsCode,1,LOCATION_SZONE,0,1,nil,{Spell})").AssertPasses();
        // Without a counter, the cost can't be paid.
        Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Card(Spell, 1, "szone").Activate(Id).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void TheLimit_StopsMoreCounters()
    {
        // An Ignition Effect placing 1 Spell Counter, max. 2: activated 3 times, only 2 go on (the third can't be activated).
        var p = Mon(Id, Ignition(B(PlaceCounterAction.BlockId, ("max", "2")).With("cards", new CardFilter { ThisCard = true })));
        var r = ScriptCompiler.Compile(p);
        Assert.Contains("e:GetHandler():IsCanAddCounter(0x1,1)", r.Lua);
        Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Activate(Id, 3).ExpectActivations(2).Check(Counters(Id, 0x1, 2)).AssertPasses();
    }

    [EngineFact]
    public void ACounters_GoOnAnyCard_Targeted_Or_All()
    {
        // A-Counters (0x100e) need no permit: "target 1 face-up monster your opponent controls; place 1 A-Counter on it".
        var target = B(PlaceCounterAction.BlockId, ("counter", "0x100e"), ("target", "true"))
            .With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true });
        var p = Mon(Id, Ignition(target));
        Assert.EndsWith("target 1 face-up monster your opponent controls; place 1 A-Counter on it.", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        Assert.DoesNotContain("EnableCounterPermit", ScriptCompiler.Compile(p).Lua);
        Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Card(Warrior, 1, "mzone").Activate(Id).Check(Counters(Warrior, 0x100e, 1, 1)).AssertPasses();
        // "Place 2 A-Counters on each face-up monster your opponent controls".
        var all = B(PlaceCounterAction.BlockId, ("counter", "0x100e"), ("amount", "2"), ("mode", "all"))
            .With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true });
        var q = Mon(Id, Ignition(all));
        Assert.EndsWith("place 2 A-Counters on each face-up monster your opponent controls.", EffectTextGenerator.Describe(q.Effects[0], q.Card));
        Decks(new EngineScenario(q)).Card(Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Dragon, 1, "mzone").Activate(Id)
            .Check(Counters(Warrior, 0x100e, 2, 1)).Check(Counters(Dragon, 0x100e, 2, 1)).AssertPasses();
    }

    [EngineFact]
    public void Values_CountOneKind_OrEveryCounter()
    {
        // 3 of one kind, 2 of another: "for each Wedge Counter" is 2, "for each counter" is 5 (as saved before kinds).
        const string rig = "local c=Duel.GetFirstMatchingCard(Card.IsCode,0,LOCATION_MZONE,0,nil,100097001) c:AddCounter(0x1001,3) c:AddCounter(0x1002,2)";
        foreach (var (kind, atk) in new[] { ("0x1002", 1200), ("any", 1500), ("custom", 1300) })
        {
            var b = B(StatsFromValuePassive.BlockId, ("value", "its_counters"), ("how", "per"), ("amount", "100"), ("counter", kind), ("counter_id", "4097"))
                .With("cards", new CardFilter { ThisCard = true, Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster });
            Decks(new EngineScenario(Mon(Id, Lasting(b)))).Card(Id, 0, "mzone").Setup(rig).Check($"{Find(Id)}:GetAttack()=={atk}").AssertPasses();
        }
        // An old save without a counter kind: every counter.
        var old = B(StatsFromValuePassive.BlockId, ("value", "its_counters"), ("how", "per"), ("amount", "100"))
            .With("cards", new CardFilter { ThisCard = true, Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster });
        old.Values.Remove("counter");
        old.Values.Remove("counter_id");
        Decks(new EngineScenario(Mon(Id, Lasting(old)))).Card(Id, 0, "mzone").Setup(rig).Check($"{Find(Id)}:GetAttack()==1500").AssertPasses();
        // "for each Spell Counter on it" on the per-card block, and "for each counter".
        var per = B("passive.self_stats_per", ("per", "counters"), ("counter", "0x1002"), ("amount", "100"));
        Decks(new EngineScenario(Mon(Id, Lasting(per)))).Card(Id, 0, "mzone").Setup(rig).Check($"{Find(Id)}:GetAttack()==1200").AssertPasses();
        per.Values["counter"] = "any";
        Decks(new EngineScenario(Mon(Id, Lasting(per)))).Card(Id, 0, "mzone").Setup(rig).Check($"{Find(Id)}:GetAttack()==1500").AssertPasses();
    }

    [EngineFact]
    public void CountersOnTheField_AreAddedUp()
    {
        // "Inflict 100 damage to your opponent for each Wedge Counter on your field": 2 on this card, 3 on another.
        var burn = B(LifeFromValueAction.BlockId, ("value", "sum_counters"), ("how", "per"), ("amount", "100"), ("counter", "0x1002"))
            .With(ValueSources.CardsKey, new CardFilter { Owner = CardOwner.You, Zones = Zone.Field });
        var p = Mon(Id, Ignition(burn));
        Assert.EndsWith("inflict 100 damage to your opponent for each Wedge Counter on your field.", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone")
            .Setup($"{Find(Id)}:AddCounter(0x1002,2) {Find(Warrior)}:AddCounter(0x1002,3) {Find(Fiend, 1)}:AddCounter(0x1002,4)")
            .Activate(Id).Check("Duel.GetLP(1)==7500").AssertPasses();
    }

    [EngineFact]
    public void Conditions_OnThisCardsCounters()
    {
        // "If there are 2 or more Wedge Counters on this card: you can draw 1 card."
        var p = Mon(Id, new EffectEvent
        {
            When = new BlockInstance("when.ignition"),
            If = [B(SelfCountersCondition.BlockId, ("amount", "2"), ("counter", "0x1002"))],
            Do = [new BlockInstance("do.draw")],
        });
        Assert.StartsWith("If there are 2 or more Wedge Counters on this card: ", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Setup($"{Find(Id)}:AddCounter(0x1002,2)").Activate(Id).ExpectActivations(1).AssertPasses();
        Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Setup($"{Find(Id)}:AddCounter(0x1002,1) {Find(Id)}:AddCounter(0x1001,3)")
            .Activate(Id).ExpectActivations(0).AssertPasses();
    }

    [EngineFact]
    public void Removing_FromThisCard_AsAnEffect()
    {
        // "Remove 2 Wedge Counters from this card, and if you do, draw 1 card": with only 1, none go and nothing is drawn.
        var p = Mon(Id, Ignition(B(RemoveCounterAction.BlockId, ("amount", "2"), ("counter", "0x1002")), new BlockInstance("do.draw").Linked(ActionLink.IfYouDo)));
        Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Setup($"{Find(Id)}:AddCounter(0x1002,3)").Activate(Id)
            .Check(Counters(Id, 0x1002, 1)).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").AssertPasses();
        Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Setup($"{Find(Id)}:AddCounter(0x1002,1)").Activate(Id)
            .Check(Counters(Id, 0x1002, 1)).Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==0").AssertPasses();
    }

    [EngineFact]
    public void Removing_FromYourField_AsACost()
    {
        // "Remove 2 Wedge Counters from anywhere on your field; draw 1 card." The player picks which counters go: a harness
        // without an answer for that prompt (older builds) can't play it, so the run proves nothing there.
        var p = Mon(Id, new EffectEvent
        {
            When = new BlockInstance("when.ignition"),
            Cost = [B(RemoveCounterCost.BlockId, ("amount", "2"), ("counter", "0x1002"), ("from", "yours"))],
            Do = [new BlockInstance("do.draw")],
        });
        Assert.Contains("remove 2 Wedge Counters from anywhere on your field; ", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        var (code, log) = Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone")
            .Setup($"{Find(Id)}:AddCounter(0x1002,1) {Find(Warrior)}:AddCounter(0x1002,1)").Activate(Id)
            .Check($"{Find(Id)}:GetCounter(0x1002)+{Find(Warrior)}:GetCounter(0x1002)==0").Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==1").Run();
        if (log.Contains("no automatic answer", StringComparison.Ordinal)) return;
        Assert.True(code == 0, log);
        // With only 1 on the field the cost can't be paid.
        Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Setup($"{Find(Id)}:AddCounter(0x1002,1)").Activate(Id).ExpectActivations(0).AssertPasses();
    }

    [Fact]
    public void Kinds_SaveAsTheirIds_AndOthersAsTypedIds()
    {
        var b = B(PlaceCounterAction.BlockId).WithCounter(0x100e);
        Assert.Equal("0x100e", b.Values[Blocks.Counters.KindKey]);
        Assert.Equal(0x100e, Blocks.Counters.IdOf(b));
        b.WithCounter(0x77);
        Assert.Equal((Blocks.Counters.Custom, "119"), (b.Values[Blocks.Counters.KindKey], b.Values[Blocks.Counters.IdKey]));
        Assert.Equal(0x77, Blocks.Counters.IdOf(b));
        // Every known counter has a unique id and a name.
        Assert.Equal(Blocks.Counters.Known.Count, Blocks.Counters.Known.Select(k => k.Id).Distinct().Count());
        Assert.All(Blocks.Counters.Known, k => Assert.EndsWith("Counter", k.Name));
        // A custom one without a name reads "counter": "Place 3 counters on this card".
        var custom = B(PlaceCounterAction.BlockId, ("amount", "3")).WithCounter(0x77).With("cards", new CardFilter { ThisCard = true });
        Assert.EndsWith("place 3 counters on this card.", EffectTextGenerator.Describe(Ignition(custom), new CardData { Name = "X" }));
    }

    [Fact]
    public void CountingRequirement_ReadsAndCompiles()
    {
        var f = new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
        f.Requirements = [new CardRequirement { Property = CardProperties.CounterCount, Op = RequirementOp.GreaterOrEqual, Value = 2, Values = [0x100e] }];
        Assert.Equal("monster with 2 or more A-Counters on it", FilterText.Describe(f, null, withLocation: false));
        Assert.Equal("(c:GetCounter(0x100e)>=2)", CardProperties.Lua(f.Requirements[0]));
        f.Requirements[0].Values = [];
        f.Requirements[0].Op = RequirementOp.Equal;
        f.Requirements[0].Value = 0;
        Assert.Equal("monster with no counters on it", FilterText.Describe(f, null, withLocation: false));
        Assert.Contains("function(x)", CardProperties.Lua(f.Requirements[0]));
    }

    [EngineFact]
    public void CountingRequirement_CountsOneKind_OrEveryCounter()
    {
        // "Destroy all monsters on the field with 2 or more A-Counters on them" / "with 4 or more counters on them".
        foreach (var (values, n, destroyed) in new (long[], int, bool)[] { ([0x100e], 2, true), ([0x100e], 3, false), ([], 4, true), ([], 5, false) })
        {
            var f = new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster };
            f.Requirements = [new CardRequirement { Property = CardProperties.CounterCount, Op = RequirementOp.GreaterOrEqual, Value = n, Values = [.. values] }];
            var p = Mon(Id, Ignition(B("do.destroy", ("mode", "all")).With("cards", f)));
            var s = Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Card(Warrior, 1, "mzone")
                .Setup($"{Find(Warrior, 1)}:AddCounter(0x100e,2) {Find(Warrior, 1)}:AddCounter(0x1002,2)").Activate(Id);
            (destroyed ? s.Check($"not {Find(Warrior, 1)}") : s.Check($"{Find(Warrior, 1)}~=nil").ExpectActivations(0)).AssertPasses();
        }
    }

    // ---------------------------------------------------------------- LP amounts

    [EngineFact]
    public void BurnAmounts_CanBeActivatedAtZero_OrWorkedOutWhenActivated()
    {
        // "Inflict 300 damage to your opponent for each card in your hand": without a card it can't be activated...
        var burn = B(LifeFromValueAction.BlockId, ("value", "count"), ("how", "per"), ("amount", "300"));
        Decks(new EngineScenario(Mon(Id, Ignition(burn)))).Card(Id, 0, "mzone").Activate(Id).ExpectActivations(0).AssertPasses();
        // ... unless it may be 0 (Dark Dimension Soldier's End Phase burn).
        var zero = B(LifeFromValueAction.BlockId, ("value", "count"), ("how", "per"), ("amount", "300"), ("zero_ok", "true"));
        Assert.EndsWith("inflict 300 damage to your opponent for each card in your hand.", EffectTextGenerator.Describe(Ignition(zero), new CardData { Name = "X" }));
        Decks(new EngineScenario(Mon(Id, Ignition(zero)))).Card(Id, 0, "mzone").Activate(Id).ExpectActivations(1).Check("Duel.GetLP(1)==8000").AssertPasses();
        // "Draw 1 card, then inflict 300 damage for each card in your hand": counted when it resolves (after the draw), or
        // when it was activated (before it).
        foreach (var (when, lp) in new[] { ("resolve", 7100), ("activate", 7400) })
        {
            var b = B(LifeFromValueAction.BlockId, ("value", "count"), ("how", "per"), ("amount", "300"), ("when", when)).Linked(ActionLink.Then);
            Decks(new EngineScenario(Mon(Id, Ignition(new BlockInstance("do.draw"), b)))).Card(Id, 0, "mzone").Card(Fiend, 0, "hand", 2)
                .Activate(Id).Check($"Duel.GetLP(1)=={lp}").AssertPasses();
        }
    }

    [Fact]
    public void AnAmountWorkedOutWhenActivated_IsKeptWithTheChainLink()
    {
        var b = B(LifeFromValueAction.BlockId, ("value", "count"), ("how", "per"), ("amount", "300"), ("when", "activate"));
        var r = ScriptCompiler.Compile(Mon(Id, Ignition(b)));
        Assert.Contains("Duel.SetTargetParam(", r.Lua);
        Assert.Contains("Duel.GetChainInfo(0,CHAININFO_TARGET_PARAM)", r.Lua);
        // Two of them can't share the chain link's one number.
        var two = ScriptCompiler.Compile(Mon(Id, Ignition(b, B(LifeFromValueAction.BlockId, ("when", "activate")))));
        Assert.Contains(two.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("only one block", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- every card of a group

    [EngineFact]
    public void AllMonsters_GainAtk_LevelsChange_AndCantAttack()
    {
        var mine = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };
        // The checks run after the turn: what lasts "until the end of this turn" is gone by then.
        var gain = B("do.change_stats", ("mode", "all"), ("amount", "500"), ("duration", "always")).With("cards", mine);
        var p = Mon(Id, Ignition(gain));
        Assert.EndsWith("all face-up monsters you control gain 500 ATK.", EffectTextGenerator.Describe(p.Effects[0], p.Card));
        Decks(new EngineScenario(p)).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(Fiend, 1, "mzone").Activate(Id)
            .Check($"{Find(Id)}:GetAttack()==1500").Check($"{Find(Warrior)}:GetAttack()==2000").Check($"{Find(Fiend, 1)}:GetAttack()==1000").AssertPasses();
        var level = B("do.change_level", ("count_mode", "all"), ("mode", "up"), ("amount", "2"), ("target", "false"), ("duration", "always")).With("cards", mine);
        var q = Mon(Id, Ignition(level));
        Assert.EndsWith("the Levels of all face-up monsters you control increase by 2.", EffectTextGenerator.Describe(q.Effects[0], q.Card));
        Decks(new EngineScenario(q)).Card(Id, 0, "mzone").Card(Warrior, 0, "mzone").Activate(Id)
            .Check($"{Find(Id)}:GetLevel()==6").Check($"{Find(Warrior)}:GetLevel()==6").AssertPasses();
        var zero = B("do.set_atk", ("count_mode", "all"), ("mode", "zero"), ("target", "false"), ("duration", "always"))
            .With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true });
        Decks(new EngineScenario(Mon(Id, Ignition(zero)))).Card(Id, 0, "mzone").Card(Warrior, 1, "mzone").Card(Dragon, 1, "mzone").Activate(Id)
            .Check($"{Find(Warrior, 1)}:GetAttack()==0").Check($"{Find(Dragon, 1)}:GetAttack()==0").Check($"{Find(Id)}:GetAttack()==1000").AssertPasses();
        // "They cannot attack this turn": player 1's monster, on player 1's turn 2, doesn't attack.
        var rule = B("do.battle_rule", ("mode", "all"), ("target", "false"), ("rule", "cannot_attack"))
            .With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true });
        var r = Mon(Id, new EffectEvent { When = B("when.phase", ("whose", "opponent"), ("phase", "standby")), Do = [rule] });
        Decks(new EngineScenario(r)).Card(Id, 0, "mzone").Card(Warrior, 1, "mzone").Activate(Id).Attack(Warrior).Turns(2)
            .Check("Duel.GetLP(0)==8000").AssertPasses();
        Decks(new EngineScenario(Mon(Id))).Card(Id, 0, "mzone").Card(Warrior, 1, "mzone").Attack(Warrior).Turns(2)
            .Check("Duel.GetLP(0)==7500").AssertPasses();
    }

    [EngineFact]
    public void ThisCard_KeepsWhatItGaveItself_WhenTicked()
    {
        // "All face-up monsters you control gain 500 ATK": then this card is negated. Without "kept" it loses its own 500.
        var mine = new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, FaceUpOnly = true };
        foreach (var (kept, atk) in new[] { ("false", 1000), ("true", 1500) })
        {
            var gain = B("do.change_stats", ("mode", "all"), ("amount", "500"), ("duration", "always"), ("kept", kept)).With("cards", mine);
            Decks(new EngineScenario(Mon(Id, Ignition(gain)))).Card(Id, 0, "mzone").Activate(Id)
                .Check($"(function() local c={Find(Id)} local e1=Effect.CreateEffect(c) e1:SetType(EFFECT_TYPE_SINGLE) e1:SetCode(EFFECT_DISABLE) c:RegisterEffect(e1) Duel.AdjustInstantly(c) return c:IsDisabled() end)()")
                .Check($"{Find(Id)}:GetAttack()=={atk}").AssertPasses();
        }
    }

    // ---------------------------------------------------------------- each time a Spell is activated

    private const long Draw = 100097003;

    /// <summary>A Normal Spell: "Draw 1 card."</summary>
    private static CardProject DrawSpell() => new()
    {
        Card = new CardData { Id = Draw, Name = "Counter Test Draw", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = new BlockInstance("when.activate"), Do = [new BlockInstance("do.draw")] }],
    };

    [EngineFact]
    public void EachTimeASpellIsActivated_ASpellCounterGoesOn_UpToTheMax()
    {
        // Royal Magical Library: "Each time a Spell Card is activated, place 1 Spell Counter on this card when that Spell resolves (max. 2)".
        var library = Mon(Id, Lasting(B(CounterEachTimePassive.BlockId, ("max", "2"))));
        Assert.Contains("Each time a Spell Card is activated, place 1 Spell Counter on this card when that Spell resolves (max. 2).", EffectTextGenerator.Generate(library));
        var r = ScriptCompiler.Compile(library);
        Assert.Contains("aux.chainreg", r.Lua);
        Assert.Contains("c:SetCounterLimit(0x1,2)", r.Lua);
        Decks(new EngineScenario(library, DrawSpell())).Card(Id, 0, "mzone").Card(Draw, 0, "hand", 3).Activate(Draw, 3)
            .Check(Counters(Id, 0x1, 2)).AssertPasses();
        // Your opponent's Spells only, and monster effects only: a Spell of yours puts none on.
        var theirs = Mon(Id, Lasting(B(CounterEachTimePassive.BlockId, ("who", "opponent"))));
        Assert.Contains("Each time your opponent activates a Spell Card, place 1 Spell Counter", EffectTextGenerator.Generate(theirs));
        Decks(new EngineScenario(theirs, DrawSpell())).Card(Id, 0, "mzone").Card(Draw, 0, "hand").Activate(Draw).Check(Counters(Id, 0x1, 0)).AssertPasses();
        var monsters = Mon(Id, Lasting(B(CounterEachTimePassive.BlockId, ("what", "monster"), ("counter", "0x1002"))));
        Decks(new EngineScenario(monsters, DrawSpell())).Card(Id, 0, "mzone").Card(Draw, 0, "hand").Activate(Draw).Check(Counters(Id, 0x1002, 0)).AssertPasses();
    }
}
