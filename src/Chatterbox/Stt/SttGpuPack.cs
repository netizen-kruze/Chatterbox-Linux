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

// Optional CUDA acceleration for the Whisper engine, downloaded on demand so
// release packages stay CPU-only. Three parts make it up, each a zip fetched
// from its publisher, verified per file and placed in the app's own
// runtimes/cuda/linux-x64/ folder — exactly where Whisper.net's loader
// probes first, so no loader configuration is needed:
//
//  1. whisper.cpp's CUDA build, from the official Whisper.net.Runtime.Cuda.Linux
//     package on NuGet (a nupkg is a zip).
//  2. The CUDA 13 runtime that build links against (libcudart.so.13) and
//  3. cuBLAS (libcublas.so.13 + libcublasLt.so.13), from NVIDIA's own wheels
//     on PyPI (a wheel is a zip). The driver does not ship these and nobody
//     has them unless they installed the CUDA toolkit; before this they were
//     the reason a Fedora machine with the pack "didn't detect CUDA". A
//     system-wide CUDA 13 runtime is used instead when one is installed.
//
// Whisper.net decides whether CUDA is usable by asking for "libcudart.so.13"
// by bare name, which the loader only finds in the system folders — or among
// libraries already loaded under that soname. PreloadRuntime therefore loads
// the three runtime libraries by full path (dependencies first) before any
// Whisper.net native call; the pack's libggml-cuda then resolves the same
// sonames to the copies already in the process.
//
// The driver's own libcuda.so.1 cannot be downloaded — it must match the
// kernel module. On Fedora it lives in RPM Fusion's
// xorg-x11-drv-nvidia-cuda-libs, which the driver package only recommends;
// DriverGapNote names it when the module is loaded but the library is not.
//
// Takes effect on the next app start (natives load once per process). App
// updates swap only the binary, so an installed pack survives them; deleting
// the whole app folder removes it and the pack shows as downloadable again.
public static class SttGpuPack
{
    public const string Id = "cuda-gpu-pack";
    public const string DisplayName = "GPU acceleration for Whisper (CUDA)";
    public const string License = "MIT (whisper.cpp) + NVIDIA EULA (CUDA runtime)";
    public const string Attribution = "whisper.cpp CUDA build packaged by Whisper.net; CUDA runtime and cuBLAS by NVIDIA";

    // One file inside a part's archive: where it is in the zip, what it is
    // called once installed, and what it must hash to.
    public sealed record PackFile(string Entry, string Name, long Size, string Sha256);

    // One downloadable archive. Runtime parts are skipped when the system
    // already provides a CUDA 13 runtime.
    public sealed record PackPart(string Id, string Label, string Url, long Size, string Sha256, PackFile[] Files, bool Runtime)
    {
        public long ExtractedBytes => Files.Sum(f => f.Size);
    }

    private static PackFile F(string prefix, string name, long size, string sha256) => new(prefix + name, name, size, sha256);

    public static readonly PackPart WhisperCuda = new(
        "whisper-cuda", "whisper.cpp CUDA build",
        "https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.cuda.linux/1.9.1/whisper.net.runtime.cuda.linux.1.9.1.nupkg",
        143_915_614, "502375e1c8cf278c7c2159ca429d1102fc7e51323b893b2ceb92160e4c130184",
        new[]
        {
            F("build/linux-x64/", "libggml-base-whisper.so", 877_392, "def814632d7d30a85bb8a98a6d298c59e208d9f7e12892b62fab6ed50d70bf61"),
            F("build/linux-x64/", "libggml-cpu-whisper.so", 996_008, "d0cb40cda370e055579f2641068db54359c0d52c096d7646e7d35469a0b9c3f6"),
            F("build/linux-x64/", "libggml-cuda-whisper.so", 167_065_880, "f98fdc3dcd46dfededf1d9b0c0722291eba83e43ad761750b7823a6564b8fede"),
            F("build/linux-x64/", "libggml-whisper.so", 55_184, "c05de815f666fb58c13c5ff0b324688e6bd1802c09fea144cb92cece3a0f0819"),
            F("build/linux-x64/", "libwhisper.so", 610_496, "17d0d4650a8d0971a8c296a3392871aa4e08d2691b470714d40ad506b9ac3f61"),
        },
        Runtime: false);

    public const string LicenseFileName = "NVIDIA-CUDA-LICENSE.txt";

    // NVIDIA's CUDA runtime wheel (nvidia-cuda-runtime 13.4.49 on PyPI): the
    // runtime library plus NVIDIA's license text, kept next to it.
    public static readonly PackPart CudaRuntime = new(
        "cuda-runtime", "CUDA 13 runtime",
        "https://files.pythonhosted.org/packages/e6/cb/b5959bff298d114338aaf4bface538ca9f2a721c439618fb252162f6192e/nvidia_cuda_runtime-13.4.49-py3-none-manylinux2014_x86_64.manylinux_2_17_x86_64.whl",
        2_495_545, "0b228cc646acda27e9b512b8b94417c3ff8b3d24a0ad9b1d6603dc6c2a908046",
        new[]
        {
            F("nvidia/cu13/lib/", "libcudart.so.13", 798_496, "a77eeb711d350a7791a45a07f37cd3b2ae61c71da4db1bf50cc8bd45d33b5b72"),
            new PackFile("nvidia_cuda_runtime-13.4.49.dist-info/licenses/License.txt", LicenseFileName, 59_262,
                "ad6f5853fba0ca0d159d0f58d49ae49830c2f8c93f7a92648b9ce90adb4c6ccd"),
        },
        Runtime: true);

    // NVIDIA's cuBLAS wheel (nvidia-cublas 13.7.0.27 on PyPI). libcublas
    // needs libcublasLt; whisper.cpp's GEMMs need nothing else from it
    // (libnvblas and the headers stay in the archive).
    public static readonly PackPart CuBlas = new(
        "cublas", "cuBLAS 13",
        "https://files.pythonhosted.org/packages/da/b3/358ee3e2237d4d663cd7a50e4dae3b07bbc5c93bf1eab034c17d66b7e428/nvidia_cublas-13.7.0.27-py3-none-manylinux_2_27_x86_64.whl",
        440_472_026, "481adf76a6b7585a7a4cd5f53298589abe207415dd584887751b99baa85491d4",
        new[]
        {
            F("nvidia/cu13/lib/", "libcublasLt.so.13", 544_944_760, "39749400e4f4afe82f514a1c2d2870255b7de1b2b4cc143fef928a6b5fa3768a"),
            F("nvidia/cu13/lib/", "libcublas.so.13", 57_619_096, "32aab49190032659ecc06b11f72a7420d458ab9836bf3358f75c0d6f935ecbe8"),
        },
        Runtime: true);

    public static readonly PackPart[] Parts = { WhisperCuda, CudaRuntime, CuBlas };

    // The runtime libraries in dependency order: each must be in the process
    // (by soname) before the next one asks for it. cudart first because
    // Whisper.net's own CUDA check asks for it by name.
    public static readonly string[] RuntimeLoadOrder = { "libcudart.so.13", "libcublasLt.so.13", "libcublas.so.13" };

    // What a system-wide CUDA 13 runtime (NVIDIA's cuda-toolkit or the
    // cuda-cudart / libcublas packages) must provide for the pack to skip its own copy.
    public static string[] CudaRuntimeLibraries => RuntimeLoadOrder;

    // Fedora's NVIDIA driver package only recommends the sub-package with the
    // driver-side CUDA library; without it no CUDA program can start.
    public const string DriverPackageAdvice = "sudo dnf install xorg-x11-drv-nvidia-cuda-libs";

    // A connect that never answers gives up after 30 s; a transfer that
    // stalls is cut by SttDownload's idle timeout, never by a total one.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };

    // Identifies the app to the download hosts — see SttModelManager.SetUserAgent.
    public static void SetUserAgent(string ua) =>
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);

    public static string InstallDir => SttPaths.CudaDir;

    // ── state ──────────────────────────────────────────────────────

    private static bool FilePresent(PackFile f) =>
        new FileInfo(Path.Combine(InstallDir, f.Name)) is { Exists: true } fi && fi.Length == f.Size;

    public static bool PartInstalled(PackPart part) => part.Files.All(FilePresent);

    // The whisper.cpp CUDA natives are there (size check only, as before).
    public static bool WhisperInstalled() => PartInstalled(WhisperCuda);

    // The pack's own copy of the CUDA 13 runtime is complete.
    public static bool BundledRuntimeInstalled() => Parts.Where(p => p.Runtime).All(PartInstalled);

    // The system provides a CUDA 13 runtime (never answered by loading
    // anything — see LinuxHost.LibraryPresent).
    public static bool SystemRuntimePresent() =>
        !OperatingSystem.IsLinux() || CudaRuntimeLibraries.All(LinuxHost.LibraryPresent);

    // Can libggml-cuda's runtime dependencies be satisfied right now?
    public static bool CudaRuntimePresent() => BundledRuntimeInstalled() || SystemRuntimePresent();

    // "Installed" means it can actually work: natives plus a runtime.
    public static bool IsInstalled() => WhisperInstalled() && CudaRuntimePresent();

    // Which parts a download fetches now: what is missing, minus the runtime
    // parts when the system already provides a CUDA 13 runtime.
    internal static List<PackPart> Plan(Func<PackPart, bool> partInstalled, bool systemRuntime)
    {
        var plan = new List<PackPart>();
        foreach (var part in Parts)
        {
            if (partInstalled(part)) continue;
            if (part.Runtime && systemRuntime) continue;
            plan.Add(part);
        }
        return plan;
    }

    private static List<PackPart> Plan() => Plan(PartInstalled, SystemRuntimePresent());

    // The download size shown in the UI: what a download would fetch now, or
    // the footprint of what is installed.
    public static long SizeBytes
    {
        get
        {
            var plan = Plan();
            return plan.Count > 0 ? plan.Sum(p => p.Size) : Parts.Where(PartInstalled).Sum(p => p.Size);
        }
    }

    public static bool DriverModuleLoaded => OperatingSystem.IsLinux() && File.Exists("/proc/driver/nvidia/version");
    public static bool DriverLibraryPresent => LinuxHost.LibraryPresent("libcuda.so.1");

    // "" when there is nothing to say; otherwise the one sentence a Fedora
    // user needs when the NVIDIA kernel module is loaded but the driver's
    // CUDA library is not installed (the driver package only recommends it).
    public static string DriverGapNote() => DriverGapNote(DriverModuleLoaded, DriverLibraryPresent);

    internal static string DriverGapNote(bool moduleLoaded, bool libraryPresent) =>
        moduleLoaded && !libraryPresent
            ? "The NVIDIA driver is loaded, but its CUDA library (libcuda.so.1) isn't installed — run " +
              DriverPackageAdvice + " and restart Chatterbox."
            : "";

    // What to append to a message that promises acceleration when the
    // natives are there but no runtime is; "" otherwise.
    public static string RuntimeNote() =>
        CudaRuntimePresent()
            ? ""
            : ". It still needs its CUDA runtime part — download GPU acceleration again on the Models screen to fetch it";

    // ── loading ────────────────────────────────────────────────────

    private static int _preloaded;
    private static readonly List<IntPtr> Handles = new(); // kept for the life of the process
    public static string RuntimeStatus { get; private set; } = "not checked";

    // The complete pack was in place when this process started (natives
    // plus a runtime): whisper.cpp had every chance to pick CUDA, so a CPU
    // engine now is a loader verdict, not something a restart fixes.
    public static bool ArmedAtStartup { get; private set; }

    // Loads the CUDA 13 runtime into the process by full path, dependencies
    // first, so that Whisper.net's bare-name lookup of libcudart.so.13 and
    // libggml-cuda's DT_NEEDED entries resolve to these copies. Bundled
    // libraries win; a system runtime is loaded by its real path too, so
    // "present" and "loadable" mean the same thing (a toolkit under
    // /usr/local/cuda that ldconfig doesn't know about still works). Once
    // per process, before any Whisper.net native call; never unloads.
    public static void PreloadRuntime()
    {
        if (Interlocked.Exchange(ref _preloaded, 1) != 0) return;
        if (!OperatingSystem.IsLinux()) { RuntimeStatus = "n/a"; return; }
        if (!WhisperInstalled()) { RuntimeStatus = "GPU pack not installed"; return; }
        bool bundled = BundledRuntimeInstalled();
        if (!bundled && !SystemRuntimePresent())
        {
            RuntimeStatus = "CUDA 13 runtime missing (libcudart.so.13, libcublas.so.13) — download GPU acceleration again";
            return;
        }
        // The driver library first, by its real path: cudart and libggml-cuda
        // ask for "libcuda.so.1" by name, which only works when the loader
        // cache knows it — a WSL or /usr/local layout without an ld.so.conf
        // entry does not. Missing altogether is reported by Status(), not here.
        var driver = LinuxHost.LibraryPath("libcuda.so.1");
        if (driver != null && NativeLibrary.TryLoad(driver, out var driverHandle)) Handles.Add(driverHandle);
        foreach (var name in RuntimeLoadOrder)
        {
            var path = bundled ? Path.Combine(InstallDir, name) : LinuxHost.LibraryPath(name);
            if (path != null && NativeLibrary.TryLoad(path, out var handle))
            {
                Handles.Add(handle);
                continue;
            }
            RuntimeStatus = $"{(bundled ? "bundled" : "system")} CUDA 13 runtime failed to load: {name}" +
                            (path == null ? " (path unknown)" : $" from {path}");
            ErrorLog.WriteNote("SttGpuPack.PreloadRuntime", RuntimeStatus);
            return;
        }
        RuntimeStatus = bundled ? "bundled CUDA 13 runtime loaded" : "system CUDA 13 runtime loaded";
        ArmedAtStartup = true;
    }

    // One line for the boot log: the CUDA verdict, the pack and the runtime
    // as this start saw them.
    public static string Status()
    {
        if (!OperatingSystem.IsLinux()) return "n/a";
        _ = SttHardwareTier.Detect();
        return $"{SttHardwareTier.CudaVerdict}; pack {(WhisperInstalled() ? "installed" : "not installed")}; runtime: {RuntimeStatus}";
    }

    // ── download ───────────────────────────────────────────────────

    // (receivedBytes, totalBytes) progress across every part still needed.
    public static async Task<(bool Ok, string? Error)> DownloadAsync(
        Action<long, long> onProgress, CancellationToken ct = default)
    {
        var plan = Plan();
        if (plan.Count == 0) return (true, null);
        long total = plan.Sum(p => p.Size);
        long done = 0;
        try
        {
            Directory.CreateDirectory(InstallDir);
            SttDownload.EnsureFreeSpace(Path.GetTempPath(), plan.Max(p => p.Size));
            SttDownload.EnsureFreeSpace(InstallDir, plan.Sum(p => p.ExtractedBytes) + 64_000_000);
        }
        catch (Exception ex)
        {
            return (false, $"GPU pack: {ex.Message}");
        }
        foreach (var part in plan)
        {
            long offset = done;
            var (ok, error) = await FetchPartAsync(part, received => onProgress(offset + received, total), ct);
            if (!ok) return (false, error);
            done += part.Size;
        }
        return (true, null);
    }

    // The archive is staged in the install folder itself, not /tmp: on
    // Fedora /tmp is RAM, and 440 MB there is 440 MB of memory. A network
    // failure keeps the staged file so the next attempt resumes it
    // (SttDownload.ResumableDownloadAsync); a bad hash or a cancel drops it.
    private static async Task<(bool Ok, string? Error)> FetchPartAsync(
        PackPart part, Action<long> onProgress, CancellationToken ct)
    {
        var temp = Path.Combine(InstallDir, part.Id + ".download");
        bool keepTemp = false;
        try
        {
            Directory.CreateDirectory(InstallDir);
            using (var sha = SHA256.Create())
            {
                long received = await SttDownload.ResumableDownloadAsync(Http, part.Url, temp, part.Size, sha, onProgress, ct);
                var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (received != part.Size || hash != part.Sha256)
                    return (false, $"GPU pack ({part.Label}): package SHA-256 mismatch — download corrupt or source changed");
            }

            using (var zip = ZipFile.OpenRead(temp))
            {
                foreach (var file in part.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.GetEntry(file.Entry);
                    if (entry == null)
                        return (false, $"GPU pack ({part.Label}): {file.Name} missing from the package");

                    var partial = Path.Combine(InstallDir, file.Name + ".partial");
                    entry.ExtractToFile(partial, overwrite: true);

                    if (new FileInfo(partial).Length != file.Size ||
                        SttModelManager.ComputeSha256(partial) != file.Sha256)
                    {
                        File.Delete(partial);
                        return (false, $"GPU pack ({part.Label}): {file.Name} failed verification");
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
            keepTemp = true;
            return (false, $"GPU pack ({part.Label}): download failed — {ex.Message} (a retry continues where it stopped)");
        }
        finally
        {
            try { if (!keepTemp && File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    // Re-hash every installed pack file against the pinned hashes — the
    // Verify action's coverage of the natives (install detection is
    // size-only). A part that is absent altogether is not the user's problem
    // (a system runtime stands in for it); a part with any file present must
    // be whole.
    public static (int Ok, List<string> Bad) VerifyFiles()
    {
        int ok = 0;
        var bad = new List<string>();
        foreach (var part in Parts)
        {
            if (!part.Files.Any(f => File.Exists(Path.Combine(InstallDir, f.Name)))) continue;
            foreach (var f in part.Files)
            {
                var path = Path.Combine(InstallDir, f.Name);
                if (File.Exists(path) && new FileInfo(path).Length == f.Size &&
                    SttModelManager.ComputeSha256(path) == f.Sha256) ok++;
                else bad.Add($"GPU acceleration ({f.Name})");
            }
        }
        return (ok, bad);
    }

    // Removes every pack file, any staged or half-extracted leftover, and the
    // folder itself when nothing else is in it.
    public static bool Delete()
    {
        try
        {
            if (!Directory.Exists(InstallDir)) return true;
            foreach (var f in Parts.SelectMany(p => p.Files))
            {
                var path = Path.Combine(InstallDir, f.Name);
                if (File.Exists(path)) File.Delete(path);
            }
            foreach (var pattern in new[] { "*.partial", "*.download" })
                foreach (var stray in Directory.EnumerateFiles(InstallDir, pattern)) File.Delete(stray);
            if (!Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir);
            return true;
        }
        catch { return false; }
    }
}
