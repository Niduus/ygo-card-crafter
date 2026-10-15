using System.Net;
using System.Security.Cryptography;
using CardCrafter.Core.Updates;

namespace CardCrafter.Core.Tests;

public class UpdateTests
{
    private static string ReleaseJson(string tag, string url, long size, string? digest = null, bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}", "name": "Card Crafter {{tag}}", "body": "What's new", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/Niduus/ygo-card-crafter/releases/tag/{{tag}}",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://github.com/x/notes.txt", "size": 3 },
            { "name": "YGOCardCrafter.exe", "browser_download_url": "{{url}}", "size": {{size}}{{(digest is null ? "" : $", \"digest\": \"sha256:{digest}\"")}} }
          ]
        }
        """;

    [Theory]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2.0.1-beta", "2.0.1")]
    public void ParsesTags(string tag, string expected) => Assert.Equal(Version.Parse(expected), UpdateChecker.ParseTag(tag));

    [Fact]
    public void ComparesVersions()
    {
        Assert.True(UpdateChecker.IsNewer(new Version(0, 3, 0), new Version(0, 2, 0, 0)));
        Assert.False(UpdateChecker.IsNewer(new Version(0, 2, 0), new Version(0, 2, 0, 0)));
        Assert.Equal(new Version(0, 2, 0), UpdateChecker.CurrentVersion);
    }

    [Fact]
    public void ParsesLatestRelease_PicksTheExe()
    {
        var r = UpdateChecker.Parse(ReleaseJson("v0.3.0", "https://github.com/Niduus/ygo-card-crafter/releases/download/v0.3.0/YGOCardCrafter.exe", 1234, "ab12"))!;
        Assert.Equal(new Version(0, 3, 0), r.Version);
        Assert.Equal(1234, r.Size);
        Assert.Equal("ab12", r.Sha256);
        Assert.EndsWith("YGOCardCrafter.exe", r.DownloadUrl);
    }

    [Fact]
    public void IgnoresPrereleasesAndNonGithubDownloads()
    {
        Assert.Null(UpdateChecker.Parse(ReleaseJson("v9.0.0", "https://github.com/a/YGOCardCrafter.exe", 1, prerelease: true)));
        Assert.Null(UpdateChecker.Parse(ReleaseJson("v9.0.0", "https://evil.example/YGOCardCrafter.exe", 1)));
    }

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public async Task Download_ChecksSizeAndChecksum()
    {
        using var tmp = new TempDir();
        var bytes = "new exe contents"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var http = new HttpClient(new FakeHandler(bytes));
        var good = new ReleaseInfo(new Version(0, 3, 0), "v0.3.0", "", "", "", "https://github.com/x/YGOCardCrafter.exe", bytes.Length, sha);
        var path = await UpdateChecker.DownloadAsync(http, good, tmp.Path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));

        var tampered = good with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadAsync(http, tampered, tmp.Path));
        var truncated = good with { Size = bytes.Length + 10, Sha256 = null };
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadAsync(http, truncated, tmp.Path));
    }

    [Fact]
    public void Swap_ReplacesExeAndKeepsOldForCleanup()
    {
        using var tmp = new TempDir();
        var exe = Path.Combine(tmp.Path, "YGOCardCrafter.exe");
        var update = Path.Combine(tmp.Path, "new.download");
        File.WriteAllText(exe, "old");
        File.WriteAllText(update, "new");
        SelfUpdater.Swap(exe, update);
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(SelfUpdater.OldPath(exe)));
        SelfUpdater.CleanupOld(exe);
        Assert.False(File.Exists(SelfUpdater.OldPath(exe)));
    }

    [Fact]
    public void Swap_RestoresTheOldExeIfTheNewOneIsMissing()
    {
        using var tmp = new TempDir();
        var exe = Path.Combine(tmp.Path, "YGOCardCrafter.exe");
        File.WriteAllText(exe, "old");
        Assert.ThrowsAny<IOException>(() => SelfUpdater.Swap(exe, Path.Combine(tmp.Path, "missing")));
        Assert.Equal("old", File.ReadAllText(exe));
    }
}
