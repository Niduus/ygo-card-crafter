namespace CardCrafter.Core.Combos;

/// <summary>One copy of a card on the board.</summary>
public sealed class BoardCard(long code, int copy)
{
    public long Code { get; } = code;
    /// <summary>Which copy of the card in the deck list (1 = the first). Tokens count on from the deck's copies.</summary>
    public int Copy { get; } = copy;
    public ComboPosition Position { get; set; }
    public bool IsToken { get; init; }
    /// <summary>A Spell/Trap Zone card placed as a Pendulum Scale.</summary>
    public bool InPendulumZone { get; set; }
    /// <summary>Xyz Materials under this card, bottom first.</summary>
    public List<BoardCard> Materials { get; } = [];

    public string Key => $"{Code}#{Copy}";
    public bool FaceDown => Position is ComboPosition.FaceDown or ComboPosition.FaceDownDefense;

    public BoardCard Clone()
    {
        var c = new BoardCard(Code, Copy) { Position = Position, IsToken = IsToken, InPendulumZone = InPendulumZone };
        c.Materials.AddRange(Materials.Select(m => m.Clone()));
        return c;
    }
}

/// <summary>Where a card is: its zone, the zone's number (0-based) on the field, and for an Xyz Material the monster it is under.</summary>
public sealed record BoardSpot(BoardCard Card, ComboZone Zone, int Index, BoardCard? Owner = null);

/// <summary>Your side of the field, your hand and your piles, at one moment of a combo.</summary>
public sealed class ComboBoard
{
    public const int Zones = 5;

    public BoardCard?[] MonsterZones { get; } = new BoardCard?[Zones];
    public BoardCard?[] ExtraMonsterZones { get; } = new BoardCard?[2];
    /// <summary>The Spell &amp; Trap Zones; the two at the ends are also the Pendulum Zones.</summary>
    public BoardCard?[] SpellTrapZones { get; } = new BoardCard?[Zones];
    public BoardCard? FieldZone { get; set; }
    public List<BoardCard> Hand { get; } = [];
    /// <summary>The Deck, top card first.</summary>
    public List<BoardCard> Deck { get; } = [];
    public List<BoardCard> ExtraDeck { get; } = [];
    /// <summary>The GY, the card sent there last at the end.</summary>
    public List<BoardCard> Graveyard { get; } = [];
    public List<BoardCard> Banished { get; } = [];

    public ComboBoard Clone()
    {
        var b = new ComboBoard { FieldZone = FieldZone?.Clone() };
        for (int i = 0; i < Zones; i++)
        {
            b.MonsterZones[i] = MonsterZones[i]?.Clone();
            b.SpellTrapZones[i] = SpellTrapZones[i]?.Clone();
        }
        for (int i = 0; i < 2; i++) b.ExtraMonsterZones[i] = ExtraMonsterZones[i]?.Clone();
        foreach (var (from, to) in new[] { (Hand, b.Hand), (Deck, b.Deck), (ExtraDeck, b.ExtraDeck), (Graveyard, b.Graveyard), (Banished, b.Banished) })
            to.AddRange(from.Select(c => c.Clone()));
        return b;
    }

    /// <summary>Every card and where it is, Xyz Materials included.</summary>
    public IEnumerable<BoardSpot> Spots()
    {
        for (int i = 0; i < Zones; i++)
            if (MonsterZones[i] is { } m)
            {
                yield return new BoardSpot(m, ComboZone.MonsterZone, i);
                foreach (var x in m.Materials) yield return new BoardSpot(x, ComboZone.XyzMaterial, i, m);
            }
        for (int i = 0; i < 2; i++)
            if (ExtraMonsterZones[i] is { } m)
            {
                yield return new BoardSpot(m, ComboZone.ExtraMonsterZone, i);
                foreach (var x in m.Materials) yield return new BoardSpot(x, ComboZone.XyzMaterial, i, m);
            }
        for (int i = 0; i < Zones; i++)
            if (SpellTrapZones[i] is { } s) yield return new BoardSpot(s, s.InPendulumZone ? ComboZone.PendulumZone : ComboZone.SpellTrapZone, i);
        if (FieldZone is { } f) yield return new BoardSpot(f, ComboZone.FieldZone, 0);
        foreach (var (list, zone) in Piles)
            for (int i = 0; i < list.Count; i++) yield return new BoardSpot(list[i], zone, i);
    }

    private IEnumerable<(List<BoardCard> List, ComboZone Zone)> Piles =>
        [(Hand, ComboZone.Hand), (Graveyard, ComboZone.Graveyard), (Banished, ComboZone.Banished), (Deck, ComboZone.Deck), (ExtraDeck, ComboZone.ExtraDeck)];

    /// <summary>The cards on the field: monsters (with their materials under them), Spells and Traps, and the Field Spell.</summary>
    public IEnumerable<BoardSpot> FieldSpots() => Spots().Where(s => IsOnField(s.Zone));

    public BoardSpot? Find(BoardCard card) => Spots().FirstOrDefault(s => ReferenceEquals(s.Card, card));

    public static bool IsOnField(ComboZone zone) => zone is ComboZone.MonsterZone or ComboZone.ExtraMonsterZone or ComboZone.SpellTrapZone
        or ComboZone.PendulumZone or ComboZone.FieldZone;

    /// <summary>Takes the card out of where it is. Its Xyz Materials stay with it.</summary>
    public void Remove(BoardSpot spot)
    {
        switch (spot.Zone)
        {
            case ComboZone.MonsterZone: MonsterZones[spot.Index] = null; break;
            case ComboZone.ExtraMonsterZone: ExtraMonsterZones[spot.Index] = null; break;
            case ComboZone.SpellTrapZone or ComboZone.PendulumZone: SpellTrapZones[spot.Index] = null; break;
            case ComboZone.FieldZone: FieldZone = null; break;
            case ComboZone.XyzMaterial: spot.Owner!.Materials.Remove(spot.Card); break;
            default: Pile(spot.Zone)!.Remove(spot.Card); break;
        }
        spot.Card.InPendulumZone = false;
    }

    public List<BoardCard>? Pile(ComboZone zone) => zone switch
    {
        ComboZone.Hand => Hand,
        ComboZone.Deck => Deck,
        ComboZone.ExtraDeck => ExtraDeck,
        ComboZone.Graveyard => Graveyard,
        ComboZone.Banished => Banished,
        _ => null,
    };

    /// <summary>The field zones of a kind: the 5 Main Monster Zones, the 2 Extra Monster Zones, or the Spell &amp; Trap Zones.</summary>
    public BoardCard?[]? Row(ComboZone zone) => zone switch
    {
        ComboZone.MonsterZone => MonsterZones,
        ComboZone.ExtraMonsterZone => ExtraMonsterZones,
        ComboZone.SpellTrapZone or ComboZone.PendulumZone => SpellTrapZones,
        _ => null,
    };
}
