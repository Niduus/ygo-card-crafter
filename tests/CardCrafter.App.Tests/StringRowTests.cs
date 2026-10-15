using CardCrafter.App.ViewModels;
using CardCrafter.Core.Model;
using CardCrafter.Core.Templates;

namespace CardCrafter.App.Tests;

/// <summary>The Card tab's str1..str16 section, like DataEditorX's strings.</summary>
public class StringRowTests
{
    private static (CardViewModel Vm, CardProject Project) Card()
    {
        var project = new CardProject { Card = new CardData { Id = 100000321, Name = "Strings", Level = 4 } };
        project.Effects.Add(EffectTemplates.All.First(t => t.Name == "Search on summon").Create());
        var pack = new CardPack { Cards = [project] };
        return (new CardViewModel(project, () => pack, () => { }), project);
    }

    [Fact]
    public void ShowsEachEffectsText_AndTypingOverridesIt_ResetGoesBack()
    {
        var (vm, project) = Card();
        var row = Assert.Single(vm.Strings);
        Assert.Equal("str1", row.Label);
        Assert.Equal("Effect 1", row.Use);
        Assert.StartsWith("If this card is Normal or Special Summoned: You can add 1 monster", row.Text);
        Assert.False(row.IsOverridden);

        row.Text = "Search now?";
        Assert.Equal(["Search now?"], project.StringOverrides);
        Assert.Same(row, vm.Strings[0]); // the row survives the refresh, so typing keeps its place
        Assert.True(row.IsOverridden);

        row.ResetCommand.Execute(null);
        Assert.Empty(project.StringOverrides);
        Assert.Equal(row.Generated, row.Text);
    }

    [Fact]
    public void HandWrittenScripts_EditTheirOwnStrings()
    {
        var (vm, project) = Card();
        project.UseCustomLua = true;
        project.CustomLua = "local s,id=c1,1\nfunction s.initial_effect(c)\n\tlocal e1=Effect.CreateEffect(c)\n\te1:SetDescription(aux.Stringid(id,1))\nend";
        vm.Changed();
        Assert.Equal(2, vm.Strings.Count);
        vm.Strings[1].Text = "Second";
        Assert.Equal(["", "Second"], project.ScriptStrings);
    }
}
