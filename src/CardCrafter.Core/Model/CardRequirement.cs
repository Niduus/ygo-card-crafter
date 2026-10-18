namespace CardCrafter.Core.Model;

/// <summary>How a requirement compares a card's property with its value(s).</summary>
public enum RequirementOp
{
    /// <summary>Numbers.</summary>
    Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual, Between, Outside,
    /// <summary>Choices (Attribute, Type, Summon...): one of the picked values, or none of them. States: it is so, or not.</summary>
    Is, IsNot,
}

/// <summary>
/// One extra requirement a card must meet, on top of a card filter's own fields: a property from
/// <see cref="Compiler.CardProperties"/>, how it is compared, and the value(s): "original ATK 2000 or more", "not DARK",
/// "Level between 3 and 5", "its effects are negated". All of a filter's requirements must hold for the same card.
/// </summary>
public sealed class CardRequirement
{
    public string Property { get; set; } = "atk";
    public RequirementOp Op { get; set; } = RequirementOp.GreaterOrEqual;
    /// <summary>The number, or the low end of a range.</summary>
    public int Value { get; set; }
    /// <summary>The high end of a range (Between, Outside).</summary>
    public int Value2 { get; set; }
    /// <summary>The picked choices: Attribute or Type bits, archetype codes, passcodes, Summon kinds...</summary>
    public List<long> Values { get; set; } = [];
    /// <summary>
    /// An "or" group, 0 for none: requirements with the same group number need only one of them to hold ("Level 4 or
    /// lower, or a Tuner"), while each group, and every requirement in none, must hold.
    /// </summary>
    public int Group { get; set; }
    /// <summary>
    /// The other cards it is compared with, for requirements about them ("in the same column as the "Elfnote" monster(s) you
    /// control"); null for the others.
    /// </summary>
    public CardFilter? Cards { get; set; }

    public CardRequirement Clone() => new() { Property = Property, Op = Op, Value = Value, Value2 = Value2, Values = [.. Values], Group = Group, Cards = Cards?.Clone() };
}
