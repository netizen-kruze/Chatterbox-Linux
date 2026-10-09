using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// A set of native libraries fetched on demand from an official NuGet
// package (a nupkg is a zip): the archive's SHA-256 is checked while it
// streams, then every extracted file is checked against its own pin before
// it is moved into place. The translation runtime ships this way — its
// natives are too large to embed in the binary and useless without the
// (larger) model download anyway — exactly like the Parakeet engine pack.
// Files land under <runtime root>/runtimes/linux-x64/native/<variant>/,
// the layout the LLamaSharp loader probes under its search directory
// (LlamaTranslator points it at the same root), so no further loader
// configuration is needed. The runtime root is the data folder, like the
// Parakeet and CUDA packs: an app update swaps only the binary, so an
// installed pack survives it, and --purge removes it with everything else.
public sealed class SttNativePack
{
    public readonly record struct PackFile(string Subdir, string Name, long Size, string Sha256);

    public string Id { get; }
    public string DisplayName { get; }
    public string License { get; }
    public string Attribution { get; }
    public long SizeBytes { get; }
    private readonly string _nupkgUrl;
    private readonly string _nupkgSha256;
    private readonly string _archivePrefix;
    private readonly PackFile[] _files;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };

    public static void SetUserAgent(string ua) =>
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);

    public SttNativePack(string id, string displayName, string license, string attribution,
        string nupkgUrl, long nupkgSize, string nupkgSha256, string archivePrefix, PackFile[] files)
    {
        Id = id; DisplayName = displayName; License = license; Attribution = attribution;
        _nupkgUrl = nupkgUrl; SizeBytes = nupkgSize; _nupkgSha256 = nupkgSha256;
        _archivePrefix = archivePrefix; _files = files;
    }

    public static string InstallRoot => Path.Combine(SttPaths.NativeDir, "native");

    public IReadOnlyList<PackFile> Files => _files;

    private static string PathFor(PackFile f) => Path.Combine(InstallRoot, f.Subdir, f.Name);

    public bool IsInstalled() =>
        _files.All(f => new FileInfo(PathFor(f)) is { Exists: true } fi && fi.Length == f.Size);

    public async Task<(bool Ok, string? Error)> DownloadAsync(Action<long, long> onProgress, CancellationToken ct = default)
    {
        var tempNupkg = Path.Combine(Path.GetTempPath(), $"chatterbox-{Id}-{Guid.NewGuid():N}.nupkg");
        try
        {
            using (var response = await Http.GetAsync(_nupkgUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                SttDownload.EnsureFreeSpace(Path.GetTempPath(), SizeBytes);
                Directory.CreateDirectory(InstallRoot);
                SttDownload.EnsureFreeSpace(InstallRoot, _files.Sum(f => f.Size) + SizeBytes);
                using var sha = SHA256.Create();
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = File.Create(tempNupkg))
                {
                    await SttDownload.CopyAsync(source, target, sha, received => onProgress(received, SizeBytes), ct);
                }
                var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (hash != _nupkgSha256)
                    return (false, $"{DisplayName}: package SHA-256 mismatch — download corrupt or source changed");
            }

            using (var zip = ZipFile.OpenRead(tempNupkg))
            {
                foreach (var file in _files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.GetEntry(_archivePrefix + file.Subdir + "/" + file.Name);
                    if (entry == null) return (false, $"{DisplayName}: {file.Subdir}/{file.Name} missing from the package");

                    var dir = Path.Combine(InstallRoot, file.Subdir);
                    Directory.CreateDirectory(dir);
                    // Written beside and renamed over, like the bundled
                    // natives: a mapped library truncated in place is a SIGBUS
                    // for any other instance that has it loaded.
                    var partial = Path.Combine(dir, file.Name + ".partial");
                    entry.ExtractToFile(partial, overwrite: true);
                    if (new FileInfo(partial).Length != file.Size || SttModelManager.ComputeSha256(partial) != file.Sha256)
                    {
                        File.Delete(partial);
                        return (false, $"{DisplayName}: {file.Name} failed verification");
                    }
                    File.Move(partial, Path.Combine(dir, file.Name), overwrite: true);
                }
            }
            return (true, null);
        }
        catch (OperationCanceledException) { return (false, "download cancelled"); }
        catch (Exception ex) { return (false, $"{DisplayName}: download failed — {ex.Message}"); }
        finally
        {
            try { if (File.Exists(tempNupkg)) File.Delete(tempNupkg); } catch { }
        }
    }

    // Re-hash installed files against the pins (the Verify action's coverage).
    public (int Ok, List<string> Bad) VerifyFiles()
    {
        int ok = 0;
        var bad = new List<string>();
        foreach (var f in _files)
        {
            var path = PathFor(f);
            if (File.Exists(path) && new FileInfo(path).Length == f.Size && SttModelManager.ComputeSha256(path) == f.Sha256) ok++;
            else bad.Add($"{DisplayName} ({f.Subdir}/{f.Name})");
        }
        return (ok, bad);
    }

    public bool Delete()
    {
        try
        {
            foreach (var f in _files)
            {
                var path = PathFor(f);
                if (File.Exists(path)) File.Delete(path);
            }
            foreach (var dir in _files.Select(f => Path.Combine(InstallRoot, f.Subdir)).Distinct())
            {
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("SttNativePack.Delete", ex);
            return false;
        }
    }
}

// The translation runtime (llama.cpp, packaged by LLamaSharp) as two packs:
// the CPU build, which every machine can run (four variants; the loader
// picks the best one for the processor), and the Vulkan build, which runs
// the model on any GPU — NVIDIA, AMD or Intel — through the distribution's
// Vulkan loader (libvulkan.so.1) and the driver's ICD, no CUDA runtime
// needed. Pins are the official 0.27.0 packages on nuget.org (the CPU
// package is the same one the Windows build uses; its linux-x64 entries
// are what is hashed here), file by file. libmtmd (vision) stays in the
// archive: not shipped, not needed.
public static class SttTranslatePacks
{
    private const string Version = "0.27.0";
    private const string Prefix = "LLamaSharpRuntimes/linux-x64/native/";

    public static readonly SttNativePack Cpu = new(
        "translate-engine", "Translation engine (llama.cpp, CPU)", "MIT",
        "llama.cpp (MIT, ggml-org), packaged by LLamaSharp (MIT)",
        $"https://api.nuget.org/v3-flatcontainer/llamasharp.backend.cpu/{Version}/llamasharp.backend.cpu.{Version}.nupkg",
        36_337_071, "5a3416a712c2fe9a4a70f8915f8a7e5b82948f5c74a517a2c3ee29158204bb26", Prefix, new SttNativePack.PackFile[]
        {
            new("avx", "libggml-base.so", 845_104, "dcb256a2759243345044dadca911095db0289ebb7947ff94c969b00784f862f1"),
            new("avx", "libggml-cpu.so", 1_060_696, "a7e950b9bf452615bbc99c3083f8b3266a618ee6fc8a7eaaa64fb1824b24e54b"),
            new("avx", "libggml.so", 55_544, "1ea01d18e42e416d2a520ad6b54c0cb76899d318e67c35daba26c2de37ee5813"),
            new("avx", "libllama.so", 3_274_096, "b9b5413956880c3f157a66b58081d929e8b19d076d515f9784f2794f0748fcf1"),
            new("avx2", "libggml-base.so", 845_104, "dcb256a2759243345044dadca911095db0289ebb7947ff94c969b00784f862f1"),
            new("avx2", "libggml-cpu.so", 1_115_416, "f3a021c92d26d4f06a5618e64e5c68f91faad7c81655c4e523bc8fee2124d506"),
            new("avx2", "libggml.so", 55_544, "1ea01d18e42e416d2a520ad6b54c0cb76899d318e67c35daba26c2de37ee5813"),
            new("avx2", "libllama.so", 3_274_096, "b9b5413956880c3f157a66b58081d929e8b19d076d515f9784f2794f0748fcf1"),
            new("avx512", "libggml-base.so", 845_104, "dcb256a2759243345044dadca911095db0289ebb7947ff94c969b00784f862f1"),
            new("avx512", "libggml-cpu.so", 1_256_304, "ba6f2bf8c4fc0091c3c76f9cea50fd5d2d539add311eb837c2e4eb9c17889884"),
            new("avx512", "libggml.so", 55_544, "1ea01d18e42e416d2a520ad6b54c0cb76899d318e67c35daba26c2de37ee5813"),
            new("avx512", "libllama.so", 3_274_096, "b9b5413956880c3f157a66b58081d929e8b19d076d515f9784f2794f0748fcf1"),
            new("noavx", "libggml-base.so", 845_104, "dcb256a2759243345044dadca911095db0289ebb7947ff94c969b00784f862f1"),
            new("noavx", "libggml-cpu.so", 1_064_792, "79cd63a3a09c53661746074a78c2268f281c529d14c8cbe45e54457544a64fa8"),
            new("noavx", "libggml.so", 55_544, "1ea01d18e42e416d2a520ad6b54c0cb76899d318e67c35daba26c2de37ee5813"),
            new("noavx", "libllama.so", 3_274_096, "b9b5413956880c3f157a66b58081d929e8b19d076d515f9784f2794f0748fcf1"),
        });

    public static readonly SttNativePack Gpu = new(
        "translate-gpu-pack", "GPU acceleration for translation (Vulkan)", "MIT",
        "llama.cpp Vulkan build (MIT, ggml-org), packaged by LLamaSharp (MIT)",
        $"https://api.nuget.org/v3-flatcontainer/llamasharp.backend.vulkan.linux/{Version}/llamasharp.backend.vulkan.linux.{Version}.nupkg",
        20_870_722, "6798d9dddfd1413e4cffb91545ffc91b4b3c4c94e7d077bddf7b80a6b404a351", Prefix, new SttNativePack.PackFile[]
        {
            new("vulkan", "libggml-base.so", 845_104, "dcb256a2759243345044dadca911095db0289ebb7947ff94c969b00784f862f1"),
            new("vulkan", "libggml-vulkan.so", 62_587_680, "87640180882f48b1776c42a7fadaa89e15714131c631f09c8f3b1a251811e049"),
            new("vulkan", "libggml.so", 55_176, "4cf8347c7b721e9d395f1dc0859bcc7c6aa5bad2da62f0063573b79e878af271"),
            new("vulkan", "libllama.so", 3_274_096, "b9b5413956880c3f157a66b58081d929e8b19d076d515f9784f2794f0748fcf1"),
        });

    public static SttNativePack? Find(string id) => id == Cpu.Id ? Cpu : id == Gpu.Id ? Gpu : null;
}
