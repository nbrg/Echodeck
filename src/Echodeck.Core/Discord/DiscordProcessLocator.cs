namespace Echodeck.Core.Discord;

/// <summary>A row from a process snapshot: PID, parent PID and executable name (e.g. "Discord.exe").</summary>
public readonly record struct ProcessEntry(int ProcessId, int ParentProcessId, string ExeName);

/// <summary>Abstraction over the OS process list so the selection logic can be unit-tested.</summary>
public interface IProcessSnapshotProvider
{
    IReadOnlyList<ProcessEntry> GetProcesses();
}

/// <summary>A running Discord client: its root process and every process in its tree.</summary>
public sealed record DiscordInstance(int RootProcessId, string ExeName, IReadOnlyList<int> ProcessIds)
{
    public string Flavor => DiscordProcessLocator.FlavorOf(ExeName);
}

/// <summary>
/// Finds the Discord client among running processes.
/// <para>
/// Discord is Electron/Chromium: Discord.exe starts several child Discord.exe processes and the
/// voice audio is rendered by one of the <i>children</i> (the audio/utility process), not the
/// window process. Per-process loopback is therefore pointed at the <b>root</b> Discord.exe with
/// "include process tree", which covers whichever child renders audio — even if Discord
/// restarts that child.
/// </para>
/// The root is the Discord process whose parent is not itself a Discord process
/// (its parent is usually Update.exe or explorer.exe).
/// </summary>
public static class DiscordProcessLocator
{
    /// <summary>Executable names in preference order (stable first).</summary>
    public static readonly IReadOnlyList<string> KnownExeNames = new[]
    {
        "Discord.exe",
        "DiscordPTB.exe",
        "DiscordCanary.exe",
        "DiscordDevelopment.exe",
    };

    public static bool IsDiscordExe(string exeName) =>
        KnownExeNames.Any(n => string.Equals(n, exeName, StringComparison.OrdinalIgnoreCase));

    public static string FlavorOf(string exeName) => Path.GetFileNameWithoutExtension(exeName) switch
    {
        var n when n.Equals("DiscordPTB", StringComparison.OrdinalIgnoreCase) => "Discord PTB",
        var n when n.Equals("DiscordCanary", StringComparison.OrdinalIgnoreCase) => "Discord Canary",
        var n when n.Equals("DiscordDevelopment", StringComparison.OrdinalIgnoreCase) => "Discord Development",
        _ => "Discord",
    };

    /// <summary>
    /// Returns all running Discord instances, best candidate first: preferred flavour (stable,
    /// then PTB, then Canary), then the instance with the most processes (the real client rather
    /// than a lone updater/crash-handler stub).
    /// </summary>
    public static IReadOnlyList<DiscordInstance> FindInstances(IReadOnlyList<ProcessEntry> processes)
    {
        var discord = processes.Where(p => IsDiscordExe(p.ExeName)).ToList();
        if (discord.Count == 0) return Array.Empty<DiscordInstance>();

        var byPid = discord.ToDictionary(p => p.ProcessId);
        var children = discord
            .GroupBy(p => p.ParentProcessId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.ProcessId).ToList());

        var roots = discord.Where(p =>
            p.ParentProcessId == p.ProcessId || // defensive: PID 0 / self-parent
            !byPid.TryGetValue(p.ParentProcessId, out var parent) ||
            // Same name required: a Discord PTB launched from Discord stable is its own root.
            !string.Equals(parent.ExeName, p.ExeName, StringComparison.OrdinalIgnoreCase));

        var instances = new List<DiscordInstance>();
        foreach (var root in roots)
        {
            var tree = new List<int>();
            var stack = new Stack<int>();
            stack.Push(root.ProcessId);
            while (stack.Count > 0)
            {
                int pid = stack.Pop();
                if (tree.Contains(pid)) continue; // guards against PID-reuse cycles
                tree.Add(pid);
                if (children.TryGetValue(pid, out var kids))
                {
                    foreach (int kid in kids)
                    {
                        if (string.Equals(byPid[kid].ExeName, root.ExeName, StringComparison.OrdinalIgnoreCase))
                            stack.Push(kid);
                    }
                }
            }
            instances.Add(new DiscordInstance(root.ProcessId, root.ExeName, tree));
        }

        return instances
            .OrderBy(i => PreferenceIndex(i.ExeName))
            .ThenByDescending(i => i.ProcessIds.Count)
            .ThenBy(i => i.RootProcessId)
            .ToList();
    }

    public static DiscordInstance? FindBest(IProcessSnapshotProvider provider) =>
        FindInstances(provider.GetProcesses()).FirstOrDefault();

    private static int PreferenceIndex(string exeName)
    {
        for (int i = 0; i < KnownExeNames.Count; i++)
            if (string.Equals(KnownExeNames[i], exeName, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }
}
