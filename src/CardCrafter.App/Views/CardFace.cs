using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.App.Views;

/// <summary>
/// A live preview drawn like a real card face: frame colour for the card kind, name bar, Attribute, Level/Rank
/// stars or Link arrows, the art box, Pendulum scales, the type line, card text and ATK/DEF.
/// Everything is drawn with shapes and text, so no game artwork is bundled.
/// </summary>
public sealed class CardFace : Control
{
    private const double W = 421, H = 614;

    private CardData? _card;
    private string _typeLine = "";
    private string _text = "";
    private Bitmap? _art;

    public void Update(CardData card, string typeLine, string text, Bitmap? art)
    {
        _card = card;
        _typeLine = typeLine;
        _text = text;
        _art = art;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? W * 0.7 : available.Width;
        return new Size(w, w * H / W);
    }

    private static Color Hex(string s) => Color.Parse(s);
    private static SolidColorBrush Brush(string s) => new(Hex(s));

    private static (Color Main, bool LightText) FrameColor(CardData c) => c.Kind switch
    {
        CardKind.Spell => (Hex("#1A9E7E"), true),
        CardKind.Trap => (Hex("#B8317B"), true),
        _ => c.Frame switch
        {
            MonsterFrame.Normal => (Hex("#E1BE6A"), false),
            MonsterFrame.Ritual => (Hex("#6E95D4"), false),
            MonsterFrame.Fusion => (Hex("#9461B4"), false),
            MonsterFrame.Synchro => (Hex("#E9E9E9"), false),
            MonsterFrame.Xyz => (Hex("#2A2A2E"), true),
            MonsterFrame.Link => (Hex("#1F5FA6"), false),
            _ => (Hex("#D9813F"), false),
        },
    };

    private static Color Shade(Color c, double f) => f >= 0
        ? Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f))
        : Color.FromRgb((byte)(c.R * (1 + f)), (byte)(c.G * (1 + f)), (byte)(c.B * (1 + f)));

    private static readonly Typeface NameFont = new(new FontFamily("Palatino Linotype, Book Antiqua, Georgia, serif"), FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface BodyFont = new(new FontFamily("Palatino Linotype, Book Antiqua, Georgia, serif"));
    private static readonly Typeface BoldFont = new(new FontFamily("Palatino Linotype, Book Antiqua, Georgia, serif"), FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface ItalicFont = new(new FontFamily("Palatino Linotype, Book Antiqua, Georgia, serif"), FontStyle.Italic);
    private static readonly Typeface SansBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    private static FormattedText Text(string s, Typeface face, double size, IBrush brush, double maxWidth = double.PositiveInfinity)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush);
        if (!double.IsInfinity(maxWidth)) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    public override void Render(DrawingContext ctx)
    {
        if (_card is null) return;
        var c = _card;
        var scale = Math.Min(Bounds.Width / W, Bounds.Height / H);
        using var _ = ctx.PushTransform(Matrix.CreateScale(scale, scale));

        var (frame, lightText) = FrameColor(c);
        IBrush ink = lightText ? Brushes.White : Brush("#141414");

        // Card body and coloured frame.
        ctx.DrawRectangle(Brush("#151515"), null, new RoundedRect(new Rect(0, 0, W, H), 14));
        var frameBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Shade(frame, 0.18), 0), new GradientStop(frame, 0.5), new GradientStop(Shade(frame, -0.18), 1) },
        };
        if (c.IsPendulum)
        {
            // Pendulum cards: monster colour on top fading into Spell green below.
            frameBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Shade(frame, 0.1), 0), new GradientStop(frame, 0.5), new GradientStop(Hex("#1A9E7E"), 0.62), new GradientStop(Hex("#147A62"), 1) },
            };
        }
        ctx.DrawRectangle(frameBrush, null, new RoundedRect(new Rect(9, 9, W - 18, H - 18), 8));

        DrawName(ctx, c, ink);
        DrawAttribute(ctx, c);
        if (c.IsMonster) DrawStars(ctx, c); else DrawSpellTrapLine(ctx, c, ink);
        var art = c.IsPendulum ? new Rect(28, 114, W - 56, 268) : new Rect(52, 114, 317, 317);
        DrawArt(ctx, art);
        if (c.IsLink) DrawLinkArrows(ctx, c, art);
        if (c.IsPendulum) DrawPendulumBox(ctx, c);
        DrawTextBox(ctx, c);

        ctx.DrawText(Text(c.Id.ToString(CultureInfo.InvariantCulture), SansBold, 11, ink), new Point(20, H - 23));
    }

    private void DrawName(DrawingContext ctx, CardData c, IBrush ink)
    {
        var box = new Rect(26, 24, W - 52, 50);
        var (frame, _) = FrameColor(c);
        ctx.DrawRectangle(new SolidColorBrush(Shade(frame, c.Frame == MonsterFrame.Xyz && c.IsMonster ? 0.12 : 0.28)), new Pen(Brush("#3A2E1E"), 2), new RoundedRect(box, 3));
        var name = Text(string.IsNullOrWhiteSpace(c.Name) ? "Card Name" : c.Name, NameFont, 30, ink);
        const double maxWidth = 300;
        var sx = name.Width > maxWidth ? maxWidth / name.Width : 1;
        // Long names are squeezed sideways, the way printed cards do it.
        using (ctx.PushTransform(Matrix.CreateScale(sx, 1) * Matrix.CreateTranslation(box.X + 10, box.Y + (box.Height - name.Height) / 2)))
            ctx.DrawText(name, new Point(0, 0));
    }

    private static void DrawAttribute(DrawingContext ctx, CardData c)
    {
        var center = new Point(W - 52, 49);
        string label;
        string color;
        if (c.Kind == CardKind.Spell) (label, color) = ("SPELL", "#16916F");
        else if (c.Kind == CardKind.Trap) (label, color) = ("TRAP", "#A82A6F");
        else
        {
            label = c.Attribute == CardAttribute.None ? "?" : EnumNames.Display(c.Attribute);
            color = c.Attribute switch
            {
                CardAttribute.Dark => "#5B2C83",
                CardAttribute.Light => "#D9A928",
                CardAttribute.Earth => "#7A5A36",
                CardAttribute.Water => "#2F7FD0",
                CardAttribute.Fire => "#D23B22",
                CardAttribute.Wind => "#3E9B47",
                CardAttribute.Divine => "#B8862B",
                _ => "#666666",
            };
        }
        ctx.DrawEllipse(new RadialGradientBrush
        {
            GradientStops = { new GradientStop(Shade(Hex(color), 0.35), 0), new GradientStop(Hex(color), 0.7), new GradientStop(Shade(Hex(color), -0.35), 1) },
        }, new Pen(Brushes.White, 2), center, 21, 21);
        var t = Text(label, SansBold, label.Length > 4 ? 8 : 9, Brushes.White);
        ctx.DrawText(t, new Point(center.X - t.Width / 2, center.Y - t.Height / 2));
    }

    private static Geometry StarGeometry(Point c, double r)
    {
        var g = new StreamGeometry();
        using var s = g.Open();
        for (int i = 0; i < 10; i++)
        {
            var rad = i % 2 == 0 ? r : r * 0.45;
            var a = -Math.PI / 2 + i * Math.PI / 5;
            var p = new Point(c.X + rad * Math.Cos(a), c.Y + rad * Math.Sin(a));
            if (i == 0) s.BeginFigure(p, true); else s.LineTo(p);
        }
        s.EndFigure(true);
        return g;
    }

    private static void DrawStars(DrawingContext ctx, CardData c)
    {
        if (c.IsLink) return;
        var count = Math.Clamp(c.Level, 0, 13);
        var rank = c.IsXyz;
        for (int i = 0; i < count; i++)
        {
            // Levels line up from the right, Ranks from the left, as on real cards.
            var x = rank ? 46 + i * 26.5 : W - 46 - i * 26.5;
            var center = new Point(x, 93);
            ctx.DrawEllipse(new SolidColorBrush(Hex(rank ? "#1C1C1C" : "#D8432B")), new Pen(Brush(rank ? "#C9A23A" : "#7A1F12"), 1.5), center, 11, 11);
            ctx.DrawGeometry(Brush(rank ? "#E9C24F" : "#FFD34D"), null, StarGeometry(center, 8));
        }
    }

    private static void DrawSpellTrapLine(DrawingContext ctx, CardData c, IBrush ink)
    {
        var (label, kind) = CardFaceText.SpellTrapLine(c);
        var text = Text(kind.Length > 0 ? $"[{label}  ·  {kind}]" : $"[{label}]", BoldFont, 19, Brush("#101010"));
        ctx.DrawText(text, new Point(W - 34 - text.Width, 80));
    }

    private void DrawArt(DrawingContext ctx, Rect box)
    {
        ctx.DrawRectangle(Brush("#2A2A2A"), new Pen(Brush("#1A1A1A"), 3), box.Inflate(4));
        if (_art is { } art && art.PixelSize.Width > 0)
        {
            // Fill the box, cropping the middle of the image like UniformToFill.
            var src = new Rect(0, 0, art.Size.Width, art.Size.Height);
            var target = box.Width / box.Height;
            if (src.Width / src.Height > target)
            {
                var w = src.Height * target;
                src = new Rect((src.Width - w) / 2, 0, w, src.Height);
            }
            else
            {
                var h = src.Width / target;
                src = new Rect(0, (src.Height - h) / 2, src.Width, h);
            }
            ctx.DrawImage(art, src, box);
        }
        else
        {
            ctx.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Hex("#3B3F55"), 0), new GradientStop(Hex("#1E2030"), 1) },
            }, null, box);
            var t = Text("Card art goes here", BodyFont, 16, Brush("#8F95B2"));
            ctx.DrawText(t, new Point(box.Center.X - t.Width / 2, box.Center.Y - t.Height / 2));
        }
    }

    private static void DrawLinkArrows(DrawingContext ctx, CardData c, Rect box)
    {
        var o = box.Inflate(4);
        void Arrow(LinkMarker m, params Point[] pts)
        {
            var on = c.LinkMarkers.HasFlag(m);
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(pts[0], true);
                foreach (var p in pts.Skip(1)) s.LineTo(p);
                s.EndFigure(true);
            }
            ctx.DrawGeometry(Brush(on ? "#F03A2E" : "#3A3A3A"), new Pen(Brush(on ? "#FFD0C8" : "#555555"), 1), g);
        }
        double cx = o.Center.X, cy = o.Center.Y, l = o.Left, r = o.Right, t = o.Top, b = o.Bottom;
        Arrow(LinkMarker.Top, new(cx - 22, t + 2), new(cx + 22, t + 2), new(cx, t - 12));
        Arrow(LinkMarker.Bottom, new(cx - 22, b - 2), new(cx + 22, b - 2), new(cx, b + 12));
        Arrow(LinkMarker.Left, new(l + 2, cy - 22), new(l + 2, cy + 22), new(l - 12, cy));
        Arrow(LinkMarker.Right, new(r - 2, cy - 22), new(r - 2, cy + 22), new(r + 12, cy));
        Arrow(LinkMarker.TopLeft, new(l - 8, t - 8), new(l + 22, t - 8), new(l - 8, t + 22));
        Arrow(LinkMarker.TopRight, new(r + 8, t - 8), new(r - 22, t - 8), new(r + 8, t + 22));
        Arrow(LinkMarker.BottomLeft, new(l - 8, b + 8), new(l + 22, b + 8), new(l - 8, b - 22));
        Arrow(LinkMarker.BottomRight, new(r + 8, b + 8), new(r - 22, b + 8), new(r + 8, b - 22));
    }

    private static void DrawPendulumBox(DrawingContext ctx, CardData c)
    {
        var box = new Rect(28, 386, W - 56, 60);
        ctx.DrawRectangle(Brush("#E9F3EC"), new Pen(Brush("#2C5E4E"), 1.5), box);
        foreach (var (x, value, arrow) in new[] { (box.X, c.LeftScale, "◀"), (box.Right - 40, c.RightScale, "▶") })
        {
            ctx.DrawRectangle(Brush("#CFE5D8"), null, new Rect(x, box.Y, 40, box.Height));
            var a = Text(arrow, SansBold, 11, Brush("#2E6FD1"));
            ctx.DrawText(a, new Point(x + 20 - a.Width / 2, box.Y + 4));
            var n = Text(value.ToString(CultureInfo.InvariantCulture), SansBold, 22, Brush("#111111"));
            ctx.DrawText(n, new Point(x + 20 - n.Width / 2, box.Y + 22));
        }
        DrawFitted(ctx, c.PendulumText, BodyFont, new Rect(box.X + 46, box.Y + 4, box.Width - 92, box.Height - 8), 11, Brush("#111111"));
    }

    private void DrawTextBox(DrawingContext ctx, CardData c)
    {
        var box = new Rect(26, 452, W - 52, 136);
        var normal = c.IsMonster && c.Frame == MonsterFrame.Normal;
        ctx.DrawRectangle(Brush(normal ? "#F4E3B5" : "#F1E6CB"), new Pen(Brush("#8C7A50"), 1.5), box);
        var black = Brush("#141414");
        double top = box.Y + 5;
        var bottom = box.Bottom - 4;
        if (c.IsMonster)
        {
            var type = Text(_typeLine, BoldFont, 13.5, black);
            ctx.DrawText(type, new Point(box.X + 8, top));
            top += type.Height + 1;
            bottom = box.Bottom - 26;
            ctx.DrawLine(new Pen(black, 1.2), new Point(box.X + 6, bottom + 2), new Point(box.Right - 6, bottom + 2));
            var stats = c.IsLink
                ? $"ATK/{CardFaceText.Stat(c.Atk)}   LINK–{c.LinkRating}"
                : $"ATK/{CardFaceText.Stat(c.Atk)}   DEF/{CardFaceText.Stat(c.Def)}";
            var st = Text(stats, BoldFont, 15, black);
            ctx.DrawText(st, new Point(box.Right - 10 - st.Width, bottom + 3));
        }
        DrawFitted(ctx, _text, normal ? ItalicFont : BodyFont, new Rect(box.X + 8, top, box.Width - 16, bottom - top), 12.5, black);
    }

    /// <summary>Draws wrapped text, shrinking the font until it fits the box, like dense real cards.</summary>
    private static void DrawFitted(DrawingContext ctx, string text, Typeface face, Rect box, double size, IBrush brush)
    {
        if (string.IsNullOrWhiteSpace(text) || box.Height <= 0) return;
        FormattedText ft;
        do
        {
            ft = Text(text, face, size, brush, box.Width);
            size -= 0.5;
        } while (ft.Height > box.Height && size > 6);
        using (ctx.PushClip(box)) ctx.DrawText(ft, box.TopLeft);
    }
}
