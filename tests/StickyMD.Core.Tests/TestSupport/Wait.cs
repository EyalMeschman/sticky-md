namespace StickyMD.Core.Tests.TestSupport;

public static class Wait
{
    /// <summary>Polls until the condition holds, or fails the test on timeout.</summary>
    public static void Until(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            Thread.Sleep(15);
        }

        throw new Xunit.Sdk.XunitException(
            $"Timed out after {timeoutMs}ms waiting for: {because}");
    }

    /// <summary>
    /// Waits out a window in which the condition must NOT become true.
    /// Used to prove an event does not fire.
    /// </summary>
    public static void StaysFalse(Func<bool> condition, string because, int forMs = 600)
    {
        var deadline = Environment.TickCount64 + forMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                throw new Xunit.Sdk.XunitException($"Expected to stay false: {because}");
            Thread.Sleep(15);
        }
    }
}
