using Avalonia.Media.Imaging;

namespace CardCrafter.App.Services;

/// <summary>
/// Optional card-frame artwork for the preview: full-card templates drawn over the art, with the card's
/// name, stars, text and stats drawn on top. Card Crafter ships none. They come from a folder the user picks,
/// the textures of the community OmegaCustom pack (found in the Omega folder or downloaded on request),
/// or the built-in drawing when there are none.
/// </summary>
public static class FrameImages
{
    /// <summary>Card Crafter's names for each slot, and the OmegaCustom texture file names that fill them.</summary>
    public static readonly IReadOnlyDictionary<string, string> OmegaCustomNames = new Dictionary<string, string>
    {
        ["normal"] = "NormalFrame", ["effect"] = "EffectFrame", ["ritual"] = "RitualFrame", ["fusion"] = "FusionFrame",
        ["synchro"] = "SynchroFrame", ["xyz"] = "XYZFrame", ["link"] = "LinkFrame", ["token"] = "TokenFrame",
        ["spell"] = "SpellCardFrame", ["trap"] = "TrapCardFrame",
        ["pendulum-normal"] = "PendulumNormalFullFrame", ["pendulum-effect"] = "PendulumEffectFullFrame",
        ["pendulum-fusion"] = "PendulumFusionFullFrame", ["pendulum-synchro"] = "PendulumSynchroFullFrame",
        ["pendulum-xyz"] = "PendulumXYZFullFrame",
        ["attribute-dark"] = "Dark", ["attribute-light"] = "Light", ["attribute-earth"] = "Earth", ["attribute-water"] = "Water",
        ["attribute-fire"] = "Fire", ["attribute-wind"] = "Wind", ["attribute-divine"] = "Divine",
        ["attribute-spell"] = "Spell", ["attribute-trap"] = "Trap", ["level"] = "Level", ["rank"] = "Rank",
    };

    public static IEnumerable<string> Names => OmegaCustomNames.Keys.Append("pendulum-ritual");

    public const string OmegaCustomRepo = "zeak6464/OmegaCustom";
    public const string OmegaCustomRawBase = "https://raw.githubusercontent.com/zeak6464/OmegaCustom/main/Textures/Interface/";

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];
    private static Dictionary<string, Bitmap> _images = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _omegaStyle = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, FrameGeometry> _geometry = new(StringComparer.OrdinalIgnoreCase);

    public static string? Folder { get; private set; }
    public static int Count => _images.Count;
    public static event Action? Changed;

    /// <summary>Loads every recognised image in the folder (null clears). Returns how many were found.</summary>
    public static int Load(string? folder)
    {
        var old = _images;
        var images = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        var omega = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var geometry = new Dictionary<string, FrameGeometry>(StringComparer.OrdinalIgnoreCase);
        if (folder is not null && Directory.Exists(folder))
        {
            foreach (var name in Names)
            {
                // Card Crafter's own name first, then the OmegaCustom texture name.
                var candidates = new List<(string File, bool Omega)> { (name, false) };
                if (OmegaCustomNames.TryGetValue(name, out var omegaName)) candidates.Add((omegaName, true));
                foreach (var (file, isOmega) in candidates)
                {
                    var (bitmap, path) = TryLoad(folder, file);
                    if (bitmap is null) continue;
                    images[name] = bitmap;
                    if (isOmega) omega.Add(name);
                    // Full-card frames get their boxes measured; small icons (attributes, stars) don't need it.
                    if (!name.StartsWith("attribute-") && name is not ("level" or "rank") && FrameGeometry.Measure(path!) is { } g)
                        geometry[name] = g;
                    break;
                }
            }
        }
        _images = images;
        _omegaStyle = omega;
        _geometry = geometry;
        Folder = images.Count > 0 ? folder : null;
        Changed?.Invoke();
        foreach (var b in old.Values) b.Dispose();
        return images.Count;
    }

    private static (Bitmap? Bitmap, string? Path) TryLoad(string folder, string file)
    {
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(folder, file + ext);
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                return (new Bitmap(stream), path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // Unreadable images are skipped; the built-in drawing covers that part.
            }
        }
        return (null, null);
    }

    public static Bitmap? Get(string name) => _images.GetValueOrDefault(name);

    /// <summary>The measured boxes of a full-card frame image, or null.</summary>
    public static FrameGeometry? Geometry(string name) => _geometry.GetValueOrDefault(name);

    /// <summary>True when the image came from OmegaCustom, whose frames use YGO Omega's own layout.</summary>
    public static bool IsOmegaStyle(string name) => _omegaStyle.Contains(name);

    /// <summary>Where OmegaCustom's textures live inside an Omega install that has the pack applied.</summary>
    public static string? FindInOmega(string? omegaRoot)
    {
        if (omegaRoot is null) return null;
        foreach (var rel in new[] { Path.Combine("YGO Omega_Data", "Files", "Textures", "Interface"), Path.Combine("Files", "Textures", "Interface"), Path.Combine("Textures", "Interface") })
        {
            var dir = Path.Combine(omegaRoot, rel);
            if (File.Exists(Path.Combine(dir, "EffectFrame.png"))) return dir;
        }
        return null;
    }

    /// <summary>
    /// Downloads just the card-frame textures from the OmegaCustom repository into <paramref name="folder"/>.
    /// Returns how many files were saved.
    /// </summary>
    public static async Task<int> DownloadOmegaCustomAsync(HttpClient http, string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var files = OmegaCustomNames.Values.Distinct().ToList();
        int saved = 0, done = 0;
        foreach (var file in files)
        {
            using var response = await http.GetAsync(OmegaCustomRawBase + Uri.EscapeDataString(file) + ".png", ct);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                // Only keep real PNGs.
                if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50)
                {
                    await File.WriteAllBytesAsync(Path.Combine(folder, file + ".png"), bytes, ct);
                    saved++;
                }
            }
            progress?.Report(++done / (double)files.Count);
        }
        return saved;
    }
}
