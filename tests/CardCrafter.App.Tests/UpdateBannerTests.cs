using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;
using CardCrafter.Core.Omega;
using CardCrafter.Core.Updates;

namespace CardCrafter.App.Tests;

public class UpdateBannerTests
{
    private static ReleaseInfo Release => new(new Version(9, 0, 0), "v9.0.0", "Card Crafter v9.0.0", "notes",
        "https://github.com/Niduus/ygo-card-crafter/releases/tag/v9.0.0", "https://github.com/Niduus/ygo-card-crafter/releases/download/v9.0.0/YGOCardCrafter.exe", 10, null);

    [AvaloniaFact]
    public void BannerShowsAndButtonsWork()
    {
        var dialogs = new FakeDialogs();
        var settings = new AppSettings();
        var vm = new MainViewModel(dialogs, settings);
        Assert.False(vm.ShowUpdateBanner);
        vm.AvailableUpdate = Release;
        Assert.True(vm.ShowUpdateBanner);
        Assert.Contains("9.0.0 is available", vm.UpdateBannerText);

        var window = new MainWindow { Width = 1360, Height = 900, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var dir = Environment.GetEnvironmentVariable("CARDCRAFTER_SCREENSHOTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, "9-update-banner.png"));

        vm.WhatsNewCommand.Execute(null);
        Assert.Single(dialogs.OpenedUrls);

        // Outside the Windows exe (tests, dotnet run) updating opens the download page instead.
        Assert.True(vm.UpdateNowCommand.ExecuteAsync(null).Wait(5000));
        Assert.Equal(2, dialogs.OpenedUrls.Count);

        vm.UpdateLaterCommand.Execute(null);
        Assert.False(vm.ShowUpdateBanner);
        Assert.Equal("9.0.0", settings.SkippedVersion);
    }
}
