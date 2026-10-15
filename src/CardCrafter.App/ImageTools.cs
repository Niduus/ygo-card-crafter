using SkiaSharp;

namespace CardCrafter.App;

internal static class ImageTools
{
    /// <summary>YGO Omega only accepts card art as a 624 × 624 JPEG.</summary>
    public const int ArtSize = 624;

    /// <summary>
    /// Writes card art the way YGO Omega accepts it: any image Skia can read (JPEG, PNG, WebP, BMP...), centre-cropped
    /// to a square, resized to 624 × 624 and saved as a high-quality JPEG.
    /// </summary>
    public static bool ConvertToJpeg(string source, string destination)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(source);
            if (bitmap is null) return false;
            var side = Math.Min(bitmap.Width, bitmap.Height);
            var crop = SKRect.Create((bitmap.Width - side) / 2f, (bitmap.Height - side) / 2f, side, side);
            // JPEG has no transparency: transparent parts become black, as the card frame shows them.
            using var surface = SKSurface.Create(new SKImageInfo(ArtSize, ArtSize));
            surface.Canvas.Clear(SKColors.Black);
            using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
                surface.Canvas.DrawBitmap(bitmap, crop, SKRect.Create(ArtSize, ArtSize), paint);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
            var temp = destination + ".tmp";
            using (var file = File.Create(temp)) data.SaveTo(file);
            File.Move(temp, destination, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
