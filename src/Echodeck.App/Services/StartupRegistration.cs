using Microsoft.Win32;

namespace Echodeck.App.Services;

/// <summary>
/// "Start with Windows" via the per-user Run key (HKCU — no admin rights needed).
/// Starts with --minimized so Echodeck goes straight to the tray.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Echodeck";

    private static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Each release is a new exe (often in Downloads), so keep the Run entry pointing at the
    /// copy that is actually running.
    /// </summary>
    public static void RefreshPathIfEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current && current != Command)
            key.SetValue(ValueName, Command);
    }
}
