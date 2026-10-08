using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Echodeck.App.Services;
using Echodeck.Audio.Playback;
using Echodeck.Audio.Soundboard;
using Echodeck.Core.Hotkeys;
using Echodeck.Core.Infrastructure;
using Echodeck.Core.Settings;
using Echodeck.Core.Soundboard;
using Microsoft.Extensions.Logging;

namespace Echodeck.App.ViewModels;

/// <summary>
/// One soundboard clip as shown in the lists. Editing Category / Favourite / Volume / Hotkey
/// writes straight through to the library (and from there to clips.json and the hotkeys).
/// </summary>
public sealed partial class ClipItemViewModel : ObservableObject
{
    private readonly SoundboardService _soundboard;
    private bool _applying;

    public ClipItemViewModel(SoundboardClip clip, SoundboardService soundboard)
    {
        _soundboard = soundboard;
        Id = clip.Id;
        Apply(clip);
    }

    public Guid Id { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _category;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private double _volumePercent;
    [ObservableProperty] private string? _hotkey;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private DateTime _createdAt;
    [ObservableProperty] private string _hotkeyStatus = "";
    [ObservableProperty] private bool _hotkeyHasProblem;

    /// <summary>What's said in the clip; null while not transcribed.</summary>
    [ObservableProperty] private string? _transcript;

    public string TranscriptText => Transcript switch
    {
        null => "",
        "" => "(no speech recognised)",
        var t => $"“{t}”",
    };

    partial void OnTranscriptChanged(string? value) => OnPropertyChanged(nameof(TranscriptText));

    public string DurationText => $"{DurationSeconds:0.0} s";

    /// <summary>Updates from the library without writing back.</summary>
    public void Apply(SoundboardClip clip)
    {
        _applying = true;
        Name = clip.Name;
        Category = clip.Category;
        IsFavorite = clip.IsFavorite;
        VolumePercent = Math.Round(clip.Volume * 100);
        Hotkey = clip.Hotkey;
        DurationSeconds = clip.DurationSeconds;
        CreatedAt = clip.CreatedAt;
        Transcript = clip.Transcript;
        _applying = false;
        OnPropertyChanged(nameof(DurationText));
    }

    partial void OnCategoryChanged(string? value)
    {
        // Through the library, so typing "bob" reuses an existing "Bob" and a new name becomes a category.
        if (!_applying) _soundboard.Library.SetCategory(Id, value);
    }

    partial void OnIsFavoriteChanged(bool value) => Push(c => c.IsFavorite = value);
    partial void OnVolumePercentChanged(double value) => Push(c => c.Volume = Math.Clamp(value / 100, 0, 2));
    partial void OnHotkeyChanged(string? value) => Push(c => c.Hotkey = value);

    private void Push(Action<SoundboardClip> change)
    {
        if (!_applying) _soundboard.Update(Id, change);
    }
}

/// <summary>An entry in the right-click "Category" menu.</summary>
public sealed record CategoryMenuItem(string Header, string? Category, bool IsNew = false, bool IsClear = false);

/// <summary>"Soundboard" tab: the permanent clip library with search, categories, sort and per-clip settings.</summary>
public sealed partial class SoundboardViewModel : ObservableObject, IDisposable
{
    public const string AllFilter = "All clips";
    public const string FavoritesFilter = "★ Favourites";

    private readonly SoundboardService _soundboard;
    private readonly AppActions _actions;
    private readonly LocalPreviewPlayer _preview;
    private readonly HotkeyService _hotkeys;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly ILogger<SoundboardViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Guid, ClipItemViewModel> _byId = new();

    public SoundboardViewModel(SoundboardService soundboard, AppActions actions, LocalPreviewPlayer preview, HotkeyService hotkeys,
        SettingsService settings, DialogService dialogs, AppPaths paths, ILogger<SoundboardViewModel> logger)
    {
        _soundboard = soundboard;
        _actions = actions;
        _preview = preview;
        _hotkeys = hotkeys;
        _settings = settings;
        _dialogs = dialogs;
        _paths = paths;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        SortOptions = new[] { "Favourites first", "Name", "Newest", "Oldest", "Longest", "Shortest" };
        _selectedSort = SortOptions[0];
        _selectedFilter = AllFilter;

        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Matches;
        ApplySort();

        Sync();
        _soundboard.Library.Changed += OnLibraryChanged;
        _hotkeys.RegistrationsChanged += OnRegistrationsChanged;
        _preview.PlaybackEnded += OnPreviewEnded;
    }

    public ObservableCollection<ClipItemViewModel> Items { get; } = new();
    public ICollectionView View { get; }
    public ObservableCollection<string> Filters { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();

    /// <summary>Right-click → Category: "New category…", "No category", then every category.</summary>
    public ObservableCollection<CategoryMenuItem> CategoryMenu { get; } = new();
    public IReadOnlyList<string> SortOptions { get; }

    /// <summary>Raised when an operation wants to report something in the status bar.</summary>
    public event EventHandler<string>? Notified;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedFilter;
    [ObservableProperty] private string _selectedSort;
    [ObservableProperty] private ClipItemViewModel? _selectedClip;
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private string _countText = "";

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSelectedFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsCategoryFilter));
        Refresh();
    }

    /// <summary>The filter is a category (so it can be renamed or deleted).</summary>
    public bool IsCategoryFilter => SelectedFilter != AllFilter && SelectedFilter != FavoritesFilter;
    partial void OnSelectedSortChanged(string value) { ApplySort(); Refresh(); }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private async Task PlayToDiscordAsync(ClipItemViewModel? clip)
    {
        clip ??= SelectedClip;
        if (clip is not null) await _actions.PlayClipAsync(clip.Id);
    }

    [RelayCommand]
    private async Task PreviewAsync(ClipItemViewModel? clip)
    {
        clip ??= SelectedClip;
        if (clip is null) return;
        try
        {
            var entry = _soundboard.Library.Find(clip.Id);
            if (entry is null) return;
            await _preview.PlayFileAsync(_soundboard.FullPath(entry), _settings.Current.PreviewDeviceId);
            IsPreviewing = true;
            Notify($"Previewing \"{clip.Name}\" on your headphones (not sent to Discord).");
        }
        catch (Exception ex)
        {
            Notify($"Preview failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void StopPreview()
    {
        _preview.Stop();
        IsPreviewing = false;
    }

    [RelayCommand]
    private async Task EditAsync(ClipItemViewModel? clip)
    {
        clip ??= SelectedClip;
        if (clip is null) return;
        StopPreview();
        try { await _actions.OpenClipEditorAsync(clip.Id); }
        catch (Exception ex) { Notify($"Could not open clip: {ex.Message}"); }
    }

    [RelayCommand]
    private void Rename(ClipItemViewModel? clip)
    {
        clip ??= SelectedClip;
        if (clip is null) return;
        string? newName = _dialogs.Prompt("Rename clip", "New name:", clip.Name);
        if (newName is null || newName == clip.Name) return;
        Guarded(() =>
        {
            StopPreview();
            var renamed = _soundboard.Rename(clip.Id, newName);
            Notify($"Renamed to \"{renamed.Name}\".");
        }, "Rename");
    }

    /// <summary>Deletes one clip (row button), or — with no argument — every selected clip.</summary>
    [RelayCommand]
    private void Delete(ClipItemViewModel? clip) =>
        DeleteClips(clip is not null ? new[] { clip } : SelectedClips.Count > 0 ? SelectedClips : SelectedClip is null ? Array.Empty<ClipItemViewModel>() : new[] { SelectedClip });

    /// <summary>Deletes the given clips after a single confirmation.</summary>
    public void DeleteClips(IReadOnlyList<ClipItemViewModel> clips)
    {
        if (clips.Count == 0) return;
        string question = clips.Count == 1
            ? $"Delete \"{clips[0].Name}\"? This can't be undone."
            : $"Delete these {clips.Count} clips? This can't be undone.\n\n" +
              string.Join("\n", clips.Take(8).Select(c => "• " + c.Name)) + (clips.Count > 8 ? $"\n… and {clips.Count - 8} more" : "");
        if (!_dialogs.Confirm(question, clips.Count == 1 ? "Delete clip" : "Delete clips")) return;

        StopPreview(); // a previewing file is open and can't be deleted
        int deleted = 0;
        foreach (var clip in clips.ToList())
        {
            try
            {
                _soundboard.Delete(clip.Id);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delete {Clip} failed", clip.Name);
                Notify($"Couldn't delete \"{clip.Name}\": {ex.Message}");
            }
        }
        SelectedClips = Array.Empty<ClipItemViewModel>();
        if (deleted > 0) Notify(deleted == 1 ? $"Deleted \"{clips[0].Name}\"." : $"Deleted {deleted} clips.");
    }

    /// <summary>Clips currently selected in a list (set by the view; supports Ctrl/Shift multi-select).</summary>
    public IReadOnlyList<ClipItemViewModel> SelectedClips { get; set; } = Array.Empty<ClipItemViewModel>();

    [RelayCommand]
    private async Task DuplicateAsync(ClipItemViewModel? clip)
    {
        clip ??= SelectedClip;
        if (clip is null) return;
        try
        {
            var copy = await _soundboard.DuplicateAsync(clip.Id);
            Notify($"Created \"{copy.Name}\".");
            SelectById(copy.Id);
        }
        catch (Exception ex)
        {
            Notify($"Duplicate failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ToggleFavorite(ClipItemViewModel? clip)
    {
        clip ??= SelectedClip;
        if (clip is not null) clip.IsFavorite = !clip.IsFavorite;
    }

    // ------------------------------------------------------------------ categories

    /// <summary>Clips a command applies to: the right-clicked/selected rows, or the current clip.</summary>
    private IReadOnlyList<ClipItemViewModel> Targets() =>
        SelectedClips.Count > 0 ? SelectedClips : SelectedClip is null ? Array.Empty<ClipItemViewModel>() : new[] { SelectedClip };

    /// <summary>Creates a category (e.g. a friend's name). Clips selected right now go into it.</summary>
    [RelayCommand]
    private void NewCategory()
    {
        string? name = _dialogs.Prompt("New category", "Name (for example a friend's name):", "");
        if (ClipLibraryStore.CleanCategory(name) is null) return;
        Guarded(() =>
        {
            string created = _soundboard.Library.AddCategory(name!);
            Notify($"Created category \"{created}\".");
        }, "New category");
    }

    /// <summary>Right-click → Category → …: moves the selected clips into a category (or a new one).</summary>
    [RelayCommand]
    private void AssignCategory(CategoryMenuItem? item)
    {
        if (item is null) return;
        var targets = Targets();
        if (targets.Count == 0) return;
        string? category = item.Category;
        if (item.IsNew)
        {
            category = _dialogs.Prompt("New category", "Name (for example a friend's name):", "");
            if (ClipLibraryStore.CleanCategory(category) is null) return;
        }
        Guarded(() =>
        {
            string? applied = null;
            foreach (var clip in targets.ToList())
                applied = _soundboard.Library.SetCategory(clip.Id, item.IsClear ? null : category)?.Category;
            string what = targets.Count == 1 ? $"\"{targets[0].Name}\"" : $"{targets.Count} clips";
            Notify(applied is null ? $"Removed the category from {what}." : $"Moved {what} to \"{applied}\".");
        }, "Set category");
    }

    [RelayCommand]
    private void RenameCategory()
    {
        if (!IsCategoryFilter) return;
        string old = SelectedFilter;
        string? name = _dialogs.Prompt("Rename category", $"New name for \"{old}\":", old);
        if (ClipLibraryStore.CleanCategory(name) is null || name == old) return;
        Guarded(() =>
        {
            string renamed = _soundboard.Library.RenameCategory(old, name!);
            // After the library change has refreshed the filter list (queued at normal priority).
            _dispatcher.BeginInvoke(() => SelectedFilter = renamed, DispatcherPriority.Background);
            Notify($"Renamed category \"{old}\" to \"{renamed}\".");
        }, "Rename category");
    }

    [RelayCommand]
    private void DeleteCategory()
    {
        if (!IsCategoryFilter) return;
        string name = SelectedFilter;
        int count = Items.Count(i => string.Equals(i.Category, name, StringComparison.CurrentCultureIgnoreCase));
        string question = count == 0
            ? $"Delete the category \"{name}\"?"
            : $"Delete the category \"{name}\"? Its {count} clip(s) are kept, just without a category.";
        if (!_dialogs.Confirm(question, "Delete category")) return;
        Guarded(() =>
        {
            _soundboard.Library.DeleteCategory(name);
            SelectedFilter = AllFilter;
            Notify($"Deleted category \"{name}\".");
        }, "Delete category");
    }

    [RelayCommand]
    private void ClearHotkey(ClipItemViewModel? clip)
    {
        clip ??= SelectedClip;
        if (clip is not null) clip.Hotkey = null;
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var files = _dialogs.PickAudioFiles();
        if (files.Count == 0) return;
        Notify($"Importing {files.Count} file(s)…");
        var added = await _soundboard.ImportAsync(files);
        Notify(added.Count == files.Count
            ? $"Imported {added.Count} clip(s)."
            : $"Imported {added.Count} of {files.Count} — see the log for files that couldn't be read.");
        if (added.Count > 0) SelectById(added[^1].Id);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.ClipsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.ClipsDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify($"Could not open folder: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ library sync

    private void OnLibraryChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(Sync);

    /// <summary>Brings the item list in line with the library, keeping existing item objects (and the selection).</summary>
    private void Sync()
    {
        var clips = _soundboard.Library.Clips;
        var live = new HashSet<Guid>(clips.Select(c => c.Id));

        foreach (var gone in Items.Where(i => !live.Contains(i.Id)).ToList())
        {
            Items.Remove(gone);
            _byId.Remove(gone.Id);
        }
        foreach (var clip in clips)
        {
            if (_byId.TryGetValue(clip.Id, out var existing)) existing.Apply(clip);
            else
            {
                var item = new ClipItemViewModel(clip, _soundboard);
                _byId[clip.Id] = item;
                Items.Add(item);
            }
        }

        var categories = _soundboard.Library.Categories;
        Replace(Categories, categories);
        Replace(Filters, new[] { AllFilter, FavoritesFilter }.Concat(categories));
        var menu = new[] { new CategoryMenuItem("＋ New category…", null, IsNew: true), new CategoryMenuItem("No category", null, IsClear: true) }
            .Concat(categories.Select(c => new CategoryMenuItem(c, c))).ToList();
        if (!CategoryMenu.SequenceEqual(menu))
        {
            CategoryMenu.Clear();
            foreach (var m in menu) CategoryMenu.Add(m);
        }
        if (!Filters.Contains(SelectedFilter)) SelectedFilter = AllFilter;

        OnRegistrationsChanged(null, EventArgs.Empty);
        Refresh();
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> values)
    {
        var list = values.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var v in list) target.Add(v);
    }

    private void OnRegistrationsChanged(object? sender, EventArgs e)
    {
        foreach (var item in Items)
        {
            if (string.IsNullOrEmpty(item.Hotkey))
            {
                item.HotkeyStatus = "";
                item.HotkeyHasProblem = false;
            }
            else if (_hotkeys.Registrations.TryGetValue(HotkeyActions.ForClip(item.Id), out var reg))
            {
                item.HotkeyHasProblem = reg.State != HotkeyState.Registered;
                item.HotkeyStatus = string.IsNullOrEmpty(reg.Message) ? "✔ Works everywhere, even in games" : reg.Message;
            }
        }
    }

    private void OnPreviewEnded(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() => IsPreviewing = false);

    // ------------------------------------------------------------------ filter / sort

    private bool Matches(object o)
    {
        if (o is not ClipItemViewModel c) return false;
        if (SelectedFilter == FavoritesFilter && !c.IsFavorite) return false;
        if (SelectedFilter != AllFilter && SelectedFilter != FavoritesFilter &&
            !string.Equals(c.Category, SelectedFilter, StringComparison.CurrentCultureIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        string q = SearchText.Trim();
        return c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || (c.Category?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
               || (c.Transcript?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
               || (c.Hotkey?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private void ApplySort()
    {
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            void By(string prop, ListSortDirection dir = ListSortDirection.Ascending) => View.SortDescriptions.Add(new SortDescription(prop, dir));
            switch (SelectedSort)
            {
                case "Name": By(nameof(ClipItemViewModel.Name)); break;
                case "Newest": By(nameof(ClipItemViewModel.CreatedAt), ListSortDirection.Descending); break;
                case "Oldest": By(nameof(ClipItemViewModel.CreatedAt)); break;
                case "Longest": By(nameof(ClipItemViewModel.DurationSeconds), ListSortDirection.Descending); break;
                case "Shortest": By(nameof(ClipItemViewModel.DurationSeconds)); break;
                default:
                    By(nameof(ClipItemViewModel.IsFavorite), ListSortDirection.Descending);
                    By(nameof(ClipItemViewModel.Name));
                    break;
            }
        }
    }

    private void Refresh()
    {
        View.Refresh();
        int shown = View.Cast<object>().Count();
        CountText = shown == Items.Count ? $"{Items.Count} clips" : $"{shown} of {Items.Count} clips";
    }

    private void SelectById(Guid id) =>
        _dispatcher.BeginInvoke(() => { if (_byId.TryGetValue(id, out var item)) SelectedClip = item; }, DispatcherPriority.Background);

    private void Guarded(Action action, string what)
    {
        try { action(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{What} failed", what);
            Notify($"{what} failed: {ex.Message}");
        }
    }

    private void Notify(string message) => Notified?.Invoke(this, message);

    public void Dispose()
    {
        _soundboard.Library.Changed -= OnLibraryChanged;
        _hotkeys.RegistrationsChanged -= OnRegistrationsChanged;
        _preview.PlaybackEnded -= OnPreviewEnded;
    }
}
