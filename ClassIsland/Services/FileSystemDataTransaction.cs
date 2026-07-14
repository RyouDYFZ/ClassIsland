using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClassIsland.Services;

internal sealed class FileSystemRollbackException : IOException
{
    internal FileSystemRollbackException(
        string rollbackPath,
        Exception operationException,
        Exception rollbackException)
        : base(
            $"数据操作失败，且无法完整回滚。原始数据快照已保留在：{rollbackPath}",
            new AggregateException(operationException, rollbackException))
    {
        RollbackPath = rollbackPath;
    }

    internal string RollbackPath { get; }
}

internal static class FileSystemDataTransaction
{
    private const string ManifestFileName = "rollback-manifest.json";

    internal static void Execute(
        string liveRoot,
        string rollbackRoot,
        IReadOnlyCollection<string> relativePaths,
        Action operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(rollbackRoot);
        ArgumentNullException.ThrowIfNull(relativePaths);
        ArgumentNullException.ThrowIfNull(operation);

        var live = Path.TrimEndingDirectorySeparator(Path.GetFullPath(liveRoot));
        var rollback = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rollbackRoot));
        var paths = relativePaths
            .Select(ValidateRelativePath)
            .Distinct(GetPathComparer())
            .ToList();
        var preserveRollback = false;

        try
        {
            Directory.CreateDirectory(rollback);
            EnsureDirectoryIsNotLink(rollback);
            if (Directory.EnumerateFileSystemEntries(rollback).Any())
            {
                throw new IOException("回滚快照目录必须为空。");
            }
            var manifest = CaptureSnapshot(live, rollback, paths);
            File.WriteAllText(
                Path.Combine(rollback, ManifestFileName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

            try
            {
                operation();
            }
            catch (Exception operationException)
            {
                try
                {
                    RestoreSnapshot(live, rollback, manifest);
                }
                catch (Exception rollbackException)
                {
                    preserveRollback = true;
                    throw new FileSystemRollbackException(
                        rollback,
                        operationException,
                        rollbackException);
                }

                throw;
            }
        }
        finally
        {
            if (!preserveRollback)
            {
                TryDeleteDirectory(rollback);
            }
        }
    }

    internal static void CopyDirectoryStrict(
        string source,
        string destination,
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        EnsureDirectoryIsNotLink(sourceRoot);
        EnsureDestinationIsOutsideSource(sourceRoot, destinationRoot);
        CopyDirectoryCore(sourceRoot, destinationRoot, overwrite);
    }

    internal static void CopyFileStrict(
        string source,
        string destination,
        bool overwrite = false)
    {
        EnsureFileIsNotLink(source);
        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw new InvalidOperationException("无法确定文件目标目录。"));
        File.Copy(source, destination, overwrite);
    }

    internal static void EnsureFileIsNotLink(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到文件。", path);
        }

        RejectReparsePoint(path);
    }

    internal static IEnumerable<string> EnumerateFilesStrict(string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        EnsureDirectoryIsNotLink(fullRoot);
        return EnumerateFilesCore(fullRoot).ToList();
    }

    internal static void DeleteEntry(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    internal static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // 临时目录清理失败不能掩盖操作结果。
        }
    }

    internal static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件清理失败不能掩盖操作结果。
        }
    }

    private static List<SnapshotEntry> CaptureSnapshot(
        string liveRoot,
        string rollbackRoot,
        IEnumerable<string> relativePaths)
    {
        var manifest = new List<SnapshotEntry>();
        foreach (var relativePath in relativePaths)
        {
            var source = ResolveInsideRoot(liveRoot, relativePath);
            var destination = ResolveInsideRoot(rollbackRoot, relativePath);
            if (File.Exists(source))
            {
                CopyFileStrict(source, destination, true);
                manifest.Add(new SnapshotEntry(relativePath, SnapshotEntryKind.File));
            }
            else if (Directory.Exists(source))
            {
                CopyDirectoryStrict(source, destination, true);
                manifest.Add(new SnapshotEntry(relativePath, SnapshotEntryKind.Directory));
            }
            else
            {
                manifest.Add(new SnapshotEntry(relativePath, SnapshotEntryKind.Missing));
            }
        }

        return manifest;
    }

    private static void RestoreSnapshot(
        string liveRoot,
        string rollbackRoot,
        IEnumerable<SnapshotEntry> manifest)
    {
        foreach (var entry in manifest)
        {
            var destination = ResolveInsideRoot(liveRoot, entry.RelativePath);
            DeleteEntry(destination);

            var source = ResolveInsideRoot(rollbackRoot, entry.RelativePath);
            switch (entry.Kind)
            {
                case SnapshotEntryKind.File:
                    CopyFileStrict(source, destination, true);
                    break;
                case SnapshotEntryKind.Directory:
                    CopyDirectoryStrict(source, destination, true);
                    break;
                case SnapshotEntryKind.Missing:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private static void CopyDirectoryCore(
        string source,
        string destination,
        bool overwrite)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            RejectReparsePoint(file);
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            EnsureDirectoryIsNotLink(directory);
            CopyDirectoryCore(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)),
                overwrite);
        }
    }

    private static IEnumerable<string> EnumerateFilesCore(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root))
        {
            RejectReparsePoint(file);
            yield return file;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            EnsureDirectoryIsNotLink(directory);
            foreach (var file in EnumerateFilesCore(directory))
            {
                yield return file;
            }
        }
    }

    private static string ValidateRelativePath(string relativePath)
    {
        var normalized = SafeArchiveExtractor.NormalizeRelativePath(relativePath)
            .TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("事务路径不能为空。", nameof(relativePath));
        }

        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string ResolveInsideRoot(string root, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidDataException($"事务路径越界：{relativePath}");
        }

        return path;
    }

    private static void EnsureDestinationIsOutsideSource(
        string source,
        string destination)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(source, destination, comparison) ||
            destination.StartsWith(source + Path.DirectorySeparatorChar, comparison))
        {
            throw new IOException("目录复制目标不能位于源目录内部。");
        }
    }

    internal static void EnsureDirectoryIsNotLink(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"找不到目录：{path}");
        }

        RejectReparsePoint(path);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"不允许复制符号链接或重解析点：{path}");
        }
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record SnapshotEntry(
        string RelativePath,
        SnapshotEntryKind Kind);

    private enum SnapshotEntryKind
    {
        Missing,
        File,
        Directory
    }
}
