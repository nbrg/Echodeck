using Echodeck.Audio.Interop;
using Echodeck.Core.Discord;

namespace Echodeck.Audio.Discord;

public sealed class WindowsProcessSnapshotProvider : IProcessSnapshotProvider
{
    public IReadOnlyList<ProcessEntry> GetProcesses() =>
        Toolhelp.Snapshot().Select(p => new ProcessEntry(p.Pid, p.ParentPid, p.ExeName)).ToList();
}
