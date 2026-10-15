using CardCrafter.Core.Model;

namespace CardCrafter.Core.Text;

/// <summary>Text pieces printed on a card face, worded the way real cards print them.</summary>
public static class CardFaceText
{
    /// <summary>The bold line above the card text, e.g. "[Dragon/Synchro/Tuner/Effect]".</summary>
    public static string TypeLine(CardData card, CardPack? pack)
    {
        if (!card.IsMonster) return "";
        var parts = new List<string> { pack?.TypeName(card.Race) ?? EnumNames.Display(card.Race) };
        switch (card.Frame)
        {
            case MonsterFrame.Ritual: parts.Add("Ritual"); break;
            case MonsterFrame.Fusion: parts.Add("Fusion"); break;
            case MonsterFrame.Synchro: parts.Add("Synchro"); break;
            case MonsterFrame.Xyz: parts.Add("Xyz"); break;
            case MonsterFrame.Link: parts.Add("Link"); break;
        }
        var a = card.Abilities;
        if (a.HasFlag(MonsterAbility.Pendulum)) parts.Add("Pendulum");
        if (a.HasFlag(MonsterAbility.Flip)) parts.Add("Flip");
        if (a.HasFlag(MonsterAbility.Gemini)) parts.Add("Gemini");
        if (a.HasFlag(MonsterAbility.Spirit)) parts.Add("Spirit");
        if (a.HasFlag(MonsterAbility.Toon)) parts.Add("Toon");
        if (a.HasFlag(MonsterAbility.Union)) parts.Add("Union");
        if (a.HasFlag(MonsterAbility.Tuner)) parts.Add("Tuner");
        if (card.Frame == MonsterFrame.Normal) { if (parts.Count > 1) parts.Add("Normal"); }
        else parts.Add("Effect");
        return "[" + string.Join("/", parts) + "]";
    }

    /// <summary>The line in the Spell/Trap slot, e.g. "[Spell Card]" plus the kind, e.g. "Quick-Play".</summary>
    public static (string Label, string Kind) SpellTrapLine(CardData card) => card.Kind switch
    {
        CardKind.Spell => ("Spell Card", card.SpellKind switch
        {
            SpellKind.QuickPlay => "Quick-Play",
            SpellKind.Continuous => "Continuous",
            SpellKind.Equip => "Equip",
            SpellKind.Field => "Field",
            SpellKind.Ritual => "Ritual",
            _ => "",
        }),
        CardKind.Trap => ("Trap Card", card.TrapKind switch
        {
            TrapKind.Continuous => "Continuous",
            TrapKind.Counter => "Counter",
            _ => "",
        }),
        _ => ("", ""),
    };

    public static string Stat(int v) => v == -2 ? "?" : v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
