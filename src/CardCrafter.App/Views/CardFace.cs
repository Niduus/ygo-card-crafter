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
/// The card preview, assembled from the card textures the user loaded (YGO Omega's frames, Attribute orbs,
/// stars, Link Arrows, Spell/Trap icons and border): art under the frame, the frame, the border, then the name,
/// Attribute, stars, type line, text and stats in the places the frames are made for. Positions are in the
/// textures' own 813 × 1185 pixels, measured from them. Without textures it shows a plain placeholder.
/// </summary>
public sealed class CardFace : Control
{
    /// <summary>The textures' size; everything is laid out in these pixels and scaled to the control or export size.</summary>
    public const double W = 813, H = 1185;

    private CardData? _card;
    private string _typeLine = "";
    private string _text = "";
    private string _copyright = "";
    private Bitmap? _art;

    public CardFace()
    {
        AttachedToVisualTree += (_, _) => { FrameImages.Changed -= InvalidateVisual; FrameImages.Changed += InvalidateVisual; };
        DetachedFromVisualTree += (_, _) => FrameImages.Changed -= InvalidateVisual;
    }

    public void Update(CardData card, string typeLine, string text, Bitmap? art, string copyright = "")
    {
        _card = card;
        _typeLine = typeLine;
        _text = text;
        _art = art;
        _copyright = copyright;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? 300 : available.Width;
        return new Size(w, w * H / W);
    }

    // ---------------------------------------------------------------- layout (texture pixels)

    private static readonly Rect NameBand = new(44, 51, 727, 84);
    private const double NameLeft = 66;
    private static readonly Point AttributeCenter = new(697, 93);
    private const double AttributeSize = 68;
    private const double StarY = 167, StarSize = 42, StarStep = 55, LevelRight = 720, RankLeft = 93;
    private const double SpellLineRight = 721, SpellLineBaseline = 183;
    private static readonly Rect Art = new(99, 218, 616, 615);
    private static readonly Rect PendulumArt = new(54, 212, 706, 525);
    private static readonly Rect TextBox = new(56, 890, 702, 225);
    private static readonly Rect PendulumTextBox = new(55, 888, 704, 227);
    private const double RuleY = 1078;
    private static readonly Rect PendulumEffect = new(124, 752, 566, 122);
    private static readonly Point LeftScale = new(82, 846), RightScale = new(731, 846);
    private const double FooterBaseline = 1150, FooterLeft = 56, FooterRight = 757;

    /// <summary>Where each Link Arrow texture is centred, matched to the arrow slots printed on the Link frame.</summary>
    private static readonly (LinkMarker Marker, string Name, Point Center)[] LinkArrows =
    [
        (LinkMarker.Top, "top", new(406.5, 191.5)), (LinkMarker.Bottom, "bottom", new(406.5, 857)),
        (LinkMarker.Left, "left", new(72, 525)), (LinkMarker.Right, "right", new(741, 525)),
        (LinkMarker.TopLeft, "topleft", new(102, 221)), (LinkMarker.TopRight, "topright", new(711, 221)),
        (LinkMarker.BottomLeft, "bottomleft", new(102, 829)), (LinkMarker.BottomRight, "bottomright", new(711, 829)),
    ];

    /// <summary>The frame texture for this card, e.g. "synchro" or "pendulum-effect".</summary>
    public static string FrameKey(CardData c)
    {
        if (c.Kind == CardKind.Spell) return "spell";
        if (c.Kind == CardKind.Trap) return "trap";
        var frame = c.Frame.ToString().ToLowerInvariant();
        return c.IsPendulum && c.Frame != MonsterFrame.Link ? "pendulum-" + frame : frame;
    }

    // ---------------------------------------------------------------- text helpers

    private static FormattedText Text(string s, Typeface face, double size, IBrush brush, double maxWidth = double.PositiveInfinity, TextAlignment align = TextAlignment.Left)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush) { TextAlignment = align };
        if (!double.IsInfinity(maxWidth)) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    /// <summary>The height of a capital letter in this font, measured from its outline.</summary>
    private static double CapHeight(Typeface face, double size) =>
        Text("H", face, size, Brushes.Black).BuildGeometry(new Point(0, 0))?.Bounds.Height ?? size * 0.7;

    /// <summary>One line of text on a baseline, squeezed sideways when it is wider than <paramref name="maxWidth"/>, as printed cards do.</summary>
    private static void DrawLine(DrawingContext ctx, FormattedText text, double x, double baseline, double maxWidth, bool alignRight = false)
    {
        var sx = text.Width > maxWidth ? maxWidth / text.Width : 1;
        var left = alignRight ? x - text.Width * sx : x;
        using (ctx.PushTransform(Matrix.CreateScale(sx, 1) * Matrix.CreateTranslation(left, baseline - text.Baseline)))
            ctx.DrawText(text, new Point(0, 0));
    }

    /// <summary>Draws text with its visible outline centred on <paramref name="center"/>.</summary>
    private static void DrawCentered(DrawingContext ctx, FormattedText text, Point center, IBrush fill)
    {
        if (text.BuildGeometry(new Point(0, 0)) is not { } g) return;
        var b = g.Bounds;
        using (ctx.PushTransform(Matrix.CreateTranslation(center.X - b.Center.X, center.Y - b.Center.Y)))
            ctx.DrawGeometry(fill, null, g);
    }

    /// <summary>Justified card text, shrunk until it fits its box.</summary>
    private static void DrawFitted(DrawingContext ctx, string text, Typeface face, Rect box, double size, IBrush ink)
    {
        if (string.IsNullOrWhiteSpace(text) || box.Height <= 0) return;
        FormattedText ft;
        do
        {
            ft = Text(text, face, size, ink, box.Width, TextAlignment.Justify);
            ft.LineHeight = size * 1.04;
            size -= 0.5;
        } while (ft.Height > box.Height && size > 10);
        using (ctx.PushClip(box.Inflate(new Thickness(0, 0, 0, 2)))) ctx.DrawText(ft, box.TopLeft);
    }

    private static void DrawImage(DrawingContext ctx, string slot, Rect rect)
    {
        if (FrameImages.Get(slot) is { } image) ctx.DrawImage(image, rect);
    }

    private static Rect Around(Point center, double width, double height) => new(center.X - width / 2, center.Y - height / 2, width, height);

    // ---------------------------------------------------------------- drawing

    public override void Render(DrawingContext ctx) => Draw(ctx, Bounds.Size);

    /// <summary>Draws the whole card scaled into <paramref name="size"/>. Also used to export images.</summary>
    public void Draw(DrawingContext ctx, Size size)
    {
        if (_card is null) return;
        var c = _card;
        var scale = Math.Min(size.Width / W, size.Height / H);
        using var _ = ctx.PushTransform(Matrix.CreateScale(scale, scale));

        var key = FrameKey(c);
        var frame = FrameImages.Get(key);
        // A Pendulum kind with no frame of its own: its plain frame with the Pendulum lower half laid over it.
        var pendulumOverlay = frame is null && c.IsPendulum && FrameImages.Has("pendulum-bottom");
        frame ??= FrameImages.Get(c.Kind == CardKind.Monster ? c.Frame.ToString().ToLowerInvariant() : key);
        if (frame is null)
        {
            DrawPlaceholder(ctx, c);
            return;
        }
        var pendulum = c.IsPendulum && (FrameImages.Has(key) || pendulumOverlay);

        DrawArt(ctx, pendulum ? PendulumArt : Art);
        ctx.DrawImage(frame, new Rect(0, 0, W, H));
        if (pendulumOverlay) DrawImage(ctx, "pendulum-bottom", new Rect(0, 0, W, H));
        if (c.IsLink) DrawLinkArrows(ctx, c);
        DrawImage(ctx, "border", new Rect(0, 0, W, H));

        DrawName(ctx, c);
        DrawAttribute(ctx, c);
        if (c.IsMonster) DrawStars(ctx, c);
        else DrawSpellTrapLine(ctx, c);
        if (pendulum) DrawPendulum(ctx, c);
        DrawTextBox(ctx, c, pendulum ? PendulumTextBox : TextBox);
        DrawFooter(ctx, c);
    }

    private static bool WhiteName(CardData c) => c.Kind != CardKind.Monster || c.Frame == MonsterFrame.Xyz;

    private void DrawArt(DrawingContext ctx, Rect box)
    {
        if (_art is { } image && image.PixelSize.Width > 0)
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
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#23252F")), null, box);
        var grey = new SolidColorBrush(Color.Parse("#8F95B2"));
        DrawCentered(ctx, Text("Card art goes here", CardFonts.Body, 34, grey), box.Center, grey);
    }

    private static void DrawName(DrawingContext ctx, CardData c)
    {
        IBrush ink = WhiteName(c) ? Brushes.White : Brushes.Black;
        var name = string.IsNullOrWhiteSpace(c.Name) ? "Card Name" : c.Name;
        const double size = 58;
        var text = Text(name, CardFonts.Name, size, ink);
        // Capitals centred in the name band; long names squeezed to stop short of the Attribute.
        var baseline = NameBand.Center.Y + CapHeight(CardFonts.Name, size) / 2;
        var maxWidth = AttributeCenter.X - AttributeSize / 2 - 18 - NameLeft;
        DrawLine(ctx, text, NameLeft, baseline, maxWidth);
    }

    private static void DrawAttribute(DrawingContext ctx, CardData c)
    {
        var slot = c.Kind switch
        {
            CardKind.Spell => "attribute-spell",
            CardKind.Trap => "attribute-trap",
            _ => "attribute-" + c.Attribute.ToString().ToLowerInvariant(),
        };
        DrawImage(ctx, slot, Around(AttributeCenter, AttributeSize, AttributeSize));
    }

    /// <summary>Levels line up from the right edge of the art, Ranks from the left, as on printed cards.</summary>
    private static void DrawStars(DrawingContext ctx, CardData c)
    {
        if (c.IsLink) return;
        var rank = c.IsXyz;
        var count = Math.Clamp(c.Level, 0, 13);
        var step = count > 12 ? (LevelRight - RankLeft - StarSize) / 12 : StarStep;
        for (int i = 0; i < count; i++)
        {
            var x = rank ? RankLeft + StarSize / 2 + i * step : LevelRight - StarSize / 2 - i * step;
            DrawImage(ctx, rank ? "rank" : "level", Around(new Point(x, StarY), StarSize, StarSize));
        }
    }

    /// <summary>"[Spell Card]" or "[Trap Card ∞]" under the name, right-aligned, with the property icon inside the bracket.</summary>
    private static void DrawSpellTrapLine(DrawingContext ctx, CardData c)
    {
        var (label, _) = CardFaceText.SpellTrapLine(c);
        var icon = c.Kind == CardKind.Spell ? c.SpellKind switch
        {
            SpellKind.QuickPlay => "icon-quickplay", SpellKind.Continuous => "icon-continuous", SpellKind.Equip => "icon-equip",
            SpellKind.Field => "icon-field", SpellKind.Ritual => "icon-ritual", _ => null,
        } : c.TrapKind switch { TrapKind.Continuous => "icon-continuous", TrapKind.Counter => "icon-counter", _ => null };
        if (icon is not null && !FrameImages.Has(icon)) icon = null;

        const double size = 44;
        var face = CardFonts.TypeLine;
        var open = Text("[" + label, face, size, Brushes.Black);
        var close = Text("]", face, size, Brushes.Black);
        var cap = CapHeight(face, size);
        var iconSize = cap * 1.6;
        var gap = icon is null ? 0 : 6;
        var total = open.Width + (icon is null ? 0 : gap + iconSize + gap) + close.Width;
        var x = SpellLineRight - total;
        ctx.DrawText(open, new Point(x, SpellLineBaseline - open.Baseline));
        x += open.Width;
        if (icon is not null)
        {
            DrawImage(ctx, icon, new Rect(x + gap, SpellLineBaseline - cap / 2 - iconSize / 2, iconSize, iconSize));
            x += gap + iconSize + gap;
        }
        ctx.DrawText(close, new Point(x, SpellLineBaseline - close.Baseline));
    }

    private static void DrawLinkArrows(DrawingContext ctx, CardData c)
    {
        foreach (var (marker, name, center) in LinkArrows)
        {
            var slot = $"link-{name}-{(c.LinkMarkers.HasFlag(marker) ? "on" : "off")}";
            if (FrameImages.Get(slot) is not { } image) continue;
            ctx.DrawImage(image, Around(center, image.Size.Width, image.Size.Height));
        }
    }

    private static void DrawPendulum(DrawingContext ctx, CardData c)
    {
        IBrush ink = Brushes.Black;
        foreach (var (center, value) in new[] { (LeftScale, c.LeftScale), (RightScale, c.RightScale) })
            DrawCentered(ctx, Text(value.ToString(CultureInfo.InvariantCulture), CardFonts.Stats, 50, ink), center, ink);
        DrawFitted(ctx, c.PendulumText, CardFonts.Body, PendulumEffect, 23, ink);
    }

    private void DrawTextBox(DrawingContext ctx, CardData c, Rect box)
    {
        IBrush ink = Brushes.Black;
        var inner = new Rect(box.X + 12, box.Y + 6, box.Width - 24, box.Height - 12);
        var top = inner.Y;
        var bottom = inner.Bottom;
        if (c.IsMonster)
        {
            // "[Spellcaster / Tuner / Effect]", spaced like current cards.
            const double typeSize = 31;
            var line = Text(_typeLine.Replace("/", " / "), CardFonts.TypeLine, typeSize, ink);
            var cap = CapHeight(CardFonts.TypeLine, typeSize);
            DrawLine(ctx, line, inner.X, top + 4 + cap, inner.Width);
            top += cap + 13;

            // The rule is printed on the frame; the stats sit under it, right-aligned.
            const double statSize = 31;
            var stats = c.IsLink
                ? $"ATK/{CardFaceText.Stat(c.Atk)}  LINK-{c.LinkRating}"
                : $"ATK/{CardFaceText.Stat(c.Atk)}  DEF/{CardFaceText.Stat(c.Def)}";
            var statCap = CapHeight(CardFonts.Stats, statSize);
            var statBaseline = RuleY + (box.Bottom - RuleY) / 2 + statCap / 2;
            DrawLine(ctx, Text(stats, CardFonts.Stats, statSize, ink), inner.Right - 2, statBaseline, inner.Width, alignRight: true);
            bottom = RuleY - 5;
        }
        var normal = c.IsMonster && c.Frame == MonsterFrame.Normal;
        DrawFitted(ctx, _text, normal ? CardFonts.Flavor : CardFonts.Body, new Rect(inner.X, top, inner.Width, bottom - top), 25, ink);
    }

    private void DrawFooter(DrawingContext ctx, CardData c)
    {
        IBrush ink = c.IsXyz ? Brushes.White : Brushes.Black;
        DrawLine(ctx, Text(c.Id.ToString("D8", CultureInfo.InvariantCulture), CardFonts.Footer, 22, ink), FooterLeft, FooterBaseline, 300);
        if (_copyright.Length > 0)
            DrawLine(ctx, Text(_copyright, CardFonts.Footer, 21, ink), FooterRight, FooterBaseline, 380, alignRight: true);
    }

    /// <summary>Shown until card textures are loaded: the card's details on a plain card, and how to load textures.</summary>
    private void DrawPlaceholder(DrawingContext ctx, CardData c)
    {
        var dim = new SolidColorBrush(Color.Parse("#2A2C36"));
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#16171D")), new Pen(dim, 6), new RoundedRect(new Rect(3, 3, W - 6, H - 6), 24));
        ctx.DrawRectangle(dim, null, NameBand);
        DrawLine(ctx, Text(string.IsNullOrWhiteSpace(c.Name) ? "Card Name" : c.Name, CardFonts.Name, 50, Brushes.White),
            NameLeft, NameBand.Center.Y + CapHeight(CardFonts.Name, 50) / 2, NameBand.Width - 40);
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#20222B")), null, Art);
        var hint = Text("Card textures aren't loaded.\n\nUnder the preview, choose\nTextures… → Use card textures from a .zip…",
            CardFonts.Body, 34, new SolidColorBrush(Color.Parse("#B9BED6")), Art.Width - 60, TextAlignment.Center);
        ctx.DrawText(hint, new Point(Art.X + 30, Art.Center.Y - hint.Height / 2));
        ctx.DrawRectangle(dim, null, TextBox);
        var light = new SolidColorBrush(Color.Parse("#D1D5DB"));
        DrawFitted(ctx, (c.IsMonster ? _typeLine + "\n" : "") + _text, CardFonts.Body, TextBox.Deflate(14), 25, light);
    }
}
