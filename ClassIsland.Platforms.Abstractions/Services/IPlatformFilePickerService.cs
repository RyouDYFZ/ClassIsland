using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ClassIsland.Platforms.Abstraction.Services;

/// <summary>
/// 平台自定义文件选取服务
/// </summary>
public interface IPlatformFilePickerService
{
    /// <summary>
    /// 打开文件打开选择器
    /// </summary>
    /// <param name="options">文件浏览器选项</param>
    /// <param name="root">根窗口</param>
    /// <returns>选择的文件完整路径列表</returns>
    Task<List<string>> OpenFilesPickerAsync(FilePickerOpenOptions options, TopLevel root);

    /// <summary>
    /// 将平台提供的文件转换为调用方可持续读取的本地路径。
    /// </summary>
    /// <param name="files">平台提供的文件。</param>
    /// <returns>可持续读取的本地路径列表。</returns>
    /// <remarks>平台实现可能在转换后释放传入文件，调用方不得继续使用这些对象。</remarks>
    Task<List<string>> MaterializeFilesAsync(IReadOnlyList<IStorageFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return PlatformFileMaterializationFallback.MaterializeFilesAsync(files);
    }

    /// <summary>
    /// 打开文件保存选择器
    /// </summary>
    /// <param name="options">文件浏览器选项</param>
    /// <param name="root">根窗口</param>
    /// <returns>选择的文件路径</returns>
    Task<string?> SaveFilePickerAsync(FilePickerSaveOptions options, TopLevel root);

    /// <summary>
    /// 选择保存目标，并在平台授予的写入权限有效期内写入内容。
    /// </summary>
    /// <param name="options">文件浏览器选项</param>
    /// <param name="root">根窗口</param>
    /// <param name="writer">向已授权目标流写入内容的回调</param>
    /// <returns>保存目标的本地路径或显示名称；取消时返回 <see langword="null"/>。</returns>
    async Task<string?> SaveFileAsync(
        FilePickerSaveOptions options,
        TopLevel root,
        Func<Stream, Task> writer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(writer);

        var path = await SaveFilePickerAsync(options, root);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous);
        await writer(output);
        await output.FlushAsync();
        return path;
    }

    /// <summary>
    /// 打开文件夹打开选择器
    /// </summary>
    /// <param name="options">文件浏览器选项</param>
    /// <param name="root">根窗口</param>
    /// <returns>选择的文件夹完整路径列表</returns>
    Task<List<string>> OpenFoldersPickerAsync(FolderPickerOpenOptions options, TopLevel root);

    /// <summary>
    /// 获取一个文件。
    /// </summary>
    /// <param name="path">文件路径或书签</param>
    /// <param name="root">根窗口</param>
    /// <returns>打开的文件流</returns>
    Task<IStorageFile?> GetFileAsync(string path, TopLevel root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(root);
        return root.StorageProvider.TryGetFileFromPathAsync(path);
    }

    /// <summary>
    /// 获取一个文件夹。
    /// </summary>
    /// <param name="path">文件路径或书签</param>
    /// <param name="root">根窗口</param>
    /// <returns>打开的文件流</returns>
    Task<IStorageFolder?> GetFolderAsync(string path, TopLevel root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(root);
        return root.StorageProvider.TryGetFolderFromPathAsync(path);
    }

    /// <summary>
    /// 判断该路径是否是间接的书签
    /// </summary>
    /// <param name="path">文件路径或书签</param>
    /// <returns>是否是书签</returns>
    bool IsBookmark(string? path) => false;

}

/// <summary>
/// 为平台文件选择器实现提供兼容的本地路径物化行为。
/// </summary>
internal static class PlatformFileMaterializationFallback
{
    private const string TemporaryMaterializationFolderName = "ClassIslandFilePicker";
    private static readonly TimeSpan TemporaryItemRetention = TimeSpan.FromDays(7);
    private static int _temporaryItemsCleaned;

    public static Task<List<string>> MaterializeFilesAsync(
        IReadOnlyList<IStorageFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return MaterializeFilesCoreAsync(files);
    }

    private static async Task<List<string>> MaterializeFilesCoreAsync(
        IReadOnlyList<IStorageFile> files)
    {
        var paths = new string?[files.Count];
        var nonLocalFiles = new List<IStorageFile>();
        var nonLocalIndexes = new List<int>();
        var nextItemIndex = 0;
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                nextItemIndex = index;
                if (file.TryGetLocalPath() is { } path)
                {
                    paths[index] = path;
                    file.Dispose();
                }
                else
                {
                    nonLocalFiles.Add(file);
                    nonLocalIndexes.Add(index);
                }

                nextItemIndex = index + 1;
            }
        }
        catch
        {
            DisposeItems(nonLocalFiles, 0);
            DisposeItems(files, nextItemIndex);
            throw;
        }

        if (nonLocalFiles.Count > 0)
        {
            StorageItemMaterializer materializer;
            try
            {
                materializer = CreateTemporaryMaterializer();
            }
            catch
            {
                DisposeItems(nonLocalFiles, 0);
                throw;
            }

            var materializedPaths = await materializer.MaterializeFilesAsync(nonLocalFiles);
            if (materializedPaths.Count != nonLocalIndexes.Count)
            {
                DeleteMaterializedItems(materializedPaths);
                throw new InvalidOperationException("平台文件暂存结果与选择项数量不一致。");
            }

            for (var index = 0; index < materializedPaths.Count; index++)
            {
                paths[nonLocalIndexes[index]] = materializedPaths[index];
            }
        }

        return paths.OfType<string>().ToList();
    }

    private static StorageItemMaterializer CreateTemporaryMaterializer()
    {
        var materializer = new StorageItemMaterializer(Path.Combine(
            Path.GetTempPath(),
            TemporaryMaterializationFolderName));
        if (Interlocked.Exchange(ref _temporaryItemsCleaned, 1) == 0)
        {
            materializer.DeleteOperationsOlderThan(TemporaryItemRetention);
        }

        return materializer;
    }

    private static void DeleteMaterializedItems(IEnumerable<string> paths)
    {
        foreach (var directory in paths
                     .Select(Path.GetDirectoryName)
                     .OfType<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // 保留结果数量不一致异常。
            }
        }
    }

    private static void DisposeItems<T>(IReadOnlyList<T> items, int startIndex)
        where T : IStorageItem
    {
        for (var index = startIndex; index < items.Count; index++)
        {
            try
            {
                items[index].Dispose();
            }
            catch
            {
                // 清理失败不能掩盖原始物化异常。
            }
        }
    }
}
