using ClassIsland.Services;
using Xunit;

namespace ClassIsland.Platforms.Abstractions.Tests;

public sealed class StagedDataImportValidatorTests
{
    [Fact]
    public void ValidateProfileDirectory_ReturnsPrimaryProfilesAndAllowsOpaqueFiles()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "Default.json"), "{}");
        File.WriteAllText(Path.Combine(directory.Path, "Default.json.bak"), "{}");
        File.WriteAllText(Path.Combine(directory.Path, "notes.txt"), "opaque");

        var files = StagedDataImportValidator.ValidateProfileDirectory(directory.Path);

        Assert.Single(files);
        Assert.EndsWith("Default.json", files[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ broken")]
    public void ValidateProfileDirectory_RejectsInvalidProfileJson(string content)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "Default.json"), content);

        Assert.Throws<InvalidDataException>(() =>
            StagedDataImportValidator.ValidateProfileDirectory(directory.Path));
    }

    [Fact]
    public void ValidateProfileDirectory_PromotesBackupOnlyProfile()
    {
        using var directory = new TemporaryDirectory();
        var primary = Path.Combine(directory.Path, "Default.json");
        File.WriteAllText(primary + ".bak", "{}");

        var files = StagedDataImportValidator.ValidateProfileDirectory(directory.Path);

        Assert.Equal([primary], files);
        Assert.Equal("{}", File.ReadAllText(primary));
    }

    [Fact]
    public void LoadPrimaryOrBackup_PrefersValidPrimary()
    {
        using var directory = new TemporaryDirectory();
        var primary = Path.Combine(directory.Path, "Settings.json");
        File.WriteAllText(primary, "{\"source\":\"primary\"}");
        File.WriteAllText(primary + ".bak", "{ broken");

        var content = StagedDataImportValidator.LoadPrimaryOrBackup(
            primary,
            LoadObjectJson);

        Assert.Equal("{\"source\":\"primary\"}", content);
        Assert.Equal(content, File.ReadAllText(primary));
    }

    [Fact]
    public void LoadPrimaryOrBackup_PromotesValidBackupWhenPrimaryIsInvalid()
    {
        using var directory = new TemporaryDirectory();
        var primary = Path.Combine(directory.Path, "Settings.json");
        File.WriteAllText(primary, "{ broken");
        File.WriteAllText(primary + ".bak", "{\"source\":\"backup\"}");

        var content = StagedDataImportValidator.LoadPrimaryOrBackup(
            primary,
            LoadObjectJson);

        Assert.Equal("{\"source\":\"backup\"}", content);
        Assert.Equal(content, File.ReadAllText(primary));
        Assert.Equal(content, File.ReadAllText(primary + ".bak"));
    }

    [Fact]
    public void LoadPrimaryOrBackup_FallsBackWhenFullValidationRejectsPrimary()
    {
        using var directory = new TemporaryDirectory();
        var primary = Path.Combine(directory.Path, "Settings.json");
        File.WriteAllText(primary, "{\"valid\":false}");
        File.WriteAllText(primary + ".bak", "{\"valid\":true}");

        var content = StagedDataImportValidator.LoadPrimaryOrBackup(
            primary,
            path =>
            {
                var candidate = LoadObjectJson(path);
                if (!candidate.Contains("\"valid\":true", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Configuration model is invalid.");
                }

                return candidate;
            });

        Assert.Equal("{\"valid\":true}", content);
        Assert.Equal(content, File.ReadAllText(primary));
    }

    [Fact]
    public void LoadPrimaryOrBackup_PromotesValidBackupWhenPrimaryIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var primary = Path.Combine(directory.Path, "Settings.json");
        File.WriteAllText(primary + ".bak", "{\"source\":\"backup\"}");

        var content = StagedDataImportValidator.LoadPrimaryOrBackup(
            primary,
            LoadObjectJson);

        Assert.Equal("{\"source\":\"backup\"}", content);
        Assert.Equal(content, File.ReadAllText(primary));
    }

    [Fact]
    public void LoadPrimaryOrBackup_RejectsWhenPrimaryAndBackupAreInvalid()
    {
        using var directory = new TemporaryDirectory();
        var primary = Path.Combine(directory.Path, "Settings.json");
        File.WriteAllText(primary, "{ broken primary");
        File.WriteAllText(primary + ".bak", "{ broken backup");

        Assert.Throws<InvalidDataException>(() =>
            StagedDataImportValidator.LoadPrimaryOrBackup(
                primary,
                LoadObjectJson));
    }

    private static string LoadObjectJson(string path)
    {
        StagedDataImportValidator.ValidateJsonFile(
            path,
            System.Text.Json.JsonValueKind.Object);
        return File.ReadAllText(path);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } =
            Directory.CreateTempSubdirectory("classisland-staged-import-").FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }
}
