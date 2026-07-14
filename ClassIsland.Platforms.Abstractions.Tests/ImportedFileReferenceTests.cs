using ClassIsland.Core;
using ClassIsland.Core.Helpers;
using Xunit;

namespace ClassIsland.Platforms.Abstractions.Tests;

[Collection("CommonDirectories serial tests")]
public sealed class ImportedFileReferenceTests
{
    private const string ItemDirectory = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void TryResolve_MapsLegacyIosAbsolutePathToCurrentContainer()
    {
        var legacyPath =
            $"/private/var/mobile/Containers/Data/Application/OLD/Documents/ClassIsland/Data/ImportedFiles/{ItemDirectory}/notification.wav";

        Assert.True(ImportedFileReference.TryResolve(legacyPath, out var resolvedPath));
        Assert.Equal(CurrentImportedPath(ItemDirectory, "notification.wav"), resolvedPath);
        Assert.Equal(resolvedPath, ImportedFileReference.Resolve(legacyPath));
    }

    [Fact]
    public void TryResolve_AcceptsLegacyBackslashSeparators()
    {
        var legacyPath =
            $@"\private\var\mobile\Containers\Data\Application\OLD\Documents\ClassIsland\Data\ImportedFiles\{ItemDirectory}\logo.png";

        Assert.True(ImportedFileReference.TryResolve(legacyPath, out var resolvedPath));
        Assert.Equal(CurrentImportedPath(ItemDirectory, "logo.png"), resolvedPath);
    }

    [Theory]
    [InlineData("/private/var/mobile/ClassIsland/Data/ImportedFiles/item/../outside.wav")]
    [InlineData(@"\private\var\mobile\ClassIsland\Data\ImportedFiles\item\..\outside.wav")]
    [InlineData("_classisland-imported:item/%2E%2E/outside.wav")]
    public void Resolve_RejectsTraversal(string reference)
    {
        Assert.Throws<FormatException>(() => ImportedFileReference.Resolve(reference));
    }

    [Theory]
    [InlineData("_classisland-imported:0123456789abcdef0123456789abcdef/notification.wav")]
    [InlineData("/private/var/mobile/Containers/Data/Application/OLD/Documents/ClassIsland/Data/ImportedFiles/0123456789abcdef0123456789abcdef/notification.wav")]
    [InlineData(@"\private\var\mobile\Containers\Data\Application\OLD\Documents\ClassIsland\Data\ImportedFiles\0123456789abcdef0123456789abcdef\notification.wav")]
    public void TryGetItemDirectoryName_SupportsPortableAndLegacyReferences(string reference)
    {
        Assert.True(ImportedFileReference.TryGetItemDirectoryName(reference, out var directoryName));
        Assert.Equal(ItemDirectory, directoryName);
    }

    [Fact]
    public void TryResolve_LeavesUnrelatedAbsolutePathUntouched()
    {
        const string path = "/private/var/mobile/Documents/notification.wav";

        Assert.False(ImportedFileReference.TryResolve(path, out var unresolvedPath));
        Assert.Equal(path, unresolvedPath);
        Assert.Equal(path, ImportedFileReference.Resolve(path));
        Assert.False(ImportedFileReference.TryGetItemDirectoryName(path, out _));
    }

    [Fact]
    public void TryResolve_DoesNotRewriteUnrelatedImportedFilesDirectory()
    {
        const string path = "/Users/example/ImportedFiles/notification.wav";

        Assert.False(ImportedFileReference.TryResolve(path, out var unresolvedPath));
        Assert.Equal(path, unresolvedPath);
    }

    private static string CurrentImportedPath(params string[] segments)
    {
        return Path.GetFullPath(Path.Combine(
            [CommonDirectories.AppImportedFilesFolderPath, .. segments]));
    }
}
