using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;

namespace CardCrafter.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Core.Omega.PackExporter.ConvertToJpeg = ImageTools.ConvertToJpeg;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var vm = new MainViewModel(new DialogService(window));
            window.DataContext = vm;
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await vm.StartAsync(desktop.Args ?? []);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
