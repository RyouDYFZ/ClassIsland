using Avalonia.Platform.Storage;
using ClassIsland.Platforms.Abstraction.Services;

namespace ClassIsland.Core.Helpers;

/// <summary>
/// 将需要长期使用的平台文件或文件夹复制到应用数据目录，并生成可迁移引用。
/// </summary>
internal static class PersistentImportedFileService
{
    public static Task<List<string>> ImportAsync(
        IReadOnlyList<IStorageFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return ImportItemsAsync(
            files,
            static (materializer, file) => materializer.MaterializeFilesAsync([file]),
            "文件");
    }

    public static Task<List<string>> ImportFoldersAsync(
        IReadOnlyList<IStorageFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return ImportItemsAsync(
            folders,
            static (materializer, folder) => materializer.MaterializeFoldersAsync([folder]),
            "文件夹");
    }

    private static async Task<List<string>> ImportItemsAsync<T>(
        IReadOnlyList<T> items,
        Func<StorageItemMaterializer, T, Task<List<string>>> materializeItemAsync,
        string itemKind)
        where T : IStorageItem
    {
        if (items.Count == 0)
        {
            return [];
        }

        var materializer = new StorageItemMaterializer(
            CommonDirectories.AppImportedFilesFolderPath);
        var result = new List<string>(items.Count);
        var createdPaths = new List<string>(items.Count);
        var nextItemIndex = 0;
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                nextItemIndex = index + 1;
                var paths = await materializeItemAsync(materializer, items[index]);
                var path = paths.Count == 1
                    ? paths[0]
                    : throw new InvalidOperationException($"持久{itemKind}导入没有生成唯一目标。");
                createdPaths.Add(path);
                result.Add(ImportedFileReference.Create(path));
            }

            return result;
        }
        catch
        {
            foreach (var path in createdPaths)
            {
                TryDeleteItemDirectory(path);
            }

            throw;
        }
        finally
        {
            for (var index = nextItemIndex; index < items.Count; index++)
            {
                try
                {
                    items[index].Dispose();
                }
                catch
                {
                    // 清理失败不能掩盖原始导入异常。
                }
            }
        }
    }

    private static void TryDeleteItemDirectory(string path)
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
            // 保留原始选择或复制异常。
        }
    }
}
