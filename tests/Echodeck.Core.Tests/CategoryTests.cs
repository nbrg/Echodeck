using Echodeck.Core.Infrastructure;
using Echodeck.Core.Soundboard;
using Microsoft.Extensions.Logging.Abstractions;

namespace Echodeck.Core.Tests;

public sealed class CategoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "echodeck-cat-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public CategoryTests()
    {
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    private ClipLibraryStore NewStore()
    {
        var store = new ClipLibraryStore(_paths, _ => TimeSpan.FromSeconds(1), NullLogger<ClipLibraryStore>.Instance);
        store.Load();
        return store;
    }

    private SoundboardClip AddClip(ClipLibraryStore store, string name, string? category = null)
    {
        File.WriteAllBytes(Path.Combine(_paths.ClipsDirectory, name + ".wav"), new byte[44]);
        var clip = new SoundboardClip { Name = name, FileName = name + ".wav", Category = category };
        store.Add(clip);
        return clip;
    }

    [Fact]
    public void Empty_categories_are_kept_across_restarts()
    {
        var store = NewStore();
        Assert.Equal("Bob", store.AddCategory("  Bob  "));
        store.AddCategory("Alice");

        Assert.Equal(new[] { "Alice", "Bob" }, NewStore().Categories);
    }

    [Fact]
    public void Adding_an_existing_category_reuses_its_spelling()
    {
        var store = NewStore();
        store.AddCategory("Bob");
        Assert.Equal("Bob", store.AddCategory("bob"));
        Assert.Single(store.Categories);
    }

    [Fact]
    public void SetCategory_creates_the_category_and_reuses_spelling()
    {
        var store = NewStore();
        store.AddCategory("Bob");
        var a = AddClip(store, "a");
        var b = AddClip(store, "b");

        Assert.Equal("Bob", store.SetCategory(a.Id, "BOB")!.Category);
        Assert.Equal("Charlie", store.SetCategory(b.Id, "Charlie")!.Category);
        Assert.Equal(new[] { "Bob", "Charlie" }, store.Categories);

        Assert.Null(store.SetCategory(b.Id, "  ")!.Category);
        Assert.Contains("Charlie", store.Categories); // still there, now empty
        Assert.Null(store.SetCategory(Guid.NewGuid(), "x"));
    }

    [Fact]
    public void Rename_moves_every_clip_and_merges_into_an_existing_category()
    {
        var store = NewStore();
        var a = AddClip(store, "a", "Bobby");
        var b = AddClip(store, "b", "bobby");
        var c = AddClip(store, "c", "Bob");

        Assert.Equal("Robert", store.RenameCategory("Bobby", "Robert"));
        Assert.Equal("Robert", store.Find(a.Id)!.Category);
        Assert.Equal("Robert", store.Find(b.Id)!.Category);

        Assert.Equal("Bob", store.RenameCategory("Robert", "bob")); // merge into the existing "Bob"
        Assert.All(new[] { a, b, c }, x => Assert.Equal("Bob", store.Find(x.Id)!.Category));
        Assert.Equal(new[] { "Bob" }, NewStore().Categories);
    }

    [Fact]
    public void Rename_can_change_letter_case()
    {
        var store = NewStore();
        var a = AddClip(store, "a", "bob");
        Assert.Equal("Bob", store.RenameCategory("bob", "Bob"));
        Assert.Equal("Bob", store.Find(a.Id)!.Category);
        Assert.Equal(new[] { "Bob" }, store.Categories);
    }

    [Fact]
    public void Delete_keeps_clips_without_a_category()
    {
        var store = NewStore();
        var a = AddClip(store, "a", "Bob");
        store.DeleteCategory("bob");
        Assert.Null(store.Find(a.Id)!.Category);
        Assert.Empty(store.Categories);
        Assert.Empty(NewStore().Categories);
    }

    [Fact]
    public void Category_names_are_cleaned()
    {
        Assert.Null(ClipLibraryStore.CleanCategory("   "));
        Assert.Equal("Big Bob", ClipLibraryStore.CleanCategory("  Big   Bob "));
        Assert.Equal(40, ClipLibraryStore.CleanCategory(new string('x', 100))!.Length);
        Assert.Throws<ArgumentException>(() => NewStore().AddCategory(" "));
    }

    [Fact]
    public void Corrupt_categories_file_is_ignored()
    {
        File.WriteAllText(_paths.CategoriesFile, "{ nope");
        var store = NewStore();
        Assert.Empty(store.Categories);
        store.AddCategory("Bob");
        Assert.Equal(new[] { "Bob" }, NewStore().Categories);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
