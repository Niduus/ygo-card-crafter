using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace CardCrafter.Core.Updates;

/// <summary>A published release that has a Windows exe attached.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Name, string Notes, string PageUrl, string DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Finds the latest GitHub Release of Card Crafter. Uses GitHub's public API without signing in,
/// so the repository (or the one releases are published to) must be public.
/// </summary>
public static class UpdateChecker
{
    public const string DefaultRepository = "Niduus/ygo-card-crafter";
    public const string AssetName = "YGOCardCrafter.exe";

    /// <summary>The running app's version, set from the release tag at build time.</summary>
    public static Version CurrentVersion =>
        Normalize(typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>"v1.2.3", "1.2" or "v1.2.3-beta" to a version; null when it isn't one.</summary>
    public static Version? ParseTag(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(['-', '+']);
        if (dash >= 0) s = s[..dash];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return null;
        return Normalize(v);
    }

    public static bool IsNewer(Version latest, Version current) => Normalize(latest) > Normalize(current);

    /// <summary>Reads GitHub's "latest release" JSON. Null for drafts, pre-releases, or releases without the exe.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseTag(tag);
        if (version is null || !root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return null;
            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d[7..].ToLowerInvariant();
            return new ReleaseInfo(
                version, tag,
                root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                url,
                asset.GetProperty("size").GetInt64(),
                sha);
        }
        return null;
    }

    public static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YGOCardCrafter", CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The latest release, or null when there is none (or the repository is private or unreachable).</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(HttpClient http, string repository = DefaultRepository, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cts.Token);
        if (!response.IsSuccessStatusCode) return null;
        return Parse(await response.Content.ReadAsStringAsync(cts.Token));
    }

    /// <summary>Downloads the release's exe into <paramref name="folder"/> and checks its size and SHA-256.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, ReleaseInfo release, string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"YGOCardCrafter-{release.Version}.exe.download");
        using (var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
                if (release.Size > 0) progress?.Report(Math.Min(1.0, (double)total / release.Size));
            }
        }
        var size = new FileInfo(path).Length;
        if (release.Size > 0 && size != release.Size)
        {
            File.Delete(path);
            throw new InvalidDataException($"The download was {size} bytes but GitHub says the file is {release.Size}. Try again.");
        }
        if (release.Sha256 is { } expected)
        {
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (actual != expected)
            {
                stream.Close();
                File.Delete(path);
                throw new InvalidDataException("The downloaded file doesn't match the release's checksum, so it wasn't used.");
            }
        }
        return path;
    }
}

/// <summary>Replaces the running exe with a downloaded one. Windows can't overwrite a running exe, but it can rename it.</summary>
public static class SelfUpdater
{
    public static string OldPath(string exe) => exe + ".old";

    /// <summary>Moves the running exe aside to .old and puts the new one in its place. Restores it if anything fails.</summary>
    public static void Swap(string currentExe, string newExe)
    {
        var old = OldPath(currentExe);
        if (File.Exists(old)) File.Delete(old);
        File.Move(currentExe, old);
        try
        {
            File.Move(newExe, currentExe);
        }
        catch
        {
            File.Move(old, currentExe);
            throw;
        }
    }

    /// <summary>Deletes the previous version left behind by an update. Called at startup.</summary>
    public static void CleanupOld(string currentExe)
    {
        try
        {
            var old = OldPath(currentExe);
            if (File.Exists(old)) File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use by the exiting old process; it is removed on the next start.
        }
    }
}
