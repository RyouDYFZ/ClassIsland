using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ClassIsland.Services;

internal static class SafeArchiveExtractor
{
    internal static int ExtractSelected(
        ZipArchive archive,
        string destinationRoot,
        IReadOnlySet<string> allowedFiles,
        IReadOnlySet<string> allowedDirectories)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(allowedFiles);
        ArgumentNullException.ThrowIfNull(allowedDirectories);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        var entries = archive.Entries
            .Select(entry => InspectEntry(entry, root, allowedFiles, allowedDirectories))
            .ToList();

        Directory.CreateDirectory(root);
        var extracted = 0;
        foreach (var inspected in entries.Where(entry => entry.ShouldExtract))
        {
            if (inspected.IsDirectory)
            {
                Directory.CreateDirectory(inspected.TargetPath);
            }
            else
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(inspected.TargetPath) ?? root);
                inspected.Entry.ExtractToFile(inspected.TargetPath, true);
            }

            extracted++;
        }

        return extracted;
    }

    internal static string NormalizeRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("归档路径包含空字符。");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            IsWindowsAbsolutePath(normalized))
        {
            throw new InvalidDataException($"归档包含绝对路径：{path}");
        }

        var hasTrailingSeparator = normalized.EndsWith("/", StringComparison.Ordinal);
        var segments = normalized.Split('/', StringSplitOptions.None);
        var segmentCount = hasTrailingSeparator ? segments.Length - 1 : segments.Length;
        if (segmentCount == 0 ||
            segments.Take(segmentCount).Any(segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException($"归档包含无效相对路径：{path}");
        }

        return string.Join('/', segments.Take(segmentCount)) +
               (hasTrailingSeparator ? "/" : string.Empty);
    }

    internal static string NormalizeFileSystemRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.DirectorySeparatorChar != '\\' && path.Contains('\\'))
        {
            throw new InvalidDataException(
                $"文件名包含无法跨平台安全归档的反斜杠：{path}");
        }

        return NormalizeRelativePath(
            path.Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static InspectedArchiveEntry InspectEntry(
        ZipArchiveEntry entry,
        string destinationRoot,
        IReadOnlySet<string> allowedFiles,
        IReadOnlySet<string> allowedDirectories)
    {
        var normalized = NormalizeRelativePath(entry.FullName);
        var isDirectory = normalized.EndsWith("/", StringComparison.Ordinal);
        var segments = normalized.TrimEnd('/').Split('/');
        var firstSegment = segments[0];

        if (IsSymbolicLink(entry))
        {
            throw new InvalidDataException($"归档包含不支持的符号链接：{entry.FullName}");
        }

        var isAllowedFile = segments.Length == 1 &&
                            !isDirectory &&
                            allowedFiles.Contains(firstSegment);
        var isAllowedDirectory = allowedDirectories.Contains(firstSegment);
        if (isAllowedDirectory && segments.Length == 1 && !isDirectory)
        {
            throw new InvalidDataException(
                $"归档目录 {firstSegment} 被存储为普通文件。");
        }

        if (allowedFiles.Contains(firstSegment) &&
            (segments.Length != 1 || isDirectory))
        {
            throw new InvalidDataException(
                $"归档文件 {firstSegment} 的路径或类型无效。");
        }

        var targetPath = Path.GetFullPath(Path.Combine(
            [destinationRoot, .. segments]));
        EnsureInsideRoot(targetPath, destinationRoot, entry.FullName);

        return new InspectedArchiveEntry(
            entry,
            targetPath,
            isDirectory,
            isAllowedFile || isAllowedDirectory);
    }

    private static bool IsWindowsAbsolutePath(string path) =>
        path.Length >= 3 &&
        char.IsAsciiLetter(path[0]) &&
        path[1] == ':' &&
        path[2] == '/';

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        return unixMode == unixSymbolicLink;
    }

    private static void EnsureInsideRoot(
        string path,
        string root,
        string archivePath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, comparison) &&
            !string.Equals(path, root, comparison))
        {
            throw new InvalidDataException($"归档包含越界路径：{archivePath}");
        }
    }

    private sealed record InspectedArchiveEntry(
        ZipArchiveEntry Entry,
        string TargetPath,
        bool IsDirectory,
        bool ShouldExtract);
}
