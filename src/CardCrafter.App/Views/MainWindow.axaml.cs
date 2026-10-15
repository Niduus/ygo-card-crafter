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
