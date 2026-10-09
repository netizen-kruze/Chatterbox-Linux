using System.Diagnostics;
using Chatterbox;
using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The seams added by the Fedora 44 and stability audits: CUDA usability
// judgement, Steam's runtime environment, load shedding and bounded passes
// in the pipeline, final passes in the pace monitor, crash-record naming.
public class StabilityTests
{
    // ── CUDA usability ─────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, false, null, null, false, "no NVIDIA driver library")]
    [InlineData(false, true, false, null, null, false, "libcuda.so.1")]                 // module loaded, cuda-libs missing
    [InlineData(true, false, false, null, null, false, "kernel module is not loaded")]
    [InlineData(true, false, true, null, null, true, "CUDA usable")]                   // WSL: /dev/dxg, no module
    [InlineData(true, true, false, 570, null, false, "older than CUDA 13")]
    [InlineData(true, true, false, 580, 6.1, false, "compute capability 6.1")]         // Pascal
    [InlineData(true, true, false, 580, 7.5, true, "compute 7.5")]
    [InlineData(true, true, false, 595, 12.0, true, "driver 595")]
    [InlineData(true, true, false, null, null, true, "CUDA usable")]                   // nothing known: benefit of the doubt
    public void CudaIsJudgedFromTheDriverTheModuleAndTheCard(bool libcuda, bool module, bool wsl, int? driver, double? cap, bool usable, string verdictPart)
    {
        var (ok, verdict) = SttHardwareTier.JudgeCuda(libcuda, module, wsl, driver, cap);
        Assert.Equal(usable, ok);
        Assert.Contains(verdictPart, verdict);
    }

    [Fact]
    public void DriverVersionAndComputeCapabilityAreParsedFromTheirTools()
    {
        Assert.Equal(580, SttHardwareTier.ParseDriverMajor("NVRM version: NVIDIA UNIX Open Kernel Module for x86_64  580.82.07  Release Build  (dvs-builder@U22-I3-AE08-13-2)\nGCC version:  gcc version 15.1.1"));
        Assert.Null(SttHardwareTier.ParseDriverMajor(""));
        Assert.Equal(8.9, SttHardwareTier.ParseComputeCapability("8.9\n"));
        Assert.Equal(12.0, SttHardwareTier.ParseComputeCapability("\n12.0\n8.6\n"));   // first GPU
        Assert.Null(SttHardwareTier.ParseComputeCapability(""));
        Assert.Null(SttHardwareTier.ParseComputeCapability("nvidia-smi: command not found"));
    }

    // ── Steam's environment ────────────────────────────────────────

    [Fact]
    public void SteamsRuntimeLibraryPathIsRecognizedAndReplacedForHelpers()
    {
        Assert.True(LinuxHost.IsSteamRuntimeLibraryPath("/home/u/.local/share/Steam/ubuntu12_32/steam-runtime/pinned_libs_32:/usr/lib64"));
        Assert.True(LinuxHost.IsSteamRuntimeLibraryPath("/home/u/.steam/steam/ubuntu12_64/"));
        Assert.False(LinuxHost.IsSteamRuntimeLibraryPath("/opt/cuda/lib64"));
        Assert.False(LinuxHost.IsSteamRuntimeLibraryPath(null));

        var psi = new System.Diagnostics.ProcessStartInfo("true");
        psi.Environment["LD_LIBRARY_PATH"] = "/home/u/.local/share/Steam/ubuntu12_32/steam-runtime/lib";
        psi.Environment["SYSTEM_LD_LIBRARY_PATH"] = "/opt/cuda/lib64";
        LinuxHost.StripSteamPreload(psi);
        Assert.Equal("/opt/cuda/lib64", psi.Environment["LD_LIBRARY_PATH"]);

        psi.Environment["LD_LIBRARY_PATH"] = "/home/u/.local/share/Steam/ubuntu12_32/steam-runtime/lib";
        psi.Environment.Remove("SYSTEM_LD_LIBRARY_PATH");
        LinuxHost.StripSteamPreload(psi);
        Assert.False(psi.Environment.ContainsKey("LD_LIBRARY_PATH"));

        psi.Environment["LD_LIBRARY_PATH"] = "/opt/cuda/lib64";
        LinuxHost.StripSteamPreload(psi);
        Assert.Equal("/opt/cuda/lib64", psi.Environment["LD_LIBRARY_PATH"]);
    }

    // ── crash record naming ────────────────────────────────────────

    [Fact]
    public void TheCrashRecordAsksForTheKernelsFifteenByteProcessName()
    {
        Assert.Equal("Chatterbox-1.5.", CrashRecord.ProcessComm("/home/u/Downloads/Chatterbox-1.5.1-linux-x64"));
        Assert.Equal("Chatterbox", CrashRecord.ProcessComm("/home/u/.local/share/Chatterbox/app/Chatterbox"));
    }

    // ── pace monitor ───────────────────────────────────────────────

    [Fact]
    public void AFinalPassMovesLagButNotLoad()
    {
        var m = new SttPaceMonitor();
        for (int i = 1; i <= 8; i++) m.Record(new SttPassInfo(100, 3000, 300, 0), i * 300);
        double before = m.Load;
        Assert.Equal(SttPaceMonitor.PaceStatus.KeepingUp, m.Status);
        // The utterance-closing pass covers ten seconds after 300 ms of new audio.
        m.Record(new SttPassInfo(2000, 10_000, 300, 0, Final: true), 3000);
        Assert.Equal(before, m.Load, 6);
        Assert.Equal(SttPaceMonitor.PaceStatus.KeepingUp, m.Status);
        Assert.Equal(9, m.Passes);
    }

    // ── pipeline: shedding and bounded passes ──────────────────────

    private sealed class AlwaysSpeech : IVadSegmenter
    {
        public IReadOnlyList<SpeechSegment> Detect(byte[] pcm, int length)
        {
            int ms = length / SttAudio.MsToBytes(1);
            return ms == 0 ? Array.Empty<SpeechSegment>() : new[] { new SpeechSegment(TimeSpan.Zero, TimeSpan.FromMilliseconds(ms)) };
        }
    }

    private sealed class CountingEngine : ISttEngine
    {
        public int MaxLengthMs, Calls;
        public string Name => "counting";
        public bool IsLoaded => true;
        public bool TryLoad(out string? error) { error = null; return true; }
        public void Dispose() { }
        public Task<string> TranscribeAsync(byte[] pcm, int length, CancellationToken ct = default) => Task.FromResult("");
        public Task<SttTranscript> TranscribeTimedAsync(byte[] pcm, int length, CancellationToken ct = default)
        {
            Calls++;
            MaxLengthMs = Math.Max(MaxLengthMs, length / SttAudio.MsToBytes(1));
            return Task.FromResult(new SttTranscript("word", new[] { new SttSpan("word", 0, length / SttAudio.MsToBytes(1)) }));
        }
    }

    private static byte[] Chunk() => new byte[SttAudio.MsToBytes(100)];

    [Fact]
    public async Task ABacklogBeyondOnePassPlusTheLagAllowanceIsShed()
    {
        var engine = new CountingEngine();
        using var pipeline = new SttPipeline(engine, new AlwaysSpeech())
        {
            MaxUtteranceSeconds = 2,
            MaxLagMs = 1000,
            VadTickMs = 300,
            MinInferIntervalMs = 300,
        };
        var shed = new List<int>();
        pipeline.OnShed += s => shed.Add(s);
        // Six seconds queued before the worker gets a turn: one pass may
        // cover two of them, one more second may wait — the rest goes.
        for (int i = 0; i < 60; i++) pipeline.Push(Chunk());
        pipeline.Start();
        for (int i = 0; i < 60; i++) pipeline.Push(Chunk());
        await Task.Delay(500);
        pipeline.Stop();
        Assert.NotEmpty(shed);
        Assert.True(shed[0] >= 2, $"shed {shed[0]} s");
        Assert.True(engine.MaxLengthMs <= 2000, $"a pass covered {engine.MaxLengthMs} ms");
    }

    // Speech on the first look, silence from then on: the utterance closes
    // on the second tick, over whatever the window holds by then.
    private sealed class SpeechThenSilence : IVadSegmenter
    {
        private int _calls;
        public IReadOnlyList<SpeechSegment> Detect(byte[] pcm, int length)
        {
            int ms = length / SttAudio.MsToBytes(1);
            return _calls++ == 0 && ms > 0 ? new[] { new SpeechSegment(TimeSpan.Zero, TimeSpan.FromMilliseconds(ms)) } : Array.Empty<SpeechSegment>();
        }
    }

    [Fact]
    public async Task AnUtteranceClosingPassNeverHandsTheEngineMoreThanOneWindow()
    {
        var engine = new CountingEngine();
        using var pipeline = new SttPipeline(engine, new SpeechThenSilence())
        {
            MaxUtteranceSeconds = 2,
            MaxLagMs = 100_000,   // no shedding here: the cap alone must hold
            VadTickMs = 300,
            MinInferIntervalMs = 100_000,   // no partial passes: the closing pass is the one measured
        };
        pipeline.Start();
        for (int i = 0; i < 5; i++) pipeline.Push(Chunk());      // 0.5 s: speech seen, window open
        await Task.Delay(400);
        for (int i = 0; i < 35; i++) pipeline.Push(Chunk());     // 3.5 s more arrive at once: the window is 4 s when silence closes it
        for (int i = 0; i < 40 && engine.Calls == 0; i++) await Task.Delay(50);
        pipeline.Stop();
        Assert.True(engine.Calls > 0, "the closing pass never ran");
        Assert.True(engine.MaxLengthMs <= 2000, $"a pass covered {engine.MaxLengthMs} ms");
        Assert.False(pipeline.IsRunning);
    }

    [Fact]
    public async Task StopDoesNotTranscribeWhatIsLeftInTheWindow()
    {
        // The flushed text would only reach the chatbox relay, which the
        // owner clears right after Stop — so Stop must not spend seconds on
        // a pass nobody sees.
        var engine = new CountingEngine();
        using var pipeline = new SttPipeline(engine, new AlwaysSpeech()) { VadTickMs = 300, MinInferIntervalMs = 100_000 };
        pipeline.Start();
        for (int i = 0; i < 5; i++) pipeline.Push(Chunk());
        await Task.Delay(400);                                   // a tick: speech active, no partial pass (interval gate)
        var sw = Stopwatch.StartNew();
        Assert.True(pipeline.Stop());
        Assert.Equal(0, engine.Calls);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Stop took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task AWorkerExceptionIsReportedNotSwallowed()
    {
        var failures = new List<Exception>();
        using var pipeline = new SttPipeline(new ThrowingEngine(), new AlwaysSpeech()) { VadTickMs = 300, MinInferIntervalMs = 100 };
        pipeline.OnWorkerFailed += ex => failures.Add(ex);
        pipeline.Start();
        for (int i = 0; i < 10; i++) pipeline.Push(Chunk());
        for (int i = 0; i < 50 && failures.Count == 0; i++) await Task.Delay(50);
        Assert.Single(failures);
        Assert.False(pipeline.IsRunning);
        pipeline.Stop();
    }

    [Fact]
    public async Task AWorkerFailureHandlerMayStopThePipelineAtOnce()
    {
        // The owner's handler calls Stop() (the controller does): it must
        // return at once, not time out waiting for the worker it was called
        // from, and never log the "did not exit in time" note.
        using var pipeline = new SttPipeline(new ThrowingEngine(), new AlwaysSpeech()) { VadTickMs = 300, MinInferIntervalMs = 100 };
        var logs = new List<string>();
        pipeline.OnLog += l => { lock (logs) logs.Add(l); };
        long stopMs = -1; bool exited = false;
        var done = new TaskCompletionSource();
        pipeline.OnWorkerFailed += _ =>
        {
            var sw = Stopwatch.StartNew();
            exited = pipeline.Stop();
            stopMs = sw.ElapsedMilliseconds;
            done.TrySetResult();
        };
        pipeline.Start();
        for (int i = 0; i < 10; i++) pipeline.Push(Chunk());
        Assert.True(await Task.WhenAny(done.Task, Task.Delay(5000)) == done.Task, "the failure was never reported");
        Assert.True(exited, "Stop reported the worker still running");
        Assert.True(stopMs < 2000, $"Stop took {stopMs} ms");
        lock (logs) Assert.DoesNotContain(logs, l => l.Contains("did not exit"));
        Assert.False(pipeline.IsRunning);
    }

    private sealed class ThrowingEngine : ISttEngine
    {
        public string Name => "throwing";
        public bool IsLoaded => true;
        public bool TryLoad(out string? error) { error = null; return true; }
        public void Dispose() { }
        public Task<string> TranscribeAsync(byte[] pcm, int length, CancellationToken ct = default) => throw new InvalidOperationException("engine pass failed");
        public Task<SttTranscript> TranscribeTimedAsync(byte[] pcm, int length, CancellationToken ct = default) => throw new InvalidOperationException("engine pass failed");
    }
}
