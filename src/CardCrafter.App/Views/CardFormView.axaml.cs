using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CardCrafter.App.ViewModels;

namespace CardCrafter.App.Views;

public partial class CardFormView : UserControl
{
    private CardViewModel? _card;

    public CardFormView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_card is not null) _card.PropertyChanged -= OnCardChanged;
            _card = DataContext as CardViewModel;
            if (_card is not null) _card.PropertyChanged += OnCardChanged;
            LoadPreview();
        };
    }

    private void OnCardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardViewModel.ArtworkPath)) LoadPreview();
    }

    private void LoadPreview()
    {
        var old = ArtPreview.Source as IDisposable;
        ArtPreview.Source = null;
        old?.Dispose();
        var path = _card?.ResolvedArtwork;
        if (path is null) return;
        try
        {
            using var stream = File.OpenRead(path);
            ArtPreview.Source = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable or unsupported images just show no preview.
        }
    }

    private async void OnPickArtwork(object? sender, RoutedEventArgs e)
    {
        if (_card is null || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose card artwork",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageJpg, FilePickerFileTypes.ImagePng],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) _card.ArtworkPath = path;
    }

    private void OnRemoveArtwork(object? sender, RoutedEventArgs e)
    {
        if (_card is not null) _card.ArtworkPath = null;
    }
}
