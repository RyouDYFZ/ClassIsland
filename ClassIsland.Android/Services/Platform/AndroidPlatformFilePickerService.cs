using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClassIsland.Core;
using ClassIsland.Core.Helpers;
using ClassIsland.Platforms.Abstraction.Services;
using ClassIsland.Platforms.Abstraction.Stubs.Services;

namespace ClassIsland.Android.Services.Platform;

/// <summary>
/// 将 Android SAF 内容复制到应用可持续访问的本地目录。
/// </summary>
internal sealed class AndroidPlatformFilePickerService : AvaloniaDefaultPlatformFilePickerService,
    IPersistentFilePickerService
{
    private const string TemporaryPickerFolderName = "AndroidFilePicker";
    private static readonly TimeSpan TemporaryItemRetention = TimeSpan.FromDays(7);
    private int _temporaryItemsCleaned;

    private StorageItemMaterializer CreateTemporaryMaterializer()
    {
        var materializer = new StorageItemMaterializer(Path.Combine(
            CommonDirectories.AppTempFolderPath,
            TemporaryPickerFolderName));
        if (Interlocked.Exchange(ref _temporaryItemsCleaned, 1) == 0)
        {
            materializer.DeleteOperationsOlderThan(TemporaryItemRetention);
        }

        return materializer;
    }

    public override async Task<List<string>> OpenFilesPickerAsync(
        FilePickerOpenOptions options,
        TopLevel root)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(root);

        var files = await root.StorageProvider.OpenFilePickerAsync(options);
        return await CreateTemporaryMaterializer().MaterializeFilesAsync(files);
    }

    public override Task<List<string>> MaterializeFilesAsync(
        IReadOnlyList<IStorageFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return CreateTemporaryMaterializer().MaterializeFilesAsync(files);
    }

    public async Task<List<string>> OpenPersistentFilesPickerAsync(
        FilePickerOpenOptions options,
        TopLevel root)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(root);

        var files = await root.StorageProvider.OpenFilePickerAsync(options);
        return await PersistentImportedFileService.ImportAsync(files);
    }

    public async Task<List<string>> OpenPersistentFoldersPickerAsync(
        FolderPickerOpenOptions options,
        TopLevel root)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(root);

        var folders = await root.StorageProvider.OpenFolderPickerAsync(options);
        return await PersistentImportedFileService.ImportFoldersAsync(folders);
    }

    public override async Task<List<string>> OpenFoldersPickerAsync(
        FolderPickerOpenOptions options,
        TopLevel root)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(root);

        var folders = await root.StorageProvider.OpenFolderPickerAsync(options);
        return await CreateTemporaryMaterializer().MaterializeFoldersAsync(folders);
    }

    public override Task<IStorageFile?> GetFileAsync(string path, TopLevel root)
    {
        if (ImportedFileReference.TryResolve(path, out var resolvedPath))
        {
            path = resolvedPath;
        }

        return base.GetFileAsync(path, root);
    }

    public override Task<IStorageFolder?> GetFolderAsync(string path, TopLevel root)
    {
        if (ImportedFileReference.TryResolve(path, out var resolvedPath))
        {
            path = resolvedPath;
        }

        return base.GetFolderAsync(path, root);
    }

    public override bool IsBookmark(string? path) =>
        path?.StartsWith(ImportedFileReference.Prefix, StringComparison.Ordinal) == true ||
        base.IsBookmark(path);
}
