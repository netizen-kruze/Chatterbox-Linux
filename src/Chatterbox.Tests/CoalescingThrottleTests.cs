using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The chatbox rate limiter: first update goes out at once, a burst inside
// the interval collapses to its latest text, identical text is never
// re-sent. Real timers, generous margins — and where a late timer would
// only mean a slow machine (a build running beside the tests starved the
// thread pool for more than 300 ms once), the test waits with a deadline
// instead of a fixed sleep.
public class CoalescingThrottleTests
{
    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    [Fact]
    public void FirstSendIsImmediateAndABurstCoalescesToTheLatest()
    {
        var sent = new List<string>();
        using var t = new CoalescingThrottle(s => { lock (sent) sent.Add(s); }, 200);
        t.Update("a");
        t.Update("b");
        t.Update("c");
        lock (sent) Assert.Equal(new[] { "a" }, sent);
        // The trailing send fires once the interval is over — "b" never
        // goes out, "c" does, however late the timer is on a loaded box.
        Assert.True(WaitFor(() => { lock (sent) return sent.Count >= 2; }, 5000), "the trailing send never came");
        lock (sent) Assert.Equal(new[] { "a", "c" }, sent);
        Thread.Sleep(300);
        lock (sent) Assert.Equal(new[] { "a", "c" }, sent);   // and nothing after it
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
