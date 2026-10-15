using SkiaSharp;

namespace CardCrafter.App;

internal static class ImageTools
{
    /// <summary>Re-encodes any image Skia can read (PNG, WebP, BMP...) as a high-quality JPEG.</summary>
    public static bool ConvertToJpeg(string source, string destination)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(source);
            if (bitmap is null) return false;
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
            using var file = File.Create(destination);
            data.SaveTo(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
