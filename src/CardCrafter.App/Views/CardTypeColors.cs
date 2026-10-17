using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CardCrafter.Core.Model;

namespace CardCrafter.App.Views;

/// <summary>
/// The colour of a card's frame, for the card lists: orange for Effect Monsters, green for Spells, pink for Traps, and so
/// on. Rows get a faint wash of it (so the text stays readable) and a solid stripe on the left edge.
/// </summary>
public static class CardTypeColors
{
    public static Color Frame(CardData card) => card.Kind switch
    {
        CardKind.Spell => Color.Parse("#1D9A87"),
        CardKind.Trap => Color.Parse("#B8458F"),
        _ => card.Frame switch
        {
            MonsterFrame.Normal => Color.Parse("#C9A659"),
            MonsterFrame.Ritual => Color.Parse("#4F7BD1"),
            MonsterFrame.Fusion => Color.Parse("#9157B5"),
            MonsterFrame.Synchro => Color.Parse("#D6D8DE"),
            // Xyz frames are black: a dark grey still shows on the dark lists.
            MonsterFrame.Xyz => Color.Parse("#5B5E6A"),
            MonsterFrame.Link => Color.Parse("#2A63B8"),
            MonsterFrame.Token => Color.Parse("#9EA2AD"),
            _ => Color.Parse("#D0773A"),
        },
    };

    private static readonly Color Spell = Color.Parse("#1D9A87");

    /// <summary>The row background: the frame colour, faint. Pendulum Monsters fade into Spell green, like their frame.</summary>
    public static IBrush Wash(CardData card)
    {
        var frame = WithAlpha(Frame(card), 0x38);
        if (!card.IsPendulum) return new SolidColorBrush(frame);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(frame, 0.35), new GradientStop(WithAlpha(Spell, 0x38), 1) },
        };
    }

    /// <summary>The solid stripe on the left edge of the row.</summary>
    public static IBrush Stripe(CardData card) => new SolidColorBrush(Frame(card));

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

/// <summary>For list templates whose items are cards: ConverterParameter "stripe" gives the edge stripe, otherwise the wash.</summary>
public sealed class CardTypeBrushConverter : IValueConverter
{
    public static CardTypeBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is CardData card
        ? parameter as string == "stripe" ? CardTypeColors.Stripe(card) : CardTypeColors.Wash(card)
        : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
