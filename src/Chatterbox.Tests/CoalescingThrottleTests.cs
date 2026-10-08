using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The chatbox rate limiter: first update goes out at once, a burst inside
// the interval collapses to its latest text, identical text is never
// re-sent. Real timers, generous margins.
public class CoalescingThrottleTests
{
    [Fact]
    public void FirstSendIsImmediateAndABurstCoalescesToTheLatest()
    {
        var sent = new List<string>();
        using var t = new CoalescingThrottle(s => { lock (sent) sent.Add(s); }, 200);
        t.Update("a");
        t.Update("b");
        t.Update("c");
        lock (sent) Assert.Equal(new[] { "a" }, sent);
        Thread.Sleep(500);
        lock (sent) Assert.Equal(new[] { "a", "c" }, sent);
    }

    [Fact]
    public void IdenticalTextIsNotResent()
    {
        var sent = new List<string>();
        using var t = new CoalescingThrottle(s => { lock (sent) sent.Add(s); }, 100);
        t.Update("same");
        Thread.Sleep(150);
        t.Update("same");
        Thread.Sleep(150);
        lock (sent) Assert.Single(sent);
    }

    [Fact]
    public void ADisposedThrottleDropsItsPendingText()
    {
        var sent = new List<string>();
        var t = new CoalescingThrottle(s => { lock (sent) sent.Add(s); }, 200);
        t.Update("a");
        t.Update("b");
        t.Dispose();
        Thread.Sleep(350);
        lock (sent) Assert.Equal(new[] { "a" }, sent);
    }
}
