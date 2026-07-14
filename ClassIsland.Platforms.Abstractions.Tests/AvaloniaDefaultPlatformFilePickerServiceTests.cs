using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClassIsland.Platforms.Abstraction.Services;
using ClassIsland.Platforms.Abstraction.Stubs.Services;
using Xunit;

namespace ClassIsland.Platforms.Abstractions.Tests;

public sealed class AvaloniaDefaultPlatformFilePickerServiceTests
{
    [Fact]
    public void MaterializeFiles_RejectsNullInput()
    {
        var service = new AvaloniaDefaultPlatformFilePickerService();

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = service.MaterializeFilesAsync(null!);
        });
    }

    [Fact]
    public async Task MaterializeFiles_ReturnsLocalPathWithoutReadingFile()
    {
        using var scope = new TemporaryDirectory();
        var path = Path.Combine(scope.Path, "plugin.cipx");
        await File.WriteAllTextAsync(path, "plugin");
        using var file = CreateStorageFile(path);
        var service = new AvaloniaDefaultPlatformFilePickerService();

        var paths = await service.MaterializeFilesAsync([file]);

        Assert.Equal(Path.GetFullPath(path), Assert.Single(paths));
    }

    [Fact]
    public async Task AddedMaterializeMethod_HasLegacyImplementationFallback()
    {
        using var scope = new TemporaryDirectory();
        var path = Path.Combine(scope.Path, "legacy-plugin.cipx");
        await File.WriteAllTextAsync(path, "plugin");
        var service = (IPlatformFilePickerService)new LegacyFilePickerService();

        var paths = await service.MaterializeFilesAsync([CreateStorageFile(path)]);

        Assert.Equal(Path.GetFullPath(path), Assert.Single(paths));
    }

    [Fact]
    public async Task AddedMaterializeMethod_CopiesVirtualFileForLegacyImplementation()
    {
        var disposed = false;
        var service = (IPlatformFilePickerService)new LegacyFilePickerService();

        var paths = await service.MaterializeFilesAsync([
            CreateVirtualFile(
                "legacy-virtual.txt",
                readStream: new MemoryStream(Encoding.UTF8.GetBytes("virtual")),
                onDisposed: () => disposed = true)
        ]);

        var path = Assert.Single(paths);
        try
        {
            Assert.Equal("virtual", await File.ReadAllTextAsync(path));
            Assert.True(disposed);
        }
        finally
        {
            Directory.Delete(Directory.GetParent(path)!.FullName, true);
        }
    }

    [Fact]
    public async Task AddedMaterializeMethod_DisposesAllVirtualFilesWhenCopyFails()
    {
        var disposeCounts = new int[3];
        var firstFileName = $"first-{Guid.NewGuid():N}.txt";
        var service = (IPlatformFilePickerService)new LegacyFilePickerService();

        await Assert.ThrowsAsync<IOException>(() => service.MaterializeFilesAsync([
            CreateVirtualFile(
                firstFileName,
                readStream: new MemoryStream(Encoding.UTF8.GetBytes("first")),
                onDisposed: () => disposeCounts[0]++),
            CreateVirtualFile(
                "failure.txt",
                readException: new IOException("read failed"),
                onDisposed: () => disposeCounts[1]++),
            CreateVirtualFile(
                "remaining.txt",
                readStream: new MemoryStream(Encoding.UTF8.GetBytes("remaining")),
                onDisposed: () => disposeCounts[2]++)
        ]));

        Assert.Equal([1, 1, 1], disposeCounts);
        var stagingRoot = Path.Combine(Path.GetTempPath(), "ClassIslandFilePicker");
        if (Directory.Exists(stagingRoot))
        {
            Assert.Empty(Directory.EnumerateFiles(
                stagingRoot,
                firstFileName,
                SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task OpenFilesPicker_ReturnsLocalPathAndPortableBookmark()
    {
        using var scope = new TemporaryDirectory();
        var localPath = Path.Combine(scope.Path, "local.txt");
        await File.WriteAllTextAsync(localPath, "local");
        var bookmarkDisposed = false;
        var bookmarkFile = CreateVirtualFile(
            "cloud.txt",
            bookmark: "file-bookmark",
            onDisposed: () => bookmarkDisposed = true);
        var provider = CreateStorageProvider(
            openFiles: [CreateStorageFile(localPath), bookmarkFile]);
        var service = new AvaloniaDefaultPlatformFilePickerService(provider);

        var result = await service.OpenFilesPickerAsync(
            new FilePickerOpenOptions(),
            CreateDummyTopLevel());

        Assert.Equal(
            [Path.GetFullPath(localPath), "_elysia-bookmark:file-bookmark"],
            result);
        Assert.True(bookmarkDisposed);
    }

    [Fact]
    public async Task OpenFilesPicker_MaterializesVirtualFileWithoutBookmark()
    {
        var disposed = false;
        var provider = CreateStorageProvider(openFiles:
        [
            CreateVirtualFile(
                "provider-only.txt",
                readStream: new MemoryStream(Encoding.UTF8.GetBytes("provider-content")),
                onDisposed: () => disposed = true)
        ]);
        var service = new AvaloniaDefaultPlatformFilePickerService(provider);

        var result = await service.OpenFilesPickerAsync(
            new FilePickerOpenOptions(),
            CreateDummyTopLevel());

        var path = Assert.Single(result);
        try
        {
            Assert.Equal("provider-content", await File.ReadAllTextAsync(path));
            Assert.True(disposed);
        }
        finally
        {
            Directory.Delete(Directory.GetParent(path)!.FullName, true);
        }
    }

    [Fact]
    public async Task MaterializeFiles_CopiesVirtualFileInsteadOfDroppingIt()
    {
        var source = new MemoryStream(Encoding.UTF8.GetBytes("virtual-content"));
        var service = new AvaloniaDefaultPlatformFilePickerService();

        var result = await service.MaterializeFilesAsync([
            CreateVirtualFile("virtual.txt", readStream: source)
        ]);

        var path = Assert.Single(result);
        Assert.Equal("virtual-content", await File.ReadAllTextAsync(path));
        Directory.Delete(Directory.GetParent(path)!.FullName, true);
    }

    [Fact]
    public async Task SaveFilePicker_ReturnsBookmarkForVirtualTarget()
    {
        var provider = CreateStorageProvider(
            saveFile: CreateVirtualFile("export.cidata", bookmark: "save-bookmark"));
        var service = new AvaloniaDefaultPlatformFilePickerService(provider);

        var result = await service.SaveFilePickerAsync(
            new FilePickerSaveOptions(),
            CreateDummyTopLevel());

        Assert.Equal("_elysia-bookmark:save-bookmark", result);
    }

    [Fact]
    public async Task SaveFile_WritesAuthorizedStreamAndReturnsBookmark()
    {
        var output = new MemoryStream();
        await output.WriteAsync(Encoding.UTF8.GetBytes("stale"));
        var provider = CreateStorageProvider(
            saveFile: CreateVirtualFile(
                "export.cidata",
                bookmark: "save-bookmark",
                writeStream: output));
        var service = new AvaloniaDefaultPlatformFilePickerService(provider);

        var result = await service.SaveFileAsync(
            new FilePickerSaveOptions(),
            CreateDummyTopLevel(),
            async stream => await stream.WriteAsync(Encoding.UTF8.GetBytes("payload")));

        Assert.Equal("_elysia-bookmark:save-bookmark", result);
        Assert.Equal("payload", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task OpenFoldersPicker_ReturnsLocalPathAndPortableBookmark()
    {
        using var scope = new TemporaryDirectory();
        var localFolder = CreateStorageFolder(scope.Path);
        var bookmarkFolder = CreateVirtualFolder("Cloud", "folder-bookmark");
        var provider = CreateStorageProvider(openFolders: [localFolder, bookmarkFolder]);
        var service = new AvaloniaDefaultPlatformFilePickerService(provider);

        var result = await service.OpenFoldersPickerAsync(
            new FolderPickerOpenOptions(),
            CreateDummyTopLevel());

        Assert.Equal(
            [Path.GetFullPath(scope.Path), "_cyrene-bookmark:folder-bookmark"],
            result);
    }

    [Fact]
    public async Task GetFileAndFolder_ResolveBookmarksThroughProvider()
    {
        var file = CreateVirtualFile("cloud.txt", bookmark: "file-bookmark");
        var folder = CreateVirtualFolder("Cloud", "folder-bookmark");
        var provider = CreateStorageProvider(
            bookmarkedFile: file,
            bookmarkedFolder: folder);
        var service = new AvaloniaDefaultPlatformFilePickerService(provider);

        var resolvedFile = await service.GetFileAsync(
            "_elysia-bookmark:file-bookmark",
            CreateDummyTopLevel());
        var resolvedFolder = await service.GetFolderAsync(
            "_cyrene-bookmark:folder-bookmark",
            CreateDummyTopLevel());

        Assert.Same(file, resolvedFile);
        Assert.Same(folder, resolvedFolder);
        resolvedFile?.Dispose();
        resolvedFolder?.Dispose();
    }

    [Fact]
    public async Task LegacyDefaultSaveFile_WritesSelectedLocalPath()
    {
        using var scope = new TemporaryDirectory();
        var path = Path.Combine(scope.Path, "legacy-export.json");
        var service = (IPlatformFilePickerService)new LegacyFilePickerService(path);

        var result = await service.SaveFileAsync(
            new FilePickerSaveOptions(),
            CreateDummyTopLevel(),
            async stream => await stream.WriteAsync(Encoding.UTF8.GetBytes("legacy")));

        Assert.Equal(path, result);
        Assert.Equal("legacy", await File.ReadAllTextAsync(path));
    }

    private static IStorageFile CreateStorageFile(string path)
    {
        var type = typeof(IStorageFile).Assembly.GetType(
            "Avalonia.Platform.Storage.FileIO.BclStorageFile",
            throwOnError: true)!;
        var constructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(FileInfo)],
            modifiers: null)
            ?? throw new InvalidOperationException("Avalonia BclStorageFile constructor is unavailable.");
        return (IStorageFile)constructor.Invoke([new FileInfo(path)]);
    }

    private static IStorageFolder CreateStorageFolder(string path)
    {
        var type = typeof(IStorageFolder).Assembly.GetType(
            "Avalonia.Platform.Storage.FileIO.BclStorageFolder",
            throwOnError: true)!;
        var constructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(DirectoryInfo)],
            modifiers: null)
            ?? throw new InvalidOperationException("Avalonia BclStorageFolder constructor is unavailable.");
        return (IStorageFolder)constructor.Invoke([new DirectoryInfo(path)]);
    }

    private static IStorageProvider CreateStorageProvider(
        IReadOnlyList<IStorageFile>? openFiles = null,
        IStorageFile? saveFile = null,
        IReadOnlyList<IStorageFolder>? openFolders = null,
        IStorageBookmarkFile? bookmarkedFile = null,
        IStorageBookmarkFolder? bookmarkedFolder = null) =>
        CreateProxy<IStorageProvider>((method, _) => method.Name switch
        {
            "OpenFilePickerAsync" => Task.FromResult(openFiles ?? (IReadOnlyList<IStorageFile>)[]),
            "SaveFilePickerAsync" => Task.FromResult(saveFile),
            "OpenFolderPickerAsync" => Task.FromResult(openFolders ?? (IReadOnlyList<IStorageFolder>)[]),
            "OpenFileBookmarkAsync" => Task.FromResult(bookmarkedFile),
            "OpenFolderBookmarkAsync" => Task.FromResult(bookmarkedFolder),
            "get_CanOpen" or "get_CanSave" or "get_CanPickFolder" => true,
            _ => throw new NotSupportedException($"Unexpected storage provider call: {method.Name}")
        });

    private static IStorageBookmarkFile CreateVirtualFile(
        string name,
        string? bookmark = null,
        Stream? readStream = null,
        Stream? writeStream = null,
        Exception? readException = null,
        Action? onDisposed = null) =>
        CreateProxy<IStorageBookmarkFile>((method, _) => method.Name switch
        {
            "get_Name" => name,
            "get_Path" => new Uri($"virtual:///{Uri.EscapeDataString(name)}"),
            "get_CanBookmark" => bookmark != null,
            "SaveBookmarkAsync" => Task.FromResult(bookmark),
            "OpenReadAsync" => readException is null
                ? Task.FromResult(readStream ?? Stream.Null)
                : Task.FromException<Stream>(readException),
            "OpenWriteAsync" => Task.FromResult(writeStream ?? Stream.Null),
            "Dispose" => Invoke(onDisposed),
            _ => throw new NotSupportedException($"Unexpected storage file call: {method.Name}")
        });

    private static IStorageBookmarkFolder CreateVirtualFolder(string name, string bookmark) =>
        CreateProxy<IStorageBookmarkFolder>((method, _) => method.Name switch
        {
            "get_Name" => name,
            "get_Path" => new Uri($"virtual:///{Uri.EscapeDataString(name)}"),
            "get_CanBookmark" => true,
            "SaveBookmarkAsync" => Task.FromResult<string?>(bookmark),
            "Dispose" => null,
            _ => throw new NotSupportedException($"Unexpected storage folder call: {method.Name}")
        });

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

    private static TopLevel CreateDummyTopLevel() =>
        (TopLevel)RuntimeHelpers.GetUninitializedObject(typeof(Window));

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("classisland-default-picker-").FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }

    public class CallbackDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod ?? throw new InvalidOperationException("Proxy method is unavailable."), args);
    }

    private sealed class LegacyFilePickerService(string? savePath = null) : IPlatformFilePickerService
    {
        public Task<List<string>> OpenFilesPickerAsync(FilePickerOpenOptions options, TopLevel root) =>
            Task.FromResult<List<string>>([]);

        public Task<string?> SaveFilePickerAsync(FilePickerSaveOptions options, TopLevel root) =>
            Task.FromResult(savePath);

        public Task<List<string>> OpenFoldersPickerAsync(FolderPickerOpenOptions options, TopLevel root) =>
            Task.FromResult<List<string>>([]);
    }
}
