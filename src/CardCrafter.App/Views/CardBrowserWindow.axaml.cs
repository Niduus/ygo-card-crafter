using Avalonia.Controls;
using CardCrafter.App.ViewModels;

namespace CardCrafter.App.Views;

public partial class CardBrowserWindow : Window
{
    public CardBrowserWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            this.FindControl<TextBox>("SearchBox")?.Focus();
            if (DataContext is CardBrowserViewModel vm) await vm.LoadAsync();
        };
    }
}
