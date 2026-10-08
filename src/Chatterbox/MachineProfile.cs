using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Chatterbox.Stt;

namespace Chatterbox;

// One line describing the machine — CPU, threads, RAM, GPUs, distribution,
// kernel, desktop session, hardware tier, runtime — for the boot log and
// the speed check, so a report from another machine says what it ran on
// without anyone asking. Read from /proc, /sys, os-release and the
// driver tools once; nothing here touches the network.
public static class MachineProfile
{
    private static readonly Lazy<string> Cached = new(Build);

    public static string Describe() => Cached.Value;

    private static string Build()
    {
        var parts = new List<string>();
        try
        {
            var cpu = Cpu();
            if (cpu.Length > 0) parts.Add(cpu);
            parts.Add($"{Environment.ProcessorCount} threads");
            parts.Add($"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0:0} GB RAM");
            var gpus = Gpus();
            if (gpus.Count > 0) parts.Add("GPU: " + string.Join(", ", gpus));
            parts.Add(Os());
            parts.Add("tier " + SttHardwareTier.Label(SttHardwareTier.Detect()));
            parts.Add($".NET {Environment.Version} {RuntimeInformation.ProcessArchitecture}");
        }
        catch (Exception ex) { parts.Add("profile error: " + ex.Message); }
        return string.Join(" · ", parts);
    }

    private static string Cpu()
    {
        try
        {
            if (!File.Exists("/proc/cpuinfo")) return "";
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (!line.StartsWith("model name", StringComparison.Ordinal)) continue;
                int colon = line.IndexOf(':');
                if (colon > 0) return Regex.Replace(line[(colon + 1)..].Trim(), "\\s+", " ");
            }
        }
        catch { }
        return "";
    }

    // NVIDIA cards through the driver's own tool (name and memory); every
    // display device through lspci, with VRAM from sysfs where the driver
    // exposes it (amdgpu does).
    private static List<string> Gpus()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsLinux()) return list;
        bool viaSmi = false;
        try
        {
            var smi = LinuxHost.Capture("nvidia-smi", new[] { "--query-gpu=name,memory.total", "--format=csv,noheader,nounits" });
            foreach (var line in smi.Split('\n'))
            {
                var parts = line.Split(',');
                if (parts.Length < 2 || parts[0].Trim().Length == 0) continue;
                viaSmi = true;
                list.Add(long.TryParse(parts[1].Trim(), out var mib)
                    ? $"{parts[0].Trim()} ({mib / 1024.0:0} GB)"
                    : parts[0].Trim());
            }
        }
        catch { }
        try
        {
            foreach (var line in LinuxHost.Capture("lspci", new[] { "-mm" }).Split('\n'))
            {
                var f = PciFields(line);
                if (f == null) continue;
                var (slot, cls, vendor, device) = f.Value;
                if (!(cls.Contains("VGA", StringComparison.Ordinal) ||
                      cls.Contains("3D controller", StringComparison.Ordinal) ||
                      cls.Contains("Display controller", StringComparison.Ordinal))) continue;
                if (viaSmi && vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;
                var label = ShortVendor(vendor) + " " + DeviceLabel(device);
                long vram = SysfsVram(slot);
                list.Add(vram > 0 ? $"{label} ({vram / 1073741824.0:0} GB)" : label);
            }
        }
        catch { }
        return list;
    }

    // lspci -mm: slot, then quoted class, vendor, device, subsystem vendor,
    // subsystem device, with unquoted -r/-p revision tokens in between.
    internal static (string Slot, string Class, string Vendor, string Device)? PciFields(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        int space = line.IndexOf(' ');
        if (space <= 0) return null;
        var slot = line[..space];
        var quoted = new List<string>();
        foreach (Match m in Regex.Matches(line[space..], "\"([^\"]*)\"")) quoted.Add(m.Groups[1].Value);
        return quoted.Count < 3 ? null : (slot, quoted[0], quoted[1], quoted[2]);
    }

    // "GB203 [GeForce RTX 5080]" -> "GeForce RTX 5080"
    internal static string DeviceLabel(string device)
    {
        int open = device.LastIndexOf('[');
        return open >= 0 && device.EndsWith(']') ? device[(open + 1)..^1] : device;
    }

    internal static string ShortVendor(string vendor)
    {
        if (vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (vendor.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
            vendor.Contains("AMD", StringComparison.Ordinal)) return "AMD";
        if (vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        int space = vendor.IndexOf(' ');
        return space > 0 ? vendor[..space] : vendor;
    }

    private static long SysfsVram(string slot)
    {
        try
        {
            // lspci prints the PCI domain only when it is not 0000.
            var address = slot.Count(ch => ch == ':') >= 2 ? slot : "0000:" + slot;
            var path = Path.Combine("/sys/bus/pci/devices", address, "mem_info_vram_total");
            if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var bytes)) return bytes;
        }
        catch { }
        return 0;
    }

    private static string Os()
    {
        if (!OperatingSystem.IsLinux()) return Environment.OSVersion.VersionString;
        try
        {
            string pretty = "", name = "", version = "";
            if (File.Exists("/etc/os-release"))
            {
                foreach (var line in File.ReadLines("/etc/os-release"))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line[..eq];
                    var value = line[(eq + 1)..].Trim().Trim('"');
                    if (key == "PRETTY_NAME") pretty = value;
                    else if (key == "NAME") name = value;
                    else if (key == "VERSION_ID") version = value;
                }
            }
            var kernel = File.Exists("/proc/sys/kernel/osrelease") ? File.ReadAllText("/proc/sys/kernel/osrelease").Trim() : "";
            return OsLabel(pretty.Length > 0 ? pretty : (name + " " + version).Trim(), kernel,
                Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "",
                Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "");
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    // "Fedora Linux 44 (Workstation Edition), kernel 6.15.4-200.fc44.x86_64, GNOME on wayland"
    internal static string OsLabel(string distro, string kernel, string sessionType, string desktop)
    {
        var sb = new StringBuilder(distro.Length > 0 ? distro : "Linux");
        if (kernel.Length > 0) sb.Append(", kernel ").Append(kernel);
        if (desktop.Length > 0 || sessionType.Length > 0)
        {
            sb.Append(", ");
            if (desktop.Length > 0) sb.Append(desktop);
            if (desktop.Length > 0 && sessionType.Length > 0) sb.Append(" on ");
            sb.Append(sessionType);
        }
        return sb.ToString();
    }
}
