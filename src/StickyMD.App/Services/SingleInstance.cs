using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using StickyMD.Core.Diagnostics;
using StickyMD.Core.Persistence;

namespace StickyMD.App.Services;

/// <summary>
/// The one-process guard, and the pipe a losing launch hands its command line
/// to before it exits.
/// </summary>
/// <remarks>
/// <para>
/// Two StickyMD processes are not an untidiness. Each holds its own in-memory
/// copy of notes.json and writes the WHOLE snapshot on every save, so whichever
/// writes last silently drops the other's rows, and a note with no index entry
/// is unreachable because there is no Open command. It has happened once
/// already, to 2026-09-04-untitled.md. They also share one WebView2 user-data
/// folder, which CoreWebView2Environment.CreateAsync documents as a failure.
/// </para>
/// <para>
/// The guard is a LOCK FILE, not the named mutex spec 7 asks for, and the
/// difference is not cosmetic. A mutex is per-user only in the Global
/// namespace, which a standard non-elevated user has no privilege to create
/// objects in; a session-scoped one is per LOGON SESSION, and one user holds
/// two of those merely by remoting into a machine they are already logged into
/// at the console. That is two processes over one notes.json, which is the
/// thing this class exists to prevent. LOCALAPPDATA is per-user by
/// construction, needs no privilege, and the kernel drops the handle when the
/// process dies however it dies. See the 2026-09-04 revision note in the spec.
/// </para>
/// <para>
/// The pipe is keyed on the user for the same reason, and carries
/// <c>PipeOptions.CurrentUserOnly</c> on both ends per spec 7: it ACLs the
/// server to this user, and makes the client verify the server's owner before
/// it writes a path into it.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The name the app uses. Tests pass their own so a test run does
    /// not collide with a StickyMD the developer has open.</summary>
    public const string DefaultName = "StickyMD";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly string UserKey = CurrentUserKey();

    private readonly string _pipeName;
    private readonly FileStream _lock;
    private readonly CancellationTokenSource _stopping = new();

    private SingleInstance(FileStream held, string pipeName)
    {
        _lock = held;
        _pipeName = pipeName;

        _ = Task.Run(() => ListenAsync(_stopping.Token));
    }

    /// <summary>
    /// A launch that handed off, with its arguments. Empty means a bare
    /// relaunch: someone started StickyMD again with nothing to open.
    /// </summary>
    /// <remarks>
    /// Raised on a PIPE thread. Marshal onto the dispatcher before touching a
    /// window, exactly as the NoteWatcher handlers do.
    /// </remarks>
    public event Action<IReadOnlyList<string>>? Received;

    /// <summary>
    /// Takes the session for this user, or reports that another process has it.
    /// </summary>
    public static bool TryAcquire(string name, out SingleInstance? instance)
    {
        instance = null;

        var lockFile = Path.Combine(AppPaths.AppData, $"{name}.lock");

        FileStream held;
        try
        {
            Directory.CreateDirectory(AppPaths.AppData);

            // DeleteOnClose so the file goes with the process, crash included:
            // there is no stale lock to reason about and no cleanup pass to
            // write. FileShare.None is the whole guard.
            held = new FileStream(
                lockFile, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            // A sharing violation: another StickyMD holds it. This is the ONLY
            // exception that means that, which is why the catch below does
            // something entirely different.
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException)
        {
            // The folder is unwritable, so the guard cannot be TAKEN, which is
            // a different thing from losing it and must not be read as
            // "somebody else is running". Starting anyway is the lesser
            // failure: refusing would make an unwritable LOCALAPPDATA mean
            // StickyMD never starts again, and settings.json, notes.json and
            // this very log all live in that folder, so the app has a bigger
            // problem that it is about to report properly.
            DiagnosticsLog.Write(
                AppPaths.DiagnosticsFile,
                $"The single-instance lock '{lockFile}' could not be taken -- {ex.Message}. "
                    + "Starting without a guard; do not run a second copy.");

            return true;
        }

        instance = new SingleInstance(held, PipeName(name));
        return true;
    }

    /// <summary>
    /// Hands <paramref name="args"/> to the live instance. False means nobody
    /// answered, which the caller has to say out loud: this launch is exiting
    /// either way, so a silent false loses the note the user asked for.
    /// </summary>
    public static bool TrySend(string name, IReadOnlyList<string> args, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName(name), PipeDirection.Out, PipeOptions.CurrentUserOnly);

            // The lock is taken before the server is listening, so a launch
            // that loses a startup race can arrive first. Connect waits.
            client.Connect(timeoutMs);

            using var writer = new StreamWriter(client, Utf8);
            foreach (var arg in args) writer.WriteLine(arg);

            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // One server stream per handoff. Rebuilding it is cheaper than
                // the state machine reusing one would need.
                using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Utf8);
                var payload = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                Received?.Invoke(payload.Split(
                    '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (IOException)
            {
                // A launch that died mid-handoff. Pause before rebuilding, or a
                // pipe that fails on CREATION spins this loop at full speed.
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();

        // Closing the handle deletes the lock file and frees the next launch.
        _lock.Dispose();
    }

    private static string PipeName(string name) => $"{name}.{UserKey}";

    private static string CurrentUserKey()
    {
        using var identity = WindowsIdentity.GetCurrent();

        // The SID first: it is already pipe-name-legal. The fallback is only
        // for an identity with no user SID, and there a DOMAIN\user name has
        // to lose its backslash, the one character a pipe name cannot contain.
        return identity.User?.Value ?? identity.Name.Replace('\\', '.');
    }
}
