using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClassIsland.Platforms.Abstraction.Services;

namespace ClassIsland.Platforms.Abstraction.Stubs.Services;

/// <inheritdoc />
public class AvaloniaDefaultPlatformFilePickerService : IPlatformFilePickerService
{
    private const string FileBookmarkSchema = "_elysia-bookmark:";
    private const string FolderBookmarkSchema = "_cyrene-bookmark:";

    /// <inheritdoc />
    public virtual async Task<List<string>> OpenFilesPickerAsync(FilePickerOpenOptions options, TopLevel root)
    {
        var list =  (await root.StorageProvider.OpenFilePickerAsync(options))
            .ToList();
        try
        {
            var result = new List<string>(list.Count);
            foreach (var file in list)
            {
                if (file.TryGetLocalPath() is { } path)
                {
                    result.Add(path);
                    continue;
                }

                if (file.CanBookmark)
                {
                    result.Add(FileBookmarkSchema + await file.SaveBookmarkAsync());
                }
            }

            return result;
        }
        finally
        {
            foreach (var file in list)
            {
                file.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public virtual Task<List<string>> MaterializeFilesAsync(IReadOnlyList<IStorageFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var paths = new List<string>(files.Count);
        foreach (var file in files)
        {
            using (file)
            {
                if (file.TryGetLocalPath() is { } path)
                {
                    paths.Add(path);
                }
            }
        }

        return Task.FromResult(paths);
    }

    /// <inheritdoc />
    public virtual async Task<string?> SaveFilePickerAsync(FilePickerSaveOptions options, TopLevel root)
    {
        using var file = await root.StorageProvider.SaveFilePickerAsync(options);
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

        return FileBookmarkSchema + await file.SaveBookmarkAsync();
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

        using var file = await root.StorageProvider.SaveFilePickerAsync(options);
        if (file == null)
        {
            return null;
        }

        await using (var output = await file.OpenWriteAsync())
        {
            if (output.CanSeek)
            {
                output.SetLength(0);
            }

            await writer(output);
            await output.FlushAsync();
        }

        if (file.TryGetLocalPath() is { } path)
        {
            return path;
        }

        return file.CanBookmark
            ? FileBookmarkSchema + await file.SaveBookmarkAsync()
            : file.Name;
    }

    /// <inheritdoc />
    public virtual async Task<List<string>> OpenFoldersPickerAsync(FolderPickerOpenOptions options, TopLevel root)
    {
        var list = await root.StorageProvider.OpenFolderPickerAsync(options);
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
                    result.Add(FolderBookmarkSchema + await folder.SaveBookmarkAsync());
                }
            }

            return result;
        }
        finally
        {
            foreach (var folder in list)
            {
                folder.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public virtual async Task<IStorageFile?> GetFileAsync(string path, TopLevel root)
    {
        if (path.StartsWith(FileBookmarkSchema) &&
            await root.StorageProvider.OpenFileBookmarkAsync(path[FileBookmarkSchema.Length..]) is {} bookmarkFile)
        {
            return bookmarkFile;
        }

        return await root.StorageProvider.TryGetFileFromPathAsync(path);
    }

    /// <inheritdoc />
    public virtual async Task<IStorageFolder?> GetFolderAsync(string path, TopLevel root)
    {
        if (path.StartsWith(FolderBookmarkSchema) &&
            await root.StorageProvider.OpenFolderBookmarkAsync(path[FolderBookmarkSchema.Length..]) is {} bookmarkFolder)
        {
            return bookmarkFolder;
        }

        return await root.StorageProvider.TryGetFolderFromPathAsync(path);
    }

    /// <inheritdoc />
    public virtual bool IsBookmark(string path)
    {
        return path.StartsWith(FileBookmarkSchema) || path.StartsWith(FolderBookmarkSchema);
    }
}
