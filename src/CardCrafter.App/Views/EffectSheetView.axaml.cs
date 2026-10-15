using Avalonia.Controls;
using Avalonia.Interactivity;
using CardCrafter.App.ViewModels;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Templates;

namespace CardCrafter.App.Views;

public partial class EffectSheetView : UserControl
{
    public EffectSheetView() => InitializeComponent();

    /// <summary>Opens the "+ Add ..." menu for a column, grouped like GDevelop's condition/action pickers.</summary>
    private void OnAddBlock(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EffectViewModel effect, Tag: string tag } button) return;
        var kind = Enum.Parse<BlockKind>(tag);
        var menu = new MenuFlyout();
        foreach (var group in effect.Available(kind).GroupBy(b => b.Group))
        {
            var header = new MenuItem { Header = group.Key };
            foreach (var def in group)
            {
                var item = new MenuItem { Header = def.Name };
                if (def.Help.Length > 0) ToolTip.SetTip(item, def.Help);
                item.Click += (_, _) => effect.AddBlock(def);
                header.Items.Add(item);
            }
            menu.Items.Add(header);
        }
        menu.ShowAt(button);
    }

    private void OnTemplates(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not CardViewModel card) return;
        var menu = new MenuFlyout();
        foreach (var group in EffectTemplates.All.GroupBy(t => t.Group))
        {
            var header = new MenuItem { Header = group.Key };
            foreach (var t in group)
            {
                var item = new MenuItem { Header = $"{t.Name}  —  like {t.LikeCard}" };
                ToolTip.SetTip(item, t.Description);
                item.Click += (_, _) =>
                {
                    if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main) main.AddTemplate(t);
                    else card.AddTemplate(t);
                };
                header.Items.Add(item);
            }
            menu.Items.Add(header);
        }
        menu.ShowAt(button);
    }
}
