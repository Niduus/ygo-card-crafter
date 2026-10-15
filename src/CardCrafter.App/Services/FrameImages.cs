using System.IO.Compression;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace CardCrafter.App.Services;

/// <summary>
/// The card textures the preview is assembled from: YGO Omega's card frames, Attribute orbs, Level and Rank stars,
/// Link Arrows, Spell/Trap icons and border, in the user's own zip or folder. Card Crafter ships none and draws
/// no frames of its own: without textures the preview shows a plain placeholder.
/// </summary>
public static partial class FrameImages
{
    /// <summary>Card Crafter's name for each texture, and the texture file it comes from (as YGO Omega names them).</summary>
    public static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>
    {
        ["normal"] = "NormalFrame", ["effect"] = "EffectFrame", ["ritual"] = "RitualFrame", ["fusion"] = "FusionFrame",
        ["synchro"] = "SynchroFrame", ["xyz"] = "XYZFrame", ["link"] = "LinkFrame", ["token"] = "TokenFrame",
        ["spell"] = "SpellCardFrame", ["trap"] = "TrapCardFrame",
        ["pendulum-normal"] = "PendulumNormalFullFrame", ["pendulum-effect"] = "PendulumEffectFullFrame",
        ["pendulum-fusion"] = "PendulumFusionFullFrame", ["pendulum-synchro"] = "PendulumSynchroFullFrame",
        ["pendulum-xyz"] = "PendulumXYZFullFrame", ["pendulum-ritual"] = "PendulumRitualFullFrame", ["pendulum-bottom"] = "PendulumBottom",
        ["border"] = "DefaultBorder",
        ["attribute-dark"] = "Dark", ["attribute-light"] = "Light", ["attribute-earth"] = "Earth", ["attribute-water"] = "Water",
        ["attribute-fire"] = "Fire", ["attribute-wind"] = "Wind", ["attribute-divine"] = "Divine",
        ["attribute-spell"] = "Spell", ["attribute-trap"] = "Trap",
        ["level"] = "Level", ["rank"] = "Rank",
        ["icon-quickplay"] = "QuickPlay", ["icon-continuous"] = "Continuous", ["icon-equip"] = "Equipment",
        ["icon-field"] = "Field", ["icon-ritual"] = "Ritual", ["icon-counter"] = "Counter",
        ["link-top-on"] = "TopOn", ["link-top-off"] = "TopOff", ["link-bottom-on"] = "BottomOn", ["link-bottom-off"] = "BottomOff",
        ["link-left-on"] = "LeftOn", ["link-left-off"] = "LeftOff", ["link-right-on"] = "RightOn", ["link-right-off"] = "RightOff",
        ["link-topleft-on"] = "TopLeftOn", ["link-topleft-off"] = "TopLeftOff", ["link-topright-on"] = "TopRightOn", ["link-topright-off"] = "TopRightOff",
        ["link-bottomleft-on"] = "BottomLeftOn", ["link-bottomleft-off"] = "BottomLeftOff",
        ["link-bottomright-on"] = "BottomRightOn", ["link-bottomright-off"] = "BottomRightOff",
    };

    /// <summary>The full-card frames, which need the standard layout (a name band at the top).</summary>
    private static bool IsFrame(string slot) => slot is "normal" or "effect" or "ritual" or "fusion" or "synchro" or "xyz" or "link" or "token" or "spell" or "trap"
        || (slot.StartsWith("pendulum-", StringComparison.Ordinal) && slot != "pendulum-bottom");

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];
    private static Dictionary<string, Bitmap> _images = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the textures were loaded from, or null when none are loaded.</summary>
    public static string? Folder { get; private set; }
    public static int Count => _images.Count;
    public static event Action? Changed;

    /// <summary>Textures Card Crafter looked for and didn't find in the folder.</summary>
    public static IReadOnlyList<string> Missing { get; private set; } = [];

    /// <summary>
    /// Loads every recognised texture in the folder and its subfolders (null clears), plus any fonts there.
    /// Returns how many textures were found.
    /// </summary>
    public static int Load(string? folder)
    {
        var old = _images;
        var images = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        if (folder is not null && Directory.Exists(folder))
        {
            // Exported textures can carry a number, e.g. "EffectFrame #2375.png"; group them by their plain name.
            var byName = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .GroupBy(f => Numbered().Replace(Path.GetFileNameWithoutExtension(f), "").Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var (slot, file) in Files)
            {
                if (!byName.TryGetValue(file, out var candidates))
                {
                    if (slot is not ("pendulum-ritual" or "icon-field" or "token")) missing.Add(file);
                    continue;
                }
                if (Choose(slot, file, candidates) is { } path && TryLoad(path) is { } bitmap) images[slot] = bitmap;
            }
        }
        CardFonts.Load(images.Count > 0 ? folder : null);
        _images = images;
        Missing = missing;
        Folder = images.Count > 0 ? folder : null;
        Changed?.Invoke();
        foreach (var b in old.Values) b.Dispose();
        return images.Count;
    }

    [GeneratedRegex(@"\s*#\d+$")]
    private static partial Regex Numbered();

    /// <summary>
    /// Picks one file when several share a name. Frames come in a standard version (name band, then the art)
    /// and a full-art version with the art reaching the top; the preview needs the standard one. Otherwise the
    /// file without a number wins.
    /// </summary>
    private static string? Choose(string slot, string name, List<string> candidates)
    {
        if (candidates.Count == 1) return candidates[0];
        if (IsFrame(slot))
        {
            var standard = candidates.FirstOrDefault(HasNameBand);
            if (standard is not null) return standard;
        }
        return candidates.FirstOrDefault(c => Path.GetFileNameWithoutExtension(c).Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? candidates.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).First();
    }

    /// <summary>True when the frame has an opaque name band at the top and its art window starts below it.</summary>
    private static bool HasNameBand(string path)
    {
        try
        {
            using var bmp = SKBitmap.Decode(path);
            if (bmp is null) return false;
            int x = bmp.Width / 2;
            return bmp.GetPixel(x, (int)(bmp.Height * 0.077)).Alpha > 200 && bmp.GetPixel(x, (int)(bmp.Height * 0.14)).Alpha > 200;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Bitmap? TryLoad(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null; // an unreadable texture is reported as missing by the preview
        }
    }

    /// <summary>
    /// Unpacks a zip of textures (and fonts) into <paramref name="intoFolder"/>/&lt;zip name&gt; and returns that folder.
    /// Only images and font files are taken, and nothing is written outside the folder.
    /// </summary>
    public static string ExtractZip(string zipPath, string intoFolder)
    {
        var target = Path.Combine(intoFolder, Path.GetFileNameWithoutExtension(zipPath));
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue;
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (!Extensions.Contains(ext) && !CardFonts.Extensions.Contains(ext)) continue;
            var dest = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!dest.StartsWith(root, StringComparison.Ordinal)) continue; // no "../" escapes
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
        return target;
    }

    public static Bitmap? Get(string name) => _images.GetValueOrDefault(name);
    public static bool Has(string name) => _images.ContainsKey(name);
}
