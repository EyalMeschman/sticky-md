using System.IO;
using Microsoft.Web.WebView2.Core;
using StickyMD.Core.Persistence;

namespace StickyMD.App.Services;

/// <summary>
/// The one <see cref="CoreWebView2Environment"/> every note shares.
/// </summary>
/// <remarks>
/// SHARING IS NOT AN OPTIMISATION. Each environment owns a browser process
/// tree; one per note would mean a dozen sticky notes costing a dozen
/// browsers. And CreateAsync with the same user-data folder but different
/// options throws, so "create one per note and hope" fails on the second note
/// as soon as anything differs.
///
/// The user-data folder is under LOCALAPPDATA with the rest of StickyMD's
/// state, never inside the notes root -- nothing app-owned goes there.
/// </remarks>
public static class WebViewEnvironment
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static CoreWebView2Environment? _environment;

    /// <summary>
    /// The runtime version string, or null when the Evergreen runtime is
    /// absent. Call this BEFORE opening any note: the failure mode otherwise
    /// is a note window that never paints.
    /// </summary>
    public static string? DetectRuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // Some SDK versions return null, others throw. Treat both as
            // absent, because a caller that handles only one of them will
            // eventually meet the other.
            return null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    public static async Task<CoreWebView2Environment> GetAsync()
    {
        if (_environment is not null) return _environment;

        await Gate.WaitAsync().ConfigureAwait(true);

        try
        {
            // Re-check inside the gate. Two notes opening at once would
            // otherwise both create an environment, and the loser's browser
            // process tree would be orphaned.
            if (_environment is not null) return _environment;

            Directory.CreateDirectory(AppPaths.WebViewUserDataDir);

            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.WebViewUserDataDir,
                options: null).ConfigureAwait(true);

            return _environment;
        }
        finally
        {
            Gate.Release();
        }
    }
}
