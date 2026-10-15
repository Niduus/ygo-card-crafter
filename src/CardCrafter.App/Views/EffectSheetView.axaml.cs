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
                // "Discard this card", "Banish this card"...: the block already set up, findable by what it does.
                foreach (var shortcut in def.Shortcuts)
                {
                    var quick = new MenuItem { Header = shortcut.Label };
                    if (def.Help.Length > 0) ToolTip.SetTip(quick, def.Help);
                    quick.Click += (_, _) => effect.AddBlock(def, shortcut.Values, shortcut.Filters);
                    header.Items.Add(quick);
                }
                if (def.Shortcuts.Count > 0) continue;
                var item = new MenuItem { Header = def.Name };
                if (def.Help.Length > 0) ToolTip.SetTip(item, def.Help);
                item.Click += (_, _) => effect.AddBlock(def);
                header.Items.Add(item);
            }
            menu.Items.Add(header);
        }
        // DO menus also offer IF blocks, which make the DO blocks after them conditional.
        if (kind == BlockKind.Action && effect.AvailableGates.Any())
        {
            var gates = new MenuItem { Header = "Continue only if... (IF)" };
            ToolTip.SetTip(gates, "The DO blocks after this one happen only if it holds, like \"then if you control a Synchro Monster, destroy that card\".");
            foreach (var group in effect.AvailableGates.GroupBy(b => b.Group))
            {
                var header = new MenuItem { Header = group.Key };
                foreach (var def in group)
                {
                    var item = new MenuItem { Header = def.Name };
                    item.Click += (_, _) => effect.AddGate(def);
                    header.Items.Add(item);
                }
                gates.Items.Add(header);
            }
            menu.Items.Add(gates);
        }
        menu.ShowAt(button);
    }

    /// <summary>"+ Or when...": another trigger for the same effect.</summary>
    private void OnAddAlsoWhen(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EffectViewModel effect } button) return;
        var menu = new MenuFlyout();
        foreach (var group in effect.AlsoWhenChoices.GroupBy(b => b.Group))
        {
            var header = new MenuItem { Header = group.Key };
            foreach (var def in group)
            {
                var item = new MenuItem { Header = def.Name };
                if (def.Help.Length > 0) ToolTip.SetTip(item, def.Help);
                item.Click += (_, _) => effect.AddAlsoWhen(def);
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
