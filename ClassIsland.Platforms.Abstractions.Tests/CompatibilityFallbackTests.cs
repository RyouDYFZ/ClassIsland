using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClassIsland.Platforms.Abstraction.Models;
using ClassIsland.Platforms.Abstraction.Services;
using ClassIsland.Platforms.Abstraction.Stubs.Services;
using Xunit;

namespace ClassIsland.Platforms.Abstractions.Tests;

public sealed class CompatibilityFallbackTests
{
    [Fact]
    public async Task DefaultSaveFile_WritesToLegacyPickerPath()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "export.txt");
        IPlatformFilePickerService service = new LegacyFilePickerService(path);

        var result = await service.SaveFileAsync(
            new FilePickerSaveOptions(),
            CreateUnusedTopLevel(),
            async output =>
            {
                await using var writer = new StreamWriter(output, leaveOpen: true);
                await writer.WriteAsync("ClassIsland");
                await writer.FlushAsync();
            });

        Assert.Equal(path, result);
        Assert.Equal("ClassIsland", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DefaultSaveFile_ReturnsNullWhenLegacyPickerIsCancelled()
    {
        IPlatformFilePickerService service = new LegacyFilePickerService(null);
        var writerCalled = false;

        var result = await service.SaveFileAsync(
            new FilePickerSaveOptions(),
            CreateUnusedTopLevel(),
            _ =>
            {
                writerCalled = true;
                return Task.CompletedTask;
            });

        Assert.Null(result);
        Assert.False(writerCalled);
    }

    [Fact]
    public async Task DefaultStorageLookup_ValidatesArgumentsAndBookmarkFallback()
    {
        IPlatformFilePickerService service = new LegacyFilePickerService(null);
        var root = CreateUnusedTopLevel();

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetFileAsync("", root));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.GetFileAsync("file", null!));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetFolderAsync(" ", root));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.GetFolderAsync("folder", null!));
        Assert.False(service.IsBookmark(null));
        Assert.False(service.IsBookmark(string.Empty));
        Assert.False(service.IsBookmark("plain-path"));
    }

    [Fact]
    public async Task PersistentPickerExtension_UsesCapabilityOrLegacyFallback()
    {
        var root = CreateUnusedTopLevel();
        var options = new FilePickerOpenOptions();
        IPlatformFilePickerService legacy = new LegacyFilePickerService(null);
        IPlatformFilePickerService persistent = new PersistentFilePickerService();

        Assert.Equal(["legacy"], await legacy.OpenPersistentFilesPickerAsync(options, root));
        Assert.Equal(["persistent"], await persistent.OpenPersistentFilesPickerAsync(options, root));
        Assert.Equal(
            ["legacy-folder"],
            await legacy.OpenPersistentFoldersPickerAsync(new FolderPickerOpenOptions(), root));
        Assert.Equal(
            ["persistent-fallback-folder"],
            await persistent.OpenPersistentFoldersPickerAsync(new FolderPickerOpenOptions(), root));
    }

    [Fact]
    public async Task BasicModelsAndFallbackServices_RoundTripValues()
    {
        var activated = false;
        var toast = new DesktopToastContent
        {
            Title = "title",
            Body = "body",
            HeroImageUri = new Uri("https://classisland.tech/hero.png"),
            InlineImageUri = new Uri("https://classisland.tech/inline.png"),
            LogoImageUri = new Uri("https://classisland.tech/logo.png"),
            Buttons = new Dictionary<string, Action>
            {
                ["open"] = () => activated = true
            }
        };
        toast.Activated += (_, _) => activated = true;
        toast.Activated.Invoke(toast, EventArgs.Empty);
        toast.Buttons["open"]();

        Assert.Equal("title", toast.Title);
        Assert.Equal("body", toast.Body);
        Assert.NotNull(toast.HeroImageUri);
        Assert.NotNull(toast.InlineImageUri);
        Assert.NotNull(toast.LogoImageUri);
        Assert.True(activated);

        var foreground = new ForegroundWindowChangedEventArgs((nint)42);
        Assert.Equal((nint)42, foreground.Handle);

        var location = await new LocationServiceStub().GetLocationAsync();
        location.Longitude = 120.1;
        location.Latitude = 30.2;
        Assert.Equal(120.1, location.Longitude);
        Assert.Equal(30.2, location.Latitude);

        var launcher = new LauncherServiceStub();
        await launcher.LaunchPath("ignored");
        await launcher.LaunchUrl("https://classisland.tech");

    }

    private static TopLevel CreateUnusedTopLevel() =>
        (TopLevel)RuntimeHelpers.GetUninitializedObject(typeof(Window));

    private sealed class LegacyFilePickerService(string? savePath) : IPlatformFilePickerService
    {
        public Task<List<string>> OpenFilesPickerAsync(FilePickerOpenOptions options, TopLevel root) =>
            Task.FromResult<List<string>>(["legacy"]);

        public Task<string?> SaveFilePickerAsync(FilePickerSaveOptions options, TopLevel root) =>
            Task.FromResult(savePath);

        public Task<List<string>> OpenFoldersPickerAsync(FolderPickerOpenOptions options, TopLevel root) =>
            Task.FromResult<List<string>>(["legacy-folder"]);
    }

    private sealed class PersistentFilePickerService : IPlatformFilePickerService,
        IPersistentFilePickerService
    {
        public Task<List<string>> OpenFilesPickerAsync(FilePickerOpenOptions options, TopLevel root) =>
            Task.FromResult<List<string>>(["legacy"]);

        public Task<List<string>> OpenPersistentFilesPickerAsync(
            FilePickerOpenOptions options,
            TopLevel root) => Task.FromResult<List<string>>(["persistent"]);

        public Task<string?> SaveFilePickerAsync(FilePickerSaveOptions options, TopLevel root) =>
            Task.FromResult<string?>(null);

        public Task<List<string>> OpenFoldersPickerAsync(FolderPickerOpenOptions options, TopLevel root) =>
            Task.FromResult<List<string>>(["persistent-fallback-folder"]);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("classisland-compatibility-").FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }
}
