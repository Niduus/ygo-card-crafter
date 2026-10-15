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

    private Bitmap? _art;

    private void OnCardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardViewModel.ArtworkPath)) LoadPreview();
        else if (e.PropertyName == nameof(CardViewModel.FaceVersion)) UpdateFace();
    }

    private void LoadPreview()
    {
        var old = _art;
        _art = null;
        var path = _card?.ResolvedArtwork;
        if (path is not null)
        {
            try
            {
                using var stream = File.OpenRead(path);
                _art = new Bitmap(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Unreadable or unsupported images just show the empty art box.
            }
        }
        UpdateFace();
        old?.Dispose();
    }

    private void UpdateFace()
    {
        if (_card is null) return;
        Face.Update(_card.Data, _card.TypeLine, _card.FaceText, _art);
    }

    private async void OnPickArtwork(object? sender, RoutedEventArgs e)
    {
        if (_card is null || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose card art",
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
