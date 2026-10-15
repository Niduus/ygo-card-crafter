using CardCrafter.Core.Model;
using static CardCrafter.Core.Database.TypeBits;

namespace CardCrafter.Core.Database;

/// <summary>Converts between the friendly card model and the packed database columns.</summary>
public static class CdbEncoder
{
    public static CdbRow Encode(CardData card, IReadOnlyList<string> strings, long ot)
    {
        var row = new CdbRow
        {
            Id = card.Id,
            Ot = card.Ot ?? ot,
            Alias = card.Alias,
            Setcode = PackSetcodes(card.Archetypes),
            Type = TypeOf(card) | card.ExtraTypeBits,
            Category = card.Category,
            Name = card.Name,
            Desc = Description(card),
        };
        for (int i = 0; i < 16; i++) row.Strings[i] = i < strings.Count ? strings[i] : "";

        // YGO Omega's columns. A card in the OCG or TCG pool needs a release date there to be listed.
        row.OmegaFlags = card.OmegaFlags;
        row.Support = card.Support;
        row.OcgDate = card.OcgDate ?? ((row.Ot & (long)CardPool.Ocg) != 0 && row.Ot < (long)CardPool.Custom ? DefaultReleaseDate : CdbRow.Unreleased);
        row.TcgDate = card.TcgDate ?? ((row.Ot & (long)CardPool.Tcg) != 0 && row.Ot < (long)CardPool.Custom ? DefaultReleaseDate : CdbRow.Unreleased);
        row.Script = card.DatabaseScript;
        // Limits read from a database are written back as they were; a Limitation set in the editor applies to every banlist.
        bool asRead = card.BanlistLimits.Count > 0 && (card.Limitation == Limitation.Unlimited || card.BanlistLimits.Values.All(v => v == (int)card.Limitation));
        if (asRead) foreach (var (banlist, limit) in card.BanlistLimits) row.Limits[banlist] = limit;
        else if (card.Limitation != Limitation.Unlimited) row.Limits[CdbFile.AllBanlists] = (int)card.Limitation;

        if (card.IsMonster)
        {
            row.Atk = card.Atk;
            row.Race = (long)card.Race;
            row.Attribute = (long)card.Attribute;
            if (card.IsLink)
            {
                row.Level = card.LinkRating;
                row.Def = (long)card.LinkMarkers;
            }
            else
            {
                row.Def = card.Def;
                row.Level = card.Level;
                if (card.IsPendulum)
                    row.Level |= ((long)(card.LeftScale & 0xff) << 24) | ((long)(card.RightScale & 0xff) << 16);
            }
        }
        return row;
    }

    /// <summary>2000-01-01, used as the release date of custom cards placed in the OCG or TCG pool.</summary>
    public const long DefaultReleaseDate = 946684800;

    public static long TypeOf(CardData card)
    {
        switch (card.Kind)
        {
            case CardKind.Spell:
                return Spell | card.SpellKind switch
                {
                    SpellKind.QuickPlay => QuickPlay,
                    SpellKind.Continuous => Continuous,
                    SpellKind.Equip => Equip,
                    SpellKind.Field => Field,
                    SpellKind.Ritual => Ritual,
                    _ => 0,
                };
            case CardKind.Trap:
                return Trap | card.TrapKind switch
                {
                    TrapKind.Continuous => Continuous,
                    TrapKind.Counter => Counter,
                    _ => 0,
                };
        }

        long t = Monster | card.Frame switch
        {
            MonsterFrame.Normal => Normal,
            MonsterFrame.Effect => Effect,
            MonsterFrame.Ritual => Ritual | Effect,
            MonsterFrame.Fusion => Fusion | Effect,
            MonsterFrame.Synchro => Synchro | Effect,
            MonsterFrame.Xyz => Xyz | Effect,
            MonsterFrame.Link => Link | Effect,
            _ => 0,
        };
        var a = card.Abilities;
        if (a.HasFlag(MonsterAbility.Tuner)) t |= Tuner;
        if (a.HasFlag(MonsterAbility.Flip)) t |= Flip | Effect;
        if (a.HasFlag(MonsterAbility.Spirit)) t |= Spirit | Effect;
        if (a.HasFlag(MonsterAbility.Toon)) t |= Toon | Effect;
        if (a.HasFlag(MonsterAbility.Union)) t |= Union | Effect;
        if (a.HasFlag(MonsterAbility.Gemini)) t |= Gemini | Effect;
        if (a.HasFlag(MonsterAbility.Pendulum)) t |= Pendulum;
        if (a.HasFlag(MonsterAbility.SpecialSummonOnly)) t |= SpecialSummon | Effect;
        // A plain Normal monster with an ability like Tuner or Pendulum stays Normal.
        if ((t & Normal) != 0) t &= ~Effect;
        return t;
    }

    public static string Description(CardData card)
    {
        if (!card.IsPendulum || string.IsNullOrWhiteSpace(card.PendulumText)) return card.Text;
        return $"←{card.LeftScale} 【Pendulum Scale】 {card.RightScale}→\n{card.PendulumText.Trim()}\n" +
               $"【{(card.Frame == MonsterFrame.Normal ? "Flavor Text" : "Monster Effect")}】\n{card.Text.Trim()}";
    }

    public static long PackSetcodes(IReadOnlyList<int> codes)
    {
        long packed = 0;
        for (int i = 0; i < Math.Min(4, codes.Count); i++) packed |= (long)(codes[i] & 0xffff) << (16 * i);
        return packed;
    }

    public static List<int> UnpackSetcodes(long packed)
    {
        var list = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            var code = (int)((packed >> (16 * i)) & 0xffff);
            if (code != 0) list.Add(code);
        }
        return list;
    }

    /// <summary>Rebuilds a card model from a database row, e.g. to import cards from an existing .cdb.</summary>
    public static CardData Decode(CdbRow row)
    {
        var card = DecodeKnown(row);
        card.Category = row.Category;
        card.Ot = row.Ot;
        card.OmegaFlags = row.OmegaFlags;
        card.Support = row.Support;
        card.OcgDate = row.OcgDate;
        card.TcgDate = row.TcgDate;
        card.DatabaseScript = row.Script;
        card.BanlistLimits = new Dictionary<long, int>(row.Limits);
        // The same limit on every banlist reads as one Limitation; mixed limits stay per banlist.
        if (row.Limits.Count > 0 && row.Limits.Values.Distinct().Count() == 1 && Enum.IsDefined((Limitation)row.Limits.Values.First()))
            card.Limitation = (Limitation)row.Limits.Values.First();
        // Whatever type bits the editor can't show are kept as they are.
        card.ExtraTypeBits = row.Type & ~TypeOf(card);
        return card;
    }

    private static CardData DecodeKnown(CdbRow row)
    {
        var card = new CardData
        {
            Id = row.Id,
            Alias = row.Alias,
            Name = row.Name,
            Text = row.Desc,
            Archetypes = UnpackSetcodes(row.Setcode),
        };
        var t = row.Type;
        if ((t & Spell) != 0)
        {
            card.Kind = CardKind.Spell;
            card.SpellKind = (t & QuickPlay) != 0 ? SpellKind.QuickPlay
                : (t & Continuous) != 0 ? SpellKind.Continuous
                : (t & Equip) != 0 ? SpellKind.Equip
                : (t & Field) != 0 ? SpellKind.Field
                : (t & Ritual) != 0 ? SpellKind.Ritual
                : SpellKind.Normal;
            return card;
        }
        if ((t & Trap) != 0)
        {
            card.Kind = CardKind.Trap;
            card.TrapKind = (t & Continuous) != 0 ? TrapKind.Continuous : (t & Counter) != 0 ? TrapKind.Counter : TrapKind.Normal;
            return card;
        }

        card.Kind = CardKind.Monster;
        card.Frame = (t & Link) != 0 ? MonsterFrame.Link
            : (t & Xyz) != 0 ? MonsterFrame.Xyz
            : (t & Synchro) != 0 ? MonsterFrame.Synchro
            : (t & Fusion) != 0 ? MonsterFrame.Fusion
            : (t & Ritual) != 0 ? MonsterFrame.Ritual
            : (t & Normal) != 0 ? MonsterFrame.Normal
            : MonsterFrame.Effect;
        var a = MonsterAbility.None;
        if ((t & Tuner) != 0) a |= MonsterAbility.Tuner;
        if ((t & Flip) != 0) a |= MonsterAbility.Flip;
        if ((t & Spirit) != 0) a |= MonsterAbility.Spirit;
        if ((t & Toon) != 0) a |= MonsterAbility.Toon;
        if ((t & Union) != 0) a |= MonsterAbility.Union;
        if ((t & Gemini) != 0) a |= MonsterAbility.Gemini;
        if ((t & Pendulum) != 0) a |= MonsterAbility.Pendulum;
        if ((t & SpecialSummon) != 0) a |= MonsterAbility.SpecialSummonOnly;
        card.Abilities = a;
        card.Atk = (int)row.Atk;
        card.Race = (CardRace)row.Race;
        card.Attribute = (CardAttribute)row.Attribute;
        if (card.Frame == MonsterFrame.Link)
        {
            card.LinkMarkers = (LinkMarker)row.Def;
            card.Level = (int)(row.Level & 0xff);
        }
        else
        {
            card.Def = (int)row.Def;
            card.Level = (int)(row.Level & 0xff);
            card.LeftScale = (int)((row.Level >> 24) & 0xff);
            card.RightScale = (int)((row.Level >> 16) & 0xff);
        }
        return card;
    }
}
