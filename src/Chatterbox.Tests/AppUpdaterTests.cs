using System.Net;
using System.Security.Cryptography;
using Chatterbox;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Chatterbox.Tests;

// The in-app updater against a fake GitHub: release parsing, the version
// gate, the SHA-256 gate on the download, the binary swap, and the cleanup
// the next start performs. No network, no real binary; temp paths only.
public class AppUpdaterTests : IDisposable
{
    private const string LatestUrl = "http://fake.invalid/latest";
    private const string FileUrl = "http://fake.invalid/Chatterbox-99.1.2-linux-x64";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chatterbox-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _appDir;
    private readonly string _exe;
    private readonly string _marker;

    public AppUpdaterTests()
    {
        _appDir = Path.Combine(_dir, "app");
        Directory.CreateDirectory(_appDir);
        _exe = Path.Combine(_appDir, AppUpdater.ExeName);
        _marker = Path.Combine(_dir, "data", "update_applied.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReleaseJson(string tag, string sha, long size, string assetName = "Chatterbox-99.1.2-linux-x64") =>
        new JObject
        {
            ["tag_name"] = tag,
            ["name"] = "Chatterbox " + tag.TrimStart('v') + " for Linux",
            ["html_url"] = "http://fake.invalid/releases/" + tag,
            ["body"] = "Live local captions for VRChat. Linux x64, one self-contained file. SHA-256: " + sha.ToUpperInvariant(),
            ["assets"] = new JArray(
                new JObject { ["name"] = "Source code (zip)", ["browser_download_url"] = "http://fake.invalid/src.zip", ["size"] = 5 },
                new JObject { ["name"] = assetName, ["browser_download_url"] = FileUrl, ["size"] = size }),
        }.ToString();

    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new();
        public readonly List<string> Requested = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(Routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private AppUpdater Updater(FakeHandler handler, string? url = LatestUrl) =>
        new(new HttpClient(handler)) { ReleaseUrl = url, ExePath = _exe, MarkerPath = _marker };

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Bytes(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    // ── parsing ──

    [Theory]
    [InlineData("v1.6.1", "1.6.1")]
    [InlineData("1.6.1", "1.6.1")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData("v1.7.0-beta.1", "1.7.0")]
    public void ParseVersion_ReadsTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), AppUpdater.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("release-2")]
    [InlineData("v1.x")]
    public void ParseVersion_RejectsNonVersions(string tag) =>
        Assert.Null(AppUpdater.ParseVersion(tag));

    [Fact]
    public void ParseRelease_PicksTheLinuxFileAndTheShaLine()
    {
        var sha = new string('a', 64);
        var info = AppUpdater.ParseRelease(ReleaseJson("v99.1.2", sha, 1234));
        Assert.Equal(new Version(99, 1, 2), info.Version);
        Assert.Equal("Chatterbox-99.1.2-linux-x64", info.AssetName);
        Assert.Equal(FileUrl, info.AssetUrl);
        Assert.Equal(1234, info.AssetSize);
        Assert.Equal(sha, info.Sha256);          // lower-cased
        Assert.Equal("Chatterbox 99.1.2 for Linux", info.Title);
    }

    [Fact]
    public void ParseRelease_ReadsTheShapeThePublishedReleaseUses()
    {
        // The v1.5.4 release on GitHub, as its API returned it (abridged).
        var json = new JObject
        {
            ["tag_name"] = "v1.5.4",
            ["name"] = "Chatterbox 1.5.4 for Linux",
            ["html_url"] = "https://github.com/example/Chatterbox-Linux/releases/tag/v1.5.4",
            ["body"] = "Live local speech-to-text captions for the VRChat chatbox. Linux x64, one self-contained file (chmod +x after download). SHA-256: FD71954F56D614BD4E0E375682446A3EAF2CAB8D595F5DF6A82A45045D7A6521",
            ["assets"] = new JArray(new JObject
            {
                ["name"] = "Chatterbox-1.5.4-linux-x64",
                ["browser_download_url"] = "https://github.com/example/Chatterbox-Linux/releases/download/v1.5.4/Chatterbox-1.5.4-linux-x64",
                ["size"] = 39698716,
            }),
        }.ToString();
        var info = AppUpdater.ParseRelease(json);
        Assert.Equal(new Version(1, 5, 4), info.Version);
        Assert.Equal("fd71954f56d614bd4e0e375682446a3eaf2cab8d595f5df6a82a45045d7a6521", info.Sha256);
        Assert.Equal(39698716, info.AssetSize);
    }

    [Fact]
    public void ParseRelease_RefusesNotesWithoutSha()
    {
        var json = new JObject
        {
            ["tag_name"] = "v99.0.0",
            ["body"] = "no checksum here",
            ["assets"] = new JArray(new JObject { ["name"] = "Chatterbox-99.0.0-linux-x64", ["browser_download_url"] = FileUrl, ["size"] = 1 }),
        }.ToString();
        var ex = Assert.Throws<FormatException>(() => AppUpdater.ParseRelease(json));
        Assert.Contains("SHA-256", ex.Message);
    }

    [Fact]
    public void ParseRelease_RefusesReleasesWithoutTheLinuxFile()
    {
        var ex = Assert.Throws<FormatException>(() =>
            AppUpdater.ParseRelease(ReleaseJson("v99.0.0", new string('b', 64), 1, assetName: "Chatterbox-99.0.0-win-x64.zip")));
        Assert.Contains("linux-x64", ex.Message);
    }

    // ── check ──

    [Fact]
    public async Task Check_ReportsANewerRelease()
    {
        var file = new byte[] { 1, 2, 3 };
        var handler = new FakeHandler();
        handler.Routes[LatestUrl] = () => Text(ReleaseJson("v99.1.2", Sha(file), file.Length));
        var r = await Updater(handler).CheckAsync();
        Assert.Null(r.Error);
        Assert.NotNull(r.Update);
        Assert.Equal(new Version(99, 1, 2), r.Latest);
    }

    [Fact]
    public async Task Check_AnOlderOrEqualReleaseIsUpToDate()
    {
        var handler = new FakeHandler();
        handler.Routes[LatestUrl] = () => Text(ReleaseJson("v0.0.1", new string('c', 64), 1));
        var r = await Updater(handler).CheckAsync();
        Assert.Null(r.Error);
        Assert.Null(r.Update);
        Assert.Equal(new Version(0, 0, 1), r.Latest);
    }

    [Fact]
    public async Task Check_NoReleaseYet_IsAPlainMessageNotAnException()
    {
        var r = await Updater(new FakeHandler()).CheckAsync();   // 404
        Assert.Null(r.Update);
        Assert.Contains("no release", r.Error);
        Assert.Contains("still private", r.Error);
    }

    [Fact]
    public async Task Check_UnconfiguredBuild_NeverRequests()
    {
        var handler = new FakeHandler();
        var r = await Updater(handler, url: null).CheckAsync();
        Assert.Empty(handler.Requested);
        Assert.Contains("no update source", r.Error);
    }

    [Fact]
    public void ThisBuild_HasAnUpdateSource()
    {
        // The csproj bakes <UpdateRepository> in; a release without it
        // could never update itself.
        Assert.Matches(@"^[\w.-]+/[\w.-]+$", AppUpdater.Repository);
        Assert.True(AppUpdater.IsConfigured);
        Assert.StartsWith("https://api.github.com/repos/", AppUpdater.DefaultReleaseUrl);
        Assert.EndsWith("/releases/latest", AppUpdater.DefaultReleaseUrl);
    }

    // ── download + stage ──

    [Fact]
    public async Task Download_VerifiesAndStagesTheBinary()
    {
        var fileBytes = new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 9, 9 };
        var handler = new FakeHandler();
        handler.Routes[LatestUrl] = () => Text(ReleaseJson("v99.1.2", Sha(fileBytes), fileBytes.Length));
        handler.Routes[FileUrl] = () => Bytes(fileBytes);
        var updater = Updater(handler);
        var check = await updater.CheckAsync();

        long lastGot = -1, lastTotal = -1;
        var (staged, error) = await updater.DownloadAsync(check.Update!, (got, total) => { lastGot = got; lastTotal = total; });
        Assert.Null(error);
        Assert.Equal(Path.Combine(_appDir, "Chatterbox.new"), staged);
        Assert.Equal(fileBytes, File.ReadAllBytes(staged!));
        Assert.Equal(fileBytes.Length, lastGot);
        Assert.Equal(fileBytes.Length, lastTotal);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "data", "updates")));   // no .partial left
        if (OperatingSystem.IsLinux())
            Assert.True(File.GetUnixFileMode(staged!).HasFlag(UnixFileMode.UserExecute | UnixFileMode.OtherExecute));
    }

    [Fact]
    public async Task Download_RefusesAHashMismatch()
    {
        var file = new byte[] { 1 };
        var handler = new FakeHandler();
        handler.Routes[LatestUrl] = () => Text(ReleaseJson("v99.1.2", new string('d', 64), file.Length));
        handler.Routes[FileUrl] = () => Bytes(file);
        var updater = Updater(handler);
        var check = await updater.CheckAsync();

        var (staged, error) = await updater.DownloadAsync(check.Update!, (_, _) => { });
        Assert.Null(staged);
        Assert.Contains("SHA-256", error);
        Assert.False(File.Exists(Path.Combine(_appDir, "Chatterbox.new")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "data", "updates")));
    }

    [Fact]
    public async Task Download_RefusesASizeMismatch()
    {
        var file = new byte[] { 1, 2 };
        var handler = new FakeHandler();
        handler.Routes[LatestUrl] = () => Text(ReleaseJson("v99.1.2", Sha(file), 999));
        handler.Routes[FileUrl] = () => Bytes(file);
        var updater = Updater(handler);
        var check = await updater.CheckAsync();

        var (staged, error) = await updater.DownloadAsync(check.Update!, (_, _) => { });
        Assert.Null(staged);
        Assert.Contains("size mismatch", error);
        Assert.False(File.Exists(Path.Combine(_appDir, "Chatterbox.new")));
    }

    // ── apply + the next start ──

    [Fact]
    public void Apply_SwapsTheBinaryAndTheNextStartCleansUp()
    {
        var oldBytes = new byte[] { 1, 1, 1 };
        var newBytes = new byte[] { 2, 2, 2, 2 };
        File.WriteAllBytes(_exe, oldBytes);
        var staged = _exe + ".new";
        File.WriteAllBytes(staged, newBytes);
        var updater = Updater(new FakeHandler());

        Assert.Null(updater.Apply(staged, new Version(99, 1, 2)));
        Assert.Equal(newBytes, File.ReadAllBytes(_exe));
        Assert.Equal(oldBytes, File.ReadAllBytes(_exe + ".old"));
        Assert.False(File.Exists(staged));
        Assert.True(File.Exists(_marker));
        var marker = JObject.Parse(File.ReadAllText(_marker));
        Assert.Equal(AppUpdater.CurrentVersion.ToString(3), marker["from"]?.ToString());
        Assert.Equal("99.1.2", marker["to"]?.ToString());

        // The "next start" (same test build, so the version doesn't match 99.1.2).
        var note = updater.FinishPendingUpdate();
        Assert.Contains("99.1.2", note);
        Assert.False(File.Exists(_exe + ".old"));
        Assert.False(File.Exists(_marker));
        Assert.Null(updater.FinishPendingUpdate());   // nothing twice
    }

    [Fact]
    public void Apply_ReplacesWhateverPathTheAppRunsFrom()
    {
        // Not the app-grid folder: a download run from ~/Downloads updates
        // itself in place too.
        var elsewhere = Path.Combine(_dir, "Downloads", "Chatterbox-1.0.0-linux-x64");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllBytes(elsewhere, new byte[] { 1 });
        File.WriteAllBytes(elsewhere + ".new", new byte[] { 2, 2 });
        var updater = new AppUpdater(new HttpClient(new FakeHandler())) { ReleaseUrl = LatestUrl, ExePath = elsewhere, MarkerPath = _marker };

        Assert.Null(updater.Apply(elsewhere + ".new", new Version(99, 0, 0)));
        Assert.Equal(new byte[] { 2, 2 }, File.ReadAllBytes(elsewhere));
        Assert.False(File.Exists(_exe));                    // the app-grid path was never touched
        Assert.NotNull(updater.FinishPendingUpdate());
        Assert.False(File.Exists(elsewhere + ".old"));
    }

    [Fact]
    public void Apply_WithoutAStagedFile_LeavesTheBinaryAlone()
    {
        File.WriteAllBytes(_exe, new byte[] { 7 });
        var error = Updater(new FakeHandler()).Apply(_exe + ".new", new Version(99, 0, 0));
        Assert.NotNull(error);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(_exe));
        Assert.False(File.Exists(_marker));
    }

    [Fact]
    public void NextStart_RemovesAnAbandonedDownloadToo()
    {
        File.WriteAllBytes(_exe, new byte[] { 7 });
        File.WriteAllBytes(_exe + ".new", new byte[] { 8 });   // downloaded, never applied
        var updater = Updater(new FakeHandler());
        Assert.Null(updater.FinishPendingUpdate());            // no marker: nothing to report
        Assert.False(File.Exists(_exe + ".new"));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(_exe));
    }
}
