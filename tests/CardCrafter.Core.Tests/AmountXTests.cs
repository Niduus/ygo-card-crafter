using CardCrafter.Core.Blocks;
using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;
using static CardCrafter.Core.Tests.EngineScenario;

namespace CardCrafter.Core.Tests;

/// <summary>
/// "Pay LP so that you only have X left" and "draw until you have X cards in your hand", where X is a number set on the
/// block, a number the player declares, or a value from the Duel.
/// </summary>
public class AmountXTests
{
    private const long PayId = 100720001, DrawId = 100720002;

    private static BlockInstance B(string id, params (string Key, string Value)[] values)
    {
        var b = BlockRegistry.Find(id)!.CreateInstance();
        foreach (var (k, v) in values) b.Values[k] = v;
        return b;
    }

    private static string Lua(CardProject p)
    {
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics) + "\n" + r.Lua);
        Assert.DoesNotContain(Validation.ScriptChecker.Check(r.Lua), d => d.Severity == Severity.Error);
        Assert.Empty(Validation.ScriptChecker.UnknownNames(r.Lua));
        return r.Lua;
    }

    private static CardProject Pay(params (string Key, string Value)[] x) => new()
    {
        Card = new CardData { Id = PayId, Name = "Pay Down", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Cost = [B("cost.pay_lp", [("how", "to_one"), .. x])], Do = [B("do.draw", ("count", "1"))] }],
    };

    private static CardProject Draw(params (string Key, string Value)[] x) => new()
    {
        Card = new CardData { Id = DrawId, Name = "Refill", Kind = CardKind.Spell },
        Effects = [new EffectEvent { When = B("when.activate"), Do = [B("do.draw", [("until", "true"), .. x])] }],
    };

    [Fact]
    public void PayLp_Wording()
    {
        Assert.Contains("Pay LP so that you only have 100 left;", EffectTextGenerator.Generate(Pay(("left", "100"))));
        Assert.Contains("Declare an amount from 100 to 8000 in multiples of 100, then pay LP so that you only have that much left;",
            EffectTextGenerator.Generate(Pay(("x", "declare"))));
        Assert.Contains("Pay LP so that you only have LP equal to half your opponent's LP left;",
            EffectTextGenerator.Generate(Pay(("x", "value"), ("x_value", "opp_lp"), ("x_how", "half"))));
        Assert.Contains("Duel.AnnounceNumber(tp,table.unpack(", Lua(Pay(("x", "declare"))));
        Assert.Contains("local lpc=Duel.GetLP(tp)-(math.floor((Duel.GetLP(1-tp))/2))", Lua(Pay(("x", "value"), ("x_value", "opp_lp"), ("x_how", "half"))));
    }

    [Fact]
    public void DrawUntil_Wording()
    {
        Assert.Equal("Draw until you have 6 cards in your hand.", EffectTextGenerator.Generate(Draw(("count", "6"))));
        Assert.Equal("Declare a number from 1 to 6, then each player draws until they have that many cards in their hand.",
            EffectTextGenerator.Generate(Draw(("x", "declare"), ("who", "each"))));
        Assert.Equal("Draw until you have cards in your hand equal to the number of cards in your opponent's hand.",
            EffectTextGenerator.Generate(Draw(("x", "value"), ("x_value", "count"))));
        Assert.Contains("Duel.SetTargetParam(", Lua(Draw(("x", "declare"))));
        Assert.Contains("Duel.GetChainInfo(0,CHAININFO_TARGET_PARAM)", Lua(Draw(("x", "declare"))));
    }

    [Fact]
    public void Declare_HighestBelowLowest_IsAnError()
    {
        var r = ScriptCompiler.Compile(Draw(("x", "declare"), ("x_min", "5"), ("x_max", "2")));
        Assert.True(r.HasErrors);
    }

    [EngineTheory]
    [InlineData(3000)]
    [InlineData(500)]
    public void PayLp_DeclaredAmountIsLeft(int declared)
    {
        new EngineScenario(Pay(("x", "declare"))).Card(PayId, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Declare(declared).Activate(PayId).ExpectActivations(1)
            .Check($"Duel.GetLP(0)=={declared}").AssertPasses();
    }

    [EngineFact]
    public void PayLp_ValueIsLeft()
    {
        new EngineScenario(Pay(("x", "value"), ("x_value", "opp_lp"), ("x_how", "half"))).Card(PayId, 0, "hand").Card(Warrior, 0, "deck", 5).Card(Warrior, 1, "deck", 5)
            .Activate(PayId).ExpectActivations(1)
            .Check("Duel.GetLP(0)==4000").AssertPasses();
    }

    [EngineTheory]
    [InlineData(4)]
    [InlineData(2)]
    public void DrawUntil_DeclaredNumber(int declared)
    {
        new EngineScenario(Draw(("x", "declare"))).Card(DrawId, 0, "hand").Card(Fiend, 0, "hand").Card(Warrior, 0, "deck", 8).Card(Warrior, 1, "deck", 5)
            .Declare(declared).Activate(DrawId).ExpectActivations(1)
            .Check($"Duel.GetFieldGroupCount(0,LOCATION_HAND,0)=={declared}").AssertPasses();
    }

    [EngineFact]
    public void DrawUntil_AsManyAsTheOpponentHolds()
    {
        new EngineScenario(Draw(("x", "value"), ("x_value", "count"))).Card(DrawId, 0, "hand").Card(Fiend, 1, "hand", 5).Card(Warrior, 0, "deck", 8).Card(Warrior, 1, "deck", 5)
            .Activate(DrawId).ExpectActivations(1)
            .Check("Duel.GetFieldGroupCount(0,LOCATION_HAND,0)==5").AssertPasses();
    }
}
