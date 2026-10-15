using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;
using CardCrafter.Core.Database;
using CardCrafter.Core.Omega;

namespace CardCrafter.App.Tests;

public class CardBrowserTests
{
    private const string PotOfGreed = """
        --Pot of Greed
        function c55144522.initial_effect(c)
        	--Activate
        	local e1=Effect.CreateEffect(c)
        	e1:SetCategory(CATEGORY_DRAW)
        	e1:SetType(EFFECT_TYPE_ACTIVATE)
        	e1:SetCode(EVENT_FREE_CHAIN)
        	e1:SetTarget(c55144522.target)
        	e1:SetOperation(c55144522.activate)
        	c:RegisterEffect(e1)
        end
        function c55144522.target(e,tp,eg,ep,ev,re,r,rp,chk)
        	if chk==0 then return Duel.IsPlayerCanDraw(tp,2) end
        end
        function c55144522.activate(e,tp,eg,ep,ev,re,r,rp)
        	Duel.Draw(tp,2,REASON_EFFECT)
        end
        """;

    private static string FakeOmega(TempDirectory tmp)
    {
        var omega = Path.Combine(tmp.Path, "YGO Omega");
        var files = Path.Combine(omega, "YGO Omega_Data", "Files");
        Directory.CreateDirectory(Path.Combine(files, "Scripts"));
        File.WriteAllText(Path.Combine(omega, "YGO Omega.exe"), "");
        File.WriteAllText(Path.Combine(files, "Scripts", "c55144522.lua"), PotOfGreed);
        CdbFile.WriteFresh(Path.Combine(files, "Databases", "Cards.db"),
        [
            new CdbRow { Id = 55144522, Type = 0x2, Name = "Pot of Greed", Desc = "Draw 2 cards." },
            new CdbRow { Id = 14558127, Type = 0x21, Level = 3, Atk = 0, Def = 1800, Race = 0x10, Attribute = 0x1, Name = "Ash Blossom & Joyous Spring",
                Desc = "When a card or effect is activated that includes any of these effects (Quick Effect): You can discard this card; negate that effect." },
        ]);
        return omega;
    }

    [AvaloniaFact]
    public async Task SearchesByText_AndTakesCardsApart()
    {
        using var tmp = new TempDirectory();
        var main = new MainViewModel(new FakeDialogs(), new CardCrafter.Core.Omega.AppSettings { OmegaPath = FakeOmega(tmp) });
        await main.StartAsync([]);
        var browser = main.CreateCardBrowser();
        var window = new CardBrowserWindow { DataContext = browser, Width = 1180, Height = 780 };
        window.Show();
        await browser.LoadAsync();

        browser.Query = "negate";
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Ash Blossom & Joyous Spring"], browser.Results.Select(r => r.Name));

        browser.Query = "draw";
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        browser.Selected = browser.Results.Single();
        Assert.True(browser.HasScript);
        var piece = Assert.Single(browser.Pieces);
        Assert.Equal("Activate", piece.Title);

        // Pot of Greed's one effect has exact blocks.
        Assert.True(piece.AsBlocks, piece.Status);
        Assert.StartsWith("Becomes blocks", piece.Status);
        Assert.Contains("exact blocks, so \"Use as a new card\" makes it with blocks", browser.ScriptStatus);

        // One effect into the starter card, as blocks...
        var starter = main.SelectedCard!;
        var before = starter.Effects.Count;
        browser.AddPieceCommand.Execute(piece);
        Assert.False(starter.UseCustomLua);
        Assert.Equal(before + 1, starter.Effects.Count);

        // ...or into its script when it uses a hand-written one.
        starter.EjectToLuaCommand.Execute(null);
        browser.AddPieceCommand.Execute(piece);
        Assert.True(starter.UseCustomLua);
        Assert.Contains("Duel.Draw(tp,2,REASON_EFFECT)", starter.CustomLua);

        // The whole card as a new one, with its own passcode: it comes in as blocks, the script kept.
        browser.UseAsNewCardCommand.Execute(null);
        var copy = main.SelectedCard!;
        Assert.Equal("Pot of Greed", copy.Name);
        Assert.NotEqual(55144522, copy.Project.Card.Id);
        Assert.True(copy.UsesBlocks);
        var draw = Assert.Single(copy.Effects);
        Assert.False(copy.HasErrors, string.Join("\n", copy.Diagnostics.Select(d => d.Text)));
        Assert.Contains($"function c{copy.Project.Card.Id}.initial_effect(c)", copy.CustomLua);
        Assert.DoesNotContain("55144522", copy.CustomLua);

        // A script that is used as script can be turned into blocks from the Effects tab.
        copy.RemoveEffect(draw);
        copy.UseCustomLua = true;
        copy.TranslateScriptCommand.Execute(null);
        Assert.True(copy.UsesBlocks);
        Assert.Single(copy.Effects);
        Assert.False(copy.HasTranslationReport);

        Dispatcher.UIThread.RunJobs();
        var dir = Environment.GetEnvironmentVariable("CARDCRAFTER_SCREENSHOTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, "17-card-browser.png"));
    }

    [AvaloniaFact]
    public async Task ScriptsWithoutExactBlocks_StayScript_AndSayWhy()
    {
        using var tmp = new TempDirectory();
        var main = new MainViewModel(new FakeDialogs(), new CardCrafter.Core.Omega.AppSettings { OmegaPath = FakeOmega(tmp) });
        await main.StartAsync([]);
        var card = main.SelectedCard!;
        var id = card.Project.Card.Id;
        card.CustomLua = $$"""
            function c{{id}}.initial_effect(c)
            	local e1=Effect.CreateEffect(c)
            	e1:SetType(EFFECT_TYPE_IGNITION)
            	e1:SetRange(LOCATION_MZONE)
            	e1:SetOperation(c{{id}}.op)
            	c:RegisterEffect(e1)
            end
            function c{{id}}.op(e,tp,eg,ep,ev,re,r,rp)
            	Duel.SwapDeckAndGrave(tp)
            end
            """;
        card.UseCustomLua = true;
        var lua = card.CustomLua;
        card.TranslateScriptCommand.Execute(null);
        Assert.True(card.UseCustomLua);
        Assert.Equal(lua, card.CustomLua);
        Assert.True(card.HasTranslationReport);
        Assert.Contains("0 of 1 parts", card.TranslationReport);
    }
}
