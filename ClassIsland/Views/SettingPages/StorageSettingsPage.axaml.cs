using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Abstractions.Services.Management;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Core.Helpers;
using ClassIsland.Core.Helpers.UI;
using ClassIsland.Models;
using ClassIsland.Platforms.Abstraction;
using ClassIsland.Services;
using ClassIsland.Shared;
using ClassIsland.ViewModels.SettingsPages;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Views.SettingPages;

/// <summary>
/// StorageSettingsPage.xaml 的交互逻辑
/// </summary>
[Group("classisland.general")]
[SettingsPageInfo("storage", "存储", "\ue6b7", "\ue6b6", SettingsPageCategory.Internal)]
public partial class StorageSettingsPage : SettingsPageBase
{
    public StorageSettingsViewModel ViewModel { get; } = IAppHost.GetService<StorageSettingsViewModel>();

    public ILogger<StorageSettingsPage> Logger => ViewModel.Logger;

    public StorageSettingsPage()
    {
        ViewModel.SettingsService.Settings.BackupFilesSize = Helpers.StorageSizeHelper.FormatSize(Helpers.StorageSizeHelper.GetFolderStorageSize(Path.Combine(CommonDirectories.AppRootFolderPath, "Backups/")));
        DataContext = this;
        InitializeComponent();
        IosImportedFilesSettings.IsVisible = PlatformHelper.IsAppleMobile;
    }

    private async void ButtonCreateBackup_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.IsBackupFinished = false;
        ViewModel.IsBackingUp = true;
        try
        {
            await FileFolderService.CreateBackupAsync();
            ViewModel.IsBackupFinished = true;
            this.ShowSuccessToast("备份成功。");
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "无法创建备份。");
            this.ShowErrorToast("无法创建备份", exception);
        }
        ViewModel.IsBackingUp = false;
    }

    private async void ButtonViewBackupFiles_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await PlatformServices.LauncherService.LaunchPath(
                Path.GetFullPath(Path.Combine(CommonDirectories.AppRootFolderPath, "Backups")));
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "无法浏览备份文件。");
            this.ShowErrorToast($"无法浏览备份文件", exception);
        }
    }

    private async void ButtonRecoverBackup_OnClick(object sender, RoutedEventArgs e)
    {
        if (!await ViewModel.ManagementService.AuthorizeByLevel(ViewModel.ManagementService.CredentialConfig.ExitApplicationAuthorizeLevel))
        {
            return;
        }
        AppBase.Current.Restart(["-m", "-r"]);
    }

    private async void ButtonViewImportedFiles_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CommonDirectories.AppImportedFilesFolderPath);
            await PlatformServices.LauncherService.LaunchPath(
                CommonDirectories.AppImportedFilesFolderPath);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "无法浏览 iOS 导入文件副本。");
            this.ShowErrorToast("无法浏览导入文件副本", exception);
        }
    }

    private async void ButtonCleanupImportedFiles_OnClick(object sender, RoutedEventArgs e)
    {
        var confirmed = await ContentDialogHelper.ShowConfirmationDialog(
            "清理未使用的导入文件",
            "ClassIsland 会扫描当前设置、档案和配置，只删除没有持久引用的导入项目。正在进行的一次性导入文件保存在临时目录，不受影响。",
            root: TopLevel.GetTopLevel(this));
        if (!confirmed)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(CommonDirectories.AppImportedFilesFolderPath);
            var deleted = await Task.Run(DeleteUnreferencedImportedItems);
            this.ShowSuccessToast(deleted == 0
                ? "没有发现未使用的导入文件。"
                : $"已清理 {deleted} 个未使用的导入文件项目。");
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "无法清理 iOS 导入文件副本。");
            this.ShowErrorToast("无法清理导入文件副本", exception);
        }
    }

    private static int DeleteUnreferencedImportedItems()
    {
        var importedRoot = Path.GetFullPath(CommonDirectories.AppImportedFilesFolderPath);
        var candidates = Directory.EnumerateDirectories(importedRoot)
            .Select(path => new
            {
                Path = path,
                Marker = ImportedFileReference.Prefix +
                         Uri.EscapeDataString(Path.GetFileName(path)) + "/"
            })
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        var referencedMarkers = new HashSet<string>(StringComparer.Ordinal);
        var searchableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".yaml", ".yml", ".toml", ".xml", ".txt"
        };
        foreach (var file in Directory.EnumerateFiles(
                     CommonDirectories.AppRootFolderPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(file);
            if (fullPath.StartsWith(importedRoot + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) ||
                !searchableExtensions.Contains(Path.GetExtension(fullPath)))
            {
                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length > 16 * 1024 * 1024)
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(fullPath);
            }
            catch
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (!referencedMarkers.Contains(candidate.Marker) &&
                    text.Contains(candidate.Marker, StringComparison.Ordinal))
                {
                    referencedMarkers.Add(candidate.Marker);
                }
            }
        }

        var deleted = 0;
        foreach (var candidate in candidates.Where(x => !referencedMarkers.Contains(x.Marker)))
        {
            Directory.Delete(candidate.Path, true);
            deleted++;
        }

        return deleted;
    }
}
