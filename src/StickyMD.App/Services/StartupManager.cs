using Microsoft.Win32;
using StickyMD.Core.Diagnostics;

namespace StickyMD.App.Services;

/// <summary>
/// The seam over <c>HKCU\...\Run</c>, so <see cref="StartupManager"/>'s logic
/// is testable without writing to the real registry.
/// </summary>
/// <remarks>
/// Spec §9 asks for this by name: the Run-key logic is "the one App service
/// with enough logic to be worth covering". A test that actually wrote to
/// HKCU would change the developer's own logon.
/// </remarks>
public interface IStartupRegistry
{
    /// <summary>The value, or null when it is absent.</summary>
    string? ReadValue(string name);

    void WriteValue(string name, string value);

    /// <summary>Removes the value. A value that is already absent is not an error.</summary>
    void DeleteValue(string name);
}

/// <summary>
/// The real <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
/// </summary>
/// <remarks>
/// HKCU, never HKLM: no admin rights, and startup is a per-user choice.
/// </remarks>
public sealed class RunKeyRegistry : IStartupRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? ReadValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(name) as string;
    }

    public void WriteValue(string name, string value)
    {
        // CreateSubKey rather than OpenSubKey: the Run key exists on every
        // Windows install, but a cleanup tool that deleted it outright would
        // otherwise make Launch at Startup permanently unavailable.
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

        // throwOnMissingValue: false. Untick-when-already-absent is the state
        // the user asked for, not a failure.
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Launch at Startup, per spec §7: <c>HKCU\...\Run</c>, value
/// <c>StickyMD</c> → <c>"&lt;exe&gt;" --startup</c>.
/// </summary>
/// <remarks>
/// THE REGISTRY IS THE ONLY SOURCE OF TRUTH. settings.json deliberately has no
/// <c>launchAtStartup</c> flag: cleanup tools, group policy and other
/// "startup manager" utilities strip these entries behind the app's back, so a
/// cached copy would show a tick for an entry that is no longer there. Every
/// read goes to the registry.
///
/// KNOWN LIMITATION: enabled means "the value is present", not "the value
/// points at THIS exe". Rebuild the app somewhere else and the old entry stays
/// as it is, so Windows would launch a path that may no longer exist while the
/// tray still shows a tick. Re-ticking the item rewrites it, which is the whole
/// repair. A launch-time comparison was considered and left out: it would put
/// a registry write on every start for the one user whose install genuinely
/// lives somewhere else.
/// </remarks>
public sealed class StartupManager(
    IStartupRegistry registry, string exePath, string diagnosticsFile)
{
    /// <summary>The Run-key value name. Spec §7, verbatim.</summary>
    public const string ValueName = "StickyMD";

    /// <summary>
    /// Spec §7: <c>--startup</c> reopens notes with <c>ShowActivated=false</c>
    /// so a screenful of notes does not fight the logon sequence for focus.
    /// </summary>
    /// <remarks>
    /// <c>App.ApplyLaunchArgs</c> handles it by IGNORING it, which is correct
    /// rather than lazy: <c>RestoreOpenNotes</c> is unconditionally
    /// <c>ShowActivated=false</c> already, so the flag has nothing left to ask
    /// for. It is still written, because it is the thing that makes a startup
    /// launch distinguishable in Task Manager and in a future crash report.
    /// </remarks>
    public const string StartupArgument = "--startup";

    /// <summary>
    /// What gets written. Quoted, because the default install path under
    /// <c>Program Files</c> and the dev path under <c>C:\Users\...</c> both
    /// contain spaces, and an unquoted Run value would be parsed as a
    /// different executable with arguments.
    /// </summary>
    public string CommandLine { get; } = $"\"{exePath}\" {StartupArgument}";

    /// <summary>
    /// True when the Run value is present. Reads the registry every time.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                return registry.ReadValue(ValueName) is not null;
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                // Reporting "off" is the safe answer: the tick then does not
                // claim a startup entry the app cannot see, and ticking it
                // attempts a write whose failure is reported properly.
                DiagnosticsLog.Write(
                    diagnosticsFile,
                    $"Launch at Startup could not be read from the registry -- {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Applies the user's choice. Returns false, having logged, when the
    /// registry refused -- the caller surfaces that rather than silently
    /// showing the wrong tick.
    /// </summary>
    public bool TrySet(bool enabled)
    {
        try
        {
            if (enabled) registry.WriteValue(ValueName, CommandLine);
            else registry.DeleteValue(ValueName);

            return true;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            DiagnosticsLog.Write(
                diagnosticsFile,
                $"Launch at Startup could not be turned {(enabled ? "on" : "off")} -- {ex.Message}");
            return false;
        }
    }

    /// <remarks>
    /// SecurityException and UnauthorizedAccessException are the policy cases;
    /// IOException is a registry hive that is corrupt or being written to.
    /// Anything else escapes to the last-resort handler, because a startup
    /// checkbox is not worth swallowing an unknown fault for.
    /// </remarks>
    private static bool IsRegistryFailure(Exception ex)
        => ex is System.Security.SecurityException
            or UnauthorizedAccessException
            or System.IO.IOException;
}
