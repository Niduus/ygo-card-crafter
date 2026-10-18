using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CardCrafter.App.ViewModels;

namespace CardCrafter.App.Views;

/// <summary>The Combos tab. The viewer's card is drawn with the app's card renderer, which is updated here.</summary>
public partial class CombosView : UserControl
{
    private CombosViewModel? _vm;
    private Bitmap? _art;

    public CombosView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnChanged;
            _vm = DataContext as CombosViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnChanged;
            UpdateFace();
        };
        // The tab is attached again each time it is opened: pick up cards renamed or rewritten since.
        AttachedToVisualTree += (_, _) => _vm?.CatalogChanged();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CombosViewModel.PlayCard) or nameof(CombosViewModel.PlayArt)) UpdateFace();
    }

    private void UpdateFace()
    {
        if (_vm?.PlayCard is not { } card) return;
        var old = _art;
        _art = null;
        if (_vm.PlayArt is { } path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                _art = new Bitmap(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // No picture: the face shows its empty art box.
            }
        }
        PlayFace.Update(card.Data, _vm.PlayTypeLine, card.Text, _art);
        old?.Dispose();
    }
}
