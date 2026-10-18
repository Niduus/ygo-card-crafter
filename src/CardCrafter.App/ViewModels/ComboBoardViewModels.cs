using Avalonia;
using Avalonia.Media;
using CardCrafter.App.Views;
using CardCrafter.Core.Combos;

namespace CardCrafter.App.ViewModels;

/// <summary>A zone on the board, or a card in it: a name tile tinted with the card's frame colour.</summary>
public sealed class ComboTileViewModel
{
    private static readonly IBrush Empty = new SolidColorBrush(Color.Parse("#15161B"));
    private static readonly IBrush Unknown = new SolidColorBrush(Color.Parse("#383B47"));
    private static readonly IBrush FaceDownWash = new SolidColorBrush(Color.Parse("#2A2433"));
    private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#2A2C36"));
    public static readonly IBrush HighlightBrush = new SolidColorBrush(Color.Parse("#F59E0B"));

    /// <summary>An empty zone.</summary>
    public ComboTileViewModel(string zone)
    {
        IsEmpty = true;
        ZoneLabel = zone;
        Name = "";
        Wash = Empty;
        Stripe = Brushes.Transparent;
        Tip = zone;
    }

    public ComboTileViewModel(BoardCard card, ComboCard? info, string name, bool highlight, string zone = "")
    {
        Card = card;
        ZoneLabel = zone;
        Name = name;
        Highlight = highlight;
        FaceDown = card.FaceDown;
        Wash = info is null ? Unknown : card.FaceDown ? FaceDownWash : CardTypeColors.Wash(info.Data);
        Stripe = info is null ? Unknown : CardTypeColors.Stripe(info.Data);
        Badge = card.IsToken ? "TOKEN"
            : card.Position switch
            {
                ComboPosition.Defense => "DEF",
                ComboPosition.FaceDownDefense or ComboPosition.FaceDown => "SET",
                _ => card.InPendulumZone ? "SCALE" : "",
            };
        MaterialCount = card.Materials.Count;
        Tip = info is null ? name : $"{name}\n{info.Text}";
    }

    public BoardCard? Card { get; }
    public bool IsEmpty { get; }
    public bool HasCard => !IsEmpty;
    public string ZoneLabel { get; }
    public string Name { get; }
    public bool Highlight { get; }
    public bool FaceDown { get; }
    public IBrush Wash { get; }
    public IBrush Stripe { get; }
    public IBrush BorderBrush => Highlight ? HighlightBrush : Normal;
    public Thickness BorderThickness => Highlight ? new Thickness(2) : new Thickness(1);
    public string Badge { get; } = "";
    public bool HasBadge => Badge.Length > 0;
    public int MaterialCount { get; }
    public bool HasMaterials => MaterialCount > 0;
    public string MaterialText => $"▣ {MaterialCount}";
    /// <summary>The frame colour of the material on top, peeking out under the monster.</summary>
    public IBrush? MaterialLayer { get; init; }
    public bool HasSecondLayer => MaterialCount > 1;
    /// <summary>The materials under it, by name, for the tooltip and the end board.</summary>
    public string MaterialNames { get; init; } = "";
    public string Tip { get; init; }
}

/// <summary>A pile (GY, banishment, Deck, Extra Deck): its count, its top card, and the list behind it.</summary>
public sealed class ComboPileViewModel(string label, IReadOnlyList<ComboTileViewModel> cards, bool topIsLast)
{
    public string Label { get; } = label;
    public IReadOnlyList<ComboTileViewModel> Cards { get; } = cards;
    public int Count => Cards.Count;
    public string TopName => Cards.Count == 0 ? "" : topIsLast ? Cards[^1].Name : Cards[0].Name;
    public bool Highlight => Cards.Any(c => c.Highlight);
    public IBrush BorderBrush => Highlight ? ComboTileViewModel.HighlightBrush : new SolidColorBrush(Color.Parse("#2A2C36"));
    public string Tip => Cards.Count == 0 ? $"{Label}: empty" : $"{Label}: {Count} card{(Count == 1 ? "" : "s")}. Click to see them.";
}

/// <summary>Your side of the field at one moment, laid out like the game's: Extra Monster Zones above, then Monster and Spell &amp; Trap Zones, then the hand.</summary>
public sealed class ComboBoardViewModel
{
    public IReadOnlyList<ComboTileViewModel> MonsterZones { get; }
    public ComboTileViewModel ExtraLeft { get; }
    public ComboTileViewModel ExtraRight { get; }
    public IReadOnlyList<ComboTileViewModel> SpellTrapZones { get; }
    public ComboTileViewModel FieldZone { get; }
    public IReadOnlyList<ComboTileViewModel> Hand { get; }
    public ComboPileViewModel Graveyard { get; }
    public ComboPileViewModel Banished { get; }
    public ComboPileViewModel ExtraDeck { get; }
    public ComboPileViewModel Deck { get; }
    public string HandLabel => $"Hand ({Hand.Count})";

    public ComboBoardViewModel(ComboBoard board, ComboCatalog catalog, IReadOnlyDictionary<long, string> names, IReadOnlySet<string> touched)
    {
        ComboTileViewModel Tile(BoardCard? card, string zone)
        {
            if (card is null) return new ComboTileViewModel(zone);
            var name = catalog.Name(card.Code, names);
            return new ComboTileViewModel(card, catalog.Find(card.Code), name, touched.Contains(card.Key), zone)
            {
                MaterialNames = card.Materials.Count == 0 ? "" : "Materials: " + string.Join(", ", card.Materials.Select(m => catalog.Name(m.Code, names))),
                MaterialLayer = card.Materials.Count == 0 ? null
                    : catalog.Find(card.Materials[^1].Code) is { } top ? CardTypeColors.Stripe(top.Data) : Brushes.Gray,
                Tip = Tip(card, name),
            };
        }
        string Tip(BoardCard card, string name)
        {
            var text = catalog.Find(card.Code)?.Text ?? "";
            var mats = card.Materials.Count == 0 ? "" : "\n\nMaterials: " + string.Join(", ", card.Materials.Select(m => catalog.Name(m.Code, names)));
            return (text.Length > 0 ? $"{name}\n\n{text}" : name) + mats;
        }
        ComboPileViewModel Pile(string label, List<BoardCard> cards, bool topIsLast) =>
            new(label, cards.Select(c => Tile(c, label)).ToList(), topIsLast);

        MonsterZones = board.MonsterZones.Select(c => Tile(c, "Monster")).ToList();
        ExtraLeft = Tile(board.ExtraMonsterZones[0], "Extra Monster");
        ExtraRight = Tile(board.ExtraMonsterZones[1], "Extra Monster");
        SpellTrapZones = board.SpellTrapZones.Select((c, i) => Tile(c, i is 0 or ComboBoard.Zones - 1 ? "Spell/Trap · Pendulum" : "Spell/Trap")).ToList();
        FieldZone = Tile(board.FieldZone, "Field");
        Hand = board.Hand.Select(c => Tile(c, "Hand")).ToList();
        Graveyard = Pile("GY", board.Graveyard, true);
        Banished = Pile("Banished", board.Banished, true);
        ExtraDeck = Pile("Extra Deck", board.ExtraDeck, false);
        Deck = Pile("Deck", board.Deck, false);
    }
}

/// <summary>One paragraph of a card's text in the viewer: the effect the step used is lit up.</summary>
public sealed record ComboEffectLine(string Text, string Number, bool IsUsed, bool IsRule)
{
    public bool HasNumber => Number.Length > 0;
    public IBrush Background => IsUsed ? new SolidColorBrush(Color.Parse("#33F59E0B")) : Brushes.Transparent;
    public IBrush BorderBrush => IsUsed ? ComboTileViewModel.HighlightBrush : Brushes.Transparent;
    public IBrush Foreground => IsRule ? new SolidColorBrush(Color.Parse("#9CA3AF")) : new SolidColorBrush(Color.Parse("#E5E7EB"));
}

/// <summary>A card in the end board summary, with its text.</summary>
public sealed record ComboEndCard(string Name, string Detail, string Text, IBrush Wash, IBrush Stripe)
{
    public bool HasDetail => Detail.Length > 0;
    public bool HasText => Text.Length > 0;
}

public sealed record ComboEndGroup(string Title, IReadOnlyList<ComboEndCard> Cards);
