using System.Text.Json;
using CardCrafter.Core.Assistant;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// Organize: effects in the order official cards print them, conditions and costs in reading order, repeated conditions
/// and same-but-for-the-trigger effects joined, and none of it changing what the card does: the compiled scripts are
/// compared (<see cref="ScriptEquivalence"/>) and played in the engine.
/// </summary>
public class OrganizeTests
{
    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static BlockInstance Draw(int n = 1) => B("do.draw", ("count", n.ToString()));

    private static BlockInstance Search() => B("do.add_to_hand").With("cards", new CardFilter { Zones = Zone.Deck, CardType = FilterCardType.Monster, Race = CardRace.Dragon });

    private static BlockInstance YouControlADragon() =>
        B("if.cards_exist").With("cards", new CardFilter { Owner = CardOwner.You, Zones = Zone.MonsterZone, CardType = FilterCardType.Monster, Race = CardRace.Dragon });

    private static string Json(object o) => JsonSerializer.Serialize(o, PackSerializer.Options);

    /// <summary>Organizes a copy and checks it compiles to a script that does the same (descriptions and cost order aside).</summary>
    private static (CardProject Organized, OrganizeResult Result) Organized(CardProject p, CardPack? pack = null)
    {
        var before = ScriptCompiler.Compile(p, pack);
        var copy = PackSerializer.Clone(p);
        var result = CardOrganizer.Organize(copy, pack);
        var after = ScriptCompiler.Compile(copy, pack);
        Assert.Null(ScriptEquivalence.Difference(before, after, ignoreDescriptions: true, costOrder: true));
        Assert.Equal(before.Diagnostics.Select(d => d.Message).Order(), after.Diagnostics.Select(d => d.Message).Order());
        return (copy, result);
    }

    /// <summary>A monster with one effect of each kind, in the wrong order (like a card built bit by bit).</summary>
    private static CardProject Jumbled() => new()
    {
        Card = new CardData { Id = 100310001, Name = "Jumbled Dragon", Race = CardRace.Dragon, Level = 7, Atk = 2500, Def = 2000, OnlyOneOnField = true },
        Effects =
        [
            new EffectEvent { Label = "GY", When = B("when.ignition", ("zone", "graveyard")), Cost = [B("cost.this_card", ("how", "banish"))], Do = [Draw()], Limit = OncePerTurn.Hard },
            new EffectEvent { Label = "Quick", When = B("when.quick", ("timing", "opponent")), Do = [B("do.destroy")], Limit = OncePerTurn.Hard },
            new EffectEvent { Label = "Ignition", When = B("when.ignition"), Do = [Draw()], Limit = OncePerTurn.Hard },
            new EffectEvent { Label = "Boost", When = B("when.while_face_up"), Do = [B("passive.self_stats")] },
            new EffectEvent { Label = "On Summon", When = B("when.summoned", ("how", "normal")), Do = [Search()], Limit = OncePerTurn.Hard },
            new EffectEvent { Label = "From hand", When = B("when.ignition", ("zone", "hand")), Cost = [B("cost.this_card", ("how", "discard"))], Do = [Search()], Limit = OncePerTurn.Hard },
            new EffectEvent { Label = "Unaffected", When = B("when.while_face_up"), Do = [B("passive.indestructible")] },
            new EffectEvent { Label = "Procedure", When = B(SummonProcedureTrigger.BlockId), Cost = [B("cost.pay_lp", ("amount", "1000"))] },
        ],
    };

    // ---------------------------------------------------------------- the order of the effects

    [Fact]
    public void Effects_GoInTheOrderOfficialCardsPrintThem()
    {
        var (p, result) = Organized(Jumbled());
        Assert.True(result.Changed, result.Summary);
        // Procedure, lasting rule ("cannot be destroyed"), hand, on Summon, other lasting, ignition, Quick, GY. The two lasting
        // effects keep their order between themselves (Boost was before Unaffected), so they swap places with each other.
        Assert.Equal(["Procedure", "Boost", "From hand", "On Summon", "Unaffected", "Ignition", "Quick", "GY"], p.Effects.Select(e => e.Label));
        var text = EffectTextGenerator.Generate(p);
        // The Summoning procedure, then "You can only control 1", then the effects, and the combined limit last.
        Assert.True(text.IndexOf("Special Summon this card", StringComparison.Ordinal) is >= 0 and var at && at < text.IndexOf("You can only control 1", StringComparison.Ordinal), text);
        Assert.EndsWith("You can only use each effect of \"Jumbled Dragon\" once per turn.", text);
    }

    [Fact]
    public void ASpell_ActivationFirst_ThenLasting_ThenGy()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100310002, Name = "Jumbled Field", Kind = CardKind.Spell, SpellKind = SpellKind.Continuous },
            Effects =
            [
                new EffectEvent { Label = "GY", When = B("when.ignition", ("zone", "graveyard")), Cost = [B("cost.this_card", ("how", "banish"))], Do = [Draw()] },
                new EffectEvent { Label = "Lasting", When = B("when.while_face_up"), Do = [B("passive.boost_cards")] },
                new EffectEvent { Label = "Activate", When = B("when.activate"), Do = [Search()] },
            ],
        };
        var (organized, _) = Organized(p);
        Assert.Equal(["Activate", "Lasting", "GY"], organized.Effects.Select(e => e.Label));
    }

    [Fact]
    public void LastingEffects_KeepTheirOrder_SinceTheGameAppliesThemInThatOrder()
    {
        // "This card's ATK becomes 0" registered before "gains 500 ATK" gives 500; the other way it gives 0.
        var p = new CardProject
        {
            Card = new CardData { Id = 100310003, Name = "Layered", Level = 4, Atk = 1000, Def = 1000 },
            Effects =
            [
                new EffectEvent { Label = "Gains", When = B("when.while_face_up"), Do = [B("passive.self_stats")] },
                new EffectEvent { Label = "Search", When = B("when.summoned", ("how", "normal")), Do = [Search()] },
                new EffectEvent { Label = "Cannot be destroyed", When = B("when.while_face_up"), Do = [B("passive.indestructible")] },
            ],
        };
        var (organized, _) = Organized(p);
        var lasting = organized.Effects.Where(e => e.When!.BlockId == "when.while_face_up").Select(e => e.Label);
        Assert.Equal(["Gains", "Cannot be destroyed"], lasting);
    }

    [Fact]
    public void ThePendulumBox_ComesFirst()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100310004, Name = "Scales", Level = 4, Abilities = MonsterAbility.Pendulum, LeftScale = 3, RightScale = 3 },
            Effects =
            [
                new EffectEvent { Label = "Monster", When = B("when.summoned", ("how", "normal")), Do = [Draw()] },
                new EffectEvent { Label = "Pendulum", When = B("when.ignition", ("zone", "pendulum_zone")), Do = [Draw()] },
            ],
        };
        Assert.Equal(["Pendulum", "Monster"], Organized(p).Organized.Effects.Select(e => e.Label));
    }

    [Fact]
    public void EffectsSharingAOncePerTurn_GoTogether()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100310005, Name = "Sharer", Level = 4 },
            Effects =
            [
                new EffectEvent { Label = "A", When = B("when.ignition"), Do = [Draw()], Limit = OncePerTurn.Hard, SharedLimitGroup = 3 },
                new EffectEvent { Label = "B", When = B("when.quick"), Do = [B("do.destroy")], Limit = OncePerTurn.Hard },
                new EffectEvent { Label = "C", When = B("when.ignition", ("zone", "graveyard")), Do = [Draw()], Limit = OncePerTurn.Hard, SharedLimitGroup = 3 },
            ],
        };
        var (organized, _) = Organized(p);
        Assert.Equal(["A", "C", "B"], organized.Effects.Select(e => e.Label));
        // Renumbered from 1.
        Assert.Equal([1, 1, 0], organized.Effects.Select(e => e.SharedLimitGroup));
    }

    // ---------------------------------------------------------------- inside an effect

    [Fact]
    public void Conditions_ReadingOrder_RepeatsRemoved_ThisCardCostsFirst_DoUntouched()
    {
        var effect = new EffectEvent
        {
            When = B("when.quick", ("zone", "graveyard")),
            If = [YouControlADragon(), B("if.phase", ("phase", "main")), YouControlADragon()],
            Cost = [B("cost.pay_lp", ("amount", "500")), B("cost.this_card", ("how", "banish"))],
            Do = [B("do.destroy"), Draw().Linked(ActionLink.Then)],
        };
        var p = new CardProject { Card = new CardData { Id = 100310006, Name = "Tidy", Level = 4 }, Effects = [effect] };
        var doBefore = Json(effect.Do);
        var (organized, result) = Organized(p);
        var e = organized.Effects[0];
        Assert.Equal(["if.phase", "if.cards_exist"], e.If.Select(b => b.BlockId));
        Assert.Equal(["cost.this_card", "cost.pay_lp"], e.Cost.Select(b => b.BlockId));
        Assert.Equal(doBefore, Json(e.Do));
        // "banish this card from your GY" says where it is, so the condition doesn't.
        Assert.Equal("During the Main Phase, if you control a Dragon monster (Quick Effect): You can banish this card from your GY and pay 500 LP; "
                     + "destroy 1 card your opponent controls, then draw 1 card.", EffectTextGenerator.Describe(e, organized.Card));
        Assert.Contains(result.Changes, c => c.Contains("repeated condition"));
        Assert.Contains(result.Changes, c => c.Contains("reading order"));
        Assert.Contains(result.Changes, c => c.Contains("costs in the order"));
    }

    [Fact]
    public void CostsThatRememberWhatTheyPaid_StayInTheirOrder()
    {
        // Two costs that each keep how many cards they moved in the effect's label: the last one paid is what the effect
        // reads, so swapping them could change it.
        var effect = new EffectEvent
        {
            When = B("when.ignition", ("zone", "hand")),
            Cost = [B("cost.move_cards", ("count", "1")), B("cost.move_cards").With("cards", new CardFilter { ThisCard = true })],
            Do = [Draw()],
        };
        var p = new CardProject { Card = new CardData { Id = 100310007, Name = "Payer", Level = 4 }, Effects = [effect] };
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(ScriptCompiler.Compile(p).Lua, "SetLabel").Count);
        Assert.Equal(Json(effect.Cost), Json(Organized(p).Organized.Effects[0].Cost));
        // With one of them paying LP instead, this card's cost goes first.
        effect.Cost[0] = B("cost.pay_lp", ("amount", "500"));
        Assert.Equal(["cost.move_cards", "cost.pay_lp"], Organized(p).Organized.Effects[0].Cost.Select(b => b.BlockId));
    }

    // ---------------------------------------------------------------- "or when"

    [Fact]
    public void EffectsThatOnlyDifferInTheirTrigger_BecomeOne_WithOrWhen()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100310008, Name = "Twin", Level = 4 },
            Effects =
            [
                new EffectEvent { Label = "Special", When = B("when.summoned", ("how", "special")), Do = [Search()], Limit = OncePerTurn.Hard, SharedLimitGroup = 1 },
                new EffectEvent { Label = "Normal", When = B("when.summoned", ("how", "normal")), Do = [Search()], Limit = OncePerTurn.Hard, SharedLimitGroup = 1 },
            ],
        };
        Assert.Equal("If this card is Special Summoned: You can add 1 Dragon monster from your Deck to your hand. If this card is Normal Summoned: You can add 1 Dragon monster from your Deck to your hand. "
                     + "You can only use 1 \"Twin\" effect per turn, and only once that turn.", EffectTextGenerator.Generate(p));
        var (organized, _) = Organized(p);
        Assert.Single(organized.Effects);
        Assert.Equal("If this card is Normal or Special Summoned: You can add 1 Dragon monster from your Deck to your hand. You can only use this effect of \"Twin\" once per turn.",
            EffectTextGenerator.Generate(organized));
    }

    [Theory]
    [InlineData(OncePerTurn.Hard, 0)]
    [InlineData(OncePerTurn.Soft, 0)]
    [InlineData(OncePerTurn.Hard, 2)]
    public void EffectsWithTheirOwnOncePerTurn_StaySeparate(OncePerTurn limit, int secondGroup)
    {
        // Each can be used once per turn now; as one effect they would share one use.
        var p = new CardProject
        {
            Card = new CardData { Id = 100310009, Name = "Apart", Level = 4 },
            Effects =
            [
                new EffectEvent { When = B("when.summoned", ("how", "normal")), Do = [Draw()], Limit = limit, SharedLimitGroup = secondGroup == 0 ? 0 : 1 },
                new EffectEvent { When = B("when.summoned", ("how", "special")), Do = [Draw()], Limit = limit, SharedLimitGroup = secondGroup },
            ],
        };
        Assert.Equal(2, Organized(p).Organized.Effects.Count);
    }

    // ---------------------------------------------------------------- the checker itself

    [Fact]
    public void TheChecker_SeesRealDifferences()
    {
        var p = Jumbled();
        var original = ScriptCompiler.Compile(p);
        CompileResult With(Action<CardProject> change)
        {
            var copy = PackSerializer.Clone(p);
            change(copy);
            return ScriptCompiler.Compile(copy);
        }
        // The same card, with its effects in another order and its labels renamed: the same.
        Assert.Null(ScriptEquivalence.Difference(original, With(c => { c.Effects.Add(c.Effects[0]); c.Effects.RemoveAt(0); foreach (var e in c.Effects) e.Label += "!"; })));
        Assert.True(ScriptEquivalence.Same(original, With(c => (c.Effects[1], c.Effects[2]) = (c.Effects[2], c.Effects[1]))));
        // DO steps in another order, another limit, a lost effect, lasting effects swapped, two uses sharing one count: different.
        var twoSteps = PackSerializer.Clone(p);
        twoSteps.Effects[0].Do.Add(B("do.destroy"));
        var twoStepsLua = ScriptCompiler.Compile(twoSteps);
        twoSteps.Effects[0].Do.Reverse();
        Assert.False(ScriptEquivalence.Same(twoStepsLua, ScriptCompiler.Compile(twoSteps), ignoreDescriptions: true));
        Assert.False(ScriptEquivalence.Same(original, With(c => c.Effects[1].Limit = OncePerTurn.Soft), ignoreDescriptions: true));
        Assert.False(ScriptEquivalence.Same(original, With(c => c.Effects.RemoveAt(1))));
        Assert.Contains("order", ScriptEquivalence.Difference(original, With(c => (c.Effects[3], c.Effects[6]) = (c.Effects[6], c.Effects[3]))));
        Assert.False(ScriptEquivalence.Same(original, With(c => { c.Effects[1].SharedLimitGroup = 4; c.Effects[2].SharedLimitGroup = 4; }), ignoreDescriptions: true));
    }

    // ---------------------------------------------------------------- what Organize keeps and leaves

    [Fact]
    public void TypedOverStrings_FollowTheirEffects()
    {
        var p = Jumbled();
        p.StringOverrides = ["GY text", "", "", "", "On Summon text"];
        var (organized, _) = Organized(p);
        var slots = ScriptCompiler.StringSlots(organized);
        Assert.Equal("GY text", slots[organized.Effects.FindIndex(e => e.Label == "GY")].Text);
        Assert.Equal("On Summon text", slots[organized.Effects.FindIndex(e => e.Label == "On Summon")].Text);
        Assert.Equal(2, slots.Count(s => s.Overridden));
    }

    [Fact]
    public void ASecondOrganize_ChangesNothing_AndAScriptCardIsLeftAlone()
    {
        var (organized, _) = Organized(Jumbled());
        var again = CardOrganizer.Organize(organized);
        Assert.False(again.Changed, again.Summary);
        var script = Jumbled();
        script.UseCustomLua = true;
        script.CustomLua = "-- hand-written";
        var json = Json(script);
        var r = CardOrganizer.Organize(script);
        Assert.NotNull(r.Skipped);
        Assert.Equal(json, Json(script));
    }

    [Fact]
    public void LabelsAreTidied()
    {
        var p = new CardProject
        {
            Card = new CardData { Id = 100310010, Name = "Labels", Level = 4 },
            Effects =
            [
                new EffectEvent { Label = "Draw (copy) (copy)", When = B("when.ignition"), Do = [Draw()] },
                new EffectEvent { Label = "Draw", When = B("when.summoned", ("how", "flip")), Do = [Draw()] },
                new EffectEvent { Label = "Effect", When = B("when.ignition", ("zone", "graveyard")), Do = [B("do.destroy")] },
            ],
        };
        var (organized, _) = Organized(p);
        Assert.Equal(["Draw", "Draw 2", BlockRegistry.Find("do.destroy")!.Name], organized.Effects.Select(e => e.Label));
    }

    [Fact]
    public void TheAssistant_CanOrganize()
    {
        var pack = new CardPack { Cards = [Jumbled()] };
        var workspace = new PackWorkspace(pack);
        var outcome = new AssistantTools(workspace).Run("organize_card", JsonDocument.Parse("{}").RootElement);
        Assert.False(outcome.IsError, outcome.Content);
        Assert.Contains("Organized", outcome.Activity);
        Assert.Equal("Procedure", pack.Cards[0].Effects[0].Label);
        Assert.Equal(1, workspace.Changes);
        Assert.Contains(AssistantTools.All, t => t.Name == "organize_card");
    }

    // ---------------------------------------------------------------- the text, whatever order the blocks are in

    [Fact]
    public void Text_QuickEffectAndTimingWording()
    {
        var card = new CardData { Name = "Wording" };
        Assert.Equal("Once per turn (Quick Effect): You can draw 1 card.", EffectTextGenerator.Describe(new EffectEvent { When = B("when.quick"), Limit = OncePerTurn.Soft, Do = [Draw()] }, card));
        Assert.Equal("Once per turn, during the Main Phase, if this card is in your GY (Quick Effect): You can draw 1 card.",
            EffectTextGenerator.Describe(new EffectEvent { When = B("when.quick", ("zone", "graveyard")), If = [B("if.phase", ("phase", "main"))], Limit = OncePerTurn.Soft, Do = [Draw()] }, card));
        foreach (var ifs in new[] { new[] { B("if.turn", ("whose", "opponent")), YouControlADragon() }, [YouControlADragon(), B("if.turn", ("whose", "opponent"))] })
            Assert.Equal("During your opponent's turn, if you control a Dragon monster: You can draw 1 card.",
                EffectTextGenerator.Describe(new EffectEvent { When = B("when.ignition"), If = [.. ifs], Do = [Draw()] }, card));
        Assert.Equal("If this card is Normal Summoned during your turn while you control a Dragon monster: You can draw 1 card.",
            EffectTextGenerator.Describe(new EffectEvent { When = B("when.summoned", ("how", "normal")), If = [YouControlADragon(), B("if.turn", ("whose", "you"))], Do = [Draw()] }, card));
        Assert.Equal("During your opponent's Main Phase (Quick Effect): You can draw 1 card.",
            EffectTextGenerator.Describe(new EffectEvent { When = B("when.quick"), If = [B("if.turn", ("whose", "opponent")), B("if.phase", ("phase", "main"))], Do = [Draw()] }, card));
        // "Normal or Special Summoned", whichever WHEN came first.
        Assert.Equal("If this card is Normal or Special Summoned: You can draw 1 card.",
            EffectTextGenerator.Describe(new EffectEvent { When = B("when.summoned", ("how", "special")), AlsoWhen = [B("when.summoned", ("how", "normal"))], Do = [Draw()] }, card));
    }

    [Fact]
    public void Text_SummoningConditionsComeFirst()
    {
        var p = Jumbled();
        p.Card.SummonRule = SummonRule.SpecialSummonOnly;
        p.Card.SpecialSummonOncePerTurn = true;
        var text = EffectTextGenerator.Generate(p);
        Assert.StartsWith("Cannot be Normal Summoned/Set. Must first be Special Summoned (from your hand) by paying 1000 LP. You can only Special Summon \"Jumbled Dragon\" once per turn. "
                          + "You can only control 1 \"Jumbled Dragon\".", text);
    }

    // ---------------------------------------------------------------- in a duel

    [EngineFact]
    public void TheOrganizedCard_PlaysTheSame()
    {
        // Normal Summon it (its search triggers), then use its ignition draw; both ways the hand and Deck end up the same.
        foreach (var organize in new[] { false, true })
        {
            var p = new CardProject
            {
                Card = new CardData { Id = organize ? 100310021 : 100310020, Name = "Duelist", Race = CardRace.Dragon, Level = 4, Atk = 1000, Def = 1000 },
                Effects =
                [
                    new EffectEvent { Label = "Draw", When = B("when.ignition"), If = [YouControlADragon(), YouControlADragon()], Do = [Draw()], Limit = OncePerTurn.Hard },
                    new EffectEvent { Label = "Special", When = B("when.summoned", ("how", "special")), Do = [Search()], Limit = OncePerTurn.Hard, SharedLimitGroup = 1 },
                    new EffectEvent { Label = "Normal", When = B("when.summoned", ("how", "normal")), Do = [Search()], Limit = OncePerTurn.Hard, SharedLimitGroup = 1 },
                ],
            };
            if (organize)
            {
                var result = CardOrganizer.Organize(p);
                Assert.True(result.Changed);
                Assert.Equal(2, p.Effects.Count);
            }
            new EngineScenario(p).Card(p.Card.Id, 0, "hand").Card(Dragon, 0, "deck", 3).Card(Warrior, 1, "deck", 5)
                .Summon(p.Card.Id).Activate(p.Card.Id, 3)
                // Searched 1 Dragon and drew 1 card, once each.
                .Check("Duel.GetFieldGroupCount(0,LOCATION_DECK,0)==1")
                .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==2").AssertPasses();
        }
    }
}
