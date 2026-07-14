using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core;
using ClassIsland.Core.Controls;
using ClassIsland.Core.Helpers.UI;
using ClassIsland.Services;
using ClassIsland.ViewModels.RecoveryPages;
using FluentAvalonia.UI.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Path = System.IO.Path;

namespace ClassIsland.Views.RecoveryPages;

/// <summary>
/// RecoverBackupPage.xaml 的交互逻辑
/// </summary>
public partial class RecoverBackupPage : UserControl
{
    private static readonly string[] RecoverableFileNames =
    [
        "Settings.json",
        "Settings.json.bak"
    ];

    private static readonly string[] RecoverableDirectoryNames =
    [
        "Config",
        "Profiles",
        "ImportedFiles"
    ];

    public FAFrame? MainFrame { get; init; }

    public UserControl? LastPage { get; init; }

    public RecoverBackupViewModel ViewModel { get; } = new();
    public RecoverBackupPage()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void RecoverBackupPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        var backupPath = Path.Combine(CommonDirectories.AppRootFolderPath, "Backups");

        if (Directory.Exists(backupPath))
        {
            var excludeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) // 忽略指定的文件
            {
                ".DS_Store",
                "Thumbs.db",
                "desktop.ini"
            };

            IEnumerable<string?> files = Directory.GetFiles(backupPath)
                 .Where(file => string.Equals(
                     Path.GetExtension(file),
                     ".zip",
                     StringComparison.OrdinalIgnoreCase))
                 .Where(file => !excludeFiles.Contains(Path.GetFileName(file)))
                 .OrderByDescending(File.GetLastWriteTime)
                 .Select(Path.GetFileName);

            IEnumerable<string?> directories = Directory.GetDirectories(backupPath)
                .OrderByDescending(Directory.GetLastWriteTime)
                .Select(Path.GetFileName);

            ViewModel.Backups = new ObservableCollection<string>(files.Concat(directories));
        }
    }

    private async Task RecoverBackupAsync(string backupPath)
    {
        var fullRecovery = ViewModel.RecoverMode == 1;
        string? stagingPath = null;
        try
        {
            await Task.Run(() =>
            {
                stagingPath = Directory.CreateTempSubdirectory(
                    "ClassIslandBackupRecovery-").FullName;
                MaterializeBackup(backupPath, stagingPath);
                EnsureBackupContainsRecoverableData(stagingPath);
                AppDataConfigurationValidator.ValidateAvailable(stagingPath);

                var appRoot = Path.GetFullPath(CommonDirectories.AppRootFolderPath);
                var rollbackPath = Directory.CreateTempSubdirectory(
                    "ClassIslandBackupRollback-").FullName;
                FileSystemDataTransaction.Execute(
                    appRoot,
                    rollbackPath,
                    RecoverableFileNames.Concat(RecoverableDirectoryNames).ToArray(),
                    () =>
                    {
                        if (fullRecovery)
                        {
                            DeleteRecoverableData(appRoot);
                        }

                        CopyRecoverableData(stagingPath, appRoot, true);
                    });
            });
        }
        finally
        {
            FileSystemDataTransaction.TryDeleteDirectory(stagingPath);
        }
    }

    private static void MaterializeBackup(string backupPath, string stagingPath)
    {
        if (File.Exists(backupPath))
        {
            if (!string.Equals(
                    Path.GetExtension(backupPath),
                    ".zip",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("备份文件不是有效的 ZIP 归档。");
            }

            using var archive = ZipFile.OpenRead(backupPath);
            SafeArchiveExtractor.ExtractSelected(
                archive,
                stagingPath,
                new HashSet<string>(RecoverableFileNames, StringComparer.Ordinal),
                new HashSet<string>(RecoverableDirectoryNames, StringComparer.Ordinal));
            return;
        }

        if (Directory.Exists(backupPath))
        {
            CopyRecoverableData(backupPath, stagingPath, true);
            return;
        }

        throw new FileNotFoundException("找不到要恢复的备份。", backupPath);
    }

    private static void EnsureBackupContainsRecoverableData(string root)
    {
        if (RecoverableFileNames.Any(name => File.Exists(Path.Combine(root, name))) ||
            RecoverableDirectoryNames.Any(name =>
                Directory.Exists(Path.Combine(root, name)) &&
                Directory.EnumerateFiles(
                    Path.Combine(root, name),
                    "*",
                    SearchOption.AllDirectories).Any()))
        {
            return;
        }

        throw new InvalidDataException("备份中不包含可恢复的 ClassIsland 配置数据。");
    }

    private static void CopyRecoverableData(
        string sourceRoot,
        string destinationRoot,
        bool overwrite)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var name in RecoverableFileNames)
        {
            var source = Path.Combine(sourceRoot, name);
            if (File.Exists(source))
            {
                FileSystemDataTransaction.CopyFileStrict(
                    source,
                    Path.Combine(destinationRoot, name),
                    overwrite);
            }
        }

        foreach (var name in RecoverableDirectoryNames)
        {
            var source = Path.Combine(sourceRoot, name);
            if (Directory.Exists(source))
            {
                FileFolderService.CopyFolderStrict(
                    source,
                    Path.Combine(destinationRoot, name),
                    overwrite);
            }
        }
    }

    private static void DeleteRecoverableData(string root)
    {
        foreach (var name in RecoverableFileNames)
        {
            FileSystemDataTransaction.DeleteEntry(Path.Combine(root, name));
        }

        foreach (var name in RecoverableDirectoryNames)
        {
            FileSystemDataTransaction.DeleteEntry(Path.Combine(root, name));
        }
    }

    private async void ButtonRecover_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedBackupName == null)
        {
            return;
        }

        var result = await ContentDialogHelper.ShowConfirmationDialog("恢复备份",
            $"您确定要把应用配置恢复到备份 {ViewModel.SelectedBackupName} 的状态吗？此操作无法撤销。",
            root: TopLevel.GetTopLevel(this));
        if (!result)
        {
            return;
        }

        var backupPath = Path.Combine(CommonDirectories.AppRootFolderPath, "Backups", ViewModel.SelectedBackupName);

        try
        {
            ViewModel.IsWorking = true;
            await RecoverBackupAsync(backupPath);
            this.ShowSuccessToast("操作成功完成。");
        }
        catch (Exception exception)
        {
            this.ShowErrorToast("无法恢复备份", exception);
        }
        finally
        {
            ViewModel.IsWorking = false;
        }
    }

    private void ButtonGoBack_OnClick(object? sender, RoutedEventArgs e)
    {
        if (MainFrame != null)
        {
            MainFrame.Content = LastPage;
        }
    }
}
