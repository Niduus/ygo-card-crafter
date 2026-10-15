using Avalonia.Headless.XUnit;
using CardCrafter.App.Services;
using CardCrafter.App.ViewModels;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using SkiaSharp;

namespace CardCrafter.App.Tests;

public class ImageExportTests
{
    [AvaloniaTheory]
    [InlineData(177, ".png")]
    [InlineData(813, ".jpg")]
    [InlineData(1394, ".png")]
    public void SavesAtTheRequestedSize(int width, string ext)
    {
        using var tmp = new TempDirectory();
        var path = Path.Combine(tmp.Path, "card" + ext);
        var card = new CardData { Name = "Size Test", Level = 4, Atk = 1000, Def = 1000 };
        CardImageExporter.Save(card, "[Warrior/Effect]", "Text.", Path.Combine(AppContext.BaseDirectory, "sample-art.png"), width, path);
        using var bmp = SKBitmap.Decode(path);
        Assert.Equal(width, bmp.Width);
        // The textures' shape: 813 × 1185.
        Assert.Equal((int)Math.Round(width * 1185 / 813.0), bmp.Height);
        var head = File.ReadAllBytes(path).Take(2).ToArray();
        Assert.Equal(ext == ".jpg" ? new byte[] { 0xFF, 0xD8 } : new byte[] { 0x89, 0x50 }, head);
    }

    [AvaloniaFact]
    public async Task SavesEveryCardInThePack()
    {
        using var tmp = new TempDirectory();
        var vm = new MainViewModel(new FakeDialogs { NextFile = tmp.Path }, new AppSettings());
        vm.AddCardCommand.Execute("Spell");
        vm.SelectedImageSize = ImageSize.Presets[0];
        await vm.SaveAllCardImagesCommand.ExecuteAsync(null);
        var files = Directory.GetFiles(tmp.Path, "*.png");
        Assert.Equal(2, files.Length);
        using var bmp = SKBitmap.Decode(files[0]);
        Assert.Equal(177, bmp.Width);
    }
}
