using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CardCrafter.App.Services;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.App.Views;

/// <summary>
/// A live preview drawn like a real card face: frame, name, Attribute, Level/Rank stars or Link arrows,
/// art, Pendulum scales, type line, card text and ATK/DEF. With frame images (OmegaCustom textures or the
/// user's own templates) the image is the frame and everything else is drawn into its boxes; without, the
/// whole card is drawn with shapes, so no game artwork is bundled.
/// </summary>
public sealed class CardFace : Control
{
    /// <summary>Design size; everything is laid out in these units and scaled to the control or export size.</summary>
    public const double W = 421, H = 614;

    private CardData? _card;
    private string _typeLine = "";
    private string _text = "";
    private Bitmap? _art;

    public CardFace()
    {
        AttachedToVisualTree += (_, _) => { FrameImages.Changed -= InvalidateVisual; FrameImages.Changed += InvalidateVisual; };
        DetachedFromVisualTree += (_, _) => FrameImages.Changed -= InvalidateVisual;
    }

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

    // ------------------------------------------------------------------ layouts

    /// <summary>Where each part of the card goes, in design units.</summary>
    private sealed record Layout(
        Rect Name, Point AttrCenter, double AttrSize,
        double StarY, double StarSize, double StarStep, double StarRight, double StarLeft,
        Rect Art, Rect? TypeLine, Rect Text, Rect? Stats, Point Passcode,
        Rect SpellLine, Rect Pendulum, double ScaleWidth, bool StatsInTextBox, bool Overlay = false, bool ScalesOutside = false);

    private static readonly Layout BuiltIn = new(
        Name: new Rect(26, 24, W - 52, 50), AttrCenter: new Point(W - 52, 49), AttrSize: 42,
        StarY: 93, StarSize: 22, StarStep: 26.5, StarRight: W - 46, StarLeft: 46,
        Art: new Rect(52, 114, 317, 317), TypeLine: null, Text: new Rect(26, 452, W - 52, 136), Stats: null,
        Passcode: new Point(20, H - 23), SpellLine: new Rect(34, 80, W - 68, 26),
        Pendulum: new Rect(28, 386, W - 56, 60), ScaleWidth: 40, StatsInTextBox: true);

    private static readonly Layout BuiltInPendulum = BuiltIn with { Art = new Rect(28, 114, W - 56, 268) };

    /// <summary>
    /// A layout for a frame image, built from its measured art window and text boxes. Omega-style frames have the
    /// art window right under a name band; classic frames have a name bar, a stars/Spell line, then square art.
    /// </summary>
    private static Layout FromGeometry(FrameGeometry g, CardData c)
    {
        static Rect F((double X, double Y, double W, double H) r) => new(r.X * W, r.Y * H, r.W * W, r.H * H);
        var art = F(g.Art!.Value);
        var omegaStyle = art.Top < 0.13 * H;
        if (art.Top < 0.05 * H)
        {
            // Full-art frame: the name and card line sit on a dark strip over the top of the art.
            var fullText = g.Text is { } ft ? F(ft) : new Rect(art.X, art.Bottom + 0.03 * H, art.Width, H * 0.93 - art.Bottom - 0.03 * H);
            var strip = new Rect(art.X + 4, art.Top + 4, art.Width - 8, 0.075 * H);
            return new Layout(
                Name: strip.Deflate(new Thickness(0, 0, 40, 0)), AttrCenter: new Point(strip.Right - 22, strip.Center.Y), AttrSize: 34,
                StarY: strip.Bottom + 16, StarSize: 22.8, StarStep: 24.9, StarRight: art.Right - 10, StarLeft: art.X + 10,
                Art: art, TypeLine: null, Text: fullText, Stats: null, Passcode: new Point(0.06 * W, 0.953 * H),
                SpellLine: new Rect(art.X + 8, strip.Bottom + 2, art.Width - 16, 26), Pendulum: BuiltIn.Pendulum,
                ScaleWidth: 40, StatsInTextBox: true, Overlay: true);
        }
        var text = g.Text is { } t ? F(t) : BuiltIn.Text;
        var pend = g.Pendulum is { } p ? F(p) : BuiltIn.Pendulum;
        const double attrSize = 36;
        if (omegaStyle)
        {
            var gap = text.Top - art.Bottom;
            Rect? typeLine = gap > 0.028 * H ? new Rect(text.X, art.Bottom + 2, text.Width, gap - 4) : null;
            Rect? stats = text.Bottom < 0.945 * H ? new Rect(text.X, text.Bottom + 4, text.Width, Math.Max(14, 0.975 * H - text.Bottom - 6)) : null;
            var nameTop = 0.02 * H;
            return new Layout(
                Name: new Rect(art.X, nameTop, art.Width * 0.82, Math.Max(20, art.Top - nameTop - 2)),
                AttrCenter: new Point(0.908 * W, (nameTop + art.Top) / 2), AttrSize: attrSize,
                StarY: art.Top + 0.024 * H, StarSize: 22.8, StarStep: 24.9, StarRight: art.X + art.Width * 0.79, StarLeft: art.X + 0.036 * W,
                Art: art, TypeLine: typeLine, Text: text, Stats: stats, Passcode: new Point(0.06 * W, 0.953 * H),
                SpellLine: new Rect(0.055 * W, art.Top + 4, 0.89 * W, 26), Pendulum: pend, ScaleWidth: 40, StatsInTextBox: stats is null);
        }
        var bandTop = 0.112 * H;
        return new Layout(
            Name: new Rect(0.055 * W, 0.022 * H, 0.79 * W, 0.086 * H),
            AttrCenter: new Point(0.898 * W, 0.065 * H), AttrSize: attrSize,
            StarY: (bandTop + art.Top) / 2, StarSize: 22.8, StarStep: 24.9, StarRight: 0.94 * W, StarLeft: 0.06 * W,
            Art: art, TypeLine: null, Text: text, Stats: null, Passcode: new Point(0.06 * W, 0.953 * H),
            SpellLine: new Rect(0.055 * W, bandTop, 0.89 * W, Math.Max(20, art.Top - bandTop - 2)), Pendulum: pend,
            // Frames whose scale box sits between two gem columns get the numbers in those columns.
            ScaleWidth: pend.X - art.X > 20 ? pend.X - art.X : Math.Min(42, pend.Width * 0.11), StatsInTextBox: true,
            ScalesOutside: pend.X - art.X > 20);
    }

    /// <summary>The frame image name for this card, e.g. "synchro" or "pendulum-effect".</summary>
    public static string FrameKey(CardData c)
    {
        if (c.Kind == CardKind.Spell) return "spell";
        if (c.Kind == CardKind.Trap) return "trap";
        var frame = c.Frame.ToString().ToLowerInvariant();
        return c.IsPendulum && c.Frame != MonsterFrame.Link ? "pendulum-" + frame : frame;
    }

    private static (Bitmap? Image, Layout Layout, FrameGeometry? Geometry) Template(CardData c)
    {
        var key = FrameKey(c);
        var image = FrameImages.Get(key);
        if (image is null && c.IsPendulum)
        {
            // No Pendulum frame of this kind: fall back to the plain frame of the same kind.
            key = c.Frame.ToString().ToLowerInvariant();
            image = FrameImages.Get(key);
        }
        if (image is null) return (null, c.IsPendulum ? BuiltInPendulum : BuiltIn, null);
        var g = FrameImages.Geometry(key);
        // A template without a see-through art window is laid out like the built-in card.
        if (g?.Art is null) return (image, c.IsPendulum && key.StartsWith("pendulum") ? BuiltInPendulum : BuiltIn, g);
        return (image, FromGeometry(g, c), g);
    }

    private FrameGeometry? _geometry;

    /// <summary>White or near-black text, whichever stands out against the frame behind <paramref name="r"/>.</summary>
    private IBrush Ink(Rect r, IBrush fallback)
    {
        if (_geometry?.Brightness(r.X / W, r.Y / H, r.Width / W, r.Height / H) is not { } b) return fallback;
        return b < 125 ? Brushes.White : Brush("#141414");
    }

    // ------------------------------------------------------------------ drawing

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

    private static readonly FontFamily Serif = new("Palatino Linotype, Book Antiqua, Georgia, serif");
    private static readonly Typeface NameFont = new(Serif, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface BodyFont = new(Serif);
    private static readonly Typeface BoldFont = new(Serif, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface ItalicFont = new(Serif, FontStyle.Italic);
    private static readonly Typeface SansBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);

    private static FormattedText Text(string s, Typeface face, double size, IBrush brush, double maxWidth = double.PositiveInfinity)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush);
        if (!double.IsInfinity(maxWidth)) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    public override void Render(DrawingContext ctx) => Draw(ctx, Bounds.Size);

    /// <summary>Draws the whole card scaled into <paramref name="size"/>. Also used to export images.</summary>
    public void Draw(DrawingContext ctx, Size size)
    {
        if (_card is null) return;
        var c = _card;
        var scale = Math.Min(size.Width / W, size.Height / H);
        using var _ = ctx.PushTransform(Matrix.CreateScale(scale, scale));

        var (frame, lightText) = FrameColor(c);
        IBrush ink = lightText ? Brushes.White : Brush("#141414");
        var (template, layout, geometry) = Template(c);
        _geometry = geometry;

        if (template is not null)
        {
            // The art shows through the frame's window, so it goes underneath.
            ctx.DrawRectangle(Brush("#151515"), null, new RoundedRect(new Rect(0, 0, W, H), 14));
            DrawArt(ctx, layout.Art, plainBox: false);
            ctx.DrawImage(template, new Rect(0, 0, W, H));
        }
        else
        {
            DrawBuiltInFrame(ctx, c, frame);
            DrawArt(ctx, layout.Art, plainBox: true);
        }

        var drawn = template is not null;
        if (layout.Overlay)
        {
            // Dark strips so the name, stars and card line read over full art.
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(layout.Art.X, layout.Art.Y, layout.Art.Width, layout.Name.Height + 40));
        }
        DrawName(ctx, c, layout.Overlay ? Brushes.White : drawn ? Ink(layout.Name, ink) : ink, layout, drawn);
        DrawAttribute(ctx, c, layout);
        if (c.IsMonster) DrawStars(ctx, c, layout);
        else DrawSpellTrapLine(ctx, c, layout, layout.Overlay ? Brushes.White : drawn ? Ink(layout.SpellLine, Brush("#101010")) : Brush("#101010"));
        if (c.IsLink) DrawLinkArrows(ctx, c, layout.Art);
        if (c.IsPendulum) DrawPendulumBox(ctx, c, layout, drawn);
        DrawTextBox(ctx, c, layout, drawn);

        var passInk = drawn ? Ink(new Rect(layout.Passcode, new Size(80, 12)), ink) : ink;
        ctx.DrawText(Text(c.Id.ToString(CultureInfo.InvariantCulture), SansBold, 10.5, passInk), layout.Passcode);
    }

    private static void DrawBuiltInFrame(DrawingContext ctx, CardData c, Color frame)
    {
        ctx.DrawRectangle(Brush("#151515"), null, new RoundedRect(new Rect(0, 0, W, H), 14));
        IBrush frameBrush = new LinearGradientBrush
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
    }

    private static void DrawName(DrawingContext ctx, CardData c, IBrush ink, Layout l, bool template)
    {
        var box = l.Name;
        var (frame, _) = FrameColor(c);
        if (!template)
            ctx.DrawRectangle(new SolidColorBrush(Shade(frame, c.Frame == MonsterFrame.Xyz && c.IsMonster ? 0.12 : 0.28)),
                new Pen(Brush("#3A2E1E"), 2), new RoundedRect(box, 3));
        var size = Math.Min(30, box.Height * 0.62);
        var name = Text(string.IsNullOrWhiteSpace(c.Name) ? "Card Name" : c.Name, NameFont, size, ink);
        var maxWidth = box.Width - 20 - (template ? 0 : l.AttrSize);
        var sx = name.Width > maxWidth ? maxWidth / name.Width : 1;
        // Long names are squeezed sideways, the way printed cards do it.
        using (ctx.PushTransform(Matrix.CreateScale(sx, 1) * Matrix.CreateTranslation(box.X + 10, box.Y + (box.Height - name.Height) / 2)))
            ctx.DrawText(name, new Point(0, 0));
    }

    private static void DrawAttribute(DrawingContext ctx, CardData c, Layout l)
    {
        var center = l.AttrCenter;
        var r = l.AttrSize / 2;
        var key = c.Kind switch
        {
            CardKind.Spell => "attribute-spell",
            CardKind.Trap => "attribute-trap",
            _ => "attribute-" + c.Attribute.ToString().ToLowerInvariant(),
        };
        if (FrameImages.Get(key) is { } icon)
        {
            ctx.DrawImage(icon, new Rect(center.X - r, center.Y - r, r * 2, r * 2));
            return;
        }
        string label, color;
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
        }, new Pen(Brushes.White, 2), center, r, r);
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

    private static void DrawStars(DrawingContext ctx, CardData c, Layout l)
    {
        if (c.IsLink) return;
        var count = Math.Clamp(c.Level, 0, 13);
        var rank = c.IsXyz;
        var image = FrameImages.Get(rank ? "rank" : "level");
        var half = l.StarSize / 2;
        for (int i = 0; i < count; i++)
        {
            // Levels line up from the right, Ranks from the left, as on real cards.
            var x = rank ? l.StarLeft + half + i * l.StarStep : l.StarRight - half - i * l.StarStep;
            var center = new Point(x, l.StarY);
            if (image is not null)
            {
                ctx.DrawImage(image, new Rect(center.X - half, center.Y - half, l.StarSize, l.StarSize));
                continue;
            }
            ctx.DrawEllipse(new SolidColorBrush(Hex(rank ? "#1C1C1C" : "#D8432B")), new Pen(Brush(rank ? "#C9A23A" : "#7A1F12"), 1.5), center, half, half);
            ctx.DrawGeometry(Brush(rank ? "#E9C24F" : "#FFD34D"), null, StarGeometry(center, half * 0.72));
        }
    }

    private static void DrawSpellTrapLine(DrawingContext ctx, CardData c, Layout l, IBrush ink)
    {
        var (label, kind) = CardFaceText.SpellTrapLine(c);
        var text = Text(kind.Length > 0 ? $"[{label}  ·  {kind}]" : $"[{label}]", BoldFont, 19, ink);
        ctx.DrawText(text, new Point(l.SpellLine.Right - text.Width, l.SpellLine.Y + (l.SpellLine.Height - text.Height) / 2));
    }

    private void DrawArt(DrawingContext ctx, Rect box, bool plainBox)
    {
        if (plainBox) ctx.DrawRectangle(Brush("#2A2A2A"), new Pen(Brush("#1A1A1A"), 3), box.Inflate(4));
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

    private static void DrawPendulumBox(DrawingContext ctx, CardData c, Layout l, bool template)
    {
        var box = l.Pendulum;
        var sw = l.ScaleWidth;
        if (!template) ctx.DrawRectangle(Brush("#E9F3EC"), new Pen(Brush("#2C5E4E"), 1.5), box);
        var left = l.ScalesOutside ? box.X - sw : box.X;
        var right = l.ScalesOutside ? box.Right : box.Right - sw;
        foreach (var (x, value, arrow) in new[] { (left, c.LeftScale, "◀"), (right, c.RightScale, "▶") })
        {
            if (!template)
            {
                ctx.DrawRectangle(Brush("#CFE5D8"), null, new Rect(x, box.Y, sw, box.Height));
                var a = Text(arrow, SansBold, 11, Brush("#2E6FD1"));
                ctx.DrawText(a, new Point(x + sw / 2 - a.Width / 2, box.Y + 4));
            }
            var n = Text(value.ToString(CultureInfo.InvariantCulture), SansBold, 22, Brush("#111111"));
            ctx.DrawText(n, new Point(x + sw / 2 - n.Width / 2, box.Y + box.Height * 0.42));
        }
        var textBox = l.ScalesOutside ? box.Deflate(6) : new Rect(box.X + sw + 6, box.Y + 4, box.Width - 2 * sw - 12, box.Height - 8);
        DrawFitted(ctx, c.PendulumText, BodyFont, textBox, 11, Brush("#111111"));
    }

    private void DrawTextBox(DrawingContext ctx, CardData c, Layout l, bool template)
    {
        var box = l.Text;
        var normal = c.IsMonster && c.Frame == MonsterFrame.Normal;
        if (!template) ctx.DrawRectangle(Brush(normal ? "#F4E3B5" : "#F1E6CB"), new Pen(Brush("#8C7A50"), 1.5), box);
        var black = Brush("#141414");
        double top = box.Y + 5;
        var bottom = box.Bottom - 4;
        if (c.IsMonster)
        {
            var typeInk = template && l.TypeLine is { } area ? Ink(area, black) : black;
            var typeLine = Text(_typeLine, BoldFont, 13.5, typeInk);
            if (l.TypeLine is { } tl)
            {
                // Link arrows sit on the corners just above; keep clear of the bottom-left one.
                var indent = c.IsLink ? 26 : 6;
                ctx.DrawText(typeLine, new Point(tl.X + indent, tl.Y + (tl.Height - typeLine.Height) / 2));
            }
            else
            {
                ctx.DrawText(typeLine, new Point(box.X + 8, top));
                top += typeLine.Height + 1;
            }
            var stats = c.IsLink
                ? $"ATK/{CardFaceText.Stat(c.Atk)}   LINK–{c.LinkRating}"
                : $"ATK/{CardFaceText.Stat(c.Atk)}   DEF/{CardFaceText.Stat(c.Def)}";
            if (l.StatsInTextBox || l.Stats is null)
            {
                bottom = box.Bottom - 26;
                if (!template) ctx.DrawLine(new Pen(black, 1.2), new Point(box.X + 6, bottom + 2), new Point(box.Right - 6, bottom + 2));
                var st = Text(stats, BoldFont, 15, black);
                ctx.DrawText(st, new Point(box.Right - 10 - st.Width, bottom + 3));
            }
            else
            {
                var sr = l.Stats.Value;
                var st = Text(stats, BoldFont, 15, template ? Ink(sr, black) : black);
                ctx.DrawText(st, new Point(sr.Right - 8 - st.Width, sr.Y + (sr.Height - st.Height) / 2));
            }
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
