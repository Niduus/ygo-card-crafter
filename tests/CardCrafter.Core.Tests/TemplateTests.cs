using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;
using CardCrafter.Core.Templates;
using CardCrafter.Core.Text;
using CardCrafter.Core.Validation;

namespace CardCrafter.Core.Tests;

public class TemplateTests
{
    public static IEnumerable<object[]> Names() => EffectTemplates.All.Select(t => new object[] { t.Name });

    public static CardProject CardWith(EffectTemplate t) => new()
    {
        Card = t.For switch
        {
            CardKind.Spell => new CardData
            {
                Id = 100000600, Name = t.LikeCard, Kind = CardKind.Spell,
                SpellKind = t.Name switch
                {
                    "Type boost field" => SpellKind.Field, "Standby heal" or "Monsters banished instead" => SpellKind.Continuous,
                    "Destroy a Spell/Trap" => SpellKind.QuickPlay, _ => SpellKind.Normal,
                },
            },
            CardKind.Trap => new CardData
            {
                Id = 100000601, Name = t.LikeCard, Kind = CardKind.Trap,
                TrapKind = t.Group == "Floodgates & locks" ? TrapKind.Continuous : TrapKind.Normal,
            },
            _ => t.Name switch
            {
                "Banish from GY to summon" => new CardData { Id = 100000605, Name = t.LikeCard, Level = 6, SummonRule = SummonRule.SpecialSummonOnly },
                "Link support" => new CardData { Id = 100000603, Name = t.LikeCard, Frame = MonsterFrame.Link, LinkMarkers = LinkMarker.Left | LinkMarker.Right },
                "Detach to destroy" => new CardData { Id = 100000604, Name = t.LikeCard, Frame = MonsterFrame.Xyz, Level = 4 },
                _ => new CardData { Id = 100000602, Name = t.LikeCard },
            },
        },
        Procedure = t.Name switch
        {
            "Link support" => new SummonProcedure { Kind = SummonProcedureKind.Link, MinMaterials = 2 },
            "Detach to destroy" => new SummonProcedure { Kind = SummonProcedureKind.Xyz, MinMaterials = 2 },
            _ => new SummonProcedure(),
        },
        Effects = [t.Create()],
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void TemplateCompilesAndDescribes(string name)
    {
        var t = EffectTemplates.All.Single(x => x.Name == name);
        var p = CardWith(t);
        var r = ScriptCompiler.Compile(p);
        Assert.False(r.HasErrors, string.Join("\n", r.Diagnostics));
        Assert.Empty(ScriptChecker.Check(r.Lua));
        Assert.NotEmpty(EffectTextGenerator.Generate(p));
    }

    [Fact]
    public void TemplatesCreateFreshCopies()
    {
        var t = EffectTemplates.All[0];
        Assert.NotSame(t.Create(), t.Create());
    }
}
