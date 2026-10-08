using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Chatterbox.Stt;

public enum SttTier
{
    Gpu,       // NVIDIA CUDA usable — whisper runs GPU-accelerated
    CpuHigh,   // strong CPU — base.en comfortably real-time
    CpuLow,    // modest CPU — tiny.en
    CpuMinimal, // very weak hardware — smallest quantized model only
}

// Hardware tier autodetect: usable CUDA -> GPU tier, else CPU tier by
// cores/RAM. Classify() and JudgeCuda() are pure for unit testing; Detect()
// feeds them live machine facts once per process (hardware does not change
// under us).
public static class SttHardwareTier
{
    private static readonly Lazy<SttTier> Detected = new(() =>
        Classify(HasUsableCuda(), Environment.ProcessorCount, TotalRamGB()));

    public static SttTier Detect() => Detected.Value;

    public static SttTier Classify(bool hasCuda, int cores, double ramGB)
    {
        if (hasCuda && ramGB >= 8) return SttTier.Gpu;
        if (cores >= 8 && ramGB >= 8) return SttTier.CpuHigh;
        if (cores >= 4 && ramGB >= 4) return SttTier.CpuLow;
        return SttTier.CpuMinimal;
    }

    // Recommended whisper model id per tier; "" = no recommendation (the
    // host maps it to the smallest quantized model). GPU tier gets
    // large-v3-turbo (q5): near large-v3 accuracy, still far faster than
    // realtime on any CUDA card that clears the tier bar.
    public static string RecommendedModelId(SttTier tier) => tier switch
    {
        SttTier.Gpu => "large-v3-turbo-q5",
        SttTier.CpuHigh => "base.en",
        SttTier.CpuLow => "tiny.en",
        _ => "",
    };

    public static string Label(SttTier tier) => tier switch
    {
        SttTier.Gpu => "GPU (NVIDIA)",
        SttTier.CpuHigh => "CPU (high)",
        SttTier.CpuLow => "CPU (low)",
        _ => "Minimal",
    };

    // Why the CUDA verdict came out the way it did — for the boot log and
    // the Models banner ("" until Detect() has run).
    public static string CudaVerdict { get; private set; } = "";

    // What the GPU pack's CUDA 13 build needs: driver 580 or newer, and a
    // Turing-or-newer card (compute capability 7.5) — CUDA 13 dropped
    // Maxwell, Pascal and Volta.
    public const int MinDriverMajor = 580;
    public const double MinComputeCapability = 7.5;

    // For the Models banner: why GPU acceleration isn't offered on a machine
    // that clearly has NVIDIA hardware; "" when there is nothing to explain.
    public static string TierNote()
    {
        if (Detect() == SttTier.Gpu) return "";
        var gap = SttGpuPack.DriverGapNote();
        if (gap.Length > 0) return gap;
        return CudaVerdict.Length > 0 &&
               !CudaVerdict.StartsWith("no NVIDIA", StringComparison.Ordinal) &&
               !CudaVerdict.StartsWith("CUDA usable", StringComparison.Ordinal)
            ? "GPU acceleration not offered: " + CudaVerdict
            : "";
    }

    // Linux: the driver's libcuda.so.1 alone is not enough — RPM Fusion
    // installs it whether or not the kernel module ever loads (Secure Boot
    // without an enrolled key is the classic Fedora case), and it says
    // nothing about the card's generation. Nothing here loads a library
    // (see LinuxHost.LibraryPresent); the facts are files and nvidia-smi.
    private static bool HasUsableCuda()
    {
        if (OperatingSystem.IsLinux())
        {
            var (usable, verdict) = JudgeCuda(
                LinuxHost.LibraryPresent("libcuda.so.1"),
                File.Exists("/proc/driver/nvidia/version"),
                File.Exists("/dev/dxg"),
                NvidiaDriverMajor(),
                NvidiaComputeCapability());
            CudaVerdict = verdict;
            return usable;
        }
        if (!OperatingSystem.IsWindows()) return false;
        if (NativeLibrary.TryLoad("nvcuda.dll", out var handle))
        {
            NativeLibrary.Free(handle);
            CudaVerdict = "CUDA usable";
            return true;
        }
        CudaVerdict = "no NVIDIA driver";
        return false;
    }

    // Pure: libcuda.so.1 on disk, the kernel module loaded (/proc/driver/nvidia),
    // a WSL guest (/dev/dxg — the driver is the host's), the driver's major
    // version when known, the first GPU's compute capability when nvidia-smi
    // could say. Unknown facts give the card the benefit of the doubt.
    internal static (bool Usable, string Verdict) JudgeCuda(bool libcuda, bool moduleLoaded, bool wsl, int? driverMajor, double? computeCap)
    {
        if (!libcuda)
            return (false, moduleLoaded
                ? "NVIDIA driver loaded but its CUDA library (libcuda.so.1) is not installed"
                : "no NVIDIA driver library");
        if (!moduleLoaded && !wsl)
            return (false, "libcuda.so.1 is installed but the NVIDIA kernel module is not loaded");
        if (driverMajor is { } d && d < MinDriverMajor)
            return (false, $"NVIDIA driver {d} is older than CUDA 13 needs ({MinDriverMajor} or newer)");
        if (computeCap is { } c && c < MinComputeCapability)
            return (false, $"GPU compute capability {c.ToString("0.0", CultureInfo.InvariantCulture)} is below CUDA 13's minimum " +
                           $"({MinComputeCapability.ToString("0.0", CultureInfo.InvariantCulture)}, Turing or newer)");
        var facts = new System.Collections.Generic.List<string>();
        if (driverMajor != null) facts.Add($"driver {driverMajor}");
        if (computeCap != null) facts.Add($"compute {computeCap.Value.ToString("0.0", CultureInfo.InvariantCulture)}");
        return (true, facts.Count == 0 ? "CUDA usable" : $"CUDA usable ({string.Join(", ", facts)})");
    }

    // "NVRM version: NVIDIA UNIX Open Kernel Module for x86_64  580.82.07  Release Build" -> 580
    internal static int? ParseDriverMajor(string text)
    {
        var m = Regex.Match(text ?? "", @"\b(\d{3})\.\d+");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    private static int? NvidiaDriverMajor()
    {
        try
        {
            return File.Exists("/proc/driver/nvidia/version")
                ? ParseDriverMajor(File.ReadAllText("/proc/driver/nvidia/version"))
                : null;
        }
        catch { return null; }
    }

    // nvidia-smi's first line ("8.9"); null when the tool is absent — it
    // lives in xorg-x11-drv-nvidia-cuda, which the driver does not require.
    internal static double? ParseComputeCapability(string text)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            return double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        return null;
    }

    private static double? NvidiaComputeCapability()
    {
        if (!OperatingSystem.IsLinux()) return null;
        return ParseComputeCapability(LinuxHost.Capture("nvidia-smi", new[] { "--query-gpu=compute_cap", "--format=csv,noheader" }));
    }

    private static double TotalRamGB() =>
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;
}
