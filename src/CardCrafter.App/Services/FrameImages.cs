using Avalonia.Media.Imaging;

namespace CardCrafter.App.Services;

/// <summary>
/// Optional card-frame artwork for the preview, loaded from a folder the user picks (for example card
/// templates they already have). Card Crafter ships no official frame art; without a folder it draws its own.
/// Images are drawn over the whole card, so they should be full card templates at the standard 59x86 ratio.
/// </summary>
public static class FrameImages
{
    /// <summary>The file names looked for, without extension (.png, .jpg, .jpeg or .webp).</summary>
    public static readonly string[] Names =
    [
        "normal", "effect", "ritual", "fusion", "synchro", "xyz", "link", "token", "spell", "trap",
        "pendulum-normal", "pendulum-effect", "pendulum-ritual", "pendulum-fusion", "pendulum-synchro", "pendulum-xyz",
        "attribute-dark", "attribute-light", "attribute-earth", "attribute-water", "attribute-fire", "attribute-wind",
        "attribute-divine", "attribute-spell", "attribute-trap", "level", "rank",
    ];

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp"];
    private static Dictionary<string, Bitmap> _images = new(StringComparer.OrdinalIgnoreCase);

    public static string? Folder { get; private set; }
    public static int Count => _images.Count;
    public static event Action? Changed;

    /// <summary>Loads every recognised image in the folder (null clears). Returns how many were found.</summary>
    public static int Load(string? folder)
    {
        var old = _images;
        var images = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        if (folder is not null && Directory.Exists(folder))
        {
            foreach (var name in Names)
            {
                foreach (var ext in Extensions)
                {
                    var path = Path.Combine(folder, name + ext);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        using var stream = File.OpenRead(path);
                        images[name] = new Bitmap(stream);
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                    {
                        // Skip images that can't be read; the built-in drawing is used for that part instead.
                    }
                }
            }
        }
        _images = images;
        Folder = images.Count > 0 ? folder : null;
        Changed?.Invoke();
        foreach (var b in old.Values) b.Dispose();
        return images.Count;
    }

    public static Bitmap? Get(string name) => _images.GetValueOrDefault(name);
}
