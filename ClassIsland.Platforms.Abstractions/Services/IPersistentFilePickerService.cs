using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ClassIsland.Platforms.Abstraction.Services;

/// <summary>
/// 可选的平台文件选择能力，用于返回可跨应用重启和容器迁移保存的文件引用。
/// </summary>
public interface IPersistentFilePickerService
{
    /// <summary>
    /// 选择文件并返回适合长期保存的路径或平台引用。
    /// </summary>
    Task<List<string>> OpenPersistentFilesPickerAsync(
        FilePickerOpenOptions options,
        TopLevel root);
}

/// <summary>
/// 持久文件选择能力的兼容扩展。
/// </summary>
public static class PersistentFilePickerServiceExtensions
{
    /// <summary>
    /// 在平台支持时获取持久引用；旧平台实现自动回退到普通文件选择。
    /// </summary>
    public static Task<List<string>> OpenPersistentFilesPickerAsync(
        this IPlatformFilePickerService service,
        FilePickerOpenOptions options,
        TopLevel root)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service is IPersistentFilePickerService persistentService
            ? persistentService.OpenPersistentFilesPickerAsync(options, root)
            : service.OpenFilesPickerAsync(options, root);
    }
}
