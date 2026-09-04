using System.IO;
using StickyMD.App.Interop;
using StickyMD.Core.Notes;

namespace StickyMD.App.Services;

public enum DeletionOutcome
{
    /// <summary>
    /// Gone from its folder. Recoverable from the Recycle Bin on any drive that
    /// provides one. Exception: removable media, some network drives, and a
    /// drive configured to "delete immediately" have no bin to catch it --
    /// FOF_ALLOWUNDO then silently permanently deletes the file, the call
    /// still returns success, and this outcome is indistinguishable from the
    /// recoverable case from here. Detecting that difference needs the same
    /// Shell-COM enumeration the automated tests deliberately avoid.
    /// </summary>
    Deleted,

    /// <summary>Already absent. Not an error -- the desired end state holds.</summary>
    NotFound,

    /// <summary>The shell aborted it.</summary>
    Cancelled,

    /// <summary>It is still there. <c>Message</c> says why.</summary>
    Failed,
}

/// <param name="Message">
/// Non-null on <see cref="DeletionOutcome.Failed"/>. Shown in an inline bar
/// and written to the diagnostics log.
/// </param>
public sealed record DeletionResult(DeletionOutcome Outcome, string? Message);

/// <summary>
/// Sends a note file to the Recycle Bin. Injected so <c>WindowManager</c>'s
/// delete path is testable without destroying anything.
/// </summary>
public interface IFileDeletionService
{
    DeletionResult SendToRecycleBin(string path);
}

/// <summary>
/// Recycle Bin deletion via the shell.
/// </summary>
/// <remarks>
/// FOF_ALLOWUNDO is the entire point. File.Delete is permanent, and this app's
/// governing rule is "never destroy a file" -- a mis-clicked Delete has to be
/// recoverable from the place users already know to look.
///
/// SHFileOperationW is deprecated in favour of the IFileOperation COM
/// interface, and is used anyway: it needs no COM apartment management, no
/// interop assembly, and no reference beyond shell32, and for a single-file
/// delete-to-bin it does exactly one thing. If it ever stops working,
/// IFileOperation is the replacement -- not File.Delete.
///
/// pFrom is DOUBLE-NULL-terminated. It is a list, not a string, and a single
/// terminator makes the shell read past the end of the buffer for the next
/// entry. LPWStr marshalling adds one terminator, so the extra "\0" is added
/// here.
/// </remarks>
public sealed class RecycleBinService : IFileDeletionService
{
    public DeletionResult SendToRecycleBin(string path)
    {
        if (!NotePath.TryCanonical(path, out var canonical))
            return new DeletionResult(DeletionOutcome.Failed, "That path is not usable.");

        if (!File.Exists(canonical))
            return new DeletionResult(DeletionOutcome.NotFound, null);

        var operation = new NativeMethods.SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = NativeMethods.FO_DELETE,
            pFrom = canonical + "\0",
            pTo = null,
            fFlags = (ushort)(
                NativeMethods.FOF_ALLOWUNDO
                | NativeMethods.FOF_NOCONFIRMATION
                | NativeMethods.FOF_SILENT
                | NativeMethods.FOF_NOERRORUI),
        };

        int result;

        try
        {
            result = NativeMethods.SHFileOperationW(ref operation);
        }
        catch (Exception ex) when (
            ex is System.Runtime.InteropServices.SEHException
            or System.Runtime.InteropServices.ExternalException
            or System.Runtime.InteropServices.MarshalDirectiveException)
        {
            // AccessViolationException cannot be caught in modern .NET
            // regardless, so it is not listed here -- this is the widest net
            // a managed catch can throw over a P/Invoke boundary. A blanket
            // catch (Exception) would swallow programming errors along with
            // marshalling failures, so it stays narrow.
            return new DeletionResult(DeletionOutcome.Failed, ex.Message);
        }

        if (operation.fAnyOperationsAborted)
        {
            // Confirm rather than trust, same as the result == 0 path below.
            // FOF_SILENT | FOF_NOCONFIRMATION leaves nothing for a user to
            // abort, so this should be near-dead in practice, but the flag
            // alone does not rule out the file having actually moved before
            // the operation was flagged aborted. If it did move, it is in the
            // bin and recoverable -- report that truthfully instead of
            // leaving a caller to believe the file is still in place when it
            // is not.
            if (!File.Exists(canonical))
                return new DeletionResult(DeletionOutcome.Deleted, null);

            return new DeletionResult(DeletionOutcome.Cancelled, null);
        }

        if (result != 0)
        {
            // SHFileOperation's codes are its own, not Win32 error codes, so
            // FormatMessage would produce something misleading. Report the
            // number and the observable fact.
            return new DeletionResult(
                DeletionOutcome.Failed,
                $"Windows could not delete the file (shell error {result}).");
        }

        // Confirm rather than trust. A zero return with the file still present
        // would otherwise close the note and leave the file behind, and the
        // window would be gone before anyone noticed.
        if (File.Exists(canonical))
        {
            return new DeletionResult(
                DeletionOutcome.Failed,
                "Windows reported success but the file is still there.");
        }

        return new DeletionResult(DeletionOutcome.Deleted, null);
    }
}
