using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using CardCrafter.App.ViewModels;

namespace CardCrafter.App.Views;

public partial class AssistantView : UserControl
{
    private AssistantViewModel? _watched;

    public AssistantView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_watched is not null) _watched.Lines.CollectionChanged -= ScrollDown;
            _watched = DataContext as AssistantViewModel;
            if (_watched is not null) _watched.Lines.CollectionChanged += ScrollDown;
        };
    }

    /// <summary>Keeps the newest line in view.</summary>
    private void ScrollDown(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("ChatScroll")?.ScrollToEnd(), DispatcherPriority.Background);
}
