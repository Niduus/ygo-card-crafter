using Avalonia.Controls;
using CardCrafter.App.ViewModels;

namespace CardCrafter.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.RequestClose += () => { _closeConfirmed = true; Close(); };
        };
    }

    private CardBrowserWindow? _browser;

    /// <summary>Opens the card browser, or brings it to the front if it is already open.</summary>
    private void OnBrowseCards(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (_browser is { IsVisible: true })
        {
            _browser.Activate();
            return;
        }
        _browser = new CardBrowserWindow { DataContext = vm.CreateCardBrowser() };
        _browser.Closed += (_, _) => _browser = null;
        _browser.Show(this);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || DataContext is not MainViewModel { IsDirty: true } vm) return;
        e.Cancel = true;
        if (await vm.ConfirmDiscardAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
