using Chatterbox;
using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The Linux-specific seams: how the game is recognized, where its log is
// looked for, how microphones are listed, and how the recorder is invoked.
public class LinuxPortTests
{
    [Theory]
    [InlineData("VRChat.exe", true)]
    [InlineData("vrchat.exe", true)]
    [InlineData("VRChat", true)]
    [InlineData("VRChat.exe\n", true)]
    [InlineData("wine64-preloader", false)]
    [InlineData("VRChatHelper", false)]
    [InlineData("", false)]
    public void TheGameIsRecognizedByItsProcessName(string comm, bool expected)
    {
        Assert.Equal(expected, LinuxHost.IsVrchatComm(comm));
    }

    [Fact]
    public void SteamLibraryFoldersAreReadFromTheVdf()
    {
        var vdf =
            "\"libraryfolders\"\n{\n" +
            "\t\"0\"\n\t{\n\t\t\"path\"\t\t\"/home/u/.local/share/Steam\"\n\t\t\"label\"\t\t\"\"\n\t}\n" +
            "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"/mnt/games/SteamLibrary\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"438100\"\t\t\"1234\"\n\t\t}\n\t}\n}\n";
        Assert.Equal(new[] { "/home/u/.local/share/Steam", "/mnt/games/SteamLibrary" }, VrchatLogLocator.ParseLibraryFolders(vdf));
        Assert.Empty(VrchatLogLocator.ParseLibraryFolders(""));
    }

    [Fact]
    public void CandidatesCoverNativeAndFlatpakSteam()
    {
        var all = VrchatLogLocator.Candidates().ToList();
        Assert.Contains(all, c =>
            c.Contains(Path.Combine(".local", "share", "Steam"), StringComparison.Ordinal) &&
            c.Contains(VrchatLogLocator.SteamAppId, StringComparison.Ordinal) &&
            c.EndsWith(Path.Combine("VRChat", "VRChat"), StringComparison.Ordinal));
        Assert.Contains(all, c => c.Contains("com.valvesoftware.Steam", StringComparison.Ordinal));
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.NotEmpty(VrchatLogLocator.Find());
    }

    [Fact]
    public void PipeWireCaptureNodesAreListedByDescription()
    {
        const string dump = "[" +
            "{\"id\": 30, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"media.class\": \"Audio/Sink\", \"node.name\": \"alsa_output.pci-0000_00_1f.3.analog-stereo\", \"node.description\": \"Built-in Audio Analog Stereo\"}}}," +
            "{\"id\": 31, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"media.class\": \"Audio/Source\", \"node.name\": \"alsa_input.usb-Blue_Yeti-00.analog-stereo\", \"node.description\": \"Yeti Stereo Microphone\"}}}," +
            "{\"id\": 32, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"media.class\": \"Audio/Source/Virtual\", \"node.name\": \"echo-cancel-source\", \"node.nick\": \"Echo-cancelled mic\"}}}," +
            "{\"id\": 33, \"type\": \"PipeWire:Interface:Port\", \"info\": {\"props\": {\"port.name\": \"capture_FL\"}}}," +
            "{\"id\": 34, \"type\": \"PipeWire:Interface:Node\", \"info\": {\"props\": {\"media.class\": \"Audio/Source\", \"node.name\": \"alsa_output.pci-0000_00_1f.3.analog-stereo.monitor\"}}}" +
            "]";
        var devices = SttAudioDevices.ParsePwDump(dump);
        Assert.Equal(2, devices.Count);
        Assert.Equal("Yeti Stereo Microphone", devices[0].Name);
        Assert.Equal("alsa_input.usb-Blue_Yeti-00.analog-stereo", devices[0].Target);
        Assert.Equal("Echo-cancelled mic", devices[1].Name);
        Assert.Equal("echo-cancel-source", devices[1].Target);
        Assert.Empty(SttAudioDevices.ParsePwDump("not json"));
    }

    [Fact]
    public void PulseAudioSourcesSkipMonitors()
    {
        var text = "48\talsa_input.pci-0000_00_1f.3.analog-stereo\tPipeWire\ts32le 2ch 48000Hz\tSUSPENDED\n" +
                   "47\talsa_output.pci-0000_00_1f.3.analog-stereo.monitor\tPipeWire\ts32le 2ch 48000Hz\tIDLE\n";
        var devices = SttAudioDevices.ParsePactlShort(text);
        Assert.Single(devices);
        Assert.Equal("alsa_input.pci-0000_00_1f.3.analog-stereo", devices[0].Target);
        Assert.Empty(SttAudioDevices.ParsePactlShort(""));
    }

    [Fact]
    public void RecorderCommandsCarryTheTargetAndTheRawFormat()
    {
        var cmds = SttAudioDevices.CaptureCommands("alsa_input.usb-mic").ToList();
        Assert.Equal(new[] { "pw-record", "parec", "arecord" }, cmds.Select(c => c.Label));
        Assert.Contains("--target=alsa_input.usb-mic", cmds[0].Args);
        Assert.Contains("--rate=16000", cmds[0].Args);
        Assert.Contains("--channels=1", cmds[0].Args);
        Assert.Contains("--raw", cmds[0].Args);
        Assert.Equal("-", cmds[0].Args[^1]);
        Assert.Contains("--device=alsa_input.usb-mic", cmds[1].Args);
        Assert.DoesNotContain(cmds[2].Args, a => a.Contains("usb-mic", StringComparison.Ordinal));
        Assert.DoesNotContain(SttAudioDevices.CaptureCommands(null).First().Args, a => a.StartsWith("--target", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultInputIsAlwaysFirstAndHasNoTarget()
    {
        var inputs = SttAudioDevices.GetInputs();
        Assert.Equal(SttAudioDevices.DefaultName, inputs[0].Name);
        Assert.Null(SttAudioDevices.TargetFor(0));
        Assert.Null(SttAudioDevices.TargetFor(-1));
        Assert.Null(SttAudioDevices.TargetFor(99));
    }

    [Fact]
    public void LdconfigCacheLinesYieldSonames()
    {
        var text = "1234 libs found in cache `/etc/ld.so.cache'\n" +
                   "\tlibwebkit2gtk-4.1.so.0 (libc6,x86-64) => /usr/lib64/libwebkit2gtk-4.1.so.0\n" +
                   "\tlibgtk-3.so.0 (libc6,x86-64) => /usr/lib64/libgtk-3.so.0\n" +
                   "Cache generated by: ldconfig (GNU libc) 2.41\n";
        var parsed = LinuxHost.ParseLdconfig(text).ToArray();
        Assert.Equal(new[] { "libwebkit2gtk-4.1.so.0", "libgtk-3.so.0" }, parsed.Select(e => e.Name).ToArray());
        Assert.Equal("/usr/lib64/libgtk-3.so.0", parsed[1].Path);
        Assert.Empty(LinuxHost.ParseLdconfig(""));
        if (!OperatingSystem.IsLinux())
        {
            Assert.False(LinuxHost.LibraryPresent("libgtk-3.so.0"));
            Assert.Null(LinuxHost.LibraryPath("libgtk-3.so.0"));
        }
    }

    [Fact]
    public void SteamOverlayPreloadIsStrippedFromHelperProcesses()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("true");
        psi.Environment["LD_PRELOAD"] = "/home/u/.local/share/Steam/ubuntu12_64/gameoverlayrenderer.so";
        LinuxHost.StripSteamPreload(psi);
        Assert.False(psi.Environment.ContainsKey("LD_PRELOAD"));

        psi.Environment["LD_PRELOAD"] = "/usr/lib64/libjemalloc.so.2";
        LinuxHost.StripSteamPreload(psi);
        Assert.True(psi.Environment.ContainsKey("LD_PRELOAD"));
    }

    [Fact]
    public void RuntimeFoldersHangOffTheRuntimeRoot()
    {
        Assert.EndsWith(Path.Combine("runtimes", "linux-x64"), SttPaths.NativeDir);
        Assert.EndsWith(Path.Combine("runtimes", "cuda", "linux-x64"), SttPaths.CudaDir);
        Assert.StartsWith(SttPaths.RuntimeRoot, SttPaths.NativeDir, StringComparison.Ordinal);
        Assert.Equal(SttPaths.NativeDir, SttEnginePack.InstallDir);
        Assert.Equal(SttPaths.CudaDir, SttGpuPack.InstallDir);
    }
}
