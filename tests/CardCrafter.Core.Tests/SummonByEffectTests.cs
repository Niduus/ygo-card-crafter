using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Synchro, Xyz and Link Summons by effect, Rank-Up-Magic, and Pendulums (Scales, extra Pendulum Summons, Pendulum
/// Zone effects and the Pendulum Effect text), played in the engine.
/// </summary>
public class SummonByEffectTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static BlockInstance With(BlockInstance b, string key, CardFilter f) { b.Filters[key] = f; return b; }

    private static string Find(long code, int player, string loc) => $"Duel.GetFirstMatchingCard(Card.IsCode,{player},{loc},0,nil,{code})";
    private static string In(long code, string loc, int player = 0) => $"Duel.IsExistingMatchingCard(Card.IsCode,{player},{loc},0,1,nil,{code})";

    private static CardProject Spell(long id, params BlockInstance[] actions) => new()
    {
        Card = new CardData { Id = id, Name = "Spell " + id, Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [.. actions] }],
    };

    private static CardProject Xyz(long id, int rank, CardAttribute attr = CardAttribute.Dark, int[]? archetypes = null) => new()
    {
        Card = new CardData { Id = id, Name = "Rank " + rank + " " + id, Frame = MonsterFrame.Xyz, Level = rank, Atk = 2000, Def = 2000, Attribute = attr, Archetypes = [.. archetypes ?? []] },
        Procedure = new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2 },
    };

    private static CardProject Pendulum(long id, int scale, int level = 4, MonsterFrame frame = MonsterFrame.Effect, int[]? archetypes = null, params EffectEvent[] effects) => new()
    {
        Card = new CardData
        {
            Id = id, Name = "Pendulum " + id, Frame = frame, Abilities = MonsterAbility.Pendulum, Level = level, LeftScale = scale, RightScale = scale,
            Atk = 1000, Def = 1000, Archetypes = [.. archetypes ?? []],
        },
        Effects = [.. effects],
    };

    /// <summary>The scripted card that moves cards around at the first Standby Phase (see RequirementMatrixTests).</summary>
    private static CardProject Rigger(params string[] lines) => RequirementMatrix.RiggerCard(string.Join("\n", lines.Select(l => "\t" + l)));

    private static string ToPendulumZone(long code) => $"Duel.MoveToField(find({code},LOCATION_HAND,tp),tp,tp,LOCATION_PZONE,POS_FACEUP,true)";

    // ---------------------------------------------------------------- Synchro/Xyz/Link Summons by effect

    [EngineTheory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void WonderXyz_XyzSummonsUsingMonstersYouControl(int warriors, bool summons)
    {
        var rank4 = Xyz(100170001, 4);
        var spell = Spell(100170002, B(ExtraDeckSummonAction.BlockId, ("kind", "xyz")));
        Assert.Equal("Immediately after this effect resolves, Xyz Summon 1 Xyz Monster using monsters you control as material.", EffectTextGenerator.Generate(spell));
        new EngineScenario(rank4, spell).Card(Warrior, 0, "mzone", warriors).Card(rank4.Card.Id, 0, "extra").Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Activate(spell.Card.Id).ExpectActivations(summons ? 1 : 0)
            .Check(summons ? $"{Find(rank4.Card.Id, 0, "LOCATION_MZONE")}:GetOverlayCount()==2 and {Find(rank4.Card.Id, 0, "LOCATION_MZONE")}:IsSummonType(SUMMON_TYPE_XYZ)" : In(rank4.Card.Id, "LOCATION_EXTRA"))
            .AssertPasses();
    }

    [EngineFact]
    public void QuickEffect_SynchroSummonsDuringTheOpponentsTurn()
    {
        var synchro = new CardProject
        {
            Card = new CardData { Id = 100170011, Name = "Level 6 Synchro", Frame = MonsterFrame.Synchro, Level = 6, Atk = 2400, Def = 1000 },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Synchro, MinMaterials = 1 },
        };
        var tuner = new CardProject
        {
            Card = new CardData { Id = 100170010, Name = "Quick Tuner", Frame = MonsterFrame.Effect, Abilities = MonsterAbility.Tuner, Level = 2, Atk = 500, Def = 500 },
            Effects =
            [
                new EffectEvent
                {
                    When = B("when.quick"), If = [B("if.turn", ("whose", "opponent"))],
                    Do = [B(ExtraDeckSummonAction.BlockId, ("kind", "synchro"))], Limit = OncePerTurn.Hard,
                },
            ],
        };
        Assert.Equal("During your opponent's turn (Quick Effect): You can Synchro Summon 1 Synchro Monster using monsters you control as material. " +
                     "You can only use this effect of \"Quick Tuner\" once per turn.", EffectTextGenerator.Generate(tuner));
        new EngineScenario(synchro, tuner).Card(tuner.Card.Id, 0, "mzone").Card(Warrior, 0, "mzone").Card(synchro.Card.Id, 0, "extra")
            .Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(tuner.Card.Id).Turns(2).ExpectActivations(1)
            .Check(In(synchro.Card.Id, "LOCATION_MZONE")).Check($"{Find(synchro.Card.Id, 0, "LOCATION_MZONE")}:IsSummonType(SUMMON_TYPE_SYNCHRO)")
            .AssertPasses();
    }

    [EngineFact]
    public void LinkSummon_UsingOnlySomeMonstersYouControl()
    {
        var link = new CardProject
        {
            Card = new CardData { Id = 100170021, Name = "Link Two", Frame = MonsterFrame.Link, Atk = 1500, LinkMarkers = LinkMarker.BottomLeft | LinkMarker.BottomRight },
            Procedure = new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 2, MaxMaterials = 2 },
        };
        var summon = With(B(ExtraDeckSummonAction.BlockId, ("kind", "link"), ("using", "only")), "mats", new CardFilter { CardType = FilterCardType.Monster, Race = CardRace.Warrior });
        var spell = Spell(100170022, summon);
        Assert.Equal("Immediately after this effect resolves, Link Summon 1 Link Monster using only Warrior monsters you control.", EffectTextGenerator.Generate(spell));
        // The Fiend is first on the field: only the Warriors may be used.
        new EngineScenario(link, spell).Card(Fiend, 0, "mzone").Card(Warrior, 0, "mzone", 2).Card(link.Card.Id, 0, "extra").Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Activate(spell.Card.Id).ExpectActivations(1)
            .Check(In(link.Card.Id, "LOCATION_MZONE")).Check(In(Fiend, "LOCATION_MZONE")).Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_GRAVE,0,nil,{Warrior})==2")
            .AssertPasses();
        // With only a Fiend and a Warrior, it can't be activated.
        new EngineScenario(link, spell).Card(Fiend, 0, "mzone").Card(Warrior, 0, "mzone").Card(link.Card.Id, 0, "extra").Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Activate(spell.Card.Id).ExpectActivations(0).AssertPasses();
    }

    // ---------------------------------------------------------------- Rank-Up-Magic

    [EngineTheory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void RankUpMagic_SummonsAnXyzMonster1RankHigher_AndTransfersTheMaterials(int higher, bool summons)
    {
        var rank4 = Xyz(100170031, 4);
        var next = Xyz(100170032, higher);
        var rum = Spell(100170033, B(RankUpAction.BlockId));
        Assert.Equal("Target 1 Xyz Monster you control; Special Summon from your Extra Deck, 1 Xyz Monster that is 1 Rank higher than that monster you control, by using it as material. " +
                     "(This is treated as an Xyz Summon. Transfer its materials to the Summoned monster.)", EffectTextGenerator.Generate(rum));
        var s = new EngineScenario(rank4, next, rum).Card(Warrior, 0, "mzone", 2).Card(rank4.Card.Id, 0, "extra").Card(next.Card.Id, 0, "extra").Card(rum.Card.Id, 0, "hand")
            .Card(Warrior, 0, "deck", 5).SpSummon(rank4.Card.Id).Activate(rum.Card.Id).ExpectActivations(summons ? 1 : 0);
        if (summons)
            s.Check(In(next.Card.Id, "LOCATION_MZONE")).Check($"{Find(next.Card.Id, 0, "LOCATION_MZONE")}:GetOverlayCount()==3")
                .Check($"{Find(next.Card.Id, 0, "LOCATION_MZONE")}:IsSummonType(SUMMON_TYPE_XYZ)").Check($"not {In(rank4.Card.Id, "LOCATION_MZONE")}");
        else s.Check(In(rank4.Card.Id, "LOCATION_MZONE"));
        s.AssertPasses();
    }

    [EngineTheory]
    [InlineData(CardAttribute.Dark, true)]
    [InlineData(CardAttribute.Light, false)]
    public void RankUp_WithTheSameAttribute_2RanksHigher(CardAttribute attribute, bool summons)
    {
        var rank4 = Xyz(100170041, 4, CardAttribute.Dark);
        var rank6 = Xyz(100170042, 6, attribute);
        var rum = Spell(100170043, B(RankUpAction.BlockId, ("ranks", "2"), ("same", "attribute")));
        Assert.Contains("1 Xyz Monster with the same Attribute as that monster you control but 2 Ranks higher, by using it as material", EffectTextGenerator.Generate(rum));
        new EngineScenario(rank4, rank6, rum).Card(Warrior, 0, "mzone", 2).Card(rank4.Card.Id, 0, "extra").Card(rank6.Card.Id, 0, "extra").Card(rum.Card.Id, 0, "hand")
            .Card(Warrior, 0, "deck", 5).SpSummon(rank4.Card.Id).Activate(rum.Card.Id).ExpectActivations(summons ? 1 : 0)
            .Check(summons ? In(rank6.Card.Id, "LOCATION_MZONE") : In(rank6.Card.Id, "LOCATION_EXTRA")).AssertPasses();
    }

    [EngineFact]
    public void RaptorsForce_SummonsTheXyzMonsterFromTheGY_ThenRanksItUp()
    {
        var rank4 = Xyz(100170051, 4);
        var rank5 = Xyz(100170052, 5);
        var rum = Spell(100170053, With(B(RankUpAction.BlockId), "material", new CardFilter { Owner = CardOwner.You, Zones = Zone.Graveyard, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Xyz }));
        Assert.Equal("Target 1 Xyz Monster in your GY; Special Summon it, then Special Summon from your Extra Deck, 1 Xyz Monster that is 1 Rank higher than that monster, by using it as material. " +
                     "(This is treated as an Xyz Summon.)", EffectTextGenerator.Generate(rum));
        new EngineScenario(rank4, rank5, rum).Card(rank4.Card.Id, 0, "grave").Card(rank5.Card.Id, 0, "extra").Card(rum.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            // An Xyz Monster in the GY must have been properly Summoned to come back.
            .Setup($"{Find(rank4.Card.Id, 0, "LOCATION_GRAVE")}:CompleteProcedure()")
            .Activate(rum.Card.Id).ExpectActivations(1)
            .Check(In(rank5.Card.Id, "LOCATION_MZONE")).Check($"{Find(rank5.Card.Id, 0, "LOCATION_MZONE")}:GetOverlayCount()==1")
            .Check($"not {In(rank4.Card.Id, "LOCATION_GRAVE")}").AssertPasses();
    }

    [EngineFact]
    public void ThisCardRanksItselfUp()
    {
        var rank4 = Xyz(100170061, 4);
        rank4.Effects.Add(new EffectEvent { When = B("when.ignition"), Do = [With(B(RankUpAction.BlockId), "material", new CardFilter { ThisCard = true })] });
        var rank5 = Xyz(100170062, 5);
        Assert.Equal("You can Special Summon from your Extra Deck, 1 Xyz Monster that is 1 Rank higher than this card, by using this face-up card you control as material. " +
                     "(This is treated as an Xyz Summon. Transfer its materials to the Summoned monster.)", EffectTextGenerator.Describe(rank4.Effects[0], rank4.Card));
        new EngineScenario(rank4, rank5).Card(Warrior, 0, "mzone", 2).Card(rank4.Card.Id, 0, "extra").Card(rank5.Card.Id, 0, "extra").Card(Warrior, 0, "deck", 5)
            .SpSummon(rank4.Card.Id).Activate(rank4.Card.Id).ExpectActivations(1)
            .Check($"{Find(rank5.Card.Id, 0, "LOCATION_MZONE")}:GetOverlayCount()==3").AssertPasses();
    }

    // ---------------------------------------------------------------- Pendulum Scales

    [EngineTheory]
    [InlineData("always", 7)]
    [InlineData("turn", 1)]
    public void MakeThisCardsPendulumScaleBecome7(string duration, int after)
    {
        var pend = Pendulum(100170071, 1, effects: new EffectEvent
        {
            When = B("when.ignition", ("zone", "pendulum_zone")), Limit = OncePerTurn.Soft,
            Do = [B(PendulumScaleAction.BlockId, ("amount", "7"), ("duration", duration))],
        });
        Assert.Equal($"Once per turn: You can make this card's Pendulum Scale become 7{(duration == "turn" ? " until the end of this turn" : "")}.", EffectTextGenerator.GeneratePendulum(pend));
        Assert.Equal("", EffectTextGenerator.Generate(pend));
        // The duel is checked at the start of the next turn: a change until the end of this turn is gone by then.
        new EngineScenario(pend, Rigger(ToPendulumZone(pend.Card.Id))).Card(pend.Card.Id, 0, "hand").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "deck", 5)
            .Activate(pend.Card.Id).ExpectActivations(1)
            .Check($"{Find(pend.Card.Id, 0, "LOCATION_PZONE")}:GetLeftScale()=={after} and {Find(pend.Card.Id, 0, "LOCATION_PZONE")}:GetRightScale()=={after}").AssertPasses();
    }

    [EngineTheory]
    [InlineData(3, 1)]
    [InlineData(12, 0)]
    public void IncreaseThisCardsPendulumScaleBy1_Max12(int scale, int activations)
    {
        var pend = Pendulum(100170081, scale, effects: new EffectEvent
        {
            When = B("when.ignition", ("zone", "pendulum_zone")), Limit = OncePerTurn.Soft,
            Do = [B(PendulumScaleAction.BlockId, ("mode", "increase"), ("amount", "1"), ("limit", "12"), ("duration", "always"))],
        });
        Assert.Equal("Once per turn: You can increase this card's Pendulum Scale by 1 (max. 12).", EffectTextGenerator.GeneratePendulum(pend));
        new EngineScenario(pend, Rigger(ToPendulumZone(pend.Card.Id))).Card(pend.Card.Id, 0, "hand").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "deck", 5)
            .Activate(pend.Card.Id).ExpectActivations(activations)
            .Check($"{Find(pend.Card.Id, 0, "LOCATION_PZONE")}:GetLeftScale()=={scale + activations}").AssertPasses();
    }

    [EngineTheory]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    public void UnlessYouHaveAMagicianInYourOtherPendulumZone_TheScaleBecomes4(bool partner, int scale)
    {
        const int magician = 0x7b1;
        var none = With(B("if.no_cards"), "cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.PendulumZone, CardType = FilterCardType.Any, Archetype = magician, ExcludeThisCard = true });
        var gazer = Pendulum(100170091, 1, effects: new EffectEvent
        {
            When = B("when.while_face_up", ("zone", "pendulum_zone")), If = [none], Do = [B(PendulumScalePassive.BlockId, ("amount", "4"))],
        });
        var other = Pendulum(100170092, 8, archetypes: [magician]);
        var pack = new CardPack { Archetypes = [new Archetype { Code = magician, Name = "Magician" }] };
        Assert.Equal("Unless you have a \"Magician\" card in your other Pendulum Zone, this card's Pendulum Scale becomes 4.", EffectTextGenerator.GeneratePendulum(gazer, pack));
        var rig = partner ? Rigger(ToPendulumZone(gazer.Card.Id), ToPendulumZone(other.Card.Id)) : Rigger(ToPendulumZone(gazer.Card.Id));
        new EngineScenario(gazer, other, rig).Card(gazer.Card.Id, 0, "hand").Card(other.Card.Id, 0, "hand").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "deck", 5)
            .Check($"{Find(gazer.Card.Id, 0, "LOCATION_PZONE")}:GetLeftScale()=={scale}").AssertPasses();
    }

    // ---------------------------------------------------------------- Pendulum Summons

    [EngineTheory]
    [InlineData(null, 1)]
    [InlineData("hand", 2)]
    [InlineData("extra", 1)]
    public void ExtraPendulum_YouCanPendulumSummonAgainThisTurn(string? from, int summoned)
    {
        var left = Pendulum(100170101, 1);
        var right = Pendulum(100170102, 8);
        var extra = Spell(100170103, B(ExtraPendulumAction.BlockId, ("from", from ?? "any")));
        var s = new EngineScenario(left, right, extra, Rigger(ToPendulumZone(left.Card.Id), ToPendulumZone(right.Card.Id)))
            .Card(left.Card.Id, 0, "hand").Card(right.Card.Id, 0, "hand").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "hand", 3).Card(Warrior, 0, "deck", 5)
            .SpSummon(left.Card.Id);
        if (from is not null) s.Card(extra.Card.Id, 0, "hand").Activate(extra.Card.Id).ExpectActivations(1);
        s.Check($"Duel.GetMatchingGroupCount(Card.IsSummonType,0,LOCATION_MZONE,0,nil,SUMMON_TYPE_PENDULUM)=={summoned}").AssertPasses();
        if (from == "extra")
            Assert.Equal("During your Main Phase this turn, you can conduct 1 Pendulum Summon of a monster(s) from your Extra Deck in addition to your Pendulum Summon. " +
                         "(You can only gain this effect once per turn.)", EffectTextGenerator.Generate(extra));
    }

    [EngineFact]
    public void ExtraPendulum_CanOnlyBeGainedOncePerTurn()
    {
        var extra = Spell(100170111, With(B(ExtraPendulumAction.BlockId), "cards", new CardFilter { CardType = FilterCardType.Monster, Archetype = 0x7b2 }));
        var pack = new CardPack { Archetypes = [new Archetype { Code = 0x7b2, Name = "Dee Dee" }] };
        Assert.Equal("During your Main Phase this turn, you can conduct 1 Pendulum Summon of a \"Dee Dee\" monster(s) in addition to your Pendulum Summon. " +
                     "(You can only gain this effect once per turn.)", EffectTextGenerator.Generate(extra, pack));
        // Two copies: the second can't be activated once the effect is gained.
        new EngineScenario(extra).Card(extra.Card.Id, 0, "hand", 2).Card(Warrior, 0, "deck", 5).Activate(extra.Card.Id, 2).ExpectActivations(1)
            .Check($"Duel.GetMatchingGroupCount(Card.IsCode,0,LOCATION_HAND,0,nil,{extra.Card.Id})==1").AssertPasses();
    }

    // ---------------------------------------------------------------- Pendulum Zone effects

    [EngineTheory]
    [InlineData("mzone", 1)]
    [InlineData("pzone", 0)]
    public void IfThisCardInTheMonsterZoneIsDestroyed_PlaceItInYourPendulumZone(string from, int activations)
    {
        var pend = Pendulum(100170121, 3, effects: new EffectEvent
        {
            When = B("when.destroyed", ("by", "in_mzone")), Do = [With(B(PlaceAction.BlockId, ("where", "pzone")), "cards", new CardFilter { ThisCard = true })],
        });
        Assert.Equal("If this card in the Monster Zone is destroyed: You can place this card in your Pendulum Zone.", EffectTextGenerator.Generate(pend));
        var rig = from == "mzone"
            ? Rigger($"Duel.Destroy(find({pend.Card.Id},LOCATION_MZONE,tp),REASON_EFFECT)")
            : Rigger(ToPendulumZone(pend.Card.Id), $"Duel.Destroy(find({pend.Card.Id},LOCATION_PZONE,tp),REASON_EFFECT)");
        new EngineScenario(pend, rig).Card(pend.Card.Id, 0, from == "mzone" ? "mzone" : "hand").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "deck", 5)
            .Activate(pend.Card.Id).ExpectActivations(activations)
            .Check(activations == 1 ? In(pend.Card.Id, "LOCATION_PZONE") : $"{Find(pend.Card.Id, 0, "LOCATION_EXTRA")}:IsFaceup()").AssertPasses();
        // As official Pendulum Monsters: only a free Pendulum Zone is checked.
        Assert.DoesNotContain("IsForbidden", ScriptCompiler.Compile(pend).Lua);
    }

    [EngineFact]
    public void SpecialSummonThisCardFromYourPendulumZone()
    {
        var pend = Pendulum(100170131, 5, effects: new EffectEvent { When = B("when.ignition", ("zone", "pendulum_zone")), Do = [B("do.special_summon_self")] });
        Assert.Equal("You can Special Summon this card from your Pendulum Zone.", EffectTextGenerator.GeneratePendulum(pend));
        new EngineScenario(pend, Rigger(ToPendulumZone(pend.Card.Id))).Card(pend.Card.Id, 0, "hand").Card(RequirementMatrix.Rigger, 0, "grave").Card(Warrior, 0, "deck", 5)
            .Activate(pend.Card.Id).ExpectActivations(1).Check(In(pend.Card.Id, "LOCATION_MZONE")).AssertPasses();
    }

    [EngineTheory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void AddAFaceUpPendulumMonsterFromYourExtraDeck_NotAFaceDownOne(bool faceUp, int activations)
    {
        var shown = Pendulum(100170141, 2);
        var hidden = Pendulum(100170142, 2);
        var pick = new CardFilter { Owner = CardOwner.You, Zones = Zone.ExtraDeck, CardType = FilterCardType.Monster, MonsterKind = MonsterKind.Pendulum, FaceUpOnly = true };
        var spell = Spell(100170143, With(B("do.add_to_hand"), "cards", pick));
        Assert.Equal("Add 1 face-up Pendulum Monster from your Extra Deck to your hand.", EffectTextGenerator.Generate(spell));
        Assert.Contains("c:IsFaceup()", ScriptCompiler.Compile(spell).Lua);
        // The rigger turns one Pendulum Monster face-up in the Extra Deck; banished, it does nothing.
        new EngineScenario(shown, hidden, spell, Rigger($"Duel.SendtoExtraP(find({shown.Card.Id},LOCATION_HAND,tp),nil,REASON_EFFECT)"))
            .Card(hidden.Card.Id, 0, "extra").Card(spell.Card.Id, 0, "hand").Card(Warrior, 0, "deck", 5)
            .Card(shown.Card.Id, 0, faceUp ? "hand" : "deck").Card(RequirementMatrix.Rigger, 0, faceUp ? "grave" : "removed")
            .Activate(spell.Card.Id).ExpectActivations(activations)
            .Check(In(hidden.Card.Id, "LOCATION_EXTRA")).Check(faceUp ? In(shown.Card.Id, "LOCATION_HAND") : "true").AssertPasses();
        Assert.Equal("You have a face-up Pendulum Monster in your Extra Deck", SentenceCase.Upper(new CardsExistCondition().Describe(new BlockArgs(new CardsExistCondition(), With(B("if.cards_exist"), "cards", pick)))));
    }

    [Fact]
    public void PendulumZonePicker_ReadsAndCompilesAsThePendulumZone()
    {
        var other = new CardFilter { Owner = CardOwner.You, Zones = Zone.PendulumZone, CardType = FilterCardType.Any, ExcludeThisCard = true };
        Assert.Equal("card in your other Pendulum Zone", FilterText.Describe(other, null, preposition: "in"));
        Assert.Equal("2 cards in your Pendulum Zones", "2 " + FilterText.Describe(new CardFilter { Owner = CardOwner.You, Zones = Zone.PendulumZone }, null, plural: true, preposition: "in"));
        Assert.Equal("card in a Pendulum Zone", FilterText.Describe(new CardFilter { Owner = CardOwner.Either, Zones = Zone.PendulumZone }, null, preposition: "in"));
        Assert.Equal("LOCATION_PZONE", Lua.Locations(Zone.PendulumZone));
        Assert.Equal("LOCATION_SZONE", Lua.Locations(Zone.SpellTrapZone | Zone.PendulumZone));
        var destroy = Spell(100170151, With(B("do.destroy", ("target", "true")), "cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.PendulumZone, CardType = FilterCardType.Any }));
        var lua = ScriptCompiler.Compile(destroy).Lua;
        Assert.Contains("chkc:IsLocation(LOCATION_PZONE)", lua);
        Assert.Contains("LOCATION_PZONE,LOCATION_PZONE", lua);
    }

    [EngineFact]
    public void DestroyACardInAPendulumZone()
    {
        var scale = Pendulum(100170161, 4);
        var destroy = Spell(100170162, With(B("do.destroy", ("target", "true")), "cards", new CardFilter { Owner = CardOwner.Either, Zones = Zone.PendulumZone, CardType = FilterCardType.Any }));
        new EngineScenario(scale, destroy, Rigger(ToPendulumZone(scale.Card.Id))).Card(scale.Card.Id, 0, "hand").Card(destroy.Card.Id, 0, "hand").Card(RequirementMatrix.Rigger, 0, "grave")
            .Card(Warrior, 0, "deck", 5).Activate(destroy.Card.Id).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_PZONE,0)==0").Check($"{Find(scale.Card.Id, 0, "LOCATION_EXTRA")}:IsFaceup()").AssertPasses();
    }

    // ---------------------------------------------------------------- Pendulum Effect text

    [Fact]
    public void PendulumEffects_GoInThePendulumBox_AndTheRestInTheMonsterEffect()
    {
        var pend = Pendulum(100170171, 2, effects:
        [
            new EffectEvent { When = B("when.ignition", ("zone", "pendulum_zone")), Do = [B("do.special_summon_self")], Limit = OncePerTurn.Hard },
            new EffectEvent { When = B("when.summoned", ("how", "normal")), Do = [B("do.draw")] },
            new EffectEvent { When = B("when.destroyed", ("by", "in_pzone")), Do = [B("do.draw")] },
        ]);
        pend.Card.Name = "Swing";
        Assert.Equal("You can Special Summon this card from your Pendulum Zone. You can only use this effect of \"Swing\" once per turn.", EffectTextGenerator.GeneratePendulum(pend));
        // "If this card in the Pendulum Zone is destroyed" is a Monster Effect, as printed cards have it.
        Assert.Equal("If this card is Normal Summoned: You can draw 1 card. If this card in the Pendulum Zone is destroyed: You can draw 1 card.", EffectTextGenerator.Generate(pend));
        Assert.Equal("[ Pendulum Effect ]\nYou can Special Summon this card from your Pendulum Zone. You can only use this effect of \"Swing\" once per turn.\n[ Monster Effect ]\n" +
                     "If this card is Normal Summoned: You can draw 1 card. If this card in the Pendulum Zone is destroyed: You can draw 1 card.", EffectTextGenerator.GenerateWithSections(pend));
        // Not a Pendulum Monster: everything is its card text.
        pend.Card.Abilities = MonsterAbility.None;
        Assert.Equal("", EffectTextGenerator.GeneratePendulum(pend));
        Assert.StartsWith("You can Special Summon this card", EffectTextGenerator.Generate(pend), StringComparison.Ordinal);
    }

    [Fact]
    public void PendulumNormalMonster_KeepsItsFlavorText_AndExportsBothBoxes()
    {
        var turkey = Pendulum(100170181, 7, frame: MonsterFrame.Normal, effects: new EffectEvent
        {
            When = B("when.ignition", ("zone", "pendulum_zone")), Limit = OncePerTurn.Soft,
            Do = [B(PendulumScaleAction.BlockId, ("mode", "reduce"), ("amount", "1"), ("limit", "1"))],
        });
        turkey.Card.Text = "The taste of victory.";
        Assert.Equal("", EffectTextGenerator.Generate(turkey));
        Assert.StartsWith("[ Pendulum Effect ]\nOnce per turn: You can reduce this card's Pendulum Scale by 1 (min. 1) until the end of this turn.\n[ Flavor Text ]", EffectTextGenerator.GenerateWithSections(turkey), StringComparison.Ordinal);
        using var tmp = new TempDir();
        var pack = new CardPack { Name = "Pend", Cards = [turkey] };
        var export = Omega.PackExporter.Export(pack, tmp.Path);
        var desc = Database.CdbFile.Read(export.DatabasePath).Single().Desc;
        Assert.Equal("←7 【Pendulum Scale】 7→\nOnce per turn: You can reduce this card's Pendulum Scale by 1 (min. 1) until the end of this turn.\n【Flavor Text】\nThe taste of victory.", desc);
        // The saved card keeps its own (empty) Pendulum text: the suggestion is only written to the database.
        Assert.Equal("", turkey.Card.PendulumText);
    }

    [Fact]
    public void PendulumLock_OnAScale_ReadsAsPrinted()
    {
        var scale = Pendulum(100170191, 1, effects: new EffectEvent
        {
            When = B("when.while_face_up", ("zone", "pendulum_zone")),
            Do = [With(B("passive.lock", ("who", "you"), ("what", "pendulum")), "cards", new CardFilter { CardType = FilterCardType.Monster, Archetype = 0x7b3 })],
        });
        var pack = new CardPack { Archetypes = [new Archetype { Code = 0x7b3, Name = "Dee Dee" }] };
        Assert.Equal("You cannot Pendulum Summon monsters, except \"Dee Dee\" monsters. This effect cannot be negated.", EffectTextGenerator.GeneratePendulum(scale, pack));
        Assert.Equal("", EffectTextGenerator.Generate(scale, pack));
    }
}
