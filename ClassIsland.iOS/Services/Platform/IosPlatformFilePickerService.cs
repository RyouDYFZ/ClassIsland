using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClassIsland.Core;
using ClassIsland.Core.Helpers;
using ClassIsland.Platforms.Abstraction.Services;
using ClassIsland.Platforms.Abstraction.Stubs.Services;

namespace ClassIsland.iOS.Services.Platform;

/// <summary>
/// 在 security-scoped resource 有效期间将选择内容暂存到应用沙盒。
/// </summary>
internal sealed class IosPlatformFilePickerService : AvaloniaDefaultPlatformFilePickerService,
    IPersistentFilePickerService
{
    private const string TemporaryPickerFolderName = "iOSFilePicker";
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
        if (files.Count == 0)
        {
            return [];
        }

        // 每个长期引用使用独立目录，清理未引用资源时不会误删同批选择的其它文件。
        var materializer = new StorageItemMaterializer(
            CommonDirectories.AppImportedFilesFolderPath);
        var result = new List<string>(files.Count);
        var createdPaths = new List<string>(files.Count);
        try
        {
            foreach (var file in files)
            {
                var path = AssertSingle(await materializer.MaterializeFilesAsync([file]));
                createdPaths.Add(path);
                result.Add(ImportedFileReference.Create(path));
            }

            return result;
        }
        catch
        {
            foreach (var file in files)
            {
                file.Dispose();
            }

            foreach (var path in createdPaths)
            {
                try
                {
                    var itemDirectory = Directory.GetParent(path)?.FullName;
                    if (itemDirectory != null)
                    {
                        Directory.Delete(itemDirectory, true);
                    }
                }
                catch
                {
                    // 保留原始选择/复制异常。
                }
            }

            throw;
        }

        static string AssertSingle(List<string> paths) => paths.Count == 1
            ? paths[0]
            : throw new InvalidOperationException("持久文件导入没有生成唯一目标。");
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

}
