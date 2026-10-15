using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace CardCrafter.App.Services;

/// <summary>
/// The fonts the card face is printed in. Real cards use Matrix Small Caps for names, ITC Stone Serif Small Caps
/// Bold for the type line and ATK/DEF, Matrix Book for card text and Stone Serif Italic for flavor text. Those are
/// commercial fonts Card Crafter can't include, so it uses them when they are installed or sit in the texture
/// folder, and otherwise free look-alikes bundled with the app (Spectral SC and Crimson Text).
/// </summary>
public static class CardFonts
{
    public static readonly string[] Extensions = [".ttf", ".otf"];

    private static readonly FontFamily BundledCaps = new("avares://YGOCardCrafter/Assets/Fonts#Spectral SC");
    private static readonly FontFamily BundledBody = new("avares://YGOCardCrafter/Assets/Fonts#Crimson Text");

    public static Typeface Name { get; private set; } = new(BundledCaps, FontStyle.Normal, FontWeight.SemiBold);
    public static Typeface TypeLine { get; private set; } = new(BundledCaps, FontStyle.Normal, FontWeight.Bold);
    /// <summary>ATK/DEF and Pendulum scales. Needs full-height digits.</summary>
    public static Typeface Stats { get; private set; } = new(BundledBody, FontStyle.Normal, FontWeight.Bold);
    public static Typeface Body { get; private set; } = new(BundledBody);
    public static Typeface Flavor { get; private set; } = new(BundledBody, FontStyle.Italic);
    public static Typeface Footer { get; private set; } = new(BundledBody);

    /// <summary>Which font each part uses, for the Frames menu.</summary>
    public static string Summary { get; private set; } = "Fonts: bundled look-alikes";

    private static FolderFontCollection? _collection;
    private static int _generation;

    /// <summary>Registers the fonts in <paramref name="folder"/> (and its subfolders), then picks a font for each part of the card.</summary>
    public static void Load(string? folder) => Load(folder is null ? [] : [folder]);

    public static void Load(IReadOnlyList<string> folders)
    {
        var families = new List<(string Name, FontFamily Family)>();
        try
        {
            if (_collection is not null) FontManager.Current.RemoveFontCollection(_collection.Key);
            _collection = null;
            var files = folders.Where(Directory.Exists)
                .SelectMany(f => Directory.EnumerateFiles(f, "*.*", SearchOption.AllDirectories))
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Distinct().ToList();
            if (files.Count > 0)
            {
                var key = new Uri($"fonts:cardcrafter{++_generation}", UriKind.Absolute);
                _collection = new FolderFontCollection(key, files);
                FontManager.Current.AddFontCollection(_collection);
                families.AddRange(_collection.Select(f => (f.Name, new FontFamily($"{key}#{f.Name}"))));
            }
            families.AddRange(FontManager.Current.SystemFonts.Select(f => (f.Name, f)));
        }
        catch (Exception)
        {
            // No font system (some test hosts): keep the bundled fonts.
        }

        var used = new List<string>();
        FontFamily? Pick(string part, Func<string, bool> match)
        {
            var hit = families.FirstOrDefault(f => match(Normal(f.Name)));
            if (hit.Family is not null) used.Add($"{part}: {hit.Name}");
            return hit.Family;
        }

        static bool SmallCaps(string n) => n.Contains("smallcaps") || n.Contains("smcap") || n.Contains("smcp") || n.EndsWith("sc", StringComparison.Ordinal);
        var name = Pick("names", n => n.Contains("matrix") && SmallCaps(n));
        var type = Pick("type line", n => n.Contains("stoneserif") && SmallCaps(n));
        var body = Pick("card text", n => n.Contains("matrix") && n.Contains("book"));
        var flavor = Pick("flavor text", n => n.Contains("stoneserif") && n.Contains("italic"));
        var footer = families.FirstOrDefault(f => Normal(f.Name).Contains("stoneserif") && !SmallCaps(Normal(f.Name)) && !Normal(f.Name).Contains("italic")).Family;

        Name = name is null ? new(BundledCaps, FontStyle.Normal, FontWeight.SemiBold) : new(name);
        TypeLine = type is null ? new(BundledCaps, FontStyle.Normal, FontWeight.Bold) : new(type, FontStyle.Normal, FontWeight.Bold);
        Stats = type is null ? new(BundledBody, FontStyle.Normal, FontWeight.Bold) : new(type, FontStyle.Normal, FontWeight.Bold);
        Body = body is null ? new(BundledBody) : new(body);
        Flavor = flavor is not null ? new(flavor, FontStyle.Italic) : footer is not null ? new(footer, FontStyle.Italic) : new(BundledBody, FontStyle.Italic);
        Footer = footer is not null ? new(footer) : type is not null ? new(type) : new(BundledBody);
        Summary = used.Count == 0 ? "Fonts: bundled look-alikes (install the card fonts or add them to the texture folder)" : "Fonts: " + string.Join(", ", used);
    }

    /// <summary>Lower case, no spaces or punctuation, without "Yu-Gi-Oh!": "Yu-Gi-Oh! Matrix Book" becomes "matrixbook".</summary>
    private static string Normal(string name) =>
        new string(name.ToLowerInvariant().Replace("yu-gi-oh!", "").Replace("yugioh", "").Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Font files from a folder, registered with Avalonia so card faces can use them by family name.</summary>
    private sealed class FolderFontCollection(Uri key, IReadOnlyList<string> files) : FontCollectionBase
    {
        private readonly List<FontFamily> _families = [];

        public override Uri Key => key;
        public override int Count => _families.Count;
        public override FontFamily this[int index] => _families[index];

        public override void Initialize(IFontManagerImpl fontManager)
        {
            foreach (var file in files)
            {
                try
                {
                    // The font manager may read the stream later, so it keeps its own copy in memory.
                    var stream = new MemoryStream(File.ReadAllBytes(file));
                    if (CreateFace(fontManager, stream) is not { } face) continue;
                    var faces = _glyphTypefaceCache.GetOrAdd(face.FamilyName, _ => new ConcurrentDictionary<FontCollectionKey, IGlyphTypeface?>());
                    faces.TryAdd(new FontCollectionKey(face.Style, face.Weight, face.Stretch), face);
                    if (_families.All(f => f.Name != face.FamilyName)) _families.Add(new FontFamily(key, face.FamilyName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    // A broken font file is skipped.
                }
            }
        }

        public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
        {
            glyphTypeface = null;
            if (!_glyphTypefaceCache.TryGetValue(familyName, out var faces) || faces.IsEmpty) return false;
            // The same style at the nearest weight, else the nearest weight of any style.
            glyphTypeface = faces.Where(f => f.Value is not null)
                .OrderBy(f => f.Key.Style == style ? 0 : 1)
                .ThenBy(f => Math.Abs((int)f.Key.Weight - (int)weight))
                .Select(f => f.Value)
                .FirstOrDefault();
            return glyphTypeface is not null;
        }

        /// <summary>Avalonia can make a typeface from a font file, but only through a member it doesn't expose publicly.</summary>
        private static IGlyphTypeface? CreateFace(IFontManagerImpl fontManager, Stream stream)
        {
            var method = fontManager.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(m => m.Name.EndsWith("TryCreateGlyphTypeface", StringComparison.Ordinal) && m.GetParameters() is { Length: 3 } p && p[0].ParameterType == typeof(Stream));
            if (method is null) return null;
            var args = new object?[] { stream, FontSimulations.None, null };
            return method.Invoke(fontManager, args) is true ? args[2] as IGlyphTypeface : null;
        }

        public override IEnumerator<FontFamily> GetEnumerator() => _families.GetEnumerator();
    }
}
