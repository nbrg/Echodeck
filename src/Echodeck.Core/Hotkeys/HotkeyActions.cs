namespace Echodeck.Core.Hotkeys;

public sealed record HotkeyActionInfo(string Id, string Name, string? DefaultGesture);

/// <summary>
/// Everything a hotkey (or the tray menu, or the phone remote) can trigger. Action IDs are
/// stable strings stored in settings; per-clip actions are "clip:{guid}".
/// </summary>
public static class HotkeyActions
{
    public const string SaveLast = "save:last";
    public const string OpenEditor = "editor:open";
    public const string StopClips = "clips:stop";
    public const string ToggleMute = "mic:toggle-mute";
    public const string ShowWindow = "app:show";

    private const string ClipPrefix = "clip:";

    /// <summary>The fixed (non-clip) actions, in display order, with their default shortcuts.</summary>
    public static IReadOnlyList<HotkeyActionInfo> Global { get; } = new[]
    {
        new HotkeyActionInfo(SaveLast, "Save last N s as a clip", "F8"),
        new HotkeyActionInfo(OpenEditor, "Open trim editor on the whole replay buffer", "F9"),
        new HotkeyActionInfo(StopClips, "Stop playing clips", null),
        new HotkeyActionInfo(ToggleMute, "Mute / unmute microphone", null),
        new HotkeyActionInfo(ShowWindow, "Show Echodeck window", null),
    };

    public static string ForClip(Guid clipId) => ClipPrefix + clipId.ToString("N");

    public static bool TryGetClipId(string actionId, out Guid clipId)
    {
        clipId = Guid.Empty;
        return actionId.StartsWith(ClipPrefix, StringComparison.Ordinal) && Guid.TryParse(actionId[ClipPrefix.Length..], out clipId);
    }

    /// <summary>
    /// Removes bindings for actions that no longer exist (the old "replay:N" quick-replay actions).
    /// If the user had a replay shortcut and "save" has none yet, the shortcut moves to "save",
    /// so their F8 keeps doing something sensible.
    /// </summary>
    public static void Migrate(Dictionary<string, string> bindings)
    {
        var obsolete = bindings.Keys.Where(k => Global.All(a => a.Id != k)).ToList();
        foreach (string id in obsolete)
        {
            if (id.StartsWith("replay:", StringComparison.Ordinal) && !bindings.ContainsKey(SaveLast))
                bindings[SaveLast] = bindings[id];
            bindings.Remove(id);
        }
    }

    public static Dictionary<string, string> DefaultBindings() =>
        Global.Where(a => a.DefaultGesture is not null).ToDictionary(a => a.Id, a => a.DefaultGesture!);
}

public sealed record HotkeyBinding(string ActionId, string Name, HotkeyGesture Gesture);

public static class HotkeyConflicts
{
    /// <summary>
    /// Finds actions sharing a shortcut. Returns actionId → message naming the other action(s).
    /// Only the first binding of a duplicated shortcut gets registered; all of them are reported.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Find(IEnumerable<HotkeyBinding> bindings)
    {
        var result = new Dictionary<string, string>();
        foreach (var group in bindings.GroupBy(b => b.Gesture).Where(g => g.Count() > 1))
        {
            foreach (var b in group)
            {
                var others = group.Where(o => o.ActionId != b.ActionId).Select(o => $"\"{o.Name}\"");
                result[b.ActionId] = $"{b.Gesture} is also used by {string.Join(", ", others)}";
            }
        }
        return result;
    }
}
