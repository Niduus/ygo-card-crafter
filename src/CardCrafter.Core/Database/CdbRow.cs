namespace CardCrafter.Core.Database;

/// <summary>One card exactly as stored in a ygopro-format card database (the datas and texts tables).</summary>
public sealed class CdbRow
{
    public long Id { get; set; }
    public long Ot { get; set; }
    public long Alias { get; set; }
    public long Setcode { get; set; }
    public long Type { get; set; }
    public long Atk { get; set; }
    public long Def { get; set; }
    public long Level { get; set; }
    public long Race { get; set; }
    public long Attribute { get; set; }
    public long Category { get; set; }
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    /// <summary>The 16 effect description strings (str1..str16).</summary>
    public string[] Strings { get; set; } = Enumerable.Repeat("", 16).ToArray();
}

/// <summary>Card type bits from the engine.</summary>
public static class TypeBits
{
    public const long Monster = 0x1;
    public const long Spell = 0x2;
    public const long Trap = 0x4;
    public const long Normal = 0x10;
    public const long Effect = 0x20;
    public const long Fusion = 0x40;
    public const long Ritual = 0x80;
    public const long Spirit = 0x200;
    public const long Union = 0x400;
    public const long Gemini = 0x800;
    public const long Tuner = 0x1000;
    public const long Synchro = 0x2000;
    public const long QuickPlay = 0x10000;
    public const long Continuous = 0x20000;
    public const long Equip = 0x40000;
    public const long Field = 0x80000;
    public const long Counter = 0x100000;
    public const long Flip = 0x200000;
    public const long Toon = 0x400000;
    public const long Xyz = 0x800000;
    public const long Pendulum = 0x1000000;
    public const long SpecialSummon = 0x2000000;
    public const long Link = 0x4000000;
}
