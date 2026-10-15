using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;
using CardCrafter.Core.Database;
using CardCrafter.Core.Omega;

namespace CardCrafter.App.Tests;

public class DatabaseWorkflowTests
{
    /// <summary>A minimal YGO Omega install: the exe, a Scripts folder with one script, an Arts folder and a custom database.</summary>
    private static string FakeOmega(TempDirectory tmp)
    {
        var omega = Path.Combine(tmp.Path, "YGO Omega");
        var files = Path.Combine(omega, "YGO Omega_Data", "Files");
        Directory.CreateDirectory(Path.Combine(files, "Scripts"));
        Directory.CreateDirectory(Path.Combine(files, "Arts"));
        File.WriteAllText(Path.Combine(omega, "YGO Omega.exe"), "");
        File.WriteAllText(Path.Combine(files, "Scripts", "c1.lua"), "");
        CdbFile.WriteFresh(Path.Combine(files, "Databases", "Other.db"), [new CdbRow { Id = 555000001, Type = 0x11, Name = "Other Card" }]);
        return omega;
    }

    [AvaloniaFact]
    public async Task InstallOnSave_ThenOpenFromOmega_ThenSaveInPlace()
    {
        using var tmp = new TempDirectory();
        var omega = FakeOmega(tmp);
        var databases = Path.Combine(omega, "YGO Omega_Data", "Files", "Databases");
        var scripts = Path.Combine(omega, "YGO Omega_Data", "Files", "Scripts");
        var dialogs = new FakeDialogs();
        var vm = new MainViewModel(dialogs, new AppSettings { OmegaPath = omega });
        await vm.StartAsync([]);
        Assert.True(vm.CanInstall);
        var original = vm.InstallOnSave;
        try
        {
            vm.InstallOnSave = true;
            dialogs.NextFile = Path.Combine(tmp.Path, "pack.ycc");
            Assert.True(await vm.SavePackAs());
            Assert.Empty(dialogs.Shown);

            // Saving installed it: database, scripts.
            var installed = Path.Combine(databases, vm.PackName + ".db");
            Assert.True(File.Exists(installed), vm.Status);
            var id = vm.Cards[0].Project.Card.Id;
            Assert.True(File.Exists(Path.Combine(scripts, $"c{id}.lua")));

            // Omega's databases are listed; opening ours brings the event sheets back.
            await vm.DetectOmega();
            Assert.Contains(vm.OmegaDatabases, d => d.Path == installed);
            var effects = vm.Cards[0].Project.Effects.Count;
            await vm.OpenOmegaDatabaseCommand.ExecuteAsync(installed);
            Assert.Equal(installed, vm.DatabasePath);
            Assert.Null(vm.PackPath);
            Assert.Equal(effects, vm.Cards[0].Project.Effects.Count);
            Assert.False(vm.Cards[0].Project.UseCustomLua);
            Assert.False(vm.Cards[0].HasPasscodeWarning, "A card must not clash with its own database");

            // Save writes back into the database where it is.
            vm.Cards[0].Name = "Renamed In Place";
            Assert.True(await vm.SavePack());
            Assert.Equal("Renamed In Place", CdbFile.Read(installed).First(r => r.Id == id).Name);
            Assert.False(vm.IsDirty);
            Assert.Contains("backed up", vm.Status);
        }
        finally
        {
            vm.InstallOnSave = original;
        }
    }

    [AvaloniaFact]
    public async Task OpenAnyDatabase_UsesTheScriptFolderNextToIt()
    {
        using var tmp = new TempDirectory();
        var db = Path.Combine(tmp.Path, "theirs.cdb");
        CdbFile.WriteFresh(db, [new CdbRow { Id = 321000001, Type = 0x21, Name = "Scripted" }]);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "script"));
        File.WriteAllText(Path.Combine(tmp.Path, "script", "c321000001.lua"), "--theirs\n");
        var dialogs = new FakeDialogs { NextFile = db };
        var vm = new MainViewModel(dialogs, new AppSettings());
        await vm.OpenPackCommand.ExecuteAsync(null);
        Assert.Equal(db, vm.DatabasePath);
        Assert.Equal("theirs", vm.PackName);
        Assert.True(vm.Cards.Single().Project.UseCustomLua);
        Assert.Equal("--theirs\n", vm.Cards.Single().Project.CustomLua);
        Assert.Contains(db, vm.SaveTip);
    }

    [AvaloniaFact]
    public async Task SaveAsDatabase_KeepsArtFromThePacksFolder()
    {
        using var tmp = new TempDirectory();
        var dialogs = new FakeDialogs { NextFile = Path.Combine(tmp.Path, "pack", "pack.ycc") };
        Directory.CreateDirectory(Path.Combine(tmp.Path, "pack"));
        var vm = new MainViewModel(dialogs, new AppSettings());
        File.WriteAllBytes(Path.Combine(tmp.Path, "pack", "art.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
        vm.Cards[0].Project.ArtworkPath = "art.jpg";
        Assert.True(await vm.SavePackAs());

        dialogs.NextFile = Path.Combine(tmp.Path, "elsewhere", "cards.cdb");
        Assert.True(await vm.SaveAsDatabase());
        var id = vm.Cards[0].Project.Card.Id;
        Assert.True(File.Exists(Path.Combine(tmp.Path, "elsewhere", "pics", id + ".jpg")));
        // A second save from the database still finds the art.
        File.Delete(Path.Combine(tmp.Path, "elsewhere", "pics", id + ".jpg"));
        Assert.True(await vm.SavePack());
        Assert.True(File.Exists(Path.Combine(tmp.Path, "elsewhere", "pics", id + ".jpg")), vm.Status);
    }

    [AvaloniaTheory]
    [InlineData(1360)]
    [InlineData(1100)]
    public void ToolbarIsNotCutOff(int width)
    {
        var vm = new MainViewModel(new FakeDialogs(), new AppSettings());
        var window = new MainWindow { Width = width, Height = 800, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var toolbar = window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "New").FindAncestorOfType<WrapPanel>()
                      ?? throw new InvalidOperationException("The toolbar buttons should wrap instead of running off the window.");
        foreach (var b in toolbar.GetVisualDescendants().OfType<Control>().Where(c => c is Button or CheckBox))
        {
            var right = b.TranslatePoint(new Avalonia.Point(b.Bounds.Width, 0), window)!.Value.X;
            Assert.True(right <= width, $"{(b as ContentControl)?.Content} ends at {right}px in a {width}px window");
        }
        var dir = Environment.GetEnvironmentVariable("CARDCRAFTER_SCREENSHOTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"11-toolbar-{width}.png"));
    }
}
