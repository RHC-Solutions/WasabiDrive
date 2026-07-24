using WasabiDrive.Core.Sync;
using Xunit;

namespace WasabiDrive.Tests;

public class SyncReconcilerTests
{
    private static SyncEntry Known(long size = 100, long localTicks = 1000, string etag = "abc") =>
        new() { Key = "k", ETag = etag, Size = size, LocalModifiedUtcTicks = localTicks };

    [Fact]
    public void Upload_NeverSynced_Uploads()
    {
        Assert.Equal(UploadAction.Upload,
            SyncReconciler.DecideUpload(10, 5, known: null, remoteChangedSinceSync: false));
    }

    [Fact]
    public void Upload_Unchanged_Skips()
    {
        var k = Known(size: 100, localTicks: 1000);
        Assert.Equal(UploadAction.Skip,
            SyncReconciler.DecideUpload(100, 1000, k, remoteChangedSinceSync: false));
    }

    [Fact]
    public void Upload_LocalChanged_Uploads()
    {
        var k = Known(size: 100, localTicks: 1000);
        Assert.Equal(UploadAction.Upload,
            SyncReconciler.DecideUpload(140, 2000, k, remoteChangedSinceSync: false));
    }

    [Fact]
    public void Upload_BothChanged_Conflict()
    {
        var k = Known(size: 100, localTicks: 1000);
        Assert.Equal(UploadAction.Conflict,
            SyncReconciler.DecideUpload(140, 2000, k, remoteChangedSinceSync: true));
    }

    [Fact]
    public void Remote_NewObject_CreatesPlaceholder()
    {
        Assert.Equal(RemoteAction.CreatePlaceholder,
            SyncReconciler.DecideRemote("etag1", remoteExists: true, known: null, localExists: false, localDirty: false));
    }

    [Fact]
    public void Remote_Unchanged_Skips()
    {
        var k = Known(etag: "etag1");
        Assert.Equal(RemoteAction.Skip,
            SyncReconciler.DecideRemote("etag1", remoteExists: true, k, localExists: true, localDirty: false));
    }

    [Fact]
    public void Remote_Changed_UpdatesPlaceholder()
    {
        var k = Known(etag: "old");
        Assert.Equal(RemoteAction.UpdatePlaceholder,
            SyncReconciler.DecideRemote("new", remoteExists: true, k, localExists: true, localDirty: false));
    }

    [Fact]
    public void Remote_ChangedButLocalDirty_Conflict()
    {
        var k = Known(etag: "old");
        Assert.Equal(RemoteAction.Conflict,
            SyncReconciler.DecideRemote("new", remoteExists: true, k, localExists: true, localDirty: true));
    }

    [Fact]
    public void Remote_DeletedRemotely_DeletesLocalWhenClean()
    {
        var k = Known();
        Assert.Equal(RemoteAction.DeleteLocal,
            SyncReconciler.DecideRemote(null, remoteExists: false, k, localExists: true, localDirty: false));
    }

    [Fact]
    public void Remote_DeletedRemotelyButLocalDirty_Conflict()
    {
        var k = Known();
        Assert.Equal(RemoteAction.Conflict,
            SyncReconciler.DecideRemote(null, remoteExists: false, k, localExists: true, localDirty: true));
    }
}
