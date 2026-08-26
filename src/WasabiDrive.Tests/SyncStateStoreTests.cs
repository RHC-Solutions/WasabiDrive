using System.Text.Json;
using WasabiDrive.Core.Sync;
using Xunit;

namespace WasabiDrive.Tests;

/// <summary>
/// Covers the per-mapping sync state store. The behaviour that matters most here is scoping: the
/// on-demand folder only ever reconciles one directory at a time, so every query it makes must be
/// answerable without touching keys outside that directory. Before 0.9 the store was a JSON
/// document rewritten in full on every write, which made both the scoping and the cost impossible.
/// </summary>
public class SyncStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wasabidrive-tests", Guid.NewGuid().ToString("N"));

    private string PathFor(string name = "state.db")
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, name);
    }

    private static SyncEntry Entry(string key, string? etag = "e", long size = 1) =>
        new() { Key = key, ETag = etag, Size = size, RemoteModifiedUtcTicks = 42, LocalModifiedUtcTicks = 7 };

    [Fact]
    public void SetThenGet_RoundTripsEveryField()
    {
        using var store = new SyncStateStore(PathFor());
        store.Set(new SyncEntry
        {
            Key = "a/b.txt",
            ETag = "\"abc\"",
            Size = 1234,
            RemoteModifiedUtcTicks = 555,
            LocalModifiedUtcTicks = 666,
        });

        var read = store.Get("a/b.txt");
        Assert.NotNull(read);
        Assert.Equal("a/b.txt", read!.Key);
        Assert.Equal("\"abc\"", read.ETag);
        Assert.Equal(1234, read.Size);
        Assert.Equal(555, read.RemoteModifiedUtcTicks);
        Assert.Equal(666, read.LocalModifiedUtcTicks);
    }

    [Fact]
    public void Get_ReturnsNullForAnUnknownKey()
    {
        using var store = new SyncStateStore(PathFor());
        Assert.Null(store.Get("nope"));
    }

    [Fact]
    public void Set_OverwritesAnExistingKeyRatherThanDuplicatingIt()
    {
        using var store = new SyncStateStore(PathFor());
        store.Set(Entry("k", "first", 1));
        store.Set(Entry("k", "second", 2));

        Assert.Equal(1, store.Count);
        Assert.Equal("second", store.Get("k")!.ETag);
        Assert.Equal(2, store.Get("k")!.Size);
    }

    [Fact]
    public void SetMany_WritesEveryEntry()
    {
        using var store = new SyncStateStore(PathFor());
        store.SetMany(Enumerable.Range(0, 500).Select(i => Entry($"docs/{i:D4}.txt")));

        Assert.Equal(500, store.Count);
        Assert.NotNull(store.Get("docs/0499.txt"));
    }

    [Fact]
    public void SetMany_HandlesANullETag()
    {
        using var store = new SyncStateStore(PathFor());
        store.SetMany(new[] { Entry("k", etag: null) });
        Assert.Null(store.Get("k")!.ETag);
    }

    [Fact]
    public void KeysWithPrefix_ReturnsOnlyTheMatchingSubtree()
    {
        using var store = new SyncStateStore(PathFor());
        store.SetMany(new[]
        {
            Entry("photos/a.jpg"), Entry("photos/sub/b.jpg"),
            Entry("photosaurus/c.jpg"), // shares the first six characters but is a different folder
            Entry("videos/d.mp4"),
        });

        var keys = store.KeysWithPrefix("photos/");

        Assert.Equal(new[] { "photos/a.jpg", "photos/sub/b.jpg" }, keys);
    }

    [Fact]
    public void KeysWithPrefix_WithAnEmptyPrefixReturnsEverything()
    {
        using var store = new SyncStateStore(PathFor());
        store.SetMany(new[] { Entry("a"), Entry("b") });
        Assert.Equal(2, store.KeysWithPrefix(string.Empty).Count);
    }

    [Fact]
    public void EntriesWithPrefix_IsScopedAndOrdered()
    {
        using var store = new SyncStateStore(PathFor());
        store.SetMany(new[] { Entry("d/z"), Entry("d/a"), Entry("e/a") });

        var entries = store.EntriesWithPrefix("d/");

        Assert.Equal(new[] { "d/a", "d/z" }, entries.Select(e => e.Key));
    }

    [Fact]
    public void PrefixQueries_SurviveAKeyEndingInTheHighestCharacter()
    {
        // The range scan derives its upper bound by bumping the prefix's last character, so a
        // prefix that cannot be bumped has to fall back rather than produce an empty range.
        using var store = new SyncStateStore(PathFor());
        var prefix = "odd" + char.MaxValue;
        store.SetMany(new[] { Entry(prefix + "child"), Entry("other") });

        Assert.Equal(new[] { prefix + "child" }, store.KeysWithPrefix(prefix));
    }

    [Fact]
    public void Remove_DropsOnlyTheNamedKey()
    {
        using var store = new SyncStateStore(PathFor());
        store.SetMany(new[] { Entry("a"), Entry("b") });

        store.Remove("a");

        Assert.Null(store.Get("a"));
        Assert.NotNull(store.Get("b"));
    }

    [Fact]
    public void RemoveMany_DropsEveryNamedKeyAndIgnoresUnknownOnes()
    {
        using var store = new SyncStateStore(PathFor());
        store.SetMany(new[] { Entry("a"), Entry("b"), Entry("c") });

        store.RemoveMany(new[] { "a", "c", "never-existed" });

        Assert.Equal(1, store.Count);
        Assert.NotNull(store.Get("b"));
    }

    [Fact]
    public void ListedDirectories_ReturnsShallowestFirst()
    {
        using var store = new SyncStateStore(PathFor());
        store.MarkDirectoryListed("a/b/c/", DateTime.UtcNow);
        store.MarkDirectoryListed(string.Empty, DateTime.UtcNow);
        store.MarkDirectoryListed("a/", DateTime.UtcNow);

        Assert.Equal(new[] { "", "a/", "a/b/c/" }, store.ListedDirectories());
    }

    [Fact]
    public void MarkDirectoryListed_IsIdempotent()
    {
        using var store = new SyncStateStore(PathFor());
        store.MarkDirectoryListed("a/", DateTime.UtcNow);
        store.MarkDirectoryListed("a/", DateTime.UtcNow.AddMinutes(1));

        Assert.Single(store.ListedDirectories());
    }

    [Fact]
    public void ForgetDirectoryListings_DropsTheSubtreesListingsButKeepsItsEntries()
    {
        // A rename needs this split: the listing is stale immediately, but the per-object state is
        // still what tells the mover which keys to copy across.
        using var store = new SyncStateStore(PathFor());
        store.MarkDirectoryListed("old/", DateTime.UtcNow);
        store.MarkDirectoryListed("old/deep/", DateTime.UtcNow);
        store.MarkDirectoryListed("kept/", DateTime.UtcNow);
        store.SetMany(new[] { Entry("old/file.txt"), Entry("kept/file.txt") });

        store.ForgetDirectoryListings("old/");

        Assert.Equal(new[] { "kept/" }, store.ListedDirectories());
        Assert.NotNull(store.Get("old/file.txt"));
        Assert.NotNull(store.Get("kept/file.txt"));
    }

    [Fact]
    public void ForgetDirectoryTree_DropsBothTheListingsAndTheEntriesBeneathIt()
    {
        using var store = new SyncStateStore(PathFor());
        store.MarkDirectoryListed("gone/", DateTime.UtcNow);
        store.MarkDirectoryListed("stays/", DateTime.UtcNow);
        store.SetMany(new[] { Entry("gone/a.txt"), Entry("gone/deep/b.txt"), Entry("stays/c.txt") });

        store.ForgetDirectoryTree("gone/");

        Assert.Equal(new[] { "stays/" }, store.ListedDirectories());
        Assert.Null(store.Get("gone/a.txt"));
        Assert.Null(store.Get("gone/deep/b.txt"));
        Assert.NotNull(store.Get("stays/c.txt"));
    }

    [Fact]
    public void State_PersistsAcrossReopening()
    {
        var path = PathFor();
        using (var store = new SyncStateStore(path))
        {
            store.Set(Entry("a/b.txt", "tag", 9));
            store.MarkDirectoryListed("a/", DateTime.UtcNow);
        }

        using var reopened = new SyncStateStore(path);
        Assert.Equal("tag", reopened.Get("a/b.txt")!.ETag);
        Assert.Equal(new[] { "a/" }, reopened.ListedDirectories());
    }

    [Fact]
    public void LegacyJsonState_IsImportedOnceAndThenSetAside()
    {
        // Folders enabled before 0.9 have a .json state file next to where the database now goes.
        // It has to be carried over, or every already-synced file looks brand new.
        var dbPath = PathFor("mapping.db");
        var jsonPath = Path.Combine(_dir, "mapping.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new[]
        {
            Entry("legacy/one.txt", "l1", 11),
            Entry("legacy/two.txt", "l2", 22),
        }));

        using (var store = new SyncStateStore(dbPath))
        {
            Assert.Equal(2, store.Count);
            Assert.Equal("l1", store.Get("legacy/one.txt")!.ETag);
            Assert.Equal(22, store.Get("legacy/two.txt")!.Size);
        }

        Assert.False(File.Exists(jsonPath));
        Assert.True(File.Exists(jsonPath + ".migrated"));
    }

    [Fact]
    public void LegacyJsonState_ThatIsCorruptDoesNotStopTheStoreOpening()
    {
        var dbPath = PathFor("mapping.db");
        File.WriteAllText(Path.Combine(_dir, "mapping.json"), "{ this is not the file you expected");

        using var store = new SyncStateStore(dbPath);

        Assert.Equal(0, store.Count);
        store.Set(Entry("still/works.txt"));
        Assert.NotNull(store.Get("still/works.txt"));
    }

    [Fact]
    public void ConstructorAcceptsALegacyJsonPathAndStillOpensTheDatabase()
    {
        // FilePathFor now hands out a .db path, but a caller holding an older .json path should
        // land on the same database rather than silently starting a second one.
        var jsonStylePath = PathFor("mapping.json");

        using (var store = new SyncStateStore(jsonStylePath)) store.Set(Entry("k"));

        Assert.True(File.Exists(Path.Combine(_dir, "mapping.db")));
        using var reopened = new SyncStateStore(Path.Combine(_dir, "mapping.db"));
        Assert.NotNull(reopened.Get("k"));
    }

    [Fact]
    public void FilePathFor_IsPerMappingAndUsesTheDatabaseExtension()
    {
        var id = Guid.NewGuid();
        var path = SyncStateStore.FilePathFor(id);

        Assert.EndsWith(".db", path, StringComparison.Ordinal);
        Assert.Contains(id.ToString("N"), path, StringComparison.Ordinal);
        Assert.NotEqual(path, SyncStateStore.FilePathFor(Guid.NewGuid()));
    }

    [Fact]
    public void ManyEntriesStayCorrectlyScoped()
    {
        // The old store re-serialised every key on every write, so this many entries took quadratic
        // time and had to be held in memory in full. The point here is that a directory-sized query
        // still answers with exactly its own slice once the store is large.
        using var store = new SyncStateStore(PathFor());
        store.SetMany(Enumerable.Range(0, 20_000).Select(i => Entry($"bulk/{i % 20:D2}/{i:D6}.bin")));

        var slice = store.KeysWithPrefix("bulk/07/");

        Assert.Equal(20_000 / 20, slice.Count);
        Assert.All(slice, k => Assert.StartsWith("bulk/07/", k, StringComparison.Ordinal));
        Assert.Equal(20_000, store.Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }
}
