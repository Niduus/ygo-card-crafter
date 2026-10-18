using CardCrafter.Core.Model;

namespace CardCrafter.Core.Combos;

/// <summary>One card a step moved: from where to where, and why when a rule sent it somewhere else.</summary>
public sealed record ComboMoveRecord(long Code, int Copy, ComboZone From, ComboZone To, string Note = "")
{
    public string Key => $"{Code}#{Copy}";
}

/// <summary>What one step did when the combo was replayed, and why it couldn't when it couldn't.</summary>
public sealed class StepOutcome
{
    public List<string> Problems { get; } = [];
    public List<ComboMoveRecord> Moves { get; } = [];
    /// <summary>The cards the step used or moved (<see cref="BoardCard.Key"/>), to highlight on the board.</summary>
    public HashSet<string> Touched { get; } = [];
    public bool IsFlagged => Problems.Count > 0;
}

/// <summary>
/// Plays a combo's steps from the opening hand and keeps the board after each one. The board is always worked out from
/// the steps, so a changed or deleted step changes everything after it. A step that can't be played (its card isn't where
/// it says any more, its zone is full) is flagged with the reason and leaves the board as it was; the rest go on.
/// </summary>
public sealed class ComboReplay
{
    private readonly List<ComboBoard> _boards = [];
    private readonly List<StepOutcome> _steps = [];

    /// <summary>Problems with the deck and the opening hand.</summary>
    public List<string> StartProblems { get; } = [];
    public IReadOnlyList<StepOutcome> Steps => _steps;
    public ComboBoard Start => _boards[0];
    public ComboBoard End => _boards[^1];
    public int FlaggedCount => _steps.Count(s => s.IsFlagged);

    /// <summary>The board after <paramref name="stepsPlayed"/> steps: 0 is the opening hand.</summary>
    public ComboBoard BoardAfter(int stepsPlayed) => _boards[Math.Clamp(stepsPlayed, 0, _boards.Count - 1)];

    public static ComboReplay Run(Combo combo, ComboCatalog? catalog = null)
    {
        var replay = new ComboReplay();
        var runner = new Runner(combo, catalog ?? ComboCatalog.Empty, replay.StartProblems);
        replay._boards.Add(runner.Board.Clone());
        for (int i = 0; i < combo.Steps.Count; i++)
        {
            var outcome = new StepOutcome();
            runner.Apply(combo.Steps[i], outcome);
            replay._steps.Add(outcome);
            replay._boards.Add(runner.Board.Clone());
        }
        return replay;
    }

    private sealed class Runner
    {
        private readonly Combo _combo;
        private readonly ComboCatalog _catalog;
        private readonly HashSet<long> _inDeck = [];
        private readonly Dictionary<long, int> _copies = [];
        private readonly HashSet<BoardCard> _used = [];
        public ComboBoard Board { get; } = new();

        public Runner(Combo combo, ComboCatalog catalog, List<string> problems)
        {
            _combo = combo;
            _catalog = catalog;
            var main = combo.Deck.Main.Select(NewCard).ToList();
            var extra = combo.Deck.Extra.Select(NewCard).ToList();
            Board.ExtraDeck.AddRange(extra);
            if (combo.RandomHand)
            {
                // Practice: a shuffled Deck, the hand off the top. The seed brings the same hand back until it is redrawn.
                var random = new Random(combo.Seed);
                for (int i = main.Count - 1; i > 0; i--)
                {
                    var j = random.Next(i + 1);
                    (main[i], main[j]) = (main[j], main[i]);
                }
                Board.Deck.AddRange(main);
                var n = Math.Min(Math.Max(0, combo.HandSize), Board.Deck.Count);
                Board.Hand.AddRange(Board.Deck.Take(n));
                Board.Deck.RemoveRange(0, n);
                return;
            }
            Board.Deck.AddRange(main);
            foreach (var code in combo.StartingHand)
            {
                var canon = _catalog.Canonical(code);
                var card = Board.Deck.FirstOrDefault(c => c.Code == canon);
                if (card is null)
                {
                    problems.Add(combo.Deck.Main.Any(c => _catalog.Canonical(c) == canon)
                        ? $"The opening hand has more copies of {Name(code)} than the Main Deck."
                        : $"{Name(code)} is in the opening hand but not in the Main Deck.");
                    continue;
                }
                Board.Deck.Remove(card);
                Board.Hand.Add(card);
            }
        }

        private BoardCard NewCard(long code)
        {
            var canon = _catalog.Canonical(code);
            _inDeck.Add(canon);
            var copy = _copies[canon] = _copies.GetValueOrDefault(canon) + 1;
            return new BoardCard(canon, copy);
        }

        private string Name(long code) => _catalog.Name(code, _combo.Names);
        private CardData? Data(long code) => _catalog.Find(code)?.Data;

        public void Apply(ComboStep step, StepOutcome o)
        {
            _used.Clear();
            if (ComboRules.MovesNothing(step.Kind))
            {
                if (step.Code != 0 && step.Kind != ComboStepKind.Interruption && Board.Spots().FirstOrDefault(s => s.Card.Code == _catalog.Canonical(step.Code)) is { } noted)
                    o.Touched.Add(noted.Card.Key);
                return;
            }

            BoardCard card;
            BoardSpot? spot = null;
            if (step.Code == 0 && step.Kind == ComboStepKind.Draw)
            {
                if (Board.Deck.Count == 0) { o.Problems.Add("The Deck is empty: there is nothing to draw."); return; }
                spot = new BoardSpot(Board.Deck[0], ComboZone.Deck, 0);
                card = spot.Card;
            }
            else if (step.Code == 0)
            {
                o.Problems.Add("Pick the card this step uses.");
                return;
            }
            else if (step.From == ComboZone.Token)
            {
                var canon = _catalog.Canonical(step.Code);
                card = new BoardCard(canon, _copies[canon] = _copies.GetValueOrDefault(canon) + 1) { IsToken = true };
            }
            else
            {
                spot = Locate(step.Code, step.Copy, step.From, ComboRules.SearchZones(step.Kind), null, o, "");
                if (spot is null) return;
                card = spot.Card;
            }
            _used.Add(card);
            o.Touched.Add(card.Key);
            var data = Data(card.Code);
            Check(step, data, o);

            if (ComboRules.MaterialsFirst(step.Kind))
            {
                MoveMaterials(step, card, o);
                MoveMain(step, card, spot, data, o);
            }
            else
            {
                MoveMain(step, card, spot, data, o);
                MoveMaterials(step, card, o);
            }
        }

        /// <summary>Flags a step that doesn't fit its card: a Spell Normal Summoned, a Synchro Monster Xyz Summoned.</summary>
        private void Check(ComboStep step, CardData? data, StepOutcome o)
        {
            var name = data is null ? "" : Name(data.Id);
            if (data is not null)
            {
                switch (step.Kind)
                {
                    case ComboStepKind.NormalSummon or ComboStepKind.TributeSummon when !data.IsMonster:
                        o.Problems.Add($"{name} isn't a monster, so it can't be Normal Summoned.");
                        break;
                    case ComboStepKind.NormalSummon or ComboStepKind.TributeSummon or ComboStepKind.Set when data.IsExtraDeck:
                        o.Problems.Add($"{name} is an Extra Deck monster, so it can't be Normal Summoned or Set.");
                        break;
                    case ComboStepKind.ActivateCard when data.IsMonster && !data.IsPendulum:
                        o.Problems.Add($"{name} is a monster: use \"Activate an effect\" for its effects.");
                        break;
                    case ComboStepKind.Attach or ComboStepKind.Detach when !data.IsXyz:
                        o.Problems.Add($"{name} isn't an Xyz Monster, so it has no materials.");
                        break;
                }
                if (ComboRules.FrameFor(step.Kind) is { } frame && (!data.IsMonster || data.Frame != frame))
                    o.Problems.Add($"{name} isn't a {frame} Monster.");
            }
            if (step.Kind is ComboStepKind.SynchroSummon or ComboStepKind.XyzSummon or ComboStepKind.LinkSummon or ComboStepKind.TributeSummon
                && step.Materials.Count == 0)
                o.Problems.Add(step.Kind == ComboStepKind.TributeSummon ? "Pick the monsters it Tributes." : "Pick its materials.");
        }

        private void MoveMain(ComboStep step, BoardCard card, BoardSpot? spot, CardData? data, StepOutcome o)
        {
            var to = step.To == ComboZone.Auto ? ComboRules.DefaultTo(step.Kind, data) : step.To;
            if (step.Kind == ComboStepKind.ChangePosition)
            {
                if (spot is null || !ComboBoard.IsOnField(spot.Zone)) { o.Problems.Add($"{Name(card.Code)} isn't on the field."); return; }
                var was = card.Position;
                card.Position = step.Position != ComboPosition.Auto ? step.Position
                    : spot.Zone is ComboZone.MonsterZone or ComboZone.ExtraMonsterZone
                        ? card.Position is ComboPosition.Attack ? ComboPosition.Defense : ComboPosition.Attack
                        : card.FaceDown ? ComboPosition.FaceUp : ComboPosition.FaceDown;
                if (was != card.Position) o.Moves.Add(new ComboMoveRecord(card.Code, card.Copy, spot.Zone, spot.Zone, ComboText.Position(card.Position)));
                return;
            }
            // Staying put: an effect used where the card is, or a Set card flipped face-up to activate it.
            if (to == ComboZone.Auto || (spot is not null && ComboBoard.IsOnField(spot.Zone) && ComboRules.ZoneMatches(spot.Zone, to) && step.Slot == 0))
            {
                if (spot is not null && step.Kind == ComboStepKind.ActivateCard && card.FaceDown)
                {
                    card.Position = ComboPosition.FaceUp;
                    o.Moves.Add(new ComboMoveRecord(card.Code, card.Copy, spot.Zone, spot.Zone, "flipped face-up"));
                }
                return;
            }
            Move(card, spot, to, step.Slot, step.Position, step.Kind, step.To == ComboZone.Auto, null, o);
        }

        private void MoveMaterials(ComboStep step, BoardCard card, StepOutcome o)
        {
            var materials = step.Materials;
            if (step.Kind == ComboStepKind.Detach && materials.Count == 0)
            {
                // "Detach 1 material": the one at the bottom.
                if (card.Materials.Count == 0) { o.Problems.Add($"{Name(card.Code)} has no materials to detach."); return; }
                var bottom = card.Materials[0];
                materials = [new ComboMove { Code = bottom.Code, Copy = bottom.Copy, From = ComboZone.XyzMaterial }];
            }
            foreach (var m in materials)
            {
                if (m.Code == 0) { o.Problems.Add("A moved card has no card picked."); continue; }
                var spot = Locate(m.Code, m.Copy, m.From, ComboRules.MaterialSearchZones(step.Kind), step.Kind == ComboStepKind.Detach ? card : null, o,
                    step.Kind == ComboStepKind.Detach ? $" under {Name(card.Code)}" : "");
                if (spot is null) continue;
                _used.Add(spot.Card);
                o.Touched.Add(spot.Card.Key);
                var to = m.To == ComboZone.Auto ? ComboRules.MaterialDefaultTo(step.Kind) : m.To;
                Move(spot.Card, spot, to, m.Slot, m.Position, step.Kind, m.To == ComboZone.Auto, card, o);
            }
        }

        /// <summary>
        /// Finds the card: the copy asked for if it is there, otherwise any copy of it, in <paramref name="from"/> (or the
        /// kind's usual zones). Says where it is instead when it isn't there.
        /// </summary>
        private BoardSpot? Locate(long code, int copy, ComboZone from, IReadOnlyList<ComboZone> usual, BoardCard? owner, StepOutcome o, string where)
        {
            var canon = _catalog.Canonical(code);
            var all = Board.Spots().Where(s => s.Card.Code == canon && !_used.Contains(s.Card)).ToList();
            var fits = (from == ComboZone.Auto
                    ? all.Where(s => usual.Any(z => ComboRules.ZoneMatches(s.Zone, z))).OrderBy(s => IndexOf(usual, s.Zone))
                    : all.Where(s => ComboRules.ZoneMatches(s.Zone, from)))
                .Where(s => owner is null || ReferenceEquals(s.Owner, owner))
                .ToList();
            var found = fits.FirstOrDefault(s => copy > 0 && s.Card.Copy == copy) ?? fits.FirstOrDefault();
            if (found is not null) return found;
            var name = Name(code);
            var wanted = from == ComboZone.Auto ? string.Join(" or ", usual.Take(3).Select(ComboText.InZone)) : ComboText.InZone(from);
            if (owner is not null) wanted = "attached" + where;
            o.Problems.Add(all.Count > 0
                ? $"{name} isn't {wanted} at this point: it is {ComboText.InZone(all[0].Zone)}."
                : _inDeck.Contains(canon) || _copies.ContainsKey(canon)
                    ? $"{name} isn't {wanted} at this point: every copy is already used in this step or gone."
                    : $"{name} isn't in this combo's deck.");
            return null;
        }

        private static int IndexOf(IReadOnlyList<ComboZone> zones, ComboZone zone)
        {
            for (int i = 0; i < zones.Count; i++) if (ComboRules.ZoneMatches(zone, zones[i])) return i;
            return zones.Count;
        }

        /// <summary>Moves a card, applying the rules a player would: materials leave with an Xyz Monster, Pendulum Monsters go face-up to the Extra Deck...</summary>
        private void Move(BoardCard card, BoardSpot? from, ComboZone to, int slot, ComboPosition position, ComboStepKind kind, bool autoTo,
            BoardCard? xyzOwner, StepOutcome o)
        {
            var data = Data(card.Code);
            var fromZone = from?.Zone ?? ComboZone.Token;
            var leavesField = from is not null && ComboBoard.IsOnField(fromZone);
            var note = "";
            if (to is ComboZone.Hand or ComboZone.Deck && data?.IsExtraDeck == true && !card.IsToken)
            {
                to = ComboZone.ExtraDeck;
                note = "Extra Deck monsters go back to the Extra Deck";
            }
            if (to == ComboZone.Graveyard && leavesField && data?.IsPendulum == true && fromZone != ComboZone.FieldZone)
            {
                to = ComboZone.ExtraDeck;
                position = ComboPosition.FaceUp;
                note = "Pendulum Monsters go face-up to the Extra Deck instead";
            }

            int index = 0;
            if (ComboBoard.IsOnField(to))
            {
                var picked = PickZone(card, to, slot, autoTo && fromZone == ComboZone.ExtraDeck, data, o);
                if (picked is null) return;
                (to, index) = picked.Value;
            }
            else if (to == ComboZone.XyzMaterial)
            {
                if (xyzOwner is null || ReferenceEquals(xyzOwner, card))
                {
                    o.Problems.Add($"{Name(card.Code)} can't be attached: there is no Xyz Monster to put it under.");
                    return;
                }
                // An Xyz Summon puts its materials under the monster as it arrives; anything else attaches to one on the field.
                if (kind != ComboStepKind.XyzSummon && Board.Find(xyzOwner) is not { Zone: ComboZone.MonsterZone or ComboZone.ExtraMonsterZone })
                {
                    o.Problems.Add($"{Name(xyzOwner.Code)} isn't on the field, so nothing can be attached to it.");
                    return;
                }
            }
            else if (to is ComboZone.Field or ComboZone.Token)
            {
                o.Problems.Add($"Say which zone {Name(card.Code)} goes to.");
                return;
            }

            if (from is not null) Board.Remove(from);
            if (leavesField && card.Materials.Count > 0 && to is not (ComboZone.MonsterZone or ComboZone.ExtraMonsterZone))
            {
                if (to == ComboZone.XyzMaterial && kind == ComboStepKind.XyzSummon)
                    xyzOwner!.Materials.AddRange(card.Materials);   // an Xyz Monster used as material brings its materials along
                else
                    foreach (var m in card.Materials)
                    {
                        Board.Graveyard.Add(m);
                        o.Moves.Add(new ComboMoveRecord(m.Code, m.Copy, ComboZone.XyzMaterial, ComboZone.Graveyard, $"was under {Name(card.Code)} when it left the field"));
                        o.Touched.Add(m.Key);
                    }
                card.Materials.Clear();
            }
            if (card.IsToken && !ComboBoard.IsOnField(to))
            {
                o.Moves.Add(new ComboMoveRecord(card.Code, card.Copy, fromZone, to, "Tokens vanish when they leave the field"));
                return;
            }
            Put(card, to, index, position, kind, o);
            o.Moves.Add(new ComboMoveRecord(card.Code, card.Copy, fromZone, to == ComboZone.XyzMaterial ? ComboZone.XyzMaterial : to, note));
            if (to == ComboZone.XyzMaterial) xyzOwner!.Materials.Add(card);
        }

        private void Put(BoardCard card, ComboZone to, int index, ComboPosition position, ComboStepKind kind, StepOutcome o)
        {
            card.InPendulumZone = false;
            switch (to)
            {
                case ComboZone.MonsterZone or ComboZone.ExtraMonsterZone:
                    card.Position = position is ComboPosition.Attack or ComboPosition.Defense or ComboPosition.FaceDownDefense ? position
                        : kind == ComboStepKind.Set ? ComboPosition.FaceDownDefense : ComboPosition.Attack;
                    Board.Row(to)![index] = card;
                    break;
                case ComboZone.SpellTrapZone or ComboZone.PendulumZone:
                    card.Position = position is ComboPosition.FaceDown or ComboPosition.FaceUp ? position
                        : kind == ComboStepKind.Set ? ComboPosition.FaceDown : ComboPosition.FaceUp;
                    card.InPendulumZone = to == ComboZone.PendulumZone;
                    Board.SpellTrapZones[index] = card;
                    break;
                case ComboZone.FieldZone:
                    if (Board.FieldZone is { } old)
                    {
                        // A new Field Spell sends the old one to the GY.
                        Board.FieldZone = null;
                        Board.Graveyard.Add(old);
                        o.Moves.Add(new ComboMoveRecord(old.Code, old.Copy, ComboZone.FieldZone, ComboZone.Graveyard, "replaced by the new Field Spell"));
                        o.Touched.Add(old.Key);
                    }
                    card.Position = position is ComboPosition.FaceDown || kind == ComboStepKind.Set ? ComboPosition.FaceDown : ComboPosition.FaceUp;
                    Board.FieldZone = card;
                    break;
                case ComboZone.XyzMaterial:
                    card.Position = ComboPosition.Auto;
                    break;
                default:
                    card.Position = to == ComboZone.Banished && position is ComboPosition.FaceDown or ComboPosition.FaceDownDefense ? ComboPosition.FaceDown
                        : to == ComboZone.ExtraDeck && position == ComboPosition.FaceUp ? ComboPosition.FaceUp
                        : ComboPosition.Auto;
                    Board.Pile(to)!.Add(card);
                    break;
            }
        }

        /// <summary>The zone a card goes to on the field, or null (and a problem) when there is no room.</summary>
        private (ComboZone Zone, int Index)? PickZone(BoardCard card, ComboZone to, int slot, bool fromExtraAuto, CardData? data, StepOutcome o)
        {
            var name = Name(card.Code);
            if (to == ComboZone.FieldZone) return (to, 0);
            if (to == ComboZone.MonsterZone && slot == 0 && fromExtraAuto && data is not null
                && (data.IsLink || data.IsPendulum) && Board.ExtraMonsterZones.All(z => z is null))
                return (ComboZone.ExtraMonsterZone, 0);
            int[] order = to switch
            {
                ComboZone.ExtraMonsterZone => [0, 1],
                ComboZone.PendulumZone => [0, ComboBoard.Zones - 1],
                ComboZone.SpellTrapZone => [1, 2, 3, 0, 4],
                _ => [0, 1, 2, 3, 4],
            };
            var row = Board.Row(to)!;
            if (slot > 0)
            {
                var i = to == ComboZone.PendulumZone ? (slot == 1 ? 0 : ComboBoard.Zones - 1) : slot - 1;
                if (i >= 0 && i < row.Length)
                {
                    if (row[i] is null) return (to, i);
                    o.Problems.Add($"{ComboText.Zone(to)} {slot} already holds {Name(row[i]!.Code)}, so {name} went to another one.");
                }
            }
            foreach (var i in order) if (row[i] is null) return (to, i);
            o.Problems.Add(to switch
            {
                ComboZone.MonsterZone => $"There is no free Main Monster Zone for {name}.",
                ComboZone.ExtraMonsterZone => $"There is no free Extra Monster Zone for {name}.",
                ComboZone.PendulumZone => $"Both Pendulum Zones are taken, so {name} can't be placed.",
                _ => $"There is no free Spell & Trap Zone for {name}.",
            });
            return null;
        }
    }
}
