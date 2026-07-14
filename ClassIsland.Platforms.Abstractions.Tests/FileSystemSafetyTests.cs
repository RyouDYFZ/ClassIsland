using System.IO.Compression;
using System.Text;
using ClassIsland.Services;
using Xunit;

namespace ClassIsland.Platforms.Abstractions.Tests;

public sealed class FileSystemSafetyTests
{
    [Fact]
    public void ExtractSelected_RejectsCategoryTraversalBeforeWriting()
    {
        using var archiveStream = CreateArchive(
            ("Profiles/valid.json", "valid"),
            ("Profiles/../Settings.json", "malicious"));
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var destination = CreateTemporaryDirectory();
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                SafeArchiveExtractor.ExtractSelected(
                    archive,
                    destination,
                    new HashSet<string>(StringComparer.Ordinal),
                    new HashSet<string>(["Profiles"], StringComparer.Ordinal)));

            Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        }
        finally
        {
            Directory.Delete(destination, true);
        }
    }

    [Fact]
    public void ExtractSelected_WritesOnlyAllowedRoots()
    {
        using var archiveStream = CreateArchive(
            ("Settings.json", "settings"),
            ("Profiles/profile.json", "profile"));
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var destination = CreateTemporaryDirectory();
        try
        {
            var count = SafeArchiveExtractor.ExtractSelected(
                archive,
                destination,
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(["Profiles"], StringComparer.Ordinal));

            Assert.Equal(1, count);
            Assert.False(File.Exists(Path.Combine(destination, "Settings.json")));
            Assert.Equal(
                "profile",
                File.ReadAllText(Path.Combine(destination, "Profiles", "profile.json")));
        }
        finally
        {
            Directory.Delete(destination, true);
        }
    }

    [Fact]
    public void Execute_RestoresOriginalStateAndRemovesNewEntriesOnFailure()
    {
        var live = CreateTemporaryDirectory();
        var rollback = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(live, "Settings.json"), "original-settings");
            Directory.CreateDirectory(Path.Combine(live, "Config"));
            File.WriteAllText(Path.Combine(live, "Config", "original.json"), "original-config");

            Assert.Throws<InvalidOperationException>(() =>
                FileSystemDataTransaction.Execute(
                    live,
                    rollback,
                    ["Settings.json", "Config", "ImportedFiles"],
                    () =>
                    {
                        File.WriteAllText(Path.Combine(live, "Settings.json"), "changed");
                        Directory.Delete(Path.Combine(live, "Config"), true);
                        Directory.CreateDirectory(Path.Combine(live, "Config"));
                        File.WriteAllText(Path.Combine(live, "Config", "new.json"), "new");
                        Directory.CreateDirectory(Path.Combine(live, "ImportedFiles"));
                        throw new InvalidOperationException("apply failed");
                    }));

            Assert.Equal(
                "original-settings",
                File.ReadAllText(Path.Combine(live, "Settings.json")));
            Assert.Equal(
                "original-config",
                File.ReadAllText(Path.Combine(live, "Config", "original.json")));
            Assert.False(File.Exists(Path.Combine(live, "Config", "new.json")));
            Assert.False(Directory.Exists(Path.Combine(live, "ImportedFiles")));
            Assert.False(Directory.Exists(rollback));
        }
        finally
        {
            FileSystemDataTransaction.TryDeleteDirectory(live);
            FileSystemDataTransaction.TryDeleteDirectory(rollback);
        }
    }

    [Fact]
    public void Execute_PreservesSnapshotWhenRollbackFails()
    {
        var live = CreateTemporaryDirectory();
        var rollback = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(live, "Settings.json"), "original");

            var exception = Assert.Throws<FileSystemRollbackException>(() =>
                FileSystemDataTransaction.Execute(
                    live,
                    rollback,
                    ["Settings.json"],
                    () =>
                    {
                        File.Delete(Path.Combine(rollback, "Settings.json"));
                        File.WriteAllText(Path.Combine(live, "Settings.json"), "changed");
                        throw new InvalidOperationException("apply failed");
                    }));

            Assert.Equal(Path.GetFullPath(rollback), exception.RollbackPath);
            Assert.True(Directory.Exists(rollback));
            Assert.True(File.Exists(Path.Combine(rollback, "rollback-manifest.json")));
        }
        finally
        {
            FileSystemDataTransaction.TryDeleteDirectory(live);
            FileSystemDataTransaction.TryDeleteDirectory(rollback);
        }
    }

    [Fact]
    public void CopyDirectoryStrict_RejectsSymbolicLinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var source = CreateTemporaryDirectory();
        var destination = CreateTemporaryDirectory();
        var external = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(external, "outside.txt"), "outside");
            Directory.CreateSymbolicLink(
                Path.Combine(source, "linked"),
                external);

            Assert.Throws<IOException>(() =>
                FileSystemDataTransaction.CopyDirectoryStrict(
                    source,
                    destination,
                    true));
            Assert.False(File.Exists(Path.Combine(destination, "linked", "outside.txt")));
        }
        finally
        {
            FileSystemDataTransaction.TryDeleteDirectory(source);
            FileSystemDataTransaction.TryDeleteDirectory(destination);
            FileSystemDataTransaction.TryDeleteDirectory(external);
        }
    }

    [Theory]
    [InlineData("../Settings.json")]
    [InlineData("Profiles/../Settings.json")]
    [InlineData("/absolute/Settings.json")]
    [InlineData("C:/absolute/Settings.json")]
    public void NormalizeRelativePath_RejectsUnsafePaths(string path)
    {
        Assert.Throws<InvalidDataException>(() =>
            SafeArchiveExtractor.NormalizeRelativePath(path));
    }

    [Fact]
    public void NormalizeFileSystemRelativePath_UsesPortableSeparators()
    {
        var relativePath = Path.Combine("Config", "Plugins", "settings.json");

        Assert.Equal(
            "Config/Plugins/settings.json",
            SafeArchiveExtractor.NormalizeFileSystemRelativePath(relativePath));
    }

    private static MemoryStream CreateArchive(
        params (string Path, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(
                    entry.Open(),
                    new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static string CreateTemporaryDirectory() =>
        Directory.CreateTempSubdirectory("ClassIslandSafetyTest-").FullName;
}
