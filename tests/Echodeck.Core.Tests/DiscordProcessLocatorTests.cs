using Echodeck.Core.Discord;

namespace Echodeck.Core.Tests;

public class DiscordProcessLocatorTests
{
    private static ProcessEntry P(int pid, int parent, string exe) => new(pid, parent, exe);

    [Fact]
    public void NoDiscord_ReturnsEmpty()
    {
        var list = new[] { P(4, 0, "System"), P(100, 4, "explorer.exe"), P(200, 100, "cs2.exe") };
        Assert.Empty(DiscordProcessLocator.FindInstances(list));
    }

    [Fact]
    public void FindsRoot_AndWholeTree()
    {
        var list = new[]
        {
            P(100, 4, "explorer.exe"),
            P(500, 100, "Update.exe"),
            P(1000, 500, "Discord.exe"),   // root (window/browser process)
            P(1001, 1000, "Discord.exe"),  // gpu
            P(1002, 1000, "Discord.exe"),  // audio utility process
            P(1003, 1002, "Discord.exe"),  // grandchild
            P(2000, 100, "Spotify.exe"),
        };

        var instance = Assert.Single(DiscordProcessLocator.FindInstances(list));
        Assert.Equal(1000, instance.RootProcessId);
        Assert.Equal(new[] { 1000, 1001, 1002, 1003 }, instance.ProcessIds.OrderBy(x => x));
        Assert.Equal("Discord", instance.Flavor);
    }

    [Fact]
    public void ParentExited_ChildBecomesRoot()
    {
        // Update.exe already exited; parent PID points at nothing.
        var list = new[] { P(1000, 999, "Discord.exe"), P(1001, 1000, "Discord.exe") };
        Assert.Equal(1000, DiscordProcessLocator.FindInstances(list)[0].RootProcessId);
    }

    [Fact]
    public void PrefersStable_OverPtbAndCanary()
    {
        var list = new[]
        {
            P(3000, 100, "DiscordCanary.exe"), P(3001, 3000, "DiscordCanary.exe"), P(3002, 3000, "DiscordCanary.exe"),
            P(2000, 100, "DiscordPTB.exe"),
            P(1000, 100, "discord.exe"),   // case-insensitive
        };

        var instances = DiscordProcessLocator.FindInstances(list);
        Assert.Equal(new[] { 1000, 2000, 3000 }, instances.Select(i => i.RootProcessId));
    }

    [Fact]
    public void SameFlavour_PrefersInstanceWithMostProcesses()
    {
        var list = new[]
        {
            P(10, 1, "Discord.exe"),                        // lone stub (e.g. crash handler)
            P(20, 1, "Discord.exe"), P(21, 20, "Discord.exe"), P(22, 20, "Discord.exe"),
        };
        Assert.Equal(20, DiscordProcessLocator.FindInstances(list)[0].RootProcessId);
    }

    [Fact]
    public void ParentCycle_DoesNotHang()
    {
        // PID reuse can in theory produce a cycle; must terminate.
        var list = new[] { P(1, 2, "Discord.exe"), P(2, 1, "Discord.exe"), P(3, 50, "Discord.exe"), P(4, 3, "Discord.exe") };
        var instances = DiscordProcessLocator.FindInstances(list);
        Assert.Contains(instances, i => i.RootProcessId == 3);
    }
}
