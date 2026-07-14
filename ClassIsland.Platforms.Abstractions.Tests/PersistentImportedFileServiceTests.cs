using System.Reflection;
using System.Text;
using Avalonia.Platform.Storage;
using ClassIsland.Core;
using ClassIsland.Core.Helpers;
using Xunit;

namespace ClassIsland.Platforms.Abstractions.Tests;

[CollectionDefinition("CommonDirectories serial tests", DisableParallelization = true)]
public sealed class CommonDirectoriesSerialTestCollection
{
}

[Collection("CommonDirectories serial tests")]
public sealed class PersistentImportedFileServiceTests
{
    [Fact]
    public Task ImportFiles_CopiesContentAndCreatesPortableReference() =>
        WithIsolatedAppRootAsync(async () =>
        {
            var disposeCount = 0;
            var file = CreateStorageFile(
                "custom sound #1.wav",
                "audio-content",
                onDisposed: () => disposeCount++);

            var reference = Assert.Single(
                await PersistentImportedFileService.ImportAsync([file]));

            Assert.StartsWith(ImportedFileReference.Prefix, reference, StringComparison.Ordinal);
            Assert.EndsWith("/custom%20sound%20%231.wav", reference, StringComparison.Ordinal);
            Assert.True(ImportedFileReference.TryResolve(reference, out var path));
            Assert.Equal("audio-content", await File.ReadAllTextAsync(path));
            Assert.Equal(1, disposeCount);
        });

    [Fact]
    public Task ImportFiles_RollsBackEarlierItemsAndDisposesEverySelection() =>
        WithIsolatedAppRootAsync(async () =>
        {
            var disposeCounts = new int[3];
            var files = new IStorageFile[]
            {
                CreateStorageFile("first.txt", "first", onDisposed: () => disposeCounts[0]++),
                CreateStorageFile(
                    "failure.txt",
                    readException: new IOException("read failed"),
                    onDisposed: () => disposeCounts[1]++),
                CreateStorageFile("remaining.txt", "remaining", onDisposed: () => disposeCounts[2]++)
            };

            await Assert.ThrowsAsync<IOException>(() =>
                PersistentImportedFileService.ImportAsync(files));

            Assert.Equal([1, 1, 1], disposeCounts);
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                CommonDirectories.AppImportedFilesFolderPath));
        });

    [Fact]
    public Task ImportFolders_CopiesContentsAndCreatesPortableReference() =>
        WithIsolatedAppRootAsync(async () =>
        {
            var folderDisposeCount = 0;
            var childDisposeCount = 0;
            var folder = CreateStorageFolder(
                "Resources",
                [CreateStorageFile("tone.wav", "tone", onDisposed: () => childDisposeCount++)],
                () => folderDisposeCount++);

            var reference = Assert.Single(
                await PersistentImportedFileService.ImportFoldersAsync([folder]));

            Assert.EndsWith("/Resources", reference, StringComparison.Ordinal);
            Assert.True(ImportedFileReference.TryResolve(reference, out var path));
            Assert.Equal("tone", await File.ReadAllTextAsync(Path.Combine(path, "tone.wav")));
            Assert.Equal(1, folderDisposeCount);
            Assert.Equal(1, childDisposeCount);
        });

    [Fact]
    public Task ImportFolders_RollsBackEarlierItemsAndDisposesEverySelection() =>
        WithIsolatedAppRootAsync(async () =>
        {
            var folderDisposeCounts = new int[3];
            var firstChildDisposeCount = 0;
            var failingChildDisposeCount = 0;
            var folders = new IStorageFolder[]
            {
                CreateStorageFolder(
                    "First",
                    [CreateStorageFile("first.txt", "first", onDisposed: () => firstChildDisposeCount++)],
                    () => folderDisposeCounts[0]++),
                CreateStorageFolder(
                    "Failure",
                    [CreateStorageFile(
                        "failure.txt",
                        readException: new IOException("read failed"),
                        onDisposed: () => failingChildDisposeCount++)],
                    () => folderDisposeCounts[1]++),
                CreateStorageFolder("Remaining", [], () => folderDisposeCounts[2]++)
            };

            await Assert.ThrowsAsync<IOException>(() =>
                PersistentImportedFileService.ImportFoldersAsync(folders));

            Assert.Equal([1, 1, 1], folderDisposeCounts);
            Assert.Equal(1, firstChildDisposeCount);
            Assert.Equal(1, failingChildDisposeCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                CommonDirectories.AppImportedFilesFolderPath));
        });

    [Fact]
    public Task ImportEmptySelections_DoesNotCreateImportedFilesDirectory() =>
        WithIsolatedAppRootAsync(async () =>
        {
            Assert.Empty(await PersistentImportedFileService.ImportAsync([]));
            Assert.Empty(await PersistentImportedFileService.ImportFoldersAsync([]));
            Assert.False(Directory.Exists(CommonDirectories.AppImportedFilesFolderPath));
        });

    private static async Task WithIsolatedAppRootAsync(Func<Task> action)
    {
        using var root = new TemporaryDirectory();
        var previousAppRoot = CommonDirectories.AppRootFolderPath;
        CommonDirectories.AppRootFolderPath = root.Path;
        try
        {
            await action();
        }
        finally
        {
            CommonDirectories.AppRootFolderPath = previousAppRoot;
        }
    }

    private static IStorageFile CreateStorageFile(
        string name,
        string content = "",
        Exception? readException = null,
        Action? onDisposed = null) =>
        CreateProxy<IStorageFile>((method, _) => method.Name switch
        {
            "get_Name" => name,
            "get_Path" => new Uri($"virtual:///{Uri.EscapeDataString(name)}"),
            "get_CanBookmark" => false,
            "OpenReadAsync" => readException is null
                ? Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content)))
                : Task.FromException<Stream>(readException),
            "Dispose" => Invoke(onDisposed),
            _ => throw new NotSupportedException($"Unexpected storage file call: {method.Name}")
        });

    private static IStorageFolder CreateStorageFolder(
        string name,
        IReadOnlyList<IStorageItem> items,
        Action? onDisposed = null) =>
        CreateProxy<IStorageFolder>((method, _) => method.Name switch
        {
            "get_Name" => name,
            "get_Path" => new Uri($"virtual:///{Uri.EscapeDataString(name)}"),
            "get_CanBookmark" => false,
            "GetItemsAsync" => EnumerateItems(items),
            "Dispose" => Invoke(onDisposed),
            _ => throw new NotSupportedException($"Unexpected storage folder call: {method.Name}")
        });

    private static async IAsyncEnumerable<IStorageItem> EnumerateItems(
        IEnumerable<IStorageItem> items)
    {
        await Task.Yield();
        foreach (var item in items)
        {
            yield return item;
        }
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, CallbackDispatchProxy>();
        ((CallbackDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? Invoke(Action? action)
    {
        action?.Invoke();
        return null;
    }

    public class CallbackDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod ?? throw new InvalidOperationException("Proxy method is unavailable."), args);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("classisland-persistent-import-").FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }
}
