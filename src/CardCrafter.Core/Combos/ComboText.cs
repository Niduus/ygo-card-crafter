using System.Text;

namespace CardCrafter.Core.Combos;

/// <summary>Combos in words: each step as a line ("Normal Summon Snake-Eye Ash"), and a whole combo as text to paste in a chat.</summary>
public static class ComboText
{
    public static string Kind(ComboStepKind kind) => kind switch
    {
        ComboStepKind.NormalSummon => "Normal Summon",
        ComboStepKind.TributeSummon => "Tribute Summon",
        ComboStepKind.Set => "Set",
        ComboStepKind.SpecialSummon => "Special Summon",
        ComboStepKind.FusionSummon => "Fusion Summon",
        ComboStepKind.SynchroSummon => "Synchro Summon",
        ComboStepKind.XyzSummon => "Xyz Summon",
        ComboStepKind.LinkSummon => "Link Summon",
        ComboStepKind.RitualSummon => "Ritual Summon",
        ComboStepKind.ActivateCard => "Activate a card",
        ComboStepKind.ActivateEffect => "Activate an effect",
        ComboStepKind.AddToHand => "Add to hand",
        ComboStepKind.Draw => "Draw",
        ComboStepKind.SendToGraveyard => "Send to the GY",
        ComboStepKind.Banish => "Banish",
        ComboStepKind.ReturnToHand => "Return to hand",
        ComboStepKind.ReturnToDeck => "Return to the Deck",
        ComboStepKind.ReturnToExtraDeck => "Return to the Extra Deck",
        ComboStepKind.Place => "Place on the field",
        ComboStepKind.Attach => "Attach Xyz Material",
        ComboStepKind.Detach => "Detach Xyz Material",
        ComboStepKind.ChangePosition => "Change position",
        ComboStepKind.Interruption => "Opponent's interruption",
        ComboStepKind.EndTurn => "End the turn",
        _ => "Note",
    };

    public static string Zone(ComboZone zone) => zone switch
    {
        ComboZone.Auto => "Auto",
        ComboZone.Hand => "Hand",
        ComboZone.Deck => "Deck",
        ComboZone.ExtraDeck => "Extra Deck",
        ComboZone.MonsterZone => "Monster Zone",
        ComboZone.ExtraMonsterZone => "Extra Monster Zone",
        ComboZone.SpellTrapZone => "Spell & Trap Zone",
        ComboZone.PendulumZone => "Pendulum Zone",
        ComboZone.FieldZone => "Field Zone",
        ComboZone.Graveyard => "GY",
        ComboZone.Banished => "Banishment",
        ComboZone.XyzMaterial => "Xyz Material",
        ComboZone.Field => "Field",
        _ => "Token (made by the effect)",
    };

    /// <summary>"in the hand", "in the GY", "on the field"...</summary>
    public static string InZone(ComboZone zone) => zone switch
    {
        ComboZone.Hand => "in the hand",
        ComboZone.Deck => "in the Deck",
        ComboZone.ExtraDeck => "in the Extra Deck",
        ComboZone.MonsterZone => "in a Monster Zone",
        ComboZone.ExtraMonsterZone => "in an Extra Monster Zone",
        ComboZone.SpellTrapZone => "in a Spell & Trap Zone",
        ComboZone.PendulumZone => "in a Pendulum Zone",
        ComboZone.FieldZone => "in the Field Zone",
        ComboZone.Graveyard => "in the GY",
        ComboZone.Banished => "banished",
        ComboZone.XyzMaterial => "attached to an Xyz Monster",
        ComboZone.Field => "on the field",
        _ => "anywhere",
    };

    private static string FromZone(ComboZone zone) => zone switch
    {
        ComboZone.Hand => "from the hand",
        ComboZone.Deck => "from the Deck",
        ComboZone.ExtraDeck => "from the Extra Deck",
        ComboZone.Graveyard => "from the GY",
        ComboZone.Banished => "from banishment",
        ComboZone.XyzMaterial => "from among Xyz Materials",
        ComboZone.Auto or ComboZone.Token => "",
        _ => "from the field",
    };

    public static string Position(ComboPosition position) => position switch
    {
        ComboPosition.Attack => "Attack Position",
        ComboPosition.Defense => "Defense Position",
        ComboPosition.FaceDownDefense => "face-down Defense Position",
        ComboPosition.FaceUp => "face-up",
        ComboPosition.FaceDown => "face-down",
        _ => "Auto",
    };

    /// <summary>One step as a sentence, e.g. "Xyz Summon Number 39: Utopia, using A and B" or "Activate Pot of Greed's effect 1".</summary>
    public static string Describe(ComboStep step, Func<long, string> name)
    {
        string Card() => step.Code == 0 ? (step.Kind == ComboStepKind.Draw ? "1 card" : "(no card)") : name(step.Code);
        string List(IEnumerable<ComboMove> moves) => Join(moves.Select(m => name(m.Code)).ToList());
        var effect = step.Effect > 0 ? $" (effect {step.Effect})" : "";
        var from = step.From == ComboZone.Auto ? "" : " " + FromZone(step.From);
        var pos = step.Position is ComboPosition.Defense or ComboPosition.FaceDownDefense ? $" in {Position(step.Position)}" : "";
        var mats = step.Materials;
        var text = step.Kind switch
        {
            ComboStepKind.NormalSummon => $"Normal Summon {Card()}{pos}",
            ComboStepKind.TributeSummon => $"Tribute Summon {Card()}" + (mats.Count > 0 ? $" by Tributing {List(mats)}" : "") + pos,
            ComboStepKind.Set => $"Set {Card()}",
            ComboStepKind.SpecialSummon => $"Special Summon {Card()}" + (step.From == ComboZone.Token ? " (a Token)" : from) + pos,
            ComboStepKind.FusionSummon or ComboStepKind.SynchroSummon or ComboStepKind.XyzSummon or ComboStepKind.LinkSummon =>
                $"{Kind(step.Kind)} {Card()}" + (mats.Count > 0 ? $" using {List(mats)}" : "") + pos,
            ComboStepKind.RitualSummon => $"Ritual Summon {Card()}{from}" + (mats.Count > 0 ? $" by Tributing {List(mats)}" : "") + pos,
            ComboStepKind.ActivateCard => $"Activate {Card()}" + (step.From is ComboZone.SpellTrapZone or ComboZone.FieldZone ? " (Set)" : ""),
            ComboStepKind.ActivateEffect => $"Activate the effect of {Card()}" + (step.From == ComboZone.Auto || ComboBoard.IsOnField(step.From) || step.From == ComboZone.Field ? "" : $" ({InZone(step.From)})"),
            ComboStepKind.AddToHand => $"Add {Card()}{from} to the hand",
            ComboStepKind.Draw => $"Draw {Card()}",
            ComboStepKind.SendToGraveyard => $"Send {Card()}{from} to the GY",
            ComboStepKind.Banish => $"Banish {Card()}{from}" + (step.Position == ComboPosition.FaceDown ? " face-down" : ""),
            ComboStepKind.ReturnToHand => $"Return {Card()} to the hand",
            ComboStepKind.ReturnToDeck => $"Shuffle {Card()} into the Deck",
            ComboStepKind.ReturnToExtraDeck => $"Return {Card()} to the Extra Deck",
            ComboStepKind.Place => step.To switch
            {
                ComboZone.PendulumZone => $"Place {Card()} in the Pendulum Zone",
                ComboZone.FieldZone => $"Place {Card()} in the Field Zone",
                ComboZone.SpellTrapZone => $"Place {Card()} face-up in the Spell & Trap Zone",
                _ => $"Place {Card()} on the field",
            },
            ComboStepKind.Attach => $"Attach {(mats.Count > 0 ? List(mats) : "a card")} to {Card()}",
            ComboStepKind.Detach => $"Detach {(mats.Count > 0 ? List(mats) : "1 material")} from {Card()}",
            ComboStepKind.ChangePosition => $"Change {Card()}" + (step.Position == ComboPosition.Auto ? "'s battle position" : $" to {Position(step.Position)}"),
            ComboStepKind.Interruption => $"Opponent activates {Card()}",
            ComboStepKind.EndTurn => "End the turn",
            _ => step.Code != 0 ? $"Note on {Card()}" : "Note",
        };
        text += effect;
        if (!ComboRules.IsSummon(step.Kind) && step.Kind is not (ComboStepKind.Attach or ComboStepKind.Detach) && mats.Count > 0)
            text += ": " + string.Join(", ", mats.Select(m => MoveText(m, name, step.Kind)));
        return text;
    }

    /// <summary>A card a step moves as a phrase: "send Ash Blossom from the hand to the GY".</summary>
    public static string MoveText(ComboMove m, Func<long, string> name, ComboStepKind kind)
    {
        var card = name(m.Code);
        var from = m.From == ComboZone.Auto ? "" : " " + FromZone(m.From);
        var to = m.To == ComboZone.Auto ? ComboRules.MaterialDefaultTo(kind) : m.To;
        return to switch
        {
            ComboZone.Graveyard => $"send {card}{from} to the GY",
            ComboZone.Banished => $"banish {card}{from}" + (m.Position == ComboPosition.FaceDown ? " face-down" : ""),
            ComboZone.Hand => m.From is ComboZone.Deck or ComboZone.Graveyard or ComboZone.Banished or ComboZone.Auto or ComboZone.ExtraDeck
                ? $"add {card}{from} to the hand" : $"return {card} to the hand",
            ComboZone.Deck => $"shuffle {card}{from} into the Deck",
            ComboZone.ExtraDeck => $"return {card}{from} to the Extra Deck",
            ComboZone.XyzMaterial => $"attach {card}{from}",
            ComboZone.MonsterZone or ComboZone.ExtraMonsterZone => $"Special Summon {card}{from}",
            ComboZone.PendulumZone => $"place {card}{from} in the Pendulum Zone",
            ComboZone.FieldZone => $"place {card}{from} in the Field Zone",
            _ => m.Position == ComboPosition.FaceDown ? $"Set {card}{from}" : $"place {card}{from} in the Spell & Trap Zone",
        };
    }

    /// <summary>A record of a replayed move: "Snake-Eye Oak: Deck → hand".</summary>
    public static string Moved(ComboMoveRecord m, Func<long, string> name) =>
        $"{name(m.Code)}: {Zone(m.From)} → {(m.From == m.To ? "stays" : Zone(m.To))}" + (m.Note.Length > 0 ? $" ({m.Note})" : "");

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    /// <summary>
    /// The whole combo as plain text to paste into Discord: the name, notes, opening hand, numbered steps with their notes,
    /// and the end board.
    /// </summary>
    public static string ToText(Combo combo, ComboCatalog? catalog = null)
    {
        catalog ??= ComboCatalog.Empty;
        string Name(long id) => catalog.Name(id, combo.Names);
        var replay = ComboReplay.Run(combo, catalog);
        var sb = new StringBuilder();
        sb.AppendLine($"**{combo.Name}**");
        if (combo.Notes.Trim().Length > 0) sb.AppendLine(combo.Notes.Trim());
        sb.AppendLine();
        var hand = replay.Start.Hand.Select(c => Name(c.Code)).ToList();
        sb.AppendLine((combo.RandomHand ? "Opening hand (drawn at random): " : "Opening hand: ") + (hand.Count > 0 ? string.Join(", ", hand) : "(none)"));
        sb.AppendLine();
        for (int i = 0; i < combo.Steps.Count; i++)
        {
            var step = combo.Steps[i];
            sb.AppendLine($"{i + 1}. {Describe(step, Name)}");
            if (step.Note.Trim().Length > 0)
                foreach (var line in step.Note.Trim().Split('\n')) sb.AppendLine($"   {line.TrimEnd()}");
        }
        var end = EndBoard(replay.End, Name);
        if (end.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("End board:");
            sb.Append(end);
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>The cards on the field, in hand and in the GY at the end, a line each.</summary>
    public static string EndBoard(ComboBoard board, Func<long, string> name)
    {
        var sb = new StringBuilder();
        var field = board.FieldSpots().Select(s =>
        {
            var line = name(s.Card.Code);
            if (s.Card.FaceDown) line += " (Set)";
            else if (s.Card.Position == ComboPosition.Defense) line += " (Defense)";
            if (s.Card.Materials.Count > 0) line += $" with {s.Card.Materials.Count} material{(s.Card.Materials.Count == 1 ? "" : "s")}";
            return line;
        }).ToList();
        if (field.Count > 0) sb.AppendLine("- Field: " + string.Join(", ", field));
        if (board.Hand.Count > 0) sb.AppendLine("- Hand: " + string.Join(", ", board.Hand.Select(c => name(c.Code))));
        if (board.Graveyard.Count > 0) sb.AppendLine("- GY: " + string.Join(", ", board.Graveyard.Select(c => name(c.Code))));
        if (board.Banished.Count > 0) sb.AppendLine("- Banished: " + string.Join(", ", board.Banished.Select(c => name(c.Code))));
        return sb.ToString();
    }
}
