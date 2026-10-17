using System.Text;
using CardCrafter.Core.Blocks;
using CardCrafter.Core.Templates;

var sb = new StringBuilder();
sb.AppendLine("# Block reference");
sb.AppendLine();
sb.AppendLine("Every block you can place on the event sheet. Words in `{braces}` are blanks you fill in on the sheet.");
sb.AppendLine("Blocks that pick cards open a card picker: whose cards, which zones, card type, monster Type, Attribute, archetype,");
sb.AppendLine("Level, ATK, face-up, \"other than this card\", and \"only cards this card points to\" for Link monsters. Requirements test any");
sb.AppendLine("property (\"whose ATK equals its own DEF\", \"that cannot be Normal Summoned/Set\"); requirements with the same \"or\" group need only one");
sb.AppendLine("of them. \"Or another kind of card\" lets a second kind count too (\"1 \"Ancient Gear\" monster or 1 \"Geartown\"\"), and a second");
sb.AppendLine("Spell/Trap kind gives \"a Normal or Quick-Play Spell\". Kinds of cards can also be left out (\"except ... Monster Cards\").");
sb.AppendLine();
foreach (var (kind, title, intro) in new[]
{
    (BlockKind.Trigger, "WHEN: what starts the effect", "Each effect has exactly one."),
    (BlockKind.Condition, "IF: what must be true", "All of them must be true for the effect to be used, except that one ticked \"or the one above\" is enough in place of the one above it."),
    (BlockKind.Cost, "COST: paid when activating", "Paid before the effect resolves. Lasting effects have no cost."),
    (BlockKind.Action, "DO: what happens", "Run in order. Each action after the first says how it links to the one before: *and*, *and if you do*, *then*, or *also, after that*."),
    (BlockKind.Passive, "ALWAYS: lasting effects", "Used with the \"While this card is face-up\" trigger. They apply all the time instead of activating."),
})
{
    sb.AppendLine($"## {title}").AppendLine().AppendLine(intro).AppendLine();
    sb.AppendLine("| Block | Reads as | Options |").AppendLine("|---|---|---|");
    foreach (var b in BlockRegistry.Offered(kind))
    {
        var opts = string.Join("; ", b.Params.Select(p => p.Type switch
        {
            // The counters are listed once, below.
            ParamType.Choice when p.Key.StartsWith(Counters.KindKey, StringComparison.Ordinal) =>
                $"**{p.Label}**: {(p.Choices.Any(c => c.Value == Counters.Any) ? "any counter, " : "")}one of the [counters](#counters), or another by its id",
            ParamType.Choice => $"**{p.Label}**: {string.Join(", ", p.Choices.Select(c => c.Display))}",
            ParamType.Integer => $"**{p.Label}**: {p.Min}–{p.Max}",
            ParamType.Bool => $"**{p.Label}** on/off",
            _ => $"**{p.Label}**: card picker",
        }));
        var name = b.Help.Length > 0 ? $"{b.Name}<br><sub>{b.Help}</sub>" : b.Name;
        sb.AppendLine($"| {name.Replace("|", "\\|")} | {b.Sentence} | {opts.Replace("|", "\\|")} |");
    }
    sb.AppendLine();
}
sb.AppendLine("## Counters").AppendLine();
sb.AppendLine("The counters the counter blocks name, with their engine ids. Any other counter can be picked by its id (in hexadecimal);");
sb.AppendLine("ids with 0x1000 go on any card, the others only on cards that can hold them (a card whose own blocks use a counter can).").AppendLine();
sb.AppendLine(string.Join(", ", Counters.Known.Select(k => $"{k.Name} ({Counters.Hex(k.Id)})")) + ".").AppendLine();
sb.AppendLine("## Templates").AppendLine();
sb.AppendLine("Ready-made effects modelled on well-known cards. They are inserted as normal blocks you can change.").AppendLine();
sb.AppendLine("| Template | Like | For | What it does |").AppendLine("|---|---|---|---|");
foreach (var t in EffectTemplates.All) sb.AppendLine($"| {t.Name} | {t.LikeCard} | {t.For} | {t.Description} |");
File.WriteAllText(args[0], sb.ToString());
Console.WriteLine($"wrote {BlockRegistry.All.Count} blocks and {EffectTemplates.All.Count} templates");
