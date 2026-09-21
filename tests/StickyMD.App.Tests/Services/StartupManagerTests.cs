using System.IO;
using Shouldly;
using StickyMD.App.Services;

namespace StickyMD.App.Tests.Services;

/// <summary>
/// Spec §9 asks for these by name: the Run-key logic is "the one App service
/// with enough logic to be worth covering", behind an
/// <see cref="IStartupRegistry"/> seam so a test never touches the real HKCU.
/// </summary>
public class StartupManagerTests
{
    private const string Exe = @"C:\Program Files\Sticky MD\StickyMD.exe";

    private readonly string _log = Path.Combine(
        Path.GetTempPath(), "stickymd-startup", Guid.NewGuid().ToString("N"), "diagnostics.log");

    private sealed class FakeRegistry : IStartupRegistry
    {
        private readonly Dictionary<string, string> _values = [];

        public Exception? ThrowOnWrite { get; set; }
        public Exception? ThrowOnRead { get; set; }
        public int Deletes { get; private set; }

        public string? ReadValue(string name)
        {
            if (ThrowOnRead is not null) throw ThrowOnRead;
            return _values.GetValueOrDefault(name);
        }

        public void WriteValue(string name, string value)
        {
            if (ThrowOnWrite is not null) throw ThrowOnWrite;
            _values[name] = value;
        }

        public void DeleteValue(string name)
        {
            if (ThrowOnWrite is not null) throw ThrowOnWrite;
            Deletes++;
            _values.Remove(name);
        }

        public string? Peek(string name) => _values.GetValueOrDefault(name);
    }

    private (StartupManager Manager, FakeRegistry Registry) Build()
    {
        var registry = new FakeRegistry();
        return (new StartupManager(registry, Exe, _log), registry);
    }

    [Fact]
    public void An_absent_value_reads_as_disabled()
    {
        var (manager, _) = Build();

        manager.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Enabling_writes_the_quoted_exe_path_with_the_startup_flag()
    {
        var (manager, registry) = Build();

        manager.TrySet(true).ShouldBeTrue();

        // Quoted, because the default install path and the dev path both
        // contain spaces -- unquoted, Windows parses "C:\Program" as the
        // executable and the rest as arguments.
        registry.Peek(StartupManager.ValueName)
            .ShouldBe($"\"{Exe}\" {StartupManager.StartupArgument}");

        manager.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Disabling_removes_the_value_rather_than_writing_a_false()
    {
        var (manager, registry) = Build();

        manager.TrySet(true);
        manager.TrySet(false).ShouldBeTrue();

        // Presence IS the state, per spec §5. A value of "0" or "false" would
        // still be a Run entry, and Windows would still launch it.
        registry.Peek(StartupManager.ValueName).ShouldBeNull();
        manager.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Disabling_when_it_was_never_enabled_is_not_a_failure()
    {
        var (manager, registry) = Build();

        manager.TrySet(false).ShouldBeTrue();

        registry.Deletes.ShouldBe(1);
        manager.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Enabling_twice_leaves_one_entry_holding_the_current_path()
    {
        var (manager, registry) = Build();

        manager.TrySet(true);
        manager.TrySet(true);

        // The whole repair for a stale entry: re-ticking rewrites it. There is
        // deliberately no launch-time path comparison -- see StartupManager's
        // known limitation.
        registry.Peek(StartupManager.ValueName)
            .ShouldBe($"\"{Exe}\" {StartupManager.StartupArgument}");
    }

    [Fact]
    public void A_refused_write_reports_failure_instead_of_throwing()
    {
        var (manager, _) = Build();
        var registry = new FakeRegistry { ThrowOnWrite = new UnauthorizedAccessException("policy") };
        manager = new StartupManager(registry, Exe, _log);

        // Group policy, a locked-down machine, a registry hive being written.
        // The tray puts the tick back where the registry actually is and says
        // so; a throw here would take the whole app out over a checkbox.
        manager.TrySet(true).ShouldBeFalse();
        manager.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void A_registry_that_cannot_be_read_reads_as_disabled()
    {
        var registry = new FakeRegistry { ThrowOnRead = new System.Security.SecurityException() };
        var manager = new StartupManager(registry, Exe, _log);

        // "Off" is the safe answer: the tick then does not claim a startup
        // entry the app cannot see, and ticking it attempts a write whose
        // failure is reported properly.
        manager.IsEnabled.ShouldBeFalse();
    }
}
