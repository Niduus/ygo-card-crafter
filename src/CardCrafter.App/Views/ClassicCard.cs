using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CardCrafter.App.Services;
using CardCrafter.Core.Model;
using CardCrafter.Core.Text;

namespace CardCrafter.App.Views;

/// <summary>
/// Draws a card the way printed cards (and YGO Omega) lay them out, in a 421 × 614 design space:
/// a marbled frame, an engraved name plate, the Attribute orb with its kanji, Level orbs, a bevelled art window,
/// the cream text box with a bracketed type line, justified text and the ATK/DEF rule, the passcode bottom left
/// and the copyright bottom right. Every position comes from measuring real cards, and anything centred is
/// centred on its drawn outline, not its font box, so it sits exactly in the middle.
/// </summary>
internal static class ClassicCard
{
    public const double W = CardFace.W, H = CardFace.H;

    // ---------------------------------------------------------------- measurements (design units)

    private static readonly Rect NamePlate = new(22, 26, 377, 41);
    private static readonly Point AttributeCenter = new(364.5, 46.5);
    private const double AttributeSize = 33;
    private const double StarY = 88, StarSize = 21.5, StarStep = 28.4, StarRight = 373, StarLeft = 48;
    private static readonly Rect SpellLine = new(48, 76, 325, 24);
    private static readonly Rect Art = new(51, 110, 319, 319);
    private static readonly Rect PendulumArt = new(28, 110, 365, 276);
    private static readonly Rect PendulumBox = new(28, 388, 365, 56);
    private const double PendulumScaleWidth = 40;
    private static readonly Rect TextBox = new(29, 446, 363, 128);
    private static readonly Rect PendulumTextBox = new(29, 448, 363, 126);
    private const double FooterY = 584;

    // ---------------------------------------------------------------- fonts (bundled, OFL)

    private static readonly FontFamily SmallCaps = new("avares://YGOCardCrafter/Assets/Fonts#Spectral SC");
    private static readonly FontFamily Body = new("avares://YGOCardCrafter/Assets/Fonts#Crimson Text");
    private static readonly FontFamily Kanji = new("avares://YGOCardCrafter/Assets/Fonts#Card Crafter Kanji");
    private static readonly Typeface NameFace = new(SmallCaps, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface CapsBold = new(SmallCaps, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface BodyFace = new(Body);
    private static readonly Typeface BodyItalic = new(Body, FontStyle.Italic);
    private static readonly Typeface BodyBold = new(Body, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface KanjiFace = new(Kanji, FontStyle.Normal, FontWeight.Black);
    /// <summary>Numbers need full-height digits; the small-caps face draws "2500" with lowercase-style ones.</summary>
    private static readonly Typeface Numbers = BodyBold;

    // ---------------------------------------------------------------- colours

    private static Color Hex(string s) => Color.Parse(s);
    private static SolidColorBrush Brush(string s) => new(Hex(s));
    private static SolidColorBrush Brush(Color c) => new(c);

    private static Color Shade(Color c, double f) => f >= 0
        ? Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f))
        : Color.FromRgb((byte)(c.R * (1 + f)), (byte)(c.G * (1 + f)), (byte)(c.B * (1 + f)));

    private static readonly Color SpellGreen = Hex("#1D9C82");

    /// <summary>Frame colour, surface, and whether the name is printed white, per card frame.</summary>
    private static (Color Color, FrameSurface Surface, bool WhiteName) Frame(CardData c) => c.Kind switch
    {
        CardKind.Spell => (SpellGreen, FrameSurface.Mottled, true),
        CardKind.Trap => (Hex("#B5407E"), FrameSurface.Mottled, true),
        _ => c.Frame switch
        {
            MonsterFrame.Normal => (Hex("#D8B262"), FrameSurface.Mottled, false),
            MonsterFrame.Ritual => (Hex("#6A93D3"), FrameSurface.Mottled, false),
            MonsterFrame.Fusion => (Hex("#9867B4"), FrameSurface.Mottled, false),
            MonsterFrame.Synchro => (Hex("#E4E4E2"), FrameSurface.Brushed, false),
            MonsterFrame.Xyz => (Hex("#1F2024"), FrameSurface.Starfield, true),
            MonsterFrame.Link => (Hex("#2163AB"), FrameSurface.Circuit, false),
            _ => (Hex("#CC7A3E"), FrameSurface.Mottled, false),
        },
    };

    private static (string Kanji, string Word, Color Color) AttributeLook(CardData c) => c.Kind switch
    {
        CardKind.Spell => ("魔", "SPELL", Hex("#14987C")),
        CardKind.Trap => ("罠", "TRAP", Hex("#B2307A")),
        _ => c.Attribute switch
        {
            CardAttribute.Light => ("光", "LIGHT", Hex("#E2AF1E")),
            CardAttribute.Dark => ("闇", "DARK", Hex("#6A2C8C")),
            CardAttribute.Fire => ("炎", "FIRE", Hex("#D8261C")),
            CardAttribute.Water => ("水", "WATER", Hex("#257ED6")),
            CardAttribute.Earth => ("地", "EARTH", Hex("#7A5630")),
            CardAttribute.Wind => ("風", "WIND", Hex("#2E9A47")),
            CardAttribute.Divine => ("神", "DIVINE", Hex("#C2992C")),
            _ => ("", "?", Hex("#777777")),
        },
    };

    // ---------------------------------------------------------------- text helpers

    private static FormattedText Text(string s, Typeface face, double size, IBrush brush, double maxWidth = double.PositiveInfinity, TextAlignment align = TextAlignment.Left)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush) { TextAlignment = align };
        if (!double.IsInfinity(maxWidth)) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    /// <summary>Draws text with its visible outline centred on <paramref name="center"/>, whatever the font's spacing.</summary>
    private static void DrawCentered(DrawingContext ctx, FormattedText text, Point center, IBrush fill, IPen? outline = null)
    {
        if (text.BuildGeometry(new Point(0, 0)) is not { } g) return;
        var b = g.Bounds;
        using (ctx.PushTransform(Matrix.CreateTranslation(center.X - b.Center.X, center.Y - b.Center.Y)))
            ctx.DrawGeometry(fill, outline, g);
    }

    /// <summary>Text on one line, squeezed sideways when too long, the way printed cards fit long names.</summary>
    private static void DrawSqueezed(DrawingContext ctx, FormattedText text, Rect box, bool alignRight = false)
    {
        var sx = text.Width > box.Width ? box.Width / text.Width : 1;
        var x = alignRight ? box.Right - text.Width * sx : box.X;
        // Centre the capitals, not the font box, vertically.
        var capTop = text.Baseline - text.Height * 0.62;
        var y = box.Y + box.Height / 2 - (capTop + text.Baseline) / 2;
        using (ctx.PushTransform(Matrix.CreateScale(sx, 1) * Matrix.CreateTranslation(x, y)))
            ctx.DrawText(text, new Point(0, 0));
    }

    // ---------------------------------------------------------------- the card

    public static void Draw(DrawingContext ctx, CardData c, string typeLine, string text, Bitmap? art, string copyright)
    {
        var (color, surface, whiteName) = Frame(c);

        // Card edge, then the frame surface.
        ctx.DrawRectangle(Brush("#120E0A"), null, new RoundedRect(new Rect(0, 0, W, H), 9));
        var frameRect = new Rect(5, 5, W - 10, H - 10);
        ctx.DrawImage(FrameTexture.Get(color, surface), frameRect);
        if (c.IsPendulum)
        {
            // Pendulum: the monster colour fades into Spell green halfway down the scale box.
            var split = PendulumBox.Center.Y;
            using (ctx.PushClip(new Rect(frameRect.X, split, frameRect.Width, frameRect.Bottom - split)))
                ctx.DrawImage(FrameTexture.Get(SpellGreen, FrameSurface.Mottled), frameRect);
            ctx.DrawRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 1) },
            }, null, new Rect(frameRect.X, split - 22, frameRect.Width, 22));
        }
        // A soft darker rim, as printed frames have.
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 3), frameRect.Deflate(1.5));

        DrawNamePlate(ctx, c, color, whiteName);
        DrawAttribute(ctx, c);
        if (c.IsMonster) DrawStars(ctx, c);
        else DrawSpellTrapLine(ctx, c);

        var artBox = c.IsPendulum ? PendulumArt : Art;
        DrawArt(ctx, artBox, art);
        if (c.IsLink) DrawLinkArrows(ctx, c, artBox);
        if (c.IsPendulum) DrawPendulumBox(ctx, c);
        DrawTextBox(ctx, c, typeLine, text, c.IsPendulum ? PendulumTextBox : TextBox);

        IBrush footerInk = c.IsXyz ? Brushes.White : Brush("#141414");
        var pass = Text(c.Id.ToString("D8", CultureInfo.InvariantCulture), BodyFace, 10.5, footerInk);
        ctx.DrawText(pass, new Point(TextBox.X, FooterY + 5 - pass.Baseline));
        if (copyright.Length > 0)
        {
            var copy = Text(copyright, BodyBold, 10, footerInk);
            ctx.DrawText(copy, new Point(TextBox.Right - copy.Width, FooterY + 5 - copy.Baseline));
        }
    }

    // ---------------------------------------------------------------- parts

    /// <summary>The engraved plate the name is printed on: a recessed panel with a dark upper-left and light lower-right edge.</summary>
    private static void DrawNamePlate(DrawingContext ctx, CardData c, Color frame, bool whiteName)
    {
        var plate = NamePlate;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)), null, plate);
        var dark = new Pen(new SolidColorBrush(Shade(frame, -0.55)), 2);
        var light = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1.5);
        ctx.DrawLine(dark, plate.TopLeft, plate.TopRight);
        ctx.DrawLine(dark, plate.TopLeft, plate.BottomLeft);
        ctx.DrawLine(light, plate.BottomLeft, plate.BottomRight);
        ctx.DrawLine(light, plate.TopRight, plate.BottomRight);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 1), plate.Deflate(2.5));

        IBrush ink = whiteName ? Brushes.White : Brush("#111111");
        var nameText = string.IsNullOrWhiteSpace(c.Name) ? "Card Name" : c.Name;
        var name = Text(nameText, NameFace, 27, ink);
        // Room for the Attribute orb on the right.
        var box = new Rect(plate.X + 9, plate.Y, AttributeCenter.X - AttributeSize / 2 - 6 - (plate.X + 9), plate.Height);
        if (whiteName)
        {
            // White names carry a thin dark edge so they read on any frame.
            var shadow = Text(nameText, NameFace, 27, new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
            using (ctx.PushTransform(Matrix.CreateTranslation(1, 1.2))) DrawSqueezed(ctx, shadow, box);
        }
        DrawSqueezed(ctx, name, box);
    }

    private static void DrawAttribute(DrawingContext ctx, CardData c)
    {
        var (kanji, word, color) = AttributeLook(c);
        var center = AttributeCenter;
        var r = AttributeSize / 2;
        // Dark rim, white ring, then the coloured orb lit from the upper left.
        ctx.DrawEllipse(Brush("#2A1E14"), null, center, r + 1.6, r + 1.6);
        ctx.DrawEllipse(Brush("#F6F2EA"), null, center, r, r);
        ctx.DrawEllipse(new RadialGradientBrush
        {
            Center = new RelativePoint(0.36, 0.32, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.36, 0.32, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Shade(color, 0.35), 0), new GradientStop(color, 0.55), new GradientStop(Shade(color, -0.45), 1) },
        }, null, center, r - 2.2, r - 2.2);
        if (kanji.Length > 0)
            DrawCentered(ctx, Text(kanji, KanjiFace, AttributeSize * 0.6, Brushes.White), center, Brushes.White,
                new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 0.8));
        else
            DrawCentered(ctx, Text(word, CapsBold, 14, Brushes.White), center, Brushes.White);
    }

    private static Geometry StarGeometry(Point c, double r)
    {
        var g = new StreamGeometry();
        using var s = g.Open();
        for (int i = 0; i < 10; i++)
        {
            var rad = i % 2 == 0 ? r : r * 0.43;
            var a = -Math.PI / 2 + i * Math.PI / 5;
            var p = new Point(c.X + rad * Math.Cos(a), c.Y + rad * Math.Sin(a));
            if (i == 0) s.BeginFigure(p, true); else s.LineTo(p);
        }
        s.EndFigure(true);
        return g;
    }

    /// <summary>Level orbs line up from the right; Rank orbs (black) from the left.</summary>
    private static void DrawStars(DrawingContext ctx, CardData c)
    {
        if (c.IsLink) return;
        var count = Math.Clamp(c.Level, 0, 13);
        var rank = c.IsXyz;
        var half = StarSize / 2;
        var step = count > 12 ? (StarRight - StarLeft - StarSize) / 12 : StarStep;
        for (int i = 0; i < count; i++)
        {
            var x = rank ? StarLeft + half + i * step : StarRight - half - i * step;
            var center = new Point(x, StarY);
            ctx.DrawEllipse(Brush(rank ? "#050505" : "#5E1206"), null, center, half, half);
            var stops = rank
                ? new GradientStops { new GradientStop(Hex("#5A5A5A"), 0), new GradientStop(Hex("#1B1B1B"), 0.6), new GradientStop(Hex("#000000"), 1) }
                : new GradientStops { new GradientStop(Hex("#FF9A4A"), 0), new GradientStop(Hex("#E24A1C"), 0.6), new GradientStop(Hex("#9E1F0B"), 1) };
            ctx.DrawEllipse(new RadialGradientBrush
            {
                Center = new RelativePoint(0.38, 0.32, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.38, 0.32, RelativeUnit.Relative),
                GradientStops = stops,
            }, null, center, half - 1.2, half - 1.2);
            ctx.DrawGeometry(Brush(rank ? "#F0C850" : "#FFD640"), new Pen(Brush(rank ? "#8A6510" : "#B35A0C"), 0.7), StarGeometry(center, half * 0.74));
        }
    }

    /// <summary>"[Spell Card]" or "[Trap Card ∞]" under the name, right-aligned, with the kind's icon inside the bracket.</summary>
    private static void DrawSpellTrapLine(DrawingContext ctx, CardData c)
    {
        var (label, _) = CardFaceText.SpellTrapLine(c);
        var kind = c.Kind == CardKind.Spell ? c.SpellKind switch
        {
            SpellKind.QuickPlay => "quick", SpellKind.Continuous => "continuous", SpellKind.Equip => "equip",
            SpellKind.Field => "field", SpellKind.Ritual => "ritual", _ => "",
        } : c.TrapKind switch { TrapKind.Continuous => "continuous", TrapKind.Counter => "counter", _ => "" };

        var ink = Brush("#111111");
        var open = Text("[" + label, CapsBold, 19, ink);
        var close = Text("]", CapsBold, 19, ink);
        var icon = kind.Length > 0 ? 17.0 : 0;
        var gap = kind.Length > 0 ? 5.0 : 0;
        var total = open.Width + gap + icon + gap / 2 + close.Width;
        var x = SpellLine.Right - total;
        var baseline = SpellLine.Y + SpellLine.Height / 2 + 6.5;
        ctx.DrawText(open, new Point(x, baseline - open.Baseline));
        if (kind.Length > 0) DrawKindIcon(ctx, kind, new Point(x + open.Width + gap + icon / 2, baseline - 6.5), icon);
        ctx.DrawText(close, new Point(x + open.Width + gap + icon + gap / 2, baseline - close.Baseline));
    }

    /// <summary>The small Spell/Trap property icons, drawn as shapes in a dark disc like the printed ones.</summary>
    private static void DrawKindIcon(DrawingContext ctx, string kind, Point c, double size)
    {
        var r = size / 2;
        ctx.DrawEllipse(Brush("#111111"), null, c, r, r);
        var white = Brushes.White;
        var pen = new Pen(white, 1.6, lineCap: PenLineCap.Round);
        switch (kind)
        {
            case "quick": // lightning bolt
                ctx.DrawGeometry(white, null, Poly(c, (1.5, -6.5), (-4, 1), (-0.5, 1), (-2, 6.5), (4, -1.5), (0.5, -1.5)));
                break;
            case "continuous": // infinity
                ctx.DrawEllipse(null, pen, new Point(c.X - 2.9, c.Y), 2.7, 2.1);
                ctx.DrawEllipse(null, pen, new Point(c.X + 2.9, c.Y), 2.7, 2.1);
                break;
            case "equip": // cross
                ctx.DrawLine(new Pen(white, 2.2), new Point(c.X - 4.5, c.Y), new Point(c.X + 4.5, c.Y));
                ctx.DrawLine(new Pen(white, 2.2), new Point(c.X, c.Y - 4.5), new Point(c.X, c.Y + 4.5));
                break;
            case "field": // compass star
                ctx.DrawGeometry(white, null, Poly(c, (0, -6), (1.4, -1.4), (6, 0), (1.4, 1.4), (0, 6), (-1.4, 1.4), (-6, 0), (-1.4, -1.4)));
                break;
            case "ritual": // flame
                ctx.DrawGeometry(white, null, Poly(c, (0, -6.5), (3.5, -1), (3, 3.5), (0, 6), (-3, 3.5), (-3.5, -1), (-1, 1.5)));
                break;
            case "counter": // turning arrow
                ctx.DrawGeometry(null, pen, Poly(c, (-4, 1), (-4, -2.5), (3, -2.5)));
                ctx.DrawGeometry(white, null, Poly(c, (2, -5.5), (5.5, -2.5), (2, 0.5)));
                ctx.DrawGeometry(null, pen, Poly(c, (4, -0.5), (4, 3.5), (-3, 3.5)));
                break;
        }
    }

    private static Geometry Poly(Point c, params (double X, double Y)[] pts)
    {
        var g = new StreamGeometry();
        using var s = g.Open();
        s.BeginFigure(new Point(c.X + pts[0].X, c.Y + pts[0].Y), true);
        foreach (var p in pts.Skip(1)) s.LineTo(new Point(c.X + p.X, c.Y + p.Y));
        s.EndFigure(true);
        return g;
    }

    /// <summary>The art window: a dark metal bevel with a fine light inner edge.</summary>
    private static void DrawArt(DrawingContext ctx, Rect box, Bitmap? art)
    {
        ctx.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Hex("#7A6A58"), 0), new GradientStop(Hex("#2B241D"), 0.5), new GradientStop(Hex("#8C7B66"), 1) },
        }, null, box.Inflate(5));
        ctx.DrawRectangle(Brush("#16120E"), null, box.Inflate(2.2));
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 0.8), box.Inflate(1.2));

        if (art is { } image && image.PixelSize.Width > 0)
        {
            // Fill the window, cropping the middle of the image like UniformToFill.
            var src = new Rect(0, 0, image.Size.Width, image.Size.Height);
            var target = box.Width / box.Height;
            src = src.Width / src.Height > target
                ? new Rect((src.Width - src.Height * target) / 2, 0, src.Height * target, src.Height)
                : new Rect(0, (src.Height - src.Width / target) / 2, src.Width, src.Width / target);
            ctx.DrawImage(image, src, box);
            return;
        }
        ctx.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Hex("#3B3F55"), 0), new GradientStop(Hex("#1E2030"), 1) },
        }, null, box);
        DrawCentered(ctx, Text("Card art goes here", BodyFace, 18, Brush("#8F95B2")), box.Center, Brush("#8F95B2"));
    }

    /// <summary>The eight Link Arrows on the art window's edges: lit red where set, dark where not.</summary>
    private static void DrawLinkArrows(DrawingContext ctx, CardData c, Rect box)
    {
        var o = box.Inflate(5);
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
            IBrush fill = on
                ? new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Hex("#FF6A4A"), 0), new GradientStop(Hex("#C8160E"), 1) } }
                : Brush("#3C3A3A");
            ctx.DrawGeometry(fill, new Pen(Brush(on ? "#5A0800" : "#1A1A1A"), 1.2), g);
        }
        double cx = o.Center.X, cy = o.Center.Y, l = o.Left, r = o.Right, t = o.Top, b = o.Bottom;
        const double half = 21, tip = 13;
        Arrow(LinkMarker.Top, new(cx - half, t + 3), new(cx + half, t + 3), new(cx, t - tip + 3));
        Arrow(LinkMarker.Bottom, new(cx - half, b - 3), new(cx + half, b - 3), new(cx, b + tip - 3));
        Arrow(LinkMarker.Left, new(l + 3, cy - half), new(l + 3, cy + half), new(l - tip + 3, cy));
        Arrow(LinkMarker.Right, new(r - 3, cy - half), new(r - 3, cy + half), new(r + tip - 3, cy));
        Arrow(LinkMarker.TopLeft, new(l - 7, t - 7), new(l + 24, t - 7), new(l - 7, t + 24));
        Arrow(LinkMarker.TopRight, new(r + 7, t - 7), new(r - 24, t - 7), new(r + 7, t + 24));
        Arrow(LinkMarker.BottomLeft, new(l - 7, b + 7), new(l + 24, b + 7), new(l - 7, b - 24));
        Arrow(LinkMarker.BottomRight, new(r + 7, b + 7), new(r - 24, b + 7), new(r + 7, b - 24));
    }

    /// <summary>The Pendulum Effect box with the blue (left) and red (right) scale columns.</summary>
    private static void DrawPendulumBox(DrawingContext ctx, CardData c)
    {
        var box = PendulumBox;
        DrawPaperBox(ctx, box, light: true);
        var sw = PendulumScaleWidth;
        var ink = Brush("#111111");
        foreach (var (x, value, gem) in new[] { (box.X, c.LeftScale, "#2D6FD8"), (box.Right - sw, c.RightScale, "#D8322D") })
        {
            var column = new Rect(x, box.Y, sw, box.Height);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, column.Deflate(new Thickness(1, 1, 1, 1)));
            var g = new Point(column.Center.X, box.Y + 13);
            ctx.DrawGeometry(Brush(gem), new Pen(Brush("#111111"), 0.8), Poly(g, (0, -7), (8, 0), (0, 7), (-8, 0)));
            DrawCentered(ctx, Text(value.ToString(CultureInfo.InvariantCulture), Numbers, 24, ink), new Point(column.Center.X, box.Y + 38), ink);
        }
        ctx.DrawLine(new Pen(Brush("#6B5A3E"), 1), new Point(box.X + sw, box.Y + 2), new Point(box.X + sw, box.Bottom - 2));
        ctx.DrawLine(new Pen(Brush("#6B5A3E"), 1), new Point(box.Right - sw, box.Y + 2), new Point(box.Right - sw, box.Bottom - 2));
        DrawFitted(ctx, c.PendulumText, BodyFace, new Rect(box.X + sw + 6, box.Y + 3, box.Width - 2 * sw - 12, box.Height - 6), 11.5);
    }

    /// <summary>The cream text box with its thin gold-brown border.</summary>
    private static void DrawPaperBox(DrawingContext ctx, Rect box, bool light = false)
    {
        ctx.DrawRectangle(new SolidColorBrush(Hex("#5E4524")), null, box.Inflate(2));
        ctx.DrawImage(FrameTexture.Get(Hex(light ? "#EFE8D4" : "#ECE1C8"), FrameSurface.Paper, 420, 160), box);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 196, 150, 82)), 1.4), box.Deflate(0.7));
    }

    private static void DrawTextBox(DrawingContext ctx, CardData c, string typeLine, string text, Rect box)
    {
        DrawPaperBox(ctx, box);
        var ink = Brush("#111111");
        var inner = box.Deflate(new Thickness(7, 3, 7, 3));
        var top = inner.Y;
        var bottom = inner.Bottom;

        if (c.IsMonster)
        {
            // "[Spellcaster / Tuner / Effect]" in bold small capitals, spaced like current cards.
            var spaced = typeLine.Replace("/", " / ");
            var line = Text(spaced, CapsBold, 14, ink);
            DrawSqueezed(ctx, line, new Rect(inner.X, top, inner.Width, 17));
            top += 18;

            // The ATK/DEF rule and stats at the bottom of the box.
            var statsTop = inner.Bottom - 17;
            ctx.DrawLine(new Pen(ink, 1.1), new Point(inner.X, statsTop), new Point(inner.Right, statsTop));
            var stats = c.IsLink
                ? $"ATK/{CardFaceText.Stat(c.Atk)}  LINK–{c.LinkRating}"
                : $"ATK/{CardFaceText.Stat(c.Atk)}  DEF/{CardFaceText.Stat(c.Def)}";
            DrawSqueezed(ctx, Text(stats, Numbers, 15.5, ink), new Rect(inner.X, statsTop + 1, inner.Width, 16), alignRight: true);
            bottom = statsTop - 2;
        }

        var normal = c.IsMonster && c.Frame == MonsterFrame.Normal;
        DrawFitted(ctx, text, normal ? BodyItalic : BodyFace, new Rect(inner.X, top, inner.Width, bottom - top), 12.5);
    }

    /// <summary>Justified card text, shrunk until it fits, like dense printed cards.</summary>
    private static void DrawFitted(DrawingContext ctx, string text, Typeface face, Rect box, double size)
    {
        if (string.IsNullOrWhiteSpace(text) || box.Height <= 0) return;
        var ink = Brush("#111111");
        FormattedText ft;
        do
        {
            ft = Text(text, face, size, ink, box.Width, TextAlignment.Justify);
            ft.LineHeight = size * 1.02;
            size -= 0.25;
        } while (ft.Height > box.Height && size > 6);
        using (ctx.PushClip(box.Inflate(new Thickness(0, 0, 0, 1)))) ctx.DrawText(ft, box.TopLeft);
    }
}
