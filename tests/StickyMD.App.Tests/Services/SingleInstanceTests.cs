using Shouldly;
using StickyMD.App.Services;

namespace StickyMD.App.Tests.Services;

public class SingleInstanceTests
{
    /// <summary>
    /// A name per test. The real one is in use whenever the developer has
    /// StickyMD open, and a test that took it would block the app -- or worse,
    /// answer its handoffs. The lock file lands in the real
    /// %LOCALAPPDATA%\StickyMD, which is deliberate: the guard has to be tested
    /// where it actually runs. DeleteOnClose takes each one away again.
    /// </summary>
    private static string UniqueName() => "StickyMD.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void A_second_acquire_fails_while_the_first_is_held()
    {
        var name = UniqueName();

        SingleInstance.TryAcquire(name, out var first).ShouldBeTrue();
        using (first)
        {
            SingleInstance.TryAcquire(name, out var second).ShouldBeFalse();
            second.ShouldBeNull();
        }

        // And the name is free again once the owner exits, or one crashed run
        // would lock the user out of their own app.
        SingleInstance.TryAcquire(name, out var third).ShouldBeTrue();
        third!.Dispose();
    }

    [Fact]
    public async Task The_owner_receives_the_arguments_a_losing_launch_hands_over()
    {
        var name = UniqueName();

        SingleInstance.TryAcquire(name, out var owner).ShouldBeTrue();
        using var _ = owner;

        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        owner!.Received += args => received.TrySetResult(args);

        string[] sent = [@"C:\Notes\standup.md", @"C:\Notes\shopping.md"];
        SingleInstance.TrySend(name, sent).ShouldBeTrue();

        var arrived = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        arrived.ShouldBe(sent);
    }

    [Fact]
    public async Task A_bare_relaunch_arrives_as_an_empty_argument_list()
    {
        // Not the same as no handoff at all: the app turns this one into
        // ShowAll, so it has to reach the owner rather than being dropped for
        // having nothing in it.
        var name = UniqueName();

        SingleInstance.TryAcquire(name, out var owner).ShouldBeTrue();
        using var _ = owner;

        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        owner!.Received += args => received.TrySetResult(args);

        SingleInstance.TrySend(name, []).ShouldBeTrue();

        var arrived = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        arrived.ShouldBeEmpty();
    }

    [Fact]
    public void Sending_to_a_name_nobody_owns_reports_failure_rather_than_hanging()
    {
        SingleInstance.TrySend(UniqueName(), [@"C:\Notes\standup.md"], timeoutMs: 200)
            .ShouldBeFalse();
    }
}
