using Echodeck.Core.Hotkeys;
using Echodeck.Core.Infrastructure;
using Echodeck.Core.Settings;
using Echodeck.Core.Soundboard;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echodeck.Core.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("F8", HotkeyModifiers.None, 0x77)]
    [InlineData("Ctrl+NumPad1", HotkeyModifiers.Control, 0x61)]
    [InlineData("ctrl + alt + shift + K", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x4B)]
    [InlineData("Win+Pause", HotkeyModifiers.Windows, 0x13)]
    [InlineData("Alt+Key2A", HotkeyModifiers.Alt, 0x2A)]
    public void Parses(string text, HotkeyModifiers modifiers, int vk)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(new HotkeyGesture(modifiers, vk), g);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Bogus")]
    public void RejectsInvalidText(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void RoundTripsThroughText()
    {
        var g = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x63);
        Assert.Equal("Ctrl+Shift+NumPad3", g.ToString());
        Assert.Equal(g, HotkeyGesture.ParseOrNull(g.ToString()));
    }

    [Theory]
    [InlineData("F8", true)]
    [InlineData("NumPad5", true)]
    [InlineData("Ctrl+A", true)]
    [InlineData("Alt+1", true)]
    [InlineData("A", false)]          // would hijack typing
    [InlineData("Shift+A", false)]    // Shift alone isn't enough
    [InlineData("Space", false)]
    [InlineData("Ctrl+Esc", false)]
    public void Validation(string text, bool valid)
    {
        var g = HotkeyGesture.ParseOrNull(text)!.Value;
        Assert.Equal(valid, g.ValidationError is null);
    }
}

public class HotkeyConflictTests
{
    [Fact]
    public void ReportsBothSidesOfAConflict()
    {
        var f8 = HotkeyGesture.ParseOrNull("F8")!.Value;
        var conflicts = HotkeyConflicts.Find(new[]
        {
            new HotkeyBinding("save:last", "Save", f8),
            new HotkeyBinding("clip:x", "He's definitely B", f8),
            new HotkeyBinding("clips:stop", "Stop", HotkeyGesture.ParseOrNull("F7")!.Value),
        });
        Assert.Equal(2, conflicts.Count);
        Assert.Contains("He's definitely B", conflicts["save:last"]);
        Assert.False(conflicts.ContainsKey("clips:stop"));
    }

    [Fact]
    public void ClipActionIds_RoundTrip()
    {
        var id = Guid.NewGuid();
        Assert.True(HotkeyActions.TryGetClipId(HotkeyActions.ForClip(id), out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void Migrate_MovesOldReplayShortcutToSave_AndDropsUnknownActions()
    {
        var bindings = new Dictionary<string, string> { ["replay:5"] = "F8", ["replay:10"] = "F10", ["editor:open"] = "F9" };
        HotkeyActions.Migrate(bindings);
        Assert.Equal(new Dictionary<string, string> { [HotkeyActions.SaveLast] = "F8", [HotkeyActions.OpenEditor] = "F9" }, bindings);

        var keepsExistingSave = new Dictionary<string, string> { ["replay:5"] = "F8", [HotkeyActions.SaveLast] = "F7" };
        HotkeyActions.Migrate(keepsExistingSave);
        Assert.Equal(new Dictionary<string, string> { [HotkeyActions.SaveLast] = "F7" }, keepsExistingSave);
    }
}

public class SettingsCloneTests
{
    [Fact]
    public void Clone_DeepCopiesHotkeys()
    {
        var original = new AppSettings();
        var copy = original.Clone();
        copy.Hotkeys[HotkeyActions.SaveLast] = "F2";
        Assert.Equal("F8", original.Hotkeys[HotkeyActions.SaveLast]);
    }
}

public sealed class ClipLibraryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "echodeck-lib-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public ClipLibraryStoreTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    private ClipLibraryStore NewStore() =>
        new(_paths, _ => TimeSpan.FromSeconds(2.5), NullLogger<ClipLibraryStore>.Instance);

    private void Touch(string fileName) => File.WriteAllBytes(Path.Combine(_paths.ClipsDirectory, fileName), new byte[44]);

    [Fact]
    public void Load_AdoptsUnknownWavFiles()
    {
        Touch("Trust me.wav");
        var store = NewStore();
        store.Load();

        var clip = Assert.Single(store.Clips);
        Assert.Equal("Trust me", clip.Name);
        Assert.Equal(2.5, clip.DurationSeconds);
        Assert.True(File.Exists(_paths.ClipLibraryFile));
    }

    [Fact]
    public void Metadata_PersistsAcrossLoads()
    {
        Touch("a.wav");
        var store = NewStore();
        store.Load();
        var id = store.Clips[0].Id;
        store.Update(id, c => { c.Category = "CS2"; c.IsFavorite = true; c.Hotkey = "Ctrl+NumPad1"; c.Volume = 0.5; });

        var reloaded = NewStore();
        reloaded.Load();
        var clip = Assert.Single(reloaded.Clips);
        Assert.Equal(id, clip.Id);
        Assert.Equal("CS2", clip.Category);
        Assert.True(clip.IsFavorite);
        Assert.Equal("Ctrl+NumPad1", clip.Hotkey);
        Assert.Equal(0.5, clip.Volume);
        Assert.Equal(new[] { "CS2" }, reloaded.Categories);
    }

    [Fact]
    public void Load_DropsEntriesWhoseFileWasDeleted()
    {
        Touch("a.wav");
        Touch("b.wav");
        var store = NewStore();
        store.Load();
        File.Delete(Path.Combine(_paths.ClipsDirectory, "a.wav"));

        var reloaded = NewStore();
        reloaded.Load();
        Assert.Equal("b", Assert.Single(reloaded.Clips).Name);
    }

    [Fact]
    public void ReturnsCopies_AndBumpsVersion()
    {
        Touch("a.wav");
        var store = NewStore();
        store.Load();
        long v = store.Version;
        store.Clips[0].Name = "mutated outside";
        Assert.Equal("a", store.Clips[0].Name);

        store.Update(store.Clips[0].Id, c => c.Name = "renamed");
        Assert.True(store.Version > v);
    }

    [Fact]
    public void CorruptIndex_IsRebuiltFromFiles()
    {
        Touch("a.wav");
        File.WriteAllText(_paths.ClipLibraryFile, "{ not json");
        var store = NewStore();
        store.Load();
        Assert.Single(store.Clips);
        Assert.True(File.Exists(_paths.ClipLibraryFile + ".bad"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
