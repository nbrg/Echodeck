using System.Runtime.InteropServices;

namespace Echodeck.App.Services;

/// <summary>What the user is looking at right now (used to avoid popping windows over games).</summary>
internal static class ForegroundWindow
{
    private const int QunsRunningD3DFullScreen = 3;

    /// <summary>
    /// True while a Direct3D app (a game) runs in exclusive fullscreen. Showing any window then
    /// minimises the game. Borderless-windowed games return false: windows can appear over them.
    /// </summary>
    public static bool IsExclusiveFullscreenGame()
    {
        try
        {
            return SHQueryUserNotificationState(out int state) == 0 && state == QunsRunningD3DFullScreen;
        }
        catch
        {
            return false; // never block the editor because the check itself failed
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
