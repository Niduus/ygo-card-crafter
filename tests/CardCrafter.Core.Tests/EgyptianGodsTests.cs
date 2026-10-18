using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// The Egyptian Gods built from blocks and played in the engine: 3 Tributes to Normal Summon (cannot be Normal Set), a
/// Normal Summon that cannot be negated, no responses to it ("other cards and effects" for Ra), paying LP so that you only
/// have 100 left, and Ra's Disciple's Tribute and Special Summon rules.
/// </summary>
public class EgyptianGodsTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static string Lua(CardProject p, CardPack? pack = null)
    {
        var r = ScriptCompiler.Compile(p, pack);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics) + "\n" + r.Lua);
        Assert.DoesNotContain(Validation.ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        Assert.Empty(Validation.ScriptChecker.UnknownNames(r.Lua));
        return r.Lua;
    }

    private static CardPack Pack(params CardProject[] cards) => new() { Cards = [.. cards] };

    private static string In(long code, int player, string loc) => $"Duel.IsExistingMatchingCard(Card.IsCode,{player},{loc},0,1,nil,{code})";

    private static string Card(long code, int player, string loc = "LOCATION_MZONE") => $"Duel.GetFirstMatchingCard(Card.IsCode,{player},{loc},0,nil,{code})";

    private static EffectEvent Lasting(params BlockInstance[] dos) => new() { When = B("when.while_face_up"), Do = [.. dos] };

    // ---------------------------------------------------------------- Obelisk the Tormentor

    private const long ObeliskId = 100710001, RaId = 100710002, Answer = 100710003, Negator = 100710004, DiscipleId = 100710005, Summoner = 100710006;

    private static CardProject Obelisk(bool noChain = true, bool notNegated = true)
    {
        var effects = new List<EffectEvent> { Lasting(B(TributeSummonPassive.BlockId)) };
        if (notNegated) effects.Add(Lasting(B("passive.self_rule", ("rule", "summon_not_negated"))));
        if (noChain) effects.Add(Lasting(B("passive.self_rule", ("rule", "summon_no_chain"))));
        effects.Add(Lasting(B("passive.untargetable", ("who", "neither"))));
        effects.Add(new EffectEvent
        {
            When = B("when.ignition", ("zone", "monster_zone")),
            Cost = [B("cost.tribute", ("count", "2")).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, ExcludeThisCard = true })],
            Do = [B("do.destroy", ("mode", "all")).With("cards", new CardFilter { Owner = CardOwner.Opponent, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
        });
        return new CardProject
        {
            Card = new CardData
            {
                Id = ObeliskId, Name = "Obelisk the Tormentor", Frame = MonsterFrame.Effect, Race = CardRace.DivineBeast, Attribute = CardAttribute.Divine, Level = 10, Atk = 4000, Def = 4000,
            },
            Effects = effects,
        };
    }

    [Fact]
    public void Obelisk_Wording()
    {
        var o = Obelisk();
        Assert.Equal("Requires 3 Tributes to Normal Summon (cannot be Normal Set). This card's Normal Summon cannot be negated. When Normal Summoned, cards and effects cannot be activated. " +
                     "Neither player can target this card with card effects. You can Tribute 2 other monsters you control; destroy all monsters your opponent controls.", EffectTextGenerator.Generate(o));
        var lua = Lua(o);
        Assert.Contains("SetCode(EFFECT_LIMIT_SUMMON_PROC)", lua);
        Assert.Contains("return minc<=3 and Duel.CheckTribute(c,3)", lua);
        Assert.Contains("SetCode(EFFECT_LIMIT_SET_PROC)", lua);
        Assert.Contains("SetCode(EFFECT_CANNOT_DISABLE_SUMMON)", lua);
        Assert.Contains("Duel.SetChainLimitTillChainEnd(aux.FALSE)", lua);
    }

    [Fact]
    public void TributeSummon_OfAKind_AndSet()
    {
        var p = Obelisk();
        p.Effects[0].Do[0].With("set", "set").With("cards", new CardFilter { CardType = FilterCardType.Monster, Archetype = 0xaa });
        var pack = new CardPack { Cards = [p], Archetypes = [new() { Code = 0xaa, Name = "Qli" }] };
        Assert.StartsWith("Requires 3 \"Qli\" Tributes to Normal Summon/Set. ", EffectTextGenerator.Generate(p, pack));
        var lua = Lua(p, pack);
        Assert.Contains("SetCode(EFFECT_TRIBUTE_LIMIT)", lua);
        Assert.Contains("return not s.tributefilter(c)", lua);
        Assert.DoesNotContain("return false", lua);
    }

    [EngineTheory]
    [InlineData(3, true)]
    [InlineData(2, false)]
    public void Obelisk_NeedsThreeTributes(int monsters, bool summoned)
    {
        new EngineScenario(Obelisk()).Card(ObeliskId, 0, "hand").Card(Warrior, 0, "mzone", monsters).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Summon(ObeliskId)
            .Check(summoned ? In(ObeliskId, 0, "LOCATION_MZONE") + $" and {Card(ObeliskId, 0)}:IsSummonType(SUMMON_TYPE_ADVANCE) and Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==3"
                : In(ObeliskId, 0, "LOCATION_HAND") + " and Duel.GetFieldGroupCount(0,LOCATION_MZONE,0)==2")
            .AssertPasses();
    }

    [EngineFact]
    public void Obelisk_CannotBeNormalSet()
    {
        // With the 3 Tributes it needs, it can be Normal Summoned but never Set.
        new EngineScenario(Obelisk()).Card(ObeliskId, 0, "hand").Card(Warrior, 0, "mzone", 3).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Check($"{Card(ObeliskId, 0, "LOCATION_HAND")}:IsSummonable(true,nil) and not {Card(ObeliskId, 0, "LOCATION_HAND")}:IsMSetable(true,nil)")
            .AssertPasses();
    }

    /// <summary>Your opponent's monster in the hand: "When your opponent Normal Summons a monster (Quick Effect): You can Special Summon this card from your hand."</summary>
    private static CardProject Answerer() => new()
    {
        Card = new CardData { Id = Answer, Name = "Summon Answerer", Frame = MonsterFrame.Effect, Level = 4, Atk = 1000, Def = 1000 },
        Effects = [new EffectEvent { When = B("when.quick", ("timing", "summon_normal"), ("zone", "hand")), Do = [B("do.special_summon_self")] }],
    };

    [EngineTheory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void Obelisk_NothingCanRespondToItsNormalSummon(bool noChain, int answers)
    {
        new EngineScenario(Obelisk(noChain), Answerer()).Card(ObeliskId, 0, "hand").Card(Warrior, 0, "mzone", 3).Card(Answer, 1, "hand")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Summon(ObeliskId).Activate(Answer)
            .ExpectActivations(answers)
            .Check(In(ObeliskId, 0, "LOCATION_MZONE"))
            .Check(answers == 0 ? In(Answer, 1, "LOCATION_HAND") : In(Answer, 1, "LOCATION_MZONE"))
            .AssertPasses();
    }

    /// <summary>Your opponent's Trap: "When your opponent would Normal Summon a monster: Negate the Summon, and if you do, destroy that monster."</summary>
    private static CardProject SummonNegator() => new()
    {
        Card = new CardData { Id = Negator, Name = "Summon Negator", Kind = CardKind.Trap },
        Effects = [new EffectEvent { When = B("when.summon_attempt", ("mode", "activate"), ("who", "opponent"), ("how", "normal")), Do = [B("do.negate_summon", ("and", "destroy"))] }],
    };

    [EngineTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Obelisk_ItsNormalSummonCannotBeNegated(bool notNegated)
    {
        new EngineScenario(Obelisk(noChain: false, notNegated), SummonNegator()).Card(ObeliskId, 0, "hand").Card(Warrior, 0, "mzone", 3).Card(Negator, 1, "szone")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Summon(ObeliskId).Activate(Negator)
            .ExpectActivations(notNegated ? 0 : 1)
            .Check(notNegated ? In(ObeliskId, 0, "LOCATION_MZONE") : In(ObeliskId, 0, "LOCATION_GRAVE"))
            .AssertPasses();
    }

    // ---------------------------------------------------------------- The Winged Dragon of Ra

    private static CardProject Ra(int left = 100) => new()
    {
        Card = new CardData
        {
            Id = RaId, Name = "The Winged Dragon of Ra", Frame = MonsterFrame.Effect, Race = CardRace.DivineBeast, Attribute = CardAttribute.Divine, Level = 10, Atk = 0, Def = 0,
            CannotBeSpecialSummoned = true,
        },
        Effects =
        [
            Lasting(B(TributeSummonPassive.BlockId)),
            Lasting(B("passive.self_rule", ("rule", "summon_not_negated"))),
            Lasting(B("passive.self_rule", ("rule", "summon_no_chain_other"))),
            new EffectEvent
            {
                When = B("when.summoned", ("how", "normal")),
                Cost = [B("cost.pay_lp", ("how", "to_one"), ("left", left.ToString(System.Globalization.CultureInfo.InvariantCulture)))],
                Do = [B(StatsFromValueAction.BlockId, ("stat", "both"), ("value", "lp_paid"), ("duration", "always")).With("cards", new CardFilter { ThisCard = true })],
            },
            new EffectEvent
            {
                When = B("when.ignition", ("zone", "monster_zone")),
                Cost = [B("cost.pay_lp", ("amount", "1000"))],
                Do = [B("do.destroy", ("target", "true")).With("cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster })],
            },
        ],
    };

    [Fact]
    public void Ra_Wording()
    {
        Assert.Equal("Cannot be Special Summoned. Requires 3 Tributes to Normal Summon (cannot be Normal Set). This card's Normal Summon cannot be negated. " +
                     "When Normal Summoned, other cards and effects cannot be activated. If this card is Normal Summoned: You can pay LP so that you only have 100 left; " +
                     "this card gains that much ATK/DEF. You can pay 1000 LP, then target 1 monster on the field; destroy it.", EffectTextGenerator.Generate(Ra()));
        var lua = Lua(Ra());
        Assert.Contains("return function(e,rp,tp) return e:GetHandler()==c end", lua);
        Assert.Contains("Duel.GetLP(tp)>100 and Duel.CheckLPCost(tp,Duel.GetLP(tp)-100)", lua);
    }

    // ---------------------------------------------------------------- The Winged Dragon of Ra - Sphere Mode

    private const long SphereId = 100710008;

    private static CardProject Sphere() => new()
    {
        Card = new CardData
        {
            Id = SphereId, Name = "The Winged Dragon of Ra - Sphere Mode", Frame = MonsterFrame.Effect, Race = CardRace.DivineBeast, Attribute = CardAttribute.Divine,
            Level = 10, Atk = 0, Def = 0, CannotBeSpecialSummoned = true,
        },
        Effects =
        [
            Lasting(B(TributeSummonPassive.BlockId, ("side", "either"))),
            Lasting(B("passive.self_rule", ("rule", "cannot_attack"))),
        ],
    };

    [Fact]
    public void Sphere_Wording()
    {
        Assert.Equal("Cannot be Special Summoned. Requires 3 Tributes from either side of the field to Normal Summon to that side of the field (cannot be Normal Set), " +
                     "then shift control to this card's owner during the End Phase of the next turn. This card cannot attack.", EffectTextGenerator.Generate(Sphere()));
        var lua = Lua(Sphere());
        Assert.Contains("Duel.EnableGlobalFlag(GLOBALFLAG_BRAINWASHING_CHECK)", lua);
        Assert.Contains("Duel.SelectTribute(tp,c,3,3,mg,1-tp)", lua);
    }

    [EngineTheory]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public void Sphere_TributesTheOpponentsMonsters_AndGoesBackAfterTheNextTurn(int turns, int controller)
    {
        // With only your opponent's 3 monsters, it is Summoned to their field; at the end of the next turn it comes back.
        new EngineScenario(Sphere()).Card(SphereId, 0, "hand").Card(Fiend, 1, "mzone", 3).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Summon(SphereId).Turns(turns)
            .Check(In(SphereId, controller, "LOCATION_MZONE") + " and Duel.GetFieldGroupCount(1,LOCATION_GRAVE,0)==3")
            .AssertPasses();
    }

    // ---------------------------------------------------------------- the official scripts

    /// <summary>The Gods' Summoning rules read back from their official scripts as the blocks above.</summary>
    [EngineTheory]
    [InlineData(10000000, "summon_no_chain")]
    [InlineData(10000020, "summon_no_chain")]
    [InlineData(10000010, "summon_no_chain_other")]
    public void OfficialGods_SummoningRulesTranslate(long id, string chain)
    {
        var lua = File.ReadAllText(Path.Combine(EngineScenario.EngineScripts!, $"c{id}.lua"));
        var card = new CardData { Id = 100710100, Name = "God", Frame = MonsterFrame.Effect, Level = 10, Atk = 4000, Def = 4000 };
        var blocks = ScriptTranslator.Translate(lua, id, card).Pieces.SelectMany(p => p.Effects).SelectMany(e => e.Do).ToList();
        Assert.Contains(blocks, b => b.BlockId == TributeSummonPassive.BlockId && b.Values.GetValueOrDefault("count") == "3" && b.Values.GetValueOrDefault("set", "no_set") == "no_set");
        Assert.Contains(blocks, b => b.BlockId == "passive.self_rule" && b.Values.GetValueOrDefault("rule") == "summon_not_negated");
        Assert.Contains(blocks, b => b.BlockId == "passive.self_rule" && b.Values.GetValueOrDefault("rule") == chain);
    }

    // ---------------------------------------------------------------- Ra's Disciple

    private const long BigMonster = 100710007;

    private static CardProject Disciple() => new()
    {
        Card = new CardData { Id = DiscipleId, Name = "Ra's Disciple", Frame = MonsterFrame.Effect, Race = CardRace.Fiend, Attribute = CardAttribute.Light, Level = 4, Atk = 100, Def = 100 },
        Effects =
        [
            new EffectEvent
            {
                When = B("when.summoned", ("how", "normal")), AlsoWhen = [B("when.summoned", ("how", "special")), B("when.summoned", ("how", "flip"))],
                Do = [B("do.special_summon", ("count", "2"), ("mode", "up_to")).With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand | Zone.Deck, CardType = FilterCardType.Monster, CardCode = DiscipleId })],
            },
            Lasting(B("passive.self_rule", ("rule", "tribute_only_for")).With("cards", new CardFilter { CardType = FilterCardType.Monster, CardCode = ObeliskId, AltCodes = [RaId] })),
            Lasting(B("passive.lock", ("who", "you"), ("what", "special"), ("by_effect", "true")).With("cards", new CardFilter { CardType = FilterCardType.Monster, CardCode = DiscipleId })),
        ],
    };

    /// <summary>A Level 7 monster: its Tribute Summon is not one of the Gods'.</summary>
    private static CardProject Big() => new()
    {
        Card = new CardData { Id = BigMonster, Name = "Big Monster", Frame = MonsterFrame.Normal, Race = CardRace.Dragon, Attribute = CardAttribute.Dark, Level = 7, Atk = 2500, Def = 2000 },
    };

    /// <summary>A Spell: "Special Summon 1 monster from your hand."</summary>
    private static CardProject SummonSpell() => new()
    {
        Card = new CardData { Id = Summoner, Name = "Hand Summoner", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.special_summon").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.Hand, CardType = FilterCardType.Monster })] }],
    };

    [Fact]
    public void Disciple_Wording()
    {
        var d = Disciple();
        Assert.Equal("If this card is Normal, Flip, or Special Summoned: You can Special Summon up to 2 \"Ra's Disciple\" from your hand or Deck. " +
                     "Cannot be Tributed, except for the Tribute Summon of \"Obelisk the Tormentor\" or \"The Winged Dragon of Ra\". " +
                     "You cannot Special Summon monsters, except by the effect of \"Ra's Disciple\".", EffectTextGenerator.Generate(d, Pack(d, Obelisk(), Ra())));
        var lua = Lua(d);
        Assert.Contains("return se==nil or not s.lockfilter(se:GetHandler())", lua);
        Assert.Contains("SetCode(EFFECT_UNRELEASABLE_SUM)", lua);
    }

    [EngineFact]
    public void Disciple_SummonsTheOthers()
    {
        new EngineScenario(Disciple()).Card(DiscipleId, 0, "hand").Card(DiscipleId, 0, "deck", 2).Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Summon(DiscipleId).Activate(DiscipleId, 3)
            .Check("Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_MZONE,0,nil," + DiscipleId + ")==3").AssertPasses();
    }

    [EngineFact]
    public void Disciple_LetsNothingElseSpecialSummon()
    {
        // A Spell can't Special Summon a monster from the hand while it is face-up.
        new EngineScenario(Disciple(), SummonSpell()).Card(DiscipleId, 0, "mzone").Card(Summoner, 0, "hand").Card(Fiend, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(Summoner).ExpectActivations(0).Check(In(Fiend, 0, "LOCATION_HAND")).AssertPasses();
    }

    [EngineTheory]
    [InlineData(ObeliskId, true)]
    [InlineData(BigMonster, false)]
    public void Disciple_CanOnlyBeTributedForTheGods(long summoned, bool tributed)
    {
        new EngineScenario(Disciple(), Obelisk(), Big()).Card(DiscipleId, 0, "mzone", 3).Card(summoned, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Summon(summoned)
            .Check(tributed ? In(summoned, 0, "LOCATION_MZONE") + " and Duel.GetFieldGroupCount(0,LOCATION_GRAVE,0)==3" : In(summoned, 0, "LOCATION_HAND"))
            .AssertPasses();
    }

    [EngineFact]
    public void Disciple_CannotBeTributedForACost()
    {
        // Obelisk's "Tribute 2 other monsters" can't use them.
        new EngineScenario(Disciple(), Obelisk()).Card(ObeliskId, 0, "mzone").Card(DiscipleId, 0, "mzone", 2).Card(Fiend, 1, "mzone").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(ObeliskId).ExpectActivations(0).Check(In(Fiend, 1, "LOCATION_MZONE")).AssertPasses();
    }

    [EngineFact]
    public void Ra_PaysDownTo100_AndOnlyItsOwnEffectsRespond()
    {
        // Ra's own effect activates on its Summon; the opponent's monster in the hand can't respond.
        new EngineScenario(Ra(), Answerer()).Card(RaId, 0, "hand").Card(Warrior, 0, "mzone", 3).Card(Answer, 1, "hand")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5).Summon(RaId).Activate(RaId).Activate(Answer)
            .ExpectActivations(1)
            .Check("Duel.GetLP(0)==100")
            .Check($"{Card(RaId, 0)}:GetAttack()==7900 and {Card(RaId, 0)}:GetDefense()==7900")
            .Check(In(Answer, 1, "LOCATION_HAND"))
            .AssertPasses();
    }
}
