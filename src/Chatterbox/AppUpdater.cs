using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Chatterbox.Stt;

namespace Chatterbox;

// The latest published release, when it is newer than this build.
public sealed record UpdateInfo(
    Version Version, string Tag, string Title, string Notes, string PageUrl,
    string AssetName, string AssetUrl, long AssetSize, string Sha256);

// What a check found: a newer release, or the latest version when this
// build is current, or why nothing could be determined.
public sealed record UpdateCheck(UpdateInfo? Update, Version? Latest, string? Error);

// In-app updates from the project's GitHub Releases page.
//
// Check: one request to the Releases API says what the latest release is.
// It counts as an update when its tag is a higher version than this build
// and it carries the single linux-x64 file plus a SHA-256 line in its
// notes (the release checklist puts one there). Install: the file is
// downloaded, hashed against that line, given its executable bit and
// staged beside the running binary; the running binary is renamed aside —
// a rename never disturbs a running process on Linux, its inode lives on
// — the new one takes its name, and the app restarts. The next start
// removes the old file and reports the update in the boot log and a toast.
//
// Whichever path the app runs from is the path replaced: the app-grid
// install in ~/.local/share/Chatterbox/app, or a download run in place.
//
// Privacy: the request carries nothing but the app's name and version in
// its User-Agent, like every model download. The startup check is a
// setting, off by default; the manual check is a button press.
public sealed class AppUpdater
{
    public const string ExeName = "Chatterbox";
    private const int CheckTimeoutMs = 15_000;
    private static readonly Regex ShaLine = new(@"SHA-?256\s*[:=]?\s*([0-9a-fA-F]{64})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AssetPattern = new(@"^Chatterbox-.*-linux-x64$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // owner/name of the GitHub repository releases go to — set in the csproj
    // (<UpdateRepository>) and baked in as assembly metadata. Empty = no
    // update source: the Updates section says so and no request is ever made.
    public static string Repository { get; } =
        typeof(AppUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value?.Trim() ?? "";

    // Test hook (--update-url): the full URL of a "latest release" JSON
    // document that stands in for the GitHub API.
    public static string? LatestReleaseUrlOverride { get; set; }

    public static string? DefaultReleaseUrl =>
        LatestReleaseUrlOverride ??
        (Repository.Length > 0 ? $"https://api.github.com/repos/{Repository}/releases/latest" : null);

    public static bool IsConfigured => DefaultReleaseUrl != null;

    // Three-part version of this build (the csproj <Version>).
    public static Version CurrentVersion { get; } =
        typeof(AppUpdater).Assembly.GetName().Version is { } v
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0))
            : new Version(0, 0, 0);

    private static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };

    // Identifies the app to GitHub by name and version (a User-Agent is
    // required by its API) — see SttModelManager.SetUserAgent.
    public static void SetUserAgent(string ua) =>
        Shared.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);

    // The mode the installed binary gets (LinuxInstaller gives its copy
    // the same): the user's rwx, everyone else's rx.
    private const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly HttpClient _http;

    // The "latest release" document to ask; tests point it at a fake.
    public string? ReleaseUrl { get; init; } = DefaultReleaseUrl;
    // The running binary (tests point this at a temp file). Read once, at
    // construction: on Linux /proc/self/exe follows a rename, so after
    // Apply the process path would name the file renamed aside.
    public string ExePath { get; init; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);
    // Written when an update is applied, read and removed by the next start.
    public string MarkerPath { get; init; } = Path.Combine(SttPaths.DataDir, "update_applied.json");

    public AppUpdater(HttpClient? http = null) => _http = http ?? Shared;

    // "v1.6.1" / "1.6.1" / "1.6" → a three-part version; null when the tag
    // is not a version at all.
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var nums = new int[3];
        for (int i = 0; i < Math.Min(parts.Length, 3); i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return null;
        return new Version(nums[0], nums[1], nums[2]);
    }

    // Reads a GitHub "release" JSON document. Throws FormatException with
    // the reason when it can't be used as an update.
    public static UpdateInfo ParseRelease(string json)
    {
        var j = JObject.Parse(json);
        var tag = j["tag_name"]?.ToString() ?? "";
        var version = ParseVersion(tag) ?? throw new FormatException($"release tag '{tag}' is not a version number");
        var assets = j["assets"] as JArray ?? new JArray();
        var asset = assets.FirstOrDefault(a => AssetPattern.IsMatch(a["name"]?.ToString() ?? ""))
            ?? throw new FormatException("the release has no Chatterbox-<version>-linux-x64 file");
        var url = asset["browser_download_url"]?.ToString() ?? "";
        if (url.Length == 0) throw new FormatException("the release's file has no download address");
        var notes = j["body"]?.ToString() ?? "";
        var sha = ShaLine.Match(notes);
        if (!sha.Success)
            throw new FormatException("the release notes carry no SHA-256 line, so a download could not be verified");
        return new UpdateInfo(version, tag, j["name"]?.ToString() is { Length: > 0 } name ? name : tag, notes,
            j["html_url"]?.ToString() ?? "", asset["name"]!.ToString(), url,
            asset["size"]?.Value<long>() ?? 0, sha.Groups[1].Value.ToLowerInvariant());
    }

    // One request. Never throws: the result says what happened.
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var url = ReleaseUrl;
        if (url == null) return new UpdateCheck(null, null, "no update source is configured in this build");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeoutMs);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheck(null, null, "GitHub shows no release — none has been published yet, or the repository is still private");
            if (!response.IsSuccessStatusCode)
                return new UpdateCheck(null, null, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var release = ParseRelease(json);
            return release.Version > CurrentVersion
                ? new UpdateCheck(release, release.Version, null)
                : new UpdateCheck(null, release.Version, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UpdateCheck(null, null, $"no answer from GitHub within {CheckTimeoutMs / 1000} s");
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheck(null, null, "check cancelled");
        }
        catch (Exception ex)
        {
            return new UpdateCheck(null, null, ex.Message);
        }
    }

    // Downloads the release file into <data dir>/updates, verifies it
    // against the published SHA-256, gives it the executable bit and
    // stages it beside the running binary as Chatterbox.new. Returns that
    // path, or the error.
    public async Task<(string? StagedExe, string? Error)> DownloadAsync(
        UpdateInfo update, Action<long, long> onProgress, CancellationToken ct = default)
    {
        var appDir = Path.GetDirectoryName(ExePath)!;
        var staged = ExePath + ".new";
        var tempDir = Path.Combine(Path.GetDirectoryName(MarkerPath)!, "updates");
        var tempFile = Path.Combine(tempDir, update.AssetName + ".partial");
        try
        {
            Directory.CreateDirectory(tempDir);
            using var response = await _http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = update.AssetSize > 0 ? update.AssetSize : response.Content.Headers.ContentLength ?? 0;
            SttDownload.EnsureFreeSpace(tempDir, Math.Max(total, 1) * 2);
            SttDownload.EnsureFreeSpace(appDir, Math.Max(total, 1) * 2);

            using var sha = SHA256.Create();
            long received;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(tempFile))
            {
                received = await SttDownload.CopyAsync(source, target, sha, got => onProgress(got, total), ct).ConfigureAwait(false);
            }
            if (update.AssetSize > 0 && received != update.AssetSize)
                return (null, $"size mismatch ({received} vs {update.AssetSize} bytes)");
            var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (hash != update.Sha256)
                return (null, "SHA-256 mismatch — the download is corrupt or the release was changed; nothing was installed");

            // A download never carries an executable bit; the staged copy
            // gets the installed binary's mode before it takes the name.
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(tempFile, Executable);
            File.Move(tempFile, staged, overwrite: true);
            return (staged, null);
        }
        catch (OperationCanceledException)
        {
            return (null, "download cancelled");
        }
        catch (Exception ex)
        {
            return (null, $"download failed — {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    // Puts the staged binary in place: the running one is renamed aside
    // (Chatterbox.old — it keeps running, a rename only changes the
    // directory entry) and the new one renamed under its name, both within
    // the same folder, so the swap is two renames and never a copy over a
    // file in use. The caller restarts the app. Returns the error, or null.
    public string? Apply(string stagedExe, Version to)
    {
        var old = ExePath + ".old";
        try
        {
            if (!File.Exists(stagedExe)) return "the downloaded file is gone";
            if (File.Exists(old)) File.Delete(old);
            File.Move(ExePath, old);
            try { File.Move(stagedExe, ExePath); }
            catch
            {
                File.Move(old, ExePath);   // the running binary gets its name back
                throw;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, JsonConvert.SerializeObject(new
            {
                from = CurrentVersion.ToString(3),
                to = to.ToString(3),
                at = DateTime.UtcNow.ToString("o"),
            }));
            return null;
        }
        catch (Exception ex)
        {
            return $"could not replace {ExePath} — {ex.Message}";
        }
    }

    // Called at boot: clears what the previous version left behind (its
    // binary renamed aside, or a download that never got installed) and
    // reports the update it finished, if any.
    public string? FinishPendingUpdate()
    {
        foreach (var leftover in new[] { ExePath + ".old", ExePath + ".new" })
        {
            try { if (File.Exists(leftover)) File.Delete(leftover); }
            catch { /* not ours to remove — the next start tries again */ }
        }
        try
        {
            if (!File.Exists(MarkerPath)) return null;
            var marker = JObject.Parse(File.ReadAllText(MarkerPath));
            File.Delete(MarkerPath);
            var from = marker["from"]?.ToString() ?? "?";
            var to = marker["to"]?.ToString() ?? "?";
            var now = CurrentVersion.ToString(3);
            return now == to
                ? $"updated from {from} to {to}"
                : $"update to {to} was applied, but this is version {now}";
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("AppUpdater.Finish", ex);
            return null;
        }
    }
}
