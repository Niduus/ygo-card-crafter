using Avalonia.Media.Imaging;
using SkiaSharp;

namespace CardCrafter.App.Services;

/// <summary>The surface pattern of a card frame, like the printed ones.</summary>
public enum FrameSurface { Mottled, Starfield, Circuit, Brushed, Paper }

/// <summary>
/// Makes the frame surfaces the built-in card face is drawn with: the mottled, marbled colour of printed
/// card frames, Xyz's starfield, Link's circuit pattern, Synchro's brushed silver and the text box's paper.
/// Generated from noise, so no game artwork is used. Each surface is made once and kept.
/// </summary>
public static class FrameTexture
{
    /// <summary>Twice the card's design size, so the texture stays sharp in large exported images.</summary>
    public const int Width = 842, Height = 1228;

    private static readonly Dictionary<string, Bitmap> Cache = [];

    public static Bitmap Get(Avalonia.Media.Color color, FrameSurface surface, int width = Width, int height = Height)
    {
        var key = $"{color}-{surface}-{width}x{height}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
            var bitmap = Make(new SKColor(color.R, color.G, color.B), surface, width, height);
            Cache[key] = bitmap;
            return bitmap;
        }
    }

    private static Bitmap Make(SKColor color, FrameSurface surface, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var skBitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(skBitmap))
        {
            canvas.Clear(color);
            var seed = surface switch { FrameSurface.Paper => 7f, FrameSurface.Starfield => 3f, _ => 11f };

            // Large soft clouds, then fine grain: the marbling printed card frames have.
            var (cloud, grain) = surface switch
            {
                FrameSurface.Paper => (0.10f, 0.10f),
                FrameSurface.Brushed => (0.16f, 0.08f),
                FrameSurface.Starfield => (0.30f, 0.06f),
                _ => (0.55f, 0.16f),
            };
            Noise(canvas, width, height, 0.0045f, 4, seed, cloud);
            Noise(canvas, width, height, 0.035f, 2, seed + 5, grain);

            if (surface == FrameSurface.Brushed)
            {
                // Fine diagonal streaks of silver.
                using var streak = new SKPaint { IsAntialias = true, StrokeWidth = 1.2f, Color = SKColors.White.WithAlpha(40) };
                var rnd = new Random(17);
                for (int i = 0; i < 260; i++)
                {
                    var x = rnd.Next(-height, width);
                    canvas.DrawLine(x, 0, x + height * 0.6f, height, streak);
                }
            }
            if (surface == FrameSurface.Starfield)
            {
                var rnd = new Random(23);
                using var star = new SKPaint { IsAntialias = true };
                for (int i = 0; i < 520; i++)
                {
                    star.Color = SKColors.White.WithAlpha((byte)rnd.Next(60, 230));
                    canvas.DrawCircle(rnd.Next(width), rnd.Next(height), (float)(rnd.NextDouble() * 1.6 + 0.3), star);
                }
            }
            if (surface == FrameSurface.Circuit)
            {
                // Faint hexagon lines, like Link frames.
                using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.White.WithAlpha(26) };
                const float r = 34;
                var w = MathF.Sqrt(3) * r;
                for (float y = 0, row = 0; y < height + r; y += r * 1.5f, row++)
                    for (float x = row % 2 == 0 ? 0 : w / 2; x < width + w; x += w)
                    {
                        using var path = new SKPath();
                        for (int k = 0; k < 6; k++)
                        {
                            var a = MathF.PI / 180 * (60 * k - 30);
                            var p = new SKPoint(x + r * MathF.Cos(a), y + r * MathF.Sin(a));
                            if (k == 0) path.MoveTo(p); else path.LineTo(p);
                        }
                        path.Close();
                        canvas.DrawPath(path, line);
                    }
            }
        }
        using var image = SKImage.FromBitmap(skBitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(png.ToArray());
        return new Bitmap(stream);
    }

    /// <summary>Lightens and darkens the surface with grey Perlin noise, keeping its colour.</summary>
    private static void Noise(SKCanvas canvas, int width, int height, float frequency, int octaves, float seed, float strength)
    {
        if (strength <= 0) return;
        // Turn the noise grey (its luminance), then overlay it so it shades the colour without tinting it.
        float[] grey =
        [
            0.33f, 0.33f, 0.33f, 0, 0,
            0.33f, 0.33f, 0.33f, 0, 0,
            0.33f, 0.33f, 0.33f, 0, 0,
            0, 0, 0, 0, strength,
        ];
        using var shader = SKShader.CreatePerlinNoiseFractalNoise(frequency, frequency, octaves, seed);
        using var paint = new SKPaint
        {
            Shader = shader,
            ColorFilter = SKColorFilter.CreateColorMatrix(grey),
            BlendMode = SKBlendMode.Overlay,
        };
        canvas.DrawRect(0, 0, width, height, paint);
    }
}
