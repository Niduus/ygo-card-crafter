using CardCrafter.Core.Compiler;
using CardCrafter.Core.Validation;

namespace CardCrafter.Core.Tests;

public class CheckerTests
{
    [Fact]
    public void SyntaxError_IsReportedWithLine()
    {
        var d = ScriptChecker.Check("function c1.initial_effect(c)\n\tlocal e1=\nend\n");
        var error = Assert.Single(d);
        Assert.Equal(Severity.Error, error.Severity);
        Assert.Contains("line 3", error.Message);
    }

    [Fact]
    public void Lua54Syntax_IsAccepted()
    {
        // Newer scripts use Lua 5.3+ bitwise operators; the checker must not reject them.
        Assert.Null(ScriptChecker.SyntaxError("local x=TYPE_SPELL|TYPE_TRAP\nlocal y=x&0xff\nlocal z=7//2"));
    }

    [Fact]
    public void Misspellings_AreFlagged()
    {
        var lua = "function c1.initial_effect(c)\n\tDuel.Drw(tp,1,REASON_EFFECT)\n\tc:IsCodee(1)\n\tlocal x=RACE_DRAGGON\nend";
        var names = ScriptChecker.UnknownNames(lua).ToList();
        Assert.Contains("Duel.Drw", names);
        Assert.Contains(":IsCodee", names);
        Assert.Contains("RACE_DRAGGON", names);
        Assert.DoesNotContain(names, n => n.Contains("REASON_EFFECT"));
    }

    [Fact]
    public void NamesInStringsAndComments_AreIgnored()
    {
        var lua = "--Duel.Fake\nlocal s=\"Duel.Fake2 RACE_FAKE\"\nlocal t=[[Card.Fake3]]\n--[==[ Duel.Fake4 ]==]";
        Assert.Empty(ScriptChecker.UnknownNames(lua));
    }

    [Fact]
    public void MissingInitialEffect_IsAnError()
    {
        Assert.Contains(ScriptChecker.Check("local x=1"), d => d.Severity == Severity.Error);
    }
}
