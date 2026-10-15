using Avalonia;
using Avalonia.Media.Imaging;
using CardCrafter.App.Views;
using CardCrafter.Core.Model;
using SkiaSharp;

namespace CardCrafter.App.Services;

/// <summary>A size to save card images at. Height always follows the real card's 59 x 86 shape.</summary>
public sealed record ImageSize(string Label, int Width)
{
    public int Height => (int)Math.Round(Width * CardFace.H / CardFace.W);
    public override string ToString() => Width > 0 ? $"{Label} — {Width} × {Height}" : Label;

    public static readonly IReadOnlyList<ImageSize> Presets =
    [
        new("Small (ygopro pics)", 177),
        new("Standard", 421),
        new("Large (texture size)", 813),
        new("Print, 300 DPI", 697),
        new("Print, 600 DPI", 1394),
        new("Extra large", 1626),
        new("Custom width…", 0),
    ];
}

/// <summary>Renders card previews to image files at any size. The face is vector-drawn, so large sizes stay sharp.</summary>
public static class CardImageExporter
{
    public static void Save(CardData card, string typeLine, string text, string? artPath, int width, string path, string copyright = "")
    {
        width = Math.Clamp(width, 64, 8000);
        var height = (int)Math.Round(width * CardFace.H / CardFace.W);
        using var art = LoadArt(artPath);
        var face = new CardFace();
        face.Update(card, typeLine, text, art, copyright);
        using var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
            face.Draw(ctx, new Size(width, height));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".webp")
        {
            // Render as PNG in memory, then re-encode with Skia for the other formats.
            using var png = new MemoryStream();
            rtb.Save(png);
            png.Position = 0;
            using var bitmap = SKBitmap.Decode(png);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(ext == ".webp" ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Jpeg, 94);
            using var file = File.Create(path);
            data.SaveTo(file);
        }
        else
        {
            rtb.Save(path);
        }
    }

    private static Bitmap? LoadArt(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A file name safe for any card name, e.g. "Dragon Commander (100000001).png".</summary>
    public static string FileName(CardData card, string extension) =>
        Core.Omega.PackExporter.SafeFileName($"{card.Name} ({card.Id})") + extension;
}
