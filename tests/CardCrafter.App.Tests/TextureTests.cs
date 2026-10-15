using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CardCrafter.App.Services;
using CardCrafter.App.Views;
using CardCrafter.Core.Model;
using SkiaSharp;

namespace CardCrafter.App.Tests;

/// <summary>Loading card textures from a folder or zip. Uses plain stand-in images made here, never game artwork.</summary>
public class TextureTests
{
    /// <summary>An 813 × 1185 stand-in frame: opaque except an art window, which starts at the top for "full art".</summary>
    private static void Frame(string path, SKColor color, bool fullArt = false)
    {
        using var bmp = new SKBitmap(813, 1185);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(color);
            using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
            canvas.DrawRect(fullArt ? new SKRect(54, 30, 759, 736) : new SKRect(99, 218, 715, 833), clear);
        }
        Save(bmp, path);
    }

    private static void Icon(string path, SKColor color, int size = 76)
    {
        using var bmp = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(color);
        Save(bmp, path);
    }

    private static void Save(SKBitmap bmp, string path)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    [AvaloniaFact]
    public void LoadsNumberedTextures_PicksTheStandardFrame_AndDrawsWithThem()
    {
        using var tmp = new TempDirectory();
        var dir = Directory.CreateDirectory(Path.Combine(tmp.Path, "pack", "nested")).FullName;
        Frame(Path.Combine(dir, "EffectFrame #2375.png"), SKColors.Orange);
        // Two Pendulum versions: the full-art one must not be chosen.
        Frame(Path.Combine(dir, "PendulumEffectFullFrame #2270.png"), SKColors.Red, fullArt: true);
        Frame(Path.Combine(dir, "PendulumEffectFullFrame.png"), SKColors.Green);
        Icon(Path.Combine(dir, "Dark.png"), SKColors.Purple);
        Icon(Path.Combine(dir, "Dark #2388.png"), SKColors.Black);
        Icon(Path.Combine(dir, "Level.png"), SKColors.Yellow, 62);
        try
        {
            Assert.Equal(4, FrameImages.Load(Path.Combine(tmp.Path, "pack")));
            Assert.True(FrameImages.Has("effect"));
            Assert.Equal(SKColors.Green, Pixel(FrameImages.Get("pendulum-effect")!, 400, 100));
            Assert.Equal(SKColors.Purple, Pixel(FrameImages.Get("attribute-dark")!, 30, 30));
            Assert.Contains("SpellCardFrame", FrameImages.Missing);

            var face = new CardFace { Width = 300 };
            face.Update(new CardData { Name = "Texture Test", Attribute = CardAttribute.Dark, Level = 4 }, "[Warrior/Effect]", "Text.", null);
            var window = new Window { Width = 320, Height = 460, Content = face };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame()!;
            using var ms = new MemoryStream();
            frame.Save(ms);
            using var shot = SKBitmap.Decode(ms.ToArray());
            // The orange frame texture is what's drawn at the card's edge area.
            var edge = shot.GetPixel(20, 230);
            Assert.True(edge.Red > 200 && edge.Green > 120 && edge.Blue < 60, $"frame pixel was {edge}");
        }
        finally
        {
            FrameImages.Load(null);
        }
    }

    private static SKColor Pixel(Avalonia.Media.Imaging.Bitmap bitmap, int x, int y)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        using var bmp = SKBitmap.Decode(ms.ToArray());
        return bmp.GetPixel(x, y);
    }

    [AvaloniaFact]
    public void ExtractsOnlyImagesAndFonts_FromAZip()
    {
        using var tmp = new TempDirectory();
        var zipPath = Path.Combine(tmp.Path, "cards.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var src = Path.Combine(tmp.Path, "frame.png");
            Frame(src, SKColors.Blue);
            zip.CreateEntryFromFile(src, "cards/EffectFrame.png");
            using (var w = new StreamWriter(zip.CreateEntry("cards/readme.exe").Open())) w.Write("not an image");
            using (var w = new StreamWriter(zip.CreateEntry("../escape.png").Open())) w.Write("x");
        }
        var folder = FrameImages.ExtractZip(zipPath, Path.Combine(tmp.Path, "out"));
        Assert.True(File.Exists(Path.Combine(folder, "cards", "EffectFrame.png")));
        Assert.False(File.Exists(Path.Combine(folder, "cards", "readme.exe")));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "out", "escape.png")));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "escape.png")));
        try
        {
            Assert.Equal(1, FrameImages.Load(folder));
        }
        finally
        {
            FrameImages.Load(null);
        }
    }

    [AvaloniaFact]
    public void FontsInTheTextureFolderAreUsed()
    {
        using var tmp = new TempDirectory();
        Frame(Path.Combine(tmp.Path, "EffectFrame.png"), SKColors.Orange);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestMatrixBook.ttf"), Path.Combine(tmp.Path, "TestMatrixBook.ttf"));
        try
        {
            FrameImages.Load(tmp.Path);
            Assert.Contains("card text: Test Matrix Book", CardFonts.Summary);
            Assert.Equal("Test Matrix Book", CardFonts.Body.FontFamily.Name);
            Assert.True(Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(CardFonts.Body, out var face));
            Assert.Equal("Test Matrix Book", face!.FamilyName);
        }
        finally
        {
            FrameImages.Load(null);
        }
        Assert.Contains("bundled", CardFonts.Summary);
    }

    [AvaloniaFact]
    public void WithoutTextures_ThePreviewIsAPlaceholder()
    {
        FrameImages.Load(null);
        var face = new CardFace { Width = 300 };
        face.Update(new CardData { Name = "Nothing Loaded" }, "[Warrior/Effect]", "Text.", null);
        var window = new Window { Width = 320, Height = 460, Content = face };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(window.CaptureRenderedFrame());
        Assert.Equal("effect", CardFace.FrameKey(new CardData()));
        Assert.Equal("pendulum-normal", CardFace.FrameKey(new CardData { Frame = MonsterFrame.Normal, Abilities = MonsterAbility.Pendulum }));
    }
}
