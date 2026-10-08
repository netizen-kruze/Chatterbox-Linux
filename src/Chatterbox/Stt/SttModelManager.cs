using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Chatterbox.Stt;

public sealed record SttModelFile(string FileName, long SizeBytes, string Sha256, string Url);

// A model is one or more files, all landing in ModelDir\Subdir ("" = the
// model dir itself). Whisper models are single ggml files; Parakeet is an
// ONNX transducer bundle (encoder/decoder/joiner/tokens).
public sealed record SttModelInfo(
    string Id,
    string DisplayName,
    string Subdir,
    IReadOnlyList<SttModelFile> Files,
    string License,
    string Attribution)
{
    public long SizeBytes => Files.Sum(f => f.SizeBytes);
    public string PrimaryFileName => Files[0].FileName;
}

// The known-model catalog. Hashes and sizes are the official Hugging Face LFS
// SHA-256 values, each verified against a real download before being embedded
// here. English-focused models first.
public static class SttModelCatalog
{
    public const string VadId = "vad";
    public const string ParakeetId = "parakeet-tdt-0.6b-v2";
    public const string ParakeetSubdir = "parakeet-tdt-0.6b-v2-int8";

    private const string WhisperAttribution = "OpenAI Whisper (MIT), ggml conversion by ggerganov/whisper.cpp";
    private const string WhisperBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    // Revision-pinned so the embedded hashes can never drift from the source.
    private const string ParakeetBase = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/1ab9323565ddb038682214b292f588070a538ce2/";

    private static SttModelInfo Whisper(string id, string file, string name, long size, string sha) =>
        new(id, name, "", new[] { new SttModelFile(file, size, sha, WhisperBase + file) }, "MIT", WhisperAttribution);

    public static readonly SttModelInfo[] Models =
    {
        Whisper("tiny.en", "ggml-tiny.en.bin", "Whisper tiny.en",
            77_704_715, "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f"),
        Whisper("tiny.en-q5", "ggml-tiny.en-q5_1.bin", "Whisper tiny.en (q5)",
            32_166_155, "c77c5766f1cef09b6b7d47f21b546cbddd4157886b3b5d6d4f709e91e66c7c2b"),
        Whisper("base.en", "ggml-base.en.bin", "Whisper base.en",
            147_964_211, "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002"),
        Whisper("base.en-q5", "ggml-base.en-q5_1.bin", "Whisper base.en (q5)",
            59_721_011, "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f"),
        Whisper("small.en", "ggml-small.en.bin", "Whisper small.en",
            487_614_201, "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d"),
        Whisper("small.en-q5", "ggml-small.en-q5_1.bin", "Whisper small.en (q5)",
            190_098_681, "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30"),
        Whisper("large-v3-turbo-q5", "ggml-large-v3-turbo-q5_0.bin", "Whisper large-v3-turbo (q5)",
            574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
        Whisper("large-v3-turbo", "ggml-large-v3-turbo.bin", "Whisper large-v3-turbo",
            1_624_555_275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
        Whisper("large-v3-q5", "ggml-large-v3-q5_0.bin", "Whisper large-v3 (q5)",
            1_081_140_203, "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1"),
        Whisper("large-v3", "ggml-large-v3.bin", "Whisper large-v3 (full)",
            3_095_033_483, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2"),
        new(ParakeetId, "NVIDIA Parakeet TDT 0.6B v2 (int8)", ParakeetSubdir, new[]
        {
            new SttModelFile("encoder.int8.onnx", 652_184_296,
                "a32b12d17bbbc309d0686fbbcc2987b5e9b8333a7da83fa6b089f0a2acd651ab", ParakeetBase + "encoder.int8.onnx"),
            new SttModelFile("decoder.int8.onnx", 7_257_753,
                "b6bb64963457237b900e496ee9994b59294526439fbcc1fecf705b31a15c6b4e", ParakeetBase + "decoder.int8.onnx"),
            new SttModelFile("joiner.int8.onnx", 1_739_080,
                "7946164367946e7f9f29a122407c3252b680dbae9a51343eb2488d057c3c43d2", ParakeetBase + "joiner.int8.onnx"),
            new SttModelFile("tokens.txt", 9_384,
                "ec182b70dd42113aff6c5372c75cac58c952443eb22322f57bbd7f53977d497d", ParakeetBase + "tokens.txt"),
        }, "CC-BY-4.0", "NVIDIA Parakeet TDT 0.6B v2 (CC-BY-4.0), ONNX export by k2-fsa/sherpa-onnx (csukuangfj)"),
        new(VadId, "Silero VAD v6.2.0", "", new[]
        {
            new SttModelFile("ggml-silero-v6.2.0.bin", 885_098,
                "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987",
                "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v6.2.0.bin"),
        }, "MIT", "Silero VAD (MIT) by snakers4/silero-vad, ggml conversion by ggml-org"),
    };

    public static SttModelInfo? Find(string id) => Models.FirstOrDefault(m => m.Id == id);

    // The voice detector every session needs. SileroVadSegmenter reads its
    // file name from here, so a version change happens in this one entry.
    public static SttModelInfo Vad => Find(VadId)!;
}

// Runtime model download with SHA-256 verification and a license manifest.
// Each file streams to a .partial, is verified, then moved into place
// atomically; the manifest is rewritten after changes.
public sealed class SttModelManager
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) }) { Timeout = Timeout.InfiniteTimeSpan };

    // Identifies the app to the download hosts by name and version.
    // Unset sends no User-Agent header.
    public static void SetUserAgent(string ua) =>
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);

    public string ModelDir { get; }

    // (modelId, receivedBytes, totalBytes) — cumulative across a model's
    // files; raised on the download task.
    public event Action<string, long, long>? OnProgress;

    public SttModelManager(string? modelDir = null)
    {
        ModelDir = modelDir ?? SttPaths.ModelDir;
    }

    public string DirFor(SttModelInfo model) =>
        model.Subdir.Length > 0 ? Path.Combine(ModelDir, model.Subdir) : ModelDir;

    public string PathFor(SttModelInfo model) => Path.Combine(DirFor(model), model.PrimaryFileName);
    public string PathFor(SttModelInfo model, SttModelFile file) => Path.Combine(DirFor(model), file.FileName);

    // Installed = every file present with the exact catalog size (full hash
    // checks are done at download time).
    public bool IsInstalled(SttModelInfo model) =>
        model.Files.All(f => new FileInfo(PathFor(model, f)) is { Exists: true } fi && fi.Length == f.SizeBytes);

    public async Task<(bool Ok, string? Error)> DownloadAsync(string id, CancellationToken ct = default)
    {
        var model = SttModelCatalog.Find(id);
        if (model == null) return (false, $"unknown model '{id}'");

        try { Directory.CreateDirectory(DirFor(model)); }
        catch (Exception ex) { return (false, $"{model.DisplayName}: cannot create {DirFor(model)} — {ex.Message}"); }
        long total = model.SizeBytes;
        long done = model.Files.Where(f => IsFileCurrent(model, f)).Sum(f => f.SizeBytes);

        foreach (var file in model.Files)
        {
            if (IsFileCurrent(model, file)) continue;

            var (ok, error) = await DownloadFileAsync(model, file, done, total, ct);
            if (!ok) return (false, error);
            done += file.SizeBytes;
        }

        WriteManifest();
        return (true, null);
    }

    private bool IsFileCurrent(SttModelInfo model, SttModelFile file) =>
        new FileInfo(PathFor(model, file)) is { Exists: true } fi && fi.Length == file.SizeBytes;

    private async Task<(bool Ok, string? Error)> DownloadFileAsync(
        SttModelInfo model, SttModelFile file, long doneBefore, long total, CancellationToken ct)
    {
        var finalPath = PathFor(model, file);
        var partialPath = finalPath + ".partial";

        // A network failure keeps the .partial so the next attempt resumes
        // it; a cancel, a bad size or a bad hash discard it.
        bool keepPartial = false;
        try
        {
            SttDownload.EnsureFreeSpace(Path.GetDirectoryName(partialPath)!, file.SizeBytes);
            using var sha = SHA256.Create();
            long received = await SttDownload.ResumableDownloadAsync(Http, file.Url, partialPath, file.SizeBytes, sha,
                got => OnProgress?.Invoke(model.Id, doneBefore + got, total), ct);
            if (received != file.SizeBytes)
                return (false, $"{model.DisplayName} ({file.FileName}): size mismatch ({received} vs {file.SizeBytes} bytes)");

            var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (hash != file.Sha256)
                return (false, $"{model.DisplayName} ({file.FileName}): SHA-256 mismatch — download corrupt or source changed");

            File.Move(partialPath, finalPath, overwrite: true);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, "download cancelled");
        }
        catch (Exception ex)
        {
            keepPartial = true;
            return (false, $"{model.DisplayName} ({file.FileName}): download failed — {ex.Message} (a retry continues where it stopped)");
        }
        finally
        {
            try { if (!keepPartial && File.Exists(partialPath)) File.Delete(partialPath); } catch { }
        }
    }

    public bool Delete(string id)
    {
        var model = SttModelCatalog.Find(id);
        if (model == null) return false;
        try
        {
            foreach (var file in model.Files)
            {
                var path = PathFor(model, file);
                if (File.Exists(path)) File.Delete(path);
            }
            if (model.Subdir.Length > 0)
            {
                var dir = DirFor(model);
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            WriteManifest();
            return true;
        }
        catch { return false; }
    }

    // Voice-detector files an earlier release's catalog named
    // (ggml-silero-v5.1.2.bin before this version): still in the model dir
    // after an update, loadable by nothing, absent from the manifest. Listed
    // for the boot log; removed once the current file is in place.
    public IReadOnlyList<string> StaleVadFiles()
    {
        var vad = SttModelCatalog.Vad;
        var dir = DirFor(vad);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "ggml-silero-*.bin")
            .Select(f => Path.GetFileName(f))
            .Where(n => !string.Equals(n, vad.PrimaryFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Deletes the stale voice-detector files — only once the current one is
    // installed, so a failed download leaves the old file where it was.
    public IReadOnlyList<string> RemoveStaleVadFiles()
    {
        var vad = SttModelCatalog.Vad;
        if (!IsInstalled(vad)) return Array.Empty<string>();
        var removed = new List<string>();
        foreach (var name in StaleVadFiles())
        {
            try
            {
                File.Delete(Path.Combine(DirFor(vad), name));
                removed.Add(name);
            }
            catch { }
        }
        return removed;
    }

    public static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    // models_manifest.json + LICENSES.txt in the model dir: what is installed,
    // where it came from, and under which license (surfaced in the UI).
    public void WriteManifest()
    {
        try
        {
            Directory.CreateDirectory(ModelDir);
            var installed = SttModelCatalog.Models.Where(IsInstalled).ToArray();

            var manifest = new
            {
                generated = DateTime.UtcNow.ToString("o"),
                models = installed.Select(m => new
                {
                    id = m.Id,
                    subdir = m.Subdir,
                    sizeBytes = m.SizeBytes,
                    license = m.License,
                    attribution = m.Attribution,
                    files = m.Files.Select(f => new
                    {
                        file = f.FileName,
                        sizeBytes = f.SizeBytes,
                        sha256 = f.Sha256,
                        source = f.Url,
                    }),
                }),
            };
            File.WriteAllText(Path.Combine(ModelDir, "models_manifest.json"),
                JsonConvert.SerializeObject(manifest, Formatting.Indented));

            var lines = new List<string>
            {
                "Speech-to-text model licenses (Chatterbox)",
                "",
            };
            foreach (var m in installed)
                lines.Add($"{m.DisplayName}: {m.License} — {m.Attribution}");
            File.WriteAllLines(Path.Combine(ModelDir, "LICENSES.txt"), lines);
        }
        catch { }
    }
}
