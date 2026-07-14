using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.Core;
using ClassIsland.Services.AppUpdating;
using ClassIsland.Services.Management;
using ClassIsland.Services.SpeechService;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Path = System.IO.Path;

namespace ClassIsland.Services;

public class FileFolderService(SettingsService settingsService, ILogger<FileFolderService> logger) : IHostedService
{
    public SettingsService SettingsService { get; } = settingsService;
    public ILogger<FileFolderService> Logger { get; } = logger;

    private static List<string> Folders =
    [
        CommonDirectories.AppDataFolderPath,
        ManagementService.ManagementConfigureFolderPath,
        CommonDirectories.AppTempFolderPath,
        CommonDirectories.AppCacheFolderPath,
        UpdateService.UpdateCachePath,
        EdgeTtsService.EdgeTtsCacheFolderPath,
        PluginService.PluginsPkgRootPath,
        PluginService.PluginsRootPath,
        PluginService.PluginConfigsFolderPath,
        PluginService.PluginsIndexPath,
        Path.Combine(CommonDirectories.AppRootFolderPath, "Backups"),
        CommonDirectories.AppLogFolderPath,
        AutomationService.AutomationConfigsFolderPath,
        ManagementService.LocalManagementConfigureFolderPath,
        XamlThemeService.ThemesPath,
        XamlThemeService.ThemesPkgRootPath
    ];

    public static void CreateFolders()
    {
        foreach (var i in Folders.Where(i => !Directory.Exists(i)))
        {
            Directory.CreateDirectory(i);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
    }

    public static void CopyFolder(string source, string destination, bool overwrite=false)
    {
        if (!Directory.Exists(destination))
        {
            Directory.CreateDirectory(destination);
        }
        foreach (var i in Directory.EnumerateFiles(source))
        {
            try
            {
                File.Copy(i, Path.Combine(destination, Path.GetFileName(i)), overwrite);
            }
            catch (Exception e)
            {
                // ignore
            }
        }

        foreach (var i in Directory.EnumerateDirectories(source))
        {
            CopyFolder(Path.Combine(source, Path.GetFileName(i)), Path.Combine(destination, Path.GetFileName(i)), overwrite);
        }
    }

    public static void CopyFolderStrict(string source, string destination, bool overwrite = false)
    {
        FileSystemDataTransaction.CopyDirectoryStrict(source, destination, overwrite);
    }

    public async Task ProcessAutoBackupAsync()
    {
        if (!SettingsService.Settings.IsAutoBackupEnabled)
        {
            return;
        }

        if (!(DateTime.Today - SettingsService.Settings.LastAutoBackupTime.Date >=
              TimeSpan.FromDays(SettingsService.Settings.AutoBackupIntervalDays)))
        {
            return;
        }

        await CreateBackupAsync(true);
        SettingsService.Settings.LastAutoBackupTime = DateTime.Now;

        if (!Directory.Exists(Path.Combine(CommonDirectories.AppRootFolderPath, "Backups")))
        {
            return;
        }

        if (SettingsService.Settings.AutoBackupLimit <= 0)
        {
            return;
        }
        var outdatedBackups = Directory.EnumerateFiles(Path.Combine(CommonDirectories.AppRootFolderPath, "Backups"), "Auto_*").OrderByDescending(File.GetLastWriteTime).Skip(SettingsService.Settings.AutoBackupLimit).ToList();
        foreach (var i in outdatedBackups)
        {
            File.Delete(i);
        }        
    }

    public static async Task CreateBackupAsync(bool isAuto = false, string? filename = null, string? rootPath = null)
    {
        string[] backupFolders =
        [
            "Config",
            "Profiles",
            "ImportedFiles"
        ];
        string[] backupFiles =
        [
            "Settings.json"
        ];
        rootPath = Path.GetFullPath(rootPath ?? CommonDirectories.AppRootFolderPath);
        var backupFolder = Path.Combine(rootPath, "Backups/");
        var backupFilename = string.IsNullOrWhiteSpace(filename) ? $"Backup_{DateTime.Now:yy-MMM-dd_HH-mm-ss}.zip" : filename + ".zip";
        if (isAuto)
        {
            backupFilename = "Auto_" + backupFilename;
        }

        if (!string.Equals(
                Path.GetFileName(backupFilename),
                backupFilename,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("备份名称不能包含目录路径。", nameof(filename));
        }

        var backupTarget = Path.Combine(backupFolder, backupFilename);
        var incompleteBackupTarget = Path.Combine(
            backupFolder,
            $".{backupFilename}.{Guid.NewGuid():N}.tmp");

        if (!Directory.Exists(backupFolder))
        {
            Directory.CreateDirectory(backupFolder);
        }
        FileSystemDataTransaction.EnsureDirectoryIsNotLink(backupFolder);

        await Task.Run(() =>
        {
            try
            {
                using (var zipStream = new FileStream(
                           incompleteBackupTarget,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    foreach (var file in backupFiles)
                    {
                        var filePath = Path.Combine(rootPath, file);
                        if (File.Exists(filePath))
                        {
                            FileSystemDataTransaction.EnsureFileIsNotLink(filePath);
                            archive.CreateEntryFromFile(
                                filePath,
                                SafeArchiveExtractor.NormalizeFileSystemRelativePath(file));
                        }
                    }

                    foreach (var folder in backupFolders)
                    {
                        var folderPath = Path.Combine(rootPath, folder);
                        if (!Directory.Exists(folderPath))
                        {
                            continue;
                        }

                        foreach (var file in FileSystemDataTransaction
                                     .EnumerateFilesStrict(folderPath))
                        {
                            var relativePath = SafeArchiveExtractor.NormalizeFileSystemRelativePath(
                                Path.GetRelativePath(rootPath, file));
                            archive.CreateEntryFromFile(file, relativePath);
                        }
                    }
                }

                File.Move(incompleteBackupTarget, backupTarget, true);
            }
            finally
            {
                FileSystemDataTransaction.TryDeleteFile(incompleteBackupTarget);
            }
        });
    }
}
