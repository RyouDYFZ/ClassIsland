using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Helpers;
using ClassIsland.Core.Helpers.UI;
using ClassIsland.Platforms.Abstraction;
using ClassIsland.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Extensions;
using ClassIsland.Views;
using CsesSharp;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Helpers.ProfileTransferHelpers;

public class CsesExportHelper
{
    public static async void CsesExportHandler(TopLevel root)
    {
        var profileService = IAppHost.GetService<IProfileService>();
        var warnings = new List<string>();
        foreach (var i in profileService.Profile.ClassPlans)
        {
            if (i.Value.TimeRule.WeekCountDivTotal > 2 || i.Value.TimeRule.WeekCountDiv > 2)
            {
                warnings.Add($"课程表 {i.Value.Name}：无法导出包含 2 周以上轮换的课表。");
            }
            if (i.Value.TimeLayout == null)
            {
                warnings.Add($"课程表 {i.Value.Name}：无法导出使用无效时间表的课表。");
            }
            if (i.Value.IsEnabled == false)
            {
                warnings.Add($"课程表 {i.Value.Name}：无法导出不默认启用的课表。");
            }
        }

        if (warnings.Count > 0)
        {
            var r = await ContentDialogHelper.ShowConfirmationDialog("兼容性警告",
                "以下课表无法导出到 CSES 格式："+Environment.NewLine + string.Join(Environment.NewLine, warnings) + Environment.NewLine+Environment.NewLine+"是否继续导出？", positiveText: "继续");
            if (!r)
            {
                return;
            }
        }

        try
        {
            var csesProfile = profileService.Profile.ToCsesObject();
            var filePath = await PlatformServices.FilePickerService.SaveFileAsync(
                new FilePickerSaveOptions
                {
                    DefaultExtension = ".yml",
                    FileTypeChoices =
                    [
                        new FilePickerFileType("CSES 课表文件")
                        {
                            Patterns = ["*.yml", "*.yaml"]
                        }
                    ]
                },
                root,
                async output =>
                {
                    await using var writer = new StreamWriter(output, leaveOpen: true);
                    await writer.WriteAsync(CsesLoader.SaveToYamlString(csesProfile));
                    await writer.FlushAsync();
                });
            if (filePath == null)
            {
                return;
            }

            root.ShowSuccessToast($"成功导出到 {filePath}。");
            if (!PlatformHelper.IsAppleMobile)
            {
                var launchPath = PlatformServices.FilePickerService.IsBookmark(filePath)
                    ? filePath
                    : Path.IsPathFullyQualified(filePath)
                        ? Path.GetDirectoryName(filePath)
                        : null;
                if (launchPath is { Length: > 0 })
                {
                    await PlatformServices.LauncherService.LaunchPath(launchPath);
                }
            }
        }
        catch (Exception exception)
        {
            IAppHost.GetService<ILogger<CsesExportHelper>>().LogError(exception, "无法导出到 CSES 课表");
            root.ShowErrorToast($"无法导出到 CSES 课表", exception);
        }
    }
}
