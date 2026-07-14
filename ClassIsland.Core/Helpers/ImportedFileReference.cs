namespace ClassIsland.Core.Helpers;

/// <summary>
/// 为复制到应用数据目录的持久导入文件创建不包含沙盒容器 UUID 的引用。
/// </summary>
public static class ImportedFileReference
{
    /// <summary>
    /// 持久导入文件引用前缀。
    /// </summary>
    public const string Prefix = "_classisland-imported:";

    /// <summary>
    /// 将导入目录中的绝对路径转换为可随应用容器迁移的引用。
    /// </summary>
    public static string Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(CommonDirectories.AppImportedFilesFolderPath));
        var fullPath = Path.GetFullPath(path);
        EnsurePathIsInsideRoot(fullPath, root);

        var relativePath = Path.GetRelativePath(root, fullPath);
        var segments = relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return Prefix + string.Join('/', segments);
    }

    /// <summary>
    /// 尝试将持久导入文件引用解析到当前应用容器。
    /// </summary>
    public static bool TryResolve(string? reference, out string path)
    {
        path = reference ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reference) ||
            !reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encodedRelativePath = reference[Prefix.Length..];
        if (string.IsNullOrWhiteSpace(encodedRelativePath))
        {
            throw new FormatException("导入文件引用缺少相对路径。");
        }

        var segments = encodedRelativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length == 0 || segments.Any(x => x is "." or ".."))
        {
            throw new FormatException("导入文件引用包含无效路径。");
        }

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(CommonDirectories.AppImportedFilesFolderPath));
        var fullPath = Path.GetFullPath(Path.Combine([root, .. segments]));
        EnsurePathIsInsideRoot(fullPath, root);
        path = fullPath;
        return true;
    }

    /// <summary>
    /// 如果值是持久导入文件引用，则解析到当前容器；否则原样返回。
    /// </summary>
    public static string Resolve(string? reference)
    {
        return TryResolve(reference, out var path) ? path : reference ?? string.Empty;
    }

    /// <summary>
    /// 获取引用所属的顶层导入项目录名称。
    /// </summary>
    public static bool TryGetItemDirectoryName(string? reference, out string directoryName)
    {
        directoryName = string.Empty;
        if (string.IsNullOrWhiteSpace(reference) ||
            !reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var relativePath = reference[Prefix.Length..];
        var separatorIndex = relativePath.IndexOf('/');
        var encodedName = separatorIndex >= 0 ? relativePath[..separatorIndex] : relativePath;
        if (string.IsNullOrWhiteSpace(encodedName))
        {
            return false;
        }

        directoryName = Uri.UnescapeDataString(encodedName);
        return directoryName is not "." and not ".." &&
               Path.GetFileName(directoryName) == directoryName;
    }

    private static void EnsurePathIsInsideRoot(string path, string root)
    {
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.Ordinal) &&
            !string.Equals(path, root, StringComparison.Ordinal))
        {
            throw new ArgumentException("路径不在应用导入文件目录中。", nameof(path));
        }
    }
}
