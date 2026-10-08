using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// The Parakeet engine's native libraries (sherpa-onnx + ONNX Runtime),
// downloaded on demand so the app package stays small — the natives are
// useless without the (much larger) model download anyway, so they ride
// the same flow. The official runtime package from NuGet (a nupkg is a
// zip) is verified end to end: streamed SHA-256 on the archive, then a
// pinned per-file hash on each extracted library. Installs next to the
// bundled whisper natives (runtimes/linux-x64) and is resolved from there
// by name (RegisterNativeResolver) — usable immediately, no restart. App
// updates swap only the binary, so an installed pack survives them;
// deleting the whole app folder removes it and the pack shows as
// downloadable again.
public static class SttEnginePack
{
    public const string Id = "parakeet-engine";
    public const string DisplayName = "Parakeet engine (ONNX runtime)";
    public const string License = "Apache-2.0 / MIT";
    public const string Attribution = "sherpa-onnx (Apache-2.0, k2-fsa); ONNX Runtime (MIT, Microsoft)";

    private const string NupkgUrl =
        "https://api.nuget.org/v3-flatcontainer/org.k2fsa.sherpa.onnx.runtime.linux-x64/1.13.5/org.k2fsa.sherpa.onnx.runtime.linux-x64.1.13.5.nupkg";
    private const long NupkgSizeBytes = 10_517_694;
    private const string NupkgSha256 = "cd2119ef7d43e32d9fd172555640a4d8f3597b1a5f81af44cc616f4a1dfec955";
    private const string ArchivePrefix = "runtimes/linux-x64/native/";

    private static readonly (string Name, long Size, string Sha256)[] Files =
    {
        ("libonnxruntime.so", 26_407_985, "c85f471e1bd5059a4556038f7f5288fa41141647613688452ae7de4879150903"),
        ("libsherpa-onnx-c-api.so", 5_097_376, "70ecdb45b1e0c1fe959ff4a228362386b23eef4b4332168912e031487024799d"),
    };

    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };

    public static void SetUserAgent(string ua) =>
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);

    public static string InstallDir => SttPaths.NativeDir;

    // The download size shown in the UI (the extracted footprint is ~31 MB).
    public static long SizeBytes => NupkgSizeBytes;

    public static bool IsInstalled() =>
        Files.All(f => new FileInfo(Path.Combine(InstallDir, f.Name)) is { Exists: true } fi && fi.Length == f.Size);

    private static int _resolverRegistered;

    // sherpa-onnx's managed wrapper imports "sherpa-onnx-c-api", which the
    // .NET loader would only find beside the executable. Resolve it from
    // the pack's folder instead, loading ONNX Runtime from the same place
    // first so that dependency is satisfied before the linker asks.
    public static void RegisterNativeResolver()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) != 0) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SherpaOnnx.OfflineRecognizer).Assembly, (name, _, _) =>
            {
                if (!name.Contains("sherpa-onnx-c-api", StringComparison.Ordinal)) return IntPtr.Zero;
                var api = Path.Combine(InstallDir, "libsherpa-onnx-c-api.so");
                if (!File.Exists(api)) return IntPtr.Zero; // default probing (beside the executable)
                var ort = Path.Combine(InstallDir, "libonnxruntime.so");
                if (File.Exists(ort)) NativeLibrary.TryLoad(ort, out _);
                return NativeLibrary.Load(api);
            });
        }
        catch (Exception ex) { ErrorLog.WriteEntry("SttEnginePack.RegisterNativeResolver", ex); }
    }

    public static async Task<(bool Ok, string? Error)> DownloadAsync(
        Action<long, long> onProgress, CancellationToken ct = default)
    {
        var tempNupkg = Path.Combine(Path.GetTempPath(), $"chatterbox-engine-pack-{Guid.NewGuid():N}.nupkg");
        try
        {
            using (var response = await Http.GetAsync(NupkgUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                SttDownload.EnsureFreeSpace(Path.GetTempPath(), NupkgSizeBytes);
                SttDownload.EnsureFreeSpace(InstallDir, NupkgSizeBytes * 4);
                using var sha = SHA256.Create();
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = File.Create(tempNupkg))
                {
                    await SttDownload.CopyAsync(source, target, sha, received => onProgress(received, NupkgSizeBytes), ct);
                }

                var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (hash != NupkgSha256)
                    return (false, "Engine pack: package SHA-256 mismatch — download corrupt or source changed");
            }

            Directory.CreateDirectory(InstallDir);
            using (var zip = ZipFile.OpenRead(tempNupkg))
            {
                foreach (var file in Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.GetEntry(ArchivePrefix + file.Name);
                    if (entry == null)
                        return (false, $"Engine pack: {file.Name} missing from the package");

                    var partial = Path.Combine(InstallDir, file.Name + ".partial");
                    entry.ExtractToFile(partial, overwrite: true);

                    if (new FileInfo(partial).Length != file.Size ||
                        SttModelManager.ComputeSha256(partial) != file.Sha256)
                    {
                        File.Delete(partial);
                        return (false, $"Engine pack: {file.Name} failed verification");
                    }
                    File.Move(partial, Path.Combine(InstallDir, file.Name), overwrite: true);
                }
            }
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "download cancelled");
        }
        catch (Exception ex)
        {
            return (false, $"Engine pack: download failed — {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempNupkg)) File.Delete(tempNupkg); } catch { }
        }
    }

    // Re-hash installed files against the pins (the Verify action's coverage).
    public static (int Ok, List<string> Bad) VerifyFiles()
    {
        int ok = 0;
        var bad = new List<string>();
        foreach (var f in Files)
        {
            var path = Path.Combine(InstallDir, f.Name);
            if (File.Exists(path) && new FileInfo(path).Length == f.Size &&
                SttModelManager.ComputeSha256(path) == f.Sha256) ok++;
            else bad.Add($"Parakeet engine ({f.Name})");
        }
        return (ok, bad);
    }

    public static bool Delete()
    {
        try
        {
            foreach (var f in Files)
            {
                var path = Path.Combine(InstallDir, f.Name);
                if (File.Exists(path)) File.Delete(path);
            }
            return true;
        }
        catch { return false; }
    }
}
