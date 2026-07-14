using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClassIsland.Platforms.Abstraction.Services;

namespace ClassIsland.Platforms.Abstraction.Stubs.Services;

/// <inheritdoc />
public class AvaloniaDefaultPlatformFilePickerService : IPlatformFilePickerService
{
    private const string FileBookmarkSchema = "_elysia-bookmark:";
    private const string FolderBookmarkSchema = "_cyrene-bookmark:";
    private readonly IStorageProvider? _storageProvider;

    /// <summary>
    /// 使用当前 <see cref="TopLevel"/> 的存储提供方。
    /// </summary>
    public AvaloniaDefaultPlatformFilePickerService()
    {
    }

    internal AvaloniaDefaultPlatformFilePickerService(IStorageProvider storageProvider)
    {
        ArgumentNullException.ThrowIfNull(storageProvider);
        _storageProvider = storageProvider;
    }

    /// <inheritdoc />
    public virtual async Task<List<string>> OpenFilesPickerAsync(FilePickerOpenOptions options, TopLevel root)
    {
        var list =  (await GetStorageProvider(root).OpenFilePickerAsync(options))
            .ToList();
        var delegatedFiles = new HashSet<IStorageFile>(ReferenceEqualityComparer.Instance);
        try
        {
            var result = new string?[list.Count];
            var filesToMaterialize = new List<IStorageFile>();
            var materializedIndexes = new List<int>();
            for (var index = 0; index < list.Count; index++)
            {
                var file = list[index];
                if (file.TryGetLocalPath() is { } path)
                {
                    result[index] = path;
                    continue;
                }

                if (file.CanBookmark)
                {
                    var bookmark = await file.SaveBookmarkAsync();
                    if (!string.IsNullOrWhiteSpace(bookmark))
                    {
                        result[index] = FileBookmarkSchema + bookmark;
                        continue;
                    }
                }

                filesToMaterialize.Add(file);
                materializedIndexes.Add(index);
            }

            if (filesToMaterialize.Count > 0)
            {
                delegatedFiles.UnionWith(filesToMaterialize);
                var materializedPaths = await PlatformFileMaterializationFallback
                    .MaterializeFilesAsync(filesToMaterialize);
                if (materializedPaths.Count != materializedIndexes.Count)
                {
                    throw new InvalidOperationException(
                        "平台文件暂存结果与选择项数量不一致。");
                }

                for (var index = 0; index < materializedPaths.Count; index++)
                {
                    result[materializedIndexes[index]] = materializedPaths[index];
                }
            }

            return result.OfType<string>().ToList();
        }
        finally
        {
            foreach (var file in list.Where(file => !delegatedFiles.Contains(file)))
            {
                try
                {
                    file.Dispose();
                }
                catch
                {
                    // 清理失败不能掩盖原始文件选择异常。
                }
            }
        }
    }

    /// <inheritdoc />
    public virtual Task<List<string>> MaterializeFilesAsync(IReadOnlyList<IStorageFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return PlatformFileMaterializationFallback.MaterializeFilesAsync(files);
    }

    /// <inheritdoc />
    public virtual async Task<string?> SaveFilePickerAsync(FilePickerSaveOptions options, TopLevel root)
    {
        using var file = await GetStorageProvider(root).SaveFilePickerAsync(options);
        if (file == null)
        {
            return null;
        }
        if (file.TryGetLocalPath() is {} path)
        {
            if (!File.Exists(path))
            {
                await File.Create(path).DisposeAsync();
            }
            return path;
        }

        if (!file.CanBookmark)
        {
            return null;
        }

        var bookmark = await file.SaveBookmarkAsync();
        return string.IsNullOrWhiteSpace(bookmark)
            ? null
            : FileBookmarkSchema + bookmark;
    }

    /// <inheritdoc />
    public virtual async Task<string?> SaveFileAsync(
        FilePickerSaveOptions options,
        TopLevel root,
        Func<Stream, Task> writer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(writer);

        using var file = await GetStorageProvider(root).SaveFilePickerAsync(options);
        if (file == null)
        {
            return null;
        }

        await using (var output = await file.OpenWriteAsync())
        {
            if (output.CanSeek)
            {
                output.SetLength(0);
                output.Position = 0;
            }

            await writer(output);
            await output.FlushAsync();
        }

        if (file.TryGetLocalPath() is { } path)
        {
            return path;
        }

        if (file.CanBookmark && await file.SaveBookmarkAsync() is { Length: > 0 } bookmark)
        {
            return FileBookmarkSchema + bookmark;
        }

        return file.Name;
    }

    /// <inheritdoc />
    public virtual async Task<List<string>> OpenFoldersPickerAsync(FolderPickerOpenOptions options, TopLevel root)
    {
        var list = await GetStorageProvider(root).OpenFolderPickerAsync(options);
        try
        {
            var result = new List<string>(list.Count);
            foreach (var folder in list)
            {
                if (folder.TryGetLocalPath() is { } path)
                {
                    result.Add(path);
                    continue;
                }

                if (folder.CanBookmark)
                {
                    var bookmark = await folder.SaveBookmarkAsync();
                    if (!string.IsNullOrWhiteSpace(bookmark))
                    {
                        result.Add(FolderBookmarkSchema + bookmark);
                    }
                }
            }

            return result;
        }
        finally
        {
            DisposeItems(list, 0);
        }
    }

    /// <inheritdoc />
    public virtual async Task<IStorageFile?> GetFileAsync(string path, TopLevel root)
    {
        if (path.StartsWith(FileBookmarkSchema) &&
            await GetStorageProvider(root).OpenFileBookmarkAsync(path[FileBookmarkSchema.Length..]) is {} bookmarkFile)
        {
            return bookmarkFile;
        }

        return await GetStorageProvider(root).TryGetFileFromPathAsync(path);
    }

    /// <inheritdoc />
    public virtual async Task<IStorageFolder?> GetFolderAsync(string path, TopLevel root)
    {
        if (path.StartsWith(FolderBookmarkSchema) &&
            await GetStorageProvider(root).OpenFolderBookmarkAsync(path[FolderBookmarkSchema.Length..]) is {} bookmarkFolder)
        {
            return bookmarkFolder;
        }

        return await GetStorageProvider(root).TryGetFolderFromPathAsync(path);
    }

    /// <inheritdoc />
    public virtual bool IsBookmark(string? path)
    {
        return !string.IsNullOrEmpty(path) &&
               (path.StartsWith(FileBookmarkSchema, StringComparison.Ordinal) ||
                path.StartsWith(FolderBookmarkSchema, StringComparison.Ordinal));
    }

    private IStorageProvider GetStorageProvider(TopLevel root) =>
        _storageProvider ?? root.StorageProvider;

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
                // 清理失败不能掩盖原始文件选择异常。
            }
        }
    }
}
