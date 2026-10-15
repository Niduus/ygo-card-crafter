using CardCrafter.App.ViewModels;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.App.Tests;

public class CodeRowTests
{
    [Fact]
    public async Task CopyButton_CopiesTheCode_AndCopyAllListsEveryOne()
    {
        var copied = new List<string>();
        var before = CodeRowViewModel.Copier;
        CodeRowViewModel.Copier = s => { copied.Add(s); return Task.CompletedTask; };
        try
        {
            var rows = CodeRowViewModel.Rows(CardCodes.Of(new CardData { Id = 100000123, Race = CardRace.Dragon, Archetypes = [0xdd] }));
            var type = rows.Single(r => r.Label == "Type");
            await type.CopyCommand.ExecuteAsync(null);
            Assert.Equal("0x2000", copied.Single());
            Assert.Equal("Copied", type.CopyLabel);
            var all = CodeRowViewModel.AllText(rows);
            Assert.Contains("Passcode: 100000123", all);
            Assert.Contains("Archetype: 0xdd", all);
        }
        finally { CodeRowViewModel.Copier = before; }
    }
}
