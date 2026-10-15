using Avalonia.Controls;
using CardCrafter.App.ViewModels;

namespace CardCrafter.App.Views;

public partial class BlockView : UserControl
{
    public BlockView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            // Colour the row by column: WHEN, IF, COST, DO or ALWAYS.
            foreach (var k in new[] { "Trigger", "Condition", "Cost", "Action", "Passive" }) Row.Classes.Remove(k);
            if (DataContext is BlockViewModel vm) Row.Classes.Add(vm.Kind);
        };
    }
}
