namespace Echodeck.Core.Setup;

public enum SetupSeverity
{
    Warning,
    Error,
}

/// <summary>A routing problem and how to fix it, shown as a banner in the UI.</summary>
public sealed record SetupIssue(SetupSeverity Severity, string Problem, string Fix);

/// <summary>
/// Everything the routing check needs, as plain device names (null = none / unknown).
/// Collected on Windows by AudioSetupMonitor; kept platform-neutral so the rules are unit-testable.
/// </summary>
public sealed record AudioSetupSnapshot(
    bool VirtualCableInstalled,
    string? WindowsDefaultOutput,
    string? EchodeckMicrophone,
    string? EchodeckDiscordOutput,
    string? PreviewDevice,
    bool DiscordRunning,
    string? DiscordOutputDevice,
    string? DiscordInputDevice,
    string? WholeDeviceCaptureDevice);

/// <summary>
/// The expected routing is:
/// <code>
/// Windows output   = headset           Discord output = headset
/// Echodeck mic     = real microphone   Echodeck output = CABLE Input
/// Discord input    = CABLE Output      Previews       = headset
/// </code>
/// Each rule detects one way of getting this wrong and says what the user will notice.
/// Nothing is ever changed automatically.
/// </summary>
public static class AudioSetupRules
{
    /// <summary>True for any VB-Audio virtual cable endpoint (CABLE Input/Output, CABLE In/Out 16ch, …).</summary>
    public static bool IsVirtualCable(string? deviceName) =>
        deviceName is not null &&
        (deviceName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
         deviceName.StartsWith("CABLE ", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<SetupIssue> Evaluate(AudioSetupSnapshot s)
    {
        var issues = new List<SetupIssue>();

        if (!s.VirtualCableInstalled)
        {
            issues.Add(new SetupIssue(SetupSeverity.Error,
                "VB-CABLE is not installed, so Echodeck can't send audio to Discord.",
                "Install it from vb-audio.com/Cable (run the setup as administrator), reboot, then restart Echodeck."));
        }

        if (IsVirtualCable(s.WindowsDefaultOutput))
        {
            issues.Add(new SetupIssue(SetupSeverity.Warning,
                $"Windows sound output is set to \"{s.WindowsDefaultOutput}\" — you won't hear Spotify, games or anything else.",
                "Click the speaker icon in the taskbar and pick your headset. The cable is only chosen inside Echodeck and Discord."));
        }

        if (s.VirtualCableInstalled)
        {
            if (s.EchodeckDiscordOutput is null)
            {
                issues.Add(new SetupIssue(SetupSeverity.Error,
                    "Echodeck has no Discord output device, so friends won't hear you or your clips.",
                    "On the Audio tab, set \"Send to Discord via\" to \"CABLE Input (VB-Audio Virtual Cable)\"."));
            }
            else if (!IsVirtualCable(s.EchodeckDiscordOutput))
            {
                issues.Add(new SetupIssue(SetupSeverity.Warning,
                    $"Echodeck sends its Discord audio to \"{s.EchodeckDiscordOutput}\" — you'll hear your own mic and friends won't hear clips.",
                    "On the Audio tab, set \"Send to Discord via\" to \"CABLE Input (VB-Audio Virtual Cable)\"."));
            }
        }

        if (s.EchodeckMicrophone is null)
        {
            issues.Add(new SetupIssue(SetupSeverity.Warning,
                "No microphone is available to Echodeck, so friends won't hear your voice.",
                "Plug in your headset or choose your microphone on the Audio tab."));
        }
        else if (IsVirtualCable(s.EchodeckMicrophone))
        {
            issues.Add(new SetupIssue(SetupSeverity.Error,
                $"Echodeck's microphone is \"{s.EchodeckMicrophone}\" — that loops Echodeck's output back into itself.",
                "On the Audio tab, choose your real microphone (e.g. your headset mic)."));
        }

        if (IsVirtualCable(s.PreviewDevice))
        {
            issues.Add(new SetupIssue(SetupSeverity.Warning,
                $"Local previews play on \"{s.PreviewDevice}\", so friends would hear them and you wouldn't.",
                "On the Settings tab, set \"Local headphones\" to your headset."));
        }

        if (IsVirtualCable(s.WholeDeviceCaptureDevice))
        {
            issues.Add(new SetupIssue(SetupSeverity.Warning,
                $"Replay capture records \"{s.WholeDeviceCaptureDevice}\" — that is your own outgoing audio, not your friends.",
                "On the Settings tab, set the capture method back to Auto, or pick your headset as the loopback device."));
        }

        if (s.DiscordRunning && IsVirtualCable(s.DiscordOutputDevice))
        {
            issues.Add(new SetupIssue(SetupSeverity.Error,
                $"Discord's output device is \"{s.DiscordOutputDevice}\" — you won't hear your friends.",
                "In Discord → User Settings → Voice & Video, set Output Device to your headset."));
        }

        if (s.DiscordRunning && s.DiscordInputDevice is not null && !IsVirtualCable(s.DiscordInputDevice))
        {
            issues.Add(new SetupIssue(SetupSeverity.Warning,
                $"Discord's input device is \"{s.DiscordInputDevice}\", so friends hear your mic directly but never your clips.",
                "In Discord → User Settings → Voice & Video, set Input Device to \"CABLE Output (VB-Audio Virtual Cable)\"."));
        }

        return issues;
    }
}
