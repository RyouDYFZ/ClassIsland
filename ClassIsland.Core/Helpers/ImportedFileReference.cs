namespace ClassIsland.Core.Helpers;

/// <summary>
/// 为复制到应用数据目录的持久导入文件创建不包含沙盒容器 UUID 的引用。
/// </summary>
public static class ImportedFileReference
{
    private const string ImportedFilesDirectoryName = "ImportedFiles";

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
        if (!TryGetRelativeSegments(reference, out var segments))
        {
            return false;
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
        if (!TryGetRelativeSegments(reference, out var segments))
        {
            return false;
        }

        directoryName = segments[0];
        return directoryName is not "." and not ".." &&
               Path.GetFileName(directoryName) == directoryName;
    }

    private static bool TryGetRelativeSegments(string? reference, out string[] segments)
    {
        segments = [];
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        if (reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var encodedRelativePath = reference[Prefix.Length..];
            if (string.IsNullOrWhiteSpace(encodedRelativePath))
            {
                throw new FormatException("导入文件引用缺少相对路径。");
            }

            segments = encodedRelativePath
                .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
            ValidateRelativeSegments(segments);
            return true;
        }

        var normalizedPath = reference.Replace('\\', '/');
        if (!IsAbsolutePathIndependentOfHost(normalizedPath))
        {
            return false;
        }

        var pathSegments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var importedFilesIndex = Array.FindIndex(
            pathSegments,
            segment => string.Equals(segment, ImportedFilesDirectoryName, StringComparison.Ordinal));
        if (importedFilesIndex < 2 ||
            importedFilesIndex >= pathSegments.Length - 1 ||
            !string.Equals(pathSegments[importedFilesIndex - 2], "ClassIsland", StringComparison.Ordinal) ||
            !string.Equals(pathSegments[importedFilesIndex - 1], "Data", StringComparison.Ordinal))
        {
            return false;
        }

        segments = pathSegments[(importedFilesIndex + 1)..];
        ValidateRelativeSegments(segments);
        return true;
    }

    private static bool IsAbsolutePathIndependentOfHost(string path)
    {
        return path.StartsWith("/", StringComparison.Ordinal) ||
               path.Length >= 3 &&
               char.IsAsciiLetter(path[0]) &&
               path[1] == ':' &&
               path[2] == '/';
    }

    private static void ValidateRelativeSegments(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0 || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Contains('/') ||
                segment.Contains('\\')))
        {
            throw new FormatException("导入文件引用包含无效路径。");
        }
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
