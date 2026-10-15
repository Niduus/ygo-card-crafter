using SkiaSharp;

namespace CardCrafter.App.Services;

/// <summary>
/// Where a frame image's boxes are, found by looking at its pixels: the transparent art window, the light
/// text box (and Pendulum box) below it, plus a coarse brightness map so text can be drawn in a colour that
/// stands out. All rectangles are fractions (0..1) of the image size.
/// </summary>
public sealed class FrameGeometry
{
    public required (double X, double Y, double W, double H)? Art { get; init; }
    public required (double X, double Y, double W, double H)? Text { get; init; }
    public required (double X, double Y, double W, double H)? Pendulum { get; init; }
    private readonly double[,] _lum = new double[Cols, Rows];
    private readonly double[,] _alpha = new double[Cols, Rows];
    private const int Cols = 40, Rows = 58;

    /// <summary>Average brightness (0..255) of the opaque pixels in a fractional rectangle; null when it's all transparent.</summary>
    public double? Brightness(double x, double y, double w, double h)
    {
        int x0 = Math.Clamp((int)(x * Cols), 0, Cols - 1), x1 = Math.Clamp((int)Math.Ceiling((x + w) * Cols), x0 + 1, Cols);
        int y0 = Math.Clamp((int)(y * Rows), 0, Rows - 1), y1 = Math.Clamp((int)Math.Ceiling((y + h) * Rows), y0 + 1, Rows);
        double sum = 0, weight = 0;
        for (int i = x0; i < x1; i++)
            for (int j = y0; j < y1; j++)
            {
                sum += _lum[i, j] * _alpha[i, j];
                weight += _alpha[i, j];
            }
        return weight < 0.5 ? null : sum / weight;
    }

    public static FrameGeometry? Measure(string path)
    {
        try
        {
            using var bmp = SKBitmap.Decode(path);
            return bmp is null ? null : Measure(bmp);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static FrameGeometry Measure(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        byte A(int x, int y) => bmp.GetPixel(Math.Clamp(x, 0, w - 1), Math.Clamp(y, 0, h - 1)).Alpha;
        double L(int x, int y)
        {
            var c = bmp.GetPixel(Math.Clamp(x, 0, w - 1), Math.Clamp(y, 0, h - 1));
            return 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
        }

        // Art window: the transparent area around the middle of the upper card.
        (double, double, double, double)? art = null;
        int cx = w / 2, probe = (int)(h * 0.35);
        int artBottom = (int)(h * 0.70);
        if (A(cx, probe) < 20)
        {
            int top = probe, bottom = probe, left = cx, right = cx;
            while (top > 0 && A(cx, top - 1) < 20) top--;
            while (bottom < h - 1 && A(cx, bottom + 1) < 20) bottom++;
            int mid = (top + bottom) / 2;
            while (left > 0 && A(left - 1, mid) < 20) left--;
            while (right < w - 1 && A(right + 1, mid) < 20) right++;
            art = ((double)left / w, (double)top / h, (double)(right - left + 1) / w, (double)(bottom - top + 1) / h);
            artBottom = bottom;
        }

        // Light boxes below the art, down a column. Some frames put an emblem in the middle of the text box,
        // so a column a quarter of the way in is tried too, and whichever finds more box wins.
        List<(int Top, int Bottom)> Runs(int x)
        {
            var found = new List<(int Top, int Bottom)>();
            int? start = null;
            for (int y = artBottom + 1; y < h; y++)
            {
                bool light = A(x, y) > 200 && L(x, y) > 165;
                if (light && start is null) start = y;
                if ((!light || y == h - 1) && start is not null)
                {
                    if (y - start.Value > h * 0.03) found.Add((start.Value, y - 1));
                    start = null;
                }
            }
            // A hairline inside a box can split it; join runs only a few pixels apart (box borders are thicker).
            for (int i = found.Count - 1; i > 0; i--)
                if (found[i].Top - found[i - 1].Bottom < h * 0.005) { found[i - 1] = (found[i - 1].Top, found[i].Bottom); found.RemoveAt(i); }
            return found;
        }
        var centre = Runs(cx);
        var quarter = Runs(w / 4);
        var useQuarter = quarter.Sum(r => r.Bottom - r.Top) > centre.Sum(r => r.Bottom - r.Top) * 1.2;
        var runs = useQuarter ? quarter : centre;
        var probeX = useQuarter ? w / 4 : cx;

        (double, double, double, double)? Box((int Top, int Bottom) run)
        {
            int mid = (run.Top + run.Bottom) / 2, left = probeX, right = probeX;
            while (left > 0 && A(left - 1, mid) > 200 && L(left - 1, mid) > 140) left--;
            while (right < w - 1 && A(right + 1, mid) > 200 && L(right + 1, mid) > 140) right++;
            // Probing from the side stops at the emblem; mirror the box so it spans the card evenly.
            if (useQuarter) right = Math.Max(right, w - 1 - left);
            return ((double)left / w, (double)run.Top / h, (double)(right - left + 1) / w, (double)(run.Bottom - run.Top + 1) / h);
        }

        (double, double, double, double)? text = null, pend = null;
        if (runs.Count >= 2)
        {
            pend = Box(runs[0]);
            text = Box(runs[^1]);
        }
        else if (runs.Count == 1)
        {
            text = Box(runs[0]);
        }

        var g = new FrameGeometry { Art = art, Text = text, Pendulum = pend };
        for (int i = 0; i < Cols; i++)
            for (int j = 0; j < Rows; j++)
            {
                int x = (int)((i + 0.5) * w / Cols), y = (int)((j + 0.5) * h / Rows);
                g._lum[i, j] = L(x, y);
                g._alpha[i, j] = A(x, y) / 255.0;
            }
        return g;
    }
}
