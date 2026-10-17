using CardCrafter.Core.Compiler;
using CardCrafter.Core.Model;

namespace CardCrafter.App.ViewModels;

/// <summary>
/// Contact Fusion on the Card tab: "Must first be Special Summoned (from your Extra Deck) by shuffling the above cards
/// you control into the Deck. (You do not use "Polymerization".)"
/// </summary>
public sealed partial class CardViewModel
{
    private SummonProcedure Proc => Project.Procedure;

    public bool Contact
    {
        get => Proc.Contact;
        set { if (Proc.Contact == value) return; Proc.Contact = value; ContactChanged(nameof(Contact)); }
    }

    public IReadOnlyList<Option<ContactFusionMove>> ContactMoveOptions { get; } = Options.Of(
        (ContactFusionMove.ShuffleIntoDeck, "shuffling them into the Deck"),
        (ContactFusionMove.Banish, "banishing them"),
        (ContactFusionMove.SendToGrave, "sending them to the GY"),
        (ContactFusionMove.Tribute, "Tributing them"));

    public Option<ContactFusionMove> ContactMoveChoice
    {
        get => Options.Find(ContactMoveOptions, Proc.ContactMove);
        set { if (value is null || value.Value == Proc.ContactMove) return; Proc.ContactMove = value.Value; ContactChanged(nameof(ContactMoveChoice)); }
    }

    private bool ContactZone(Zone z) => (Proc.ContactZones & z) != Zone.None;
    private void SetContactZone(Zone z, bool on, string name)
    {
        if (ContactZone(z) == on) return;
        Proc.ContactZones = (on ? Proc.ContactZones | z : Proc.ContactZones & ~z) | Zone.MonsterZone;
        ContactChanged(name);
    }

    /// <summary>The whole field, so Spell & Trap Zones too ("the above cards you control", as Neos's Contact Fusions say it).</summary>
    public bool ContactWholeField { get => ContactZone(Zone.SpellTrapZone); set => SetContactZone(Zone.SpellTrapZone, value, nameof(ContactWholeField)); }
    public bool ContactFromHand { get => ContactZone(Zone.Hand); set => SetContactZone(Zone.Hand, value, nameof(ContactFromHand)); }
    public bool ContactFromGrave { get => ContactZone(Zone.Graveyard); set => SetContactZone(Zone.Graveyard, value, nameof(ContactFromGrave)); }

    public bool ContactOpponent
    {
        get => Proc.ContactOpponent;
        set { if (Proc.ContactOpponent == value) return; Proc.ContactOpponent = value; ContactChanged(nameof(ContactOpponent)); }
    }

    /// <summary>Lava Golem, Steelcage of the Sun God: Summoned to the opponent's Main Monster Zone.</summary>
    public bool ContactToOpponent
    {
        get => Proc.ContactToOpponent;
        set { if (Proc.ContactToOpponent == value) return; Proc.ContactToOpponent = value; ContactChanged(nameof(ContactToOpponent)); }
    }

    /// <summary>"..., but if you Special Summon this card with the second method, you can only Special Summon once for the rest of this turn."</summary>
    public bool ContactOneMoreSummon
    {
        get => Proc.ContactOneMoreSummon;
        set { if (Proc.ContactOneMoreSummon == value) return; Proc.ContactOneMoreSummon = value; ContactChanged(nameof(ContactOneMoreSummon)); }
    }

    /// <summary>"You can only Special Summon "X" once per turn this way, no matter which method you use."</summary>
    public bool ContactOncePerTurn
    {
        get => Proc.ContactOncePerTurn;
        set { if (Proc.ContactOncePerTurn == value) return; Proc.ContactOncePerTurn = value; ContactChanged(nameof(ContactOncePerTurn)); }
    }

    /// <summary>The four rules official Contact Fusion Monsters have; each is a summoning rule of the card.</summary>
    public IReadOnlyList<Option<string>> ContactRuleOptions { get; } = Options.Of(
        ("first_or_fusion", "Must first be either Fusion Summoned, or Special Summoned this way"),
        ("first", "Must first be Special Summoned this way (it can't be Fusion Summoned)"),
        ("only_or_fusion", "Must be either Fusion Summoned, or Special Summoned this way (never revived)"),
        ("only", "Must be Special Summoned this way, and only this way"));

    public Option<string> ContactRuleChoice
    {
        get => Options.Find(ContactRuleOptions, C.CannotBeSpecialSummoned ? "only" : C.SummonRule switch
        {
            SummonRule.SpecialSummonOnly => "first",
            SummonRule.ProcedureOnly => "only_or_fusion",
            _ => "first_or_fusion",
        });
        set
        {
            if (value is null) return;
            (C.SummonRule, C.CannotBeSpecialSummoned) = value.Value switch
            {
                "first" => (SummonRule.SpecialSummonOnly, false),
                "only_or_fusion" => (SummonRule.ProcedureOnly, false),
                "only" => (SummonRule.Normal, true),
                _ => (SummonRule.Normal, false),
            };
            OnPropertyChanged(nameof(SummonRuleChoice));
            OnPropertyChanged(nameof(CannotBeSpecialSummoned));
            ContactChanged(nameof(ContactRuleChoice));
        }
    }

    /// <summary>The rule as the card text will print it.</summary>
    public string ContactText => ContactFusion.Text(Project);

    private void ContactChanged(string name)
    {
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(ContactText));
        Changed();
    }
}
