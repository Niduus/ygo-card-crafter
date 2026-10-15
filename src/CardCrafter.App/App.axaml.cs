using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;

namespace CardCrafter.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        MainViewModel.CrashLogHook = CrashLog.Write;
        Core.Omega.PackExporter.ConvertToJpeg = ImageTools.ConvertToJpeg;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var dialogs = new DialogService(window);
            var vm = new MainViewModel(dialogs);
            window.DataContext = vm;
            desktop.MainWindow = window;

            // Anything unexpected on the UI thread is logged and shown, instead of closing the app.
            Dispatcher.UIThread.UnhandledException += async (_, e) =>
            {
                e.Handled = true;
                CrashLog.Write("ui", e.Exception);
                vm.Status = $"Something went wrong: {e.Exception.Message} (details in {CrashLog.FilePath})";
                try { await dialogs.ShowAsync("Something went wrong", $"{e.Exception.Message}\n\nCard Crafter kept running. Details were saved to {CrashLog.FilePath}"); }
                catch (Exception) { }
            };
            window.Opened += async (_, _) =>
            {
                try { await vm.StartAsync(desktop.Args ?? []); }
                catch (Exception ex)
                {
                    CrashLog.Write("start", ex);
                    vm.Status = $"Startup problem: {ex.Message} (details in {CrashLog.FilePath})";
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
